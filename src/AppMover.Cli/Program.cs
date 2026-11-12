using System.Reflection;
using System.Runtime.Versioning;
using AppMover.Cli;
using AppMover.Core.Platform;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

[assembly: SupportedOSPlatform("windows")]

var cli = CliArguments.Parse(args);

if (cli.Has("version"))
{
    Console.WriteLine($"AppMover {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}");
    return 0;
}
if (cli.Command is null or "help" || cli.Has("help"))
{
    Commands.PrintHelp();
    return cli.Command is null && !cli.Has("help") ? ExitCodes.Usage : ExitCodes.Ok;
}
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("AppMover only runs on Windows.");
    return ExitCodes.Error;
}

var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppMover", "logs");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    // Console output goes to stderr so that --json output on stdout stays parseable.
    .WriteTo.Console(
        restrictedToMinimumLevel: cli.Has("verbose") ? LogEventLevel.Debug : LogEventLevel.Warning,
        standardErrorFromLevel: LogEventLevel.Verbose,
        outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(logDirectory, "appmover-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(dispose: true));
var logger = loggerFactory.CreateLogger("AppMover");
logger.LogInformation("AppMover started: {Args}", string.Join(' ', args));

if (!Privileges.IsElevated())
    logger.LogWarning("Not running as administrator: some installations or user hives may be unreadable.");

try
{
    return new Commands(loggerFactory).Run(cli);
}
catch (Exception e)
{
    logger.LogError(e, "Unexpected error");
    return ExitCodes.Error;
}

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int NotFound = 3;
}
