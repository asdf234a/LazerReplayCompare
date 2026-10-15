using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace LazerReplayCompare;

/// <summary>
/// Rearranges a side-by-side <see cref="Grid"/> when the page is narrow (a small window, or a large page zoom), so its
/// parts stack instead of being squeezed or cut off.
/// </summary>
public static class ResponsiveLayout
{
    /// <summary>Below this width (in page units, so the zoom counts) the pages switch to their narrow layouts.</summary>
    public const double NarrowWidth = 800;

    private sealed record WideLayout(GridLength[] Columns, GridLength[] Rows, (int Row, int Column, int ColumnSpan)[] Cells);

    private static readonly ConditionalWeakTable<Grid, WideLayout> wide_layouts = new();

    /// <summary>
    /// Narrow: the grid gets <paramref name="narrowColumns"/> and its children move to <paramref name="narrowCells"/>
    /// (row, column), in child order; one child per row in a single column when no cells are given. Wide: the layout
    /// from the XAML comes back.
    /// </summary>
    public static void Apply(Grid grid, bool narrow, string narrowColumns = "*", params (int Row, int Column)[] narrowCells)
    {
        var layout = wide_layouts.GetValue(grid, g => new WideLayout(
            g.ColumnDefinitions.Select(c => c.Width).ToArray(),
            g.RowDefinitions.Select(r => r.Height).ToArray(),
            g.Children.Select(c => (Grid.GetRow(c), Grid.GetColumn(c), Grid.GetColumnSpan(c))).ToArray()));

        if (narrow)
        {
            var cells = narrowCells.Length > 0 ? narrowCells : grid.Children.Select((_, i) => (i, 0)).ToArray();
            grid.ColumnDefinitions = new ColumnDefinitions(narrowColumns);
            grid.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", cells.Max(c => c.Item1) + 1)));
            for (var i = 0; i < grid.Children.Count && i < cells.Length; i++)
            {
                Grid.SetRow(grid.Children[i], cells[i].Item1);
                Grid.SetColumn(grid.Children[i], cells[i].Item2);
                Grid.SetColumnSpan(grid.Children[i], 1);
            }
        }
        else
        {
            var columns = new ColumnDefinitions();
            columns.AddRange(layout.Columns.Select(width => new ColumnDefinition(width)));
            grid.ColumnDefinitions = columns;
            var rows = new RowDefinitions();
            rows.AddRange(layout.Rows.Select(height => new RowDefinition(height)));
            grid.RowDefinitions = rows;
            for (var i = 0; i < grid.Children.Count && i < layout.Cells.Length; i++)
            {
                Grid.SetRow(grid.Children[i], layout.Cells[i].Row);
                Grid.SetColumn(grid.Children[i], layout.Cells[i].Column);
                Grid.SetColumnSpan(grid.Children[i], layout.Cells[i].ColumnSpan);
            }
        }
    }
}
