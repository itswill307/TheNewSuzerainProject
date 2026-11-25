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
/// Turns elevation sources into a terrain pack.
///
/// The deepest tiles are sampled straight from the sources. Each tile above is made by shrinking
/// the four below it. A tile is only written when it would visibly change the picture: if the
/// slope shading drawn from the tile above already looks the same (see Recipe.DetailSlope and
/// DetailMeasure), it is left out and the game draws that area from the tile above. Flat plains
/// and open sea therefore cost almost nothing, while mountains keep full detail.
/// </summary>
public sealed class Baker
{
    private readonly Recipe _recipe;
    private readonly LayeredSource _source;
    private readonly string _output;
    private readonly int _maxLevel;
    private readonly DetailRule _rule;
    private readonly ConcurrentDictionary<string, Lazy<DirectoryInfo>> _createdFolders = new();
    private readonly long[] _tilesWritten = new long[TileId.MaxSupportedLevel + 1];
    private readonly long[] _tilesDropped = new long[TileId.MaxSupportedLevel + 1];
    private readonly long[] _bytesWritten = new long[TileId.MaxSupportedLevel + 1];
    private int _subtreesDone;
    private DecisionLog _log;

    public Baker(Recipe recipe, LayeredSource source)
    {
        _recipe = recipe;
        _source = source;
        _rule = new DetailRule(recipe);
        _output = recipe.Resolve(recipe.Output);
        int deepest = source.Layers.Max(layer => layer.NativeLevel);
        _maxLevel = Math.Min(recipe.MaxLevel ?? deepest, deepest);
    }

    private sealed class Built
    {
        public float[] Heights;
        /// <summary>True if any tile below this one was written, so this one must be too.</summary>
        public bool HasWrittenChildren;
        /// <summary>True if every height is at the floor (open sea, raised to minElevationM): flat.</summary>
        public bool AllSea;
    }

    private readonly WorkTimer _work = new();

    private bool AllAtFloor(float[] heights)
    {
        if (!_recipe.MinElevationM.HasValue) return false;
        float floor = (float)_recipe.MinElevationM.Value;
        foreach (float h in heights)
        {
            if (h > floor) return false;
        }

        return true;
    }

    public void Run(int threads)
    {
        var timer = Stopwatch.StartNew();
        Console.WriteLine($"Baking \"{_recipe.Name}\" to {_output}");
        Console.WriteLine($"  deepest level {_maxLevel}, levels 0-{_recipe.FullLevel} always complete, " +
                          $"detail kept where slope changes by {_recipe.DetailSlope} over {_recipe.DetailAreaKm2:0} sq km, " +
                          (_recipe.MinElevationM.HasValue ? $"floor {_recipe.MinElevationM} m" : "sea bed kept"));
        Directory.CreateDirectory(_output);
        // A patch (onlyBounds) re-decides only its own tiles, so it starts from the saved record.
        _log = new DecisionLog(_output, keepExisting: _recipe.OnlyBounds != null);

        // Work in parallel on the subtrees a few levels down, then finish the top by hand.
        int splitLevel = Math.Min(3, _maxLevel);
        var subtreeRoots = new List<TileId>();
        for (int y = 0; y < TileId.RowsAt(splitLevel); y++)
        {
            for (int x = 0; x < TileId.ColumnsAt(splitLevel); x++) subtreeRoots.Add(new TileId(splitLevel, x, y));
        }

        var finished = new ConcurrentDictionary<TileId, Built>();
        Parallel.ForEach(
            subtreeRoots,
            new ParallelOptions { MaxDegreeOfParallelism = threads },
            root =>
            {
                finished[root] = Build(root, null);
                int done = Interlocked.Increment(ref _subtreesDone);
                Console.Write($"\r  {done}/{subtreeRoots.Count} regions, {_tilesWritten.Sum()} tiles written   ");
            });
        Console.WriteLine();

        foreach (TileId root in new[] { new TileId(0, 0, 0), new TileId(0, 1, 0) })
        {
            Built top = Build(root, finished);
            if (Wanted(root)) Write(root, top.Heights);
        }

        WritePackInfo(timer.Elapsed);
        _log.Save();
        PrintSummary(timer.Elapsed);
        _work.Print("Heights");
    }

