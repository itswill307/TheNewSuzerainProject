using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

/// <summary>
/// terrain decisions: draws a pack's decisions.csv (see DecisionLog) as one world map per level.
/// Each tile the bake looked at is coloured by its measured change against the tile above,
/// divided by the limit: blue below the limit (left out, unless something else kept it), orange
/// and red above it (kept because of what it shows). Tiles kept for another reason (a finer tile
/// below was kept, a level always stored, a keepSquares square) are striped. Read next to the
/// "terrain map" picture of the same pack: the deepest level kept at a place is the deepest panel
/// where that place is warm or striped.
/// </summary>
public static class DecisionMap
{
    private const int PanelWidth = 720, PanelHeight = 360, Margin = 24, Gap = 18, LabelHeight = 30;
    private static readonly (byte, byte, byte) Ink = (226, 228, 235), Faint = (150, 155, 168), Background = (20, 22, 28);
    private static readonly (byte, byte, byte) Unmeasured = (44, 48, 60), Capped = (90, 90, 96);

    /// <summary>
    /// The colour scale: measured change / limit on a log scale from 1% to 100 times, smooth on
    /// each side of the limit with a deliberate break at it. Below the limit (left out) one blue
    /// ramp, dark far below and lighter close under; above it (kept) a yellow - orange - red ramp,
    /// light just over and darker far over. Colours blend in OKLab, so equal steps of the scale
    /// look like equal steps of colour.
    /// </summary>
    private static readonly (double Ratio, (byte, byte, byte) Colour)[] Below =
    {
        (0.01, (16, 66, 129)), (0.1, (42, 120, 214)), (1.0, (183, 211, 246)),
    };

    private static readonly (double Ratio, (byte, byte, byte) Colour)[] Above =
    {
        (1.0, (247, 220, 111)), (10.0, (235, 104, 52)), (100.0, (150, 28, 34)),
    };

    private static (byte, byte, byte) ColourOf(double ratio)
    {
        var stops = ratio < 1.0 ? Below : Above;
        double t = Math.Log10(Math.Clamp(ratio, 0.01, 100.0));
        for (int i = 0; i < stops.Length - 1; i++)
        {
            double a = Math.Log10(stops[i].Ratio), b = Math.Log10(stops[i + 1].Ratio);
            if (t <= b || i == stops.Length - 2) return Mix(stops[i].Colour, stops[i + 1].Colour, Math.Clamp((t - a) / (b - a), 0.0, 1.0));
        }

        return stops[^1].Colour;
    }

    private static (byte, byte, byte) Mix((byte R, byte G, byte B) from, (byte R, byte G, byte B) to, double t)
    {
        var (l1, a1, b1) = ToOklab(from);
        var (l2, a2, b2) = ToOklab(to);
        return FromOklab(l1 + (l2 - l1) * t, a1 + (a2 - a1) * t, b1 + (b2 - b1) * t);
    }

    private static double Linear(byte c) { double v = c / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }

    private static byte Srgb(double v)
    {
        v = Math.Clamp(v, 0.0, 1.0);
        v = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
        return (byte)Math.Round(v * 255.0);
    }

