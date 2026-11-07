using System;
using System.Collections.Generic;
using System.Net;
using System.Web.Script.Serialization;

namespace FsxWeatherInjector
{
    public class Metar
    {
        public string Icao;
        public string Name;
        public string Raw;      // cleaned, FSX-ready METAR string
        public double Lat, Lon; // degrees
        public double ElevM;    // station elevation, metres
        public double DistanceNm;
        public string Source = "NOAA";
        public List<string> Covers = new List<string>();   // nearby stations merged into this one
    }

    /// <summary>What the user chose in Settings, copied so a background fetch cannot see half-changed values.</summary>
    public class FetchOptions
    {
        public SourceKind Source = SourceKind.Noaa;
        public string CustomUrl = "";
        public bool FallbackToNoaa = true;
        public bool Merge = true;
        public double MergeNm = 40;

        public static FetchOptions From(Settings s)
        {
            return new FetchOptions
            {
                Source = s.GetSourceKind(),
                CustomUrl = s.CustomUrl ?? "",
                FallbackToNoaa = s.FallbackToNoaa,
                Merge = s.MergeStations,
                MergeNm = s.MergeNm
            };
        }
    }

    public class FetchResult
    {
        public List<Metar> Stations = new List<Metar>();
        public int Considered;      // stations found inside the final search radius
        public int Merged;          // folded into a neighbour
        public int Fallbacks;       // chosen stations that ended up using NOAA text instead of the selected source
        public string SourceError;  // first problem reaching the selected source, if any
    }

    public class LookupResult
    {
        public string Icao;
        public bool Found;
        public bool HasCoords;
        public Metar Station;       // Raw/Name/Source filled in when Found
        public string Note;
    }

    /// <summary>
    /// Downloads current METARs around a position. Station positions come from NOAA's Aviation Weather Center
    /// (one area request); the report text can come from NOAA, VATSIM or a custom address.
    /// The search grows only as far as needed (100, 200, 400, 800 ... up to the configured maximum), and
    /// areas that cross the 180 degree meridian are split into two requests. Nothing runs between refreshes.
    /// </summary>
    public static class MetarService
    {
        const string AreaUrl = "https://aviationweather.gov/api/data/metar?format=json&bbox={0},{1},{2},{3}";
        const string IdsUrl = "https://aviationweather.gov/api/data/metar?format=json&ids={0}";
        const double StartRadiusNm = 100;
        const double StaleMinutes = 180;   // a network report older than this loses to a clearly newer NOAA one

        public static FetchResult Fetch(double lat, double lon, double maxRadiusNm, int maxStations, FetchOptions opt)
        {
            if (double.IsNaN(lat) || double.IsNaN(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
                throw new Exception(string.Format("Invalid position from FSX ({0}, {1})", lat, lon));
            if (opt == null) opt = new FetchOptions();

            var srcCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // icao -> report from chosen source ("" = none)
            var res = new FetchResult();
            double r = Math.Min(StartRadiusNm, maxRadiusNm);

            while (true)
            {
                var all = FetchArea(lat, lon, r);   // NOAA stations, nearest first
                var inside = new List<Metar>();
                foreach (var m in all) if (m.DistanceNm <= r) inside.Add(m);

                string srcError = ApplySource(inside, opt, srcCache);
                if (srcError != null && res.SourceError == null) res.SourceError = srcError;

                var sel = StationSelector.Select(inside, maxStations, opt.Merge, opt.MergeNm);

                Logger.Info(string.Format("Search radius {0:F0} nm: {1} stations in area, {2} within radius, {3} chosen, {4} merged (want {5}, max radius {6:F0} nm)",
                    r, all.Count, inside.Count, sel.Chosen.Count, sel.Merged, maxStations, maxRadiusNm));

                if (sel.Chosen.Count >= maxStations || r >= maxRadiusNm)
                {
                    res.Stations = sel.Chosen;
                    res.Considered = inside.Count;
                    res.Merged = sel.Merged;
                    foreach (var m in sel.Chosen)
                    {
                        if (m.Source.StartsWith("NOAA (")) res.Fallbacks++;
                        Logger.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "  {0} {1:F0}nm [{2}]{3}  {4}", m.Icao, m.DistanceNm, m.Source,
                            m.Covers.Count > 0 ? " (also covers " + string.Join(",", m.Covers.ToArray()) + ")" : "", m.Raw));
                    }
                    return res;
                }
                r = Math.Min(r * 2, maxRadiusNm);
            }
        }

