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
/// Paints the ground: turns land cover, a colour table and the sea bed into two packs of
/// surface tiles, colour (.tnsc) and water (.tnsw). See SurfaceTile for what they hold.
///
/// Like the height baker, it builds the deepest tiles from the sources and every tile above by
/// shrinking the four below, and only writes a tile where it visibly changes the picture: the
/// coast moves, or the land's colour changes. Open sea, ice sheets and wide forests stay coarse.
/// </summary>
public sealed class SurfaceBaker
{
    private const int N = HeightTile.Samples;
    private const int Count = HeightTile.SampleCount;
    /// <summary>Extra samples read round a deepest tile, so land colour can spread over the water evenly on both sides of a tile edge.</summary>
    private const int Margin = 8;

    // Planes of a built tile.
    private const int R = 0, G = 1, B = 2, Land = 3, Water = 4, Tone = 5, Kind = 6, Planes = 7;
    /// <summary>Water tone of a lake or river.</summary>
    private const float LakeTone = 1f;

    private readonly SurfaceRecipe _recipe;
    private readonly LandCover _cover;
    private readonly LandCover _fill;
    private readonly HashSet<(int Lat, int Lon)> _openSquares;
    private DecisionLog _colourLog, _waterLog, _coverLog;

    /// <summary>
    /// Update mode: only the regions are rebaked, into packs that already hold the rest of the
    /// world. Tiles outside the regions are read from the packs instead of being built.
    /// </summary>
    private readonly bool _update;
    private readonly ColourTable _colours;
    private readonly IElevationSource _elevation;
    private readonly double[][] _regions;
    private readonly string _colourOutput, _waterOutput, _coverOutput;
    private readonly TerrainPackSet _heights;
    private readonly long[] _coverTiles = new long[TileId.MaxSupportedLevel + 1];
    private readonly long[] _coverBytes = new long[TileId.MaxSupportedLevel + 1];
    private readonly ConcurrentDictionary<string, bool> _folders = new();
    private readonly long[] _colourTiles = new long[TileId.MaxSupportedLevel + 1];
    private readonly long[] _colourBytes = new long[TileId.MaxSupportedLevel + 1];
    private readonly long[] _waterTiles = new long[TileId.MaxSupportedLevel + 1];
    private readonly long[] _waterBytes = new long[TileId.MaxSupportedLevel + 1];
    private int _regionsDone;

    private sealed class Built
    {
        public float[][] Planes;
        public bool ColourBelow, WaterBelow, CoverBelow;
        /// <summary>True if no sample has any land: open sea.</summary>
        public bool AllSea;
    }

    private readonly WorkTimer _work = new();

    public SurfaceBaker(SurfaceRecipe recipe, LandCover cover, ColourTable colours, IElevationSource elevation, double[][] regions,
        LandCover fill = null, bool update = false, HashSet<(int Lat, int Lon)> openSquares = null)
    {
        _recipe = recipe;
        _openSquares = openSquares;
        _cover = cover;
        _fill = fill;
        _update = update && regions != null;
        _colours = colours;
        _elevation = elevation;
        _regions = regions;
        _colourOutput = recipe.Resolve(recipe.ColourOutput);
        _waterOutput = recipe.Resolve(recipe.WaterOutput);
        _coverOutput = recipe.Resolve(recipe.CoverOutput);
        if (!string.IsNullOrWhiteSpace(recipe.CoverMatchHeights))
        {
            _heights = new TerrainPackSet();
            int tiles = _heights.AddPack(recipe.Resolve(recipe.CoverMatchHeights));
            if (tiles == 0) throw new IOException($"coverMatchHeights: no height tiles in {recipe.Resolve(recipe.CoverMatchHeights)}");
        }
    }

