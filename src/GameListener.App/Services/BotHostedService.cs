using DSharpPlus;
using DSharpPlus.Entities;
using GameListener.App.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameListener.App.Services;

public sealed class BotHostedService : IHostedService
{
    private readonly BotClientFactory _clientFactory;
    private readonly RecordingManager _recordingManager;
    private readonly ILogger<BotHostedService> _logger;
    private readonly DiscordOptions _options;
    private DiscordClient? _client;

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

        _client.Ready += OnReady;
        _client.MessageCreated += OnMessageCreated;
        _client.VoiceStateUpdated += OnVoiceStateUpdated;

        await _client.ConnectAsync();
        _logger.LogInformation("Discord client connected.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            await _recordingManager.StopRecordingAsync("shutdown", cancellationToken);
            await _client.DisconnectAsync();
            _client.Dispose();
        }

        _logger.LogInformation("Discord client disconnected.");
    }

    private Task OnReady(DiscordClient sender, DSharpPlus.EventArgs.ReadyEventArgs e)
    {
        _logger.LogInformation("Bot connected as {Username}", sender.CurrentUser.Username);
        return Task.CompletedTask;
    }

    private async Task OnMessageCreated(DiscordClient sender, DSharpPlus.EventArgs.MessageCreateEventArgs e)
    {
        if (e.Author.IsBot)
        {
            return;
        }

        if (_options.CommandChannelId.HasValue && e.Channel.Id != _options.CommandChannelId.Value)
        {
            return;
        }

        if (!e.Message.Content.StartsWith(_options.CommandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var content = e.Message.Content[_options.CommandPrefix.Length..].Trim();
        var lower = content.ToLowerInvariant();

        switch (lower)
        {
            case "record":
                await HandleStartCommandAsync(e).ConfigureAwait(false);
                break;
            case "stop":
                await _recordingManager.StopRecordingAsync("stop command", CancellationToken.None).ConfigureAwait(false);
                await e.Message.CreateReactionAsync(DiscordEmoji.FromName(sender, ":white_check_mark:"));
                break;
        }
    }

    private async Task HandleStartCommandAsync(DSharpPlus.EventArgs.MessageCreateEventArgs e)
    {
        var channel = await ResolveVoiceChannelAsync(e.Author as DiscordMember);
        if (channel is null)
        {
            await e.Message.RespondAsync("I could not find a voice channel to join. Set Discord:VoiceChannelId or join a voice channel before calling !record.");
            return;
        }

        try
        {
            await _recordingManager.StartRecordingAsync(_client, channel, e.Author.Username);
            await e.Message.CreateReactionAsync(DiscordEmoji.FromName(_client, ":red_circle:"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to start recording");
            await e.Message.RespondAsync(ex.Message);
        }
    }

    private async Task<DiscordChannel?> ResolveVoiceChannelAsync(DiscordMember? caller)
    {
        if (_client is null)
        {
            return null;
        }

        if (_options.VoiceChannelId.HasValue)
        {
            return await _client.GetChannelAsync(_options.VoiceChannelId.Value);
        }

        if (caller is null)
        {
            return null;
        }

        return caller.VoiceState?.Channel;
    }

    private async Task OnVoiceStateUpdated(DiscordClient sender, DSharpPlus.EventArgs.VoiceStateUpdateEventArgs e)
    {
        if (!_recordingManager.IsRecording)
        {
            return;
        }

        if (e.Channel?.Id != _recordingManager.ActiveChannelId && e.Before?.Channel?.Id != _recordingManager.ActiveChannelId)
        {
            return;
        }

        // Wait briefly to allow state to settle, then check occupancy.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var channel = _recordingManager.ActiveChannel;
        if (channel is null)
        {
            return;
        }

        var members = channel.Users.Where(u => !u.IsBot).ToList();
        if (members.Count == 0)
        {
            _logger.LogInformation("Channel is empty, stopping recording.");
            using var cts = new CancellationTokenSource(_options.GracePeriodAfterEmpty);
            await _recordingManager.StopRecordingAsync("channel empty", cts.Token);
        }
    }
}
