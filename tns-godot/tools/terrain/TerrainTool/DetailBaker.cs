using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

/// <summary>
/// Bakes the detail pack (.tnsr): how much the real colour of the ground, from the colour
/// picture (Blue Marble), differs from what its kinds of ground predict (ColourTable).
///
/// Each kind's colour is an average over about 30 km, so wide areas of one kind, deserts above
/// all, would otherwise come out as smooth blobs: the Sahara's dark rock plateaus, red sand seas
/// and wind streaks are all "bare". The detail is a multiplier, about 1, stored per sample and
/// applied by the game to both the distant colour and the close-up land cover, so zooming
/// still never shifts the colours.
///
/// On steep ground the multiplier fades to 1: there the picture's own shadows would show and
/// clash with the game's slope shading. Water, and snow on ground that is not snow, are left out
/// and filled in from round about.
///
/// Stored 3 channels (red, green, blue multiplier x 128) as JPEG, to DetailMaxLevel (level 6, one
/// sample every 1.2 km, about the picture's own 1.85 km).
/// </summary>
public sealed class DetailBaker
{
    private const int N = HeightTile.Samples;
    private const int Count = HeightTile.SampleCount;
    private const float Scale = 128f;

    private readonly SurfaceRecipe _recipe;
    private readonly LandCover _cover;
    private readonly ColourTable _colours;
    private readonly IElevationSource _elevation;
    private readonly IWorldPicture _picture, _sharp;
    private readonly HashSet<(int Lat, int Lon)> _fineSquares;
    private readonly double[][] _regions;

    /// <summary>Update mode: only the regions are rebaked; tiles outside them are read from the pack.</summary>
    private readonly bool _update;
    private DecisionLog _log;
    private readonly string _output;
    private readonly ConcurrentDictionary<string, bool> _folders = new();
    private readonly long[] _tiles = new long[TileId.MaxSupportedLevel + 1];
    private readonly long[] _bytes = new long[TileId.MaxSupportedLevel + 1];

    private sealed class Built
    {
        /// <summary>Red, green, blue multipliers; null where they are all 1.</summary>
        public float[][] Planes;
        public bool Below;

        /// <summary>
        /// Finer than this place's own deepest level: built only because a tile above reaches into
        /// the fine squares, so that tile can be averaged from its children. Never stored.
        /// </summary>
        public bool Beyond;
    }

    public DetailBaker(SurfaceRecipe recipe, LandCover cover, ColourTable colours, IElevationSource elevation,
        IWorldPicture picture, double[][] regions, IWorldPicture sharp = null, HashSet<(int Lat, int Lon)> fineSquares = null,
        bool update = false)
    {
        _update = update && regions != null;
        _recipe = recipe;
        _cover = cover;
        _colours = colours;
        _elevation = elevation;
        _picture = picture;
        _sharp = sharp;
        _fineSquares = fineSquares;
        _regions = regions;
        _output = recipe.Resolve(recipe.DetailOutput);
    }

