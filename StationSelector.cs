using System;
using System.Collections.Generic;

namespace FsxWeatherInjector
{
    /// <summary>
    /// Chooses which stations to inject. Walks the candidates from nearest to furthest; a station that is
    /// close to one already chosen and reports practically the same weather is "merged" into it (it is not
    /// injected itself), and the walk simply carries on to the next station further out.
    /// </summary>
    public static class StationSelector
    {
        public class Result
        {
            public List<Metar> Chosen = new List<Metar>();
            public int Merged;     // how many candidates were folded into a chosen station
        }

        /// <param name="candidates">Sorted nearest first (distance from the aircraft).</param>
        public static Result Select(IList<Metar> candidates, int maxStations, bool merge, double mergeNm)
        {
            var res = new Result();
            var parsed = new Dictionary<Metar, Wx>();

            foreach (var c in candidates)
            {
                if (res.Chosen.Count >= maxStations) break;
                c.Covers.Clear();

                if (merge)
                {
                    Wx wc = Parse(parsed, c);
                    Metar into = null;
                    foreach (var a in res.Chosen)
                    {
                        if (StationDistanceNm(a, c) > mergeNm) continue;
                        if (!MetarParser.Similar(Parse(parsed, a), wc)) continue;
                        into = a;
                        break;
                    }
                    if (into != null)
                    {
                        into.Covers.Add(c.Icao);
                        res.Merged++;
                        continue;
                    }
                }
                res.Chosen.Add(c);
            }
            return res;
        }

        static Wx Parse(Dictionary<Metar, Wx> cache, Metar m)
        {
            Wx w;
            if (!cache.TryGetValue(m, out w)) { w = MetarParser.Parse(m.Raw); cache[m] = w; }
            return w;
        }

        public static double StationDistanceNm(Metar a, Metar b)
        {
            return GreatCircleNm(a.Lat, a.Lon, b.Lat, b.Lon);
        }

        public static double GreatCircleNm(double lat1, double lon1, double lat2, double lon2)
        {
            double r = 3440.065; // earth radius, nm
            double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180;
            double dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }
    }
}
