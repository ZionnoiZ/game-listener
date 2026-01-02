# game-listener

A .NET 8 console application that connects a Discord bot to a configurable voice channel, records incoming audio on demand, and stores session data as text files with participant and timing metadata.

## Features
- Join a specific voice channel (configurable via `appsettings.json` or environment variables).
- Start recording with the `!record` command; stop with `!stop` or automatically when the channel becomes empty.
- Capture incoming voice packets (multi-language friendly) and persist them immediately to per-session JSONL files that include who spoke and when.
- Structured design that can be extended with additional enrichment (e.g., speech-to-text) without changing the core bot loop.

## Project layout
- `GameListener.sln` – solution file.
- `src/GameListener.App` – console application entry point and services.
  - `Program.cs` – host setup and dependency injection registration.
  - `Options/DiscordOptions.cs` – configurable Discord and output settings.
  - `Services/BotClientFactory.cs` – Discord client creation with VoiceNext enabled.
  - `Services/BotHostedService.cs` – lifecycle management and command handling.
  - `Services/RecordingManager.cs` – voice connection, session handling, and JSONL persistence.
  - `appsettings.json` – sample configuration values.

## Configuration
Populate `src/GameListener.App/appsettings.json` or environment variables:

```json
{
  "Discord": {
    "Token": "YOUR_DISCORD_BOT_TOKEN",
    "CommandChannelId": "123456789012345678",
    "VoiceChannelId": "123456789012345678",
    "CommandPrefix": "!",
    "OutputDirectory": "sessions",
    "GracePeriodAfterEmpty": "00:00:15"
  }
}
```

Key notes:
- `VoiceChannelId` lets you pin the listening channel; omit it to have the bot join the command issuer's current voice channel.
- `CommandChannelId` scopes `!record`/`!stop` to a specific text channel; omit to allow any.
- `OutputDirectory` is created automatically if missing; each session writes a JSON lines (`.jsonl`) file containing voice metadata and base64-encoded PCM payloads.

Environment variables can override configuration using the `Discord__*` naming pattern, for example `Discord__Token`.

## Running locally
1. Install the .NET 8 SDK.
2. Restore dependencies and build:
   ```bash
   dotnet restore
   dotnet build
   ```
3. Run the bot (from the repository root):
   ```bash
   dotnet run --project src/GameListener.App
   ```
4. In Discord, issue `!record` in the configured text channel to begin capturing audio; use `!stop` to end the session.

## Session output format
Each session writes a `.jsonl` file under the configured output directory with entries like:

```json
{"type":"session-start","startedAt":"2026-01-02T18:24:51.0000000+00:00","guildId":123,"guildName":"Example","channelId":234,"channelName":"Voice"}
{"type":"audio","receivedAt":"2026-01-02T18:25:01.0000000+00:00","userId":345,"userName":"Player","audio":"<base64 pcm>","sequence":42,"duration":"00:00:01.0240000"}
{"type":"session-end","endedAt":"2026-01-02T18:26:00.0000000+00:00"}
```

You can extend the payload to include speech-to-text or other annotations without changing the core recording workflow.
