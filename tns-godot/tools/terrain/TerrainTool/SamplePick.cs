using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Tns.Tools.Terrain;

/// <summary>
/// Chooses where to cut each ground texture sample (terrain samplepick). A ground texture is laid
/// over every place of its kind, so its sample must be plain, typical ground of that one kind:
/// anything else in it (a lake, a town, a strip of forest) shows up again and again wherever the
/// texture is laid. For each sample place this searches the land cover map around it for the
/// square window (SizeKm across) that is most purely its kind, with no water and no built-up
/// ground, and the same all over (each of its 4 x 4 parts counts, not only the whole).
/// </summary>
public static class SamplePick
{
    /// <summary>A place to search around; MaxBuilt and MaxWater, when not negative, replace the usual limits.</summary>
    public sealed record Site(string Name, int Kind, double Lat, double Lon, double RadiusDeg, double MaxBuilt = -1, double MaxWater = -1);

    /// <summary>Share: of the kind and the kinds that belong with it; Own: of the kind alone.</summary>
    public sealed record Pick(Site Site, double Lat, double Lon, double Share, double LeastPart, double Own, double Water, double Built, double Moved, int Towns = 0, int Lakes = 0);

    /// <summary>
    /// Kinds that may share a sample with a kind, since they look alike from above and are part of
    /// the same countryside (pasture between fields, grass between bushes). Anything else, such as
    /// a wood in farmland, would stand out as a dark patch repeated wherever the texture is laid.
    /// </summary>
    private static bool Belongs(int kind, int c) => c == kind
        || (kind == Cover.Crop && c == Cover.Grass)
        || (kind == Cover.Shrub && c == Cover.Grass)
        || (kind == Cover.Grass && c == Cover.Shrub);

    /// <summary>
    /// A town or a lake: a cell (about 2 km) more than this share built up or water. The map counts
    /// villages and roads as built up too, 1 to 3% of most farmland, which is part of how farmland
    /// looks; a town or a lake is not, so the window with fewest wins.
    /// </summary>
    private const double SpotShare = 0.25;

    /// <summary>What each town and lake in a window costs its score (out of about 3).</summary>
    private const double TownCost = 0.03, LakeCost = 0.1;

    /// <summary>Cells the map is counted in, degrees (about 2 km).</summary>
    private const double CellDeg = 0.02;

    public static List<Pick> Run(LandCover cover, IEnumerable<Site> sites, double sizeKm, double maxWater, double maxBuilt)
    {
        var picks = new List<Pick>();
        foreach (Site site in sites)
        {
            Pick pick = PickOne(cover, site, sizeKm, site.MaxWater >= 0 ? site.MaxWater : maxWater, site.MaxBuilt >= 0 ? site.MaxBuilt : maxBuilt);
            picks.Add(pick);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {site.Name,-18} {pick.Lat,7:0.00} {pick.Lon,8:0.00}  belongs {100 * pick.Share,5:0.0}% (least part {100 * pick.LeastPart,5:0.0}%)  {Cover.Names[site.Kind]} alone {100 * pick.Own,5:0.0}%  water {100 * pick.Water:0.00}%  built {100 * pick.Built:0.00}%  towns {pick.Towns}  lakes {pick.Lakes}  moved {pick.Moved:0.0} deg"));
        }

        return picks;
    }

