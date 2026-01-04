using Concentus.Structs;
using CSCore;
using CSCore.Codecs.MP3;
using CSCore.Codecs.OPUS;
using CSCore.Codecs.RAW;
using CSCore.Codecs.WAV;
using CSCore.DSP;
using CSCore.MediaFoundation;
using CSCore.Opus;
using CSCore.Streams;
using CSCore.Streams.SampleConverter;
using CSCore.Win32;
using GameListener.Transcription.Models;
using GameListener.Transcription.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualBasic;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace GameListener.Transcription.Services;

public sealed class WhisperNetTranscriptionClient : IAudioTranscriptionClient, IAsyncDisposable
{
    private readonly ILogger<WhisperNetTranscriptionClient> _logger;
    private readonly TranscriptionOptions _options;
    private WhisperProcessor? _processor;
    private WhisperFactory? _factory;

    public WhisperNetTranscriptionClient(
        ILogger<WhisperNetTranscriptionClient> logger,
        IOptions<TranscriptionOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<AudioTextResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken cancellationToken)
    {
        await EnsureProcessorAsync(cancellationToken);
        WriteMp3FromOpus(request.AudioBytes, "translations", Path.GetFileNameWithoutExtension(request.FileName));
        //CreateMp3File(request.AudioBytes, "translations", Path.GetFileNameWithoutExtension(request.FileName), 16000, 1);
        //var output = CreateWaveStream(request.AudioBytes);
        //using var stream = new MemoryStream(request.AudioBytes);
        var resultText = new StringBuilder();
        var language = string.Empty;
        var mono = ResampleTo16kMono(request.AudioBytes, 1, 48000);
        var floats = Pcm16LeBytesToFloat(mono);
        await foreach (SegmentData item in _processor!.ProcessAsync(floats, cancellationToken: cancellationToken))
        {
            resultText.Append(item.Text);
            language = item.Language;
            // process text
        }

        return new AudioTextResult
        {
            Text = resultText.ToString() ?? string.Empty,
            Language = language
        };
    }
    public static float[] Pcm16LeBytesToFloat(byte[] pcm16le)
    {
        if (pcm16le is null) throw new ArgumentNullException(nameof(pcm16le));
        if ((pcm16le.Length & 1) != 0)
            throw new ArgumentException("PCM16 byte length must be even.", nameof(pcm16le));

        int samples = pcm16le.Length / 2;
        var floats = new float[samples];

        // fast + safe in .NET: use BitConverter on spans
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(pcm16le[2 * i] | (pcm16le[2 * i + 1] << 8)); // little-endian
            floats[i] = s / 32768f;
        }

