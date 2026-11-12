using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows.Data;
using AppMover.Collectors;
using AppMover.Core.Models;
using AppMover.Gui.Services;
using AppMover.Host;
using AppMover.Packaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AppMover.Gui.ViewModels;

public enum ExportStep { SelectApp, Review, Done }

/// <summary>An installed application in the list.</summary>
public sealed class AppRow(InstalledApp app)
{
    public InstalledApp App => app;
    public string Name => app.DisplayName ?? app.KeyName;
    public string? Version => app.DisplayVersion;
    public string? Publisher => app.Publisher;
    public string Scope => Labels.Scope(app);
    public string Installer => Labels.Installer(app.Installer);
    public string Size => app.EstimatedSizeKb is { } kb ? Labels.Size(kb * 1024) : "";
    public bool IsTransferable => app.Installer?.IsTransferable ?? true;

    public string Details
    {
        get
        {
            var text = new StringBuilder();
            void Line(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    text.AppendLine($"{label}: {value}");
            }
            Line("Folder", app.InstallLocation);
            Line("Uninstall", app.UninstallString);
            Line("Installer", Labels.Installer(app.Installer));
            foreach (var evidence in app.Installer?.Evidence ?? [])
                text.AppendLine($"   • {evidence.Description}");
            Line("Hidden", app.HiddenReason);
            if (!IsTransferable)
                text.AppendLine("⚠ This kind of application cannot be transferred.");
            return text.ToString().TrimEnd();
        }
    }
}

/// <summary>A collected item in the review list.</summary>
public sealed partial class ItemRow : ObservableObject
{
    private readonly Action _selectionChanged;

    public ItemRow(CollectedItem item, Action selectionChanged)
    {
        Item = item;
        _isSelected = item.Selected;
        _selectionChanged = selectionChanged;
    }

    public CollectedItem Item { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string ConfidenceLabel => Labels.Confidence(Item.Confidence);
    public int ConfidenceOrder => (int)Item.Confidence;
    public string Role => Labels.Role(Item.Role);
    public string Location => Item.Location;
    public string Reason => Item.Reason;
    public string? Warning => Item.Warning;

    public CollectedItem ToItem() => Item with { Selected = IsSelected };

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}

/// <summary>Export: choose an application, review what was found, write the package.</summary>
public sealed partial class ExportViewModel : PageViewModel, IDisposable
{
    private CollectionSession? _session;
    private CollectionResult? _collection;
    private InstalledApp? _app;