    public void Run(int threads)
    {
        var timer = Stopwatch.StartNew();
        Console.WriteLine($"Painting \"{_recipe.Name}\": land cover at levels {_recipe.CoverMinLevel}-{_recipe.MaxLevel}, " +
                          $"blended colour to level {_recipe.ColourMaxLevel}, water to level {_recipe.WaterMaxLevel}");
        Directory.CreateDirectory(_colourOutput);
        Directory.CreateDirectory(_waterOutput);
        Directory.CreateDirectory(_coverOutput);
        _colours.SaveAtlas(Path.Combine(_coverOutput, ColourTable.AtlasFileName));
        if (_update)
        {
            int removed = PackUpdate.DeleteInside(_coverOutput, SurfaceTile.CoverExtension, _regions)
                          + PackUpdate.DeleteInside(_colourOutput, SurfaceTile.ColourExtension, _regions)
                          + PackUpdate.DeleteInside(_waterOutput, SurfaceTile.WaterExtension, _regions);
            Console.WriteLine($"  update: removed {removed} old tiles inside the boxes; the rest of the world is read from the packs");
        }

        // An update re-decides only its boxes and the tiles above them, so it starts from the saved records.
        _colourLog = new DecisionLog(_colourOutput, _update);
        _waterLog = new DecisionLog(_waterOutput, _update);
        _coverLog = new DecisionLog(_coverOutput, _update);

        var roots = new List<TileId>();
        for (int y = 0; y < TileId.RowsAt(3); y++)
        {
            for (int x = 0; x < TileId.ColumnsAt(3); x++) roots.Add(new TileId(3, x, y));
        }

        // Land first: those regions take longest, so the others fill in round them.
        roots = roots.OrderByDescending(r => _cover.Covers(r.West, r.South, r.East, r.North)).ToList();
        var finished = new ConcurrentDictionary<TileId, Built>();
        Parallel.ForEach(roots, new ParallelOptions { MaxDegreeOfParallelism = threads }, root =>
        {
            finished[root] = Build(root, null);
            int done = Interlocked.Increment(ref _regionsDone);
            Console.Write($"\r  {done}/{roots.Count} regions, {_coverTiles.Sum()} cover, {_colourTiles.Sum()} colour and {_waterTiles.Sum()} water tiles written   ");
        });
        Console.WriteLine();

        foreach (TileId top in new[] { new TileId(0, 0, 0), new TileId(0, 1, 0) })
        {
            if (_update && !InRegion(top)) continue;
            Built built = Build(top, finished);
            WriteColour(top, built.Planes);
            WriteWater(top, built.Planes);
        }

        if (_update)
        {
            // The pack files describe the whole pack, not just what this update wrote.
            var (tiles, bytes) = PackUpdate.Count(_colourOutput, SurfaceTile.ColourExtension);
            WriteInfo(_colourOutput, "colour", tiles, bytes, timer.Elapsed);
            (tiles, bytes) = PackUpdate.Count(_waterOutput, SurfaceTile.WaterExtension);
            WriteInfo(_waterOutput, "water", tiles, bytes, timer.Elapsed);
            (tiles, bytes) = PackUpdate.Count(_coverOutput, SurfaceTile.CoverExtension);
            WriteInfo(_coverOutput, "cover", tiles, bytes, timer.Elapsed);
            Console.WriteLine("  written by this update:");
        }
        else
        {
            WriteInfo(_colourOutput, "colour", _colourTiles, _colourBytes, timer.Elapsed);
            WriteInfo(_waterOutput, "water", _waterTiles, _waterBytes, timer.Elapsed);
            WriteInfo(_coverOutput, "cover", _coverTiles, _coverBytes, timer.Elapsed);
        }

        _colourLog.Save();
        _waterLog.Save();
        _coverLog.Save();
        _work.Print("Ground");
        Console.WriteLine("  level    cover tiles        size  colour tiles        size   water tiles        size");
        for (int level = 0; level <= _recipe.MaxLevel; level++)
        {
            Console.WriteLine($"  {level,5} {_coverTiles[level],14} {_coverBytes[level] / 1e6,8:0.0} MB {_colourTiles[level],13} {_colourBytes[level] / 1e6,8:0.0} MB " +
                              $"{_waterTiles[level],13} {_waterBytes[level] / 1e6,8:0.0} MB");
        }

        long total = _coverBytes.Sum() + _colourBytes.Sum() + _waterBytes.Sum();
        Console.WriteLine($"  total {_coverTiles.Sum(),14} {_coverBytes.Sum() / 1e6,8:0.0} MB {_colourTiles.Sum(),13} {_colourBytes.Sum() / 1e6,8:0.0} MB " +
                          $"{_waterTiles.Sum(),13} {_waterBytes.Sum() / 1e6,8:0.0} MB   all {total / 1e6:0.0} MB in {timer.Elapsed.TotalMinutes:0.0} min");
    }

    /// <summary>
    /// How deep land cover goes here: CoverMaxLevel, or as deep as the heights where they are
    /// finer (the great mountain ranges), so treelines and snowlines match the terrain.
    /// </summary>
    private int CoverMaxLevelAt(TileId id)
    {
        int level = _recipe.CoverMaxLevel;
        if (_heights != null)
        {
            int heightLevel = _heights.MaxDetailLevelFor(id);
            // Plains keep CoverMaxLevel. Where the heights have more detail than plains do, the
            // land cover goes coverLevelsBeyondHeights finer still, because a mountainside's
            // patches of forest, meadow, rock and snow are smaller than its slopes.
            if (heightLevel > _recipe.CoverPlainsHeightLevel) heightLevel += _recipe.CoverLevelsBeyondHeights;
            level = Math.Max(level, heightLevel);
        }

        return Math.Min(level, _recipe.MaxLevel);
    }

    private bool InRegion(TileId id) =>
        _regions == null || _regions.Any(r => id.East > r[0] && id.West < r[2] && id.North > r[1] && id.South < r[3]);

    private bool HasCover(TileId id)
    {
        double pad = id.SizeDeg / HeightTile.Intervals * (Margin + 1);
        return _cover.Covers(id.West - pad, Math.Max(id.South - pad, -90), id.East + pad, Math.Min(id.North + pad, 90));
    }

    /// <summary>The level a branch's deepest tiles are built at.</summary>
    private int LeafLevel(TileId id)
    {
        if (!InRegion(id)) return id.Level;
        if (HasCover(id)) return _recipe.MaxLevel;
        // No land cover: open sea, where the sea bed is all there is to show, or Antarctica.
        return Math.Min(id.South < -60 ? 7 : 5, _recipe.MaxLevel);
    }

