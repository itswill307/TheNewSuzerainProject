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
/// Finds where the map looks bland up close, and what data would help.
///
/// For every 1 x 1 degree square of land it takes a 20 km patch at its centre on a 305 m grid
/// (about two screen pixels at the closest zoom) and works out, the way the game draws it, the
/// colour of each point (kind of ground, from cover_colours.png, times the detail tiles) and its
/// slope shading (from the height tiles). Fine contrast is how much the brightness varies at
/// scales under about 2 km, as a share of the mean: low means a smooth, blurred look.
///
/// For the same patch it measures what better data would add:
///   dune relief:  how much ETOPO's own heights (450 m) vary at fine scales. Dune fields show as
///                 a ripple ETOPO barely resolves; 90 m heights (Copernicus) draw them properly.
///   fine colour:  the fine contrast of the 500 m Blue Marble, where its tiles have been
///                 downloaded (detailImages in the recipe).
/// Results go to a CSV, from which the map is drawn.
/// </summary>
public static class BlandSurvey
{
    private const int Level = 8, Patch = 64, Blur = 3;

    public sealed record Cell(int Lat, int Lon, int Kind, double Land, double Contrast, double ShadeContrast,
        double ColourContrast, double EtopoRippleM, double PackRippleM, double SharpContrast, bool HasCopernicus,
        float R, float G, float B, double Smooth, double SharpSmooth,
        int Patches = 1, int BlandPatches = 0, int ColourPatches = 0);

    /// <summary>A patch is bland when this share of it is a smooth wash (BlandMap.Settings.Bland).</summary>
    public const double BlandShare = 0.7;

    /// <summary>The sharper picture fixes a bland patch when it is this much less smooth (BlandMap.Settings.ColourGain).</summary>
    public const double ColourGain = 0.2;

    public static List<Cell> Measure(SurfaceRecipe recipe, string data, int threads, int patches = 1, int detailLevel = 9)
    {
        var heights = new TerrainPackSet();
        heights.AddPack(Path.Combine(data, "terrain"));
        var cover = new TerrainPackSet(SurfaceTile.CoverExtension);
        cover.AddPack(Path.Combine(data, "cover"));
        var detail = new TerrainPackSet(SurfaceTile.DetailExtension);
        detail.AddPack(Path.Combine(data, "detail"));
        byte[] atlas = PngImage.ReadRgb(Path.Combine(data, "cover", ColourTable.AtlasFileName), out _, out _);

        var blocks = new BlockCache(1L << 30);
        string elevationPath = recipe.Resolve(recipe.Elevation.Path);
        IElevationSource etopo = Directory.Exists(elevationPath)
            ? new TiffFolderSource(elevationPath, blocks, recipe.Elevation)
            : new TiffElevationSource(elevationPath, blocks, recipe.Elevation);
        IWorldPicture sharp = null;
        if (!string.IsNullOrWhiteSpace(recipe.DetailImages))
        {
            string picturePath = recipe.Resolve(recipe.ColourImage);
            byte[] picture = PngImage.ReadRgb(picturePath, out int w, out int h);
            var fallback = new ImagePicture(picture, w, h);
            sharp = new TiledPicture(recipe.Resolve(recipe.DetailImages), new NoPicture(fallback.PerDegree), new BlockCache(1L << 30));
        }

        var cells = new ConcurrentBag<Cell>();
        var squares = new List<(int Lat, int Lon)>();
        for (int lat = -60; lat < 84; lat++)
        {
            for (int lon = -180; lon < 180; lon++) squares.Add((lat, lon));
        }

        int done = 0;
        Parallel.ForEach(squares, new ParallelOptions { MaxDegreeOfParallelism = threads },
            () => new Loader(heights, cover, detail, detailLevel),
            (square, _, loader) =>
            {
                Cell cell = MeasureCell(square.Lat, square.Lon, patches, loader, atlas, etopo, sharp);
                if (cell != null) cells.Add(cell);
                int now = System.Threading.Interlocked.Increment(ref done);
                if (now % 2000 == 0) Console.Write($"\r  {now}/{squares.Count} squares, {cells.Count} with land   ");
                return loader;
            },
            _ => { });
        Console.WriteLine();
        (etopo as IDisposable)?.Dispose();
        return cells.OrderBy(c => c.Lat).ThenBy(c => c.Lon).ToList();
    }

    private sealed class NoPicture : IWorldPicture
    {
        public NoPicture(double perDegree) => PerDegree = perDegree;
        public double PerDegree { get; }

        public bool TryGet(long x, long y, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            return false;
        }
    }