    public ExportViewModel(AppMoverHost host, IDialogService dialogs, ILogger logger) : base(host, dialogs, logger)
    {
        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = o => o is AppRow row && Matches(row);
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ItemRow.ConfidenceLabel)));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ItemRow.ConfidenceOrder), ListSortDirection.Descending));
    }

    // ---- Step 1: applications

    public ObservableCollection<AppRow> Apps { get; } = [];
    public ICollectionView AppsView { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectAppStep), nameof(IsReviewStep), nameof(IsDoneStep))]
    private ExportStep _step;

    public bool IsSelectAppStep => Step == ExportStep.SelectApp;
    public bool IsReviewStep => Step == ExportStep.Review;
    public bool IsDoneStep => Step == ExportStep.Done;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _allUsers;

    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private AppRow? _selectedApp;

    [ObservableProperty]
    private string _appCount = "";

    partial void OnSearchTextChanged(string value) => AppsView.Refresh();

    private bool Matches(AppRow row) =>
        SearchText.Length == 0
        || row.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
        || row.Publisher?.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) == true;

    [RelayCommand]
    private Task RefreshAsync() =>
        RunAsync("Looking for installed applications…", () => Host.LoadApps(AllUsers, ShowHidden), apps =>
        {
            Apps.Clear();
            foreach (var app in apps)
                Apps.Add(new AppRow(app));
            AppCount = $"{apps.Count} application(s)";
        });

    // ---- Step 2: review

    public ObservableCollection<ItemRow> Items { get; } = [];
    public ICollectionView ItemsView { get; }
    public ObservableCollection<string> Prerequisites { get; } = [];

    [ObservableProperty]
    private string _reviewTitle = "";

    [ObservableProperty]
    private string _installRoots = "";

    [ObservableProperty]
    private string _selectionSummary = "";

    private bool CanAnalyze() => SelectedApp is { IsTransferable: true };

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task AnalyzeAsync()
    {
        var app = SelectedApp!.App;
        var allUsers = AllUsers;
        CloseSession();
        return RunAsync($"Analyzing {SelectedApp.Name}…", () =>
        {
            var session = Host.OpenCollectionSession(app, allUsers);
            try
            {
                return (Session: session, Result: Host.Collect(app, session));
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }, collected =>
        {
            _session = collected.Session;
            _app = app;
            ReviewTitle = $"{app.DisplayName} {app.DisplayVersion}";
            TraceSummary = "";
            ShowCollection(collected.Result, null);
            Step = ExportStep.Review;
        });
    }

    /// <summary>Fills the review; the choices already made by the user are kept (by item).</summary>
    private void ShowCollection(CollectionResult result, IReadOnlyDictionary<string, bool>? previousSelection)
    {
        _collection = result;
        InstallRoots = result.InstallRoots.Count == 0
            ? "No install directory found."
            : string.Join(Environment.NewLine, result.InstallRoots.Select(r => $"{r.Path}   ({Labels.Confidence(r.Confidence)}: {r.Reason})"));
        Items.Clear();
        foreach (var item in result.Items)
        {
            var selected = previousSelection is not null && previousSelection.TryGetValue(item.Id, out var chosen) ? chosen : item.Selected;
            Items.Add(new ItemRow(item with { Selected = selected }, UpdateSummary));
        }
        Prerequisites.Clear();
        foreach (var prerequisite in result.Prerequisites)
            Prerequisites.Add($"{prerequisite.Name}   ({prerequisite.Reason})");
        UpdateSummary();
    }

    // ---- Dynamic analysis

    [ObservableProperty]
    private string _traceSummary = "";

    // A recording runs while the window stays usable (the user switches to the application): Stop analyzes it again.
    private Core.Tracing.IUsageTraceSession? _tracing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordUsageCommand), nameof(StopRecordingCommand), nameof(ExportCommand), nameof(BackCommand))]
    private bool _isRecording;

    [ObservableProperty]
    private string _recordingMessage = "";

    private bool IsNotRecording() => !IsRecording;

    /// <summary>Starts recording what the application reads and writes while the user works with it.</summary>
    [RelayCommand(CanExecute = nameof(IsNotRecording))]
    private void RecordUsage()
    {
        if (_app is null || _collection is null || _session is null)
            return;
        if (_collection.InstallRoots.Count == 0)
        {
            Dialogs.Error("Record usage", "No install directory was found: the processes of the application cannot be recognized.");
            return;
        }

        var launch = AppMoverHost.MainExecutable(_app, _collection) is not null
                     && Dialogs.Confirm("Record usage", $"Start {_app.DisplayName} now?{Environment.NewLine}(No: start it yourself after this message.)");
        try
        {
            _tracing = Host.StartUsageTrace(_app, _collection, launch);
        }
        catch (Exception e)
        {
            Dialogs.Error("Record usage", $"The recording could not start: {e.Message}");
            return;
        }
        RecordingMessage = $"Recording what {_app.DisplayName} reads and writes. Use the application now (open documents, " +
                           "change settings), close it, then click Stop recording.";
        IsRecording = true;
    }

    /// <summary>Stops the recording and analyzes the application again with what it used.</summary>
    [RelayCommand(CanExecute = nameof(IsRecording))]
    private Task StopRecordingAsync()
    {
        var tracing = _tracing;
        _tracing = null;
        IsRecording = false;
        if (tracing is null || _app is null || _session is null)
        {
            tracing?.Dispose();
            return Task.CompletedTask;
        }

        var (app, session) = (_app, _session);
        var previous = Items.ToDictionary(i => i.Item.Id, i => i.IsSelected, StringComparer.OrdinalIgnoreCase);
        return RunAsync("Analyzing the recorded usage…", () =>
        {
            Core.Tracing.UsageTrace trace;
            using (tracing)
                trace = tracing.Stop();
            return (Trace: trace, Result: Host.Collect(app, session, trace));
        }, recorded =>
        {
            var before = previous.Count;
            ShowCollection(recorded.Result, previous);
            var trace = recorded.Trace;
            TraceSummary = $"Usage recorded: {trace.Processes.Count} process(es), {trace.FilesWritten.Count} file(s) written, " +
                           $"{trace.RegistryKeysWritten.Count} registry key(s) written; {Math.Max(0, Items.Count - before)} new item(s).";
        });
    }

    private void UpdateSummary() =>
        SelectionSummary = $"{Items.Count(i => i.IsSelected)} of {Items.Count} item(s) selected";

    [RelayCommand]
    private void SelectDefault() => SetSelection(i => i.Item.Confidence >= Confidence.Probable);

    [RelayCommand]
    private void SelectAll() => SetSelection(_ => true);

    [RelayCommand]
    private void SelectNone() => SetSelection(_ => false);

    private void SetSelection(Func<ItemRow, bool> selected)
    {
        foreach (var item in Items)
            item.IsSelected = selected(item);
    }

    [RelayCommand(CanExecute = nameof(IsNotRecording))]
    private void Back()
    {
        CloseSession();
        Step = ExportStep.SelectApp;
    }

    /// <summary>Encrypt the package with <see cref="Password"/>.</summary>
    [ObservableProperty]
    private bool _encrypt;

    /// <summary>Set by the view (a PasswordBox cannot be bound).</summary>
    public string Password { get; set; } = "";

    public string PasswordConfirmation { get; set; } = "";

    [RelayCommand(CanExecute = nameof(IsNotRecording))]
    private Task ExportAsync()
    {
        if (_app is null || _collection is null || _session is null)
            return Task.CompletedTask;
        if (Encrypt && Password.Length < 8)
        {
            Dialogs.Error("Export", "The password must have at least 8 characters.");
            return Task.CompletedTask;
        }
        if (Encrypt && Password != PasswordConfirmation)
        {
            Dialogs.Error("Export", "The passwords do not match.");
            return Task.CompletedTask;
        }
        var path = Dialogs.SaveFile("Save the package", "AppMover package (*.appmove)|*.appmove", AppMoverHost.DefaultPackageName(_app));
        if (path is null)
            return Task.CompletedTask;

        var (app, collection, session) = (_app, _collection, _session);
        var items = Items.Select(i => i.ToItem()).ToList();
        var password = Encrypt ? Password : null;
        return RunAsync("Creating the package…", () => Host.Export(path, app, collection, items, session, password), package =>
        {
            CloseSession();
            ShowResult(package);
        });
    }

    // ---- Step 3: result

    public ObservableCollection<PackageIssue> Issues { get; } = [];

    [ObservableProperty]
    private string _result = "";

    private string? _packagePath;

    private void ShowResult(ExportResult export)
    {
        var package = export.Package;
        _packagePath = package.Path;
        Result = $"Package created: {package.Path}{Environment.NewLine}" +
                 $"{package.FileCount} file(s) ({Labels.Size(package.TotalSize)}), {package.RegistryKeyCount} registry key(s), " +
                 $"archive of {Labels.Size(package.ArchiveSize)}{(Encrypt ? ", encrypted (AES-256)" : "")}." +
                 (export.FilesFromSnapshot > 0 ? $"{Environment.NewLine}{export.FilesFromSnapshot} locked file(s) were read from a Volume Shadow Copy." : "") +
                 (package.HasErrors ? $"{Environment.NewLine}⚠ Some files could not be read (is the application running?)." : "");
        Issues.Clear();
        foreach (var issue in package.Manifest.Issues)
            Issues.Add(issue);
        Step = ExportStep.Done;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_packagePath is not null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_packagePath}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void NewExport()
    {
        Items.Clear();
        Issues.Clear();
        Step = ExportStep.SelectApp;
    }

    private void CloseSession()
    {
        _session?.Dispose();
        _session = null;
        _collection = null;
    }

    public void Dispose()
    {
        // Closing the window while recording: stop the kernel trace session, it would outlive AppMover otherwise.
        _tracing?.Dispose();
        _tracing = null;
        CloseSession();
    }
}
