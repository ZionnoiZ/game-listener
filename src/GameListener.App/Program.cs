using GameListener.App.Options;
using GameListener.App.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.Commands;
using NetCord.Services;
using System.Runtime.InteropServices;

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
        //services.AddHostedService<RecordingCleanupService>();
        services
            .AddDiscordGateway((options, provider) =>
            {
                var discordOptions = provider.GetRequiredService<IOptions<DiscordOptions>>().Value;
                options.Token = discordOptions.Token;
                options.Intents = GatewayIntents.All;
            })
            .AddGatewayHandlers(typeof(Program).Assembly)
            .AddCommands((options, provider) => {
                var discordOptions = provider.GetRequiredService<IOptions<DiscordOptions>>().Value;
                options.Prefix = discordOptions.Prefix;
            });
    })
    .Build();

// Add commands from modules
host.AddModules(typeof(Program).Assembly);

var dllPath = Path.Combine(AppContext.BaseDirectory, "opus.dll");
Console.WriteLine($"Trying to load: {dllPath}");
Console.WriteLine(File.Exists(dllPath) ? "opus.dll exists" : "opus.dll MISSING");

try
{
    NativeLibrary.Load(dllPath);
    Console.WriteLine("Loaded opus.dll OK");
}
catch (Exception ex)
{
    Console.WriteLine("Load failed: " + ex);
}

await host.RunAsync();