    /// <summary>Tile loading with a small cache per thread.</summary>
    private sealed class Loader
    {
        private readonly TerrainPackSet _heights, _cover, _detail;
        private readonly Dictionary<TileId, object> _cache = new();

        private readonly int _detailLevel;

        public Loader(TerrainPackSet heights, TerrainPackSet cover, TerrainPackSet detail, int detailLevel)
        {
            _detailLevel = detailLevel;
            _heights = heights;
            _cover = cover;
            _detail = detail;
        }

        private static TileId Deepest(TerrainPackSet packs, TileId id, int maxLevel)
        {
            while (id.Level > maxLevel) id = id.Parent;
            while (id.Level > 0 && !packs.Has(id)) id = id.Parent;
            return id;
        }

        private T Get<T>(TileId id, Func<T> load) where T : class
        {
            if (_cache.TryGetValue(id, out object found)) return (T)found;
            if (_cache.Count > 64) _cache.Clear();
            T value = load();
            _cache[id] = value;
            return value;
        }

        public (TileId Id, HeightTile Tile) Heights(TileId at)
        {
            TileId id = Deepest(_heights, at, 9);
            return (id, _heights.Has(id) ? Get(id, () => _heights.Load(id)) : null);
        }

        public (TileId Id, SurfaceTile Tile) Cover(TileId at)
        {
            TileId id = Deepest(_cover, at, Level);
            return (id, _cover.Has(id) ? Get(new TileId(id.Level + 100, id.X, id.Y), () => _cover.LoadSurface(id)) : null);
        }

        public (TileId Id, SurfaceTile Tile) Detail(TileId at)
        {
            TileId id = Deepest(_detail, at, _detailLevel);
            return (id, _detail.Has(id)
                ? Get(new TileId(id.Level + 200, id.X, id.Y), () => _detail.LoadSurface(id, f => Jpeg.Decode(f, out _, out _)))
                : null);
        }

        public bool HasDeepHeights(TileId at) => _heights.MaxDetailLevelFor(at) >= 9;
    }

    /// <summary>
    /// Measures a square from patches x patches patches spread evenly over it (1: one at the centre),
    /// so a river, a field or a cloud in one patch does not decide the whole square. Scores are
    /// averaged over the patches that are mostly land; the kind is the most common over all of them.
    /// </summary>
    private static Cell MeasureCell(int lat, int lon, int patches, Loader loader, byte[] atlas, IElevationSource etopo, IWorldPicture sharp)
    {
        var found = new List<Cell>();
        var kinds = new int[Cover.Count];
        for (int a = 0; a < patches; a++)
        {
            for (int b = 0; b < patches; b++)
            {
                Cell one = MeasurePatch(lat, lon, lat + (a + 0.5) / patches, lon + (b + 0.5) / patches, loader, atlas, etopo, sharp, kinds);
                if (one != null) found.Add(one);
            }
        }

        if (found.Count == 0) return null;
        // How many patches are bland, and how many of those the sharper picture fixes.
        int bland = found.Count(c => c.Smooth >= BlandShare);
        int fixedByColour = found.Count(c => c.Smooth >= BlandShare && !double.IsNaN(c.SharpSmooth) && c.SharpSmooth <= c.Smooth - ColourGain);
        if (found.Count == 1) return found[0] with { Kind = Array.IndexOf(kinds, kinds.Max()), BlandPatches = bland, ColourPatches = fixedByColour };
        double Mean(Func<Cell, double> f) => found.Average(f);
        var sharpCells = found.Where(c => !double.IsNaN(c.SharpSmooth)).ToList();
        return new Cell(lat, lon, Array.IndexOf(kinds, kinds.Max()), Mean(c => c.Land),
            Mean(c => c.Contrast), Mean(c => c.ShadeContrast), Mean(c => c.ColourContrast),
            Mean(c => c.EtopoRippleM), Mean(c => c.PackRippleM),
            sharpCells.Count == 0 ? double.NaN : sharpCells.Average(c => c.SharpContrast),
            found.Any(c => c.HasCopernicus),
            (float)Mean(c => c.R), (float)Mean(c => c.G), (float)Mean(c => c.B),
            Mean(c => c.Smooth), sharpCells.Count == 0 ? double.NaN : sharpCells.Average(c => c.SharpSmooth),
            found.Count, bland, fixedByColour);
    }

