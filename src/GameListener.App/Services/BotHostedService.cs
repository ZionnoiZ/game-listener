using System;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.CommandsNext;
using GameListener.App.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameListener.App.Services;

public sealed class BotHostedService : IHostedService
{
    private readonly BotClientFactory _clientFactory;
    private readonly ILogger<BotHostedService> _logger;
    private readonly DiscordOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly RecordingManager _recordingManager;
    private DiscordClient? _client;

    public BotHostedService(
        BotClientFactory clientFactory,
        RecordingManager recordingManager,
        ILogger<BotHostedService> logger,
        IOptions<DiscordOptions> options,
        IServiceProvider serviceProvider)
    {
        _clientFactory = clientFactory;
        _recordingManager = recordingManager;
        _logger = logger;
        _options = options.Value;
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client = _clientFactory.Create();

        _client.Ready += OnReady;
        _client.VoiceStateUpdated += OnVoiceStateUpdated;

        var commands = _client.UseCommandsNext(new CommandsNextConfiguration
        {
            StringPrefixes = new[] { _options.CommandPrefix },
            Services = _serviceProvider
        });
        commands.RegisterCommands<RecordingCommands>();

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
