using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CleanC.Core;

namespace CleanC.App;

public partial class MainWindow
{
    private AnalysisComparison? analysisComparison;

    private (AnalysisResult Result, AnalysisComparison? Comparison, string Note) AnalyzeAndRecord(string root, string[] exclusions,
        IProgress<ScanProgress> progress, CancellationToken token)
    {
        var scanned = Scanner.Analyze(root, exclusions, progress, token, scanControl, collectSnapshot: true);
        if (scanned.Cancelled || token.IsCancellationRequested) return (scanned with { Cancelled = true }, null, "扫描已停止，显示部分结果。");
        if (scanned.FileCount == 0 && scanned.Skipped > 0) return (scanned, null, "未读取到文件，无法建立快照。");
        long? available = null;
        try { available = new DriveInfo(Path.GetPathRoot(scanned.Root)!).AvailableFreeSpace; }
        catch (IOException) { }
        try
        {
            var comparison = new AnalysisSnapshotStore(Path.Combine(dataDirectory, "Analysis")).Record(scanned, exclusions, available);
            return (scanned, comparison, comparison is null ? "已保存基准，下次扫描同一范围即可比较。" : "已保存快照，可与上次比较。");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return (scanned, null, "快照保存或读取失败：" + e.Message);
        }
    }

    private void ClearAnalysisComparison()
    {
        analysisComparison = null;
        CompareAnalysisButton.IsEnabled = false;
    }

    private void CompareAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (busy || analysisComparison is not { } comparison) return;
        var before = comparison.Before;
        var after = comparison.After;
        string free = before.AvailableBytes is long previous && after.AvailableBytes is long current
            ? $"{Display.Bytes(previous)} → {Display.Bytes(current)}（{SignedBytes(current - previous)}）" : "无法读取";
        string coverage = $"前后分别跳过 {before.Skipped:N0} / {after.Skipped:N0} 项。";
        if (before.IndexLimited || after.IndexLimited) coverage += " 目录索引达到上限，排行仅比较两次都记录的路径。";
        var panel = new DockPanel { Margin = new Thickness(18) };
        var summary = new TextBlock
        {
            Text = $"{after.Root}\n{before.RecordedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss} → {after.RecordedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n" +
                $"所在盘可用空间 {free}    扫描范围逻辑大小变化 {SignedBytes(after.LogicalBytes - before.LogicalBytes)}\n{coverage}",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(summary, Dock.Top);
        panel.Children.Add(summary);
        var depth = new ComboBox { ItemsSource = new[] { "全部层级", "第 1 层", "第 2 层", "第 3 层", "第 4 层", "第 5 层", "第 6 层" }, SelectedIndex = 0,
            Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        var hint = new TextBlock { Text = "每行包含其子目录的大小。选择同一层级可避免重复计入。未记录可能由跳过或路径变化造成。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(depth, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(depth);
        panel.Children.Add(hint);
        var grid = ReportGrid(("目录", "Path", DataGridLengthUnitType.Star, 1),
            ("层级", "Depth", DataGridLengthUnitType.Pixel, 60), ("上次", "Before", DataGridLengthUnitType.Pixel, 100),
            ("本次", "After", DataGridLengthUnitType.Pixel, 100), ("变化", "Delta", DataGridLengthUnitType.Pixel, 110));
        grid.Columns[^1].SortMemberPath = "DeltaBytes";
        void ApplyDepth() => grid.ItemsSource = comparison.Changes.Where(row => depth.SelectedIndex == 0 || depth.SelectedIndex == row.Depth)
            .Select(row => new { Path = row.Path.Equals(after.Root, StringComparison.OrdinalIgnoreCase) ? $"{row.Path}（直接文件）" : row.Path,
                row.Depth, Before = row.Before is long a ? Display.Bytes(a) : "未记录",
                After = row.After is long b ? Display.Bytes(b) : "未记录", Delta = SignedBytes(row.Delta), DeltaBytes = row.Delta }).ToArray();
        depth.SelectionChanged += (_, _) => ApplyDepth();
        ApplyDepth();
        panel.Children.Add(grid);
        ShowReport("空间变化 · 与上次比较", panel);
    }

    private async void HistoryDetails_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (HistoryGrid.SelectedItem is not HistoryRow row)
        {
            StatusText.Text = "请先选中一条清理记录。";
            return;
        }
        HistoryDetails? details = null;
        await RunJob(async token => details = await Task.Run(() => HistoryDetailsReader.Read(row.Path, token), token));
        if (details is null) return;
        var panel = new DockPanel { Margin = new Thickness(18) };
        var hint = new TextBlock
        {
            Text = $"{row.Time} · {row.Status} · 可用空间变化 {row.Delta}\n" +
                (details.Managed ? "此记录由官方工具汇报，逐文件结果请以该工具输出为准。" :
                    "选中一行可查看最多 20 个文件示例。双击示例定位仍存在的文件或其目录。") +
                (details.HasSummary ? "" : " 记录未完成，下方仅显示已写入的结果。") +
                (details.InvalidLines > 0 ? $" 跳过 {details.InvalidLines:N0} 条无法读取的日志。" : ""),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(hint);
        var paths = new ListBox { Height = 115, Margin = new Thickness(0, 10, 0, 0) };
        paths.MouseDoubleClick += (_, _) =>
        {
            if (paths.SelectedItem is not string path) return;
            if (!Path.IsPathFullyQualified(path) || path.Contains('"')) return;
            if (File.Exists(path)) OpenPath(path, true);
            else if (Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent)) OpenPath(parent);
        };
        DockPanel.SetDock(paths, Dock.Bottom);
        panel.Children.Add(paths);
        var grid = ReportGrid(("规则", "Rule", DataGridLengthUnitType.Pixel, 175), ("结果 / 原因", "Reason", DataGridLengthUnitType.Star, 1),
            ("已删除", "Deleted", DataGridLengthUnitType.Pixel, 90), ("已跳过", "Skipped", DataGridLengthUnitType.Pixel, 90));
        grid.ItemsSource = details.Rows;
        grid.SelectionChanged += (_, _) => paths.ItemsSource = (grid.SelectedItem as HistoryBreakdown)?.Samples;
        panel.Children.Add(grid);
        grid.SelectedIndex = 0;
        ShowReport("清理明细", panel);
    }

    private static DataGrid ReportGrid(params (string Header, string Property, DataGridLengthUnitType Unit, double Width)[] columns)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, EnableRowVirtualization = true };
        foreach (var column in columns)
            grid.Columns.Add(new DataGridTextColumn { Header = column.Header, Binding = new Binding(column.Property),
                Width = new DataGridLength(column.Width, column.Unit) });
        return grid;
    }

    private void ShowReport(string title, UIElement content) => new Window
    {
        Owner = this, Title = "CleanC · " + title, Width = 930, Height = 530, MinWidth = 650, MinHeight = 350,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content
    }.ShowDialog();
}
