using System.Collections.ObjectModel;
using AppMover.Collectors;
using AppMover.Deployment;
using AppMover.Gui.Services;
using AppMover.Host;
using AppMover.Packaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AppMover.Gui.ViewModels;

public enum ImportStep { SelectPackage, Configure, Preview, Done }

/// <summary>A user of this machine (or "do not transfer").</summary>
public sealed record TargetUser(string? Sid, string Label);

/// <summary>Where the files and settings of a source user go.</summary>
public sealed partial class UserMappingRow(PackageUser source, IReadOnlyList<TargetUser> targets, TargetUser selected) : ObservableObject
{
    public PackageUser Source => source;
    public string SourceLabel => $"{source.Name}  ({source.Sid})";
    public IReadOnlyList<TargetUser> Targets => targets;

    [ObservableProperty]
    private TargetUser _selected = selected;
}

/// <summary>Import: open a package, choose where it goes, preview, deploy.</summary>
public sealed partial class ImportViewModel(AppMoverHost host, IDialogService dialogs, ILogger logger)
    : PageViewModel(host, dialogs, logger), IDisposable
{
    private PackageReader? _package;
    private CollectionSession? _session;
    private DeploymentPlan? _plan;
    private string? _defaultInstallDir;

    /// <summary>Raised after a successful deployment.</summary>
    public event Action? Deployed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectPackageStep), nameof(IsConfigureStep), nameof(IsPreviewStep), nameof(IsDoneStep))]
    private ImportStep _step;

    public bool IsSelectPackageStep => Step == ImportStep.SelectPackage;
    public bool IsConfigureStep => Step == ImportStep.Configure;
    public bool IsPreviewStep => Step == ImportStep.Preview;
    public bool IsDoneStep => Step == ImportStep.Done;

    // ---- Step 1-2: package and settings

    [ObservableProperty]
    private string _packageTitle = "";

    [ObservableProperty]
    private string _packageDetails = "";

    public ObservableCollection<PackageIssue> PackageIssues { get; } = [];
    public ObservableCollection<UserMappingRow> UserMappings { get; } = [];

    [ObservableProperty]
    private string _installDir = "";

    [ObservableProperty]
    private bool _force;

    [RelayCommand]
    private Task OpenPackageAsync()
    {
        var path = Dialogs.OpenFile("Open a package", "AppMover package (*.appmove)|*.appmove|All files (*.*)|*.*");
        if (path is null)
            return Task.CompletedTask;

        string? password = null;
        if (PackageCrypto.IsEncrypted(path)
            && (password = Dialogs.AskPassword("Encrypted package", $"Password of {System.IO.Path.GetFileName(path)}:")) is null)
            return Task.CompletedTask;

        Close();
        return RunAsync("Checking the package (SHA-256 hashes)…", () =>
        {
            var package = AppMoverHost.OpenPackage(path, password);
            try
            {
                var issues = package.Validate(verifyHashes: true);
                var profiles = Host.GetProfiles();
                var map = Host.DefaultUserMap(package.Manifest.Users, profiles);
                // A first plan with the default settings gives the default install directory.
                using var session = Host.OpenSession(map.Values.Distinct());
                var plan = Host.PlanDeployment(package, session, null, map, force: false);
                return (Package: package, Issues: issues, Profiles: profiles, Map: map, plan.InstallDir);
            }
            catch
            {
                package.Dispose();
                throw;
            }
        }, opened =>
        {
            _package = opened.Package;
            var manifest = opened.Package.Manifest;
            PackageTitle = $"{manifest.App.DisplayName} {manifest.App.DisplayVersion}";
            PackageDetails =
                $"Installer: {Labels.Installer(manifest.App.Installer)}{Environment.NewLine}" +
                $"Source: {manifest.Source.ComputerName} ({manifest.Source.OsDescription}), {manifest.CreatedUtc.ToLocalTime():g}{Environment.NewLine}" +
                $"Content: {manifest.Files.Count} file(s) ({Labels.Size(manifest.Files.Sum(f => f.Size))}), " +
                $"{manifest.Services.Count} service(s), {manifest.Tasks.Count} task(s), {manifest.Shortcuts.Count} shortcut(s)" +
                (manifest.Prerequisites.Count == 0 ? "" : $"{Environment.NewLine}Prerequisites: {string.Join(", ", manifest.Prerequisites.Select(p => p.Name))}");

            PackageIssues.Clear();
            foreach (var issue in opened.Issues)
                PackageIssues.Add(issue);

            var targets = new List<TargetUser> { new(null, "Do not transfer") };
            targets.AddRange(opened.Profiles.Select(p => new TargetUser(p.Sid, $"{AppMoverHost.ProfileName(opened.Profiles, p.Sid)}  ({p.Sid})")));
            UserMappings.Clear();
            foreach (var user in manifest.Users)
            {
                var sid = opened.Map.GetValueOrDefault(user.Index);
                UserMappings.Add(new UserMappingRow(user, targets, targets.FirstOrDefault(t => t.Sid == sid) ?? targets[0]));
            }

            _defaultInstallDir = opened.InstallDir;
            InstallDir = opened.InstallDir ?? "";
            Force = false;
            Step = ImportStep.Configure;
        });
    }

    [RelayCommand]
    private void BrowseInstallDir()
    {
        if (Dialogs.PickFolder("Install directory", InstallDir) is { } folder)
            InstallDir = folder;
    }

    // ---- Step 3: preview

    public ObservableCollection<string> Operations { get; } = [];
    public ObservableCollection<PackageIssue> PlanIssues { get; } = [];

    [ObservableProperty]
    private string _planSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeployCommand))]
    private bool _canDeploy;

    [RelayCommand]
    private Task PreviewAsync()
    {
        if (_package is null)
            return Task.CompletedTask;
        var package = _package;
        var map = UserMappings.Where(m => m.Selected.Sid is not null).ToDictionary(m => m.Source.Index, m => m.Selected.Sid!);
        // An unchanged directory is left to the planner (relocated default).
        var installDir = string.IsNullOrWhiteSpace(InstallDir) || string.Equals(InstallDir, _defaultInstallDir, StringComparison.OrdinalIgnoreCase)
            ? null
            : InstallDir.Trim();
        var force = Force;
        CloseSession();

        return RunAsync("Preparing the deployment…", () =>
        {
            var session = Host.OpenSession(map.Values.Distinct());
            try
            {
                return (Session: session, Plan: Host.PlanDeployment(package, session, installDir, map, force));
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }, planned =>
        {
            _session = planned.Session;
            _plan = planned.Plan;
            var plan = planned.Plan;
            PlanSummary = $"Install directory: {plan.InstallDir ?? "(none)"}{Environment.NewLine}" +
                          $"{plan.Operations.Count} operation(s), {Labels.Size(plan.BytesToWrite)} to write: " +
                          string.Join(", ", plan.Operations.GroupBy(o => o.GetType().Name.Replace("Operation", "")).Select(g => $"{g.Key} {g.Count()}"));
            PlanIssues.Clear();
            foreach (var issue in plan.Issues)
                PlanIssues.Add(issue);
            Operations.Clear();
            foreach (var operation in plan.Operations)
                Operations.Add(operation.Describe());
            CanDeploy = plan.CanProceed;
            Step = ImportStep.Preview;
        });
    }

    [RelayCommand]
    private void BackToConfigure()
    {
        CloseSession();
        Step = ImportStep.Configure;
    }

    [RelayCommand(CanExecute = nameof(CanDeploy))]
    private Task DeployAsync()
    {
        if (_plan is null || _package is null || _session is null)
            return Task.CompletedTask;
        if (!Dialogs.Confirm("Deploy", $"Install {PackageTitle} on this computer?"))
            return Task.CompletedTask;

        var (plan, package, session) = (_plan, _package, _session);
        var progress = new Progress<string>(message => BusyMessage = message);
        return RunAsync("Deploying…", () =>
        {
            try
            {
                var record = Host.Deploy(plan, package, session, progress);
                return (Record: (DeploymentRecord?)record, Error: (DeploymentException?)null);
            }
            catch (DeploymentException e)
            {
                return (Record: null, Error: e);
            }
        }, deployed =>
        {
            RollbackFailures.Clear();
            if (deployed.Record is { } record)
            {
                Result = $"{record.AppName} is installed ({record.Journal.Count} change(s)).{Environment.NewLine}" +
                         $"Id: {record.Id}{Environment.NewLine}" +
                         "It can be uninstalled from \"Apps & features\" or from the Deployments tab." +
                         string.Concat(record.Warnings.Select(w => $"{Environment.NewLine}{Environment.NewLine}⚠ {w}"));
                Succeeded = true;
                Deployed?.Invoke();
            }
            else
            {
                Result = deployed.Error!.Message;
                foreach (var failure in deployed.Error.RollbackFailures)
                    RollbackFailures.Add(failure);
                Succeeded = false;
            }
            Close();
            Step = ImportStep.Done;
        });
    }

    // ---- Step 4: result

    [ObservableProperty]
    private string _result = "";

    [ObservableProperty]
    private bool _succeeded;

    public ObservableCollection<string> RollbackFailures { get; } = [];

    [RelayCommand]
    private void Restart()
    {
        Close();
        Step = ImportStep.SelectPackage;
    }

    private void CloseSession()
    {
        _session?.Dispose();
        _session = null;
        _plan = null;
        CanDeploy = false;
    }

    private void Close()
    {
        CloseSession();
        _package?.Dispose();
        _package = null;
    }

    public void Dispose() => Close();
}
