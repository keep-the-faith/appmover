using System.Collections.ObjectModel;
using AppMover.Deployment;
using AppMover.Gui.Services;
using AppMover.Host;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AppMover.Gui.ViewModels;

public sealed class DeploymentRow(DeploymentRecord record)
{
    public DeploymentRecord Record => record;
    public string AppName => record.AppName;
    public string? Version => record.AppVersion;
    public string Date => record.DeployedUtc.ToLocalTime().ToString("g");
    public string? InstallDir => record.InstallDir;
    public string Id => record.Id;

    public string State => record.State switch
    {
        DeploymentState.Deployed => "Installed",
        DeploymentState.RolledBack => "Rolled back (failed)",
        _ => "Incomplete",
    };
}

/// <summary>Deployments made on this machine, and their uninstallation.</summary>
public sealed partial class DeploymentsViewModel(AppMoverHost host, IDialogService dialogs, ILogger logger) : PageViewModel(host, dialogs, logger)
{
    public ObservableCollection<DeploymentRow> Deployments { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    private DeploymentRow? _selected;

    [RelayCommand]
    private Task RefreshAsync() => RunAsync("Reading the deployments…", () => Host.Store.List(), Show);

    private void Show(IReadOnlyList<DeploymentRecord> records)
    {
        Deployments.Clear();
        foreach (var record in records.Reverse())
            Deployments.Add(new DeploymentRow(record));
    }

    private bool CanUninstall() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private Task UninstallAsync()
    {
        var record = Selected!.Record;
        if (!Dialogs.Confirm("Uninstall", $"Uninstall {record.AppName} ({record.Journal.Count} change(s) to revert)?"))
            return Task.CompletedTask;

        return RunAsync($"Uninstalling {record.AppName}…", () => (Failures: Host.Uninstall(record), Records: Host.Store.List()), result =>
        {
            Show(result.Records);
            if (result.Failures.Count == 0)
                Dialogs.Info("Uninstall", $"{record.AppName} has been uninstalled.");
            else
                Dialogs.Error("Uninstall",
                    $"{result.Failures.Count} change(s) could not be reverted (run the uninstallation again once the cause is fixed):" +
                    Environment.NewLine + string.Join(Environment.NewLine, result.Failures.Take(20)));
        });
    }
}
