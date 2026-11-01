using System;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace FsxWeatherInjector
{
    static class Program
    {
        // Test switch: FsxWeatherInjector.exe --simulate-simconnect-error=managed|native|bitness|unknown|ok
        const string SimulateSwitch = "--simulate-simconnect-error=";

        [STAThread]
        static void Main(string[] args)
        {
            bool createdNew;
            using (new Mutex(true, "FsxWeatherInjector.SingleInstance", out createdNew))
            {
                if (!createdNew) return;

                Logger.Header();

                // Log anything that would otherwise vanish silently.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) =>
                {
                    Logger.Error("Unhandled UI-thread exception", e.Exception);
                    MessageBox.Show("Unexpected error (details written to the log):\n" + e.Exception.Message +
                        "\n\n" + Logger.FilePath, MainForm.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    Logger.Error("Unhandled exception (terminating=" + e.IsTerminating + ")", e.ExceptionObject as Exception);

                // .NET 4.x on older Windows may not enable TLS 1.2 by default.
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Make sure SimConnect can actually be loaded before anything else touches it.
                string simulate = null;
                foreach (var a in args)
                    if (a.StartsWith(SimulateSwitch, StringComparison.OrdinalIgnoreCase))
                        simulate = a.Substring(SimulateSwitch.Length);

                var check = SimConnectCheck.Run(simulate);
                Logger.Info(SimConnectCheck.Summary(check));
                if (check.IsFatal)
                {
                    Logger.Error("Startup stopped: SimConnect is not usable (" + check.Status + ")");
                    SimConnectCheck.ShowProblem(check);
                    return;
                }

                Application.Run(new MainForm());

                Logger.Info("Application exited normally");
            }
        }
    }
}