    /// <summary>Builds a tile and everything below it. Writing the tile itself is left to the caller.</summary>
    private Built Build(TileId id, ConcurrentDictionary<TileId, Built> finished)
    {
        if (finished != null && finished.TryGetValue(id, out Built done)) return done;

        bool outsideEdit = !Wanted(id);
        int leafLevel = Math.Min(_maxLevel, Math.Max(_source.NativeLevelFor(id), Math.Min(_recipe.FullLevel, _maxLevel)));
        if (id.Level >= leafLevel || outsideEdit)
        {
            // Outside an edited region nothing changes, so read the tile as it stands and stop.
            long readStarted = _work.Start();
            float[] read = Sample(id);
            bool sea = AllAtFloor(read);
            _work.Stop(sea ? $"read the sources: all-sea tile, level {id.Level}" : $"read the sources: land tile, level {id.Level}", readStarted);
            return new Built { Heights = read, AllSea = sea };
        }

        var children = new Built[4];
        var childIds = new TileId[4];
        for (int i = 0; i < 4; i++)
        {
            childIds[i] = id.Child(i & 1, i >> 1);
            children[i] = Build(childIds[i], finished);
        }

        long started = _work.Start();
        long writing = 0;
        float[] heights = Shrink(children);
        bool allSea = children.All(c => c.AllSea);
        var result = new Built { Heights = heights, AllSea = allSea };
        float[] upsampled = null;

        for (int i = 0; i < 4; i++)
        {
            TileId child = childIds[i];
            if (!Wanted(child)) continue;
            // A patch must write every tile it touches: a tile left out would let the tile from
            // the pack underneath show through with the old terrain.
            bool keep = _recipe.OnlyBounds != null;
            if (!keep && !_rule.Capped(child))
            {
                // Measured for every tile, kept or not, so decisions.csv shows how close each came.
                upsampled ??= DetailMeasure.Upsample(heights);
                double fraction = DetailMeasure.FractionOver(upsampled, children[i].Heights, child, _recipe.DetailSlope);
                bool below = children[i].HasWrittenChildren, forced = _rule.Forced(child);
                keep = below || forced || _rule.Visible(child, fraction);
                _log.Add(child, fraction * DetailRule.AreaKm2(child), _recipe.DetailAreaKm2, keep, below ? 'b' : forced ? 'f' : 'm');
            }
            else if (!keep)
            {
                _log.Add(child, -1, _recipe.DetailAreaKm2, false, 'c');
            }

            if (keep)
            {
                long writeStarted = Stopwatch.GetTimestamp();
                Write(child, children[i].Heights);
                writing += Stopwatch.GetTimestamp() - writeStarted;
                result.HasWrittenChildren = true;
            }
            else
            {
                Interlocked.Increment(ref _tilesDropped[child.Level]);
            }
        }

        _work.Stop(allSea ? "average and measure: all four children all sea" : "average and measure: some land", started + writing);
        if (writing > 0) _work.Stop("compress and write kept tiles", Stopwatch.GetTimestamp() - writing);
        return result;
    }

    private bool Wanted(TileId id)
    {
        double[] box = _recipe.OnlyBounds;
        if (box is not { Length: 4 }) return true;
        return id.East >= box[0] && id.West <= box[2] && id.North >= box[1] && id.South <= box[3];
    }

    private float[] Sample(TileId id)
    {
        float[] heights = _source.Fill(id);
        float fill = (float)_recipe.FillElevationM;
        float floor = _recipe.MinElevationM.HasValue ? (float)_recipe.MinElevationM.Value : float.MinValue;
        for (int i = 0; i < heights.Length; i++)
        {
            float h = float.IsNaN(heights[i]) ? fill : heights[i];
            heights[i] = h < floor ? floor : h;
        }

        return heights;
    }

