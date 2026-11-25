using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

/// <summary>Somewhere ground heights can be read from while baking.</summary>
public interface IElevationSource
{
    string Name { get; }
    double West { get; }
    double South { get; }
    double East { get; }
    double North { get; }

    /// <summary>The deepest tile level this source has real detail for.</summary>
    int NativeLevel { get; }

    /// <summary>The deepest available detail at this tile, or -1 outside the source.</summary>
    int NativeLevelFor(TileId tile);

    /// <summary>
    /// Fills in the heights of a tile, in metres. Only entries that are still NaN are written, so
    /// sources can be layered: ask the most important one first.
    /// </summary>
    void Fill(TileId tile, float[] heights);
}

public static class SourceMaths
{
    /// <summary>The tile level whose sample spacing is at least as fine as a pixel size in degrees.</summary>
    public static int LevelForPixelSize(double pixelSizeDeg)
    {
        double level = Math.Log2(180.0 / (HeightTile.Intervals * pixelSizeDeg));
        // A source that is within 10% of a level counts as that level.
        return Math.Clamp((int)Math.Ceiling(level - 0.15), 0, TileId.MaxSupportedLevel);
    }
}

/// <summary>One GeoTIFF (or a plain TIFF height image with its bounds given in the recipe).</summary>
public sealed class TiffElevationSource : IElevationSource, IDisposable
{
    private readonly TiffFile _tiff;
    private readonly BlockCache _cache;
    private readonly double _valueScale;
    private readonly double _valueOffset;
    private readonly double? _noData;
    private readonly double _pixelX;
    private readonly double _pixelY;

    public string Name { get; }
    public double West { get; }
    public double South { get; }
    public double East { get; }
    public double North { get; }
    public int NativeLevel { get; }

    public TiffElevationSource(string path, BlockCache cache, SourceSettings settings)
    {
        _tiff = TiffFile.Open(path);
        _cache = cache;
        Name = Path.GetFileName(path);
        _valueScale = settings.ValueScale;
        _valueOffset = settings.ValueOffset;
        _noData = settings.NoData ?? _tiff.NoData;

        if (settings.Bounds is { Length: 4 })
        {
            West = settings.Bounds[0];
            South = settings.Bounds[1];
            East = settings.Bounds[2];
            North = settings.Bounds[3];
            _pixelX = (East - West) / _tiff.Width;
            _pixelY = (North - South) / _tiff.Height;
        }
        else if (_tiff.HasGeoreference)
        {
            _pixelX = _tiff.PixelSizeX;
            _pixelY = _tiff.PixelSizeY;
            West = _tiff.OriginX;
            North = _tiff.OriginY;
            East = West + _pixelX * _tiff.Width;
            South = North - _pixelY * _tiff.Height;
        }
        else
        {
            throw new InvalidDataException(
                $"{Name} has no georeference. Give its extent in the recipe: \"bounds\": [west, south, east, north].");
        }

        NativeLevel = Math.Min(
            SourceMaths.LevelForPixelSize(Math.Min(_pixelX, _pixelY)),
            settings.MaxLevel ?? TileId.MaxSupportedLevel);
    }

    public void Dispose() => _tiff.Dispose();

    public int NativeLevelFor(TileId tile)
    {
        double slackX = _pixelX * 1.5, slackY = _pixelY * 1.5;
        if (tile.North < South - slackY || tile.South > North + slackY) return -1;

        double west = West - slackX, east = East + slackX;
        return TouchesLongitude(0.0) || TouchesLongitude(360.0) || TouchesLongitude(-360.0)
            ? NativeLevel : -1;

        bool TouchesLongitude(double shift) => tile.East + shift >= west && tile.West + shift <= east;
    }

    public void Fill(TileId tile, float[] heights)
    {
        // A source may describe longitudes as -180..180 or 0..360; try the tile both ways round.
        foreach (double shift in new[] { 0.0, 360.0, -360.0 })
        {
            FillShifted(tile, heights, shift);
        }
    }

