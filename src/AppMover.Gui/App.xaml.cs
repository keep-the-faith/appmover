using System.IO;
using System.Windows;
using AppMover.Gui.Services;
using AppMover.Gui.ViewModels;
using AppMover.Gui.Views;
using AppMover.Host;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AppMover.Gui;

public partial class App
{
    private ILoggerFactory? _loggerFactory;
    private MainViewModel? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppMover", "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(logDirectory, "appmover-gui-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        _loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(dispose: true));
        var logger = _loggerFactory.CreateLogger("AppMover.Gui");
        logger.LogInformation("AppMover GUI started: {Args}", string.Join(' ', e.Args));

        DispatcherUnhandledException += (_, args) =>
        {
            logger.LogError(args.Exception, "Unexpected error");
            MessageBox.Show(args.Exception.Message, "AppMover", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var host = new AppMoverHost(_loggerFactory);
        var dialogs = new WpfDialogService();

        // Called by "Apps & features": appmover-gui.exe uninstall <id> [--yes]
        if (e.Args.Length >= 2 && e.Args[0].Equals("uninstall", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(UninstallMode.Run(host, dialogs, e.Args[1], e.Args.Contains("--yes", StringComparer.OrdinalIgnoreCase)));
            return;
        }

        _main = new MainViewModel(host, dialogs, logger);
        var window = new MainWindow { DataContext = _main };
        MainWindow = window;
        window.Show();
        _main.Export.RefreshCommand.Execute(null);
        _main.Deployments.RefreshCommand.Execute(null);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Dispose();
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Uninstallation started from "Apps &amp; features", without the main window.</summary>
internal static class UninstallMode
{
    public static int Run(AppMoverHost host, IDialogService dialogs, string id, bool quiet)
    {
        var matches = host.FindDeployments(id);
        if (matches.Count != 1)
        {
            if (!quiet)
                dialogs.Error("AppMover", $"Deployment not found: {id}");
            return 3;
        }

        var record = matches[0];
        if (!quiet && !dialogs.Confirm("AppMover", $"Uninstall {record.AppName}?"))
            return 0;

        var failures = host.Uninstall(record);
        if (quiet)
            return failures.Count == 0 ? 0 : 1;
        if (failures.Count == 0)
        {
            dialogs.Info("AppMover", $"{record.AppName} has been uninstalled.");
            return 0;
        }
        dialogs.Error("AppMover", $"{failures.Count} change(s) could not be reverted:{Environment.NewLine}" +
                                  string.Join(Environment.NewLine, failures.Take(20)));
        return 1;
    }
}
