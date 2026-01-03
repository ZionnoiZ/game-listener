# game-listener

A .NET 8 console application that connects a Discord bot to voice channels, records incoming audio on demand, and stores session data as text files with participant and timing metadata.

## Features
- Join the same voice channel as the user who requested recording.
- Start recording with the `!record` command; stop with `!stop` or automatically when the channel becomes empty.
- Capture incoming voice packets (multi-language friendly) and persist them immediately to per-session JSONL files that include who spoke and when.
- Structured design that can be extended with additional enrichment (e.g., speech-to-text) without changing the core bot loop.

## Project layout
- `GameListener.sln` – solution file.
- `src/GameListener.App` – console application entry point and services.
  - `Program.cs` – host setup and dependency injection registration.
  - `Options/DiscordOptions.cs` – configurable Discord and output settings.
  - `Commands/RecordingModule.cs` – NetCord text commands for recording control.
  - `Services/RecordingCleanupService.cs` – lifecycle cleanup to stop active sessions on shutdown.
  - `Services/RecordingManager.cs` – voice connection, session handling, and JSONL persistence.
  - `appsettings.json` – sample configuration values.

## Configuration
Populate `src/GameListener.App/appsettings.json` or environment variables:

```json
{
  "Discord": {
    "Token": "YOUR_DISCORD_BOT_TOKEN",
    "CommandPrefix": "!",
    "OutputDirectory": "sessions",
    "GracePeriodAfterEmpty": "00:00:15"
  }
}
```

Key notes:
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
