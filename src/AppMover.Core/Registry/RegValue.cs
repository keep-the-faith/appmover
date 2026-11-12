using System.Globalization;

namespace AppMover.Core.Registry;

public enum RegHive { LocalMachine, CurrentUser, Users, ClassesRoot }

/// <summary>Registry view. <see cref="Default"/> is the process view (64-bit for AppMover).</summary>
public enum RegView { Default, Registry64, Registry32 }

public enum RegValueType { None, String, ExpandString, Binary, DWord, MultiString, QWord, Unknown }

/// <summary>A raw registry value (environment variables are never expanded).</summary>
public sealed record RegValue
{
    public required string Name { get; init; }
    public required RegValueType Type { get; init; }
    /// <summary>REG_SZ / REG_EXPAND_SZ.</summary>
    public string? Text { get; init; }
    /// <summary>REG_DWORD (stored unsigned) / REG_QWORD.</summary>
    public long? Number { get; init; }
    /// <summary>REG_MULTI_SZ.</summary>
    public string[]? Multi { get; init; }
    /// <summary>REG_BINARY / REG_NONE / unknown types.</summary>
    public byte[]? Binary { get; init; }

    public static RegValue String(string name, string text) => new() { Name = name, Type = RegValueType.String, Text = text };
    public static RegValue ExpandString(string name, string text) => new() { Name = name, Type = RegValueType.ExpandString, Text = text };
    public static RegValue DWord(string name, uint value) => new() { Name = name, Type = RegValueType.DWord, Number = value };
    public static RegValue MultiString(string name, params string[] values) => new() { Name = name, Type = RegValueType.MultiString, Multi = values };
    public static RegValue Bytes(string name, byte[] data) => new() { Name = name, Type = RegValueType.Binary, Binary = data };

    /// <summary>Same type and data (the name is not compared).</summary>
    public bool SameData(RegValue other) =>
        Type == other.Type && Text == other.Text && Number == other.Number
        && (Multi ?? []).SequenceEqual(other.Multi ?? []) && (Binary ?? []).SequenceEqual(other.Binary ?? []);

    /// <summary>Best-effort string representation (installers sometimes store numbers as strings and vice versa).</summary>
    public string? AsString() => Type switch
    {
        RegValueType.String or RegValueType.ExpandString => Text,
        RegValueType.DWord or RegValueType.QWord => Number?.ToString(CultureInfo.InvariantCulture),
        RegValueType.MultiString => Multi is null ? null : string.Join("\n", Multi),
        _ => null,
    };

    public long? AsNumber() => Type switch
    {
        RegValueType.DWord or RegValueType.QWord => Number,
        RegValueType.String or RegValueType.ExpandString when long.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };
}
