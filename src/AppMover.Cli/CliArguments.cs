namespace AppMover.Cli;

/// <summary>Minimal parser: <c>command [positionals] [--flag] [--option value]</c>.</summary>
public sealed class CliArguments
{
    /// <summary>Options followed by a value, with their short aliases.</summary>
    private static readonly Dictionary<string, string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--output"] = "output",
        ["-o"] = "output",
        ["--items"] = "items",
        ["--install-dir"] = "install-dir",
        ["--user-map"] = "user-map",
        ["--trace"] = "trace",
    };

    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positionals = [];

    public string? Command { get; private set; }
    public IReadOnlyList<string> Positionals => _positionals;
    /// <summary>Set when the command line is invalid (e.g. an option without its value).</summary>
    public string? Error { get; private set; }

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        var result = new CliArguments();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (ValueOptions.TryGetValue(arg, out var option))
            {
                if (i + 1 >= args.Count)
                    result.Error = $"Option {arg} expects a value.";
                else
                    result._values[option] = args[++i];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
                result._flags.Add(arg[2..]);
            else if (arg is "-h" or "/?")
                result._flags.Add("help");
            else if (result.Command is null)
                result.Command = arg.ToLowerInvariant();
            else
                result._positionals.Add(arg);
        }
        return result;
    }

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Get(string option) => _values.GetValueOrDefault(option);

    /// <summary>Flags and options not in <paramref name="known"/>.</summary>
    public IEnumerable<string> Unknown(params string[] known) =>
        _flags.Concat(_values.Keys).Where(f => !known.Contains(f, StringComparer.OrdinalIgnoreCase));
}
