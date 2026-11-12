using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AppMover.Core.Msi;

/// <summary>Runs SQL queries on a Windows Installer database (.msi), read-only.</summary>
public interface IMsiDatabaseReader
{
    /// <summary>Rows of the query (null fields for NULL), or null when the database or the table cannot be read.</summary>
    IReadOnlyList<string?[]>? Query(string databasePath, string sql);
}

/// <summary>msi.dll implementation.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMsiDatabaseReader : IMsiDatabaseReader
{
    private static readonly IntPtr ReadOnly = IntPtr.Zero; // MSIDBOPEN_READONLY
    private const uint ErrorSuccess = 0;
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorMoreData = 234;

    public IReadOnlyList<string?[]>? Query(string databasePath, string sql)
    {
        if (MsiOpenDatabase(databasePath, ReadOnly, out var database) != ErrorSuccess)
            return null;
        try
        {
            // Fails when the table does not exist (e.g. no Registry table).
            if (MsiDatabaseOpenView(database, sql, out var view) != ErrorSuccess)
                return null;
            try
            {
                if (MsiViewExecute(view, IntPtr.Zero) != ErrorSuccess)
                    return null;
                var rows = new List<string?[]>();
                while (true)
                {
                    var result = MsiViewFetch(view, out var record);
                    if (result == ErrorNoMoreItems)
                        return rows;
                    if (result != ErrorSuccess)
                        return null;
                    try
                    {
                        var count = MsiRecordGetFieldCount(record);
                        var row = new string?[count];
                        for (var i = 0; i < count; i++)
                            row[i] = MsiRecordIsNull(record, (uint)(i + 1)) ? null : GetString(record, (uint)(i + 1));
                        rows.Add(row);
                    }
                    finally
                    {
                        MsiCloseHandle(record);
                    }
                }
            }
            finally
            {
                MsiCloseHandle(view);
            }
        }
        finally
        {
            MsiCloseHandle(database);
        }
    }

    private static string GetString(IntPtr record, uint field)
    {
        var size = 256u;
        var buffer = new StringBuilder((int)size);
        var result = MsiRecordGetString(record, field, buffer, ref size);
        if (result == ErrorMoreData)
        {
            buffer = new StringBuilder((int)++size);
            result = MsiRecordGetString(record, field, buffer, ref size);
        }
        return result == ErrorSuccess ? buffer.ToString() : "";
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string databasePath, IntPtr persist, out IntPtr handle);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll")]
    private static extern uint MsiRecordGetFieldCount(IntPtr record);

    [DllImport("msi.dll")]
    private static extern bool MsiRecordIsNull(IntPtr record, uint field);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder value, ref uint size);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(IntPtr handle);
}
