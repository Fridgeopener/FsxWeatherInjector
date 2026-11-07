using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FsxWeatherInjector
{
    public class AtisInfo
    {
        public string Icao;
        public string Callsign;
        public string Frequency;
        public string Code;       // information letter, if the controller software sends one
        public string Text;
        public string Updated;
    }

    /// <summary>
    /// Online ATIS from the VATSIM data feed. This only exists for airports where a VATSIM controller is
    /// currently online; there is no free public source for real-world ATIS text.
    /// The feed is large, so it is downloaded only when asked for and cached for a minute.
    /// </summary>
    public static class AtisService
    {
        const string Feed = "https://data.vatsim.net/v3/vatsim-data.json";
        static readonly Regex AtisKey = new Regex("\"atis\"\\s*:\\s*\\[", RegexOptions.Compiled);

        static string cachedArray;
        static DateTime cachedAt = DateTime.MinValue;
        static readonly object gate = new object();

        /// <summary>Returns the online ATIS entries for the given airports. Throws if the feed cannot be read.</summary>
        public static List<AtisInfo> Find(IEnumerable<string> icaos)
        {
            string arrayJson;
            lock (gate)
            {
                if (cachedArray == null || (DateTime.UtcNow - cachedAt).TotalSeconds > 60)
                {
                    Logger.Info("GET " + Feed);
                    string json = Http.Get(Feed, 30000, "application/json");
                    cachedArray = ExtractArray(json);
                    cachedAt = DateTime.UtcNow;
                    Logger.Info("VATSIM feed read (" + json.Length + " bytes), ATIS section " + (cachedArray == null ? "missing" : cachedArray.Length + " bytes"));
                }
                arrayJson = cachedArray;
            }

            var found = new List<AtisInfo>();
            if (string.IsNullOrEmpty(arrayJson)) return found;

            var wanted = new HashSet<string>(icaos, StringComparer.OrdinalIgnoreCase);
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var arr = ser.DeserializeObject(arrayJson) as object[];
            if (arr == null) return found;

            foreach (var o in arr)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                string callsign = Str(d, "callsign");
                if (string.IsNullOrEmpty(callsign)) continue;

                string icao = callsign.Split('_')[0].ToUpperInvariant();   // EGLL_ATIS, EGLL_A_ATIS ...
                if (!wanted.Contains(icao)) continue;

                string text = "";
                object lines;
                if (d.TryGetValue("text_atis", out lines) && lines is object[])
                {
                    var parts = new List<string>();
                    foreach (var l in (object[])lines) if (l != null) parts.Add(Convert.ToString(l));
                    text = string.Join(" ", parts.ToArray());
                }

                found.Add(new AtisInfo
                {
                    Icao = icao,
                    Callsign = callsign,
                    Frequency = Str(d, "frequency"),
                    Code = Str(d, "atis_code"),
                    Text = text,
                    Updated = Str(d, "last_updated")
                });
            }
            return found;
        }

        // Cut the "atis":[ ... ] array out of the big feed so we do not parse thousands of pilots.
        static string ExtractArray(string json)
        {
            Match m = AtisKey.Match(json);
            if (!m.Success) return null;

            int start = m.Index + m.Length - 1;   // the '['
            int depth = 0;
            bool inString = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) return json.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null;
        }
    }
}
