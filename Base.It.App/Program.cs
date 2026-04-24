using Avalonia;
using System;
using Velopack;

namespace Base.It.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack MUST be the first thing to run. When an installer or
        // updater invokes the exe with one of Velopack's internal switches
        // (--veloapp-install, --veloapp-update, etc.), this call performs
        // the hook and exits before Avalonia ever tries to initialize — so
        // the installer's lifecycle never collides with our UI.
        VelopackApp.Build().Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
