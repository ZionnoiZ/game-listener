using GameListener.App.Options;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameListener.App.Services;

public sealed class BotHostedService : IHostedService
{
    private readonly BotClientFactory _clientFactory;
    private readonly ILogger<BotHostedService> _logger;
    private readonly DiscordOptions _options;
    private readonly RecordingManager _recordingManager;

    private GatewayClient? _client;

    public BotHostedService(
        BotClientFactory clientFactory,
        RecordingManager recordingManager,
        ILogger<BotHostedService> logger,
        IOptions<DiscordOptions> options)
    {
        _clientFactory = clientFactory;
        _recordingManager = recordingManager;
        _logger = logger;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client = _clientFactory.Create();

        _client.Ready += OnReadyAsync;
        _client.VoiceStateUpdate += OnVoiceStateUpdateAsync;
        _client.MessageCreate += OnMessageCreateAsync;

        await _client.StartAsync();
        _logger.LogInformation("Discord client connected.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            await _recordingManager.StopRecordingAsync("shutdown", cancellationToken);
            await _client.StopAsync();
        }

        _logger.LogInformation("Discord client disconnected.");
    }

    private ValueTask OnReadyAsync(ReadyEventArgs args)
    {
        _logger.LogInformation("Bot connected as {Username}", args.User.Username);
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnMessageCreateAsync(Message message)
    {
        if (string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        if (message.Author?.IsBot == true)
        {
            return;
        }

        if (!message.Content.StartsWith(_options.CommandPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var content = message.Content[_options.CommandPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();
        var guildId = (message as IGuildMessage)?.GuildId;

        switch (command)
        {
            case "record":
                if (guildId is null)
                {
                    await ReplyAsync(message, "You must run this command in a guild.");
                    return;
                }

                await HandleRecordAsync(message, guildId.Value);
                break;
            case "stop":
                await _recordingManager.StopRecordingAsync("stop command", CancellationToken.None);
                await AddReactionAsync(message, "✅");
                break;
        }
    }

    private async Task HandleRecordAsync(Message message, ulong guildId)
    {
        var voiceState = await GetVoiceStateAsync(message.AuthorId, guildId);
        if (voiceState is null || voiceState.ChannelId is null)
        {
            await ReplyAsync(message, "Join a voice channel before calling record.");
            return;
        }

        try
        {
            await _recordingManager.StartRecordingAsync(_client!, guildId, voiceState.ChannelId.Value, message.Author?.Username ?? "unknown");
            await AddReactionAsync(message, "🔴");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to start recording");
            await ReplyAsync(message, ex.Message);
        }
    }

    private async Task<VoiceState?> GetVoiceStateAsync(ulong userId, ulong guildId)
    {
        if (_client is null)
        {
            return null;
        }

        var guild = await _client.Rest.GetGuildAsync(guildId);
        return guild?.VoiceStates?.FirstOrDefault(vs => vs.UserId == userId);
    }

    private async ValueTask OnVoiceStateUpdateAsync(VoiceState voiceState)
    {
        if (!_recordingManager.IsRecording)
        {
            return;
        }

        if (voiceState.GuildId != _recordingManager.ActiveGuildId)
        {
            return;
        }

        await Task.Delay(_options.GracePeriodAfterEmpty);

        if (!await _recordingManager.HasActiveMembersAsync(_client!))
        {
            _logger.LogInformation("Channel is empty, stopping recording.");
            await _recordingManager.StopRecordingAsync("channel empty", CancellationToken.None);
        }
    }

    private Task ReplyAsync(Message message, string content)
    {
        if (_client is null)
        {
            return Task.CompletedTask;
        }

        return _client.Rest.CreateMessageAsync(message.ChannelId, new MessageProperties
        {
            Content = content
        });
    }

    private Task AddReactionAsync(Message message, string emoji)
    {
        if (_client is null)
        {
            return Task.CompletedTask;
        }

        return _client.Rest.AddReactionAsync(message.ChannelId, message.Id, emoji);
    }
}
