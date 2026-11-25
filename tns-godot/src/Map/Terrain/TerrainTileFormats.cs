using System;
using System.Runtime.InteropServices;
using Godot;
using Tns.Core.Terrain;

namespace Tns.Map.Terrain;

/// <summary>
/// How each kind of terrain tile is laid out on the graphics card, and how a tile file becomes
/// those bytes. All of these run on worker threads.
/// </summary>
public static class TerrainTileFormats
{
    /// <summary>Height in metres = stored value x MetresPerStep + LowestM. Covers -12,000 m to +20,767 m.</summary>
    public const float MetresPerStep = 0.5f;
    public const float LowestM = -12000f;

    /// <summary>What the shader multiplies and adds to a height texture read (0..1) to get metres.</summary>
    public static Vector2 HeightShaderDecode => new(65535f * MetresPerStep, LowestM);

    /// <summary>
    /// Heights as 16-bit numbers in steps of half a metre: half the memory of 32-bit floats, and
    /// nothing lost that the tile files hold.
    /// </summary>
    public static byte[] PackHeights(float[] heights)
    {
        var packed = new ushort[heights.Length];
        for (int i = 0; i < heights.Length; i++)
        {
            packed[i] = (ushort)Math.Clamp(MathF.Round((heights[i] - LowestM) / MetresPerStep), 0f, 65535f);
        }

        return MemoryMarshal.AsBytes(packed.AsSpan()).ToArray();
    }

    public static byte[] DecodeHeights(byte[] file) => Resample16(PackHeights(HeightTile.Decode(file).Heights));

    public static byte[] BlankHeights => Resample16(PackHeights(new float[HeightTile.SampleCount]));

    /// <summary>
    /// Every kind of tile is held on the graphics card at 256 x 256 texels, not the files'
    /// 257 x 257 samples: the driver pads a 257-wide layer to the next size up, which costs 19%
    /// to 40% more memory (about 100 MB in all). Texel j holds the tile at sample j x 256 / 255,
    /// so texel 0 and texel 255 are the edge samples exactly and neighbouring tiles still meet
    /// exactly; the shader reads with the matching scale (texel_uv in map_terrain.gdshader).
    /// </summary>
    public const int Texels = 256;

    /// <summary>Old name for <see cref="Texels"/>, which colour and detail used first.</summary>
    public const int ColourSamples = Texels;

    /// <summary>For each texel along a side: the sample before it, and how far past it (0..1).</summary>
    private static readonly (int Sample, double Past)[] Along = MakeAlong();

    private static (int, double)[] MakeAlong()
    {
        const int n = HeightTile.Samples;
        var along = new (int, double)[Texels];
        for (int j = 0; j < Texels; j++)
        {
            double s = j * (double)(n - 1) / (Texels - 1);
            int i = Math.Min((int)s, n - 2);
            along[j] = (i, s - i);
        }

        return along;
    }

    /// <summary>Heights (16-bit), 257 samples to 256 texels, blending the nearest samples.</summary>
    private static byte[] Resample16(byte[] full)
    {
        const int n = HeightTile.Samples;
        ReadOnlySpan<ushort> source = MemoryMarshal.Cast<byte, ushort>(full);
        var texels = new ushort[Texels * Texels];
        for (int row = 0; row < Texels; row++)
        {
            var (y, fy) = Along[row];
            for (int column = 0; column < Texels; column++)
            {
                var (x, fx) = Along[column];
                int a = y * n + x;
                double top = source[a] + (source[a + 1] - source[a]) * fx;
                double bottom = source[a + n] + (source[a + n + 1] - source[a + n]) * fx;
                texels[row * Texels + column] = (ushort)Math.Round(top + (bottom - top) * fy);
            }
        }

        return MemoryMarshal.AsBytes(texels.AsSpan()).ToArray();
    }

    /// <summary>Water shares (1 byte), 257 samples to 256 texels, blending the nearest samples.</summary>
    private static byte[] Resample8(byte[] full)
    {
        const int n = HeightTile.Samples;
        var texels = new byte[Texels * Texels];
        for (int row = 0; row < Texels; row++)
        {
            var (y, fy) = Along[row];
            for (int column = 0; column < Texels; column++)
            {
                var (x, fx) = Along[column];
                int a = y * n + x;
                double top = full[a] + (full[a + 1] - full[a]) * fx;
                double bottom = full[a + n] + (full[a + n + 1] - full[a + n]) * fx;
                texels[row * Texels + column] = (byte)Math.Round(top + (bottom - top) * fy);
            }
        }

        return texels;
    }

