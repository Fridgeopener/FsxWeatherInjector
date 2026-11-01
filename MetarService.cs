using System;
using System.Collections.Generic;
using System.IO;
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
    }

    /// <summary>
    /// Downloads current METARs around a position from NOAA's Aviation Weather Center.
    /// The search grows only as far as needed: in busy airspace one small request (100 nm) is enough,
    /// over an ocean it keeps widening (100, 200, 400, 800 ... up to the configured maximum).
    /// Searches that cross the 180 degree meridian (Pacific routes) are split into two requests.
    /// Nothing runs between refreshes.
    /// </summary>
    public static class MetarService
    {
        const string Url = "https://aviationweather.gov/api/data/metar?format=json&bbox={0},{1},{2},{3}";
        const double StartRadiusNm = 100;

        /// <param name="maxRadiusNm">Furthest station distance we are willing to use (the Radius setting).</param>
        public static List<Metar> Fetch(double lat, double lon, double maxRadiusNm, int maxStations)
        {
            if (double.IsNaN(lat) || double.IsNaN(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
                throw new Exception(string.Format("Invalid position from FSX ({0}, {1})", lat, lon));

            double r = Math.Min(StartRadiusNm, maxRadiusNm);
            while (true)
            {
                var all = FetchArea(lat, lon, r);   // sorted nearest first
                int inside = 0;
                foreach (var m in all) if (m.DistanceNm <= r) inside++;

                Logger.Info(string.Format("Search radius {0:F0} nm: {1} stations in area, {2} within radius (want {3}, max radius {4:F0} nm)",
                    r, all.Count, inside, maxStations, maxRadiusNm));

                bool enough = inside >= maxStations;
                bool atMax = r >= maxRadiusNm;
                if (enough || atMax)
                {
                    var list = new List<Metar>();
                    foreach (var m in all)
                    {
                        if (m.DistanceNm > maxRadiusNm) continue;
                        list.Add(m);
                        if (list.Count >= maxStations) break;
                    }
                    Logger.Info("METAR stations using: " + list.Count);
                    foreach (var m in list)
                        Logger.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "  {0} {1:F0}nm  {2}", m.Icao, m.DistanceNm, m.Raw));
                    return list;
                }
                r = Math.Min(r * 2, maxRadiusNm);
            }
        }

        // One search area, possibly split in two when it crosses the +/-180 degree meridian.
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
            string url = string.Format(System.Globalization.CultureInfo.InvariantCulture, Url,
                Math.Round(minLat, 2), Math.Round(minLon, 2), Math.Round(maxLat, 2), Math.Round(maxLon, 2));

            string json;
            try
            {
                Logger.Info("GET " + url);
                json = Download(url);
                Logger.Info("NOAA response received (" + (json == null ? 0 : json.Length) + " bytes)");
            }
            catch (WebException ex)
            {
                Logger.Error("NOAA request failed: " + Describe(ex) + "  URL: " + url, ex);
                throw new Exception("NOAA request failed: " + Describe(ex) + "  URL: " + url);
            }

            var result = new List<Metar>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var arr = ser.DeserializeObject(json) as object[];
            if (arr == null) return result;

            // keep only the newest observation per station
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
                m.DistanceNm = Distance(fromLat, fromLon, m.Lat, m.Lon);
                double t = Num(d, "obsTime");

                KeyValuePair<double, Metar> prev;
                if (!newest.TryGetValue(icao, out prev) || t > prev.Key)
                    newest[icao] = new KeyValuePair<double, Metar>(t, m);
            }

            foreach (var kv in newest.Values) result.Add(kv.Value);
            return result;
        }

        static string Download(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "FsxWeatherInjector/1.0";
            req.Accept = "application/json";
            req.Timeout = 30000; // large ocean-sized areas can take a little longer
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream()))
                return rd.ReadToEnd();
        }

        // Includes the server's own error text so we can see why a request was rejected.
        static string Describe(WebException ex)
        {
            string body = "";
            try
            {
                if (ex.Response != null)
                    using (var rd = new StreamReader(ex.Response.GetResponseStream()))
                    {
                        body = rd.ReadToEnd();
                        if (body.Length > 300) body = body.Substring(0, 300);
                    }
            }
            catch { }
            return ex.Message + (body.Length > 0 ? " | " + body.Replace("\n", " ") : "");
        }

        // FSX wants "ICAO ddhhmmZ ..." with no METAR/SPECI prefix and no remarks section.
        static string Clean(string raw)
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

        static double Distance(double lat1, double lon1, double lat2, double lon2)
        {
            double r = 3440.065; // earth radius, nm
            double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180;
            double dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }
    }
}
