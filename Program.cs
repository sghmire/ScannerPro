namespace ScannerPro;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowStartupSafeError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                ShowStartupSafeError(ex);
            }
        };

        Application.Run(new Form1());
    }

    private static void ShowStartupSafeError(Exception ex)
    {
        try
        {
            MessageBox.Show(ex.Message, "ScannerPro Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Avoid recursive failure during shutdown/startup edge cases.
        }
    }
}
