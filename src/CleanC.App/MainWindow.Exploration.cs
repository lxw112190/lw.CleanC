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
        var dialog = new OpenFolderDialog { Title = "选择一个或多个目录一起检查重复文件（包含子目录）", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        long minimum = long.Parse((string)((ComboBoxItem)DuplicateMinimum.SelectedItem).Tag, CultureInfo.InvariantCulture);
        await RunJob(async token =>
        {
            DuplicateGroupsGrid.ItemsSource = null; DuplicateFilesGrid.ItemsSource = null; duplicates = null;
            var progress = Progress(); var excluded = preferences.Exclusions.ToArray();
            var found = await Task.Run(() => DuplicateFinder.Find(dialog.FolderNames, excluded, minimum, progress, token, scanControl));
            duplicates = found; DuplicateGroupsGrid.ItemsSource = found.Groups;
            DuplicateDetail.Text = $"{found.Roots.Count} 个目录（含子目录） · 发现 {found.Examined:N0} 个文件 / 参与 {found.Candidates:N0} 个 · 最小大小 {(minimum == 1 ? "全部非空文件" : Display.Bytes(minimum))} · 大小过滤 {found.BelowMinimum:N0} 个 / 排除 {found.ExcludedEntries:N0} 项 / 访问、链接或变化跳过 {found.Skipped:N0} 项 · {found.Groups.Count:N0} 组内容相同文件 · 额外逻辑大小 {Display.Bytes(found.Groups.Sum(g => g.ExtraLogicalBytes))}。{(found.Cancelled ? "已停止，仅显示已校验组。" : found.Limited ? "达到 10 万候选上限，结果不完整；请缩小扫描范围。" : "检查完成。")} 文件名和扩展名不参与比较；内容相似但字节不同的文件不归为完全重复。";
            DuplicateDetail.ToolTip = string.Join("\n", found.Roots.Concat(found.SkipExamples));
            StatusText.Text = found.Limited ? "重复检查未覆盖全部文件：已达 10 万候选上限，请缩小范围。" : found.Cancelled ? "重复检查已停止，结果不完整。" : "重复文件检查结束。双击文件路径定位；悬停说明可查看范围和跳过示例。";
        }, pauseable: true);
    }
    private void DuplicateGroup_Changed(object sender, SelectionChangedEventArgs e)
    {
        DuplicateFilesGrid.ItemsSource = (DuplicateGroupsGrid.SelectedItem as DuplicateGroup)?.Files;
    }
    private async void CompareDuplicatePair_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Title = "选择两份文件，直接校验内容（不受目录扫描最小大小和排除项过滤）", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        if (dialog.FileNames.Length != 2) { StatusText.Text = "请一次选择且仅选择两个文件进行内容校验。"; return; }
        await RunJob(async token =>
        {
            duplicates = null; DuplicateGroupsGrid.ItemsSource = null; DuplicateFilesGrid.ItemsSource = null;
            var compared = await Task.Run(() => DuplicateFinder.ComparePair(dialog.FileNames[0], dialog.FileNames[1], token), token);
            string conclusion = compared.Identical ? "两份文件内容完全相同，改名或修改扩展名不影响判断。" :
                compared.Left.Size != compared.Right.Size ? "两份文件的实际字节数不同，不是完全相同的文件。" : "两份文件字节数相同，但 SHA-256 不同，内容并非完全一致。";
            string detail = conclusion + string.Concat(new[] { compared.Left, compared.Right }.Select(file =>
                $"\n\n{file.Path}\n实际大小：{file.Size:N0} 字节\nSHA-256：{file.Hash}"));
            DuplicateDetail.Text = conclusion + " 目录扫描默认检查全部非空文件；具体哈希与路径见校验结果窗口。";
            StatusText.Text = "两文件内容校验完成，只读取了所选文件。";
            var output = new TextBox { Text = detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16), Padding = new Thickness(10) };
            new Window { Owner = this, Title = "CleanC · 文件内容校验（可选中复制）", Width = 780, Height = 430,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = output }.ShowDialog();
        });
    }
    private void DuplicateFile_Open(object sender, MouseButtonEventArgs e)
    {
        if (DuplicateFilesGrid.SelectedItem is FileCandidate file) OpenPath(file.Path, true);
    }
}
