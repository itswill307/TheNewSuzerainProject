using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tns.Tools.Terrain;

/// <summary>
/// The kinds of ground the surface is painted from. These are ESA WorldCover's classes; other
/// land cover maps can be used by translating their codes to these in LandCover.SlotOf.
/// </summary>
public static class Cover
{
    public const int Tree = 0, Shrub = 1, Grass = 2, Crop = 3, Built = 4, Bare = 5, Snow = 6,
        Water = 7, Wetland = 8, Mangrove = 9, Moss = 10;

    public const int Count = 11;

    /// <summary>Code for "no land cover file covers this place".</summary>
    public const byte Unknown = 255;

    public static readonly string[] Names =
    {
        "tree", "shrub", "grass", "crop", "built", "bare", "snow", "water", "wetland", "mangrove", "moss",
    };

    /// <summary>Used where a region has never seen a class, before any colours are measured. Linear RGB.</summary>
    public static readonly (float R, float G, float B)[] Fallback =
    {
        (0.035f, 0.065f, 0.025f), // tree
        (0.13f, 0.12f, 0.07f),    // shrub
        (0.16f, 0.17f, 0.08f),    // grass
        (0.17f, 0.17f, 0.09f),    // crop
        (0.20f, 0.19f, 0.18f),    // built
        (0.40f, 0.30f, 0.19f),    // bare
        (0.80f, 0.82f, 0.86f),    // snow
        (0.01f, 0.02f, 0.05f),    // water
        (0.06f, 0.08f, 0.05f),    // wetland
        (0.03f, 0.06f, 0.03f),    // mangrove
        (0.16f, 0.15f, 0.10f),    // moss
    };
}

/// <summary>
/// A folder of ESA WorldCover GeoTIFFs (3 x 3 degrees each, named after their south-west corner,
/// such as ESA_WorldCover_10m_2021_v200_N42E009_Map.tif), read as one map of the world.
/// Places with no file are open sea, or Antarctica, which WorldCover leaves out.
/// </summary>
public sealed class LandCover : IDisposable
{
    private const int FileSpanDeg = 3;

    private readonly Dictionary<(int Column, int Row), string> _paths = new();
    private readonly Dictionary<(int Column, int Row), TiffFile> _open = new();
    private readonly Dictionary<(TiffFile, int), LinkedListNode<(TiffFile File, int Block, byte[] Pixels)>> _blocks = new();
    private readonly LinkedList<(TiffFile File, int Block, byte[] Pixels)> _order = new();
    private readonly long _budgetBytes;
    private long _heldBytes;

    /// <summary>Pixels per degree, the same in every file.</summary>
    public int PixelsPerDegree { get; }
    public int FileCount => _paths.Count;
    public string Name { get; }

    /// <summary>What each code in the files means: its index in Cover, or Cover.Unknown.</summary>
    private static readonly byte[] SlotOf = MakeSlots();

    private static byte[] MakeSlots()
    {
        var slots = new byte[256];
        Array.Fill(slots, Cover.Unknown);
        // 0 is no data: open sea far from the coast, and all ground north of about 82.8 N, beyond
        // the reach of Sentinel-2. The baker decides land and sea there from the elevation.
        slots[10] = Cover.Tree;
        slots[20] = Cover.Shrub;
        slots[30] = Cover.Grass;
        slots[40] = Cover.Crop;
        slots[50] = Cover.Built;
        slots[60] = Cover.Bare;
        slots[70] = Cover.Snow;
        slots[80] = Cover.Water;
        slots[90] = Cover.Wetland;
        slots[95] = Cover.Mangrove;
        slots[100] = Cover.Moss;
        return slots;
    }

    public LandCover(string folder, long cacheBytes)
    {
        Name = Path.GetFileName(folder.TrimEnd('/', '\\'));
        var pattern = new Regex(@"_([NS])(\d{2})([EW])(\d{3})_", RegexOptions.IgnoreCase);
        foreach (string path in Directory.EnumerateFiles(folder, "*.tif"))
        {
            if (path.EndsWith(".part.tif", StringComparison.OrdinalIgnoreCase)) continue;
            Match match = pattern.Match(Path.GetFileName(path));
            if (!match.Success) continue;
            int south = int.Parse(match.Groups[2].Value) * (match.Groups[1].Value.ToUpperInvariant() == "S" ? -1 : 1);
            int west = int.Parse(match.Groups[4].Value) * (match.Groups[3].Value.ToUpperInvariant() == "W" ? -1 : 1);
            _paths[((west + 180) / FileSpanDeg, (south + 90) / FileSpanDeg)] = path;
        }

        if (_paths.Count == 0) throw new InvalidDataException($"No WorldCover files (*_N00E000_*.tif) in {folder}.");
        using TiffFile first = TiffFile.Open(_paths.Values.First());
        PixelsPerDegree = (int)Math.Round(first.Width / (double)FileSpanDeg);
        _budgetBytes = cacheBytes;
    }

