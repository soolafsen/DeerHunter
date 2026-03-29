using DeerHunter.Configuration;
using DeerHunter.Services;
using Microsoft.Extensions.Options;
return await DeerHunterHost.RunAsync(args);

public static class DeerHunterHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(static arg => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            using var host = BuildHost(args);
            await host.RunAsync();
            return 0;
        }
        catch (Exception exception) when (TryFormatConfigurationError(exception, out var message))
        {
            Console.Error.WriteLine(message);
            return 1;
        }
    }

    internal static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(static options => options.ServiceName = "DeerHunter");

        var configPath = ResolveConfigPath(args, builder.Configuration["DeerHunter:ConfigPath"]);
        var resolvedConfigPath = Path.GetFullPath(configPath, builder.Environment.ContentRootPath);

        builder.Configuration.AddJsonFile(resolvedConfigPath, optional: false, reloadOnChange: false);

        builder.Services
            .AddOptions<DeerHunterOptions>()
            .Bind(builder.Configuration.GetSection("DeerHunter"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<IValidateOptions<DeerHunterOptions>, DeerHunterOptionsValidator>();
        builder.Services.AddSingleton<EventStore>();
        builder.Services.AddSingleton<EventJournal>();
        builder.Services.AddSingleton<SupervisorCoordinator>();
        builder.Services.AddSingleton<LocalApiService>();
        builder.Services.AddHostedService(static services => services.GetRequiredService<EventJournal>());
        builder.Services.AddHostedService(static services => services.GetRequiredService<SupervisorCoordinator>());
        builder.Services.AddHostedService(static services => services.GetRequiredService<LocalApiService>());

        return builder.Build();
    }

    public static string ResolveConfigPath(string[] args, string? configuredPath)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--config", StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return string.IsNullOrWhiteSpace(configuredPath) ? "deerhunter.json" : configuredPath;
    }

    public static bool TryFormatConfigurationError(Exception exception, out string message)
    {
        switch (exception)
        {
            case OptionsValidationException validationException:
                message = $"Configuration validation failed: {string.Join("; ", validationException.Failures)}";
                return true;
            case FileNotFoundException:
            case DirectoryNotFoundException:
            case InvalidDataException:
            case FormatException:
                message = $"Configuration validation failed: {exception.Message}";
                return true;
            default:
                message = string.Empty;
                return false;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            DeerHunter

            Usage:
              dotnet run --project src/DeerHunter -- [--config <path>] [--help]

            Configuration:
              The app loads DeerHunter settings from deerhunter.json by default.
              Use --config to point at a different JSON file.
            """);
    }
}

public partial class Program;
