using System;
using System.IO;
using Tns.Core.Terrain;

namespace Tns.Tools.Terrain;

/// <summary>Quick checks of the tool's own reading and writing code. Run with: terrain selftest</summary>
public static class SelfTest
{
    private static int _failures;

    public static int Run()
    {
        TileRoundTrip();
        ConstantTile();
        TileAddresses();
        GeoTiffRoundTrip();
        JpegRoundTrip();
        SurfaceRoundTrip();
        Console.WriteLine(_failures == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({_failures} problems)");
        return _failures == 0 ? 0 : 1;
    }

    private static byte[] TestPicture(int width, int height)
    {
        // Smooth colour gradients with some fine detail, like a colour tile.
        var random = new Random(7);
        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 3;
                double detail = random.NextDouble() * 12;
                rgb[at] = (byte)Math.Clamp(60 + x * 0.4 + detail, 0, 255);
                rgb[at + 1] = (byte)Math.Clamp(90 + y * 0.3 + detail, 0, 255);
                rgb[at + 2] = (byte)Math.Clamp(40 + (x + y) * 0.2 + detail, 0, 255);
            }
        }

        return rgb;
    }

    private static void JpegRoundTrip()
    {
        const int width = 257, height = 257;
        byte[] rgb = TestPicture(width, height);
        byte[] file = Jpeg.Encode(rgb, width, height, 90);
        byte[] back = Jpeg.Decode(file, out int w, out int h);
        double error = 0;
        for (int i = 0; i < rgb.Length; i++) error += Math.Abs(rgb[i] - back[i]);
        error /= rgb.Length;
        Check(w == width && h == height && error < 4.0, $"JPEG survives encode and decode (average error {error:0.00} of 255, {file.Length:#,0} bytes)");
    }

    private static void SurfaceRoundTrip()
    {
        byte[] rgb = TestPicture(SurfaceTile.Samples, SurfaceTile.Samples);
        var data = new byte[4 * SurfaceTile.SampleCount];
        for (int i = 0; i < SurfaceTile.SampleCount; i++)
        {
            for (int c = 0; c < 3; c++) data[c * SurfaceTile.SampleCount + i] = rgb[i * 3 + c];
            data[3 * SurfaceTile.SampleCount + i] = (byte)(i % 251);
        }

        byte[] file = new SurfaceTile(4, data).Encode(p => Jpeg.Encode(p, SurfaceTile.Samples, SurfaceTile.Samples, 90));
        SurfaceTile back = SurfaceTile.Decode(file, f => Jpeg.Decode(f, out _, out _));
        bool toneExact = true;
        for (int i = 0; i < SurfaceTile.SampleCount; i++) toneExact &= back.Data[3 * SurfaceTile.SampleCount + i] == data[3 * SurfaceTile.SampleCount + i];
        int worstColour = 0;
        for (int i = 0; i < 3 * SurfaceTile.SampleCount; i++) worstColour = Math.Max(worstColour, Math.Abs(back.Data[i] - data[i]));
        Check(toneExact && worstColour < 40, $"colour tile keeps its water tone exactly and its colour closely ({file.Length:#,0} bytes)");

        var water = new byte[SurfaceTile.SampleCount];
        for (int i = 0; i < water.Length; i++) water[i] = (byte)(i % 257 < 128 ? 0 : 255);
        SurfaceTile waterBack = SurfaceTile.Decode(new SurfaceTile(1, water).Encode());
        Check(waterBack.Data.AsSpan().SequenceEqual(water), "water tile survives encode and decode exactly");
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private static float[] RoughHeights(int seed)
    {
        var random = new Random(seed);
        var heights = new float[HeightTile.SampleCount];
        for (int row = 0; row < HeightTile.Samples; row++)
        {
            for (int column = 0; column < HeightTile.Samples; column++)
            {
                heights[row * HeightTile.Samples + column] =
                    (float)(3000.0 * Math.Sin(row * 0.05) * Math.Cos(column * 0.03) + random.Next(-40, 40) - 500.0);
            }
        }

        return heights;
    }

    private static void TileRoundTrip()
    {
        float[] heights = RoughHeights(1);
        byte[] file = new HeightTile(heights).Encode(1f);
        HeightTile decoded = HeightTile.Decode(file);
        float worst = 0f;
        for (int i = 0; i < heights.Length; i++) worst = Math.Max(worst, Math.Abs(decoded.Heights[i] - heights[i]));
        Check(worst <= 0.5f, $"tile survives encode and decode (worst error {worst:0.00} m, {file.Length:#,0} bytes)");

        byte[] coarse = new HeightTile(heights).Encode(4f);
        HeightTile decodedCoarse = HeightTile.Decode(coarse);
        worst = 0f;
        for (int i = 0; i < heights.Length; i++) worst = Math.Max(worst, Math.Abs(decodedCoarse.Heights[i] - heights[i]));
        Check(worst <= 2.0f && coarse.Length < file.Length, $"4 m height step is smaller and within 2 m ({coarse.Length:#,0} bytes)");
    }

    private static void ConstantTile()
    {
        var heights = new float[HeightTile.SampleCount];
        byte[] file = new HeightTile(heights).Encode(1f);
        HeightTile decoded = HeightTile.Decode(file);
        Check(file.Length == 16 && decoded.MaxHeight == 0f && decoded.MinHeight == 0f, "flat tile is stored in 16 bytes");
    }

    private static void TileAddresses()
    {
        var europe = new TileId(3, 8, 5);
        Check(europe.West == 0.0 && europe.South == 22.5 && europe.SizeDeg == 22.5, "tile 3/8/5 covers 0-22.5 E, 22.5-45 N");
        Check(europe.Child(1, 1).Parent == europe && europe.Child(1, 1) == new TileId(4, 17, 11), "child and parent addresses agree");
        Check(!new TileId(2, 8, 0).IsValid && new TileId(2, 7, 3).IsValid, "out-of-range addresses are rejected");
    }

    private static void GeoTiffRoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tns_selftest_{Guid.NewGuid():N}.tif");
        try
        {
            const int width = 300, height = 200;
            var pixels = new float[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = i % 977 - 100.5f;
            TiffFile.WriteFloatGeoTiff(path, width, height, pixels, 5.0, 48.0, 0.01);

            using TiffFile tiff = TiffFile.Open(path);
            var window = new float[width * height];
            tiff.ReadWindow(0, 0, width, height, window, new BlockCache(1 << 26));
            bool same = true;
            for (int i = 0; i < pixels.Length; i++) same &= window[i] == pixels[i];
            Check(same && tiff.Width == width && tiff.Height == height, "GeoTIFF written by the tool reads back identically");
            Check(Math.Abs(tiff.OriginX - 5.0) < 1e-9 && Math.Abs(tiff.OriginY - 48.0) < 1e-9 && Math.Abs(tiff.PixelSizeX - 0.01) < 1e-12,
                "GeoTIFF position and pixel size read back");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
