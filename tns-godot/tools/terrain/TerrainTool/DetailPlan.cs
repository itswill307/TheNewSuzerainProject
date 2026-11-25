using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

/// <summary>
/// Works out what a pack would look like under a recipe's detail settings without baking it, and
/// draws maps of the detail level stored at each place.
///
/// The prediction is made from an existing pack: every tile is measured once against the tile
/// above it (the result is kept in detail_stats.csv inside the pack), after which trying
/// different settings takes a second. It can only predict settings that keep less than the
/// existing pack does, since it cannot know about tiles that were never stored.
/// </summary>
public static class DetailPlan
{
    private const string StatsFile = "detail_stats.csv";

    /// <summary>Slope changes the stats are recorded at. Settings in between are interpolated.</summary>
    private static readonly float[] Slopes =
        { 0.01f, 0.015f, 0.02f, 0.025f, 0.03f, 0.035f, 0.04f, 0.05f, 0.06f, 0.08f, 0.1f, 0.15f };

    private sealed record TileStats(TileId Id, long Bytes, double Rms, double[] Over);

    /// <summary>terrain map: the detail stored in a pack as it stands.</summary>
    public static int MapOfPack(string packFolder, string mapPath)
    {
        // Any kind of pack: heights, land cover, colour, water or detail, told by its files.
        string folder = Path.GetFullPath(packFolder).TrimEnd('\\', '/');
        string extension = new[]
            {
                HeightTile.FileExtension, SurfaceTile.CoverExtension, SurfaceTile.ColourExtension,
                SurfaceTile.WaterExtension, SurfaceTile.DetailExtension,
            }
            .FirstOrDefault(ext => Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*" + ext, SearchOption.AllDirectories).Any());
        if (extension == null) throw new IOException($"No terrain tiles in {packFolder}");
        var packs = new TerrainPackSet(extension);
        packs.AddPack(folder);

        // The land outline comes from heights: this pack, or the height pack beside it.
        TerrainPackSet heights = packs;
        if (extension != HeightTile.FileExtension)
        {
            heights = new TerrainPackSet();
            heights.AddPack(Path.Combine(Path.GetDirectoryName(folder)!, "terrain"));
            if (heights.TileCount == 0) heights = null;
        }

        string kind = extension switch
        {
            HeightTile.FileExtension => "Heights",
            SurfaceTile.CoverExtension => "Land cover",
            SurfaceTile.ColourExtension => "Distant colour",
            SurfaceTile.WaterExtension => "Water",
            _ => "Detail",
        };
        var kept = packs.AllTiles.ToDictionary(id => id, id => new FileInfo(packs.PathOf(id)).Length);
        DrawMap(mapPath, heights, kept, $"{kind}: the deepest tile stored at each place   ({Path.GetFileName(folder)})");
        PrintTable(kept, null);
        return 0;
    }

    /// <summary>terrain plan: what a recipe's detail settings would keep of an existing pack.</summary>
    public static int Run(string packFolder, Recipe recipe, string mapPath, string statsPath, int threads)
    {
        var packs = new TerrainPackSet();
        packs.AddPack(packFolder);
        if (packs.TileCount == 0) throw new IOException($"No terrain tiles in {packFolder}");
        List<TileStats> stats = statsPath != null ? ReadStats(statsPath) : Measure(packs, packFolder, threads);
        if (stats.Count == 0) throw new InvalidDataException("No tile measurements could be read.");
        var rule = new DetailRule(recipe);

        var kept = new Dictionary<TileId, long>();
        var hasKeptChild = new HashSet<TileId>();
        foreach (TileStats tile in stats.OrderByDescending(t => t.Id.Level))
        {
            if (rule.Capped(tile.Id)) continue;
            bool keep = tile.Id.Level == 0
                        || hasKeptChild.Contains(tile.Id)
                        || rule.Forced(tile.Id)
                        || rule.Visible(tile.Id, FractionAt(tile, recipe.DetailSlope));
            if (!keep) continue;
            kept[tile.Id] = tile.Bytes;
            if (tile.Id.Level > 0) hasKeptChild.Add(tile.Id.Parent);
        }

        Console.WriteLine($"Detail kept where the slope changes by more than {recipe.DetailSlope} " +
                          $"over more than {recipe.DetailAreaKm2:0} sq km of ground" +
                          (recipe.DetailAreas.Count > 0 ? $", with {recipe.DetailAreas.Count} hand-set areas:" : ":"));
        PrintTable(kept, stats);
        if (mapPath != null)
        {
            DrawMap(mapPath, packs, kept,
                $"Planned terrain detail: slope {recipe.DetailSlope.ToString(CultureInfo.InvariantCulture)}, " +
                $"over {recipe.DetailAreaKm2:0} sq km");
        }

        return 0;
    }

    /// <summary>terrain plan --list: tiles at one level in order of how much they change the picture.</summary>
    public static int List(string packFolder, int level, double slope, int count, int threads)
    {
        var packs = new TerrainPackSet();
        packs.AddPack(packFolder);
        var tiles = Measure(packs, packFolder, threads).Where(t => t.Id.Level == level)
            .OrderBy(t => FractionAt(t, slope)).ToList();
        if (tiles.Count == 0) throw new ArgumentException($"The pack has no tiles at level {level}.");
        Console.WriteLine($"Level {level}: share of each tile whose slope changes by more than {slope}, across {tiles.Count} tiles");
        for (int i = 0; i < count; i++)
        {
            TileStats tile = tiles[(int)((tiles.Count - 1) * (double)i / Math.Max(count - 1, 1))];
            Console.WriteLine($"  {FractionAt(tile, slope),6:P1}  tile {tile.Id,-12} centre {tile.Id.South + tile.Id.SizeDeg * 0.5,7:0.00}, " +
                              $"{tile.Id.West + tile.Id.SizeDeg * 0.5,8:0.00}   typical change {tile.Rms:0.000}");
        }

        return 0;
    }

    private static double FractionAt(TileStats tile, double slope)
    {
        if (slope <= Slopes[0]) return tile.Over[0];
        for (int i = 1; i < Slopes.Length; i++)
        {
            if (slope > Slopes[i]) continue;
            double t = (Math.Log(slope) - Math.Log(Slopes[i - 1])) / (Math.Log(Slopes[i]) - Math.Log(Slopes[i - 1]));
            return tile.Over[i - 1] + (tile.Over[i] - tile.Over[i - 1]) * t;
        }

        return tile.Over[^1];
    }

    private static List<TileStats> Measure(TerrainPackSet packs, string packFolder, int threads)
    {
        string cache = Path.Combine(packFolder, StatsFile);
        string packInfo = Path.Combine(packFolder, "pack.json");
        if (File.Exists(cache) && (!File.Exists(packInfo) || File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(packInfo)))
        {
            List<TileStats> cached = ReadStats(cache);
            if (cached.Count == packs.TileCount) return cached;
        }

        Console.WriteLine($"Measuring {packs.TileCount} tiles (once per pack)...");
        var results = new ConcurrentBag<TileStats>();
        var byParent = packs.AllTiles.Where(id => id.Level > 0).GroupBy(id => id.Parent).ToList();
        int done = 0;
        Parallel.ForEach(byParent, new ParallelOptions { MaxDegreeOfParallelism = threads }, group =>
        {
            HeightTile parent = packs.Load(group.Key)
                                ?? throw new InvalidDataException($"Tile {group.Key} is missing although tiles below it exist.");
            float[] upsampled = DetailMeasure.Upsample(parent.Heights);
            foreach (TileId id in group)
            {
                var over = new double[Slopes.Length];
                double rms = DetailMeasure.SlopeChange(upsampled, packs.Load(id).Heights, id, Slopes, over);
                results.Add(new TileStats(id, new FileInfo(packs.PathOf(id)).Length, rms, over));
            }

            int now = System.Threading.Interlocked.Add(ref done, group.Count());
            if (now % 500 < group.Count()) Console.Write($"\r  {now} tiles   ");
        });
        Console.WriteLine();

        foreach (TileId top in packs.AllTiles.Where(id => id.Level == 0))
        {
            results.Add(new TileStats(top, new FileInfo(packs.PathOf(top)).Length, 0.0, Slopes.Select(_ => 1.0).ToArray()));
        }

        var stats = results.OrderBy(t => t.Id.Level).ThenBy(t => t.Id.X).ThenBy(t => t.Id.Y).ToList();
        using (var writer = new StreamWriter(cache))
        {
            writer.WriteLine("level,x,y,bytes,rms," + string.Join(",", Slopes.Select(s => "over" + s.ToString(CultureInfo.InvariantCulture))));
            foreach (TileStats tile in stats)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{tile.Id.Level},{tile.Id.X},{tile.Id.Y},{tile.Bytes},{tile.Rms:0.#####},") +
                    string.Join(",", tile.Over.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture))));
            }
        }

        return stats;
    }

    private static List<TileStats> ReadStats(string path)
    {
        var stats = new List<TileStats>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] parts = line.Split(',');
            if (parts.Length != 5 + Slopes.Length) return new List<TileStats>();
            var id = new TileId(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
            double[] over = parts.Skip(5).Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            stats.Add(new TileStats(id, long.Parse(parts[3]), double.Parse(parts[4], CultureInfo.InvariantCulture), over));
        }

        return stats;
    }

    private static double SpacingM(int level) => 180.0 / (1 << level) / HeightTile.Intervals * 111_195.0;

    private static void PrintTable(Dictionary<TileId, long> kept, List<TileStats> all)
    {
        Console.WriteLine(all == null
            ? "  level  heights every     tiles        size"
            : "  level  heights every     tiles        size      now stored");
        foreach (var level in kept.GroupBy(pair => pair.Key.Level).OrderBy(g => g.Key))
        {
            string now = "";
            if (all != null)
            {
                var current = all.Where(t => t.Id.Level == level.Key).ToList();
                now = $"   {current.Count,7} tiles {current.Sum(t => t.Bytes) / 1e6,7:0.0} MB";
            }

            Console.WriteLine($"  {level.Key,5} {SpacingM(level.Key),12:#,0} m {level.Count(),9} {level.Sum(p => p.Value) / 1e6,8:0.0} MB{now}");
        }

        string totalNow = all == null ? "" : $"   {all.Count,7} tiles {all.Sum(t => t.Bytes) / 1e6,7:0.0} MB";
        Console.WriteLine($"  total {"",14} {kept.Count,9} {kept.Values.Sum() / 1e6,8:0.0} MB{totalNow}");
    }

    // ---- the map ----

    private static readonly (byte, byte, byte)[] LevelColors =
    {
        (62, 68, 86), (62, 68, 86), (62, 68, 86), (62, 68, 86), (62, 68, 86), // 0-4
        (66, 92, 168),   // 5
        (38, 150, 170),  // 6
        (84, 190, 112),  // 7
        (244, 208, 63),  // 8
        (236, 88, 48),   // 9
        (214, 60, 160),  // 10 and deeper
    };

    private static (byte, byte, byte) ColorOf(int level) => LevelColors[Math.Clamp(level, 0, LevelColors.Length - 1)];

    /// <summary>
    /// One cell per level 9 tile (about 0.35 degrees), coloured by the deepest tile stored there.
    /// Land is drawn at full strength and sea dimmed, so the continents can be made out.
    /// </summary>
    private static void DrawMap(string path, TerrainPackSet packs, Dictionary<TileId, long> kept, string title)
    {
        const int cellLevel = 9, cellPixels = 2, margin = 24, titleHeight = 44;
        int columns = TileId.ColumnsAt(cellLevel), rows = TileId.RowsAt(cellLevel);
        int mapWidth = columns * cellPixels, mapHeight = rows * cellPixels;

        var deepest = new byte[columns * rows];
        foreach (TileId id in kept.Keys)
        {
            int shift = cellLevel - id.Level;
            int x0 = shift >= 0 ? id.X << shift : id.X >> -shift, y0 = shift >= 0 ? id.Y << shift : id.Y >> -shift;
            int span = shift >= 0 ? 1 << shift : 1;
            for (int y = y0; y < y0 + span; y++)
            {
                for (int x = x0; x < x0 + span; x++)
                {
                    if (deepest[y * columns + x] < id.Level) deepest[y * columns + x] = (byte)id.Level;
                }
            }
        }

        bool[] land = LandMask(packs, mapWidth, mapHeight);
        var levels = kept.GroupBy(pair => pair.Key.Level).OrderByDescending(g => g.Key).ToList();
        var legend = levels.Where(g => g.Key >= 5).ToList();
        int legendHeight = (legend.Count + 4) * 26 + 40;
        var canvas = new Canvas(mapWidth + margin * 2, titleHeight + mapHeight + legendHeight + margin, (20, 22, 28));
        (byte, byte, byte) ink = (226, 228, 235), faint = (150, 155, 168), sea = (14, 17, 24);
        canvas.Text(margin, 14, title, ink);

        for (int py = 0; py < mapHeight; py++)
        {
            int cellY = rows - 1 - py / cellPixels;
            for (int px = 0; px < mapWidth; px++)
            {
                int x = margin + px, y = titleHeight + py;
                canvas.Set(x, y, ColorOf(deepest[cellY * columns + px / cellPixels]));
                if (!land[py * mapWidth + px]) canvas.Blend(x, y, sea, 0.62);
            }
        }

        // Lines every 30 degrees.
        for (int degrees = 30; degrees < 360; degrees += 30)
        {
            int x = margin + (int)(degrees / 360.0 * mapWidth);
            for (int py = 0; py < mapHeight; py++) canvas.Blend(x, titleHeight + py, ink, 0.16);
            if (degrees >= 180) continue;
            int y = titleHeight + (int)(degrees / 180.0 * mapHeight);
            for (int px = 0; px < mapWidth; px++) canvas.Blend(margin + px, y, ink, 0.16);
        }

        int row = titleHeight + mapHeight + 18;
        canvas.Text(margin, row, "One cell is one level 9 tile, about 39 km north to south. Sea is dimmed.", faint);
        row += 30;
        // Columns: level, spacing, tiles, size. The font is not fixed width, so each sits at its own x.
        int c1 = margin + 46, c2 = c1 + 200, c3 = c2 + 230, c4 = c3 + 130;
        canvas.Text(c1, row, "Level", faint);
        canvas.Text(c2, row, "One sample every", faint);
        canvas.Text(c3, row, "Tiles", faint);
        canvas.Text(c4, row, "Size", faint);
        row += 26;
        var coarse = levels.Where(g => g.Key < 5).ToList();
        var rowsOut = legend.Select(level => (Colour: ColorOf(level.Key), Name: $"{level.Key}", Spacing: $"{SpacingM(level.Key):#,0} m",
                Tiles: level.Count(), Bytes: level.Sum(p => p.Value)))
            .Append((Colour: ColorOf(4), Name: "4 or coarser", Spacing: $"{SpacingM(4):#,0} m or more", Tiles: coarse.Sum(g => g.Count()), Bytes: coarse.Sum(g => g.Sum(p => p.Value))));
        foreach (var line in rowsOut)
        {
            canvas.FillRect(margin, row, 34, 16, line.Colour);
            canvas.Text(c1, row, line.Name, ink);
            canvas.Text(c2, row, line.Spacing, ink);
            canvas.Text(c3, row, $"{line.Tiles:#,0}", ink);
            canvas.Text(c4, row, $"{line.Bytes / 1e6:#,0.0} MB", ink);
            row += 26;
        }

        canvas.Text(c1, row + 6, $"Whole pack: {kept.Count:#,0} tiles, {kept.Values.Sum() / 1e6:#,0} MB", ink);

        canvas.SavePng(path);
        Console.WriteLine($"Wrote {path} ({canvas.Width} x {canvas.Height})");
    }

    /// <summary>True where the pack's ground is above sea level, one entry per map pixel (row 0 north).</summary>
    internal static bool[] LandMask(TerrainPackSet packs, int width, int height)
    {
        const int level = 5;
        var land = new bool[width * height];
        if (packs == null)
        {
            Array.Fill(land, true);
            return land;
        }

        var loaded = new Dictionary<TileId, HeightTile>();
        int tilePixels = width / TileId.ColumnsAt(level);

        for (int tileY = 0; tileY < TileId.RowsAt(level); tileY++)
        {
            for (int tileX = 0; tileX < TileId.ColumnsAt(level); tileX++)
            {
                var id = new TileId(level, tileX, tileY);
                TileId source = id;
                while (!packs.Has(source) && source.Level > 0) source = source.Parent;
                if (!packs.Has(source)) continue;
                if (!loaded.TryGetValue(source, out HeightTile tile))
                {
                    tile = packs.Load(source);
                    // Coarse tiles are shared by many cells; fine ones are used once.
                    if (source.Level < level) loaded[source] = tile;
                }

                for (int j = 0; j < tilePixels; j++)
                {
                    double latitude = id.South + id.SizeDeg * (j + 0.5) / tilePixels;
                    double sourceRow = (latitude - source.South) / source.SizeDeg * HeightTile.Intervals;
                    int py = height - 1 - (tileY * tilePixels + j);
                    for (int i = 0; i < tilePixels; i++)
                    {
                        double longitude = id.West + id.SizeDeg * (i + 0.5) / tilePixels;
                        double sourceColumn = (longitude - source.West) / source.SizeDeg * HeightTile.Intervals;
                        land[py * width + tileX * tilePixels + i] = tile.SampleBilinear(sourceColumn, sourceRow) > 0.5f;
                    }
                }
            }
        }

        return land;
    }
}
