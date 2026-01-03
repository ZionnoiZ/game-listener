using GameListener.App.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Logging;
using NetCord.Rest;
using System.Linq;
using System.Text.Json;

namespace GameListener.App.Services;

public sealed class RecordingManager
{
    private readonly GatewayClient _client;
    private readonly ILogger<RecordingManager> _logger;
    private readonly IOptions<DiscordOptions> _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private RecordingSession? _session;

    public RecordingManager(GatewayClient client, ILogger<RecordingManager> logger, IOptions<DiscordOptions> options)
    {
        _client = client;
        _logger = logger;
        _options = options;

        _client.VoiceStateUpdate += OnVoiceStateUpdateAsync;
    }

    public bool IsRecording => _session is not null;
    public ulong? ActiveChannelId => _session?.ChannelId;
    public ulong? ActiveGuildId => _session?.GuildId;

    public async Task StartRecordingAsync(ulong guildId, ulong channelId, string requestedBy)
    {
        await _gate.WaitAsync();
        try
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("Recording is already active.");
            }

            _logger.LogInformation("Connecting to voice channel {ChannelId}", channelId);

            var voiceClient = await _client.JoinVoiceChannelAsync(guildId, channelId,
                new VoiceClientConfiguration
                {
                    Logger = new ConsoleLogger(),
                    ReceiveHandler = new VoiceReceiveHandler()
                });
            await voiceClient.StartAsync();

            var outputPath = EnsureOutputDirectory();
            var channel = await _client.Rest.GetChannelAsync(channelId);
            var session = new RecordingSession(_client, voiceClient, channel, guildId, channelId, outputPath, requestedBy, _logger, _options.Value);
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

    public async Task<bool> HasActiveMembersAsync()
    {
        if (_session is null)
        {
            return false;
        }

        if (!_client.Cache.Guilds.TryGetValue(_session.GuildId, out var guild))
            return false;

        // VoiceStates: userId -> VoiceState
        foreach (var kvp in guild.VoiceStates)
        {
            var userId = kvp.Key;
            var voiceState = kvp.Value;

            if (voiceState.ChannelId != _session.ChannelId)
                continue;

            // guild.Users is also gateway-state (not REST)
            if (guild.Users.TryGetValue(userId, out var user) && !user.IsBot)
                return true;
        }

        return false;
    }

    private async ValueTask OnVoiceStateUpdateAsync(VoiceState voiceState)
    {
        if (_session is null)
        {
            return;
        }

        if (voiceState.GuildId != _session.GuildId)
        {
            return;
        }

        await Task.Delay(_options.Value.GracePeriodAfterEmpty);

        if (!await HasActiveMembersAsync())
        {
            _logger.LogInformation("Channel is empty, stopping recording.");
            await StopRecordingAsync("channel empty", CancellationToken.None);
        }
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
        private readonly VoiceClient _voiceClient;
        private readonly ILogger _logger;
        private readonly DiscordOptions _options;
        private readonly SessionWriter _writer;

        public ulong GuildId { get; }
        public ulong ChannelId { get; }
        public string ChannelName { get; }

        public RecordingSession(GatewayClient client, VoiceClient voiceClient, Channel channel, ulong guildId, ulong channelId, string directory, string requestedBy, ILogger logger, DiscordOptions options)
        {
            _client = client;
            _voiceClient = voiceClient;
            GuildId = guildId;
            ChannelId = channelId;
            ChannelName = channel.ToString() ?? channelId.ToString();
            _logger = logger;
            _options = options;
            var filePath = BuildSessionFilePath(directory, ChannelName, requestedBy);
            _writer = new SessionWriter(filePath, _logger);
        }

        public async Task InitializeAsync()
        {
            _voiceClient.VoiceReceive += HandleVoiceReceivedAsync;
            _logger.LogInformation("Voice session started in {Channel}", ChannelName);
            await _writer.WriteHeaderAsync(_client, GuildId, ChannelId, ChannelName);
        }

        private ValueTask HandleVoiceReceivedAsync(VoiceReceiveEventArgs args)
        {
            try
            {
                // 1) Resolve userId from SSRC (NetCord provides this mapping in voiceClient.Cache.Users) :contentReference[oaicite:2]{index=2}
                ulong? userId = null;
                if (_voiceClient.Cache.Users.TryGetValue(args.Ssrc, out var mappedUserId))
                    userId = mappedUserId;

                // 2) Resolve username from Gateway cache (optional; might be null if not cached)
                string? userName = null;
                if (userId is not null
                    && _client.Cache.Guilds.TryGetValue(_voiceClient.GuildId, out var guild)
                    && guild.Users.TryGetValue(userId.Value, out var user))
                {
                    // Pick what you prefer; Username is always there, GlobalName may be null
                    userName = user.GlobalName ?? user.Username;
                }
                // 3) Copy frame NOW (args.Frame is a span on a ref struct) :contentReference[oaicite:3]{index=3}
                var frameCopy = args.Frame.ToArray();

                var packet = new
                {
                    UserId = userId,
                    UserName = userName,
                    ReceivedAtUtc = DateTimeOffset.UtcNow,
                    Frame = frameCopy
                };

                return _writer.WriteEntryAsync(packet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write voice packet");
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            _voiceClient.VoiceReceive -= HandleVoiceReceivedAsync;
            await _voiceClient.CloseAsync();
            _voiceClient.Dispose();
            await _writer.DisposeAsync();
        }

        private string BuildSessionFilePath(string directory, string channelName, string requestedBy)
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
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
            _writer = new StreamWriter(File.Open(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
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

        public async ValueTask WriteEntryAsync(dynamic packet)
        {
            var userId = (ulong?)packet.UserId;
            var userName = packet.UserName as string;
            //var audioData = packet.Pcm ?? packet.Audio ?? packet.Opus;
            //ReadOnlyMemory<byte> audioMemory = audioData is ReadOnlyMemory<byte> rom
            //    ? rom
            //    : audioData is byte[] bytes
            //        ? new ReadOnlyMemory<byte>(bytes)
            //        : ReadOnlyMemory<byte>.Empty;

            var payload = new
            {
                type = "audio",
                receivedAt = packet.ReceivedAtUtc,
                userId,
                userName,
                audio = Convert.ToBase64String(packet.Frame)
            };

            await WriteJsonAsync(payload);

            return;
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
