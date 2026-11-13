using System.Configuration;
using System.Data;
using System.Windows;

namespace Maison;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// --pool-host: run only the AI pool service, with no window (for testing several AI PCs on one machine; use
    /// MAISON_CONFIG_DIR and MAISON_POOL_PORT per instance). Stops when the process is ended.
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--pool-host"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            Services.StatsService.Start();   // GPU figures for the Pool page on other PCs
            try { await Services.Pool.PoolHost.StartAsync(); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Shutdown(1); }
            return;
        }
        base.OnStartup(e);
        var main = new MainWindow();
        main.Show();
        if (e.Args.Contains(Services.AutoStart.MinimizedArg)) main.WindowState = WindowState.Minimized;   // started with Windows
    }
}

