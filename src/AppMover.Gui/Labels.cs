using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AppMover.Core.Models;
using AppMover.Core.Registry;
using AppMover.Packaging;

namespace AppMover.Gui;

/// <summary>Display labels of the model values.</summary>
public static class Labels
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };

    public static string Confidence(Confidence confidence) => confidence switch
    {
        Core.Models.Confidence.Certain => "Certain",
        Core.Models.Confidence.Probable => "Probable",
        _ => "Possible",
    };

    public static string Role(ItemRole role) => role switch
    {
        ItemRole.Application => "Application",
        ItemRole.Data => "Data",
        ItemRole.Settings => "Settings",
        ItemRole.UninstallEntry => "Uninstall entry",
        ItemRole.Shortcut => "Shortcut",
        ItemRole.Service => "Service",
        ItemRole.Driver => "Driver",
        ItemRole.ScheduledTask => "Scheduled task",
        ItemRole.Startup => "Startup",
        ItemRole.Com => "COM",
        ItemRole.FileAssociation => "File association",
        ItemRole.AppPath => "App Path",
        ItemRole.Environment => "Environment",
        ItemRole.FirewallRule => "Firewall rule",
        _ => role.ToString(),
    };

    public static string Scope(InstalledApp app) => app.Scope switch
    {
        AppScope.Machine => app.View == RegView.Registry32 ? "Machine (32-bit)" : "Machine (64-bit)",
        _ => "User",
    };

    public static string Installer(InstallerInfo? installer) => installer switch
    {
        null or { Type: InstallerType.Unknown } => "Unknown",
        _ => $"{installer.Type} ({installer.Confidence} %)",
    };
}

/// <summary>Errors in red, warnings in orange.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IssueSeverity.Error ? Brushes.Firebrick : Brushes.DarkOrange;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is false (or null).</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is not null, not an empty string and not 0 (e.g. a collection Count).</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null or "" or 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
