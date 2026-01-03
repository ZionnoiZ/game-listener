using System.Linq;
using GameListener.App.Services;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.Commands;

namespace GameListener.App.Commands;

public sealed class RecordingModule : CommandModule<CommandContext>
{
    private readonly GatewayClient _client;
    private readonly ILogger<RecordingModule> _logger;
    private readonly RecordingManager _recordingManager;

    public RecordingModule(
        GatewayClient client,
        RecordingManager recordingManager,
        ILogger<RecordingModule> logger)
    {
        _client = client;
        _recordingManager = recordingManager;
        _logger = logger;
    }

    [Command("record")]
    public async Task RecordAsync()
    {
        var message = Context.Message;

        if (message.Author?.IsBot == true)
        {
            return;
        }

        var guildMessage = message as IGuildMessage;
        if (guildMessage is null)
        {
            await ReplyAsync(message, "You must run this command in a guild.");
            return;
        }

        var voiceState = await GetVoiceStateAsync(message.AuthorId, guildMessage.GuildId);
        if (voiceState is null || voiceState.ChannelId is null)
        {
            await ReplyAsync(message, "Join a voice channel before calling record.");
            return;
        }

        try
        {
            await _recordingManager.StartRecordingAsync(guildMessage.GuildId, voiceState.ChannelId.Value, message.Author?.Username ?? "unknown");
            await AddReactionAsync(message, "🔴");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to start recording");
            await ReplyAsync(message, ex.Message);
        }
    }

    [Command("stop")]
    public async Task StopAsync()
    {
        await _recordingManager.StopRecordingAsync("stop command", CancellationToken.None);
        await AddReactionAsync(Context.Message, "✅");
    }

    private async Task<VoiceState?> GetVoiceStateAsync(ulong userId, ulong guildId)
    {
        var guild = await _client.Rest.GetGuildAsync(guildId);
        return guild?.VoiceStates?.FirstOrDefault(vs => vs.UserId == userId);
    }

    private Task ReplyAsync(Message message, string content)
    {
        return _client.Rest.CreateMessageAsync(message.ChannelId, new MessageProperties
        {
            Content = content
        });
    }

    private Task AddReactionAsync(Message message, string emoji)
    {
        return _client.Rest.AddReactionAsync(message.ChannelId, message.Id, emoji);
    }
}