    private static Cell MeasurePatch(int lat, int lon, double centreLat, double centreLon, Loader loader, byte[] atlas,
        IElevationSource etopo, IWorldPicture sharp, int[] allKinds)
    {
        double size = 180.0 / (1 << Level);
        var tile = new TileId(Level, (int)Math.Floor((centreLon + 180.0) / size), (int)Math.Floor((centreLat + 90.0) / size));
        var (coverId, coverTile) = loader.Cover(tile);
        if (coverTile == null || coverId.Level < 7) return null;

        double step = size / HeightTile.Intervals;
        // The patch, inside the tile, as near the given centre as it can be.
        int c0 = Math.Clamp((int)Math.Round((centreLon - tile.West) / step) - Patch / 2, 0, HeightTile.Samples - Patch);
        int r0 = Math.Clamp((int)Math.Round((centreLat - tile.South) / step) - Patch / 2, 0, HeightTile.Samples - Patch);

        var (heightId, heightTile) = loader.Heights(tile);
        var (detailId, detailTile) = loader.Detail(tile);
        if (heightTile == null) return null;

        int n = Patch * Patch;
        var lum = new double[n];
        var colourOnly = new double[n];
        var shadeOnly = new double[n];
        var height = new double[n];
        var land = new bool[n];
        var kinds = new int[Cover.Count];
        double sr = 0, sg = 0, sb = 0;
        for (int j = 0; j < Patch; j++)
        {
            double sampleLat = tile.South + (r0 + j) * step;
            for (int i = 0; i < Patch; i++)
            {
                double sampleLon = tile.West + (c0 + i) * step;
                int at = j * Patch + i;
                height[at] = Sample(heightTile.Heights, heightId, sampleLat, sampleLon);
            }
        }

        double metres = step * 111_195.0;
        for (int j = 0; j < Patch; j++)
        {
            double sampleLat = tile.South + (r0 + j) * step;
            double eastM = metres * Math.Max(Math.Cos(sampleLat * Math.PI / 180.0), 0.05);
            for (int i = 0; i < Patch; i++)
            {
                double sampleLon = tile.West + (c0 + i) * step;
                int at = j * Patch + i;
                int kind = Nearest(coverTile.Data, coverId, sampleLat, sampleLon);
                if (kind < 0 || kind >= Cover.Count || kind == Cover.Water) continue;
                land[at] = true;
                kinds[kind]++;

                var colour = AtlasColour(atlas, kind, sampleLat, sampleLon);
                if (detailTile != null)
                {
                    colour.R *= SampleBytes(detailTile.Data, 0, detailId, sampleLat, sampleLon) / 128.0;
                    colour.G *= SampleBytes(detailTile.Data, 1, detailId, sampleLat, sampleLon) / 128.0;
                    colour.B *= SampleBytes(detailTile.Data, 2, detailId, sampleLat, sampleLon) / 128.0;
                }

                int w = Math.Max(i - 1, 0), e = Math.Min(i + 1, Patch - 1), s = Math.Max(j - 1, 0), no = Math.Min(j + 1, Patch - 1);
                double sx = (height[j * Patch + e] - height[j * Patch + w]) / ((e - w) * eastM) * 3.0;
                double sy = (height[no * Patch + i] - height[s * Patch + i]) / ((no - s) * metres) * 3.0;
                double len = Math.Sqrt(sx * sx + sy * sy + 1.0);
                double shade = ((-sx) * -0.5 + (-sy) * 0.5 + 0.70710678) / len / 0.70710678;
                shade = 1.0 + (Math.Clamp(shade, 0.35, 1.6) - 1.0) * 0.5;

                double y = 0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B;
                colourOnly[at] = y;
                shadeOnly[at] = shade;
                lum[at] = y * shade;
                sr += colour.R;
                sg += colour.G;
                sb += colour.B;
            }
        }

        int landCount = land.Count(l => l);
        double landShare = landCount / (double)n;
        if (landShare < 0.6) return null;
        for (int k = 0; k < Cover.Count; k++) allKinds[k] += kinds[k];

        var etopoHeights = new float[HeightTile.SampleCount];
        Array.Fill(etopoHeights, float.NaN);
        etopo.Fill(tile, etopoHeights);
        var etopoPatch = new double[n];
        for (int j = 0; j < Patch; j++)
        {
            for (int i = 0; i < Patch; i++)
            {
                float v = etopoHeights[(r0 + j) * HeightTile.Samples + c0 + i];
                etopoPatch[j * Patch + i] = float.IsNaN(v) ? 0 : v;
            }
        }

        double sharpContrast = double.NaN, sharpSmooth = double.NaN;
        if (sharp != null)
        {
            var sharpLum = new double[n];
            bool covered = true;
            for (int j = 0; j < Patch && covered; j++)
            {
                double sampleLat = tile.South + (r0 + j) * step;
                for (int i = 0; i < Patch; i++)
                {
                    double sampleLon = tile.West + (c0 + i) * step;
                    long px = (long)Math.Floor((sampleLon + 180.0) * sharp.PerDegree);
                    long py = (long)Math.Floor((90.0 - sampleLat) * sharp.PerDegree);
                    if (!sharp.TryGet(px, py, out byte r, out byte g, out byte b))
                    {
                        covered = false;
                        break;
                    }

                    sharpLum[j * Patch + i] = 0.2126 * ColourTable.SrgbToLinear[r] + 0.7152 * ColourTable.SrgbToLinear[g]
                                              + 0.0722 * ColourTable.SrgbToLinear[b];
                }
            }

            if (covered)
            {
                for (int i = 0; i < n; i++) sharpLum[i] = Lightness(sharpLum[i]);
                sharpContrast = FineContrast(sharpLum, land, relative: false);
                sharpSmooth = SmoothShare(sharpLum, land);
            }
        }

        int dominant = Array.IndexOf(kinds, kinds.Max());
        var lightness = new double[n];
        for (int i = 0; i < n; i++) lightness[i] = land[i] ? Lightness(lum[i]) : 0;
        return new Cell(lat, lon, dominant, landShare,
            FineContrast(lightness, land, relative: false),
            FineContrast(shadeOnly, land, relative: true),
            FineContrast(colourOnly, land, relative: true),
            FineContrast(etopoPatch, land, relative: false),
            FineContrast(height, land, relative: false),
            sharpContrast,
            loader.HasDeepHeights(tile),
            (float)(sr / landCount), (float)(sg / landCount), (float)(sb / landCount),
            SmoothShare(lightness, land), sharpSmooth);
    }

