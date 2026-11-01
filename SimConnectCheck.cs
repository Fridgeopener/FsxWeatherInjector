using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.FlightSimulator.SimConnect;
using Microsoft.Win32;

namespace FsxWeatherInjector
{
    public enum SimConnectStatus
    {
        Ok,
        ManagedMissing,     // Microsoft.FlightSimulator.SimConnect.dll missing or a different version
        NativeMissing,      // the SimConnect runtime FSX installs (side-by-side) is missing / doesn't match
        WrongArchitecture,  // 32-bit / 64-bit mismatch
        Unknown             // something unexpected; not treated as fatal
    }

    public class SimConnectCheckResult
    {
        public SimConnectStatus Status;
        public string Detail;
        public string ManagedVersion;
        public string ManagedPath;
        public string FsxFolder;
        public string FsxNativeVersion;
        public bool Simulated;

        public bool IsFatal
        {
            get
            {
                return Status == SimConnectStatus.ManagedMissing ||
                       Status == SimConnectStatus.NativeMissing ||
                       Status == SimConnectStatus.WrongArchitecture;
            }
        }
    }

    /// <summary>
    /// Verifies that SimConnect can actually be loaded before the app tries to use it, and explains
    /// what to do if not. IMPORTANT: Run() must never mention SimConnect types itself. Only Probe() does,
    /// so that a missing DLL surfaces as an exception we can catch here instead of crashing at startup.
    /// </summary>
    public static class SimConnectCheck
    {
        public static SimConnectCheckResult Run(string simulate)
        {
            var r = new SimConnectCheckResult();
            r.FsxFolder = FindFsxFolder();
            r.FsxNativeVersion = FsxNativeVersion(r.FsxFolder);

            if (!string.IsNullOrEmpty(simulate))
            {
                Simulate(r, simulate);
                return r;
            }

            try
            {
                Probe(r);
                r.Status = SimConnectStatus.Ok;
                r.Detail = "SimConnect loaded and a connection could be created (FSX appears to be running).";
            }
            catch (Exception ex)
            {
                Classify(r, Unwrap(ex));
            }
            return r;
        }

        // Only place that touches SimConnect types. Not inlined, so the assembly is bound when this
        // method is first called (inside Run's try block).
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Probe(SimConnectCheckResult r)
        {
            Assembly asm = typeof(SimConnect).Assembly;
            r.ManagedPath = asm.Location;
            try { r.ManagedVersion = FileVersionInfo.GetVersionInfo(asm.Location).FileVersion; }
            catch { r.ManagedVersion = asm.GetName().Version.ToString(); }

            // Creating a connection forces the native library to load too. If FSX is not running this
            // throws a COMException, which still proves both libraries loaded fine.
            var sc = new SimConnect("FSX Weather Injector check", IntPtr.Zero, 0, null, 0);
            sc.Dispose();
        }

        static Exception Unwrap(Exception ex)
        {
            while ((ex is TypeInitializationException || ex is TargetInvocationException) && ex.InnerException != null)
                ex = ex.InnerException;
            return ex;
        }

        static void Classify(SimConnectCheckResult r, Exception ex)
        {
            string detail = ex.GetType().Name + ": " + ex.Message + " (HRESULT 0x" + ex.HResult.ToString("X8") + ")";

            if (ex is COMException)
            {
                r.Status = SimConnectStatus.Ok;
                r.Detail = "SimConnect libraries loaded fine. FSX is not running right now (connection refused, HRESULT 0x" +
                           ex.HResult.ToString("X8") + ").";
            }
            else if (ex is BadImageFormatException)
            {
                r.Status = SimConnectStatus.WrongArchitecture;
                r.Detail = detail;
            }
            else if (ex is FileNotFoundException)
            {
                string name = ((FileNotFoundException)ex).FileName ?? "";
                bool managed = name.StartsWith("Microsoft.FlightSimulator.SimConnect", StringComparison.OrdinalIgnoreCase);
                r.Status = managed ? SimConnectStatus.ManagedMissing : SimConnectStatus.NativeMissing;
                r.Detail = detail;
            }
            else if (ex is FileLoadException)
            {
                // 0x80131040 = the DLL found is a different version than the program expects.
                // Anything else here is typically a missing side-by-side native dependency (0x800736B3).
                r.Status = ex.HResult == unchecked((int)0x80131040) ? SimConnectStatus.ManagedMissing : SimConnectStatus.NativeMissing;
                r.Detail = detail;
            }
            else if (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                r.Status = SimConnectStatus.NativeMissing;
                r.Detail = detail;
            }
            else
            {
                r.Status = SimConnectStatus.Unknown;
                r.Detail = detail;
            }
        }

        // ---- test hook ---------------------------------------------------------------

        static void Simulate(SimConnectCheckResult r, string kind)
        {
            r.Simulated = true;
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "managed": r.Status = SimConnectStatus.ManagedMissing; r.Detail = "(simulated) FileNotFoundException: Microsoft.FlightSimulator.SimConnect"; break;
                case "native": r.Status = SimConnectStatus.NativeMissing; r.Detail = "(simulated) FileLoadException: referenced assembly is not installed (HRESULT 0x800736B3)"; break;
                case "bitness": r.Status = SimConnectStatus.WrongArchitecture; r.Detail = "(simulated) BadImageFormatException"; break;
                case "unknown": r.Status = SimConnectStatus.Unknown; r.Detail = "(simulated) unexpected exception"; break;
                case "ok": r.Status = SimConnectStatus.Ok; r.Detail = "(simulated) everything fine"; break;
                default:
                    r.Status = SimConnectStatus.Unknown;
                    r.Detail = "(simulated) unknown simulation '" + kind + "'. Use: managed, native, bitness, unknown or ok";
                    break;
            }
        }

