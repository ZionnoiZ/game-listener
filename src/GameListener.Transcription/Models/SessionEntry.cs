using System.Text.Json.Serialization;

namespace GameListener.Transcription.Models;

public sealed class SessionEntry
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("receivedAt")]
    public DateTimeOffset? ReceivedAt { get; set; }

    [JsonPropertyName("userId")]
    public ulong? UserId { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("audio")]
    public string? AudioBase64 { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }
}
