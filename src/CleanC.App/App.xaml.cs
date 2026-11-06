using System.Windows;

namespace CleanC.App;
public partial class App : Application
{
    private Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--verify-runtime"))
        {
            string directory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            string wpf = typeof(Window).Assembly.Location;
            bool bundled = CleanC.Core.PathPolicy.Within(System.IO.Path.GetFullPath(directory), AppContext.BaseDirectory) &&
                CleanC.Core.PathPolicy.Within(wpf, AppContext.BaseDirectory);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                bundled, runtime = Environment.Version.ToString(), directory, wpf,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
            }));
            Shutdown(bundled ? 0 : 2); return;
        }
        mutex = new Mutex(true, "Local\\CleanC.Desktop", out bool created);
        if (!created) { MessageBox.Show("CleanC 已在运行。", "CleanC"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "CleanC · 操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
