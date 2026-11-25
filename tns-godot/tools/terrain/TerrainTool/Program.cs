using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

public static class Program
{
    private const string Usage = """
        Terrain tool for The New Suzerain.

          terrain bake <recipe.json> [--clean] [--output folder] [--threads N]
              Build a terrain pack from the elevation sources listed in a recipe.
              --clean removes the tiles already in the output folder first.
              --output writes somewhere other than the recipe says, so the pack in use stays
              usable until the new one is finished.

          terrain surface <surface recipe.json> [--clean] [--recolour] [--region w,s,e,n] [--update w,s,e,n] [--threads N]
              Paint the ground: build the land cover, colour and water packs from land cover data
              and a colour picture. --recolour measures the colours of each kind of ground again.
              --region only paints that box, into empty folders, to try settings quickly.
              --update rebakes that box (widened to whole 2.8 degree tiles) into the existing
              packs, after a change to the data or settings there, and leaves the rest alone.

          terrain preview <data folder> <out.png> <west> <south> <east> <north>
                          [--heights pack] [--width 1600] [--relief 0.5]
              Draw a region from the cover, surface and water packs in a folder (such as
              data/base), lit like the game, without starting it.

          terrain info <file.tif | tile.tnsh | pack folder>
              Describe an elevation file, a single tile, or a whole pack.

          terrain height <pack folder> <latitude> <longitude>
              Print the ground height a pack gives at a place.

          terrain export <pack folder> <out.tif> <west> <south> <east> <north> [--level N]
              Save a region of a pack as a GeoTIFF height image, to inspect or edit.

          terrain rugged <pack folder> <out.txt> [--squares 2000] [--min-lat -60]
              List the steepest 1 x 1 degree squares, the ones that deserve finer elevation data.
              Feed the list to download_copernicus90.ps1.

          terrain map <pack folder> <out.png>
              Draw a world map of the detail level stored at each place.

          terrain decisions <pack folder> <out.png>
              Draw, level by level, how much each tile changed the picture against the tile
              above it, divided by the limit, from the decisions.csv the bake left in the pack.

          terrain plan <pack folder> <recipe.json> [--map out.png] [--slope S] [--area KM2] [--stats file.csv]
              Predict the pack a recipe's detail settings would give, from an existing pack that
              was baked with looser settings. Takes seconds, where a bake takes half an hour.
              --slope and --area try other values without editing the recipe.
              --list LEVEL shows example tiles at that level, from least to most detail.
              --stats uses measurements saved from another pack (its detail_stats.csv), to
              try settings that keep more than the pack now in place.

          terrain selftest
              Check the tool's own reading and writing code.
        """;

    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 0)
        {
            Console.WriteLine(Usage);
            return 1;
        }

        try
        {
            var positional = args.Skip(1).Where(a => !a.StartsWith("--")).ToList();
            switch (args[0].ToLowerInvariant())
            {
                case "bake": return Bake(args, positional);
                case "info": return Info(positional);
                case "height": return Height(positional);
                case "export": return Export(args, positional);
                case "rugged":
                    if (positional.Count < 2) throw new ArgumentException("rugged needs a pack folder and an output file.");
                    return RuggedCells.Run(
                        positional[0], positional[1],
                        int.TryParse(Option(args, "--squares"), out int squares) ? squares : 2000,
                        double.TryParse(Option(args, "--min-lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out double minLat) ? minLat : -60.0,
                        int.TryParse(Option(args, "--level"), out int ruggedLevel) ? ruggedLevel : null);
                case "map":
                    if (positional.Count < 2) throw new ArgumentException("map needs a pack folder and an output .png.");
                    return DetailPlan.MapOfPack(positional[0], positional[1]);
                case "decisions":
                    if (Option(args, "--prune") is { } prune)
                    {
                        if (positional.Count < 1) throw new ArgumentException("decisions --prune needs a pack folder (a copy, not the game's pack).");
                        return DecisionMap.Prune(positional[0], Number(prune));
                    }

                    if (Option(args, "--what-if") is { } whatIf)
                    {
                        if (positional.Count < 1) throw new ArgumentException("decisions --what-if needs a pack folder (with decisions.csv).");
                        return DecisionMap.WhatIf(positional[0], whatIf.Split(',').Select(Number).ToArray());
                    }

                    if (positional.Count < 2) throw new ArgumentException("decisions needs a pack folder (with decisions.csv) and an output .png.");
                    return DecisionMap.Run(positional[0], positional[1]);
                case "bench":
                    if (positional.Count < 1) throw new ArgumentException("bench needs a terrain, cover or water pack folder.");
                    return CompressionBench(positional[0], int.TryParse(Option(args, "--count"), out int benchCount) ? benchCount : 400);
                case "plan": return Plan(args, positional);
                case "surface": return Surface(args, positional);
                case "bland": return Bland(args, positional);
                case "compare": return Compare(positional);
                case "blandmap": return BlandMapCommand(args, positional);
                case "groundtex": return GroundTexturesCommand(args, positional);
                case "samplepick":
                {
                    // terrain samplepick <worldcover folder> <sites.csv> <out.csv> [--size-km 88]
                    // [--max-water 0.0015] [--max-built 0.05] (SamplePick.cs)
                    if (positional.Count < 3) throw new ArgumentException("samplepick needs: <worldcover folder> <sites.csv> <out.csv>.");
                    using var cover = new LandCover(positional[0], 512L << 20);
                    var picks = SamplePick.Run(cover, SamplePick.ReadSites(positional[1]),
                        Option(args, "--size-km") is { } km ? Number(km) : 88,
                        Option(args, "--max-water") is { } w ? Number(w) : 0.0015,
                        Option(args, "--max-built") is { } b ? Number(b) : 0.05);
                    SamplePick.Save(positional[2], picks);
                    return 0;
                }
                case "preview":
                    if (positional.Count < 6) throw new ArgumentException("preview needs: <data folder> <out.png> <west> <south> <east> <north>.");
                    if (args.Contains("--blocky")) SurfacePreview.Smooth = false;
                    if (args.Contains("--clamp-edges")) SurfacePreview.ClampEdges = true;
                    if (Option(args, "--softness") is { } softness) SurfacePreview.Softness = Number(softness);
                    if (Option(args, "--sharpness") is { } sharpness) SurfacePreview.Sharpness = Number(sharpness);
                    return SurfacePreview.Run(positional[0], positional[1],
                        Number(positional[2]), Number(positional[3]), Number(positional[4]), Number(positional[5]),
                        int.TryParse(Option(args, "--width"), out int previewWidth) ? previewWidth : 1600,
                        Option(args, "--relief") is { } relief ? Number(relief) : 0.5,
                        Option(args, "--heights"));
                case "selftest": return SelfTest.Run();
                default:
                    Console.WriteLine(Usage);
                    return 1;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 2;
        }
    }

    private static string Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static int Bake(string[] args, List<string> positional)
    {
        if (positional.Count < 1) throw new ArgumentException("bake needs a recipe file.");
        // An option's value is not a positional argument.
        Recipe recipe = Recipe.Load(positional[0]);
        if (Option(args, "--output") is { } elsewhere) recipe.Output = Path.GetFullPath(elsewhere);
        string output = recipe.Resolve(recipe.Output);
        bool clean = args.Contains("--clean");
        int threads = int.TryParse(Option(args, "--threads"), out int t) ? t : Math.Max(1, Environment.ProcessorCount - 2);

        if (Directory.Exists(output))
        {
            var levelFolders = Directory.EnumerateDirectories(output)
                .Where(folder => int.TryParse(Path.GetFileName(folder), out _)).ToList();
            if (levelFolders.Count > 0 && !clean)
            {
                throw new IOException(
                    $"{output} already holds terrain tiles. Add --clean to replace them, or bake to another folder.");
            }

            foreach (string folder in levelFolders) Directory.Delete(folder, recursive: true);
            File.Delete(Path.Combine(output, "detail_stats.csv"));
        }

        // Decoded source blocks kept in memory. Elevation files are read in a scattered order,
        // so a generous cache saves a great deal of repeated decompression.
        var cache = new BlockCache(2L << 30);
        var layers = new List<IElevationSource>();
        foreach (SourceSettings settings in recipe.Sources)
        {
            IElevationSource layer;
            if (!string.IsNullOrEmpty(settings.Pack))
            {
                layer = new PackElevationSource(recipe.Resolve(settings.Pack));
            }
            else
            {
                string path = recipe.Resolve(settings.Path ?? throw new InvalidDataException("A source needs a \"path\" or a \"pack\"."));
                if (Directory.Exists(path)) layer = new TiffFolderSource(path, cache, settings);
                else if (File.Exists(path)) layer = new TiffElevationSource(path, cache, settings);
                else throw new IOException($"Source not found: {path}");
            }

            if (!string.IsNullOrWhiteSpace(settings.Squares))
            {
                layer = new SquareLimitedSource(layer, SquareLimitedSource.ReadSquares(recipe.Resolve(settings.Squares)));
            }

            string files = layer is TiffFolderSource folder ? $", {folder.FileCount} files" : "";
            Console.WriteLine($"  source: {layer.Name}{files}, lon {layer.West:0.##}..{layer.East:0.##}, " +
                              $"lat {layer.South:0.##}..{layer.North:0.##}, detail to level {layer.NativeLevel}");
            layers.Add(layer);
        }

        new Baker(recipe, new LayeredSource(layers)).Run(threads);
        foreach (IElevationSource layer in layers) (layer as IDisposable)?.Dispose();
        return 0;
    }

    private static double Number(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>terrain groundtex: the close-up ground textures, from satellite samples (GroundTextures.cs).</summary>
    private static int GroundTexturesCommand(string[] args, List<string> positional)
    {
        if (positional.Count < 2) throw new ArgumentException("groundtex needs: <samples folder of PNGs> <out folder>.");
        var settings = new GroundTextures.Settings
        {
            BlurM = Option(args, "--blur-m") is { } b ? Number(b) : 500,
            PixelM = Option(args, "--pixel-m") is { } p ? Number(p) : 43,
            Chroma = Option(args, "--chroma") is { } c ? Number(c) : 0.5,
            Soften = Option(args, "--soften") is { } so ? Number(so) : 2.5,
        };
        // --pick tree=tree_congo,bare=bare_dunes: which sample each kind uses (default: the first).
        var pick = (Option(args, "--pick") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split('=')).ToDictionary(s => s[0], s => s[1]);
        GroundTextures.Run(positional[0], positional[1], settings, pick);
        // --all <folder>: also every sample, named after it, for the regional versions.
        // --blur-for bare_sand_sea=2000: a larger blur for named samples, to keep a coarser pattern.
        var blurFor = (Option(args, "--blur-for") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split('=')).ToDictionary(s => s[0], s => Number(s[1]));
        if (Option(args, "--all") is { } all) GroundTextures.RunAll(positional[0], all, settings, blurFor);
        return 0;
    }

    private static int BlandMapCommand(string[] args, List<string> positional)
    {
        if (positional.Count < 2) throw new ArgumentException("blandmap needs: <survey.csv> <out.png>.");
        var cells = BlandSurvey.Load(positional[0]);
        var settings = new BlandMap.Settings
        {
            Bland = Option(args, "--bland") is { } b ? Number(b) : 0.7,
            DuneRippleM = Option(args, "--dune") is { } d ? Number(d) : 1.5,
            ColourGain = Option(args, "--gain") is { } g ? Number(g) : 0.2,
            MinPatches = Option(args, "--min-patches") is { } mp ? int.Parse(mp, CultureInfo.InvariantCulture) : 0,
        };
        double[][] boxes = Option(args, "--measured")?.Split(';').Select(box => box.Split(',').Select(Number).ToArray()).ToArray() ?? Array.Empty<double[]>();
        BlandMap.Draw(positional[1], cells, settings, boxes, Option(args, "--title") ?? "Where the ground looks bland up close, and what would fix it");
        foreach (var group in cells.GroupBy(c => BlandMap.Classify(c, settings)).OrderBy(g => g.Key))
        {
            Console.WriteLine($"  {group.Key,-12} {group.Count(),6} squares   " +
                string.Join(", ", group.GroupBy(c => Cover.Names[c.Kind]).OrderByDescending(k => k.Count()).Take(4).Select(k => $"{k.Key} {k.Count()}")));
        }

        // --list <out.csv>: the squares the sharper picture fixes (Colour or Both), for detailFineSquares.
        if (Option(args, "--list") is { } list)
        {
            var fixedByColour = cells.Where(c => BlandMap.Classify(c, settings) is BlandMap.Fix.Colour or BlandMap.Fix.Both).ToList();
            File.WriteAllLines(list, new[] { $"# Bland squares the 500 m picture makes less smooth (terrain blandmap {Path.GetFileName(positional[0])} --list)." }
                .Concat(fixedByColour.Select(c => $"{c.Lat},{c.Lon}")));
            Console.WriteLine($"  wrote {fixedByColour.Count} squares to {list}");
        }

        return 0;
    }

    /// <summary>terrain compare: how two height packs differ, tile by tile.</summary>
    private static int Compare(List<string> positional)
    {
        if (positional.Count < 2) throw new ArgumentException("compare needs two pack folders.");
        string surfaceExtension = new[] { SurfaceTile.ColourExtension, SurfaceTile.WaterExtension, SurfaceTile.CoverExtension, SurfaceTile.DetailExtension }
            .FirstOrDefault(e => Directory.EnumerateFiles(positional[0], "*" + e, SearchOption.AllDirectories).Any());
        if (surfaceExtension != null) return CompareSurface(positional[0], positional[1], surfaceExtension);
        var a = new TerrainPackSet();
        var b = new TerrainPackSet();
        a.AddPack(positional[0]);
        b.AddPack(positional[1]);
        var both = a.AllTiles.Where(b.Has).ToList();
        int onlyA = a.TileCount - both.Count, onlyB = b.TileCount - both.Count;
        double worst = 0, sum = 0;
        long samples = 0, differing = 0;
        TileId worstTile = default;
        object gate = new();
        System.Threading.Tasks.Parallel.ForEach(both, id =>
        {
            float[] x = a.Load(id).Heights, y = b.Load(id).Heights;
            double tileWorst = 0, tileSum = 0;
            long tileDiffering = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double d = Math.Abs(x[i] - y[i]);
                tileSum += d;
                if (d > 0) tileDiffering++;
                if (d > tileWorst) tileWorst = d;
            }

            lock (gate)
            {
                sum += tileSum;
                samples += x.Length;
                differing += tileDiffering;
                if (tileWorst > worst) { worst = tileWorst; worstTile = id; }
            }
        });
        Console.WriteLine($"{both.Count:#,0} tiles in both, {onlyA:#,0} only in the first, {onlyB:#,0} only in the second");
        Console.WriteLine($"heights: {100.0 * differing / Math.Max(samples, 1):0.00}% of samples differ, by {sum / Math.Max(samples, 1):0.000} m on average, at most {worst:0.0} m (tile {worstTile})");
        return 0;
    }

    /// <summary>
    /// terrain bench: re-compresses a sample of a pack's tiles at each zlib effort and reports
    /// the time and size of each, to choose the effort the bakes use.
    /// </summary>
    private static int CompressionBench(string pack, int count)
    {
        string[] files = new[] { HeightTile.FileExtension, SurfaceTile.CoverExtension, SurfaceTile.WaterExtension }
            .SelectMany(e => Directory.EnumerateFiles(pack, "*" + e, SearchOption.AllDirectories)).ToArray();
        if (files.Length == 0) throw new IOException($"No height, cover or water tiles in {pack}.");
        var random = new Random(1);
        string[] sample = files.OrderBy(_ => random.Next()).Take(count).ToArray();
        var levels = new[] { System.IO.Compression.CompressionLevel.SmallestSize, System.IO.Compression.CompressionLevel.Optimal, System.IO.Compression.CompressionLevel.Fastest };
        Console.WriteLine($"{sample.Length} tiles from {pack}: bytes on disk today {sample.Sum(f => new FileInfo(f).Length) / 1e6:0.00} MB");
        foreach (var level in levels)
        {
            long bytes = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            foreach (string file in sample)
            {
                byte[] data = File.ReadAllBytes(file);
                if (file.EndsWith(HeightTile.FileExtension))
                {
                    HeightTile tile = HeightTile.Decode(data);
                    bytes += tile.Encode(BitConverter.ToSingle(data, 8), level).Length;
                }
                else
                {
                    SurfaceTile tile = SurfaceTile.Decode(data);
                    bytes += tile.Encode(null, (data[5] & 4) == 0, level).Length;
                }
            }

            double ms = timer.Elapsed.TotalMilliseconds;
            Console.WriteLine($"  {level,-12} {ms / sample.Length,7:0.0} ms a tile  {bytes / 1e6,7:0.00} MB");
        }

        return 0;
    }

    /// <summary>
    /// terrain compare on two cover, colour, water or detail packs: which tiles only one holds, and
    /// for the rest how far their samples differ (0-255 per channel), level by level.
    /// </summary>
    private static int CompareSurface(string first, string second, string extension)
    {
        static Dictionary<string, string> Files(string pack, string extension) =>
            Directory.EnumerateFiles(pack, "*" + extension, SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(pack, f).Replace('\\', '/'), f => f);

        var a = Files(first, extension);
        var b = Files(second, extension);
        string waterBeside = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(first).TrimEnd('/', '\\'))!, "water");
        var both = a.Keys.Where(b.ContainsKey).ToList();
        var levels = new SortedDictionary<int, (long Both, long OnlyA, long OnlyB, long Same, long Samples, long Over4, double Worst)>();
        void Add(string key, Func<(long, long, long, long, long, long, double), (long, long, long, long, long, long, double)> change)
        {
            int level = int.Parse(key.Split('/')[0]);
            lock (levels)
            {
                levels.TryGetValue(level, out var v);
                levels[level] = change(v);
            }
        }

        foreach (string key in a.Keys.Where(k => !b.ContainsKey(k))) Add(key, v => (v.Item1, v.Item2 + 1, v.Item3, v.Item4, v.Item5, v.Item6, v.Item7));
        foreach (string key in b.Keys.Where(k => !a.ContainsKey(k))) Add(key, v => (v.Item1, v.Item2, v.Item3 + 1, v.Item4, v.Item5, v.Item6, v.Item7));
        System.Threading.Tasks.Parallel.ForEach(both, key =>
        {
            byte[] fa = File.ReadAllBytes(a[key]), fb = File.ReadAllBytes(b[key]);
            if (fa.AsSpan().SequenceEqual(fb))
            {
                Add(key, v => (v.Item1 + 1, v.Item2, v.Item3, v.Item4 + 1, v.Item5, v.Item6, v.Item7));
                return;
            }

            SurfaceTile x = SurfaceTile.Decode(fa, f => Jpeg.Decode(f, out _, out _)), y = SurfaceTile.Decode(fb, f => Jpeg.Decode(f, out _, out _));
            long over = 0;
            int worst = 0, worstAt = 0;
            // Colour tiles: the sea tone (4th channel) only shows where there is water, so it is
            // left out of the comparison; compare the water packs for the coasts.
            int compared = extension == SurfaceTile.ColourExtension ? 3 * SurfaceTile.SampleCount : x.Data.Length;
            // Land colour only shows on land: with a water pack beside the first colour pack, each
            // difference counts in proportion to the land share there.
            float[] land = null;
            if (extension == SurfaceTile.ColourExtension && Directory.Exists(waterBeside))
            {
                string[] parts = key.Split('/');
                var id = new TileId(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(Path.GetFileNameWithoutExtension(parts[2])));
                land = PackUpdate.Read(waterBeside, id, SurfaceTile.WaterExtension, out _)?[0].Select(w => 1f - w / 255f).ToArray();
            }

            for (int i = 0; i < compared; i++)
            {
                int d = Math.Abs(x.Data[i] - y.Data[i]);
                if (land != null) d = (int)MathF.Round(d * land[i % SurfaceTile.SampleCount]);
                if (d > 4) over++;
                if (d > worst) { worst = d; worstAt = i; }
            }

            if (Environment.GetEnvironmentVariable("TNS_COMPARE_VERBOSE") == "1")
            {
                int channel = worstAt / SurfaceTile.SampleCount, sample = worstAt % SurfaceTile.SampleCount;
                Console.WriteLine($"    {key}: worst {worst} in channel {channel} at row {sample / SurfaceTile.Samples}, column {sample % SurfaceTile.Samples} " +
                                  $"({x.Data[worstAt]} vs {y.Data[worstAt]}), {over} values off by >4");
            }

            Add(key, v => (v.Item1 + 1, v.Item2, v.Item3, v.Item4, v.Item5 + compared, v.Item6 + over, Math.Max(v.Item7, worst)));
        });

        Console.WriteLine($"{extension} tiles: {both.Count:#,0} in both, {a.Count - both.Count:#,0} only in the first, {b.Count - both.Count:#,0} only in the second");
        Console.WriteLine("  level     both   only 1st  only 2nd  identical  values off by >4   worst");
        foreach (var (level, v) in levels)
        {
            Console.WriteLine($"  {level,5} {v.Both,8} {v.OnlyA,10} {v.OnlyB,9} {v.Same,10} {100.0 * v.Over4 / Math.Max(v.Samples, 1),15:0.000}% {v.Worst,7}");
        }

        return 0;
    }

    private static int Bland(string[] args, List<string> positional)
    {
        if (positional.Count < 3) throw new ArgumentException("bland needs: <surface recipe> <data folder> <out.csv>.");
        SurfaceRecipe recipe = SurfaceRecipe.Load(positional[0]);
        if (Option(args, "--sharp") is { } sharp) recipe.DetailImages = Path.GetFullPath(sharp);
        int threads = Math.Max(1, Environment.ProcessorCount - 2);
        // --patches 3: a 3 x 3 grid of patches per square instead of one at the centre.
        // --detail-level 6: judge the map with detail no deeper than level 6, as it looks without the sharper picture.
        int patches = Option(args, "--patches") is { } p ? int.Parse(p, CultureInfo.InvariantCulture) : 1;
        int detailLevel = Option(args, "--detail-level") is { } d ? int.Parse(d, CultureInfo.InvariantCulture) : 9;
        Console.WriteLine($"  {patches} x {patches} patches a square, detail to level {detailLevel}");
        var cells = BlandSurvey.Measure(recipe, positional[1], threads, patches, detailLevel);
        BlandSurvey.Save(positional[2], cells);
        Console.WriteLine($"Wrote {positional[2]}: {cells.Count} squares of land");
        return 0;
    }

    private static int Surface(string[] args, List<string> positional)
    {
        if (positional.Count < 1) throw new ArgumentException("surface needs a surface recipe file.");
        SurfaceRecipe recipe = SurfaceRecipe.Load(positional[0]);
        int threads = int.TryParse(Option(args, "--threads"), out int t) ? t : Math.Max(1, Environment.ProcessorCount - 2);
        // One box, or several separated by semicolons: w,s,e,n;w,s,e,n
        string updateBoxes = Option(args, "--update");
        if (updateBoxes != null && Option(args, "--region") != null) throw new ArgumentException("Use --region or --update, not both.");
        double[][] region = (updateBoxes ?? Option(args, "--region"))?.Split(';').Select(box => box.Split(',').Select(Number).ToArray()).ToArray();
        if (region != null && region.Any(box => box.Length != 4 || box[0] >= box[2] || box[1] >= box[3]))
        {
            throw new ArgumentException("--region and --update need west,south,east,north, or several boxes separated by semicolons.");
        }

        bool update = updateBoxes != null;
        if (update)
        {
            region = PackUpdate.Snap(region);
            Console.WriteLine("  update: rebaking " + string.Join("; ", region.Select(b => $"{b[0]:0.###},{b[1]:0.###},{b[2]:0.###},{b[3]:0.###}")) +
                              $" (widened to whole level {PackUpdate.SnapLevel} tiles) into the existing packs");
        }

        bool detailOnly = args.Contains("--detail-only");
        var outputs = detailOnly
            ? new[] { recipe.Resolve(recipe.DetailOutput) }
            : new[] { recipe.Resolve(recipe.CoverOutput), recipe.Resolve(recipe.ColourOutput), recipe.Resolve(recipe.WaterOutput), recipe.Resolve(recipe.DetailOutput) };
        foreach (string output in outputs)
        {
            if (update)
            {
                if (!File.Exists(Path.Combine(output, "pack.json")))
                {
                    throw new IOException($"--update rebakes part of an existing pack, but {output} has none. Bake it in full first.");
                }

                continue;
            }

            if (!Directory.Exists(output)) continue;
            var levelFolders = Directory.EnumerateDirectories(output).Where(f => int.TryParse(Path.GetFileName(f), out _)).ToList();
            if (levelFolders.Count > 0 && !args.Contains("--clean"))
            {
                throw new IOException($"{output} already holds tiles. Add --clean to replace them, or change the recipe's output folders.");
            }

            foreach (string folder in levelFolders) Directory.Delete(folder, recursive: true);
        }

        using var cover = new LandCover(recipe.Resolve(recipe.LandCover), 3L << 30);
        Console.WriteLine($"  land cover: {cover.FileCount} files, {cover.PixelsPerDegree} pixels per degree ({111_195.0 / cover.PixelsPerDegree:0} m)");

        string imagePath = recipe.Resolve(recipe.ColourImage);
        byte[] picture = PngImage.ReadRgb(imagePath, out int width, out int height);
        string cache = recipe.Resolve(recipe.ColourCache ?? Path.ChangeExtension(recipe.Resolve(recipe.ColourOutput), ".colours.bin"));
        ColourTable colours;
        if (File.Exists(cache) && !args.Contains("--recolour"))
        {
            colours = ColourTable.Load(cache);
            Console.WriteLine($"  colours of each kind of ground: from {cache}");
        }
        else
        {
            Console.WriteLine($"  measuring the colour of each kind of ground from {Path.GetFileName(imagePath)}");
            colours = ColourTable.Measure(cover, picture, width, height, recipe.ColourRadiusKm, threads);
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            colours.Save(cache);
            colours.SavePreview(Path.ChangeExtension(cache, ".png"));
            Console.WriteLine($"  saved to {cache}, with a picture of them beside it");
        }

        foreach (var (name, rgb) in recipe.ClassColours)
        {
            int index = Array.IndexOf(Cover.Names, name.ToLowerInvariant());
            if (index < 0) throw new InvalidDataException($"Unknown class \"{name}\" in classColours. Classes: {string.Join(", ", Cover.Names)}.");
            if (rgb is not { Length: 3 }) throw new InvalidDataException($"classColours.{name} needs three numbers, red, green and blue from 0 to 1.");
            colours.SetEverywhere(index, rgb[0], rgb[1], rgb[2]);
        }

        var blocks = new BlockCache(1L << 30);
        string elevationPath = recipe.Resolve(recipe.Elevation.Path ?? throw new InvalidDataException("The elevation source needs a \"path\"."));
        IElevationSource elevation = Directory.Exists(elevationPath)
            ? new TiffFolderSource(elevationPath, blocks, recipe.Elevation)
            : new TiffElevationSource(elevationPath, blocks, recipe.Elevation);
        using LandCover fill = string.IsNullOrWhiteSpace(recipe.LandCoverFill)
            ? null
            : new LandCover(recipe.Resolve(recipe.LandCoverFill), 256L << 20);
        if (fill != null)
        {
            if (fill.PixelsPerDegree != cover.PixelsPerDegree)
            {
                throw new InvalidDataException($"landCoverFill is {fill.PixelsPerDegree} pixels per degree; it must match landCover ({cover.PixelsPerDegree}).");
            }

            Console.WriteLine($"  land cover where WorldCover has none: {fill.FileCount} files in {fill.Name}");
        }

        HashSet<(int Lat, int Lon)> openSquares = string.IsNullOrWhiteSpace(recipe.OpenSquares)
            ? null
            : SquareLimitedSource.ReadSquares(recipe.Resolve(recipe.OpenSquares));
        if (openSquares != null) Console.WriteLine($"  open ground: {openSquares.Count} squares, blended colour to level {recipe.ColourOpenMaxLevel} there");
        if (!detailOnly) new SurfaceBaker(recipe, cover, colours, elevation, region, fill, update, openSquares).Run(threads);
        IWorldPicture detailPicture = new ImagePicture(picture, width, height);
        IWorldPicture sharpPicture = null;
        HashSet<(int Lat, int Lon)> fineSquares = null;
        if (!string.IsNullOrWhiteSpace(recipe.DetailImages))
        {
            var sharp = new TiledPicture(recipe.Resolve(recipe.DetailImages), detailPicture, new BlockCache(1L << 30));
            Console.WriteLine($"  sharper pictures for the detail: {sharp.PartCount}, {111_195.0 / sharp.PerDegree:0} m a pixel");
            if (string.IsNullOrWhiteSpace(recipe.DetailFineSquares) && openSquares == null) detailPicture = sharp;
            else
            {
                // The sharper picture goes deeper in the bland squares, on open ground and in the
                // squares that fill whole regions and gaps.
                sharpPicture = sharp;
                fineSquares = string.IsNullOrWhiteSpace(recipe.DetailFineSquares)
                    ? new HashSet<(int Lat, int Lon)>()
                    : SquareLimitedSource.ReadSquares(recipe.Resolve(recipe.DetailFineSquares));
                if (openSquares != null) fineSquares.UnionWith(openSquares);
                if (!string.IsNullOrWhiteSpace(recipe.DetailFillSquares))
                {
                    var extra = SquareLimitedSource.ReadSquares(recipe.Resolve(recipe.DetailFillSquares));
                    extra.ExceptWith(fineSquares);
                    fineSquares.UnionWith(extra);
                    Console.WriteLine($"  filling regions and gaps: {extra.Count} more squares");
                }

                Console.WriteLine($"  used to level {recipe.DetailFineMaxLevel} in {fineSquares.Count} squares");
            }
        }

        new DetailBaker(recipe, cover, colours, elevation, detailPicture, region, sharpPicture, fineSquares, update).Run(threads);
        (elevation as IDisposable)?.Dispose();
        return 0;
    }

    private static int Plan(string[] args, List<string> positional)
    {
        if (positional.Count < 2) throw new ArgumentException("plan needs a pack folder and a recipe file.");
        Recipe recipe = Recipe.Load(positional[1]);
        if (double.TryParse(Option(args, "--slope"), NumberStyles.Float, CultureInfo.InvariantCulture, out double slope)) recipe.DetailSlope = slope;
        if (double.TryParse(Option(args, "--area"), NumberStyles.Float, CultureInfo.InvariantCulture, out double area)) recipe.DetailAreaKm2 = area;
        int threads = Math.Max(1, Environment.ProcessorCount - 2);
        if (int.TryParse(Option(args, "--list"), out int level))
        {
            return DetailPlan.List(positional[0], level, recipe.DetailSlope, 12, threads);
        }

        return DetailPlan.Run(positional[0], recipe, Option(args, "--map"), Option(args, "--stats"), threads);
    }

    private static int Info(List<string> positional)
    {
        if (positional.Count < 1) throw new ArgumentException("info needs a file or folder.");
        string path = positional[0];

        if (Directory.Exists(path))
        {
            var packs = new TerrainPackSet();
            packs.AddPack(path);
            Console.WriteLine($"Terrain pack {path}: {packs.TileCount} tiles, deepest level {packs.MaxLevel}");
            foreach (var level in packs.AllTiles.GroupBy(id => id.Level).OrderBy(g => g.Key))
            {
                long bytes = level.Sum(id => new FileInfo(packs.PathOf(id)).Length);
                double spacingM = 180.0 / (1 << level.Key) / HeightTile.Intervals * 111_320.0;
                Console.WriteLine($"  level {level.Key,2}: {level.Count(),7} of {TileId.ColumnsAt(level.Key) * TileId.RowsAt(level.Key),8} tiles, " +
                                  $"{bytes / 1e6,8:0.0} MB, one sample every {spacingM:#,0} m");
            }

            return 0;
        }

        if (path.EndsWith(HeightTile.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            HeightTile tile = HeightTile.Decode(File.ReadAllBytes(path));
            Console.WriteLine($"Terrain tile, {HeightTile.Samples} x {HeightTile.Samples} samples, " +
                              $"heights {tile.MinHeight:0.#} to {tile.MaxHeight:0.#} m, {new FileInfo(path).Length:#,0} bytes");
            return 0;
        }

        using TiffFile tiff = TiffFile.Open(path);
        Console.WriteLine($"{Path.GetFileName(path)}: {tiff.Describe()}");
        if (tiff.HasGeoreference)
        {
            Console.WriteLine($"  detail to tile level {SourceMaths.LevelForPixelSize(Math.Min(tiff.PixelSizeX, tiff.PixelSizeY))}");
        }

        return 0;
    }

    private static int Height(List<string> positional)
    {
        if (positional.Count < 3) throw new ArgumentException("height needs a pack folder, a latitude and a longitude.");
        double lat = double.Parse(positional[1], CultureInfo.InvariantCulture);
        double lon = double.Parse(positional[2], CultureInfo.InvariantCulture);
        var packs = new TerrainPackSet();
        packs.AddPack(positional[0]);

        // Walk down to the deepest tile that covers the point.
        var id = new TileId(0, lon < 0 ? 0 : 1, 0);
        if (!packs.Has(id)) throw new IOException("The pack has no level 0 tiles.");
        while (true)
        {
            int childX = lon >= id.West + id.SizeDeg * 0.5 ? 1 : 0;
            int childY = lat >= id.South + id.SizeDeg * 0.5 ? 1 : 0;
            TileId child = id.Child(childX, childY);
            if (!packs.Has(child)) break;
            id = child;
        }

        HeightTile tile = packs.Load(id);
        double column = (lon - id.West) / id.SizeDeg * HeightTile.Intervals;
        double row = (lat - id.South) / id.SizeDeg * HeightTile.Intervals;
        Console.WriteLine($"{tile.SampleBilinear(column, row):0.0} m at {lat}, {lon} (from tile {id})");
        return 0;
    }

    private static int Export(string[] args, List<string> positional)
    {
        if (positional.Count < 6) throw new ArgumentException("export needs: <pack folder> <out.tif> <west> <south> <east> <north>.");
        var source = new PackElevationSource(positional[0]);
        double west = double.Parse(positional[2], CultureInfo.InvariantCulture);
        double south = double.Parse(positional[3], CultureInfo.InvariantCulture);
        double east = double.Parse(positional[4], CultureInfo.InvariantCulture);
        double north = double.Parse(positional[5], CultureInfo.InvariantCulture);
        int level = int.TryParse(Option(args, "--level"), out int l) ? l : source.NativeLevel;

        double tileSize = 180.0 / (1 << level);
        double step = tileSize / HeightTile.Intervals;
        // Snap the box outwards to the sample grid, so exported pixels are exactly stored samples.
        long column0 = (long)Math.Floor((west + 180.0) / step), column1 = (long)Math.Ceiling((east + 180.0) / step);
        long row0 = (long)Math.Floor((south + 90.0) / step), row1 = (long)Math.Ceiling((north + 90.0) / step);
        int width = (int)(column1 - column0 + 1), height = (int)(row1 - row0 + 1);
        if (width <= 1 || height <= 1) throw new ArgumentException("The box is empty.");
        if ((long)width * height > 400_000_000) throw new ArgumentException("That box is too large at this level. Use a smaller box or a lower --level.");

        var pixels = new float[width * height];
        for (long tileY = row0 / HeightTile.Intervals; tileY <= (row1 - 1) / HeightTile.Intervals; tileY++)
        {
            for (long tileX = column0 / HeightTile.Intervals; tileX <= (column1 - 1) / HeightTile.Intervals; tileX++)
            {
                var id = new TileId(level, (int)tileX, (int)tileY);
                if (!id.IsValid) continue;
                var heights = new float[HeightTile.SampleCount];
                Array.Fill(heights, float.NaN);
                source.Fill(id, heights);
                for (int r = 0; r < HeightTile.Samples; r++)
                {
                    long globalRow = tileY * HeightTile.Intervals + r;
                    if (globalRow < row0 || globalRow > row1) continue;
                    for (int c = 0; c < HeightTile.Samples; c++)
                    {
                        long globalColumn = tileX * HeightTile.Intervals + c;
                        if (globalColumn < column0 || globalColumn > column1) continue;
                        // GeoTIFF rows run north to south.
                        pixels[(row1 - globalRow) * width + (globalColumn - column0)] = heights[r * HeightTile.Samples + c];
                    }
                }
            }
        }

        // Each pixel is centred on a sample, so the image edge sits half a pixel outside.
        double imageWest = -180.0 + column0 * step - step * 0.5;
        double imageNorth = -90.0 + row1 * step + step * 0.5;
        TiffFile.WriteFloatGeoTiff(positional[1], width, height, pixels, imageWest, imageNorth, step);
        Console.WriteLine($"Wrote {positional[1]}: {width} x {height} px at level {level}, one pixel every {step * 111_320.0:#,0} m.");
        Console.WriteLine($"To bake an edited copy as a mod, use it as the top source with");
        Console.WriteLine($"  \"onlyBounds\": [{imageWest + step * 0.5:0.######}, {imageNorth - step * (height - 0.5):0.######}, " +
                          $"{imageWest + step * (width - 0.5):0.######}, {imageNorth - step * 0.5:0.######}]");
        return 0;
    }
}
