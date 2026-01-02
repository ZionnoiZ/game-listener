using DSharpPlus;
using DSharpPlus.VoiceNext;
using GameListener.App.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameListener.App.Services;

public sealed class BotClientFactory
{
    private readonly IOptions<DiscordOptions> _options;
    private readonly ILoggerFactory _loggerFactory;

    public BotClientFactory(IOptions<DiscordOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public DiscordClient Create()
    {
        var settings = _options.Value;
        if (string.IsNullOrWhiteSpace(settings.Token))
        {
            throw new InvalidOperationException("Discord bot token is missing. Provide Discord:Token in configuration.");
        }

        var discord = new DiscordClient(new DiscordConfiguration
        {
            Token = settings.Token,
            TokenType = TokenType.Bot,
            LoggerFactory = _loggerFactory,
            Intents = DiscordIntents.All
        });

        discord.UseVoiceNext(new VoiceNextConfiguration
        {
            EnableIncoming = true
        });

        return discord;
    }
}