    public void Run(int threads)
    {
        var timer = Stopwatch.StartNew();
        Console.WriteLine($"Detail from the colour picture, to level {_recipe.DetailMaxLevel}");
        Directory.CreateDirectory(_output);
        if (_update)
        {
            int removed = PackUpdate.DeleteInside(_output, SurfaceTile.DetailExtension, _regions);
            Console.WriteLine($"  update: removed {removed} old detail tiles inside the boxes");
        }

        _log = new DecisionLog(_output, _update);

        int rootLevel = Math.Min(3, _recipe.DetailMaxLevel);
        var roots = new List<TileId>();
        for (int y = 0; y < TileId.RowsAt(rootLevel); y++)
        {
            for (int x = 0; x < TileId.ColumnsAt(rootLevel); x++) roots.Add(new TileId(rootLevel, x, y));
        }

        var finished = new ConcurrentDictionary<TileId, Built>();
        int done = 0;
        Parallel.ForEach(roots, new ParallelOptions { MaxDegreeOfParallelism = threads }, root =>
        {
            finished[root] = Build(root, null);
            Console.Write($"\r  {Interlocked.Increment(ref done)}/{roots.Count} regions   ");
        });
        Console.WriteLine();

        foreach (TileId top in new[] { new TileId(0, 0, 0), new TileId(0, 1, 0) })
        {
            if (_update && !InRegion(top)) continue;
            WriteTile(top, Build(top, finished).Planes);
        }

        // After an update, pack.json describes the whole pack, not just what was written.
        var (packTiles, packBytes) = _update ? PackUpdate.Count(_output, SurfaceTile.DetailExtension) : (_tiles, _bytes);
        var info = new Dictionary<string, object>
        {
            ["format"] = 1,
            ["kind"] = "detail",
            ["name"] = _recipe.Name,
            ["tileSamples"] = N,
            ["maxLevel"] = Array.FindLastIndex(packTiles, t => t > 0),
            ["tiles"] = packTiles.Sum(),
            ["bytes"] = packBytes.Sum(),
            ["bakedUtc"] = DateTime.UtcNow.ToString("u"),
        };
        File.WriteAllText(Path.Combine(_output, "pack.json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        _log.Save();
        Console.WriteLine("  level   detail tiles        size");
        for (int level = 0; level <= Math.Max(_recipe.DetailMaxLevel, _recipe.DetailFineMaxLevel); level++)
        {
            Console.WriteLine($"  {level,5} {_tiles[level],14} {_bytes[level] / 1e6,8:0.0} MB");
        }

        Console.WriteLine($"  total {_tiles.Sum(),14} {_bytes.Sum() / 1e6,8:0.0} MB   in {timer.Elapsed.TotalMinutes:0.0} min");
    }

    private bool InRegion(TileId id) =>
        _regions == null || _regions.Any(r => id.East > r[0] && id.West < r[2] && id.North > r[1] && id.South < r[3]);

    private Built Build(TileId id, ConcurrentDictionary<TileId, Built> finished)
    {
        if (finished != null && finished.TryGetValue(id, out Built done)) return done;
        if (_update && !InRegion(id))
        {
            float[][] stored = PackUpdate.Read(_output, id, SurfaceTile.DetailExtension, out bool here);
            return new Built { Planes = stored?.Select(p => p.Select(v => v / Scale).ToArray()).ToArray(), Below = here };
        }

        bool fine =_sharp != null && _fineSquares != null && TouchesFine(id);
        int leafLevel = fine ? _recipe.DetailFineMaxLevel : _recipe.DetailMaxLevel;
        if (id.Level >= leafLevel || !InRegion(id))
        {
            // The sharper picture only in its squares, at its own depth; elsewhere the colour picture.
            float[][] planes = !InRegion(id) ? null
                : fine && id.Level > _recipe.DetailMaxLevel ? Leaf(id, _sharp)
                : Leaf(id, _picture);
            return new Built { Planes = planes, Beyond = id.Level > leafLevel };
        }

        var children = new Built[4];
        for (int i = 0; i < 4; i++) children[i] = Build(id.Child(i & 1, i >> 1), finished);
        float[][] parent = children.All(c => c.Planes == null) ? null : Shrink(children);
        if (_update && parent != null && !PackUpdate.Inside(id, _regions))
        {
            // Outside the boxes, keep what the pack already holds (see SurfaceBaker.KeepOutside).
            bool[] outside = PackUpdate.OutsideSamples(id, _regions);
            float[][] stored = PackUpdate.Read(_output, id, SurfaceTile.DetailExtension, out _);
            for (int p = 0; p < 3; p++)
            {
                for (int i = 0; i < Count; i++)
                {
                    if (outside[i]) parent[p][i] = stored == null ? 1f : stored[p][i] / Scale;
                }
            }
        }

        var result = new Built { Planes = parent };
        float[][] up = parent?.Select(DetailMeasure.Upsample).ToArray();

        for (int i = 0; i < 4; i++)
        {
            TileId child = id.Child(i & 1, i >> 1);
            if (!InRegion(child))
            {
                result.Below |= children[i].Below;
                continue;
            }

            if (children[i].Beyond)
            {
                if (_update) PackUpdate.DeleteIfThere(_output, child, SurfaceTile.DetailExtension);
                continue;
            }

            double changed = children[i].Planes != null ? ChangedKm2(up, children[i].Planes, child) : 0.0;
            bool forced = child.Level <= _recipe.FullLevel, below = children[i].Below;
            bool keep = forced || below || changed > _recipe.DetailAreaKm2;
            _log.Add(child, changed, _recipe.DetailAreaKm2, keep, below ? 'b' : forced ? 'f' : 'm');
            if (!keep)
            {
                if (_update) PackUpdate.DeleteIfThere(_output, child, SurfaceTile.DetailExtension);
                continue;
            }
            WriteTile(child, children[i].Planes);
            result.Below = true;
        }

        return result;
    }

    private bool TouchesFine(TileId tile) => SquareLimitedSource.Touches(_fineSquares, tile);

    /// <summary>A tile of multipliers made straight from the picture and the land cover.</summary>
    private float[][] Leaf(TileId id, IWorldPicture picture)
    {
        double pad = 3.0 / picture.PerDegree;
        if (!_cover.Covers(id.West - pad, Math.Max(id.South - pad, -90), id.East + pad, Math.Min(id.North + pad, 90))) return null;

        // The picture pixels round the tile, with a margin.
        double perDegree = picture.PerDegree;
        int px0 = (int)Math.Floor((id.West + 180.0) * perDegree) - 3;
        int py0 = (int)Math.Floor((90.0 - id.North) * perDegree) - 3;
        int w = (int)Math.Ceiling(id.SizeDeg * perDegree) + 7, h = w;
        double ratio = _cover.PixelsPerDegree / perDegree;
        long Edge(long p) => (long)Math.Floor(p * ratio + 1e-9);
        long cx0 = Edge(px0), cy0 = Edge(py0);
        int cw = (int)(Edge(px0 + w) - cx0), ch = (int)(Edge(py0 + h) - cy0);
        var classes = new byte[cw * ch];
        _cover.Read(cx0, cy0, cw, ch, classes);

        var r = new float[w * h];
        var g = new float[w * h];
        var b = new float[w * h];
        var known = new bool[w * h];
        Span<int> counts = stackalloc int[Cover.Count + 1];
        for (int y = 0; y < h; y++)
        {
            long py = py0 + y;
            double latitude = 90.0 - (py + 0.5) / perDegree;
            int y0 = (int)(Edge(py) - cy0), y1 = (int)(Edge(py + 1) - cy0);
            for (int x = 0; x < w; x++)
            {
                long px = px0 + x;
                double longitude = -180.0 + (px0 + x + 0.5) / perDegree;
                int x0 = (int)(Edge(px0 + x) - cx0), x1 = (int)(Edge(px0 + x + 1) - cx0);
                counts.Clear();
                for (int yy = y0; yy < y1; yy++)
                {
                    for (int xx = x0; xx < x1; xx++)
                    {
                        byte k = classes[yy * cw + xx];
                        counts[k == Cover.Unknown ? Cover.Count : k]++;
                    }
                }

                int i = y * w + x;
                // The pictures are plain white near the North Pole: no detail there.
                float trust = SmoothStep((float)_recipe.PictureNorthLimit, (float)_recipe.PictureNorthLimit - 0.5f, (float)latitude);
                if (trust <= 0f)
                {
                    r[i] = g[i] = b[i] = 1f;
                    known[i] = true;
                    continue;
                }

                int total = (x1 - x0) * (y1 - y0), unknown = counts[Cover.Count];
                int land = total - unknown - counts[Cover.Water];
                // Pixels with much water mix in the sea's colour; leave them to the neighbours.
                if (land * 10 < total * 7) continue;

                if (!picture.TryGet(px, py, out byte sr, out byte sg, out byte sb)) continue;
                int high = Math.Max(sr, Math.Max(sg, sb)), low = Math.Min(sr, Math.Min(sg, sb));
                bool snowy = low > 150 && high - low < 32;
                if (snowy && counts[Cover.Snow] * 2 < land) continue;

                float mr = 0, mg = 0, mb = 0;
                for (int c = 0; c < Cover.Count; c++)
                {
                    if (c == Cover.Water || counts[c] == 0) continue;
                    var colour = _colours.Sample(c, latitude, longitude);
                    float f = counts[c] / (float)land;
                    mr += f * colour.R;
                    mg += f * colour.G;
                    mb += f * colour.B;
                }

                r[i] = 1f + (Ratio(ColourTable.SrgbToLinear[sr], mr) - 1f) * trust;
                g[i] = 1f + (Ratio(ColourTable.SrgbToLinear[sg], mg) - 1f) * trust;
                b[i] = 1f + (Ratio(ColourTable.SrgbToLinear[sb], mb) - 1f) * trust;
                known[i] = true;
            }
        }

        if (!known.Any(k => k)) return null;
        FillUnknown(w, h, known, r, g, b);

        // Onto the tile's samples, and fade to 1 on steep ground.
        float[] heights = new float[Count];
        Array.Fill(heights, float.NaN);
        _elevation.Fill(id, heights);
        double step = id.SizeDeg / HeightTile.Intervals;
        double stepM = step * 111_195.0;
        var planes = new[] { new float[Count], new float[Count], new float[Count] };
        for (int row = 0; row < N; row++)
        {
            double latitude = id.South + row * step;
            double eastM = stepM * Math.Max(Math.Cos(latitude * Math.PI / 180.0), 0.05);
            double fy = (90.0 - latitude) * perDegree - 0.5 - py0;
            for (int column = 0; column < N; column++)
            {
                double fx = (id.West + column * step + 180.0) * perDegree - 0.5 - px0;
                int ix = Math.Clamp((int)Math.Floor(fx), 0, w - 2), iy = Math.Clamp((int)Math.Floor(fy), 0, h - 2);
                float tx = (float)Math.Clamp(fx - ix, 0, 1), ty = (float)Math.Clamp(fy - iy, 0, 1);
                float Bilinear(float[] p) =>
                    (p[iy * w + ix] * (1 - tx) + p[iy * w + ix + 1] * tx) * (1 - ty) +
                    (p[(iy + 1) * w + ix] * (1 - tx) + p[(iy + 1) * w + ix + 1] * tx) * ty;

                int at = row * N + column;
                float H(int rr, int cc) => float.IsNaN(heights[rr * N + cc]) ? 0f : Math.Max(heights[rr * N + cc], 0f);
                int c0 = Math.Max(column - 1, 0), c1 = Math.Min(column + 1, N - 1), r0 = Math.Max(row - 1, 0), r1 = Math.Min(row + 1, N - 1);
                double sx = (H(row, c1) - H(row, c0)) / ((c1 - c0) * eastM), sy = (H(r1, column) - H(r0, column)) / ((r1 - r0) * stepM);
                float steep = SmoothStep((float)_recipe.DetailSlopeStart, (float)_recipe.DetailSlopeEnd, (float)Math.Sqrt(sx * sx + sy * sy));
                planes[0][at] = Bilinear(r) + (1f - Bilinear(r)) * steep;
                planes[1][at] = Bilinear(g) + (1f - Bilinear(g)) * steep;
                planes[2][at] = Bilinear(b) + (1f - Bilinear(b)) * steep;
            }
        }

        return planes;
    }

    private static float Ratio(float picture, float predicted) =>
        Math.Clamp(picture / Math.Max(predicted, 0.004f), 0.4f, 1.95f);

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Spreads known values into the gaps, ring by ring; anything left becomes 1.</summary>
    private static void FillUnknown(int w, int h, bool[] known, params float[][] planes)
    {
        var has = (bool[])known.Clone();
        var next = new List<int>();
        for (int ring = 0; ring < 24; ring++)
        {
            next.Clear();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (has[y * w + x]) continue;
                    bool touches = (x > 0 && has[y * w + x - 1]) || (x < w - 1 && has[y * w + x + 1])
                                   || (y > 0 && has[(y - 1) * w + x]) || (y < h - 1 && has[(y + 1) * w + x]);
                    if (touches) next.Add(y * w + x);
                }
            }

            if (next.Count == 0) break;
            foreach (int at in next)
            {
                int x = at % w, y = at / w, found = 0;
                foreach (float[] p in planes) p[at] = 0f;
                foreach (int n in new[] { at - 1, at + 1, at - w, at + w })
                {
                    if (n < 0 || n >= w * h || !has[n] || (n == at - 1 && x == 0) || (n == at + 1 && x == w - 1)) continue;
                    found++;
                    foreach (float[] p in planes) p[at] += p[n];
                }

                foreach (float[] p in planes) p[at] /= Math.Max(found, 1);
            }

            foreach (int at in next) has[at] = true;
        }

        for (int i = 0; i < has.Length; i++)
        {
            if (has[i]) continue;
            foreach (float[] p in planes) p[i] = 1f;
        }
    }