    /// <summary>
    /// Kinds of ground (1 byte, not to be blended), 257 samples to 256 texels: each texel takes
    /// the kind of the nearest sample.
    /// </summary>
    private static byte[] ResampleNearest8(byte[] full)
    {
        const int n = HeightTile.Samples;
        var texels = new byte[Texels * Texels];
        for (int row = 0; row < Texels; row++)
        {
            var (y, fy) = Along[row];
            int sy = fy < 0.5 ? y : y + 1;
            for (int column = 0; column < Texels; column++)
            {
                var (x, fx) = Along[column];
                texels[row * Texels + column] = full[sy * n + (fx < 0.5 ? x : x + 1)];
            }
        }

        return texels;
    }

    /// <summary>Colour tiles: RGB of the land (sRGB) and the water's tone, 4 bytes a sample.</summary>
    public static byte[] DecodeColour(byte[] file) => ToColourSamples(SurfaceTile.Decode(file, DecodeJpeg).Interleaved());

    /// <summary>
    /// Detail tiles: the real colour of the ground relative to its kinds (red, green, blue
    /// multipliers x 128; see tools/terrain/TerrainTool/DetailBaker.cs). Alpha is unused.
    /// </summary>
    public static byte[] DecodeDetail(byte[] file)
    {
        SurfaceTile tile = SurfaceTile.Decode(file, DecodeJpeg);
        var rgba = new byte[HeightTile.SampleCount * 4];
        for (int i = 0; i < HeightTile.SampleCount; i++)
        {
            for (int c = 0; c < 3; c++) rgba[i * 4 + c] = tile.Data[c * HeightTile.SampleCount + i];
            rgba[i * 4 + 3] = 255;
        }

        return ToColourSamples(rgba);
    }

    /// <summary>A multiplier of 1 everywhere, for before anything is loaded.</summary>
    public static byte[] BlankDetail
    {
        get
        {
            var pixels = new byte[ColourSamples * ColourSamples * 4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 4 == 3 ? 255 : 128);
            return pixels;
        }
    }

    /// <summary>
    /// 257 x 257 samples (RGBA) to 256 x 256: texel j reads the tile at sample j x 256 / 255,
    /// blending the two nearest samples each way. Texel 0 is sample 0 and texel 255 is sample
    /// 256, so the edges are exact.
    /// </summary>
    private static byte[] ToColourSamples(byte[] full)
    {
        const int n = HeightTile.Samples, m = ColourSamples;
        if (m == n) return full;
        var source = new int[m];
        var weight = new int[m]; // weight of the second sample, out of 256
        for (int j = 0; j < m; j++)
        {
            double s = j * (double)(n - 1) / (m - 1);
            source[j] = Math.Min((int)s, n - 2);
            weight[j] = (int)Math.Round((s - source[j]) * 256.0);
        }

        var pixels = new byte[m * m * 4];
        for (int row = 0; row < m; row++)
        {
            int y = source[row], wy = weight[row];
            for (int column = 0; column < m; column++)
            {
                int x = source[column], wx = weight[column];
                int a = (y * n + x) * 4, b = a + 4, c = a + n * 4, d = c + 4;
                for (int ch = 0; ch < 4; ch++)
                {
                    int top = full[a + ch] * (256 - wx) + full[b + ch] * wx;
                    int bottom = full[c + ch] * (256 - wx) + full[d + ch] * wx;
                    pixels[(row * m + column) * 4 + ch] = (byte)((top * (256 - wy) + bottom * wy + 32768) >> 16);
                }
            }
        }

        return pixels;
    }

    /// <summary>A neutral land colour and the shallowest water, for before anything is loaded.</summary>
    public static byte[] BlankColour
    {
        get
        {
            var pixels = new byte[ColourSamples * ColourSamples * 4];
            for (int i = 0; i < ColourSamples * ColourSamples; i++)
            {
                pixels[i * 4] = 92;
                pixels[i * 4 + 1] = 90;
                pixels[i * 4 + 2] = 70;
                pixels[i * 4 + 3] = 0;
            }

            return pixels;
        }
    }

    /// <summary>Water tiles: the share of water round each sample, 1 byte a sample.</summary>
    public static byte[] DecodeWater(byte[] file) => Resample8(SurfaceTile.Decode(file).Data);

    /// <summary>Land cover tiles: the kind of ground at each sample, 1 byte a sample, never blended.</summary>
    public static byte[] DecodeCover(byte[] file) => ResampleNearest8(SurfaceTile.Decode(file).Data);

    public static byte[] BlankWater => new byte[Texels * Texels];

    private static byte[] DecodeJpeg(byte[] jpeg)
    {
        var image = new Image();
        Error error = image.LoadJpgFromBuffer(jpeg);
        if (error != Error.Ok) throw new System.IO.InvalidDataException($"colour picture could not be read ({error})");
        if (image.GetFormat() != Image.Format.Rgb8) image.Convert(Image.Format.Rgb8);
        byte[] rgb = image.GetData();
        image.Dispose();
        return rgb;
    }
}