        return floats;
    }
    public static void CreateMp3File(
        byte[] pcm,
        string outputFolder,
        string fileNameWithoutExt,
        int sampleRate,
        int channels,
        int bitsPerSample = 16,
        int mp3BitRate = 128_000,
        int forceMp3InputSampleRate = 44_100)
    {
        if (pcm is null || pcm.Length == 0)
            throw new ArgumentException("PCM buffer is empty.", nameof(pcm));
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (bitsPerSample != 16 && bitsPerSample != 24 && bitsPerSample != 32)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Expected 16/24/32-bit PCM.");

        Directory.CreateDirectory(outputFolder);

        var outPath = Path.Combine(outputFolder, fileNameWithoutExt + ".mp3");

        var inFormat = new WaveFormat(sampleRate, bitsPerSample, channels, AudioEncoding.Pcm);

        using var inputStream = new MemoryStream(pcm, writable: false);
        using IWaveSource raw = new RawDataReader(inputStream, inFormat);

        // Work in float samples, then force back to 16-bit PCM (MF encoders usually like PCM16)
        ISampleSource sample = raw.ToSampleSource();

        // If you want to force mono, uncomment:
        // if (sample.WaveFormat.Channels != 1) sample = sample.ToMono();

        using IWaveSource pcm16 = sample.ToWaveSource(16);

        // Resample to a MP3-friendly sample rate if needed (44100/48000 recommended)
        IWaveSource mfInput = pcm16;
        if (mfInput.WaveFormat.SampleRate != forceMp3InputSampleRate)
        {
            mfInput = new DmoResampler(
                mfInput,
                new WaveFormat(forceMp3InputSampleRate, 16, mfInput.WaveFormat.Channels, AudioEncoding.Pcm));
        }

        try
        {
            // Create MP3 encoder and feed it raw PCM bytes (must Dispose to finalize MP3) :contentReference[oaicite:3]{index=3}
            using var encoder = MediaFoundationEncoder.CreateMP3Encoder(mfInput.WaveFormat, outPath, mp3BitRate);

            int blockAlign = mfInput.WaveFormat.BlockAlign;
            int bufferSize = mfInput.WaveFormat.BytesPerSecond; // ~1 second
            bufferSize = (bufferSize / blockAlign) * blockAlign; // align to frames
            if (bufferSize <= 0) bufferSize = blockAlign * 1024;

            var buffer = new byte[bufferSize];
            int read;
            while ((read = mfInput.Read(buffer, 0, buffer.Length)) > 0)
            {
                // Ensure we only write full frames
                read = (read / blockAlign) * blockAlign;
                if (read == 0) break;

                encoder.Write(buffer, 0, read);
            }
        }
        finally
        {
            if (!ReferenceEquals(mfInput, pcm16))
                mfInput.Dispose();
        }
    }

    public static string WriteMp3FromOpus(
        byte[] pcmBytes,
        string outputFolder,
        string fileNameWithoutExt,
        int channels = 1,
        int mp3Bitrate = 128_000)
    {
        if (pcmBytes == null || pcmBytes.Length == 0)
            throw new ArgumentException("PCM buffer is empty");

        Directory.CreateDirectory(outputFolder);
        var outputPath = Path.Combine(outputFolder, fileNameWithoutExt + ".mp3");

        var pcmFormat = new WaveFormat(48_000, 16, channels, AudioEncoding.Pcm);

        using var pcmStream = new MemoryStream(pcmBytes, writable: false);
        using IWaveSource pcmSource = new RawDataReader(pcmStream, pcmFormat);

        using var encoder = MediaFoundationEncoder.CreateMP3Encoder(pcmSource.WaveFormat, outputPath, mp3Bitrate);

        // EncodeWholeSource is an official CSCore API :contentReference[oaicite:5]{index=5}
        MediaFoundationEncoder.EncodeWholeSource(encoder, pcmSource);

        return outputPath;
    }
    private static byte[] CreateWaveStream(byte[] audioBytes)
    {
        if (TryParseWavePcm16(audioBytes, out var parsed))
        {
            var pcm = audioBytes.AsSpan(parsed.DataOffset, parsed.DataLength);
            var normalized = parsed.SampleRate == 16_000 && parsed.Channels == 1
                ? pcm.ToArray()
                : ResampleTo16kMono(pcm, parsed.Channels, parsed.SampleRate);
            return normalized;
        }

        if (TryDecodeOpus(audioBytes, out var opusAudio))
        {
            var normalized = opusAudio.SampleRate == 16_000 && opusAudio.Channels == 1
                ? opusAudio.Data
                : ResampleTo16kMono(opusAudio.Data, opusAudio.Channels, opusAudio.SampleRate);
            return normalized;
        }

        // Fallback: treat as raw PCM from Discord (48 kHz mono) and resample.
        var converted = ResampleTo16kMono(audioBytes, channels: 1, sampleRate: 48_000);
        return converted;
    }

    private static MemoryStream BuildWaveStream(byte[] pcmData)
    {
        const short channels = 1;
        const int sampleRate = 16_000;
        const short bitsPerSample = 16;
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;

        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcmData.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // PCM header size
        writer.Write((short)1); // AudioFormat = PCM
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmData.Length);
        writer.Write(pcmData);

        stream.Position = 0;
        return stream;
    }

    private static bool HasWaveHeader(ReadOnlySpan<byte> span) =>
        span.Length >= 12 &&
        span[0] == 'R' && span[1] == 'I' && span[2] == 'F' && span[3] == 'F' &&
        span[8] == 'W' && span[9] == 'A' && span[10] == 'V' && span[11] == 'E';

    private static bool TryParseWavePcm16(ReadOnlySpan<byte> data, out WaveInfo info)
    {
        info = default;
        if (!HasWaveHeader(data))
            return false;

        var offset = 12; // after RIFF/WAVE
        var channels = 0;
        var sampleRate = 0;
        var bitsPerSample = 0;
        var dataOffset = 0;
        var dataLength = 0;

        while (offset + 8 <= data.Length)
        {
            var chunkId = Encoding.ASCII.GetString(data.Slice(offset, 4));
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4, 4));
            offset += 8;

            if (offset + chunkSize > data.Length)
                break;

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                var audioFormat = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset + 14, 2));

                if (audioFormat != 1)
                    return false;
            }
            else if (chunkId == "data")
            {
                dataOffset = offset;
                dataLength = chunkSize;
                // Don't break; ensure fmt is also parsed.
            }

            offset += chunkSize;
        }

        if (channels <= 0 || sampleRate <= 0 || bitsPerSample != 16 || dataOffset == 0 || dataLength <= 0)
            return false;

        info = new WaveInfo(channels, sampleRate, bitsPerSample, dataOffset, dataLength);
        return true;
    }

    private static bool TryDecodeOpus(ReadOnlySpan<byte> input, out PcmAudio audio)
    {
        audio = default;
        if (input.IsEmpty)
            return false;

        // Discord voice uses 48kHz; try mono first, then stereo if needed.
        const int sampleRate = 48_000;
        foreach (var channels in new[] { 1, 2 })
        {
            try
            {
                var decoder = new OpusDecoder(sampleRate, channels);
                var maxFrameSize = sampleRate / 1000 * 120 * channels; // 120 ms max frame
                var pcmBuffer = new short[maxFrameSize];
                var sampleCount = decoder.Decode(input.ToArray(), 0, input.Length, pcmBuffer, 0, maxFrameSize, false);
                if (sampleCount <= 0)
                    continue;

                var totalSamples = sampleCount * channels;
                var pcmBytes = new byte[totalSamples * 2];
                MemoryMarshal.AsBytes(pcmBuffer.AsSpan(0, totalSamples)).Slice(0, pcmBytes.Length).CopyTo(pcmBytes);

                audio = new PcmAudio(pcmBytes, sampleRate, channels);
                return true;
            }
            catch
            {
                // Try next channel layout
            }
        }

        return false;
    }

    private static byte[] ResampleTo16kMono(ReadOnlySpan<byte> pcm, int channels, int sampleRate, int bitsPerSample = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        if (bitsPerSample is not (8 or 16 or 24 or 32))
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "PCM is typically 8/16/24/32 bits.");


        // CSCore APIs expect a Stream; easiest is to materialize the span.
        // If you already have a byte[] you can pass it directly.
        var inputBytes = pcm.ToArray();

        var inputFormat = new WaveFormat(sampleRate, bitsPerSample, channels, AudioEncoding.Pcm);
        using var inputStream = new MemoryStream(inputBytes, writable: false);
        using IWaveSource rawSource = new RawDataReader(inputStream, inputFormat);

        // Convert to float samples for processing
        ISampleSource sample = rawSource.ToSampleSource();

        // Downmix to mono if needed
        if (sample.WaveFormat.Channels != 1)
            sample = sample.ToMono();

        // Convert back to 16-bit PCM wave source (still at original sample rate)
        using IWaveSource pcm16 = sample.ToWaveSource(16);

        // Resample to 16 kHz (Windows-only)
        var outFormat = new WaveFormat(16000, 16, 1, AudioEncoding.Pcm);
        using var resampled = new DmoResampler(pcm16, outFormat);

        // Write WAV container to memory
        using var output = new MemoryStream(); 
        resampled.WriteToWaveStream(output);
        //using (var writer = new WaveWriter(output, resampled.WaveFormat))
        //{
        //    // Pull audio from resampled source and write into WAV stream
        //    byte[] buffer = new byte[resampled.WaveFormat.BytesPerSecond / 2]; // ~0.5 sec buffer
        //    int read;
        //    while ((read = resampled.Read(buffer, 0, buffer.Length)) > 0)
        //    {
        //        writer.Write(buffer, 0, read);
        //    }
        //}

        return output.ToArray();
    }

    private readonly record struct WaveInfo(int Channels, int SampleRate, int BitsPerSample, int DataOffset, int DataLength);
    private readonly record struct PcmAudio(byte[] Data, int SampleRate, int Channels);

    private async Task EnsureProcessorAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
            return;

        await LoadModelAsync(cancellationToken);
        _processor = _factory!.CreateBuilder()
            .WithTemperature(0)
            .WithLanguage("ru")
            .WithNoSpeechThreshold(0.25f)
            .WithLogProbThreshold(-1.2f)
            .WithBeamSearchSamplingStrategy()
            .ParentBuilder
            .Build();

        _logger.LogInformation("Loaded Whisper model for transcription.");
    }

    private async Task LoadModelAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ModelPath))
        {
            var resolvedPath = Path.GetFullPath(_options.ModelPath);
            if (!File.Exists(resolvedPath))
            {
                throw new FileNotFoundException($"Whisper model file not found at '{resolvedPath}'.");
            }

            _logger.LogInformation("Using local Whisper model: {Path}", resolvedPath);
            _factory = WhisperFactory.FromPath(resolvedPath);
        }

        var modelType = ParseModelSize(_options.ModelSize);
        var downloadDirectory = string.IsNullOrWhiteSpace(_options.ModelDownloadDirectory)
            ? Directory.GetCurrentDirectory()
            : _options.ModelDownloadDirectory;

        Directory.CreateDirectory(downloadDirectory);

        var modelPath = Path.Combine(downloadDirectory, GetModelFileName(modelType));
        if (!File.Exists(modelPath))
        {
            _logger.LogInformation("Downloading Whisper model {Model} to {Path}...", modelType, modelPath);
            await using var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(modelType, cancellationToken: cancellationToken);
            await using var destination = File.Create(modelPath);
            await source.CopyToAsync(destination, cancellationToken);
            _logger.LogInformation("Downloaded Whisper model to {Path}", modelPath);
        }
        else
        {
            _logger.LogInformation("Using cached Whisper model at {Path}", modelPath);
        }

        _factory = WhisperFactory.FromPath(modelPath);
    }

    private static GgmlType ParseModelSize(string? value)
    {
        var key = (value ?? "base").Trim().ToLowerInvariant().Replace("-", "").Replace(".", "");
        return key switch
        {
            "tiny" => GgmlType.Tiny,
            "tinyen" => GgmlType.TinyEn,
            "base" => GgmlType.Base,
            "baseen" => GgmlType.BaseEn,
            "small" => GgmlType.Small,
            "smallen" => GgmlType.SmallEn,
            "medium" => GgmlType.Medium,
            "mediumen" => GgmlType.MediumEn,
            "largev1" or "largev1en" => GgmlType.LargeV1,
            "largev2" or "largev2en" => GgmlType.LargeV2,
            "largev3" or "largev3en" => GgmlType.LargeV3,
            "largev3turbo" or "largev3turboen" => GgmlType.LargeV3Turbo,
            //"largev4" or "largev4en" => GgmlType.LargeV4,
            _ => GgmlType.BaseEn
        };
    }

    private static string GetModelFileName(GgmlType type) => type switch
    {
        GgmlType.Tiny => "ggml-tiny.bin",
        GgmlType.TinyEn => "ggml-tiny.en.bin",
        GgmlType.Base => "ggml-base.bin",
        GgmlType.BaseEn => "ggml-base.en.bin",
        GgmlType.Small => "ggml-small.bin",
        GgmlType.SmallEn => "ggml-small.en.bin",
        GgmlType.Medium => "ggml-medium.bin",
        GgmlType.MediumEn => "ggml-medium.en.bin",
        GgmlType.LargeV1 => "ggml-large-v1.bin",
        GgmlType.LargeV2 => "ggml-large-v2.bin",
        GgmlType.LargeV3 => "ggml-large-v3.bin",
        GgmlType.LargeV3Turbo => "ggml-large-v3-turbo.bin",
        //GgmlType.LargeV4 => "ggml-large-v4.bin",
        _ => "ggml-base.en.bin"
    };

    public ValueTask DisposeAsync()
    {
        _processor?.Dispose();
        return ValueTask.CompletedTask;
    }
}
