using AppMover.Collectors;
using AppMover.Core.Models;
using AppMover.Core.Registry;
using AppMover.Core.Text;

namespace AppMover.Packaging;

/// <summary>Reads the selected registry items into tokenized records.</summary>
public sealed class RegistryExporter(IRegistrySource registry, CollectionSession session, PathTokenizer tokenizer)
{
    public List<RegKeyRecord> Export(IEnumerable<CollectedItem> items, List<PackageIssue> issues)
    {
        var records = new List<RegKeyRecord>();
        var exportedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items.Where(i => i.Kind == ItemKind.RegistryKey))
        {
            using var key = session.OpenKey(registry, item.Registry!);
            if (key is null)
            {
                issues.Add(new PackageIssue(IssueSeverity.Warning, "Registry key not found or not readable", item.Location));
                continue;
            }
            ExportTree(key, item.Registry!, records, exportedKeys);
        }

        // Single values whose key was not exported as a whole, grouped per key.
        var values = items
            .Where(i => i.Kind == ItemKind.RegistryValue && !exportedKeys.Contains(i.Registry!.ToString()))
            .GroupBy(i => i.Registry!);
        foreach (var group in values)
        {
            using var key = session.OpenKey(registry, group.Key);
            var exported = group
                .Select(i => key?.GetValue(i.ValueName ?? ""))
                .OfType<RegValue>()
                .ToList();
            if (exported.Count < group.Count())
                issues.Add(new PackageIssue(IssueSeverity.Warning, "Registry value(s) not found", group.Key.ToString()));
            if (exported.Count > 0)
                records.Add(CreateRecord(group.Key, exported, partial: true));
        }

        return records;
    }

    private void ExportTree(IRegistryKey key, RegistryLocation location, List<RegKeyRecord> records, HashSet<string> exportedKeys)
    {
        if (!exportedKeys.Add(location.ToString()))
            return;

        records.Add(CreateRecord(location, key.GetValueNames().Select(key.GetValue).OfType<RegValue>(), partial: false));
        foreach (var name in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(name);
            if (sub is not null)
                ExportTree(sub, location with { Path = $@"{location.Path}\{name}" }, records, exportedKeys);
        }
    }

    private RegKeyRecord CreateRecord(RegistryLocation location, IEnumerable<RegValue> values, bool partial)
    {
        var tokenized = new List<string>();
        var list = values.Select(value =>
        {
            var result = Tokenize(value);
            if (!ReferenceEquals(result, value))
                tokenized.Add(value.Name);
            return result;
        }).ToList();

        return new RegKeyRecord
        {
            Hive = location.Hive,
            View = location.View,
            UserIndex = session.FindUser(location.UserSid)?.Index,
            Path = location.Path,
            Partial = partial,
            Values = list,
            TokenizedValues = tokenized.Count == 0 ? null : tokenized,
        };
    }

    /// <summary>Returns a tokenized copy, or the same instance when nothing changed.</summary>
    private RegValue Tokenize(RegValue value)
    {
        switch (value.Type)
        {
            case RegValueType.String or RegValueType.ExpandString when value.Text is not null:
                var text = tokenizer.TokenizeText(value.Text);
                return text == value.Text ? value : value with { Text = text };
            case RegValueType.MultiString when value.Multi is not null:
                var multi = value.Multi.Select(tokenizer.TokenizeText).ToArray();
                return multi.SequenceEqual(value.Multi) ? value : value with { Multi = multi };
            default:
                return value;
        }
    }
}