    private void FillShifted(TileId tile, float[] heights, double lonShift)
    {
        double tileWest = tile.West + lonShift;
        double tileEast = tile.East + lonShift;
        // Let the source reach a pixel and a half past its own edge so borders between files,
        // and sources a hair smaller than the world, leave no gaps.
        double slackX = _pixelX * 1.5, slackY = _pixelY * 1.5;
        if (tileEast < West - slackX || tileWest > East + slackX) return;
        if (tile.North < South - slackY || tile.South > North + slackY) return;

        double step = tile.SizeDeg / HeightTile.Intervals;
        // How many source pixels one tile sample covers. Above 1.5 the source is finer than the
        // tile, so average the pixels under each sample instead of picking between two.
        double ratioX = step / _pixelX, ratioY = step / _pixelY;
        bool average = ratioX > 1.5 || ratioY > 1.5;
        int reachX = average ? (int)Math.Ceiling(ratioX * 0.5) + 1 : 2;
        int reachY = average ? (int)Math.Ceiling(ratioY * 0.5) + 1 : 2;

        int x0 = Math.Clamp((int)Math.Floor((tileWest - West) / _pixelX) - reachX, 0, _tiff.Width - 1);
        int x1 = Math.Clamp((int)Math.Ceiling((tileEast - West) / _pixelX) + reachX, 0, _tiff.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor((North - tile.North) / _pixelY) - reachY, 0, _tiff.Height - 1);
        int y1 = Math.Clamp((int)Math.Ceiling((North - tile.South) / _pixelY) + reachY, 0, _tiff.Height - 1);
        int windowWidth = x1 - x0 + 1, windowHeight = y1 - y0 + 1;
        var window = new float[windowWidth * windowHeight];
        _tiff.ReadWindow(x0, y0, windowWidth, windowHeight, window, _cache);

        float noData = _noData.HasValue ? (float)_noData.Value : float.NaN;
        bool Valid(float v) => !float.IsNaN(v) && v != noData;

        for (int row = 0; row < HeightTile.Samples; row++)
        {
            double lat = tile.South + row * step;
            if (lat < South - slackY || lat > North + slackY) continue;
            // Fractional pixel position, measured from pixel centres, counted from the top row.
            double py = Math.Clamp((North - lat) / _pixelY - 0.5, 0.0, _tiff.Height - 1);

            for (int column = 0; column < HeightTile.Samples; column++)
            {
                int index = row * HeightTile.Samples + column;
                if (!float.IsNaN(heights[index])) continue;
                double lon = tileWest + column * step;
                if (lon < West - slackX || lon > East + slackX) continue;
                double px = Math.Clamp((lon - West) / _pixelX - 0.5, 0.0, _tiff.Width - 1);

                double sum = 0.0, weight = 0.0;
                if (average)
                {
                    int ax0 = Math.Max(x0, (int)Math.Round(px - ratioX * 0.5));
                    int ax1 = Math.Min(x1, (int)Math.Round(px + ratioX * 0.5));
                    int ay0 = Math.Max(y0, (int)Math.Round(py - ratioY * 0.5));
                    int ay1 = Math.Min(y1, (int)Math.Round(py + ratioY * 0.5));
                    for (int y = ay0; y <= ay1; y++)
                    {
                        for (int x = ax0; x <= ax1; x++)
                        {
                            float v = window[(y - y0) * windowWidth + (x - x0)];
                            if (!Valid(v)) continue;
                            sum += v;
                            weight += 1.0;
                        }
                    }
                }
                else
                {
                    int bx = Math.Min((int)px, _tiff.Width - 2 < 0 ? 0 : _tiff.Width - 2);
                    int by = Math.Min((int)py, _tiff.Height - 2 < 0 ? 0 : _tiff.Height - 2);
                    double fx = px - bx, fy = py - by;
                    for (int dy = 0; dy <= 1; dy++)
                    {
                        for (int dx = 0; dx <= 1; dx++)
                        {
                            int x = Math.Min(bx + dx, _tiff.Width - 1), y = Math.Min(by + dy, _tiff.Height - 1);
                            float v = window[(y - y0) * windowWidth + (x - x0)];
                            if (!Valid(v)) continue;
                            double w = (dx == 0 ? 1.0 - fx : fx) * (dy == 0 ? 1.0 - fy : fy);
                            sum += v * w;
                            weight += w;
                        }
                    }
                }

                if (weight > 1e-6) heights[index] = (float)(sum / weight * _valueScale + _valueOffset);
            }
        }
    }
}