    public void Dispose()
    {
        lock (_open)
        {
            foreach (TiffFile file in _open.Values) file.Dispose();
            _open.Clear();
        }
    }

    /// <summary>True if any file touches this box (degrees).</summary>
    public bool Covers(double west, double south, double east, double north)
    {
        int c0 = (int)Math.Floor((west + 180.0) / FileSpanDeg), c1 = (int)Math.Floor((east + 180.0 - 1e-9) / FileSpanDeg);
        int r0 = (int)Math.Floor((south + 90.0) / FileSpanDeg), r1 = (int)Math.Floor((north + 90.0 - 1e-9) / FileSpanDeg);
        for (int r = r0; r <= r1; r++)
        {
            for (int c = c0; c <= c1; c++)
            {
                if (_paths.ContainsKey((((c % 120) + 120) % 120, r))) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fills a block of the world map with cover classes (Cover.Tree ... or Cover.Unknown).
    /// Pixel (0, 0) of the world is the north-west corner; x runs east, y south, and x wraps
    /// round the world.
    /// </summary>
    public void Read(long x0, long y0, int width, int height, byte[] classes)
    {
        int fileSize = PixelsPerDegree * FileSpanDeg;
        long worldWidth = 360L * PixelsPerDegree;
        Array.Fill(classes, Cover.Unknown, 0, width * height);

        for (int y = 0; y < height; y++)
        {
            long worldY = y0 + y;
            if (worldY < 0 || worldY >= 180L * PixelsPerDegree) continue;
            int fileRow = (int)((180L * PixelsPerDegree - 1 - worldY) / fileSize);
            int inFileY = (int)(worldY - (180L * PixelsPerDegree - (fileRow + 1L) * fileSize));

            int x = 0;
            while (x < width)
            {
                long worldX = ((x0 + x) % worldWidth + worldWidth) % worldWidth;
                int fileColumn = (int)(worldX / fileSize);
                int inFileX = (int)(worldX - (long)fileColumn * fileSize);
                int run = Math.Min(width - x, fileSize - inFileX);
                TiffFile file = Open(fileColumn, fileRow);
                if (file != null) CopyRow(file, inFileX, inFileY, run, classes, y * width + x);
                x += run;
            }
        }
    }

    private void CopyRow(TiffFile file, int x, int y, int count, byte[] destination, int at)
    {
        int blockRow = y / file.BlockHeight;
        while (count > 0)
        {
            int blockColumn = x / file.BlockWidth;
            int inBlockX = x - blockColumn * file.BlockWidth;
            int run = Math.Min(count, file.BlockWidth - inBlockX);
            byte[] block = Block(file, blockRow * file.BlocksAcross + blockColumn);
            int from = (y - blockRow * file.BlockHeight) * file.BlockWidth + inBlockX;
            for (int i = 0; i < run; i++) destination[at + i] = SlotOf[block[from + i]];
            x += run;
            at += run;
            count -= run;
        }
    }

    private TiffFile Open(int column, int row)
    {
        lock (_open)
        {
            if (_open.TryGetValue((column, row), out TiffFile file)) return file;
            if (!_paths.TryGetValue((column, row), out string path)) return null;
            file = TiffFile.Open(path);
            if (file.Width != PixelsPerDegree * FileSpanDeg || file.Height != file.Width || file.BitsPerSample != 8)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} is {file.Width} x {file.Height} at {file.BitsPerSample} bits; " +
                    $"every file must be {PixelsPerDegree * FileSpanDeg} pixels square, 8-bit. Download them all at the same resolution.");
            }

            _open[(column, row)] = file;
            return file;
        }
    }

    private byte[] Block(TiffFile file, int index)
    {
        lock (_blocks)
        {
            if (_blocks.TryGetValue((file, index), out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Pixels;
            }
        }

        float[] values = file.ReadBlock(index);
        var pixels = new byte[values.Length];
        for (int i = 0; i < values.Length; i++) pixels[i] = (byte)values[i];

        lock (_blocks)
        {
            if (_blocks.TryGetValue((file, index), out var existing)) return existing.Value.Pixels;
            _blocks[(file, index)] = _order.AddFirst((file, index, pixels));
            _heldBytes += pixels.Length;
            while (_heldBytes > _budgetBytes && _order.Count > 1)
            {
                var oldest = _order.Last!.Value;
                _order.RemoveLast();
                _blocks.Remove((oldest.File, oldest.Block));
                _heldBytes -= oldest.Pixels.Length;
            }
        }

        return pixels;
    }
}
