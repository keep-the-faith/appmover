using System.Text.Json;
using AppMover.Collectors;
using AppMover.Core.Models;
using AppMover.Core.Tracing;
using AppMover.Core.Registry;
using AppMover.Host;
using Microsoft.Extensions.Logging;

namespace AppMover.Cli;

/// <summary>Reviewed selection, written by <c>collect -o</c> and read by <c>export --items</c>.</summary>
internal sealed record SelectionFile(string AppId, string? AppName, List<InstallRoot> InstallRoots, List<CollectedItem> Items);

internal sealed partial class Commands(ILoggerFactory loggerFactory)
{
    private readonly AppMoverHost _host = new(loggerFactory);

    public static void PrintHelp()
    {
        Console.WriteLine("""
            AppMover - transfer installed Windows applications without their installer

            Usage:
              appmover list [--all-users] [--hidden] [--json]
                  Lists the installed applications and their installer technology.
              appmover inspect <app> [--all-users] [--hidden] [--json]
                  Shows the details of an application.
              appmover trace <app> [--launch] [-o trace.json] [--all-users]
                  Records what the application reads and writes while you use it (dynamic analysis), to find data
                  the static analysis misses. --launch starts the application. Use the trace with collect/export --trace.
              appmover collect <app> [--all-users] [--json] [-o selection.json] [--trace trace.json]
                  Lists the files, registry keys and system entries of an application, with their confidence.
                  -o writes an editable selection file (set "Selected" to true/false, then use export --items).
              appmover export <app> [-o package.appmove] [--items selection.json] [--include-possible] [--all-users] [--force]
                              [--encrypt] [--no-vss] [--trace trace.json]
                  Creates the package. Default selection: Certain and Probable items.
                  Files locked by a running program are read from a Volume Shadow Copy (--no-vss to skip them).
                  --encrypt protects the package with a password (AES-256).
              appmover verify <package>
                  Checks the integrity of a package (SHA-256 of every file).
              appmover deploy <package> [--install-dir <dir>] [--user-map 0=<user|SID>,…] [--dry-run] [--force] [--yes]
                  Installs a package on this machine. --dry-run only shows what would be done.
                  Source users are mapped to the user of the same name, otherwise to the current user.
              appmover deployments
                  Lists the deployments made on this machine.
              appmover uninstall <deployment id|app> [--yes]
                  Reverts a deployment (also used by "Apps & features").

            <app> is an id, an Uninstall key name, or part of the application name.

            Options:
              --all-users         Include every user profile (loads offline hives), not only the current one.
              --hidden            Include entries hidden by "Apps & features" (updates, system components).
              --include-possible  Also package the "Possible" items.
              --force             Overwrite the package / export a non-transferable application /
                                  deploy over an existing installation.
              --yes               Do not ask for confirmation.
              --json              Machine-readable output.
              --verbose           Show debug logs on the console.
              --version           Show the version.

            Passwords of encrypted packages are asked interactively, or read from the APPMOVER_PASSWORD variable.
            Logs: %LOCALAPPDATA%\AppMover\logs
            """);
    }

    public int Run(CliArguments cli)
    {
        if (cli.Error is not null)
            return Usage(cli.Error);
        return cli.Command switch
        {
            "list" => List(cli),
            "inspect" => Inspect(cli),
            "collect" => Collect(cli),
            "trace" => Trace(cli),
            "export" => Export(cli),
            "verify" => Verify(cli),
            "deploy" => Deploy(cli),
            "deployments" => ListDeployments(cli),
            "uninstall" => Uninstall(cli),
            _ => Usage($"Unknown command '{cli.Command}'."),
        };
    }

    private int List(CliArguments cli)
    {
        if (CheckOptions(cli, "all-users", "hidden", "json", "verbose") is { } error)
            return error;

        var apps = LoadApps(cli);
        if (cli.Has("json"))
        {
            Output.WriteJson(apps);
            return ExitCodes.Ok;
        }

        Output.WriteTable(
            [("Name", 50), ("Version", 18), ("Publisher", 28), ("Scope", 12), ("Installer", 22), ("Key", 40)],
            apps.Select(a => new[] { a.DisplayName ?? a.KeyName, a.DisplayVersion, a.Publisher, FormatScope(a), FormatInstaller(a.Installer), a.KeyName }));
        Console.WriteLine();
        Console.WriteLine($"{apps.Count} application(s).");
        return ExitCodes.Ok;
    }

