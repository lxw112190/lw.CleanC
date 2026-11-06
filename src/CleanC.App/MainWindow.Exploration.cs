using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CleanC.Core;
using Microsoft.Win32;

namespace CleanC.App;

public partial class MainWindow
{
    private List<FileCandidate> previewMatches = [];
    private int previewPage;
    private const int PreviewPageSize = 200;
    private DispatcherTimer? searchTimer;
    private DuplicateResult? duplicates;
    private static IEnumerable<Button> NavigationButtons(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
            if (child is DependencyObject node)
            {
                if (node is Button button && button.Tag is string s && int.TryParse(s, out _)) yield return button;
                foreach (var nested in NavigationButtons(node)) yield return nested;
            }
    }
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (scanControl is null) return;
        if (scanControl.IsPaused) { scanControl.Resume(); PauseButton.Content = "暂停扫描"; StatusText.Text = "正在继续扫描…"; TaskProgress.IsIndeterminate = true; }
        else { scanControl.Pause(); PauseButton.Content = "继续扫描"; StatusText.Text = "扫描已暂停，可继续或停止。"; TaskProgress.IsIndeterminate = false; }
    }
    private void PreviewSearch_Changed(object sender, TextChangedEventArgs e)
    {
        searchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        searchTimer.Stop(); searchTimer.Tick -= SearchTick; searchTimer.Tick += SearchTick; searchTimer.Start();
    }
    private void SearchTick(object? sender, EventArgs e) { searchTimer?.Stop(); RefreshPreviewMatches(); }
    private void RefreshPreviewMatches()
    {
        if (PreviewSearch is null || PreviewGrid is null) return;
        string query = PreviewSearch.Text.Trim();
        previewMatches = RulesGrid.SelectedItem is RuleResult row
            ? row.Files.Where(f => f.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.Size).ToList() : [];
        previewPage = 0; ShowPreviewPage();
    }
    private void ShowPreviewPage()
    {
        int pageCount = Math.Max(1, (previewMatches.Count + PreviewPageSize - 1) / PreviewPageSize);
        previewPage = Math.Clamp(previewPage, 0, pageCount - 1);
        PreviewGrid.ItemsSource = previewMatches.Skip(previewPage * PreviewPageSize).Take(PreviewPageSize).ToList();
        PreviewPageText.Text = $"{previewPage + 1} / {pageCount} 页 · {previewMatches.Count:N0} 个匹配";
    }
    private void PreviewPrevious_Click(object sender, RoutedEventArgs e) { previewPage--; ShowPreviewPage(); }
    private void PreviewNext_Click(object sender, RoutedEventArgs e) { previewPage++; ShowPreviewPage(); }
    private void AnalysisView_Changed(object sender, SelectionChangedEventArgs e) => RefreshAnalysisView();
    private void RefreshAnalysisView()
    {
        if (LargeFilesGrid is null || analysis is null) return;
        LargeFilesGrid.ItemsSource = AnalysisView.SelectedIndex == 1 ? analysis.FileTypes : analysis.LargestFiles;
        LargeFilesGrid.Columns[0].Header = AnalysisView.SelectedIndex == 1 ? "文件类型" : "文件 · 双击定位";
    }
    private void InvalidateDuplicates()
    {
        duplicates = null; DuplicateGroupsGrid.ItemsSource = null; DuplicateFilesGrid.ItemsSource = null;
        DuplicateDetail.Text = "排除项已变化，请重新检查重复文件。";
    }
    private async void ChooseDuplicates_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !validSettings) return;
        var dialog = new OpenFolderDialog { Title = "选择目录检查重复文件（会读取文件内容）" };
        if (dialog.ShowDialog(this) != true) return;
        long minimum = long.Parse((string)((ComboBoxItem)DuplicateMinimum.SelectedItem).Tag, CultureInfo.InvariantCulture);
        await RunJob(async token =>
        {
            DuplicateGroupsGrid.ItemsSource = null; DuplicateFilesGrid.ItemsSource = null; duplicates = null;
            var progress = Progress(); var excluded = preferences.Exclusions.ToArray();
            var found = await Task.Run(() => DuplicateFinder.Find(dialog.FolderName, excluded, minimum, progress, token, scanControl));
            duplicates = found; DuplicateGroupsGrid.ItemsSource = found.Groups;
            DuplicateDetail.Text = $"{found.Root} · {found.Examined:N0} 个文件 · {found.Groups.Count:N0} 组 SHA-256 相同文件 · 额外逻辑大小 {Display.Bytes(found.Groups.Sum(g => g.ExtraLogicalBytes))} · 跳过 {found.Skipped:N0} 项 · 已读取 {Display.Bytes(found.BytesRead)}。{(found.Cancelled ? "已停止，仅显示已校验组。" : found.Limited ? "达到 10 万候选上限，结果不完整。" : "检查完成。")} 结果代表扫描时的内容，额外逻辑大小不是保证可释放空间。";
            StatusText.Text = "重复文件检查结束。双击文件路径定位，处理前请核对用途。";
        }, pauseable: true);
    }
    private void DuplicateGroup_Changed(object sender, SelectionChangedEventArgs e)
    {
        DuplicateFilesGrid.ItemsSource = (DuplicateGroupsGrid.SelectedItem as DuplicateGroup)?.Files;
    }
    private void DuplicateFile_Open(object sender, MouseButtonEventArgs e)
    {
        if (DuplicateFilesGrid.SelectedItem is FileCandidate file) OpenPath(file.Path, true);
    }
}
