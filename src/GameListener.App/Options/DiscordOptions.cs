namespace GameListener.App.Options;

public sealed class DiscordOptions
{
    public string Token { get; set; } = string.Empty;
    public string CommandPrefix { get; set; } = "!";
    public string OutputDirectory { get; set; } = "sessions";
    public TimeSpan GracePeriodAfterEmpty { get; set; } = TimeSpan.FromSeconds(15);
}