        // ---- FSX discovery (best effort, for log + help text) ------------------------

        static string FindFsxFolder()
        {
            string[] keys =
            {
                @"SOFTWARE\Microsoft\Microsoft Games\Flight Simulator\10.0",
                @"SOFTWARE\Microsoft\Microsoft Games\Flight Simulator - Steam Edition\10.0"
            };
            foreach (var k in keys)
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(k))
                    {
                        var v = key == null ? null : key.GetValue("SetupPath") as string;
                        if (!string.IsNullOrEmpty(v) && Directory.Exists(v)) return v;
                    }
                }
                catch { }
            }
            return null;
        }

        static string FsxNativeVersion(string folder)
        {
            if (folder == null) return null;
            try
            {
                string p = Path.Combine(folder, "SimConnect.dll");
                return File.Exists(p) ? FileVersionInfo.GetVersionInfo(p).FileVersion : null;
            }
            catch { return null; }
        }

        // ---- text --------------------------------------------------------------------

        public static string Summary(SimConnectCheckResult r)
        {
            return string.Format("SimConnect check: {0}{1} | managed DLL {2} at {3} | FSX folder {4} | FSX SimConnect.dll {5} | {6}",
                r.Status, r.Simulated ? " (SIMULATED)" : "",
                r.ManagedVersion ?? "n/a", r.ManagedPath ?? "n/a",
                r.FsxFolder ?? "not found in registry", r.FsxNativeVersion ?? "n/a", r.Detail);
        }

        static string Headline(SimConnectCheckResult r)
        {
            switch (r.Status)
            {
                case SimConnectStatus.ManagedMissing:
                    return "The SimConnect library file (Microsoft.FlightSimulator.SimConnect.dll) is missing, or it is a different version than this program was built with.";
                case SimConnectStatus.NativeMissing:
                    return "The SimConnect runtime that FSX installs is missing, or it does not match the SimConnect DLL used by this program.";
                case SimConnectStatus.WrongArchitecture:
                    return "SimConnect could not be loaded because of a 32-bit / 64-bit mismatch.";
                default:
                    return "SimConnect reported an unexpected problem while loading.";
            }
        }

        static string HowToFix(SimConnectCheckResult r)
        {
            string fsx = r.FsxFolder ?? "<your FSX folder>";
            string app = AppDomain.CurrentDomain.BaseDirectory;
            var sb = new StringBuilder("How to fix:\n");
            switch (r.Status)
            {
                case SimConnectStatus.ManagedMissing:
                    sb.AppendLine("1. Open the SimConnect SDK folder of the FSX you play:");
                    sb.AppendLine("     " + fsx + "\\SDK\\Core Utilities Kit\\SimConnect SDK\\lib\\managed");
                    sb.AppendLine("2. Copy Microsoft.FlightSimulator.SimConnect.dll into the same folder as this program:");
                    sb.AppendLine("     " + app);
                    sb.AppendLine("3. Start the program again.");
                    break;
                case SimConnectStatus.NativeMissing:
                    sb.AppendLine("1. FSX itself installs the SimConnect runtime, so make sure FSX is installed on this PC.");
                    sb.AppendLine("2. Otherwise run SimConnect.msi from:");
                    sb.AppendLine("     " + fsx + "\\SDK\\Core Utilities Kit\\SimConnect SDK\\lib");
                    sb.AppendLine("   (Steam Edition: if that does not help, try the one in ...\\SimConnect SDK\\LegacyInterfaces\\FSX-XPACK)");
                    sb.AppendLine("3. Start the program again.");
                    break;
                case SimConnectStatus.WrongArchitecture:
                    sb.AppendLine("FSX is a 32-bit program, so this tool must run as 32-bit (x86). Rebuild it with Platform target = x86.");
                    break;
                default:
                    sb.AppendLine("Check the log file for details and send it to the author.");
                    break;
            }
            return sb.ToString();
        }

        /// <summary>Full report, used by Help &gt; Check SimConnect Installation.</summary>
        public static string Report(SimConnectCheckResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Result: " + r.Status + (r.Simulated ? " (simulated)" : ""));
            sb.AppendLine();
            sb.AppendLine("Managed SimConnect DLL: " + (r.ManagedVersion ?? "not loaded"));
            sb.AppendLine("   " + (r.ManagedPath ?? ""));
            sb.AppendLine("FSX folder: " + (r.FsxFolder ?? "not found in registry"));
            sb.AppendLine("FSX SimConnect.dll version: " + (r.FsxNativeVersion ?? "n/a"));
            sb.AppendLine();
            sb.AppendLine(r.Detail);
            return sb.ToString();
        }

        /// <summary>Friendly error dialog for a fatal result.</summary>
        public static void ShowProblem(SimConnectCheckResult r, IWin32Window owner = null)
        {
            string text = Headline(r) + "\n\n" + HowToFix(r) + "\nTechnical details:\n" + r.Detail +
                          "\n\nLog file:\n" + Logger.FilePath;
            MessageBox.Show(owner, text, MainForm.AppName + " – SimConnect problem" + (r.Simulated ? " (SIMULATED)" : ""),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
