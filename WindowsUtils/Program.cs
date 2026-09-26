namespace WindowsUtils;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        // Follow the Windows light/dark setting; Theme picks matching colors.
        Application.SetColorMode(SystemColorMode.System);
        Theme.Initialize();
        Application.Run(new MainForm());
    }    
}