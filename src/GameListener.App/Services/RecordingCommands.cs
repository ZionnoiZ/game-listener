using System.Threading;
using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.Entities;
using Microsoft.Extensions.Logging;

namespace GameListener.App.Services;

public sealed class RecordingCommands : BaseCommandModule
{
    private readonly RecordingManager _recordingManager;
    private readonly ILogger<RecordingCommands> _logger;

    public RecordingCommands(RecordingManager recordingManager, ILogger<RecordingCommands> logger)
    {
        _recordingManager = recordingManager;
        _logger = logger;
    }

    [Command("record")]
    public async Task RecordAsync(CommandContext ctx)
    {
        var channel = ctx.Member?.VoiceState?.Channel;
        if (channel is null)
        {
            await ctx.RespondAsync("Join a voice channel before calling !record.");
            return;
        }

        try
        {
            await _recordingManager.StartRecordingAsync(ctx.Client, channel, ctx.User.Username);
            await ctx.Message.CreateReactionAsync(DiscordEmoji.FromName(ctx.Client, ":red_circle:"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to start recording");
            await ctx.RespondAsync(ex.Message);
        }
    }

    [Command("stop")]
    public async Task StopAsync(CommandContext ctx)
    {
        await _recordingManager.StopRecordingAsync("stop command", CancellationToken.None).ConfigureAwait(false);
        await ctx.Message.CreateReactionAsync(DiscordEmoji.FromName(ctx.Client, ":white_check_mark:"));
    }
}
