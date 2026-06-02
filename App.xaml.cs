using System.Windows;
using Voxie.Services;

namespace Voxie;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (UpdateService.IsPortableUpdateCommand(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await UpdateService.ApplyPortableUpdateFromCommandLineAsync(e.Args));
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