    /// <summary>
    /// Makes a tile from the four below it. Inside the tile each sample is a small weighted
    /// average of the finer samples round it. Along the border only samples on that same border
    /// are averaged, so the tile next door, built separately, gets identical edge values.
    /// </summary>
    private static float[] Shrink(Built[] children)
    {
        const int n = HeightTile.Samples;
        const int fine = HeightTile.Intervals * 2 + 1;
        var grid = new float[fine * fine];
        for (int i = 0; i < 4; i++)
        {
            int offsetX = (i & 1) * HeightTile.Intervals;
            int offsetY = (i >> 1) * HeightTile.Intervals;
            float[] child = children[i].Heights;
            for (int row = 0; row < n; row++)
            {
                Array.Copy(child, row * n, grid, (row + offsetY) * fine + offsetX, n);
            }
        }

        var heights = new float[HeightTile.SampleCount];
        for (int row = 0; row < n; row++)
        {
            for (int column = 0; column < n; column++)
            {
                int at = row * 2 * fine + column * 2;
                bool edgeRow = row == 0 || row == n - 1;
                bool edgeColumn = column == 0 || column == n - 1;
                float value;
                if (edgeRow && edgeColumn) value = grid[at];
                else if (edgeRow) value = (grid[at - 1] + 2f * grid[at] + grid[at + 1]) * 0.25f;
                else if (edgeColumn) value = (grid[at - fine] + 2f * grid[at] + grid[at + fine]) * 0.25f;
                else
                {
                    value = (grid[at - fine - 1] + grid[at - fine + 1] + grid[at + fine - 1] + grid[at + fine + 1]
                             + 2f * (grid[at - 1] + grid[at + 1] + grid[at - fine] + grid[at + fine])
                             + 4f * grid[at]) * 0.0625f;
                }

                heights[row * n + column] = value;
            }
        }

        return heights;
    }

    private void Write(TileId id, float[] heights)
    {
        byte[] file = new HeightTile(heights).Encode((float)_recipe.HeightStepM);
        string path = Path.Combine(_output, id.RelativePath);
        string folder = Path.GetDirectoryName(path)!;
        _ = _createdFolders.GetOrAdd(folder, static path => new Lazy<DirectoryInfo>(() => Directory.CreateDirectory(path))).Value;
        File.WriteAllBytes(path, file);
        Interlocked.Increment(ref _tilesWritten[id.Level]);
        Interlocked.Add(ref _bytesWritten[id.Level], file.Length);
    }

    private void WritePackInfo(TimeSpan elapsed)
    {
        var info = new Dictionary<string, object>
        {
            ["format"] = 1,
            ["name"] = _recipe.Name,
            ["tileSamples"] = HeightTile.Samples,
            ["maxLevel"] = Array.FindLastIndex(_tilesWritten, count => count > 0),
            ["tiles"] = _tilesWritten.Sum(),
            ["bytes"] = _bytesWritten.Sum(),
            ["minElevationM"] = _recipe.MinElevationM,
            ["detailSlope"] = _recipe.DetailSlope,
            ["detailAreaKm2"] = _recipe.DetailAreaKm2,
            ["heightStepM"] = _recipe.HeightStepM,
            ["sources"] = _source.Layers.Select(layer => layer.Name).ToArray(),
            ["bakedUtc"] = DateTime.UtcNow.ToString("u"),
            ["bakeSeconds"] = Math.Round(elapsed.TotalSeconds, 1),
        };
        File.WriteAllText(
            Path.Combine(_output, "pack.json"),
            JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void PrintSummary(TimeSpan elapsed)
    {
        Console.WriteLine("  level   written   dropped        size");
        for (int level = 0; level <= TileId.MaxSupportedLevel; level++)
        {
            if (_tilesWritten[level] + _tilesDropped[level] == 0) continue;
            Console.WriteLine($"  {level,5} {_tilesWritten[level],9} {_tilesDropped[level],9} {_bytesWritten[level] / 1e6,9:0.0} MB");
        }

        Console.WriteLine($"  total {_tilesWritten.Sum(),9} {_tilesDropped.Sum(),9} {_bytesWritten.Sum() / 1e6,9:0.0} MB" +
                          $"   in {elapsed.TotalSeconds:0.0} s");
    }
}
