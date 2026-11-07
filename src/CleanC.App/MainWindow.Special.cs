using System.Windows;
using System.Windows.Controls;
using CleanC.Core;

namespace CleanC.App;
public partial class MainWindow
{
    private List<InstallerPlan> installerPlans = [];
    private string? installerBlockReason = "请先扫描";
    private async void InstallerScan_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !validSettings) return;
        InstallerConfirmed.IsChecked = false; installerPlans = []; installerBlockReason = "正在核验"; InstallerGrid.ItemsSource = installerPlans; InstallerFiles.ItemsSource = null;
        await RunJob(async token =>
        {
            var progress = Progress(); var excluded = preferences.Exclusions.ToArray();
            var plans = await Task.Run(() => new InstallerCleanup().Scan(excluded, progress, token, scanControl));
            installerPlans = plans;
            installerBlockReason = InstallerCleanup.GetBlockReason();
            foreach (var plan in plans) plan.PropertyChanged += (_, _) => UpdateInstallerSelection();
            InstallerGrid.ItemsSource = plans;
            InstallerStatus.Text = $"发现 {plans.Count} 个目录 · 候选文件 {Display.Bytes(plans.Sum(x => x.Size))}。" +
                (installerBlockReason ?? "未检测到安装进程或待重启标记；仍需自行确认安装已完成。");
            StatusText.Text = "安装包专项扫描完成；匹配目录特征不等于证明文件无用。";
        }, pauseable: true);
    }
    private void UpdateInstallerSelection()
    {
        if (InstallerCleanButton is not null) InstallerCleanButton.IsEnabled = !busy && installerBlockReason is null && InstallerConfirmed.IsChecked == true && installerPlans.Any(p => p.Selected && p.Count > 0);
    }
    private void InvalidateInstaller()
    {
        installerPlans = []; installerBlockReason = "请重新扫描"; InstallerGrid.ItemsSource = null; InstallerFiles.ItemsSource = null;
        InstallerConfirmed.IsChecked = false; InstallerStatus.Text = "范围或排除项已变化，请重新扫描。"; UpdateInstallerSelection();
    }
    private void InstallerCheck_Changed(object sender, RoutedEventArgs e) => UpdateInstallerSelection();
    private void InstallerRow_Changed(object sender, SelectionChangedEventArgs e)
    {
        InstallerFiles.ItemsSource = (InstallerGrid.SelectedItem as InstallerPlan)?.Files.Take(200).ToList();
    }
    private void InstallerFile_Open(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (InstallerFiles.SelectedItem is FileCandidate file) OpenPath(file.Path, true);
    }
    private async void InstallerClean_Click(object sender, RoutedEventArgs e)
    {
        if (busy || InstallerConfirmed.IsChecked != true) return;
        var selected = installerPlans.Where(p => p.Selected && p.Count > 0).ToList(); if (selected.Count == 0) return;
        string scope = string.Join("\n", selected.Select(p => $"{p.Root} · {p.SizeText} · {p.Count:N0} 个文件"));
        if (MessageBox.Show(this, $"将永久删除以下目录中已预览的 VS 安装临时包文件：\n\n{scope}\n\n专项不受 7 天保留期限制。\n你已确认安装结束、无需保留下载包；以后修改或修复 VS 可能需重新下载。\n下方预览每目录显示前 200 个文件，本操作处理勾选目录的全部候选。\n不进入回收站，无法撤销。确定继续？", "CleanC · 安装包专项确认", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunJob(async token =>
        {
            try
            {
                var progress = Progress(); var excluded = preferences.Exclusions.ToArray();
                var summary = await Task.Run(() => new InstallerCleanup().Clean(selected, excluded, true, LogDirectory, progress, token));
                StatusText.Text = $"安装包专项{(summary.Cancelled ? "已停止" : "完成")} · 已删 {summary.Deleted:N0} / 跳过 {summary.Skipped:N0} · 可用空间变化 {SignedBytes(summary.FreeSpaceDelta)}";
            }
            finally
            {
                installerPlans = []; InstallerGrid.ItemsSource = null; InstallerFiles.ItemsSource = null; InstallerConfirmed.IsChecked = false;
                InstallerStatus.Text = "文件状态已变化，请重新扫描。"; InvalidatePreview(); RefreshHistory();
            }
        });
    }
}
