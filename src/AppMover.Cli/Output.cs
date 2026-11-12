using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppMover.Cli;

internal static class Output
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void WriteJson<T>(T value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, Json));

    /// <summary>Writes a left-aligned text table; each column is capped to its max width.</summary>
    public static void WriteTable(IReadOnlyList<(string Header, int MaxWidth)> columns, IEnumerable<string?[]> rows)
    {
        var data = rows.Select(r => r.Select(c => c ?? "").ToArray()).ToList();
        var widths = columns
            .Select((c, i) => Math.Min(c.MaxWidth, Math.Max(c.Header.Length, data.Count == 0 ? 0 : data.Max(r => r[i].Length))))
            .ToArray();

        var sb = new StringBuilder();
        AppendRow(sb, columns.Select(c => c.Header).ToArray(), widths);
        AppendRow(sb, widths.Select(w => new string('-', w)).ToArray(), widths);
        foreach (var row in data)
            AppendRow(sb, row, widths);
        Console.Out.Write(sb.ToString());
    }

    private static void AppendRow(StringBuilder sb, string[] cells, int[] widths)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = cells[i].Length > widths[i] ? cells[i][..(widths[i] - 1)] + "…" : cells[i];
            sb.Append(i == cells.Length - 1 ? cell : cell.PadRight(widths[i] + 2));
        }
        sb.AppendLine();
    }
}
