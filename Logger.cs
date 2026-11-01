using System;
using System.IO;

namespace FsxWeatherInjector
{
    /// <summary>
    /// Simple thread-safe file logger. Writes %AppData%\FsxWeatherInjector\log.txt
    /// (rotated to log.old.txt at 2 MB). Events are infrequent, so every line is flushed immediately.
    /// </summary>
    public static class Logger
    {
        static readonly object gate = new object();
        const long MaxBytes = 2 * 1024 * 1024;

        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FsxWeatherInjector");
        public static readonly string FilePath = Path.Combine(Dir, "log.txt");

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }

        public static void Error(string msg, Exception ex = null)
        {
            Write("ERROR", ex == null ? msg : msg + " :: " + ex.GetType().Name + ": " + ex.Message +
                Environment.NewLine + ex.StackTrace);
        }

        public static void Header()
        {
            Write("INFO ", "==================== " + MainForm.AppName + " v" + MainForm.AppVersion + " started ====================");
            Write("INFO ", "OS: " + Environment.OSVersion + " | CLR: " + Environment.Version +
                " | 64-bit process: " + Environment.Is64BitProcess + " | exe: " + System.Reflection.Assembly.GetExecutingAssembly().Location);
        }

        static void Write(string level, string msg)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        File.Copy(FilePath, Path.Combine(Dir, "log.old.txt"), true);
                        File.Delete(FilePath);
                    }
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + msg + Environment.NewLine);
                }
            }
            catch { /* logging must never crash the app */ }
        }
    }
}