    private static Pick PickOne(LandCover cover, Site site, double sizeKm, double maxWater, double maxBuilt)
    {
        double halfLat = sizeKm / 2 / 111.32;
        double maxHalfLon = halfLat / Math.Max(Math.Cos((Math.Abs(site.Lat) + site.RadiusDeg + halfLat) * Math.PI / 180), 0.2);
        double north = site.Lat + site.RadiusDeg + halfLat, south = site.Lat - site.RadiusDeg - halfLat;
        double west = site.Lon - site.RadiusDeg - maxHalfLon, east = site.Lon + site.RadiusDeg + maxHalfLon;

        // Counts per cell of ground that belongs, of the kind alone, of water, of built-up ground
        // and of all mapped ground, as summed tables so any window adds up in four lookups.
        int ppd = cover.PixelsPerDegree;
        int per = Math.Max(1, (int)Math.Round(CellDeg * ppd));
        double cellDeg = per / (double)ppd;
        long x0 = (long)Math.Floor((west + 180) * ppd), y0 = (long)Math.Floor((90 - north) * ppd);
        int cw = (int)Math.Ceiling((east - west) / cellDeg), ch = (int)Math.Ceiling((north - south) / cellDeg);
        double cellWest = -180 + x0 / (double)ppd, cellNorth = 90 - y0 / (double)ppd;
        int stride = cw + 1;
        var tables = new long[7][];
        for (int t = 0; t < 7; t++) tables[t] = new long[stride * (ch + 1)];
        var row = new byte[cw * per * per];
        var counts = new int[7];
        for (int cy = 0; cy < ch; cy++)
        {
            cover.Read(x0, y0 + (long)cy * per, cw * per, per, row);
            for (int cx = 0; cx < cw; cx++)
            {
                Array.Clear(counts);
                for (int py = 0; py < per; py++)
                {
                    for (int px = 0; px < per; px++)
                    {
                        byte c = row[py * cw * per + cx * per + px];
                        if (c == Cover.Unknown) continue;
                        counts[4]++;
                        if (Belongs(site.Kind, c)) counts[0]++;
                        if (c == site.Kind) counts[1]++;
                        else if (c == Cover.Water) counts[2]++;
                        else if (c == Cover.Built) counts[3]++;
                    }
                }

                // Whole cells that are a town or a lake.
                counts[5] = counts[3] > SpotShare * Math.Max(counts[4], 1) ? 1 : 0;
                counts[6] = counts[2] > SpotShare * Math.Max(counts[4], 1) ? 1 : 0;
                int at = (cy + 1) * stride + cx + 1, up = cy * stride + cx + 1;
                for (int t = 0; t < 7; t++) tables[t][at] = counts[t] + tables[t][at - 1] + tables[t][up] - tables[t][up - 1];
            }
        }

        long Sum(int t, int ax, int ay, int bx, int by) =>
            tables[t][by * stride + bx] - tables[t][ay * stride + bx] - tables[t][by * stride + ax] + tables[t][ay * stride + ax];

        Pick best = null, cleanest = null;
        double bestScore = double.MinValue, cleanestDirt = double.MaxValue;
        const double step = 0.05;
        for (double lat = site.Lat - site.RadiusDeg; lat <= site.Lat + site.RadiusDeg + 1e-9; lat += step)
        {
            double cos = Math.Cos(lat * Math.PI / 180);
            double halfLon = halfLat / Math.Max(cos, 0.2);
            int ay = (int)Math.Round((cellNorth - (lat + halfLat)) / cellDeg), by = (int)Math.Round((cellNorth - (lat - halfLat)) / cellDeg);
            for (double lon = site.Lon - site.RadiusDeg; lon <= site.Lon + site.RadiusDeg + 1e-9; lon += step)
            {
                double moved = Math.Sqrt((lat - site.Lat) * (lat - site.Lat) + (lon - site.Lon) * (lon - site.Lon) * cos * cos);
                if (moved > site.RadiusDeg) continue;
                int ax = (int)Math.Round((lon - halfLon - cellWest) / cellDeg), bx = (int)Math.Round((lon + halfLon - cellWest) / cellDeg);
                if (ax < 0 || ay < 0 || bx > cw || by > ch) continue;
                double total = Sum(4, ax, ay, bx, by);
                if (total < 0.99 * (bx - ax) * (by - ay) * per * per) continue; // sea or unmapped in it
                double share = Sum(0, ax, ay, bx, by) / total;
                double own = Sum(1, ax, ay, bx, by) / total;
                double wet = Sum(2, ax, ay, bx, by) / total;
                double town = Sum(3, ax, ay, bx, by) / total;
                double least = 1;
                long towns = Sum(5, ax, ay, bx, by), lakes = Sum(6, ax, ay, bx, by);
                for (int j = 0; j < 4; j++)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        int px0 = ax + (bx - ax) * i / 4, px1 = ax + (bx - ax) * (i + 1) / 4;
                        int py0 = ay + (by - ay) * j / 4, py1 = ay + (by - ay) * (j + 1) / 4;
                        double t = Math.Max(Sum(4, px0, py0, px1, py1), 1);
                        least = Math.Min(least, Sum(0, px0, py0, px1, py1) / t);
                    }
                }

                var pick = new Pick(site, lat, lon, share, least, own, wet, town, moved, (int)towns, (int)lakes);
                double dirt = wet / maxWater + town / maxBuilt;
                if (dirt < cleanestDirt) { cleanestDirt = dirt; cleanest = pick; }
                if (wet > maxWater || town > maxBuilt) continue;
                // The share of ground that belongs, its weakest part and the kind itself counting
                // alike, less for each town and lake (in farmland no window this size is free of
                // small towns, so the fewest wins); a little for staying near the place chosen.
                double score = share + least + own - TownCost * towns - LakeCost * lakes - 0.01 * moved;
                if (score > bestScore) { bestScore = score; best = pick; }
            }
        }

        if (best == null) Console.WriteLine($"  {site.Name}: no window within the water and built limits, the cleanest one is used");
        return best ?? cleanest ?? new Pick(site, site.Lat, site.Lon, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>Reads sample_sites.csv: name, lat, lon, search radius (degrees), and optionally max_built and max_water.</summary>
    public static List<Site> ReadSites(string path) =>
        File.ReadLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("name,"))
            .Select(l => l.Split(','))
            .Select(p => new Site(p[0], Array.IndexOf(Cover.Names, p[0].Split('_')[0]),
                double.Parse(p[1], CultureInfo.InvariantCulture), double.Parse(p[2], CultureInfo.InvariantCulture),
                p.Length > 3 && p[3] != "" ? double.Parse(p[3], CultureInfo.InvariantCulture) : 2.0,
                p.Length > 4 && p[4] != "" ? double.Parse(p[4], CultureInfo.InvariantCulture) : -1,
                p.Length > 5 && p[5] != "" ? double.Parse(p[5], CultureInfo.InvariantCulture) : -1))
            .ToList();

    public static void Save(string path, IEnumerable<Pick> picks)
    {
        var lines = new List<string> { "name,lat,lon,belongs,least_part,own,water,built,towns,lakes,moved_deg" };
        lines.AddRange(picks.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"{p.Site.Name},{p.Lat:0.00},{p.Lon:0.00},{p.Share:0.000},{p.LeastPart:0.000},{p.Own:0.000},{p.Water:0.0000},{p.Built:0.0000},{p.Towns},{p.Lakes},{p.Moved:0.00}")));
        File.WriteAllLines(path, lines);
    }
}
