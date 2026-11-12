using AppMover.Gui.Services;
using AppMover.Host;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace AppMover.Gui.ViewModels;

/// <summary>A tab: runs its long operations in the background with a busy indicator.</summary>
public abstract partial class PageViewModel(AppMoverHost host, IDialogService dialogs, ILogger logger) : ObservableObject
{
    protected AppMoverHost Host => host;
    protected IDialogService Dialogs => dialogs;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyMessage;

    /// <summary>Runs <paramref name="work"/> on a background thread, then <paramref name="done"/> on the UI thread.</summary>
    protected async Task RunAsync<T>(string message, Func<T> work, Action<T> done)
    {
        IsBusy = true;
        BusyMessage = message;
        try
        {
            var result = await Task.Run(work);
            done(result);
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Operation} failed", message);
            dialogs.Error("AppMover", e.Message);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }
}