        // Replace the NOAA text with the chosen source's text where available. Returns the first source error.
        static string ApplySource(List<Metar> stations, FetchOptions opt, Dictionary<string, string> srcCache)
        {
            if (opt.Source == SourceKind.Noaa) return null;

            string error = null;
            var need = new List<string>();
            foreach (var m in stations) if (!srcCache.ContainsKey(m.Icao)) need.Add(m.Icao);

            if (need.Count > 0)
            {
                var got = MetarSources.Fetch(opt.Source, opt.CustomUrl, need, out error);
                foreach (var i in need) { string v; srcCache[i] = got.TryGetValue(i, out v) ? v : ""; }
            }

            var keep = new List<Metar>();
            foreach (var m in stations)
            {
                string label;
                string text = Choose(m.Raw, srcCache[m.Icao], opt.Source, opt.FallbackToNoaa, out label);
                if (text == null) continue;      // no report and fallback off: leave this station out
                m.Raw = text; m.Source = label;
                keep.Add(m);
            }
            stations.Clear();
            stations.AddRange(keep);
            return error;
        }

        // Decides which report to use for one airport, and how to label it.
        static string Choose(string noaaRaw, string srcRaw, SourceKind kind, bool fallback, out string label)
        {
            string name = MetarSources.DisplayName(kind);
            if (kind == SourceKind.Noaa) { label = "NOAA"; return noaaRaw; }

            if (!string.IsNullOrEmpty(srcRaw))
            {
                if (fallback && !string.IsNullOrEmpty(noaaRaw))
                {
                    double? a = MetarParser.AgeMinutes(srcRaw, DateTime.UtcNow);
                    double? b = MetarParser.AgeMinutes(noaaRaw, DateTime.UtcNow);
                    if (a.HasValue && b.HasValue && a.Value > StaleMinutes && b.Value < a.Value - 30)
                    {
                        label = "NOAA (" + name + " report old)";
                        return noaaRaw;
                    }
                }
                label = name;
                return srcRaw;
            }

            if (fallback && !string.IsNullOrEmpty(noaaRaw)) { label = "NOAA (fallback)"; return noaaRaw; }
            label = null;
            return null;
        }

        // ---- one search area, possibly split in two across the +/-180 degree meridian ---

        static List<Metar> FetchArea(double lat, double lon, double radiusNm)
        {
            double dLat = radiusNm / 60.0;
            double minLat = Math.Max(-90, lat - dLat), maxLat = Math.Min(90, lat + dLat);
            double dLon = radiusNm / (60.0 * Math.Max(0.2, Math.Cos(lat * Math.PI / 180.0)));

            var lonRanges = new List<double[]>();
            if (dLon >= 180)
            {
                lonRanges.Add(new[] { -180.0, 180.0 });
            }
            else
            {
                double a = lon - dLon, b = lon + dLon;
                if (a < -180) { lonRanges.Add(new[] { -180.0, b }); lonRanges.Add(new[] { a + 360, 180.0 }); }
                else if (b > 180) { lonRanges.Add(new[] { a, 180.0 }); lonRanges.Add(new[] { -180.0, b - 360 }); }
                else lonRanges.Add(new[] { a, b });
            }

            var byIcao = new Dictionary<string, Metar>();
            foreach (var lr in lonRanges)
                foreach (var m in FetchBox(lat, lon, minLat, lr[0], maxLat, lr[1]))
                    byIcao[m.Icao] = m;

            var list = new List<Metar>(byIcao.Values);
            list.Sort((x, y) => x.DistanceNm.CompareTo(y.DistanceNm));
            return list;
        }

        static List<Metar> FetchBox(double fromLat, double fromLon, double minLat, double minLon, double maxLat, double maxLon)
        {
            // Round to 2 decimals: short, clean numbers in the query string.
            string url = string.Format(System.Globalization.CultureInfo.InvariantCulture, AreaUrl,
                Math.Round(minLat, 2), Math.Round(minLon, 2), Math.Round(maxLat, 2), Math.Round(maxLon, 2));

            string json;
            try
            {
                Logger.Info("GET " + url);
                json = Http.Get(url, 30000, "application/json");   // ocean-sized areas can take a little longer
                Logger.Info("NOAA response received (" + (json == null ? 0 : json.Length) + " bytes)");
            }
            catch (WebException ex)
            {
                Logger.Error("NOAA request failed: " + Describe(ex) + "  URL: " + url, ex);
                throw new Exception("NOAA request failed: " + Describe(ex) + "  URL: " + url);
            }
            return ParseAwc(json, fromLat, fromLon, true);
        }