    /// <summary>The parent of four tiles: a small weighted average, edge samples from edge samples only.</summary>
    private static float[][] Shrink(Built[] children)
    {
        const int fine = HeightTile.Intervals * 2 + 1;
        var result = new float[3][];
        for (int p = 0; p < 3; p++)
        {
            var grid = new float[fine * fine];
            for (int i = 0; i < 4; i++)
            {
                int offsetX = (i & 1) * HeightTile.Intervals, offsetY = (i >> 1) * HeightTile.Intervals;
                float[] plane = children[i].Planes?[p];
                for (int row = 0; row < N; row++)
                {
                    for (int column = 0; column < N; column++)
                    {
                        grid[(row + offsetY) * fine + column + offsetX] = plane == null ? 1f : plane[row * N + column];
                    }
                }
            }

            var heights = new float[Count];
            for (int row = 0; row < N; row++)
            {
                for (int column = 0; column < N; column++)
                {
                    int at = row * 2 * fine + column * 2;
                    bool edgeRow = row == 0 || row == N - 1, edgeColumn = column == 0 || column == N - 1;
                    float value;
                    if (edgeRow && edgeColumn) value = grid[at];
                    else if (edgeRow) value = (grid[at - 1] + 2f * grid[at] + grid[at + 1]) * 0.25f;
                    else if (edgeColumn) value = (grid[at - fine] + 2f * grid[at] + grid[at + fine]) * 0.25f;
                    else
                    {
                        value = (grid[at - fine - 1] + grid[at - fine + 1] + grid[at + fine - 1] + grid[at + fine + 1]
                                 + 2f * (grid[at - 1] + grid[at + 1] + grid[at - fine] + grid[at + fine]) + 4f * grid[at]) * 0.0625f;
                    }

                    heights[row * N + column] = value;
                }
            }

            result[p] = heights;
        }

        return result;
    }

