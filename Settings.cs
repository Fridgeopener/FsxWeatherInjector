using System;
using System.Collections.Generic;
using System.IO;

namespace FsxWeatherInjector
{
    /// <summary>Tiny key=value settings file in %AppData%.</summary>
    public class Settings
    {
        public bool Enabled = true;
        public bool SyncTime = true;
        public int IntervalMin = 10;
        public int Stations = 6;
        public int RadiusNm = 500;

        static string PathFile
        {
            get
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FsxWeatherInjector");
                Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, "settings.ini");
            }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                foreach (var line in File.ReadAllLines(PathFile))
                {
                    var kv = line.Split(new[] { '=' }, 2);
                    if (kv.Length != 2) continue;
                    int n;
                    switch (kv[0].Trim())
                    {
                        case "Enabled": s.Enabled = kv[1].Trim() == "1"; break;
                        case "SyncTime": s.SyncTime = kv[1].Trim() == "1"; break;
                        case "IntervalMin": if (int.TryParse(kv[1], out n)) s.IntervalMin = Clamp(n, 1, 120); break;
                        case "Stations": if (int.TryParse(kv[1], out n)) s.Stations = Clamp(n, 1, 12); break;
                        case "RadiusNm": if (int.TryParse(kv[1], out n)) s.RadiusNm = Clamp(n, 25, 1000); break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(PathFile, new[]
                {
                    "Enabled=" + (Enabled ? "1" : "0"),
                    "SyncTime=" + (SyncTime ? "1" : "0"),
                    "IntervalMin=" + IntervalMin,
                    "Stations=" + Stations,
                    "RadiusNm=" + RadiusNm
                });
            }
            catch { }
        }

        static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }
    }
}
