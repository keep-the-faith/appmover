using System.Xml.Linq;

namespace AppMover.Deployment;

/// <summary>Adapts a scheduled task definition to the target machine: paths and user accounts.</summary>
public static class TaskXmlRewriter
{
    private static readonly string[] PathElements = ["Command", "Arguments", "WorkingDirectory"];

    /// <param name="xml">Task XML exported from the source machine.</param>
    /// <param name="relocate">Converts source paths found in a text into target paths.</param>
    /// <param name="accounts">Source account (SID, NAME or COMPUTER\NAME) → target SID.</param>
    public static string Rewrite(string xml, Func<string, string> relocate, IReadOnlyDictionary<string, string> accounts)
    {
        var document = XDocument.Parse(xml);
        foreach (var element in document.Descendants().ToList())
        {
            var name = element.Name.LocalName;
            if (name == "UserId" && accounts.TryGetValue(element.Value.Trim(), out var target))
                element.Value = target;
            else if (PathElements.Contains(name))
                element.Value = relocate(element.Value);
        }
        return document.ToString();
    }
}
