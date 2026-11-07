using System;
using System.Collections.Generic;
using System.IO;

namespace FsxWeatherInjector
{
    /// <summary>Tiny key=value settings file in %AppData%.</summary>
    public class Settings
    {
        public const int MaxStations = 30;

        public bool Enabled = true;
        public bool SyncTime = true;
        public int IntervalMin = 10;
        public int Stations = 12;
        public int RadiusNm = 500;

        // 1. METAR source
        public string MetarSource = "noaa";       // noaa | vatsim | custom
        public string CustomUrl = "";             // contains {icao} or {icaos}
        public bool FallbackToNoaa = true;

        // 2. Appearance
        public string ThemeMode = "light";        // light | dark | system

        // 3. Station selection
        public bool MergeStations = true;
        public int MergeNm = 40;

        public string LookupIcaos = "";           // last airports typed in the lookup dialog

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

        public SourceKind GetSourceKind()
        {
            switch ((MetarSource ?? "").ToLowerInvariant())
            {
                case "vatsim": return SourceKind.Vatsim;
                case "custom": return SourceKind.Custom;
                default: return SourceKind.Noaa;
            }
        }

        public void SetSourceKind(SourceKind k)
        {
            MetarSource = k == SourceKind.Vatsim ? "vatsim" : k == SourceKind.Custom ? "custom" : "noaa";
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
                    string v = kv[1].Trim();
                    int n;
                    switch (kv[0].Trim())
                    {
                        case "Enabled": s.Enabled = v == "1"; break;
                        case "SyncTime": s.SyncTime = v == "1"; break;
                        case "IntervalMin": if (int.TryParse(v, out n)) s.IntervalMin = Clamp(n, 1, 120); break;
                        case "Stations": if (int.TryParse(v, out n)) s.Stations = Clamp(n, 1, MaxStations); break;
                        case "RadiusNm": if (int.TryParse(v, out n)) s.RadiusNm = Clamp(n, 25, 1000); break;
                        case "MetarSource": s.MetarSource = v.ToLowerInvariant(); break;
                        case "CustomUrl": s.CustomUrl = v; break;
                        case "FallbackToNoaa": s.FallbackToNoaa = v == "1"; break;
                        case "ThemeMode": s.ThemeMode = v.ToLowerInvariant(); break;
                        case "MergeStations": s.MergeStations = v == "1"; break;
                        case "MergeNm": if (int.TryParse(v, out n)) s.MergeNm = Clamp(n, 5, 150); break;
                        case "LookupIcaos": s.LookupIcaos = v; break;
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
                    "RadiusNm=" + RadiusNm,
                    "MetarSource=" + MetarSource,
                    "CustomUrl=" + (CustomUrl ?? "").Replace("\r", "").Replace("\n", ""),
                    "FallbackToNoaa=" + (FallbackToNoaa ? "1" : "0"),
                    "ThemeMode=" + ThemeMode,
                    "MergeStations=" + (MergeStations ? "1" : "0"),
                    "MergeNm=" + MergeNm,
                    "LookupIcaos=" + (LookupIcaos ?? "").Replace("\r", "").Replace("\n", " ")
                });
            }
            catch { }
        }

        static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }
    }
}