    private static (double L, double A, double B) ToOklab((byte R, byte G, byte B) c)
    {
        double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static (byte, byte, byte) FromOklab(double L, double A, double B)
    {
        double l = Math.Pow(L + 0.3963377774 * A + 0.2158037573 * B, 3);
        double m = Math.Pow(L - 0.1055613458 * A - 0.0638541728 * B, 3);
        double s = Math.Pow(L - 0.0894841775 * A - 1.2914855480 * B, 3);
        return (Srgb(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
                Srgb(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
                Srgb(-0.0041960771 * l - 0.7034133414 * m + 1.7076074785 * s));
    }

    public static int Run(string packFolder, string outPath)
    {
        string folder = Path.GetFullPath(packFolder).TrimEnd('\\', '/');
        string log = Path.Combine(folder, DecisionLog.FileName);
        if (!File.Exists(log)) throw new FileNotFoundException($"No {DecisionLog.FileName} in {folder}: bake the pack again to make one.");
        List<DecisionLog.Row> rows = DecisionLog.Read(log);
        string name = Path.GetFileName(folder);
        string kind = name.Split('-')[0] switch
        {
            "terrain" => "Heights",
            "cover" => "Land cover",
            "surface" => "Distant colour",
            "water" => "Water",
            "detail" => "Detail",
            _ => name,
        };
        string measure = name.Split('-')[0] switch
        {
            "terrain" => "ground whose slope changes by more than detailSlope",
            "cover" => "ground drawn as a different kind of land",
            "surface" => "ground whose colour changes by more than colourTolerance",
            "water" => "ground that moves to the other side of the coast",
            "detail" => "ground whose detail changes by more than detailTolerance",
            _ => "ground that changes",
        };

        // Levels where the bake had a real choice; levels that are always stored add nothing to read.
        var levels = rows.GroupBy(r => r.Id.Level).Where(g => g.Any(r => r.Why == 'm'))
            .OrderBy(g => g.Key).ToList();
        if (levels.Count == 0) throw new InvalidDataException($"{log} has no measured tiles.");
        double limit = rows.First(r => r.LimitKm2 > 0).LimitKm2;

        TerrainPackSet heights = new();
        heights.AddPack(Path.Combine(Path.GetDirectoryName(folder)!, "terrain"));
        bool[] land = DetailPlan.LandMask(heights.TileCount > 0 ? heights : null, PanelWidth, PanelHeight);

        const int columns = 2;
        int panelRows = (levels.Count + columns - 1) / columns;
        int titleHeight = 70, legendHeight = 250;
        int width = Margin * 2 + columns * PanelWidth + (columns - 1) * Gap;
        int height = titleHeight + panelRows * (PanelHeight + LabelHeight + Gap) + legendHeight;
        var canvas = new Canvas(width, height, Background);
        canvas.Text(Margin, 12, $"{kind}: how much each tile changes the picture compared with the tile above   ({name})", Ink);
        canvas.Text(Margin, 40, $"Measured: {measure}, in sq km, divided by the limit of {limit:#,0.##} sq km. Over the limit, the tile is kept.", Faint);

        for (int i = 0; i < levels.Count; i++)
        {
            int px = Margin + (i % columns) * (PanelWidth + Gap);
            int py = titleHeight + (i / columns) * (PanelHeight + LabelHeight + Gap);
            DrawPanel(canvas, px, py + LabelHeight, levels[i].ToList(), land);
            int measured = levels[i].Count(), kept = levels[i].Count(r => r.Kept);
            int over = levels[i].Count(r => r.ChangedKm2 > r.LimitKm2), anyway = levels[i].Count(r => r.Kept && r.ChangedKm2 <= r.LimitKm2);
            double spacingM = 180.0 / (1 << levels[i].Key) / HeightTile.Intervals * 111_195.0;
            canvas.Text(px, py + 4, $"Level {levels[i].Key} ({spacingM:#,0} m): {measured:#,0} looked at, {over:#,0} over the limit, {anyway:#,0} kept anyway, {kept:#,0} kept", Ink);
        }

        int row = titleHeight + panelRows * (PanelHeight + LabelHeight + Gap) + 6;
        canvas.Text(Margin, row, "Measured change divided by the limit (log scale). Left of the line: left out. Right of it: kept.", Faint);
        row += 30;
        // A gradient bar from 1% to 100 times the limit, the limit marked in the middle.
        const int barWidth = 720, barHeight = 22;
        for (int x = 0; x < barWidth; x++)
        {
            double ratio = Math.Pow(10.0, -2.0 + 4.0 * (x + 0.5) / barWidth);
            canvas.FillRect(Margin + x, row, 1, barHeight, ColourOf(ratio));
        }

        canvas.FillRect(Margin + barWidth / 2 - 1, row - 4, 2, barHeight + 8, Ink);
        foreach (var (ratio, label) in new[] { (0.01, "1%"), (0.1, "10%"), (1.0, "the limit"), (10.0, "10 times"), (100.0, "100 times") })
        {
            int x = Margin + (int)Math.Round((Math.Log10(ratio) + 2.0) / 4.0 * barWidth);
            canvas.FillRect(Math.Min(x, Margin + barWidth - 1), row + barHeight, 1, 6, Faint);
            int w = label.Length * 7;
            canvas.Text(Math.Clamp(x - w / 2, Margin, Margin + barWidth - w), row + barHeight + 8, label, Ink);
        }

        row += barHeight + 40;

        canvas.FillRect(Margin, row, 34, 16, ColourOf(0.05));
        Stripe(canvas, Margin, row, 34, 16);
        canvas.Text(Margin + 46, row, "striped: kept anyway (a finer tile below it was kept, or the level is always stored)", Ink);
        row += 26;
        canvas.FillRect(Margin, row, 34, 16, Unmeasured);
        canvas.Text(Margin + 46, row, "not looked at: the tile above was not split here, or there is no data", Ink);
        row += 26;
        canvas.FillRect(Margin, row, 34, 16, Capped);
        canvas.Text(Margin + 46, row, "grey: never stored here (a detail area caps the level)", Ink);
        row += 26;
        canvas.Text(Margin, row + 4, "Sea is dimmed. Compare with the map of the deepest tile stored (terrain map): it is the deepest level kept at each place.", Faint);

        canvas.SavePng(outPath);
        Console.WriteLine($"Wrote {outPath} ({canvas.Width} x {canvas.Height}), {rows.Count:#,0} decisions over {levels.Count} levels");
        return 0;
    }

    /// <summary>
    /// terrain decisions --what-if: for each limit (as multiples of the pack's own), how many
    /// tiles a bake would keep and about how big the pack would be, worked out from decisions.csv
    /// without baking. Tiles kept whatever they measure (levels always stored, the first land cover
    /// level) stay kept; every other tile is kept if it measures over the new limit or a tile below
    /// it is kept. Sizes of tiles not stored today are the average stored tile size of their level.
    /// </summary>
    public static int WhatIf(string packFolder, double[] multipliers)
    {
        string folder = Path.GetFullPath(packFolder).TrimEnd('\\', '/');
        List<DecisionLog.Row> rows = DecisionLog.Read(Path.Combine(folder, DecisionLog.FileName));
        double limit = rows.First(r => r.LimitKm2 > 0).LimitKm2;
        var byId = rows.ToDictionary(r => r.Id);
        bool isCover = Path.GetFileName(folder).StartsWith("cover");
        int firstMeasured = rows.Where(r => r.Why == 'm').Min(r => r.Id.Level);

        // Today's stored tile sizes, and the average per level for tiles not stored today.
        var sizes = new Dictionary<TileId, long>();
        var perLevel = new Dictionary<int, (long Bytes, int Count)>();
        foreach (DecisionLog.Row r in rows.Where(r => r.Kept))
        {
            string file = Directory.EnumerateFiles(Path.Combine(folder, r.Id.Level.ToString(), r.Id.X.ToString()), $"{r.Id.Y}.*").FirstOrDefault();
            if (file == null) continue;
            long length = new FileInfo(file).Length;
            sizes[r.Id] = length;
            perLevel.TryGetValue(r.Id.Level, out var sum);
            perLevel[r.Id.Level] = (sum.Bytes + length, sum.Count + 1);
        }

        long SizeOf(TileId id) => sizes.TryGetValue(id, out long s) ? s
            : perLevel.TryGetValue(id.Level, out var avg) && avg.Count > 0 ? avg.Bytes / avg.Count : 0;

        int deepest = rows.Max(r => r.Id.Level);
        Console.WriteLine($"{Path.GetFileName(folder)}: limit today {limit:#,0.##} sq km");
        Console.WriteLine("  limit (sq km)   x today   tiles kept   change   about MB   " + string.Join("  ", Enumerable.Range(firstMeasured, deepest - firstMeasured + 1).Select(l => $"level {l,2}")));
        foreach (double m in multipliers)
        {
            double newLimit = limit * m;
            var kept = new HashSet<TileId>();
            // Deepest first, so a tile knows whether any tile below it is kept.
            foreach (DecisionLog.Row r in rows.OrderByDescending(r => r.Id.Level))
            {
                bool always = r.Id.Level < firstMeasured || (isCover && r.Id.Level == firstMeasured && r.Kept);
                bool keep = r.Why != 'c' && (always || r.ChangedKm2 > newLimit
                    || Enumerable.Range(0, 4).Any(i => kept.Contains(r.Id.Child(i & 1, i >> 1))));
                if (keep) kept.Add(r.Id);
            }

            long bytes = kept.Sum(SizeOf);
            int today = rows.Count(r => r.Kept);
            string levels = string.Join("  ", Enumerable.Range(firstMeasured, deepest - firstMeasured + 1).Select(l => $"{kept.Count(k => k.Level == l),8:#,0}"));
            Console.WriteLine($"  {newLimit,13:#,0.##} {m,9:0.##} {kept.Count,12:#,0} {100.0 * (kept.Count - today) / today,7:+0;-0;0}% {bytes / 1e6,10:#,0}   {levels}");
        }

        return 0;
    }

    /// <summary>
    /// terrain decisions --prune m: turns a COPY of a pack into the pack a bake with m times the
    /// limit would make, by deleting the tiles that limit would leave out. Exact for m >= 1: the
    /// tiles a higher limit keeps are the same files. Never run it on the pack the game uses.
    /// </summary>
    public static int Prune(string packFolder, double multiplier)
    {
        if (multiplier < 1.0) throw new ArgumentException("--prune only raises the limit (1 or more); a lower limit needs a bake.");
        string folder = Path.GetFullPath(packFolder).TrimEnd('\\', '/');
        List<DecisionLog.Row> rows = DecisionLog.Read(Path.Combine(folder, DecisionLog.FileName));
        double limit = rows.First(r => r.LimitKm2 > 0).LimitKm2 * multiplier;
        bool isCover = Path.GetFileName(folder).StartsWith("cover");
        int firstMeasured = rows.Where(r => r.Why == 'm').Min(r => r.Id.Level);
        var kept = new HashSet<TileId>();
        foreach (DecisionLog.Row r in rows.OrderByDescending(r => r.Id.Level))
        {
            bool always = r.Id.Level < firstMeasured || (isCover && r.Id.Level == firstMeasured && r.Kept);
            if (r.Why != 'c' && (always || r.ChangedKm2 > limit || Enumerable.Range(0, 4).Any(i => kept.Contains(r.Id.Child(i & 1, i >> 1))))) kept.Add(r.Id);
        }

        int removed = 0;
        foreach (DecisionLog.Row r in rows.Where(r => r.Kept && !kept.Contains(r.Id)))
        {
            string column = Path.Combine(folder, r.Id.Level.ToString(), r.Id.X.ToString());
            foreach (string file in Directory.EnumerateFiles(column, $"{r.Id.Y}.*"))
            {
                File.Delete(file);
                removed++;
            }
        }

        Console.WriteLine($"{folder}: removed {removed:#,0} tiles, the pack a limit of {limit:#,0.##} sq km ({multiplier:0.##} x) would make");
        return 0;
    }

    private static void DrawPanel(Canvas canvas, int left, int top, List<DecisionLog.Row> rows, bool[] land)
    {
        // Worst (highest) ratio per pixel, and whether that pixel's tile was kept for another reason.
        var ratio = new double[PanelWidth * PanelHeight];
        var other = new bool[PanelWidth * PanelHeight];
        var capped = new bool[PanelWidth * PanelHeight];
        Array.Fill(ratio, -1.0);
        foreach (DecisionLog.Row r in rows)
        {
            TileId id = r.Id;
            int x0 = (int)Math.Floor((id.West + 180.0) / 360.0 * PanelWidth), x1 = (int)Math.Ceiling((id.East + 180.0) / 360.0 * PanelWidth);
            int y0 = (int)Math.Floor((90.0 - id.North) / 180.0 * PanelHeight), y1 = (int)Math.Ceiling((90.0 - id.South) / 180.0 * PanelHeight);
            double value = r.ChangedKm2 < 0 ? -1.0 : r.ChangedKm2 / Math.Max(r.LimitKm2, 1e-9);
            for (int y = Math.Max(y0, 0); y < Math.Min(y1, PanelHeight); y++)
            {
                for (int x = Math.Max(x0, 0); x < Math.Min(x1, PanelWidth); x++)
                {
                    int at = y * PanelWidth + x;
                    if (r.Why == 'c') { capped[at] = true; continue; }
                    if (value > ratio[at]) ratio[at] = value;
                    if (r.Kept && r.Why != 'm' && value < 1.0) other[at] = true;
                }
            }
        }

        for (int y = 0; y < PanelHeight; y++)
        {
            for (int x = 0; x < PanelWidth; x++)
            {
                int at = y * PanelWidth + x;
                var colour = ratio[at] >= 0 ? ColourOf(ratio[at]) : capped[at] ? Capped : Unmeasured;
                // Stripes every 6 pixels on tiles kept for a reason other than the measure.
                if (other[at] && ratio[at] < 1.0 && (x + y) % 6 < 2) colour = (200, 205, 215);
                canvas.Set(left + x, top + y, colour);
                if (!land[at]) canvas.Blend(left + x, top + y, (14, 17, 24), 0.55);
            }
        }
    }

    private static void Stripe(Canvas canvas, int left, int top, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if ((x + y) % 6 < 2) canvas.Set(left + x, top + y, (200, 205, 215));
            }
        }
    }
}
