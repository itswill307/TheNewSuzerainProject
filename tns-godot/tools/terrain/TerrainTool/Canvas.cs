using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Tns.Tools.Terrain;

/// <summary>
/// A small RGB picture that can be filled, labelled and saved as a PNG, so the tool can draw
/// reference maps without any imaging library.
/// </summary>
public sealed class Canvas
{
    public readonly int Width, Height;
    private readonly byte[] _pixels;

    public Canvas(int width, int height, (byte R, byte G, byte B) fill)
    {
        Width = width;
        Height = height;
        _pixels = new byte[width * height * 3];
        FillRect(0, 0, width, height, fill);
    }

    public void Set(int x, int y, (byte R, byte G, byte B) color)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        int at = (y * Width + x) * 3;
        _pixels[at] = color.R;
        _pixels[at + 1] = color.G;
        _pixels[at + 2] = color.B;
    }

    /// <summary>Moves a pixel part of the way toward a colour.</summary>
    public void Blend(int x, int y, (byte R, byte G, byte B) color, double amount)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        int at = (y * Width + x) * 3;
        _pixels[at] = (byte)(_pixels[at] + (color.R - _pixels[at]) * amount);
        _pixels[at + 1] = (byte)(_pixels[at + 1] + (color.G - _pixels[at + 1]) * amount);
        _pixels[at + 2] = (byte)(_pixels[at + 2] + (color.B - _pixels[at + 2]) * amount);
    }

    public void FillRect(int x, int y, int width, int height, (byte R, byte G, byte B) color)
    {
        for (int row = y; row < y + height; row++)
        {
            for (int column = x; column < x + width; column++) Set(column, row, color);
        }
    }

    /// <summary>Draws text in a 5 x 7 pixel font, enlarged by <paramref name="scale"/>. Returns the width drawn.</summary>
    /// <summary>
    /// Draws text with its top at y. On Windows it uses a smooth system font (Segoe UI, about 15
    /// pixels high at scale 2); elsewhere a built-in 5 x 7 pixel font. Returns the width drawn.
    /// </summary>
    public int Text(int x, int y, string text, (byte R, byte G, byte B) color, int scale = 2)
    {
        if (OperatingSystem.IsWindows()) return SmoothText(x, y, text, color, scale);
        return PixelText(x, y, text, color, scale);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private int SmoothText(int x, int y, string text, (byte R, byte G, byte B) color, int scale)
    {
        using var font = new System.Drawing.Font("Segoe UI", 8.4f * scale, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        System.Drawing.SizeF size;
        using (var probe = new System.Drawing.Bitmap(1, 1))
        using (var measure = System.Drawing.Graphics.FromImage(probe))
        {
            size = measure.MeasureString(text, font, int.MaxValue, System.Drawing.StringFormat.GenericTypographic);
        }

        int w = (int)Math.Ceiling(size.Width) + 4, h = (int)Math.Ceiling(size.Height) + 4;
        using var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.Black);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawString(text, font, System.Drawing.Brushes.White, 1, 1, System.Drawing.StringFormat.GenericTypographic);
        }

        // The white-on-black picture is how much each pixel is covered; blend the colour by it.
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var bytes = new byte[data.Stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bitmap.UnlockBits(data);
        int top = y - (int)Math.Round(1.6 * scale);
        for (int row = 0; row < h; row++)
        {
            for (int column = 0; column < w; column++)
            {
                byte coverage = bytes[row * data.Stride + column * 3 + 1];
                if (coverage > 0) Blend(x + column - 1, top + row, color, coverage / 255.0);
            }
        }

        return (int)Math.Ceiling(size.Width);
    }

    private int PixelText(int x, int y, string text, (byte R, byte G, byte B) color, int scale)
    {
        int start = x;
        foreach (char raw in text)
        {
            char c = char.ToUpperInvariant(raw);
            if (Glyphs.TryGetValue(c, out string[] rows))
            {
                for (int row = 0; row < 7; row++)
                {
                    for (int column = 0; column < 5; column++)
                    {
                        if (rows[row][column] == '#') FillRect(x + column * scale, y + row * scale, scale, scale, color);
                    }
                }
            }

            x += 6 * scale;
        }

        return x - start;
    }

    public static int TextWidth(string text, int scale = 2) => text.Length * 6 * scale;

    public void SavePng(string path)
    {
        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8; // bits per channel
        header[9] = 2; // RGB
        WriteChunk(file, "IHDR", header);

        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int row = 0; row < Height; row++)
            {
                zlib.WriteByte(0); // no row filter
                zlib.Write(_pixels, row * Width * 3, Width * 3);
            }
        }

        WriteChunk(file, "IDAT", packed.ToArray());
        WriteChunk(file, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        var typed = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) typed[i] = (byte)type[i];
        data.CopyTo(typed, 4);
        stream.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(typed));
        stream.Write(number);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static uint Crc(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['A'] = new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
        ['B'] = new[] { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." },
        ['C'] = new[] { ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###." },
        ['D'] = new[] { "####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####." },
        ['E'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" },
        ['F'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#...." },
        ['G'] = new[] { ".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".###." },
        ['H'] = new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
        ['I'] = new[] { ".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###." },
        ['J'] = new[] { "..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##.." },
        ['K'] = new[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" },
        ['L'] = new[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" },
        ['M'] = new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" },
        ['N'] = new[] { "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#", "#...#" },
        ['O'] = new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
        ['P'] = new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." },
        ['Q'] = new[] { ".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#" },
        ['R'] = new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" },
        ['S'] = new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." },
        ['T'] = new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." },
        ['U'] = new[] { "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
        ['V'] = new[] { "#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.." },
        ['W'] = new[] { "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#" },
        ['X'] = new[] { "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#" },
        ['Y'] = new[] { "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.." },
        ['Z'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", "#....", "#####" },
        ['0'] = new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." },
        ['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
        ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
        ['3'] = new[] { "####.", "....#", "....#", ".###.", "....#", "....#", "####." },
        ['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." },
        ['5'] = new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." },
        ['6'] = new[] { ".###.", "#....", "#....", "####.", "#...#", "#...#", ".###." },
        ['7'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." },
        ['8'] = new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." },
        ['9'] = new[] { ".###.", "#...#", "#...#", ".####", "....#", "....#", ".###." },
        ['.'] = new[] { ".....", ".....", ".....", ".....", ".....", ".....", "..#.." },
        [','] = new[] { ".....", ".....", ".....", ".....", ".....", "..#..", ".#..." },
        ['-'] = new[] { ".....", ".....", ".....", ".###.", ".....", ".....", "....." },
        [':'] = new[] { ".....", "..#..", ".....", ".....", ".....", "..#..", "....." },
        ['%'] = new[] { "##..#", "##.#.", "...#.", "..#..", ".#...", ".#.##", "#..##" },
        ['/'] = new[] { "....#", "....#", "...#.", "..#..", ".#...", "#....", "#...." },
        ['('] = new[] { "...#.", "..#..", ".#...", ".#...", ".#...", "..#..", "...#." },
        [')'] = new[] { ".#...", "..#..", "...#.", "...#.", "...#.", "..#..", ".#..." },
        ['+'] = new[] { ".....", "..#..", "..#..", "#####", "..#..", "..#..", "....." },
        ['='] = new[] { ".....", ".....", "#####", ".....", "#####", ".....", "....." },
    };
}