    private int Inspect(CliArguments cli)
    {
        if (CheckOptions(cli, "all-users", "hidden", "json", "verbose") is { } error)
            return error;
        if (FindSingleApp(cli, out var app) is { } notFound)
            return notFound;

        if (cli.Has("json"))
            Output.WriteJson(app);
        else
            PrintDetails(app!);
        return ExitCodes.Ok;
    }

    private int Collect(CliArguments cli)
    {
        if (CheckOptions(cli, "all-users", "hidden", "json", "verbose", "output", "trace") is { } error)
            return error;
        if (FindSingleApp(cli, out var app) is { } notFound)
            return notFound;
        if (LoadTrace(cli, app!, out var trace) is { } invalid)
            return invalid;

        using var session = _host.OpenCollectionSession(app!, cli.Has("all-users"));
        var result = _host.Collect(app!, session, trace);
        var selection = new SelectionFile(app!.Id, app.DisplayName, result.InstallRoots.ToList(), result.Items.ToList());

        if (cli.Get("output") is { } output)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(selection, Output.Json));
            Console.Error.WriteLine($"Selection written to {output}");
        }

        if (cli.Has("json"))
            Output.WriteJson(selection);
        else
            PrintCollection(app, result, result.Items);
        return ExitCodes.Ok;
    }

    private int Export(CliArguments cli)
    {
        if (CheckOptions(cli, "all-users", "hidden", "verbose", "output", "items", "include-possible", "force", "encrypt", "no-vss", "trace") is { } error)
            return error;
        if (FindSingleApp(cli, out var app) is { } notFound)
            return notFound;
        if (LoadTrace(cli, app!, out var trace) is { } invalid)
            return invalid;

        if (app!.Installer is { IsTransferable: false } && !cli.Has("force"))
        {
            Console.Error.WriteLine($"{app.Installer.Type} applications cannot be transferred (use --force to export anyway).");
            return ExitCodes.Error;
        }

        var output = Path.GetFullPath(cli.Get("output") ?? AppMoverHost.DefaultPackageName(app));
        if (File.Exists(output) && !cli.Has("force"))
        {
            Console.Error.WriteLine($"{output} already exists (use --force to overwrite).");
            return ExitCodes.Error;
        }

        using var session = _host.OpenCollectionSession(app, cli.Has("all-users"));
        var result = _host.Collect(app, session, trace);

        IReadOnlyList<CollectedItem> items = result.Items;
        if (cli.Get("items") is { } selectionPath)
        {
            var selection = JsonSerializer.Deserialize<SelectionFile>(File.ReadAllText(selectionPath), Output.Json);
            if (selection is null || !selection.AppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"{selectionPath} is not a selection of {app.Id}.");
                return ExitCodes.Error;
            }
            // The reviewed file is authoritative: items may have been unselected, added or removed.
            items = selection.Items;
        }
        else if (cli.Has("include-possible"))
        {
            items = items.Select(i => i with { Selected = true }).ToList();
        }

        string? password = null;
        if (cli.Has("encrypt") && (password = Passwords.ReadNew()) is null)
            return ExitCodes.Usage;

        Console.WriteLine($"Packaging {app.DisplayName} ({items.Count(i => i.Selected)} selected item(s))…");
        var (package, fromSnapshot) = _host.Export(output, app, result, items, session, password, useSnapshots: !cli.Has("no-vss"));

        Console.WriteLine($"Package : {package.Path}");
        Console.WriteLine($"Content : {package.FileCount} file(s), {FormatSize(package.TotalSize)}, {package.RegistryKeyCount} registry key(s)");
        Console.WriteLine($"Archive : {FormatSize(package.ArchiveSize)}{(password is null ? "" : ", encrypted (AES-256)")}");
        if (fromSnapshot > 0)
            Console.WriteLine($"          {fromSnapshot} locked file(s) read from a Volume Shadow Copy");
        foreach (var issue in package.Manifest.Issues)
            Console.WriteLine($"  [{issue.Severity}] {issue.Message}{(issue.Location is null ? "" : $" ({issue.Location})")}");
        return package.HasErrors ? ExitCodes.Error : ExitCodes.Ok;
    }

    /// <summary>
    /// Records what the application reads and writes while the user works with it (kernel ETW trace), then saves the trace
    /// for collect/export --trace.
    /// </summary>
    private int Trace(CliArguments cli)
    {
        if (CheckOptions(cli, "all-users", "hidden", "verbose", "output", "launch") is { } error)
            return error;
        if (FindSingleApp(cli, out var app) is { } notFound)
            return notFound;

        CollectionResult collection;
        using (var session = _host.OpenCollectionSession(app!, cli.Has("all-users")))
            collection = _host.Collect(app!, session);
        if (collection.InstallRoots.Count == 0)
        {
            Console.Error.WriteLine("No install directory found: the processes of the application cannot be recognized.");
            return ExitCodes.Error;
        }

        var output = Path.GetFullPath(cli.Get("output") ?? Path.ChangeExtension(AppMoverHost.DefaultPackageName(app!), ".trace.json"));
        using var tracing = _host.StartUsageTrace(app!, collection, cli.Has("launch"));
        Console.WriteLine($"Recording what {app!.DisplayName} reads and writes.");
        Console.WriteLine("Use the application now (open documents, change settings, close it), then press Enter to stop.");
        Console.ReadLine();
        var trace = tracing.Stop();

        File.WriteAllText(output, JsonSerializer.Serialize(trace, Output.Json));
        Console.WriteLine($"{trace.Processes.Count} process(es), {trace.FilesWritten.Count} file(s) written, {trace.FilesRead.Count} read, " +
                          $"{trace.RegistryKeysWritten.Count} registry key(s) written.");
        Console.WriteLine($"Trace saved to {output}. Use it with: appmover export \"{app.DisplayName}\" --trace \"{output}\"");
        return trace.Processes.Count == 0 ? ExitCodes.Error : ExitCodes.Ok;
    }

    /// <summary>--trace file: must be a trace of this application.</summary>
    private static int? LoadTrace(CliArguments cli, InstalledApp app, out UsageTrace? trace)
    {
        trace = null;
        if (cli.Get("trace") is not { } path)
            return null;
        trace = JsonSerializer.Deserialize<UsageTrace>(File.ReadAllText(path), Output.Json);
        if (trace is not null && trace.AppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase))
            return null;
        Console.Error.WriteLine($"{path} is not a usage trace of {app.Id}.");
        return ExitCodes.Error;
    }

    private int? FindSingleApp(CliArguments cli, out InstalledApp? app)
    {
        app = null;
        if (cli.Positionals.Count != 1)
            return Usage($"{cli.Command} expects exactly one argument: <app>.");

        var query = cli.Positionals[0];
        var matches = AppMoverHost.FindApps(LoadApps(cli), query);
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"No application matches '{query}'.");
            return ExitCodes.NotFound;
        }
        if (matches.Count > 1)
        {
            Console.Error.WriteLine($"'{query}' matches {matches.Count} applications, use the id:");
            foreach (var match in matches)
                Console.Error.WriteLine($"  {match.Id}  ({match.DisplayName})");
            return ExitCodes.NotFound;
        }
        app = matches[0];
        return null;
    }

    private IReadOnlyList<InstalledApp> LoadApps(CliArguments cli) => _host.LoadApps(cli.Has("all-users"), cli.Has("hidden"));

    private static void PrintCollection(InstalledApp app, CollectionResult result, IReadOnlyList<CollectedItem> items)
    {
        Console.WriteLine($"{app.DisplayName} [{app.Id}]");
        Console.WriteLine();
        if (result.InstallRoots.Count == 0)
            Console.WriteLine("Install directory: none found");
        foreach (var root in result.InstallRoots)
            Console.WriteLine($"Install directory: {root.Path} ({root.Confidence}{(root.IsPrimary ? ", primary" : "")}: {root.Reason})");
        Console.WriteLine($"Keywords: products [{string.Join(", ", result.Keywords.Products)}], aliases [{string.Join(", ", result.Keywords.Aliases)}], publishers [{string.Join(", ", result.Keywords.Publishers)}]");
        Console.WriteLine();

        foreach (var group in items.GroupBy(i => i.Confidence).OrderByDescending(g => g.Key))
        {
            Console.WriteLine($"== {group.Key} ({group.Count()})");
            foreach (var item in group)
            {
                Console.WriteLine($"  [{(item.Selected ? 'x' : ' ')}] {item.Role,-15} {item.Location}");
                Console.WriteLine($"      {item.Reason}{(item.Warning is null ? "" : $" ⚠ {item.Warning}")}");
            }
            Console.WriteLine();
        }
        Console.WriteLine($"{items.Count(i => i.Selected)}/{items.Count} item(s) selected.");
        if (result.Prerequisites.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Prerequisites (checked on the target machine at deployment):");
            foreach (var prerequisite in result.Prerequisites)
                Console.WriteLine($"  {prerequisite.Name}  ({prerequisite.Reason})");
        }
    }

    private static void PrintDetails(InstalledApp app)
    {
        void Line(string label, object? value)
        {
            if (value is not null && value.ToString() is { Length: > 0 } text)
                Console.WriteLine($"{label,-16}: {text}");
        }

        Line("Name", app.DisplayName);
        Line("Version", app.DisplayVersion);
        Line("Publisher", app.Publisher);
        Line("Id", app.Id);
        Line("Registry", app.RegistryPath);
        Line("Scope", FormatScope(app) + (app.UserSid is null ? "" : $" ({app.UserSid})"));
        Line("Install location", app.InstallLocation);
        Line("Install date", app.InstallDate);
        Line("Size", app.EstimatedSizeKb is { } kb ? FormatSize(kb * 1024) : null);
        Line("Uninstall", app.UninstallString);
        Line("Quiet uninstall", app.QuietUninstallString);
        Line("Hidden", app.HiddenReason);

        if (app.Installer is { } installer)
        {
            Console.WriteLine();
            Line("Installer", FormatInstaller(installer));
            Line("Transferable", installer.IsTransferable ? "yes" : "no");
            Line("Uninstaller", installer.UninstallerPath);
            foreach (var evidence in installer.Evidence)
                Console.WriteLine($"  + {evidence.Description} ({evidence.Weight})");
            foreach (var secondary in installer.Secondary)
                Console.WriteLine($"  Also matches: {secondary.Type} ({secondary.Confidence}%)");
            foreach (var (key, value) in installer.Properties)
                Console.WriteLine($"  {key} = {value}");
        }

        Console.WriteLine();
        Console.WriteLine("Registry values:");
        foreach (var value in app.RawValues.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            var data = value.AsString() ?? (value.Binary is { } bytes ? Convert.ToHexString(bytes) : "");
            Console.WriteLine($"  {(value.Name.Length == 0 ? "(Default)" : value.Name)} [{value.Type}] = {data}");
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };

    private static string FormatScope(InstalledApp app) => app.Scope switch
    {
        AppScope.Machine => app.View == RegView.Registry32 ? "Machine x86" : "Machine x64",
        _ => "User",
    };

    private static string FormatInstaller(InstallerInfo? installer) => installer switch
    {
        null => "",
        { Type: InstallerType.Unknown } => "Unknown",
        _ => $"{installer.Type} ({installer.Confidence}%)",
    };

    private static int? CheckOptions(CliArguments cli, params string[] known)
    {
        var unknown = cli.Unknown(known).ToList();
        return unknown.Count == 0 ? null : Usage($"Unknown option(s): {string.Join(", ", unknown.Select(f => "--" + f))}");
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Run 'appmover help' for usage.");
        return ExitCodes.Usage;
    }
}