    /// <summary>Root mean square of the value minus its 7 x 7 average (about 2 km), over land; relative to the mean if asked.</summary>
    private static double FineContrast(double[] values, bool[] land, bool relative)
    {
        double sum = 0, mean = 0;
        int count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (!land[i]) continue;
            mean += values[i];
            count++;
        }

        if (count == 0) return 0;
        mean /= count;
        int used = 0;
        for (int j = Blur; j < Patch - Blur; j++)
        {
            for (int i = Blur; i < Patch - Blur; i++)
            {
                if (!land[j * Patch + i]) continue;
                double local = 0;
                int k = 0;
                for (int y = -Blur; y <= Blur; y++)
                {
                    for (int x = -Blur; x <= Blur; x++)
                    {
                        int at = (j + y) * Patch + i + x;
                        local += land[at] ? values[at] : mean;
                        k++;
                    }
                }

                double d = values[j * Patch + i] - local / k;
                sum += d * d;
                used++;
            }
        }

        double rms = used == 0 ? 0 : Math.Sqrt(sum / used);
        return relative ? rms / Math.Max(mean, 1e-6) : rms;
    }

    /// <summary>Perceptual lightness (CIE L*, 0 to 100) of a linear luminance.</summary>
    private static double Lightness(double y) => y > 0.008856 ? 116.0 * Math.Cbrt(y) - 16.0 : 903.3 * y;

    /// <summary>Below this much variation in lightness (L*) within about 2 km, the eye sees a smooth wash.</summary>
    public const double SmoothLimit = 1.5;

    /// <summary>Share of the land samples whose 7 x 7 neighbourhood (about 2 km) varies by less than SmoothLimit.</summary>
    private static double SmoothShare(double[] values, bool[] land)
    {
        int smooth = 0, used = 0;
        for (int j = Blur; j < Patch - Blur; j++)
        {
            for (int i = Blur; i < Patch - Blur; i++)
            {
                if (!land[j * Patch + i]) continue;
                double sum = 0, sum2 = 0;
                int k = 0;
                for (int y = -Blur; y <= Blur; y++)
                {
                    for (int x = -Blur; x <= Blur; x++)
                    {
                        int at = (j + y) * Patch + i + x;
                        if (!land[at]) continue;
                        sum += values[at];
                        sum2 += values[at] * values[at];
                        k++;
                    }
                }

                double sd = Math.Sqrt(Math.Max(sum2 / k - (sum / k) * (sum / k), 0));
                if (sd < SmoothLimit) smooth++;
                used++;
            }
        }

        return used == 0 ? 0 : smooth / (double)used;
    }

    private static double Sample(float[] grid, TileId id, double lat, double lon)
    {
        double column = (lon - id.West) / id.SizeDeg * HeightTile.Intervals;
        double row = (lat - id.South) / id.SizeDeg * HeightTile.Intervals;
        int c = Math.Clamp((int)column, 0, HeightTile.Intervals - 1), r = Math.Clamp((int)row, 0, HeightTile.Intervals - 1);
        double fc = Math.Clamp(column - c, 0, 1), fr = Math.Clamp(row - r, 0, 1);
        int at = r * HeightTile.Samples + c;
        double s = grid[at] + (grid[at + 1] - grid[at]) * fc;
        double no = grid[at + HeightTile.Samples] + (grid[at + HeightTile.Samples + 1] - grid[at + HeightTile.Samples]) * fc;
        return s + (no - s) * fr;
    }

    private static double SampleBytes(byte[] data, int channel, TileId id, double lat, double lon)
    {
        double column = (lon - id.West) / id.SizeDeg * HeightTile.Intervals;
        double row = (lat - id.South) / id.SizeDeg * HeightTile.Intervals;
        int c = Math.Clamp((int)column, 0, HeightTile.Intervals - 1), r = Math.Clamp((int)row, 0, HeightTile.Intervals - 1);
        double fc = Math.Clamp(column - c, 0, 1), fr = Math.Clamp(row - r, 0, 1);
        int at = channel * HeightTile.SampleCount + r * HeightTile.Samples + c;
        double s = data[at] + (data[at + 1] - data[at]) * fc;
        double no = data[at + HeightTile.Samples] + (data[at + HeightTile.Samples + 1] - data[at + HeightTile.Samples]) * fc;
        return s + (no - s) * fr;
    }

    private static int Nearest(byte[] data, TileId id, double lat, double lon)
    {
        int c = Math.Clamp((int)Math.Round((lon - id.West) / id.SizeDeg * HeightTile.Intervals), 0, HeightTile.Intervals);
        int r = Math.Clamp((int)Math.Round((lat - id.South) / id.SizeDeg * HeightTile.Intervals), 0, HeightTile.Intervals);
        return data[r * HeightTile.Samples + c];
    }

    private static (double R, double G, double B) AtlasColour(byte[] atlas, int kind, double lat, double lon)
    {
        int x = Math.Clamp((int)((lon + 180.0) / 360.0 * ColourTable.AtlasWidth), 0, ColourTable.AtlasWidth - 1);
        int y = Math.Clamp((int)((90.0 - lat) / 180.0 * ColourTable.AtlasHeight), 0, ColourTable.AtlasHeight - 1);
        int at = ((kind * ColourTable.AtlasHeight + y) * ColourTable.AtlasWidth + x) * 3;
        return (ColourTable.SrgbToLinear[atlas[at]], ColourTable.SrgbToLinear[atlas[at + 1]], ColourTable.SrgbToLinear[atlas[at + 2]]);
    }

    // ---- saving and the map ----

    public static void Save(string path, List<Cell> cells)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("lat,lon,kind,land,contrast,shade,colour,etopo_ripple_m,pack_ripple_m,sharp_contrast,copernicus,r,g,b,smooth,sharp_smooth,patches,bland_patches,colour_patches");
        foreach (Cell c in cells)
        {
            // The tool runs with the invariant culture, so numbers use full stops.
            writer.WriteLine(
                $"{c.Lat},{c.Lon},{Cover.Names[c.Kind]},{c.Land:0.00},{c.Contrast:0.0000},{c.ShadeContrast:0.0000},{c.ColourContrast:0.0000}," +
                $"{c.EtopoRippleM:0.00},{c.PackRippleM:0.00},{(double.IsNaN(c.SharpContrast) ? "" : c.SharpContrast.ToString("0.0000", CultureInfo.InvariantCulture))}," +
                $"{(c.HasCopernicus ? 1 : 0)},{c.R:0.0000},{c.G:0.0000},{c.B:0.0000},{c.Smooth:0.000}," +
                (double.IsNaN(c.SharpSmooth) ? "" : c.SharpSmooth.ToString("0.000", CultureInfo.InvariantCulture)) +
                $",{c.Patches},{c.BlandPatches},{c.ColourPatches}");
        }
    }

    public static List<Cell> Load(string path)
    {
        var cells = new List<Cell>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] p = line.Split(',');
            double Num(int i) => p[i].Length == 0 ? double.NaN : double.Parse(p[i], CultureInfo.InvariantCulture);
            cells.Add(new Cell(int.Parse(p[0]), int.Parse(p[1]), Array.IndexOf(Cover.Names, p[2]), Num(3), Num(4), Num(5), Num(6),
                Num(7), Num(8), Num(9), p[10] == "1", (float)Num(11), (float)Num(12), (float)Num(13), Num(14), Num(15),
                p.Length > 18 ? int.Parse(p[16]) : 1, p.Length > 18 ? int.Parse(p[17]) : 0, p.Length > 18 ? int.Parse(p[18]) : 0));
        }

        return cells;
    }
}