        // Newest report per station from an Aviation Weather Center JSON response.
        static List<Metar> ParseAwc(string json, double fromLat, double fromLon, bool computeDistance)
        {
            var result = new List<Metar>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var arr = ser.DeserializeObject(json) as object[];
            if (arr == null) return result;

            var newest = new Dictionary<string, KeyValuePair<double, Metar>>();
            foreach (var o in arr)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                string icao = Str(d, "icaoId");
                string raw = Str(d, "rawOb");
                if (icao == null || icao.Length != 4 || raw == null) continue;

                var m = new Metar
                {
                    Icao = icao,
                    Name = Str(d, "name") ?? icao,
                    Raw = Clean(raw),
                    Lat = Num(d, "lat"),
                    Lon = Num(d, "lon"),
                    ElevM = Num(d, "elev")
                };
                if (computeDistance) m.DistanceNm = StationSelector.GreatCircleNm(fromLat, fromLon, m.Lat, m.Lon);
                double t = Num(d, "obsTime");

                KeyValuePair<double, Metar> prev;
                if (!newest.TryGetValue(icao, out prev) || t > prev.Key)
                    newest[icao] = new KeyValuePair<double, Metar>(t, m);
            }

            foreach (var kv in newest.Values) result.Add(kv.Value);
            return result;
        }

        // ---- look up any airports, wherever they are (used by the lookup dialog) -------

        public static List<LookupResult> Lookup(IList<string> icaos, SourceKind kind, string customUrl, bool fallbackToNoaa)
        {
            var results = new List<LookupResult>();
            var noaa = new Dictionary<string, Metar>(StringComparer.OrdinalIgnoreCase);
            string noaaError = null;

            for (int i = 0; i < icaos.Count; i += 40)
            {
                var chunk = new List<string>();
                for (int j = i; j < icaos.Count && chunk.Count < 40; j++) chunk.Add(icaos[j]);
                string url = string.Format(IdsUrl, string.Join(",", chunk.ToArray()));
                try
                {
                    Logger.Info("GET " + url);
                    string json = Http.Get(url, 20000, "application/json");
                    foreach (var m in ParseAwc(json, 0, 0, false)) noaa[m.Icao] = m;
                }
                catch (WebException ex)
                {
                    noaaError = Describe(ex);
                    Logger.Error("NOAA lookup failed: " + noaaError, ex);
                }
            }
            if (kind == SourceKind.Noaa && noaa.Count == 0 && noaaError != null)
                throw new Exception("NOAA request failed: " + noaaError);

            Dictionary<string, string> src = null;
            string srcError = null;
            if (kind != SourceKind.Noaa) src = MetarSources.Fetch(kind, customUrl, icaos, out srcError);

            foreach (var icao in icaos)
            {
                var r = new LookupResult { Icao = icao };
                Metar n; noaa.TryGetValue(icao, out n);
                string s = null;
                if (src != null) src.TryGetValue(icao, out s);

                string label;
                string text = Choose(n == null ? null : n.Raw, s, kind, fallbackToNoaa, out label);

                if (text == null)
                {
                    r.Found = false;
                    r.Note = kind == SourceKind.Noaa ? "No report found for this airport."
                           : srcError != null ? MetarSources.DisplayName(kind) + " problem: " + srcError
                           : "No report found from " + MetarSources.DisplayName(kind) + ".";
                }
                else
                {
                    r.Found = true;
                    r.Station = new Metar
                    {
                        Icao = icao,
                        Name = n != null ? n.Name : icao,
                        Raw = text,
                        Lat = n != null ? n.Lat : 0,
                        Lon = n != null ? n.Lon : 0,
                        ElevM = n != null ? n.ElevM : 0,
                        Source = label
                    };
                    r.HasCoords = n != null && !(n.Lat == 0 && n.Lon == 0);
                    if (!r.HasCoords) r.Note = "Position unknown (not in NOAA's list), so it cannot be injected into FSX.";
                }
                results.Add(r);
            }
            return results;
        }

        // ---- helpers -------------------------------------------------------------------

        // Includes the server's own error text so we can see why a request was rejected.
        static string Describe(WebException ex)
        {
            string body = "";
            try
            {
                if (ex.Response != null)
                    using (var rd = new System.IO.StreamReader(ex.Response.GetResponseStream()))
                    {
                        body = rd.ReadToEnd();
                        if (body.Length > 300) body = body.Substring(0, 300);
                    }
            }
            catch { }
            return ex.Message + (body.Length > 0 ? " | " + body.Replace("\n", " ") : "");
        }

        // FSX wants "ICAO ddhhmmZ ..." with no METAR/SPECI prefix and no remarks section.
        internal static string Clean(string raw)
        {
            raw = raw.Trim();
            if (raw.StartsWith("METAR ") || raw.StartsWith("SPECI ")) raw = raw.Substring(6);
            int i = raw.IndexOf(" RMK", StringComparison.Ordinal);
            if (i > 0) raw = raw.Substring(0, i);
            return raw.Replace("$", "").Trim();
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null;
        }

        static double Num(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null)
            {
                try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { }
            }
            return 0;
        }
    }
}
