using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CleanC.Core;
using Microsoft.Win32;

namespace CleanC.App;

public partial class MainWindow : Window
{
    private readonly string dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
    private string RulesPath => Path.Combine(dataDirectory, "rules.json");
    private string SettingsPath => Path.Combine(dataDirectory, "settings.json");
    private string LogDirectory => Path.Combine(dataDirectory, "History");
    private readonly PathPolicy policy = new();
    private RuleDocument document = new();
    private Preferences preferences = new();
    private List<RuleResult> results = [];
    private AnalysisResult? analysis;
    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool validRules;
    private bool validSettings;
    private ScanControl? scanControl;

    public MainWindow()
    {
        InitializeComponent();
        DirectoryMap.OpenDirectory += async path => { if (analysis is not null && !path.Equals(analysis.Root, StringComparison.OrdinalIgnoreCase)) await Analyze(path); };
        RulesPathText.Text = RulesPath;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            if (!File.Exists(RulesPath)) File.Copy(Path.Combine(AppContext.BaseDirectory, "Rules", "default.rules.json"), RulesPath);
            if (File.Exists(SettingsPath))
            {
                preferences = JsonFiles.Read<Preferences>(SettingsPath);
                preferences.Exclusions = (preferences.Exclusions ?? []).Select(PathPolicy.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            validSettings = true;
            LoadRules();
            RefreshExclusions();
        }
        catch (Exception e) { StatusText.Text = "配置加载失败，请检查规则与数据目录：" + e.Message; }
        RefreshDrive(); RefreshHistory(); ShowPage(0);
    }

    private void ShowPage(int page)
    {
        Pages.SelectedIndex = page;
        string[] titles = ["空间概览", "分类清理", "空间分析", "规则与排除", "清理记录", "重复文件", "专项清理"];
        string[] subtitles = ["看清占用，按需整理。", "每一项都有来源，每一次清理都可核对。", "从目录到文件，找到空间的去向。", "让规则适合你的使用习惯。", "保留每一次操作的结果。", "按内容核对，找到重复占用的文件。", "核对来源与使用状态，再处理大项缓存。"];
        PageTitle.Text = titles[page]; PageSubtitle.Text = subtitles[page];
        foreach (var button in NavigationButtons(LayoutRoot))
        {
            bool active = (string)button.Tag == page.ToString();
            button.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(active ? "#087F75" : "#21444A"));
            button.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        }
    }
    private void Navigate(object sender, RoutedEventArgs e) => ShowPage(int.Parse((string)((Button)sender).Tag));
    private bool CanStart() => !busy;
    private void RefreshDrive()
    {
        try
        {
            var drive = new DriveInfo("C");
            FreeText.Text = Display.Bytes(drive.AvailableFreeSpace);
            DriveDetail.Text = $"已用 {Display.Bytes(drive.TotalSize - drive.TotalFreeSpace)} / 总计 {Display.Bytes(drive.TotalSize)}";
            DriveBar.Value = (drive.TotalSize - drive.TotalFreeSpace) * 100.0 / drive.TotalSize;
        }
        catch (IOException) { FreeText.Text = "无法读取"; }
    }
    private Progress<ScanProgress> Progress() => new(p => { if (scanControl?.IsPaused != true) StatusText.Text = $"{p.Message} · {p.FileCount:N0} 个文件"; });
    private async Task RunJob(Func<CancellationToken, Task> action, bool pauseable = false)
    {
        if (busy) return;
        busy = true; cancellation = new(); CancelButton.IsEnabled = true;
        scanControl = pauseable ? new ScanControl() : null; PauseButton.IsEnabled = pauseable; PauseButton.Content = "暂停扫描";
        TaskProgress.Visibility = Visibility.Visible; CleanButton.IsEnabled = false; RulesGrid.IsEnabled = false;
        InstallerGrid.IsEnabled = false; UpdateInstallerSelection();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "任务已停止。缓存扫描结果未完成时不会提供清理入口。"; }
        catch (Exception e) { StatusText.Text = "操作失败：" + e.Message; MessageBox.Show(this, e.Message, "CleanC · 操作失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            cancellation.Dispose(); cancellation = null; busy = false; CancelButton.IsEnabled = false;
            scanControl?.Dispose(); scanControl = null; PauseButton.IsEnabled = false; PauseButton.Content = "暂停扫描";
            TaskProgress.IsIndeterminate = true;
            TaskProgress.Visibility = Visibility.Collapsed; RulesGrid.IsEnabled = true; UpdateSelection(); RefreshDrive();
            InstallerGrid.IsEnabled = true;
            UpdateInstallerSelection();
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); StatusText.Text = "正在停止，请等待当前文件操作完成…"; }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!busy) return;
        e.Cancel = true; cancellation?.Cancel(); StatusText.Text = "正在停止任务，停止后即可关闭窗口。";
    }
    private void InvalidatePreview()
    {
        results = []; RulesGrid.ItemsSource = results; PreviewGrid.ItemsSource = null;
        previewMatches = []; previewPage = 0; PreviewPageText.Text = "";
        CacheTotal.Text = "未扫描"; ImpactText.Text = "选择一行查看影响与文件预览。";
        PreviewNote.Text = "清理范围或文件状态已变化，请重新扫描。"; UpdateSelection();
    }
    private void LoadRules()
    {
        validRules = false; InvalidatePreview();
        var parsed = JsonFiles.Read<RuleDocument>(RulesPath);
        var shipped = JsonFiles.Read<RuleDocument>(Path.Combine(AppContext.BaseDirectory, "Rules", "default.rules.json"));
        string statePath = Path.Combine(dataDirectory, "catalog-seen.json");
        string[] seen = File.Exists(statePath) ? JsonFiles.Read<string[]>(statePath) : RuleCatalog.LegacyIds;
        var upgraded = RuleCatalog.Upgrade(parsed, shipped, seen, policy);
        if (upgraded.Rules.Count != parsed.Rules.Count)
        {
            File.Copy(RulesPath, Path.Combine(dataDirectory, $"rules-backup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
            JsonFiles.Write(RulesPath, upgraded); parsed = upgraded;
        }
        JsonFiles.Write(statePath, shipped.Rules.Select(r => r.Id).Concat(seen).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        policy.Validate(parsed); document = parsed; validRules = true;
        RuleCount.Text = document.Rules.Count.ToString(); StatusText.Text = $"已载入 {document.Rules.Count} 条规则。";
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStart()) return;
        if (!validSettings) { StatusText.Text = "排除设置未能加载，请修复 Data/settings.json 后重启，避免遗漏排除项。"; return; }
        ShowPage(1);
        await RunJob(async token =>
        {
            LoadRules();
            var progress = Progress(); var excluded = preferences.Exclusions.ToArray();
            var resolved = RuleResolver.Resolve(document); policy.Validate(resolved);
            var scanned = await Task.Run(() => new Scanner(policy).Scan(resolved, excluded, progress, token, scanControl), token);
            results = scanned;
            foreach (var result in results) result.PropertyChanged += (_, _) => UpdateSelection();
            RulesGrid.ItemsSource = results;
            CacheTotal.Text = Display.Bytes(results.Sum(r => r.Size));
            StatusText.Text = $"缓存扫描完成 · {results.Sum(r => r.Count):N0} 个文件 · 文件总量 {CacheTotal.Text}；请检查清理影响。";
        }, pauseable: true);
    }
    private void UpdateSelection()
    {
        var chosen = results.Where(r => r.Selected && r.Count > 0).ToList();
        SelectionTotal.Text = $"已勾选 {chosen.Count} 项 · {Display.Bytes(chosen.Sum(r => r.Size))}";
        CleanButton.IsEnabled = !busy && validRules && chosen.Count > 0;
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { if (!busy) foreach (var r in results) r.Selected = r.Count > 0 && !r.Rule.AnalysisOnly && !r.Rule.ReviewRequired; }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { if (!busy) foreach (var r in results) r.Selected = false; }
    private void Rule_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not RuleResult row) return;
        ImpactText.Text = $"{row.Name} · {row.Rule.Impact}";
        PreviewNote.Text = $"{row.Rule.Description} 保留最近 {row.Rule.MinAgeDays} 天。可搜索全部 {row.Count:N0} 个结果；搜索只影响显示，清理仍处理勾选项目的全部结果。";
        RefreshPreviewMatches();
        if (row.Rule.Paths.Any(p => p.Contains("\\User Data\\", StringComparison.OrdinalIgnoreCase)))
            PreviewNote.Text += $" 浏览器已发现 {row.Rule.Paths.Length} 个缓存根目录（含编号配置）。";
        if (row.Rule.AnalysisOnly) PreviewNote.Text = $"仅统计超过 {row.Rule.MinAgeDays} 天且无多硬链接的文件，不能代表整个缓存。请在概览页使用 uv 官方清理；该入口处理整个缓存，不按保留天数过滤。";
        else if (row.Rule.ReviewRequired) PreviewNote.Text += " 专项不会被批量勾选，请阅读影响后单独勾选。";
    }
    private async void UvClean_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !validSettings) return;
        try
        {
            string? executable = UvMaintenance.FindExecutable();
            if (executable is null) { StatusText.Text = "未找到 uv.exe，请安装 uv 或将它所在目录加入 PATH 后重启。"; return; }
            UvMaintenance.Validate(preferences.Exclusions);
            if (MessageBox.Show(this, $"将通过 uv 官方命令清理整个缓存：\n{UvMaintenance.CacheRoot}\n\n执行程序：{executable}\n命令：uv cache clean --cache-dir \"{UvMaintenance.CacheRoot}\"\n\n这会处理整个缓存，不受保留天数限制，也不局限于文件预览。下次安装依赖可能需要重新下载或构建。\n官方工具只记录命令及结果，没有逐文件删除清单。\n\n确定执行？", "CleanC · uv 官方缓存清理", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await RunJob(async token =>
            {
                try
                {
                    StatusText.Text = "正在通过 uv 官方工具清理缓存…";
                    var result = await UvMaintenance.CleanAsync(executable, preferences.Exclusions.ToArray(), LogDirectory, token);
                    StatusText.Text = $"uv 官方清理 · {(result.Cancelled ? "已停止，可能部分完成" : result.ExitCode == 0 ? "完成" : $"失败（退出码 {result.ExitCode}）")} · 可用空间变化 {SignedBytes(result.FreeSpaceDelta)}；详情见清理记录。";
                }
                finally { InvalidatePreview(); RefreshHistory(); }
            });
        }
        catch (Exception ex) { StatusText.Text = "uv 清理未开始：" + ex.Message; }
    }
    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStart() || !validRules) return;
        var selected = results.Where(r => r.Selected && r.Count > 0).ToList();
        if (selected.Count == 0) return;
        string impact = string.Join("\n", selected.Select(r => $"• {r.Name}：{r.Rule.Impact}"));
        var answer = MessageBox.Show(this,
            $"将永久删除 {selected.Sum(r => r.Count):N0} 个缓存文件，文件总量 {Display.Bytes(selected.Sum(r => r.Size))}。\n\n{impact}\n\n不会进入回收站，无法在本工具内撤销。实际释放空间可能小于文件总量。\n\n确定清理这些项目吗？",
            "CleanC · 确认清理范围", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        await RunJob(async token =>
        {
            try
            {
                var progress = Progress(); var excluded = preferences.Exclusions.ToArray();
                var summary = await Task.Run(() => new Cleaner(policy).Clean(selected, excluded, LogDirectory, progress, token));
                StatusText.Text = $"{(summary.Cancelled ? "已停止" : "清理完成")} · 已删 {summary.Deleted:N0} / 跳过 {summary.Skipped:N0} · 文件总量 {Display.Bytes(summary.LogicalBytes)} · 可用空间变化 {SignedBytes(summary.FreeSpaceDelta)}";
            }
            finally { InvalidatePreview(); RefreshHistory(); }
        });
    }
    private static string SignedBytes(long bytes) => (bytes < 0 ? "−" : "+") + Display.Bytes(Math.Abs(bytes));

    private async Task Analyze(string root)
    {
        if (!CanStart()) return;
        if (!validSettings) { StatusText.Text = "排除设置未能加载，请修复 Data/settings.json 后重启。"; return; }
        ShowPage(2);
        await RunJob(async token =>
        {
            var progress = Progress(); var excluded = preferences.Exclusions.ToArray();
            var scanned = await Task.Run(() => Scanner.Analyze(root, excluded, progress, token, scanControl));
            analysis = scanned;
            FoldersGrid.ItemsSource = scanned.Folders; LargeFilesGrid.ItemsSource = scanned.LargestFiles;
            DirectoryMap.Items = scanned.Folders; RefreshAnalysisView();
            AnalysisTotal.Text = Display.Bytes(scanned.Size);
            AnalysisDetail.Text = $"{root} · {scanned.FileCount:N0} 个文件 · {Display.Bytes(scanned.Size)} · 跳过 {scanned.Skipped:N0} 项 · {(scanned.Cancelled ? "已停止，显示部分结果" : "扫描结束，无法访问的目录未计入")}。显示逻辑大小，硬链接可能重复计数，不能视为可释放空间。";
            StatusText.Text = scanned.Cancelled ? "空间分析已停止，保留已读结果。" : "空间分析完成；双击目录继续深入，双击大文件定位。";
        }, pauseable: true);
    }
    private async void AnalyzeC_Click(object sender, RoutedEventArgs e) => await Analyze(@"C:\");
    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStart()) return;
        var dialog = new OpenFolderDialog { Title = "选择要分析的目录（只读扫描）" };
        if (dialog.ShowDialog(this) == true) await Analyze(dialog.FolderName);
    }
    private async void Parent_Click(object sender, RoutedEventArgs e)
    {
        if (analysis is not null && Path.GetDirectoryName(analysis.Root) is { } parent) await Analyze(parent);
    }
    private async void Folder_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FoldersGrid.SelectedItem is UsageEntry item && analysis is not null)
        {
            if (item.Path.Equals(analysis.Root, StringComparison.OrdinalIgnoreCase))
            { StatusText.Text = "这一行是当前目录直接包含的文件，不包含子目录。"; return; }
            await Analyze(item.Path);
        }
    }
    private void OpenPath(string path, bool select = false)
    {
        try
        {
            if (select)
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.Arguments = "/select,\"" + path + "\""; Process.Start(start);
            }
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) { StatusText.Text = "无法打开：" + e.Message; }
    }
    private void Preview_Open(object sender, MouseButtonEventArgs e) { if (PreviewGrid.SelectedItem is FileCandidate file) OpenPath(file.Path, true); }
    private void LargeFile_Open(object sender, MouseButtonEventArgs e) { if (AnalysisView.SelectedIndex == 0 && LargeFilesGrid.SelectedItem is UsageEntry file) OpenPath(file.Path, true); }
    private void Storage_Click(object sender, RoutedEventArgs e) => OpenPath("ms-settings:storagesense");
    private void DiskCleanup_Click(object sender, RoutedEventArgs e) => OpenPath("cleanmgr.exe");
    private void EditRules_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true }; start.ArgumentList.Add(RulesPath);
        try { Process.Start(start); } catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void ReloadRules_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try { LoadRules(); } catch (Exception ex) { StatusText.Text = "规则验证失败：" + ex.Message; }
    }
    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenPath(dataDirectory);
    private void RefreshExclusions() => ExclusionsList.ItemsSource = preferences.Exclusions.ToArray();
    private void AddExclusion(string path)
    {
        path = PathPolicy.Normalize(path);
        if (!preferences.Exclusions.Contains(path, StringComparer.OrdinalIgnoreCase)) preferences.Exclusions.Add(path);
        JsonFiles.Write(SettingsPath, preferences); RefreshExclusions(); InvalidatePreview();
        analysis = null; FoldersGrid.ItemsSource = null; LargeFilesGrid.ItemsSource = null; AnalysisTotal.Text = "未分析";
        DirectoryMap.Items = null; InvalidateDuplicates(); InvalidateInstaller();
        AnalysisDetail.Text = "排除项已变化，请重新分析。";
        StatusText.Text = "已更新排除列表，请重新扫描。";
    }
    private void AddExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFolderDialog { Title = "选择要排除的目录" };
        if (dialog.ShowDialog(this) == true) AddExclusion(dialog.FolderName);
    }
    private void ExcludeFile_Click(object sender, RoutedEventArgs e)
    {
        if (!busy && PreviewGrid.SelectedItem is FileCandidate file) AddExclusion(file.Path);
    }
    private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (busy || ExclusionsList.SelectedItem is not string path) return;
        preferences.Exclusions.Remove(path); JsonFiles.Write(SettingsPath, preferences);
        RefreshExclusions(); InvalidatePreview(); analysis = null; FoldersGrid.ItemsSource = null; LargeFilesGrid.ItemsSource = null;
        DirectoryMap.Items = null; InvalidateDuplicates(); InvalidateInstaller();
        AnalysisTotal.Text = "未分析"; AnalysisDetail.Text = "排除项已变化，请重新分析。";
    }
    private sealed record HistoryRow(string Time, string Status, string Counts, string Bytes, string Delta, string Path);
    private void RefreshHistory()
    {
        var rows = new List<HistoryRow>();
        if (Directory.Exists(LogDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(LogDirectory, "*.jsonl").OrderDescending().Take(100))
            {
                try
                {
                    string? last = HistoryTail.ReadLastLine(path);
                    using var json = JsonDocument.Parse(last ?? "{}"); var r = json.RootElement;
                    if (r.TryGetProperty("kind", out var kind) && kind.GetString() == "summary")
                        rows.Add(new(r.GetProperty("time").GetDateTimeOffset().LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            r.GetProperty("Cancelled").GetBoolean() ? "已停止" : "完成",
                            $"{r.GetProperty("Deleted")} / {r.GetProperty("Skipped")}", Display.Bytes(r.GetProperty("LogicalBytes").GetInt64()),
                            SignedBytes(r.GetProperty("FreeSpaceDelta").GetInt64()), path));
                    else if (kind.ValueKind == JsonValueKind.String && kind.GetString() == "managed_result")
                        rows.Add(new(r.GetProperty("time").GetDateTimeOffset().LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            r.GetProperty("cancelled").GetBoolean() ? "uv · 已停止" : r.GetProperty("exitCode").GetInt32() == 0 ? "uv · 完成" : "uv · 失败",
                            "官方工具", "—", SignedBytes(r.GetProperty("delta").GetInt64()), path));
                    else rows.Add(new(File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss"), "未完成 · 请查看日志", "—", "—", "—", path));
                }
                catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
                { rows.Add(new("—", "记录不完整", "—", "—", "—", path)); }
            }
        }
        HistoryGrid.ItemsSource = rows;
    }
    private void RefreshHistory_Click(object sender, RoutedEventArgs e) { if (!busy) RefreshHistory(); }
    private void OpenHistory_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(LogDirectory); OpenPath(LogDirectory); }
    private void History_Open(object sender, MouseButtonEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not HistoryRow row) return;
        var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true }; start.ArgumentList.Add(row.Path);
        try { Process.Start(start); } catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (busy || analysis is null) return;
        var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = "CleanC-空间分析.csv" };
        if (dialog.ShowDialog(this) != true) return;
        static string Cell(string value)
        {
            if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
        writer.WriteLine("类型,路径,逻辑字节,文件数,扫描状态");
        string state = analysis.Cancelled || analysis.Skipped > 0 ? "部分结果" : "扫描结束";
        foreach (var item in analysis.Folders) writer.WriteLine($"目录,{Cell(item.Path)},{item.Size},{item.FileCount},{state}");
        foreach (var item in analysis.LargestFiles) writer.WriteLine($"大文件,{Cell(item.Path)},{item.Size},{item.FileCount},{state}");
        foreach (var item in analysis.FileTypes) writer.WriteLine($"文件类型,{Cell(item.Path)},{item.Size},{item.FileCount},{state}");
        StatusText.Text = "分析结果已导出：" + dialog.FileName;
    }
}
