using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tns.Tools.Terrain;

/// <summary>
/// A colour picture of the world in plain latitude/longitude, read a pixel at a time. Pixel
/// (0, 0) is the north-west corner of the world; x runs east and wraps round, y runs south.
/// </summary>
public interface IWorldPicture
{
    /// <summary>Pixels per degree.</summary>
    double PerDegree { get; }

    /// <summary>The sRGB colour of a pixel, or false where the picture has nothing.</summary>
    bool TryGet(long x, long y, out byte r, out byte g, out byte b);
}

/// <summary>A whole-world picture held in memory, such as the 21600 x 10800 Blue Marble.</summary>
public sealed class ImagePicture : IWorldPicture
{
    private readonly byte[] _rgb;
    private readonly int _width, _height;

    public ImagePicture(byte[] rgb, int width, int height)
    {
        _rgb = rgb;
        _width = width;
        _height = height;
    }

    public double PerDegree => _width / 360.0;

    public bool TryGet(long x, long y, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (y < 0 || y >= _height) return false;
        x = ((x % _width) + _width) % _width;
        long at = (y * _width + x) * 3;
        r = _rgb[at];
        g = _rgb[at + 1];
        b = _rgb[at + 2];
        return true;
    }
}

/// <summary>
/// Sharper pictures of parts of the world, such as the 500 m Blue Marble tiles, read from disk
/// as needed; anywhere they do not cover, a coarser whole-world picture.
///
/// Each part is three single-band GeoTIFFs in plain latitude/longitude, named NAME_r.tif,
/// NAME_g.tif and NAME_b.tif (convert a JPEG with gdal_translate -b 1, -b 2, -b 3, giving
/// its bounds with -a_ullr west north east south). All parts must have the same pixel size.
/// </summary>
public sealed class TiledPicture : IWorldPicture
{
    private sealed record Part(double West, double North, double East, double South, TiffFile R, TiffFile G, TiffFile B);

    private readonly List<Part> _parts = new();
    private readonly IWorldPicture _fallback;
    private readonly BlockCache _cache;
    private readonly double _fallbackRatio;

    public double PerDegree { get; }

    public TiledPicture(string folder, IWorldPicture fallback, BlockCache cache)
    {
        _fallback = fallback;
        _cache = cache;
        foreach (string red in Directory.EnumerateFiles(folder, "*_r.tif").OrderBy(p => p))
        {
            string stem = red[..^"_r.tif".Length];
            TiffFile r = TiffFile.Open(red), g = TiffFile.Open(stem + "_g.tif"), b = TiffFile.Open(stem + "_b.tif");
            if (!r.HasGeoreference) throw new InvalidDataException($"{red} has no position; give it one with gdal_translate -a_ullr.");
            double west = r.OriginX, north = r.OriginY;
            _parts.Add(new Part(west, north, west + r.Width * r.PixelSizeX, north - r.Height * r.PixelSizeY, r, g, b));
        }

        if (_parts.Count == 0) throw new InvalidDataException($"No sharp pictures (*_r.tif, *_g.tif, *_b.tif) in {folder}.");
        PerDegree = Math.Round(1.0 / _parts[0].R.PixelSizeX);
        _fallbackRatio = fallback.PerDegree / PerDegree;
    }

    public int PartCount => _parts.Count;

    public bool TryGet(long x, long y, out byte r, out byte g, out byte b)
    {
        double longitude = -180.0 + (x + 0.5) / PerDegree;
        longitude = ((longitude + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        double latitude = 90.0 - (y + 0.5) / PerDegree;
        foreach (Part part in _parts)
        {
            if (longitude < part.West || longitude >= part.East || latitude > part.North || latitude <= part.South) continue;
            int px = Math.Clamp((int)((longitude - part.West) * PerDegree), 0, part.R.Width - 1);
            int py = Math.Clamp((int)((part.North - latitude) * PerDegree), 0, part.R.Height - 1);
            r = Read(part.R, px, py);
            g = Read(part.G, px, py);
            b = Read(part.B, px, py);
            return true;
        }

        return _fallback.TryGet((long)Math.Floor(x * _fallbackRatio), (long)Math.Floor(y * _fallbackRatio), out r, out g, out b);
    }

    private byte Read(TiffFile file, int x, int y)
    {
        int column = x / file.BlockWidth, row = y / file.BlockHeight;
        float[] block = _cache.Get(file, row * file.BlocksAcross + column);
        return (byte)Math.Clamp(block[(y - row * file.BlockHeight) * file.BlockWidth + (x - column * file.BlockWidth)], 0f, 255f);
    }
}
