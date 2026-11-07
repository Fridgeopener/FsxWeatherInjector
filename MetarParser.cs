using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FsxWeatherInjector
{
    public class CloudLayer
    {
        public string Cover;   // FEW, SCT, BKN, OVC or VV
        public int FeetAgl;
    }

    /// <summary>The few METAR fields we need, pulled out of the raw text.</summary>
    public class Wx
    {
        public bool HasWind;
        public int? WindDir;          // null = variable
        public int WindKt, GustKt;

        public bool HasVis;
        public double VisM;           // metres, 10000 = "10 km or more"
        public bool Cavok;

        public bool CloudsKnown;      // false when the report says nothing about clouds
        public List<CloudLayer> Layers = new List<CloudLayer>();

        public bool HasTemp, HasDew;
        public int TempC, DewC;

        public bool HasQnh;
        public double QnhHpa;

        public int Day = -1, Hour = -1, Minute = -1;   // observation time (UTC) from the ddhhmmZ group
        public HashSet<string> Phenomena = new HashSet<string>();   // RA, TS, FG ... (intensity removed)
    }

    /// <summary>
    /// Light-weight METAR reader. It does not try to understand every national variation; anything it
    /// cannot read is simply left unset, and callers treat "unknown" cautiously.
    /// </summary>
    public static class MetarParser
    {
        static readonly Regex TimeRx = new Regex(@"^(\d{2})(\d{2})(\d{2})Z$", RegexOptions.Compiled);
        static readonly Regex WindRx = new Regex(@"^(VRB|\d{3})(\d{2,3})(?:G(\d{2,3}))?(KT|MPS|KMH)$", RegexOptions.Compiled);
        static readonly Regex VisMetresRx = new Regex(@"^\d{4}$", RegexOptions.Compiled);
        static readonly Regex VisMilesRx = new Regex(@"^(?:P|M)?(\d{1,2})SM$", RegexOptions.Compiled);
        static readonly Regex VisFractionRx = new Regex(@"^M?(\d{1,2})/(\d{1,2})SM$", RegexOptions.Compiled);
        static readonly Regex CloudRx = new Regex(@"^(FEW|SCT|BKN|OVC|VV)(\d{3}|///)(?:CB|TCU|///)?$", RegexOptions.Compiled);
        static readonly Regex TempRx = new Regex(@"^(M?\d{2})/(M?\d{2})?$", RegexOptions.Compiled);
        static readonly Regex QnhRx = new Regex(@"^Q(\d{4})$", RegexOptions.Compiled);
        static readonly Regex AltimeterRx = new Regex(@"^A(\d{4})$", RegexOptions.Compiled);
        static readonly Regex WxRx = new Regex(
            @"^(?:\+|-)?(?:VC)?(?:MI|BC|PR|DR|BL|SH|TS|FZ)?(?:DZ|RA|SN|SG|IC|PL|GR|GS|UP|BR|FG|FU|VA|DU|SA|HZ|PY|PO|SQ|FC|SS|DS)+$",
            RegexOptions.Compiled);
        static readonly Regex TsRx = new Regex(@"^(?:\+|-)?(?:VC)?TS$", RegexOptions.Compiled);

        public static Wx Parse(string raw)
        {
            var w = new Wx();
            if (string.IsNullOrEmpty(raw)) return w;

            string[] t = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int wholeMiles = 0;

            // t[0] is the station identifier.
            for (int i = 1; i < t.Length; i++)
            {
                string s = t[i].ToUpperInvariant();
                if (s == "RMK" || s == "TEMPO" || s == "BECMG") break;   // trends and remarks are not "now"

                Match m;

                if (w.Day < 0 && (m = TimeRx.Match(s)).Success)
                {
                    w.Day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    w.Hour = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    w.Minute = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    continue;
                }

                if (!w.HasWind && (m = WindRx.Match(s)).Success)
                {
                    w.HasWind = true;
                    w.WindDir = m.Groups[1].Value == "VRB" ? (int?)null : int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double speed = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    double gust = m.Groups[3].Success ? double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                    string unit = m.Groups[4].Value;
                    double k = unit == "MPS" ? 1.943844 : unit == "KMH" ? 0.539957 : 1.0;
                    w.WindKt = (int)Math.Round(speed * k);
                    w.GustKt = (int)Math.Round(gust * k);
                    continue;
                }

                if (s == "CAVOK")
                {
                    w.Cavok = true; w.HasVis = true; w.VisM = 10000; w.CloudsKnown = true;
                    continue;
                }

                if (s == "SKC" || s == "CLR" || s == "NSC" || s == "NCD") { w.CloudsKnown = true; continue; }

                if (!w.HasVis && VisMetresRx.IsMatch(s))
                {
                    double v = double.Parse(s, CultureInfo.InvariantCulture);
                    w.HasVis = true; w.VisM = v >= 9999 ? 10000 : v;
                    continue;
                }

                if (!w.HasVis && s.Length == 1 && char.IsDigit(s[0])) { wholeMiles = s[0] - '0'; continue; }   // "1 1/2SM"

                if (!w.HasVis && (m = VisFractionRx.Match(s)).Success)
                {
                    double den = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (den > 0)
                    {
                        double miles = wholeMiles + double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / den;
                        w.HasVis = true; w.VisM = miles * 1609.34;
                    }
                    continue;
                }

                if (!w.HasVis && (m = VisMilesRx.Match(s)).Success)
                {
                    double miles = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    w.HasVis = true; w.VisM = Math.Min(10000, miles * 1609.34);
                    continue;
                }

                if ((m = CloudRx.Match(s)).Success)
                {
                    w.CloudsKnown = true;
                    int feet = m.Groups[2].Value == "///" ? 0 : int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 100;
                    w.Layers.Add(new CloudLayer { Cover = m.Groups[1].Value, FeetAgl = feet });
                    continue;
                }

                if (!w.HasTemp && (m = TempRx.Match(s)).Success)
                {
                    w.HasTemp = true;
                    w.TempC = ParseTemp(m.Groups[1].Value);
                    if (m.Groups[2].Success && m.Groups[2].Value.Length > 0) { w.HasDew = true; w.DewC = ParseTemp(m.Groups[2].Value); }
                    continue;
                }

                if (!w.HasQnh && (m = QnhRx.Match(s)).Success)
                {
                    w.HasQnh = true; w.QnhHpa = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    continue;
                }

                if (!w.HasQnh && (m = AltimeterRx.Match(s)).Success)
                {
                    w.HasQnh = true;
                    w.QnhHpa = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0 * 33.8639;
                    continue;
                }

                if (WxRx.IsMatch(s) || TsRx.IsMatch(s))
                    w.Phenomena.Add(s.TrimStart('+', '-'));
            }
            return w;
        }

        static int ParseTemp(string s)
        {
            bool neg = s.StartsWith("M");
            int v = int.Parse(neg ? s.Substring(1) : s, CultureInfo.InvariantCulture);
            return neg ? -v : v;
        }

        /// <summary>Minutes since the report was issued, or null when the time group cannot be read.</summary>
        public static double? AgeMinutes(string raw, DateTime utcNow)
        {
            Wx w = Parse(raw);
            if (w.Day < 1) return null;

            DateTime cand;
            if (!TryBuild(utcNow.Year, utcNow.Month, w, out cand) || cand > utcNow.AddMinutes(30))
            {
                DateTime prev = new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
                if (!TryBuild(prev.Year, prev.Month, w, out cand)) return null;
            }
            return (utcNow - cand).TotalMinutes;
        }

        static bool TryBuild(int year, int month, Wx w, out DateTime result)
        {
            try
            {
                result = new DateTime(year, month, w.Day, w.Hour, w.Minute, 0, DateTimeKind.Utc);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                result = DateTime.MinValue;
                return false;
            }
        }

        // ---- "same weather?" test used to merge neighbouring stations ------------------

        static int VisClass(double m) { return m >= 9000 ? 3 : m >= 5000 ? 2 : m >= 1600 ? 1 : 0; }

        static int CoverRank(string c)
        {
            switch (c) { case "FEW": return 1; case "SCT": return 2; case "BKN": return 3; default: return 4; }   // OVC, VV
        }

        static int MaxCover(Wx w)
        {
            int r = 0;
            foreach (var l in w.Layers) r = Math.Max(r, CoverRank(l.Cover));
            return r;
        }

        // 0 below 500 ft, 1 below 1000, 2 below 3000, 3 = 3000 or higher / no ceiling.
        static int CeilingClass(Wx w)
        {
            int best = int.MaxValue;
            foreach (var l in w.Layers)
                if (l.Cover == "BKN" || l.Cover == "OVC" || l.Cover == "VV") best = Math.Min(best, l.FeetAgl);
            return best < 500 ? 0 : best < 1000 ? 1 : best < 3000 ? 2 : 3;
        }

        static int AngleDiff(int a, int b)
        {
            int d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }

        /// <summary>
        /// True when two reports describe practically the same weather: similar wind, visibility class,
        /// ceiling class, cloud amount, temperature, pressure and the same significant weather.
        /// Reports that are missing key fields are never treated as similar.
        /// </summary>
        public static bool Similar(Wx a, Wx b)
        {
            if (!a.HasWind || !b.HasWind || !a.HasVis || !b.HasVis || !a.HasQnh || !b.HasQnh) return false;
            if (!a.CloudsKnown || !b.CloudsKnown) return false;

            // wind: light winds count as the same whatever the direction
            bool light = a.WindKt <= 6 && b.WindKt <= 6 && a.GustKt == 0 && b.GustKt == 0;
            if (!light)
            {
                if (Math.Abs(a.WindKt - b.WindKt) > 6) return false;
                if (Math.Abs(Math.Max(a.GustKt, a.WindKt) - Math.Max(b.GustKt, b.WindKt)) > 8) return false;
                if (a.WindDir.HasValue && b.WindDir.HasValue && AngleDiff(a.WindDir.Value, b.WindDir.Value) > 30) return false;
            }

            if (VisClass(a.VisM) != VisClass(b.VisM)) return false;
            if (CeilingClass(a) != CeilingClass(b)) return false;
            if (Math.Abs(MaxCover(a) - MaxCover(b)) > 1) return false;
            if (Math.Abs(a.QnhHpa - b.QnhHpa) > 1.5) return false;
            if (a.HasTemp && b.HasTemp && Math.Abs(a.TempC - b.TempC) > 3) return false;
            if (!a.Phenomena.SetEquals(b.Phenomena)) return false;
            return true;
        }

        // ---- plain-language summary for the lookup dialog ------------------------------

        public static string Describe(string raw)
        {
            Wx w = Parse(raw);
            var parts = new List<string>();

            if (w.HasWind)
            {
                if (w.WindKt == 0) parts.Add("Wind calm");
                else parts.Add("Wind " + (w.WindDir.HasValue ? w.WindDir.Value.ToString("000") + "°" : "variable") + " " + w.WindKt + " kt" +
                               (w.GustKt > 0 ? " gusting " + w.GustKt : ""));
            }

            if (w.HasVis)
            {
                if (w.VisM >= 9999) parts.Add("Vis 10 km or more");
                else if (w.VisM >= 1000) parts.Add("Vis " + (w.VisM / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " km");
                else parts.Add("Vis " + w.VisM.ToString("0", CultureInfo.InvariantCulture) + " m");
            }

            if (w.Phenomena.Count > 0) parts.Add("Weather " + string.Join(" ", new List<string>(w.Phenomena).ToArray()));

            if (w.Cavok) parts.Add("CAVOK");
            else if (w.Layers.Count > 0)
            {
                var c = new List<string>();
                foreach (var l in w.Layers) c.Add(l.Cover + " " + l.FeetAgl.ToString(CultureInfo.InvariantCulture) + " ft");
                parts.Add("Sky " + string.Join(", ", c.ToArray()));
            }
            else if (w.CloudsKnown) parts.Add("Sky clear");

            if (w.HasTemp) parts.Add("Temp " + w.TempC + "°C" + (w.HasDew ? " / dew " + w.DewC + "°C" : ""));
            if (w.HasQnh) parts.Add("QNH " + w.QnhHpa.ToString("0", CultureInfo.InvariantCulture) + " hPa");

            return parts.Count == 0 ? "(could not decode this report)" : string.Join("  ·  ", parts.ToArray());
        }
    }
}
