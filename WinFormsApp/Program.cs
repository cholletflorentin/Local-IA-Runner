using System;
using System.IO;
using System.Windows.Forms;

namespace LocalAIApp
{
    internal static class Program
    {
        private static readonly object CrashLogLock = new();
        private static readonly string CrashLogPath = Path.Combine(Path.GetTempPath(), "LocalAIApp_Crash.log");

        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, args) =>
            {
                LogException("Interface WinForms", args.Exception);
                MessageBox.Show(
                    $"Une erreur inattendue est survenue. Les détails sont enregistrés dans :\n{CrashLogPath}\n\n{args.Exception.Message}",
                    "Local IA Runner",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                var exception = args.ExceptionObject as Exception
                    ?? new Exception(args.ExceptionObject?.ToString() ?? "Exception inconnue");
                LogException("Exception non gérée (terminating=" + args.IsTerminating + ")", exception);
            };

            Application.Run(new MainForm());
        }

        private static void LogException(string source, Exception exception)
        {
            try
            {
                lock (CrashLogLock)
                {
                    File.AppendAllText(
                        CrashLogPath,
                        $"[{DateTime.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must not cause a second exception while handling a crash.
            }
        }
    }
}
