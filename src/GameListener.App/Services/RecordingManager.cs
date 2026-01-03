using System.Text.Json;
using GameListener.App.Options;
using NetCord;
using NetCord.Gateway;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameListener.App.Services;

public sealed class RecordingManager
{
    private readonly ILogger<RecordingManager> _logger;
    private readonly IOptions<DiscordOptions> _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private RecordingSession? _session;

    public RecordingManager(ILogger<RecordingManager> logger, IOptions<DiscordOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public bool IsRecording => _session is not null;
    public ulong? ActiveChannelId => _session?.ChannelId;
    public ulong? ActiveGuildId => _session?.GuildId;

    public async Task StartRecordingAsync(GatewayClient client, ulong guildId, ulong channelId, string requestedBy)
    {
        await _gate.WaitAsync();
        try
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("Recording is already active.");
            }

            _logger.LogInformation("Connecting to voice channel {ChannelId}", channelId);

            var voiceClientType = Type.GetType("NetCord.Gateway.Voice.VoiceClient, NetCord.Gateway.Voice")
                ?? throw new InvalidOperationException("Voice client type not found. Ensure NetCord.Gateway.Voice is referenced.");
            dynamic voiceClient = Activator.CreateInstance(voiceClientType, client)
                ?? throw new InvalidOperationException("Failed to create voice client.");

            dynamic connection = await voiceClient.ConnectAsync(guildId, channelId);

            var outputPath = EnsureOutputDirectory();
            var channel = await client.Rest.GetChannelAsync(channelId);
            var session = new RecordingSession(client, connection, channel, guildId, channelId, outputPath, requestedBy, _logger, _options.Value);
            _session = session;

            try
            {
                await session.InitializeAsync();
            }
            catch
            {
                _session = null;
                await session.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopRecordingAsync(string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_session is null)
            {
                return;
            }

            _logger.LogInformation("Stopping recording: {Reason}", reason);
            await _session.DisposeAsync();
            _session = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasActiveMembersAsync(GatewayClient client)
    {
        if (_session is null)
        {
            return false;
        }

        var guild = await client.Rest.GetGuildAsync(_session.GuildId);
        var states = guild?.VoiceStates?.Where(vs => vs.ChannelId == _session.ChannelId && vs.UserId != client.User.Id);
        return states?.Any() == true;
    }

    private string EnsureOutputDirectory()
    {
        var directory = Path.IsPathRooted(_options.Value.OutputDirectory)
            ? _options.Value.OutputDirectory
            : Path.Combine(Directory.GetCurrentDirectory(), _options.Value.OutputDirectory);

        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class RecordingSession : IAsyncDisposable
    {
        private readonly GatewayClient _client;
        private readonly dynamic _connection;
        private readonly ILogger _logger;
        private readonly DiscordOptions _options;
        private readonly SessionWriter _writer;

        public ulong GuildId { get; }
        public ulong ChannelId { get; }
        public string ChannelName { get; }

        public RecordingSession(GatewayClient client, dynamic connection, Channel channel, ulong guildId, ulong channelId, string directory, string requestedBy, ILogger logger, DiscordOptions options)
        {
            _client = client;
            _connection = connection;
            GuildId = guildId;
            ChannelId = channelId;
            ChannelName = channel.Name ?? channelId.ToString();
            _logger = logger;
            _options = options;
            var filePath = BuildSessionFilePath(directory, ChannelName, requestedBy);
            _writer = new SessionWriter(filePath, _logger);
        }

        public async Task InitializeAsync()
        {
            _connection.Receive += new Func<dynamic, Task>(HandleVoiceReceivedAsync);
            _logger.LogInformation("Voice session started in {Channel}", ChannelName);
            await _writer.WriteHeaderAsync(_client, GuildId, ChannelId, ChannelName);
        }

        private async Task HandleVoiceReceivedAsync(dynamic packet)
        {
            try
            {
                await _writer.WriteEntryAsync(packet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write voice packet");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _connection.Receive -= new Func<dynamic, Task>(HandleVoiceReceivedAsync);
            await _connection.DisconnectAsync();
            await _writer.DisposeAsync();
        }

        private string BuildSessionFilePath(string directory, string channelName, string requestedBy)
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var safeName = string.Join("_", channelName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var fileName = $"session_{timestamp}_{safeName}_by_{SanitizeName(requestedBy)}.jsonl";
            return Path.Combine(directory, fileName);
        }

        private static string SanitizeName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
        }
    }

    private sealed class SessionWriter : IAsyncDisposable
    {
        private readonly string _filePath;
        private readonly StreamWriter _writer;
        private readonly ILogger _logger;
        private readonly JsonSerializerOptions _serializerOptions = new()
        {
            WriteIndented = false
        };

        public SessionWriter(string filePath, ILogger logger)
        {
            _filePath = filePath;
            _logger = logger;
            _writer = new StreamWriter(File.Open(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };
        }

        public async Task WriteHeaderAsync(GatewayClient client, ulong guildId, ulong channelId, string channelName)
        {
            var guild = await client.Rest.GetGuildAsync(guildId);
            var header = new
            {
                type = "session-start",
                startedAt = DateTimeOffset.UtcNow,
                guildId,
                guildName = guild?.Name ?? "unknown",
                channelId,
                channelName
            };

            await WriteJsonAsync(header);
        }

        public async Task WriteEntryAsync(dynamic packet)
        {
            var userId = (ulong?)packet.UserId;
            var userName = packet.User?.Username as string;
            var audioData = packet.Pcm ?? packet.Audio ?? packet.Opus;
            ReadOnlyMemory<byte> audioMemory = audioData is ReadOnlyMemory<byte> rom
                ? rom
                : audioData is byte[] bytes
                    ? new ReadOnlyMemory<byte>(bytes)
                    : ReadOnlyMemory<byte>.Empty;

            var payload = new
            {
                type = "audio",
                receivedAt = DateTimeOffset.UtcNow,
                userId,
                userName,
                audio = Convert.ToBase64String(audioMemory.ToArray())
            };

            await WriteJsonAsync(payload);
        }

        private async Task WriteJsonAsync(object payload)
        {
            var line = JsonSerializer.Serialize(payload, _serializerOptions);
            await _writer.WriteLineAsync(line);
            await _writer.FlushAsync();
            _logger.LogDebug("Wrote packet to {File}", _filePath);
        }

        public async ValueTask DisposeAsync()
        {
            await WriteJsonAsync(new { type = "session-end", endedAt = DateTimeOffset.UtcNow });
            await _writer.DisposeAsync();
            _logger.LogInformation("Session written to {FilePath}", _filePath);
        }
    }
}
