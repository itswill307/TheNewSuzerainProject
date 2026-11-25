using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

/// <summary>
/// What an update bake (terrain surface --update) needs: the boxes being rebaked, widened to
/// whole tiles of a coarse level, and the tiles already in a pack for everything outside them.
///
/// An update rebuilds every tile inside the boxes from the sources, and the coarser tiles that
/// reach outside them from the new tiles inside plus the stored tiles outside. Widening the boxes
/// to whole tiles of <see cref="SnapLevel"/> means every stored tile it has to read is one the
/// pack keeps at full detail (colour and detail tiles stop at that level).
/// </summary>
public static class PackUpdate
{
    /// <summary>Boxes are widened to whole tiles of this level (2.8 degrees): the deepest blended-colour level.</summary>
    public const int SnapLevel = 6;

    public static double[][] Snap(double[][] boxes) => boxes.Select(box =>
    {
        double size = 180.0 / (1 << SnapLevel);
        return new[]
        {
            Math.Floor((box[0] + 180.0) / size) * size - 180.0,
            Math.Max(Math.Floor((box[1] + 90.0) / size) * size - 90.0, -90.0),
            Math.Ceiling((box[2] + 180.0) / size) * size - 180.0,
            Math.Min(Math.Ceiling((box[3] + 90.0) / size) * size - 90.0, 90.0),
        };
    }).ToArray();

    /// <summary>
    /// For a tile reaching outside the boxes: true for each sample whose neighbourhood (one sample
    /// spacing either way, all a coarser sample is averaged from) lies wholly outside them. Those
    /// samples keep the values already stored, so the update cannot change anything outside.
    /// </summary>
    public static bool[] OutsideSamples(TileId id, double[][] boxes)
    {
        const int n = HeightTile.Samples;
        var outside = new bool[HeightTile.SampleCount];
        double step = id.SizeDeg / HeightTile.Intervals;
        for (int row = 0; row < n; row++)
        {
            double latitude = id.South + row * step;
            for (int column = 0; column < n; column++)
            {
                double longitude = id.West + column * step;
                outside[row * n + column] = !boxes.Any(b =>
                    longitude + step > b[0] && longitude - step < b[2] && latitude + step > b[1] && latitude - step < b[3]);
            }
        }

        return outside;
    }

    public static bool Inside(TileId id, double[][] boxes) =>
        boxes.Any(b => id.West >= b[0] - 1e-9 && id.East <= b[2] + 1e-9 && id.South >= b[1] - 1e-9 && id.North <= b[3] + 1e-9);

    /// <summary>
    /// Removes every tile of the pack that lies wholly inside the boxes, at every level: the
    /// update writes again whichever of them are still worth keeping.
    /// </summary>
    public static int DeleteInside(string pack, string extension, double[][] boxes)
    {
        int removed = 0;
        if (!Directory.Exists(pack)) return 0;
        foreach (string levelFolder in Directory.EnumerateDirectories(pack))
        {
            if (!int.TryParse(Path.GetFileName(levelFolder), out int level)) continue;
            double size = 180.0 / (1 << level);
            foreach (double[] b in boxes)
            {
                int x0 = (int)Math.Ceiling((b[0] + 180.0) / size - 1e-9), x1 = (int)Math.Floor((b[2] + 180.0) / size + 1e-9) - 1;
                int y0 = (int)Math.Ceiling((b[1] + 90.0) / size - 1e-9), y1 = (int)Math.Floor((b[3] + 90.0) / size + 1e-9) - 1;
                for (int x = x0; x <= x1; x++)
                {
                    string column = Path.Combine(levelFolder, x.ToString());
                    if (!Directory.Exists(column)) continue;
                    foreach (string file in Directory.EnumerateFiles(column, "*" + extension))
                    {
                        if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out int y) || y < y0 || y > y1) continue;
                        File.Delete(file);
                        removed++;
                    }
                }
            }
        }

        return removed;
    }

    public static void DeleteIfThere(string pack, TileId id, string extension)
    {
        string path = Path.Combine(pack, id.PathWith(extension));
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// A tile as the game would draw it from the pack: the tile itself if stored, otherwise the
    /// part of its nearest stored ancestor that covers it, stretched to full size (bilinear).
    /// Each channel comes back as 0-255 values; null if the pack has neither.
    /// </summary>
    public static float[][] Read(string pack, TileId id, string extension, out bool stored)
    {
        stored = false;
        for (TileId at = id; at.Level >= 0; at = at.Parent)
        {
            string path = Path.Combine(pack, at.PathWith(extension));
            if (File.Exists(path))
            {
                stored = at == id;
                SurfaceTile tile = SurfaceTile.Decode(File.ReadAllBytes(path), f => Jpeg.Decode(f, out _, out _));
                return Window(tile, id.Level - at.Level, id.X - (at.X << (id.Level - at.Level)), id.Y - (at.Y << (id.Level - at.Level)));
            }

            if (at.Level == 0) break;
        }

        return null;
    }

    /// <summary>Part (column, row) of a tile cut into 2^levels x 2^levels, at full size.</summary>
    private static float[][] Window(SurfaceTile tile, int levels, int column, int row)
    {
        const int n = HeightTile.Samples, count = HeightTile.SampleCount;
        var planes = new float[tile.Channels][];
        double span = (double)HeightTile.Intervals / (1 << levels);
        double x0 = column * span, y0 = row * span;
        for (int c = 0; c < tile.Channels; c++)
        {
            var plane = new float[count];
            int offset = c * count;
            for (int r = 0; r < n; r++)
            {
                double fy = y0 + r * span / HeightTile.Intervals;
                int iy = Math.Min((int)fy, n - 2);
                float ty = (float)(fy - iy);
                for (int k = 0; k < n; k++)
                {
                    double fx = x0 + k * span / HeightTile.Intervals;
                    int ix = Math.Min((int)fx, n - 2);
                    float tx = (float)(fx - ix);
                    float a = tile.Data[offset + iy * n + ix], b = tile.Data[offset + iy * n + ix + 1];
                    float d = tile.Data[offset + (iy + 1) * n + ix], e = tile.Data[offset + (iy + 1) * n + ix + 1];
                    plane[r * n + k] = (a + (b - a) * tx) * (1 - ty) + (d + (e - d) * tx) * ty;
                }
            }

            planes[c] = plane;
        }

        return planes;
    }

    /// <summary>Counts what the pack holds, level by level, for its pack.json after an update.</summary>
    public static (long[] Tiles, long[] Bytes) Count(string pack, string extension)
    {
        var tiles = new long[TileId.MaxSupportedLevel + 1];
        var bytes = new long[TileId.MaxSupportedLevel + 1];
        foreach (string levelFolder in Directory.EnumerateDirectories(pack))
        {
            if (!int.TryParse(Path.GetFileName(levelFolder), out int level) || level > TileId.MaxSupportedLevel) continue;
            foreach (FileInfo file in new DirectoryInfo(levelFolder).EnumerateFiles("*" + extension, SearchOption.AllDirectories))
            {
                tiles[level]++;
                bytes[level] += file.Length;
            }
        }

        return (tiles, bytes);
    }
}