    /// <summary>How deep blended colour goes here: deeper on open ground (OpenSquares).</summary>
    private int ColourMaxLevelAt(TileId id) =>
        _openSquares != null && id.Level > _recipe.ColourMaxLevel && SquareLimitedSource.Touches(_openSquares, id)
            ? _recipe.ColourOpenMaxLevel
            : _recipe.ColourMaxLevel;

    private Built Build(TileId id, ConcurrentDictionary<TileId, Built> finished)
    {
        if (finished != null && finished.TryGetValue(id, out Built done)) return done;
        if (_update && !InRegion(id)) return FromPack(id);
        if (id.Level >= LeafLevel(id) && id.Level >= Math.Min(3, _recipe.MaxLevel))
        {
            long readStarted = _work.Start();
            bool fromCover = InRegion(id) && HasCover(id);
            float[][] planes = fromCover ? FromLandCover(id) : FromElevation(id);
            bool sea = planes[Land].All(l => l <= 0f);
            _work.Stop($"build from {(fromCover ? "land cover" : "elevation")}: {(sea ? "all-sea" : "land")} tile, level {id.Level}", readStarted);
            return new Built { Planes = planes, AllSea = sea };
        }

        var children = new Built[4];
        var childIds = new TileId[4];
        for (int i = 0; i < 4; i++)
        {
            childIds[i] = id.Child(i & 1, i >> 1);
            children[i] = Build(childIds[i], finished);
        }

        long started = _work.Start();
        bool allSea = children.All(c => c.AllSea);
        var parent = Shrink(children);
        if (_update && !PackUpdate.Inside(id, _regions)) KeepOutside(id, parent);
        var result = new Built { Planes = parent, AllSea = allSea };
        float[][] up = null;
        for (int i = 0; i < 4; i++)
        {
            TileId child = childIds[i];
            if (!InRegion(child))
            {
                // Update: a stored tile outside the boxes still needs its ancestors kept.
                result.ColourBelow |= children[i].ColourBelow;
                result.WaterBelow |= children[i].WaterBelow;
                result.CoverBelow |= children[i].CoverBelow;
                continue;
            }

            up ??= parent.Select(DetailMeasure.Upsample).ToArray();
            bool forced = child.Level <= _recipe.FullLevel;

            // Each tile is measured whether or not it is kept, so the decision logs show how close
            // every tile came to its limit.
            if (child.Level <= ColourMaxLevelAt(child))
            {
                double changed = ColourChangedKm2(up, children[i].Planes, child);
                bool below = children[i].ColourBelow;
                bool keep = forced || below || changed > _recipe.ColourAreaKm2;
                _colourLog.Add(child, changed, _recipe.ColourAreaKm2, keep, below ? 'b' : forced ? 'f' : 'm');
                if (keep)
                {
                    WriteColour(child, children[i].Planes);
                    result.ColourBelow = true;
                }
                else if (_update) PackUpdate.DeleteIfThere(_colourOutput, child, SurfaceTile.ColourExtension);
            }
            else if (_update) PackUpdate.DeleteIfThere(_colourOutput, child, SurfaceTile.ColourExtension);

            if (child.Level <= _recipe.WaterMaxLevel)
            {
                double changed = CoastMovedKm2(up, children[i].Planes, child);
                bool below = children[i].WaterBelow;
                bool keep = forced || below || changed > _recipe.WaterAreaKm2;
                _waterLog.Add(child, changed, _recipe.WaterAreaKm2, keep, below ? 'b' : forced ? 'f' : 'm');
                if (keep)
                {
                    WriteWater(child, children[i].Planes);
                    result.WaterBelow = true;
                }
                else if (_update) PackUpdate.DeleteIfThere(_waterOutput, child, SurfaceTile.WaterExtension);
            }
            else if (_update) PackUpdate.DeleteIfThere(_waterOutput, child, SurfaceTile.WaterExtension);

            // Land cover: every tile with any land at the first cover level, so the sharp view
            // is available everywhere on land; below that only where the edges move.
            if (child.Level >= _recipe.CoverMinLevel && child.Level <= CoverMaxLevelAt(child))
            {
                double changed = CoverChangedKm2(parent[Kind], children[i].Planes[Kind], child);
                bool below = children[i].CoverBelow;
                bool first = child.Level == _recipe.CoverMinLevel;
                bool keep = below || (first ? children[i].Planes[Land].Any(l => l > 0f) : changed > _recipe.CoverAreaKm2);
                _coverLog.Add(child, changed, _recipe.CoverAreaKm2, keep, below ? 'b' : first && keep ? 'f' : 'm');
                if (keep)
                {
                    WriteCover(child, children[i].Planes);
                    result.CoverBelow = true;
                }
                else if (_update) PackUpdate.DeleteIfThere(_coverOutput, child, SurfaceTile.CoverExtension);
            }
        }

        _work.Stop(allSea ? "average, measure, write: all four children all sea" : "average, measure, write: some land", started);
        return result;
    }

