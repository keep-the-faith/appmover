using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AppMover.Gui;

/// <summary>
/// Works around a WPF bug: a DataGrid laid out while collapsed (e.g. a wizard step not shown yet) computes its star
/// columns against a zero width and keeps them shrunk once shown. Star widths are re-applied whenever the grid becomes
/// visible.
/// </summary>
public static class DataGridStarColumns
{
    public static readonly DependencyProperty RefreshOnShowProperty = DependencyProperty.RegisterAttached(
        "RefreshOnShow", typeof(bool), typeof(DataGridStarColumns), new PropertyMetadata(false, OnRefreshOnShowChanged));

    public static bool GetRefreshOnShow(DependencyObject element) => (bool)element.GetValue(RefreshOnShowProperty);

    public static void SetRefreshOnShow(DependencyObject element, bool value) => element.SetValue(RefreshOnShowProperty, value);

    private static void OnRefreshOnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
            return;
        grid.IsVisibleChanged -= OnIsVisibleChanged;
        if ((bool)e.NewValue)
            grid.IsVisibleChanged += OnIsVisibleChanged;
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
            return;
        var grid = (DataGrid)sender;
        // After the layout pass that gives the grid its real width.
        grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            foreach (var column in grid.Columns)
            {
                if (column.Width.IsStar)
                    column.Width = new DataGridLength(column.Width.Value, DataGridLengthUnitType.Star);
            }
        });
    }
}
