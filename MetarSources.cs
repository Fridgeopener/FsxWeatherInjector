using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FsxWeatherInjector
{
    public enum SourceKind { Noaa, Vatsim, Custom }

    /// <summary>Minimal HTTP GET used by all weather sources.</summary>
    static class Http
    {
        public const string UserAgent = "FsxWeatherInjector/0.6";

        public static string Get(string url, int timeoutMs, string accept)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;                       // generic client name only
            req.Accept = accept;
            req.Timeout = timeoutMs;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream()))
                return rd.ReadToEnd();
        }
    }

    /// <summary>
    /// Per-airport METAR sources other than NOAA's area search. Airport positions always come from NOAA;
    /// these sources only supply the report text for airports we already know about.
    /// </summary>
    public static class MetarSources
    {
        public const string VatsimBase = "https://metar.vatsim.net/";
        const int ChunkSize = 80;          // airports per request
        const int MaxSingleRequests = 60;  // cap for "one request per airport" custom addresses

        // Matches "EGLL 041650Z ..." (optionally preceded by METAR/SPECI) in plain text, JSON or HTML alike.
        static readonly Regex ReportRx = new Regex(
            @"\b(?:(?:METAR|SPECI)\s+)?([A-Z][A-Z0-9]{3})\s+\d{6}Z\b[^\r\n""<>]*", RegexOptions.Compiled);

        public static string DisplayName(SourceKind k)
        {
            switch (k) { case SourceKind.Vatsim: return "VATSIM"; case SourceKind.Custom: return "Custom"; default: return "NOAA"; }
        }

        public static bool IsValidTemplate(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (url.IndexOf("{icao}", StringComparison.OrdinalIgnoreCase) < 0 &&
                url.IndexOf("{icaos}", StringComparison.OrdinalIgnoreCase) < 0) return false;
            Uri u;
            string probe = url.Replace("{icaos}", "EGLL").Replace("{icao}", "EGLL");
            return Uri.TryCreate(probe, UriKind.Absolute, out u) &&
                   (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// Fetches reports for the given airports. Returns what could be found (cleaned, FSX-ready text);
        /// <paramref name="error"/> is set to the first problem met, but partial results are still returned.
        /// </summary>
        public static Dictionary<string, string> Fetch(SourceKind kind, string customUrl, IList<string> icaos, out string error)
        {
            string firstError = null;
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (icaos == null || icaos.Count == 0) { error = null; return result; }

            if (kind == SourceKind.Vatsim)
            {
                for (int i = 0; i < icaos.Count; i += ChunkSize)
                {
                    string list = string.Join(",", Slice(icaos, i, ChunkSize));
                    FetchOne(VatsimBase + list, result, ref firstError);
                }
            }
            else if (kind == SourceKind.Custom)
            {
                if (!IsValidTemplate(customUrl))
                {
                    error = "The custom address is not valid (it must start with http:// or https:// and contain {icao} or {icaos}).";
                    return result;
                }

                if (customUrl.IndexOf("{icaos}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    for (int i = 0; i < icaos.Count; i += ChunkSize)
                    {
                        string list = string.Join(",", Slice(icaos, i, ChunkSize));
                        FetchOne(Replace(customUrl, "{icaos}", list), result, ref firstError);
                    }
                }
                else
                {
                    var some = Slice(icaos, 0, MaxSingleRequests);
                    string err = null;
                    var gate = new object();
                    Parallel.ForEach(some, new ParallelOptions { MaxDegreeOfParallelism = 6 }, icao =>
                    {
                        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        string e = null;
                        FetchOne(Replace(customUrl, "{icao}", icao), local, ref e);
                        lock (gate)
                        {
                            foreach (var kv in local) result[kv.Key] = kv.Value;
                            if (err == null) err = e;
                        }
                    });
                    if (firstError == null) firstError = err;
                }
            }

            error = firstError;
            return result;
        }

        static void FetchOne(string url, Dictionary<string, string> into, ref string firstError)
        {
            try
            {
                Logger.Info("GET " + url);
                string text = Http.Get(url, 15000, "text/plain, application/json, */*");
                int before = into.Count;
                foreach (var kv in ParseReports(text)) into[kv.Key] = kv.Value;
                Logger.Info("  -> " + (into.Count - before) + " report(s) read from " + (text == null ? 0 : text.Length) + " bytes");
            }
            catch (Exception ex)
            {
                Logger.Warn("Source request failed: " + url + " :: " + ex.Message);
                if (firstError == null) firstError = ex.Message;
            }
        }

        /// <summary>Finds every METAR-looking line in a response, whatever the surrounding format.</summary>
        public static Dictionary<string, string> ParseReports(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return d;
            foreach (Match m in ReportRx.Matches(text))
            {
                string icao = m.Groups[1].Value.ToUpperInvariant();
                d[icao] = MetarService.Clean(m.Value);
            }
            return d;
        }

        static string Replace(string template, string token, string value)
        {
            return Regex.Replace(template, Regex.Escape(token), value.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }

        static List<string> Slice(IList<string> src, int start, int count)
        {
            var l = new List<string>();
            for (int i = start; i < src.Count && l.Count < count; i++) l.Add(src[i]);
            return l;
        }
    }
}