    /// <summary>
    /// Update mode: a tile outside the boxes, as the packs already hold it. The boxes are whole
    /// tiles of PackUpdate.SnapLevel, so this is only asked for at that level or coarser, where
    /// the colour pack is complete.
    /// </summary>
    private Built FromPack(TileId id)
    {
        float[][] planes = NewPlanes();
        float[][] colour = PackUpdate.Read(_colourOutput, id, SurfaceTile.ColourExtension, out bool colourStored);
        float[][] water = PackUpdate.Read(_waterOutput, id, SurfaceTile.WaterExtension, out bool waterStored);
        if (colour == null || water == null)
        {
            throw new InvalidDataException($"--update needs the colour and water packs already baked; tile {id} has neither itself nor an ancestor.");
        }

        for (int i = 0; i < Count; i++)
        {
            planes[R][i] = SrgbToLinear(colour[0][i]);
            planes[G][i] = SrgbToLinear(colour[1][i]);
            planes[B][i] = SrgbToLinear(colour[2][i]);
            planes[Tone][i] = colour[3][i] / 255f;
            planes[Water][i] = water[0][i] / 255f;
            planes[Land][i] = 1f - planes[Water][i];
            planes[Kind][i] = planes[Water][i] >= 0.5f ? Cover.Water : Cover.Bare;
        }

        return new Built
        {
            Planes = planes,
            ColourBelow = colourStored,
            WaterBelow = waterStored,
            CoverBelow = File.Exists(Path.Combine(_coverOutput, id.PathWith(SurfaceTile.CoverExtension))),
        };
    }

    /// <summary>
    /// Update mode, for a tile reaching outside the boxes: outside them, the values already in the
    /// packs replace the freshly averaged ones. Averaging the stored (rounded, JPEG) tiles again
    /// would drift by a few levels; this way nothing outside the boxes changes.
    /// </summary>
    private void KeepOutside(TileId id, float[][] planes)
    {
        bool[] outside = PackUpdate.OutsideSamples(id, _regions);
        float[][] colour = PackUpdate.Read(_colourOutput, id, SurfaceTile.ColourExtension, out _);
        float[][] water = PackUpdate.Read(_waterOutput, id, SurfaceTile.WaterExtension, out _);
        for (int i = 0; i < Count; i++)
        {
            if (!outside[i]) continue;
            if (colour != null)
            {
                planes[R][i] = SrgbToLinear(colour[0][i]);
                planes[G][i] = SrgbToLinear(colour[1][i]);
                planes[B][i] = SrgbToLinear(colour[2][i]);
                planes[Tone][i] = colour[3][i] / 255f;
            }

            if (water != null)
            {
                planes[Water][i] = water[0][i] / 255f;
                planes[Land][i] = 1f - planes[Water][i];
            }
        }
    }

    private static float SrgbToLinear(float srgb)
    {
        int low = Math.Clamp((int)srgb, 0, 254);
        float t = Math.Clamp(srgb - low, 0f, 1f);
        return ColourTable.SrgbToLinear[low] + (ColourTable.SrgbToLinear[low + 1] - ColourTable.SrgbToLinear[low]) * t;
    }

    // ---- deepest tiles ----

    private float[][] NewPlanes() => Enumerable.Range(0, Planes).Select(_ => new float[Count]).ToArray();

