using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Documents;
using CleanC.App;
using CleanC.Core;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        // A hidden presentation source supplies Loaded/binding/virtualization semantics without showing a desktop window.
        window.Content = null;
        root.SetValue(TextElement.ForegroundProperty, (Brush)app.FindResource("Ink"));
        using var source = new HwndSource(new HwndSourceParameters("CleanC layout tests")
        { Width = 1320, Height = 840, WindowStyle = unchecked((int)0x80000000) });
        source.RootVisual = root;
        string output = Path.GetFullPath("artifacts/ui"); Directory.CreateDirectory(output);
        var errors = new List<string>();
        var pages = (TabControl)window.FindName("Pages");
        if (pages.Items.Count != 6) errors.Add("Expected 6 pages");
        if (((TextBlock)window.FindName("RuleCount")).Text != "11") errors.Add("Rules did not load");
        if (((Button)window.FindName("CleanButton")).IsEnabled) errors.Add("Cleaning enabled without preview");
        var sampleRule = new CleanupRule { Id = "sample", Name = "npm 下载缓存", Category = "开发缓存", MinAgeDays = 7,
            Description = "界面渲染测试样本，不是真实扫描结果。", Impact = "下次安装依赖可能需要联网。" };
        var sample = new RuleResult(sampleRule);
        sample.Notes.Add("保留最近 7 天");
        sample.Files.Add(new(@"C:\Example\npm-cache\_cacache\sample.bin", @"C:\Example", "sample", 12345678,
            DateTime.UtcNow.AddDays(-30).ToFileTimeUtc(), DateTime.UtcNow.AddDays(-30).ToFileTimeUtc(), 0, 0));
        for (int i = 0; i < 420; i++) sample.Files.Add(sample.Files[0] with { Path = $@"C:\Example\npm-cache\_cacache\package-{i:000}.bin", Size = 1000000 + i });
        ((DataGrid)window.FindName("RulesGrid")).ItemsSource = new[] { sample };
        ((DataGrid)window.FindName("RulesGrid")).SelectedItem = sample;
        UsageEntry[] mapEntries = [new(@"C:\Example", 987654321, 120) { Percent = 60 }, new(@"C:\Downloads", 400000000, 52) { Percent = 25 }, new(@"C:\Applications", 160000000, 120) { Percent = 10 }, new(@"C:\Temp", 80000000, 200) { Percent = 5 }];
        ((DataGrid)window.FindName("FoldersGrid")).ItemsSource = mapEntries;
        ((UsageMap)window.FindName("DirectoryMap")).Items = mapEntries;
        ((DataGrid)window.FindName("LargeFilesGrid")).ItemsSource = new[] { new UsageEntry(@"C:\Example\archive.zip", 123456789, 1) };
        ((DataGrid)window.FindName("DuplicateGroupsGrid")).ItemsSource = new[] { new DuplicateGroup(new string('A', 64), 12345678, sample.Files.Take(3).ToList()) };
        ((DataGrid)window.FindName("DuplicateGroupsGrid")).SelectedIndex = 0;
        string[] names = ["overview", "cleanup", "analysis", "rules", "history", "duplicates"];
        for (int p = 0; p < 6; p++)
        {
            typeof(MainWindow).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [p]);
            var nav = (IEnumerable<Button>)typeof(MainWindow).GetMethod("NavigationButtons", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [root])!;
            var active = nav.Single(b => (string)b.Tag == p.ToString());
            if (((SolidColorBrush)active.Background).Color != (Color)ColorConverter.ConvertFromString("#087F75")) errors.Add($"Page {p} has no active navigation marker");
            foreach (int width in new[] { 1320, 1060 })
            {
                int height = width == 1320 ? 840 : 680;
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                root.UpdateLayout();
                var selected = (TabItem)pages.SelectedItem;
                var content = (FrameworkElement)selected.Content;
                if (content.ActualWidth < 100 || content.ActualHeight < 100) errors.Add($"Page {p} has no content bounds");
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"{names[p]}-{width}.png")); encoder.Save(file);
                Console.WriteLine($"PASS rendered {names[p]} at {width} x {height}");
            }
        }
        // Validate actual navigation handlers and initial selection totals without invoking cleanup.
        if (((TextBlock)window.FindName("PageTitle")).Text != "重复文件") errors.Add("Navigation title mismatch");
        if (!((TextBlock)window.FindName("ImpactText")).Text.Contains("联网")) errors.Add("Preview selection did not display impact");
        var testAnalysis = new AnalysisResult(@"C:\Example", mapEntries.ToList(), [], 100, 2, 0, false)
        { FileTypes = [new(".bin", 100, 2)] };
        typeof(MainWindow).GetField("analysis", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, testAnalysis);
        ((ComboBox)window.FindName("AnalysisView")).SelectedIndex = 1;
        if (((DataGrid)window.FindName("LargeFilesGrid")).Items.Count != 1 ||
            ((UsageEntry)((DataGrid)window.FindName("LargeFilesGrid")).Items[0]).Path != ".bin") errors.Add("File-type view did not switch");
        if (((DataGrid)window.FindName("DuplicateFilesGrid")).Items.Count != 3) errors.Add("Duplicate group details did not load");
        if (((DataGrid)window.FindName("PreviewGrid")).Items.Count != 200) errors.Add("Preview paging did not bound rows");
        typeof(MainWindow).GetMethod("PreviewNext_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new Button(), new RoutedEventArgs()]);
        if (!((TextBlock)window.FindName("PreviewPageText")).Text.StartsWith("2 / 3")) errors.Add("Preview next page failed");
        ((TextBox)window.FindName("PreviewSearch")).Text = "package-419";
        typeof(MainWindow).GetMethod("RefreshPreviewMatches", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        if (((DataGrid)window.FindName("PreviewGrid")).Items.Count != 1) errors.Add("Search did not find a file beyond the first page");
        typeof(MainWindow).GetMethod("InvalidatePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        if (((DataGrid)window.FindName("PreviewGrid")).Items.Count != 0 || ((Button)window.FindName("CleanButton")).IsEnabled) errors.Add("Invalidation retained stale preview");
        window.Close(); app.Shutdown();
        foreach (string error in errors) Console.WriteLine("FAIL " + error);
        Console.WriteLine($"UI component checks: {errors.Count} failures. Renders: {output}");
        return errors.Count == 0 ? 0 : 1;
    }
}