/// <summary>A folder of GeoTIFFs that together cover an area, such as the 288 ETOPO tiles.</summary>
public sealed class TiffFolderSource : IElevationSource, IDisposable
{
    private readonly List<TiffElevationSource> _files = new();

    public string Name { get; }
    public double West { get; }
    public double South { get; }
    public double East { get; }
    public double North { get; }
    public int NativeLevel { get; }
    public int FileCount => _files.Count;

    public TiffFolderSource(string folder, BlockCache cache, SourceSettings settings)
    {
        Name = Path.GetFileName(folder.TrimEnd('/', '\\'));
        foreach (string path in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".tif" && extension != ".tiff") continue;
            _files.Add(new TiffElevationSource(path, cache, settings with { Bounds = null }));
        }

        if (_files.Count == 0) throw new InvalidDataException($"No .tif files in {folder}.");
        West = _files.Min(f => f.West);
        South = _files.Min(f => f.South);
        East = _files.Max(f => f.East);
        North = _files.Max(f => f.North);
        NativeLevel = _files.Max(f => f.NativeLevel);
    }

    public void Dispose()
    {
        foreach (TiffElevationSource file in _files) file.Dispose();
    }

    public int NativeLevelFor(TileId tile)
    {
        int level = -1;
        foreach (TiffElevationSource file in _files)
        {
            if (file.NativeLevel <= level) continue;
            level = Math.Max(level, file.NativeLevelFor(tile));
            if (level == NativeLevel) break;
        }

        return level;
    }

    public void Fill(TileId tile, float[] heights)
    {
        foreach (TiffElevationSource file in _files)
        {
            if (file.NativeLevelFor(tile) >= 0) file.Fill(tile, heights);
        }
    }
}

/// <summary>
/// An existing terrain pack used as a source, so a mod can be baked on top of the base terrain
/// without needing the original downloads.
/// </summary>
public sealed class PackElevationSource : IElevationSource
{
    private readonly TerrainPackSet _packs = new();
    private readonly Dictionary<TileId, HeightTile> _loaded = new();
    private readonly LinkedList<TileId> _order = new();
    private const int MaxLoaded = 256;

    public string Name { get; }
    public double West => -180.0;
    public double South => -90.0;
    public double East => 180.0;
    public double North => 90.0;
    public int NativeLevel => _packs.MaxLevel;
    public int NativeLevelFor(TileId tile) => _packs.MaxDetailLevelFor(tile);

    public PackElevationSource(string root)
    {
        Name = root;
        if (_packs.AddPack(root) == 0) throw new InvalidDataException($"No terrain tiles found in {root}.");
    }

    public void Fill(TileId tile, float[] heights)
    {
        // Use the deepest stored tile covering each point: the tile itself if the pack has it,
        // otherwise the nearest ancestor, which is what the game draws there anyway.
        TileId source = tile;
        while (source.Level > 0 && !_packs.Has(source)) source = source.Parent;
        if (!_packs.Has(source)) return;

        HeightTile data = Get(source);
        double scale = 1.0 / (1 << (tile.Level - source.Level));
        double offsetX = (tile.West - source.West) / source.SizeDeg * HeightTile.Intervals;
        double offsetY = (tile.South - source.South) / source.SizeDeg * HeightTile.Intervals;
        for (int row = 0; row < HeightTile.Samples; row++)
        {
            for (int column = 0; column < HeightTile.Samples; column++)
            {
                int index = row * HeightTile.Samples + column;
                if (float.IsNaN(heights[index]))
                {
                    heights[index] = data.SampleBilinear(offsetX + column * scale, offsetY + row * scale);
                }
            }
        }
    }

    private HeightTile Get(TileId id)
    {
        lock (_loaded)
        {
            if (_loaded.TryGetValue(id, out HeightTile cached)) return cached;
        }

        HeightTile tile = _packs.Load(id);
        lock (_loaded)
        {
            if (_loaded.TryAdd(id, tile))
            {
                _order.AddFirst(id);
                if (_order.Count > MaxLoaded)
                {
                    _loaded.Remove(_order.Last.Value);
                    _order.RemoveLast();
                }
            }
        }

        return tile;
    }
}

/// <summary>Several sources stacked. The last one listed is on top.</summary>
public sealed class LayeredSource
{
    private readonly List<IElevationSource> _layers;

