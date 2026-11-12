using System.Reflection;
using AppMover.Gui.Services;
using AppMover.Host;
using Microsoft.Extensions.Logging;

namespace AppMover.Gui.ViewModels;

public sealed class MainViewModel : IDisposable
{
    public MainViewModel(AppMoverHost host, IDialogService dialogs, ILogger logger)
    {
        Export = new ExportViewModel(host, dialogs, logger);
        Import = new ImportViewModel(host, dialogs, logger);
        Deployments = new DeploymentsViewModel(host, dialogs, logger);
        Import.Deployed += () => Deployments.RefreshCommand.Execute(null);
    }

    public ExportViewModel Export { get; }
    public ImportViewModel Import { get; }
    public DeploymentsViewModel Deployments { get; }

    public string Title => $"AppMover {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

    /// <summary>Without administrator rights some installations and user profiles cannot be read, and nothing can be deployed.</summary>
    public bool IsElevated => AppMoverHost.IsElevated;

    public void Dispose()
    {
        Export.Dispose();
        Import.Dispose();
    }
}
