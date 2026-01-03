using GameListener.Transcription.Options;
using GameListener.Transcription.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Services.Configure<TranscriptionOptions>(builder.Configuration.GetSection("Transcription"));

builder.Services.AddSingleton<IAudioTranscriptionClient, WhisperNetTranscriptionClient>();
builder.Services.AddSingleton<SessionTranscriptionService>();

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GameListener.Transcription");

try
{
    var service = host.Services.GetRequiredService<SessionTranscriptionService>();
    await service.ProcessAsync(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
    logger.LogInformation("Transcription completed successfully.");
}
catch (Exception ex)
{
    logger.LogError(ex, "Transcription failed.");
    throw;
}
