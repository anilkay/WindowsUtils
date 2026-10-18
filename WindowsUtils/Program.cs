using Microsoft.Extensions.Logging;
using WindowsUtils.Core.Logging;

namespace WindowsUtils;

static class Program
{
    private static ILogger Log => AppLog.Logger("WindowsUtils");

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        ConfigureLogging();
        // Follow the Windows light/dark setting; Theme picks matching colors.
        Application.SetColorMode(SystemColorMode.System);
        Theme.Initialize();

        // Unhandled exceptions: log them, then let the default handler show the dialog.
        Application.ThreadException += (_, e) =>
            Log.LogError(e.Exception, "Unhandled UI exception.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.LogError(e.ExceptionObject as Exception, "Unhandled AppDomain exception.");
        Application.ApplicationExit += (_, _) => Log.LogInformation("Application exiting.");

        Log.LogInformation("WindowsUtils started (runtime {Runtime}, log folder {LogDir}).",
            Environment.Version, AppLog.LogDirectory);

        try
        {
            Application.Run(new MainForm());
        }
        finally
        {
            AppLog.Factory.Dispose();
        }
    }

    private static void ConfigureLogging()
    {
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(AppLog.LogDirectory));
#if DEBUG
            builder.AddDebug();
#endif
        });
        AppLog.Configure(factory);
    }
}
