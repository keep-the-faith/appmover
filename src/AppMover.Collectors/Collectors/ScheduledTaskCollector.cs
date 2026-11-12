using System.Xml;
using System.Xml.Linq;
using AppMover.Core.Models;
using AppMover.Core.Text;
using static AppMover.Collectors.CollectionContext;

namespace AppMover.Collectors.Collectors;

/// <summary>
/// Scheduled tasks (XML files in System32\Tasks) with an action of the application:
/// a program in an install directory (Certain) or in a data folder (at most Probable),
/// or a COM handler whose class was collected.
/// </summary>
public sealed class ScheduledTaskCollector : ICollector
{
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public string Name => "Scheduled tasks";

    public CollectionPhase Phase => CollectionPhase.Dependent;

    public IEnumerable<CollectedItem> Collect(CollectionContext context)
    {
        var tasksDir = WinPath.Combine(context.Session.Machine.System32, "Tasks");
        foreach (var file in context.Files.EnumerateFiles(tasksDir, recursive: true))
        {
            if (Evaluate(context, file) is not { } match)
                continue;

            var taskName = WinPath.Normalize(file)[WinPath.Normalize(tasksDir).Length..];
            yield return Item(Name, ItemKind.File, ItemRole.ScheduledTask, match.Confidence, $"Task {taskName} {match.Reason}",
                path: file, details: new Dictionary<string, string> { ["TaskName"] = taskName });
        }
    }

    /// <summary>The best matching action of the task, or null.</summary>
    private static (Confidence Confidence, string Reason)? Evaluate(CollectionContext context, string file)
    {
        XDocument document;
        try
        {
            using var stream = context.Files.OpenRead(file);
            document = XDocument.Load(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }

        var matches = new List<(Confidence Confidence, string Reason)>();
        foreach (var exec in document.Descendants(TaskNamespace + "Exec"))
        {
            var command = $"{exec.Element(TaskNamespace + "Command")?.Value} {exec.Element(TaskNamespace + "Arguments")?.Value}".Trim();
            if (context.FindReferencedFolder(command) is { } folder)
                matches.Add((folder.Confidence, $"runs from {folder.Path}"));
        }
        foreach (var handler in document.Descendants(TaskNamespace + "ComHandler"))
        {
            var clsid = handler.Element(TaskNamespace + "ClassId")?.Value;
            if (context.FindComClass(clsid) is { } confidence)
                matches.Add((confidence, $"calls the application's COM class {clsid}"));
        }

        return matches.Count == 0 ? null : matches.MaxBy(m => m.Confidence);
    }
}
