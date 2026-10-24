using TvEmulator.Services;

namespace TvEmulator;

public partial class App : System.Windows.Application
{
    public App()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Taskbar.Restore();
        DispatcherUnhandledException += (_, _) => Taskbar.Restore();
    }
}
