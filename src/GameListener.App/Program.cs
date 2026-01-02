using GameListener.App.Options;
using GameListener.App.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        services.Configure<DiscordOptions>(context.Configuration.GetSection("Discord"));
        services.AddSingleton<BotClientFactory>();
        services.AddSingleton<RecordingManager>();
        services.AddHostedService<BotHostedService>();
    })
    .Build();

await host.RunAsync();