    /// <summary>Ground (sq km) where the tile's multipliers differ from the tile above by more than DetailTolerance.</summary>
    private double ChangedKm2(float[][] up, float[][] child, TileId id)
    {
        int offsetX = (id.X & 1) * HeightTile.Intervals, offsetY = (id.Y & 1) * HeightTile.Intervals;
        const int fine = HeightTile.Intervals * 2 + 1;
        double stepKm = id.SizeDeg / HeightTile.Intervals * 111.195, changed = 0;
        float limit = (float)_recipe.DetailTolerance;
        for (int row = 0; row < N; row++)
        {
            double latitude = id.South + id.SizeDeg * row / HeightTile.Intervals;
            double area = stepKm * stepKm * Math.Max(Math.Cos(latitude * Math.PI / 180.0), 0.01);
            for (int column = 0; column < N; column++)
            {
                int at = row * N + column, above = (row + offsetY) * fine + column + offsetX;
                for (int p = 0; p < 3; p++)
                {
                    float parent = up == null ? 1f : up[p][above];
                    if (Math.Abs(child[p][at] - parent) > limit)
                    {
                        changed += area;
                        break;
                    }
                }
            }
        }

        return changed;
    }

    private void WriteTile(TileId id, float[][] planes)
    {
        var data = new byte[3 * Count];
        for (int p = 0; p < 3; p++)
        {
            for (int i = 0; i < Count; i++)
            {
                float value = planes == null ? 1f : planes[p][i];
                data[p * Count + i] = (byte)Math.Clamp(MathF.Round(value * Scale), 0f, 255f);
            }
        }

        byte[] file = new SurfaceTile(3, data).Encode(rgb => Jpeg.Encode(rgb, N, N, _recipe.ColourQuality));
        string path = Path.Combine(_output, id.PathWith(SurfaceTile.DetailExtension));
        string folder = Path.GetDirectoryName(path)!;
        if (_folders.TryAdd(folder, true)) Directory.CreateDirectory(folder);
        File.WriteAllBytes(path, file);
        Interlocked.Increment(ref _tiles[id.Level]);
        Interlocked.Add(ref _bytes[id.Level], file.Length);
    }
}
