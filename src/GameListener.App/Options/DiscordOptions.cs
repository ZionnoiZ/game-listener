namespace GameListener.App.Options;

public sealed class DiscordOptions
{
    public string Token { get; set; } = string.Empty;
    public ulong? GuildId { get; set; }
    public ulong? CommandChannelId { get; set; }
    public ulong? VoiceChannelId { get; set; }
    public string CommandPrefix { get; set; } = "!";
    public string OutputDirectory { get; set; } = "sessions";
    public TimeSpan GracePeriodAfterEmpty { get; set; } = TimeSpan.FromSeconds(15);
}
