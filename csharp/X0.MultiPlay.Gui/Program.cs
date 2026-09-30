using System.Windows.Forms;

namespace X0.MultiPlay.Gui;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                ShowFatal(ex);
        };

        try
        {
            ApplicationConfiguration.Initialize();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new MainForm();
            form.Show();
            form.Activate();
            Application.Run(form);
        }
        catch (Exception ex)
        {
            ShowFatal(ex);
        }
    }

    static void ShowFatal(Exception ex)
    {
        CrashLog.Write(ex);

        try
        {
            MessageBox.Show(
                ex.ToString(),
                "X0 MultiPlay 시작 실패",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // ignore
        }
    }
}