    public LayeredSource(List<IElevationSource> bottomToTop) => _layers = bottomToTop;

    public IReadOnlyList<IElevationSource> Layers => _layers;

    /// <summary>The deepest level any source touching this tile has detail for.</summary>
    public int NativeLevelFor(TileId tile)
    {
        int level = 0;
        foreach (IElevationSource source in _layers)
        {
            level = Math.Max(level, source.NativeLevelFor(tile));
        }

        return level;
    }

    public float[] Fill(TileId tile)
    {
        var heights = new float[HeightTile.SampleCount];
        Array.Fill(heights, float.NaN);
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            _layers[i].Fill(tile, heights);
        }

        return heights;
    }

}

/// <summary>
/// A source that only counts inside a list of 1 x 1 degree squares (a recipe's "squares" file):
/// elsewhere it has no data and no detail, so the sources under it are used.
/// </summary>
public sealed class SquareLimitedSource : IElevationSource, IDisposable
{
    private readonly IElevationSource _inner;
    private readonly HashSet<(int Lat, int Lon)> _squares;

    public SquareLimitedSource(IElevationSource inner, HashSet<(int Lat, int Lon)> squares)
    {
        _inner = inner;
        _squares = squares;
    }

    /// <summary>Reads "lat,lon" lines (south-west corners), or names like N24_00_W003_00.</summary>
    public static HashSet<(int Lat, int Lon)> ReadSquares(string path)
    {
        var squares = new HashSet<(int, int)>();
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var named = System.Text.RegularExpressions.Regex.Match(line, @"^([NS])(\d{2})_00_([EW])(\d{3})_00$");
            if (named.Success)
            {
                int lat = int.Parse(named.Groups[2].Value) * (named.Groups[1].Value == "S" ? -1 : 1);
                int lon = int.Parse(named.Groups[4].Value) * (named.Groups[3].Value == "W" ? -1 : 1);
                squares.Add((lat, lon));
                continue;
            }

            string[] parts = line.Split(',');
            squares.Add((int.Parse(parts[0]), int.Parse(parts[1])));
        }

        return squares;
    }

    private bool Touches(TileId tile) => Touches(_squares, tile);

    /// <summary>Whether a tile overlaps any of the squares.</summary>
    public static bool Touches(HashSet<(int Lat, int Lon)> squares, TileId tile)
    {
        int lat0 = (int)Math.Floor(tile.South), lat1 = (int)Math.Ceiling(tile.North) - 1;
        int lon0 = (int)Math.Floor(tile.West), lon1 = (int)Math.Ceiling(tile.East) - 1;
        if ((lat1 - lat0 + 1) * (lon1 - lon0 + 1) > squares.Count) return squares.Any(s => s.Lat >= lat0 && s.Lat <= lat1 && s.Lon >= lon0 && s.Lon <= lon1);
        for (int lat = lat0; lat <= lat1; lat++)
        {
            for (int lon = lon0; lon <= lon1; lon++)
            {
                if (squares.Contains((lat, lon))) return true;
            }
        }

        return false;
    }

    public string Name => _inner.Name + " (in " + _squares.Count + " squares)";
    public double West => _inner.West;
    public double South => _inner.South;
    public double East => _inner.East;
    public double North => _inner.North;
    public int NativeLevel => _inner.NativeLevel;

    public int NativeLevelFor(TileId tile) => Touches(tile) ? _inner.NativeLevelFor(tile) : -1;

    public void Fill(TileId tile, float[] heights)
    {
        if (!Touches(tile)) return;
        // Fill a copy, then keep only the samples inside the squares.
        var mine = new float[heights.Length];
        Array.Fill(mine, float.NaN);
        _inner.Fill(tile, mine);
        double step = tile.SizeDeg / HeightTile.Intervals;
        for (int row = 0; row < HeightTile.Samples; row++)
        {
            int lat = (int)Math.Floor(tile.South + row * step - 1e-9);
            for (int column = 0; column < HeightTile.Samples; column++)
            {
                int at = row * HeightTile.Samples + column;
                if (!float.IsNaN(heights[at]) || float.IsNaN(mine[at])) continue;
                int lon = (int)Math.Floor(tile.West + column * step - 1e-9);
                if (_squares.Contains((lat, lon))) heights[at] = mine[at];
            }
        }
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
