using GameListener.App.Options;
using GameListener.App.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.Commands;
using NetCord.Services;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        config.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true);
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        services.Configure<DiscordOptions>(context.Configuration.GetSection("Discord"));
        services.AddSingleton<RecordingManager>();
        services.AddHostedService<BotHostedService>(); 
        services
            .AddDiscordGateway(options =>
            {
                options.Intents = GatewayIntents.All;
            })
            .AddGatewayHandlers(typeof(Program).Assembly)
            .AddCommands();
    })
    .Build();

// Add commands from modules
host.AddModules(typeof(Program).Assembly);

await host.RunAsync();