    /// <summary>A deepest tile from the land cover: what share of the ground round each sample is water, and the colour of the rest.</summary>
    private float[][] FromLandCover(TileId id)
    {
        int n = N + Margin * 2;
        double step = id.SizeDeg / HeightTile.Intervals;
        int perDegree = _cover.PixelsPerDegree;
        double westEdge = id.West - (Margin + 1) * step, northEdge = id.North + (Margin + 1) * step;
        long x0 = (long)Math.Floor((westEdge + 180.0) * perDegree), y0 = (long)Math.Floor((90.0 - northEdge) * perDegree);
        int width = (int)Math.Ceiling((n + 2) * step * perDegree) + 2, height = width;
        var classes = new byte[width * height];
        _cover.Read(x0, y0, width, height, classes);
        double half = step * perDegree * 0.5;

        // Where WorldCover has nothing, the fill's kinds of ground (snow or bare in the far north).
        byte[] fillClasses = null;
        double pad = (Margin + 1) * step;
        if (_fill != null && _fill.Covers(id.West - pad, Math.Max(id.South - pad, -90), id.East + pad, Math.Min(id.North + pad, 90)))
        {
            fillClasses = new byte[width * height];
            _fill.Read(x0, y0, width, height, fillClasses);
        }

        var fillKind = new sbyte[n * n];
        var fillR = new float[n * n];
        var fillG = new float[n * n];
        var fillB = new float[n * n];
        Span<int> fillCounts = stackalloc int[Cover.Count + 1];

        var landR = new float[n * n];
        var landG = new float[n * n];
        var landB = new float[n * n];
        var hasLand = new bool[n * n];
        var water = new float[n * n];
        var landShare = new float[n * n];
        var unknown = new bool[n * n];
        var kind = new float[n * n];
        Span<int> counts = stackalloc int[Cover.Count + 1];

        for (int j = 0; j < n; j++)
        {
            double latitude = id.South + (j - Margin) * step;
            double cy = (90.0 - latitude) * perDegree - y0;
            int py0 = Math.Max((int)Math.Ceiling(cy - half - 0.5), 0), py1 = Math.Min((int)Math.Floor(cy + half - 0.5), height - 1);
            if (py1 < py0) py0 = py1 = Math.Clamp((int)Math.Floor(cy), 0, height - 1);
            for (int i = 0; i < n; i++)
            {
                double longitude = id.West + (i - Margin) * step;
                double cx = (longitude + 180.0) * perDegree - x0;
                int px0 = Math.Max((int)Math.Ceiling(cx - half - 0.5), 0), px1 = Math.Min((int)Math.Floor(cx + half - 0.5), width - 1);
                if (px1 < px0) px0 = px1 = Math.Clamp((int)Math.Floor(cx), 0, width - 1);

                counts.Clear();
                for (int y = py0; y <= py1; y++)
                {
                    int row = y * width;
                    for (int x = px0; x <= px1; x++)
                    {
                        byte k = classes[row + x];
                        counts[k == Cover.Unknown ? Cover.Count : k]++;
                    }
                }

                int at = j * n + i;
                int known = (px1 - px0 + 1) * (py1 - py0 + 1) - counts[Cover.Count];
                if (known == 0)
                {
                    unknown[at] = true;
                    fillKind[at] = -1;
                    if (fillClasses == null) continue;
                    fillCounts.Clear();
                    for (int y = py0; y <= py1; y++)
                    {
                        int row = y * width;
                        for (int x = px0; x <= px1; x++)
                        {
                            byte k = fillClasses[row + x];
                            if (k != Cover.Unknown && k != Cover.Water) fillCounts[k]++;
                        }
                    }

                    int filled = 0, top = -1;
                    for (int c = 0; c < Cover.Count; c++)
                    {
                        filled += fillCounts[c];
                        if (fillCounts[c] > 0 && (top < 0 || fillCounts[c] > fillCounts[top])) top = c;
                    }

                    if (top < 0) continue;
                    fillKind[at] = (sbyte)top;
                    for (int c = 0; c < Cover.Count; c++)
                    {
                        if (fillCounts[c] == 0) continue;
                        var colour = _colours.Sample(c, latitude, longitude);
                        float w = fillCounts[c] / (float)filled;
                        fillR[at] += colour.R * w;
                        fillG[at] += colour.G * w;
                        fillB[at] += colour.B * w;
                    }

                    continue;
                }

                int land = known - counts[Cover.Water];
                water[at] = counts[Cover.Water] / (float)known;
                landShare[at] = land / (float)known;
                int most = 0;
                for (int c = 1; c < Cover.Count; c++)
                {
                    if (counts[c] > counts[most]) most = c;
                }

                kind[at] = most;
                if (land == 0) continue;

                float r = 0, g = 0, b = 0;
                for (int c = 0; c < Cover.Count; c++)
                {
                    if (c == Cover.Water || counts[c] == 0) continue;
                    var colour = _colours.Sample(c, latitude, longitude);
                    float w = counts[c] / (float)land;
                    r += colour.R * w;
                    g += colour.G * w;
                    b += colour.B * w;
                }

                landR[at] = r;
                landG[at] = g;
                landB[at] = b;
                hasLand[at] = true;
            }
        }

        // Ground no land cover file reaches (a gap, or the edge of Antarctica) follows the elevation.
        float[] heights = ElevationOf(id);
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int at = j * n + i;
                if (!unknown[at]) continue;
                float h = heights[Math.Clamp(j - Margin, 0, N - 1) * N + Math.Clamp(i - Margin, 0, N - 1)];
                bool sea = h <= 0f;
                // A soft edge a few metres either side of sea level, so the coast is not stepped.
                water[at] = Math.Clamp(0.5f - h / 10f, 0f, 1f);
                landShare[at] = 1f - water[at];
                double latitude = id.South + (j - Margin) * step, longitude = id.West + (i - Margin) * step;
                if (sea)
                {
                    kind[at] = Cover.Water;
                    continue;
                }

                if (fillKind[at] >= 0)
                {
                    kind[at] = fillKind[at];
                    (landR[at], landG[at], landB[at]) = (fillR[at], fillG[at], fillB[at]);
                }
                else
                {
                    int guess = latitude < -60 ? Cover.Snow : Cover.Grass;
                    kind[at] = guess;
                    (landR[at], landG[at], landB[at]) = _colours.Sample(guess, latitude, longitude);
                }

                hasLand[at] = true;
            }
        }

        Spread(n, hasLand, Margin - 1, landR, landG, landB);

        float[][] planes = NewPlanes();
        for (int row = 0; row < N; row++)
        {
            for (int column = 0; column < N; column++)
            {
                int at = (row + Margin) * n + column + Margin, to = row * N + column;
                planes[R][to] = landR[at];
                planes[G][to] = landG[at];
                planes[B][to] = landB[at];
                planes[Land][to] = landShare[at];
                planes[Water][to] = water[at];
                planes[Kind][to] = kind[at];
            }
        }

        FillTone(planes, heights);
        return planes;
    }

    /// <summary>A deepest tile from the elevation alone: open sea, and Antarctica, which has no land cover.</summary>
    private float[][] FromElevation(TileId id)
    {
        float[] heights = ElevationOf(id);
        float[][] planes = NewPlanes();
        double step = id.SizeDeg / HeightTile.Intervals;
        for (int row = 0; row < N; row++)
        {
            double latitude = id.South + row * step;
            for (int column = 0; column < N; column++)
            {
                int at = row * N + column;
                bool sea = heights[at] <= 0f;
                planes[Water][at] = sea ? 1f : 0f;
                planes[Land][at] = sea ? 0f : 1f;
                planes[Kind][at] = sea ? Cover.Water : latitude < -60 ? Cover.Snow : Cover.Grass;
                var colour = _colours.Sample(latitude < -60 ? Cover.Snow : Cover.Grass, latitude, id.West + column * step);
                planes[R][at] = colour.R;
                planes[G][at] = colour.G;
                planes[B][at] = colour.B;
            }
        }

        FillTone(planes, heights);
        return planes;
    }

    private float[] ElevationOf(TileId id)
    {
        var heights = new float[Count];
        Array.Fill(heights, float.NaN);
        _elevation.Fill(id, heights);
        for (int i = 0; i < Count; i++)
        {
            if (float.IsNaN(heights[i])) heights[i] = 0f;
        }

        return heights;
    }

    /// <summary>
    /// The water's tone: lighter over shallow sea, darker over deep sea, and a lake tone for water
    /// above sea level. Spread a few samples onto the land, so the colour at a coast is the
    /// water's own.
    /// </summary>
    private void FillTone(float[][] planes, float[] heights)
    {
        var has = new bool[Count];
        float[] tone = planes[Tone];
        for (int i = 0; i < Count; i++)
        {
            if (planes[Water][i] <= 0f) continue;
            has[i] = true;
            float h = heights[i];
            tone[i] = h > _recipe.LakeAboveM
                ? LakeTone
                : (float)(Math.Sqrt(Math.Clamp(-h / _recipe.DeepestSeaM, 0.0, 1.0)) * 254.0 / 255.0);
        }

        Spread(N, has, 6, tone);
    }

    /// <summary>
    /// Carries values outward from the samples that have them, one ring at a time, each new
    /// sample taking the average of its neighbours that already have one. Anything still empty
    /// afterwards takes the average of all known samples.
    /// </summary>
    private static void Spread(int n, bool[] has, int rings, params float[][] planes)
    {
        var known = (bool[])has.Clone();
        var next = new List<int>();
        Span<float> sums = stackalloc float[planes.Length];
        for (int ring = 0; ring < rings; ring++)
        {
            next.Clear();
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int at = j * n + i;
                    if (known[at]) continue;
                    bool touches = false;
                    for (int dj = -1; dj <= 1 && !touches; dj++)
                    {
                        for (int di = -1; di <= 1 && !touches; di++)
                        {
                            int y = j + dj, x = i + di;
                            if (x >= 0 && y >= 0 && x < n && y < n && known[y * n + x]) touches = true;
                        }
                    }

                    if (touches) next.Add(at);
                }
            }

            if (next.Count == 0) break;
            foreach (int at in next)
            {
                int j = at / n, i = at % n, found = 0;
                sums.Clear();
                for (int dj = -1; dj <= 1; dj++)
                {
                    for (int di = -1; di <= 1; di++)
                    {
                        int y = j + dj, x = i + di;
                        if (x < 0 || y < 0 || x >= n || y >= n || !known[y * n + x]) continue;
                        found++;
                        for (int p = 0; p < planes.Length; p++) sums[p] += planes[p][y * n + x];
                    }
                }

                for (int p = 0; p < planes.Length; p++) planes[p][at] = sums[p] / found;
            }

            foreach (int at in next) known[at] = true;
        }

        int total = 0;
        Span<double> mean = stackalloc double[planes.Length];
        for (int at = 0; at < known.Length; at++)
        {
            if (!known[at]) continue;
            total++;
            for (int p = 0; p < planes.Length; p++) mean[p] += planes[p][at];
        }

        if (total == 0 || total == known.Length) return;
        for (int at = 0; at < known.Length; at++)
        {
            if (known[at]) continue;
            for (int p = 0; p < planes.Length; p++) planes[p][at] = (float)(mean[p] / total);
        }
    }

    // ---- shrinking ----

    /// <summary>
    /// A tile from the four below it, with the same small weighted average as the height baker
    /// (edge samples from edge samples only, so neighbours agree). Land colour is averaged over
    /// land only and the water tone over water only, so neither washes out the other.
    /// </summary>
    private static float[][] Shrink(Built[] children)
    {
        const int fine = HeightTile.Intervals * 2 + 1;
        var grids = new float[Planes][];
        for (int p = 0; p < Planes; p++)
        {
            grids[p] = new float[fine * fine];
            for (int i = 0; i < 4; i++)
            {
                int offsetX = (i & 1) * HeightTile.Intervals, offsetY = (i >> 1) * HeightTile.Intervals;
                for (int row = 0; row < N; row++)
                {
                    Array.Copy(children[i].Planes[p], row * N, grids[p], (row + offsetY) * fine + offsetX, N);
                }
            }
        }

        var result = Enumerable.Range(0, Planes).Select(_ => new float[Count]).ToArray();
        Span<int> taps = stackalloc int[9];
        Span<float> weights = stackalloc float[9];
        Span<float> kindWeights = stackalloc float[Cover.Count];
        for (int row = 0; row < N; row++)
        {
            for (int column = 0; column < N; column++)
            {
                int at = row * 2 * fine + column * 2;
                bool edgeRow = row == 0 || row == N - 1, edgeColumn = column == 0 || column == N - 1;
                int count;
                if (edgeRow && edgeColumn)
                {
                    taps[0] = at;
                    weights[0] = 1f;
                    count = 1;
                }
                else if (edgeRow || edgeColumn)
                {
                    int along = edgeRow ? 1 : fine;
                    taps[0] = at - along; taps[1] = at; taps[2] = at + along;
                    weights[0] = 0.25f; weights[1] = 0.5f; weights[2] = 0.25f;
                    count = 3;
                }
                else
                {
                    count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            taps[count] = at + dy * fine + dx;
                            weights[count] = (dx == 0 ? 2f : 1f) * (dy == 0 ? 2f : 1f) / 16f;
                            count++;
                        }
                    }
                }

                float land = 0, water = 0, r = 0, g = 0, b = 0, plainR = 0, plainG = 0, plainB = 0, tone = 0, plainTone = 0;
                for (int t = 0; t < count; t++)
                {
                    int k = taps[t];
                    float w = weights[t], l = grids[Land][k], wa = grids[Water][k];
                    land += w * l;
                    water += w * wa;
                    r += w * l * grids[R][k];
                    g += w * l * grids[G][k];
                    b += w * l * grids[B][k];
                    plainR += w * grids[R][k];
                    plainG += w * grids[G][k];
                    plainB += w * grids[B][k];
                    tone += w * wa * grids[Tone][k];
                    plainTone += w * grids[Tone][k];
                }

                int to = row * N + column;
                result[Land][to] = land;
                result[Water][to] = water;
                if (land > 1e-4f)
                {
                    result[R][to] = r / land;
                    result[G][to] = g / land;
                    result[B][to] = b / land;
                }
                else
                {
                    result[R][to] = plainR;
                    result[G][to] = plainG;
                    result[B][to] = plainB;
                }

                result[Tone][to] = water > 1e-4f ? tone / water : plainTone;

                // The kind of ground: the one with most weight among the samples averaged.
                kindWeights.Clear();
                for (int t = 0; t < count; t++) kindWeights[(int)grids[Kind][taps[t]]] += weights[t];
                int most = 0;
                for (int c = 1; c < Cover.Count; c++)
                {
                    if (kindWeights[c] > kindWeights[most]) most = c;
                }

                result[Kind][to] = most;
            }
        }

        return result;
    }

    // ---- what is worth storing ----

    private static double SampleAreaKm2(TileId id, int row)
    {
        double stepKm = id.SizeDeg / HeightTile.Intervals * 111.195;
        double latitude = id.South + id.SizeDeg * row / HeightTile.Intervals;
        return stepKm * stepKm * Math.Max(Math.Cos(latitude * Math.PI / 180.0), 0.01);
    }

    /// <summary>Ground (sq km) where the tile shows land or water colours the tile above cannot.</summary>
    private double ColourChangedKm2(float[][] up, float[][] child, TileId id)
    {
        int offsetX = (id.X & 1) * HeightTile.Intervals, offsetY = (id.Y & 1) * HeightTile.Intervals;
        const int fine = HeightTile.Intervals * 2 + 1;
        // Compare in square-root space, close to how different colours look.
        float limit = (float)_recipe.ColourTolerance;
        double changed = 0;
        for (int row = 0; row < N; row++)
        {
            double area = SampleAreaKm2(id, row);
            for (int column = 0; column < N; column++)
            {
                int at = row * N + column, above = (row + offsetY) * fine + column + offsetX;
                bool differs = false;
                if (child[Land][at] > 0.25f)
                {
                    for (int p = R; p <= B && !differs; p++)
                    {
                        differs = Math.Abs(MathF.Sqrt(child[p][at]) - MathF.Sqrt(Math.Max(up[p][above], 0f))) > limit;
                    }
                }

                if (!differs && child[Water][at] > 0.25f)
                {
                    differs = Math.Abs(child[Tone][at] - up[Tone][above]) > limit * 2f;
                }

                if (differs) changed += area;
            }
        }

        return changed;
    }

    /// <summary>Ground (sq km) that, drawn from the tile above, would land on the wrong side of the coast.</summary>
    private double CoastMovedKm2(float[][] up, float[][] child, TileId id)
    {
        int offsetX = (id.X & 1) * HeightTile.Intervals, offsetY = (id.Y & 1) * HeightTile.Intervals;
        const int fine = HeightTile.Intervals * 2 + 1;
        double changed = 0;
        for (int row = 0; row < N; row++)
        {
            double area = SampleAreaKm2(id, row);
            for (int column = 0; column < N; column++)
            {
                bool wet = child[Water][row * N + column] >= 0.5f;
                bool wetAbove = up[Water][(row + offsetY) * fine + column + offsetX] >= 0.5f;
                if (wet != wetAbove) changed += area;
            }
        }

        return changed;
    }

    /// <summary>
    /// Ground (sq km) where the tile shows the kinds of ground differently from the tile above,
    /// drawn the way the game draws them (sharp edges, see CoverDrawing.Pick).
    /// </summary>
    private double CoverChangedKm2(float[] parentKinds, float[] childKinds, TileId id)
    {
        double offsetX = (id.X & 1) * HeightTile.Intervals * 0.5, offsetY = (id.Y & 1) * HeightTile.Intervals * 0.5;
        double changed = 0;
        for (int row = 0; row < N; row++)
        {
            double area = SampleAreaKm2(id, row);
            for (int column = 0; column < N; column++)
            {
                // Only the kind of land counts: where the water is comes from the water tiles.
                int own = (int)childKinds[row * N + column];
                if (own == Cover.Water) continue;
                int above = CoverDrawing.Pick(parentKinds, offsetX + column * 0.5, offsetY + row * 0.5);
                if (above >= 0 && above != own) changed += area;
            }
        }

        return changed;
    }

    // ---- writing ----

    private void WriteCover(TileId id, float[][] planes)
    {
        var data = new byte[Count];
        for (int i = 0; i < Count; i++) data[i] = (byte)planes[Kind][i];
        long bytes = Write(_coverOutput, id.PathWith(SurfaceTile.CoverExtension), new SurfaceTile(1, data).Encode(predict: false));
        Interlocked.Increment(ref _coverTiles[id.Level]);
        Interlocked.Add(ref _coverBytes[id.Level], bytes);
    }

    private void WriteColour(TileId id, float[][] planes)
    {
        var data = new byte[4 * Count];
        for (int i = 0; i < Count; i++)
        {
            data[i] = ColourTable.LinearToSrgb(planes[R][i]);
            data[Count + i] = ColourTable.LinearToSrgb(planes[G][i]);
            data[2 * Count + i] = ColourTable.LinearToSrgb(planes[B][i]);
            data[3 * Count + i] = (byte)Math.Round(Math.Clamp(planes[Tone][i], 0f, 1f) * 255f);
        }

        long bytes = Write(_colourOutput, id.PathWith(SurfaceTile.ColourExtension),
            new SurfaceTile(4, data).Encode(rgb => Jpeg.Encode(rgb, N, N, _recipe.ColourQuality)));
        Interlocked.Increment(ref _colourTiles[id.Level]);
        Interlocked.Add(ref _colourBytes[id.Level], bytes);
    }

    private void WriteWater(TileId id, float[][] planes)
    {
        var data = new byte[Count];
        // Rounded to a few steps: the coast still lands within a fraction of a sample of the
        // right place, and the tiles compress far better.
        float steps = Math.Clamp(_recipe.WaterSteps, 1, 255);
        for (int i = 0; i < Count; i++)
        {
            float share = MathF.Round(Math.Clamp(planes[Water][i], 0f, 1f) * steps) / steps;
            data[i] = (byte)MathF.Round(share * 255f);
        }
        long bytes = Write(_waterOutput, id.PathWith(SurfaceTile.WaterExtension), new SurfaceTile(1, data).Encode());
        Interlocked.Increment(ref _waterTiles[id.Level]);
        Interlocked.Add(ref _waterBytes[id.Level], bytes);
    }

    private long Write(string root, string relative, byte[] file)
    {
        string path = Path.Combine(root, relative);
        string folder = Path.GetDirectoryName(path)!;
        if (_folders.TryAdd(folder, true)) Directory.CreateDirectory(folder);
        File.WriteAllBytes(path, file);
        return file.Length;
    }

    private void WriteInfo(string folder, string kind, long[] tiles, long[] bytes, TimeSpan elapsed)
    {
        var info = new Dictionary<string, object>
        {
            ["format"] = 1,
            ["kind"] = kind,
            ["name"] = _recipe.Name,
            ["tileSamples"] = N,
            ["maxLevel"] = Array.FindLastIndex(tiles, t => t > 0),
            ["tiles"] = tiles.Sum(),
            ["bytes"] = bytes.Sum(),
            ["landCover"] = _cover.Name,
            ["colourTolerance"] = _recipe.ColourTolerance,
            ["colourAreaKm2"] = _recipe.ColourAreaKm2,
            ["waterAreaKm2"] = _recipe.WaterAreaKm2,
            ["bakedUtc"] = DateTime.UtcNow.ToString("u"),
            ["bakeSeconds"] = Math.Round(elapsed.TotalSeconds, 1),
        };
        File.WriteAllText(Path.Combine(folder, "pack.json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
    }
}
