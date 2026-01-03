using GameListener.App.Options;
using NetCord.Gateway;
using Microsoft.Extensions.Options;

namespace GameListener.App.Services;

public sealed class BotClientFactory
{
    private readonly IOptions<DiscordOptions> _options;

    public BotClientFactory(IOptions<DiscordOptions> options)
    {
        _options = options;
    }

    public GatewayClient Create()
    {
        var settings = _options.Value;
        if (string.IsNullOrWhiteSpace(settings.Token))
        {
            throw new InvalidOperationException("Discord bot token is missing. Provide Discord:Token in configuration.");
        }

        var client = new GatewayClient(settings.Token, new GatewayClientConfiguration
        {
            Intents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent | GatewayIntents.GuildVoiceStates
        });

        return client;
    }
}
