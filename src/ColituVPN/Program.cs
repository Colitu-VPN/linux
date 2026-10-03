using System.Runtime.InteropServices;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Manager;
using v2rayN.Desktop.Services;

namespace v2rayN.Desktop;

internal class Program
{
    /// <summary>Guard of the single running instance; a second launch asks it to show the window.</summary>
    public static ColituSingleInstance? Instance { get; private set; }

    private static readonly List<PosixSignalRegistration> Signals = [];

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (OnStartup(args) == false)
        {
            Environment.Exit(0);
            return;
        }

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            Instance?.Dispose();
        }
    }

    private static bool OnStartup(string[]? args)
    {
        Instance = ColituSingleInstance.Acquire();
        if (Instance == null)
        {
            return false;
        }

        // Same rule as AppManager.InitApp: a read-only install keeps its data in ~/.local/share/ColituVPN.
        if (Utils.HasWritePermission() == false)
        {
            Environment.SetEnvironmentVariable(Global.LocalAppData, "1", EnvironmentVariableTarget.Process);
        }
        // Before logging starts: logs older than a week are removed.
        ColituHardening.CleanLogs();

        if (!AppManager.Instance.InitApp())
        {
            return false;
        }

        ColituHardening.HardenDataFolders();
        AppManager.Instance.WindowDialog = new WindowDialog();

        // Logout or shutdown while connected: hand the system proxy back before the session ends.
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP })
        {
            Signals.Add(PosixSignalRegistration.Create(signal, _ => ColituVpnService.Instance.CleanupForSessionEnd()));
        }
        return true;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
           .UsePlatformDetect()
           .WithFontByDefault()
#if DEBUG
           .WithDeveloperTools()
#endif
           .LogToTrace()
           .UseReactiveUI(_ => { });
    }
}
