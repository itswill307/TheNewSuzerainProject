using System;
using System.Collections.Generic;
using System.IO;

namespace Tns.Tools.Terrain;

/// <summary>
/// A small baseline JPEG encoder and decoder for RGB images, so colour tiles can be stored a
/// tenth of the size of lossless ones without depending on an imaging library. The encoder
/// writes standard JFIF files with 4:2:0 colour and the standard tables, which every JPEG
/// reader (the game's included) can open. The decoder reads those, and other plain baseline
/// files without restart markers.
/// </summary>
public static class Jpeg
{
    private static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21,
        28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61,
        54, 47, 55, 62, 63,
    };

    private static readonly byte[] LumaQuality =
    {
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51,
        87, 80, 62, 18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    };

    private static readonly byte[] ChromaQuality =
    {
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99,
    };

    // Standard Huffman tables (JPEG Annex K.3): code counts per length 1-16, then the values.
    private static readonly byte[] DcLumaCounts = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcLumaValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly byte[] DcChromaCounts = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcChromaValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly byte[] AcLumaCounts = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
    private static readonly byte[] AcLumaValues =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14,
        0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09,
        0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a,
        0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65,
        0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88,
        0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9,
        0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca,
        0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea,
        0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    };
    private static readonly byte[] AcChromaCounts = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
    private static readonly byte[] AcChromaValues =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32,
        0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16,
        0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39,
        0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64,
        0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86,
        0x87, 0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8,
        0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9,
        0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    };

    /// <summary>cos((2x + 1) u pi / 16) scaled for the DCT, [u * 8 + x].</summary>
    private static readonly float[] Cosines = MakeCosines();

    private static float[] MakeCosines()
    {
        var table = new float[64];
        for (int u = 0; u < 8; u++)
        {
            double scale = u == 0 ? Math.Sqrt(0.125) : 0.5;
            for (int x = 0; x < 8; x++) table[u * 8 + x] = (float)(scale * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        }

        return table;
    }

    // ---------------------------------------------------------------------------------------------
    // Encoding

    /// <summary>Encodes RGB bytes (row 0 at the top, 3 bytes a pixel) as a JPEG of the given quality (1-100).</summary>
    public static byte[] Encode(byte[] rgb, int width, int height, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var lumaTable = new int[64];
        var chromaTable = new int[64];
        for (int i = 0; i < 64; i++)
        {
            lumaTable[i] = Math.Clamp((LumaQuality[i] * scale + 50) / 100, 1, 255);
            chromaTable[i] = Math.Clamp((ChromaQuality[i] * scale + 50) / 100, 1, 255);
        }

        // Colour space conversion, with the image padded out to whole 16 x 16 blocks.
        int paddedWidth = (width + 15) / 16 * 16, paddedHeight = (height + 15) / 16 * 16;
        var y = new float[paddedWidth * paddedHeight];
        var cb = new float[paddedWidth * paddedHeight];
        var cr = new float[paddedWidth * paddedHeight];
        for (int row = 0; row < paddedHeight; row++)
        {
            int sourceRow = Math.Min(row, height - 1);
            for (int column = 0; column < paddedWidth; column++)
            {
                int at = (sourceRow * width + Math.Min(column, width - 1)) * 3;
                float r = rgb[at], g = rgb[at + 1], b = rgb[at + 2];
                int to = row * paddedWidth + column;
                y[to] = 0.299f * r + 0.587f * g + 0.114f * b - 128f;
                cb[to] = -0.168736f * r - 0.331264f * g + 0.5f * b;
                cr[to] = 0.5f * r - 0.418688f * g - 0.081312f * b;
            }
        }

        var output = new MemoryStream();
        output.Write(new byte[] { 0xFF, 0xD8 });
        // JFIF header, so every reader treats the colours as YCbCr.
        output.Write(new byte[] { 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 });
        WriteQuantTable(output, 0, lumaTable);
        WriteQuantTable(output, 1, chromaTable);
        output.Write(new byte[]
        {
            0xFF, 0xC0, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
            1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
        });
        WriteHuffmanTable(output, 0x00, DcLumaCounts, DcLumaValues);
        WriteHuffmanTable(output, 0x10, AcLumaCounts, AcLumaValues);
        WriteHuffmanTable(output, 0x01, DcChromaCounts, DcChromaValues);
        WriteHuffmanTable(output, 0x11, AcChromaCounts, AcChromaValues);
        output.Write(new byte[] { 0xFF, 0xDA, 0, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0 });

        var dcLuma = Codes(DcLumaCounts, DcLumaValues);
        var acLuma = Codes(AcLumaCounts, AcLumaValues);
        var dcChroma = Codes(DcChromaCounts, DcChromaValues);
        var acChroma = Codes(AcChromaCounts, AcChromaValues);
        var bits = new BitWriter(output);
        var block = new float[64];
        var chromaBlock = new float[64];
        int previousY = 0, previousCb = 0, previousCr = 0;

        for (int blockRow = 0; blockRow < paddedHeight; blockRow += 16)
        {
            for (int blockColumn = 0; blockColumn < paddedWidth; blockColumn += 16)
            {
                for (int i = 0; i < 4; i++)
                {
                    Gather(y, paddedWidth, blockColumn + (i & 1) * 8, blockRow + (i >> 1) * 8, block);
                    previousY = EncodeBlock(bits, block, lumaTable, previousY, dcLuma, acLuma);
                }

                GatherHalf(cb, paddedWidth, blockColumn, blockRow, chromaBlock);
                previousCb = EncodeBlock(bits, chromaBlock, chromaTable, previousCb, dcChroma, acChroma);
                GatherHalf(cr, paddedWidth, blockColumn, blockRow, chromaBlock);
                previousCr = EncodeBlock(bits, chromaBlock, chromaTable, previousCr, dcChroma, acChroma);
            }
        }

        bits.Flush();
        output.Write(new byte[] { 0xFF, 0xD9 });
        return output.ToArray();
    }

    private static void Gather(float[] plane, int stride, int x, int y, float[] block)
    {
        for (int row = 0; row < 8; row++)
        {
            for (int column = 0; column < 8; column++) block[row * 8 + column] = plane[(y + row) * stride + x + column];
        }
    }

    /// <summary>An 8 x 8 block of a colour plane at half resolution, from the 16 x 16 pixels it covers.</summary>
    private static void GatherHalf(float[] plane, int stride, int x, int y, float[] block)
    {
        for (int row = 0; row < 8; row++)
        {
            for (int column = 0; column < 8; column++)
            {
                int at = (y + row * 2) * stride + x + column * 2;
                block[row * 8 + column] = (plane[at] + plane[at + 1] + plane[at + stride] + plane[at + stride + 1]) * 0.25f;
            }
        }
    }

    private static int EncodeBlock(BitWriter bits, float[] block, int[] table, int previousDc,
        (int Code, int Length)[] dc, (int Code, int Length)[] ac)
    {
        Span<float> temp = stackalloc float[64];
        Span<int> quantised = stackalloc int[64];
        // Forward DCT: rows, then columns.
        for (int row = 0; row < 8; row++)
        {
            for (int u = 0; u < 8; u++)
            {
                float sum = 0f;
                for (int x = 0; x < 8; x++) sum += Cosines[u * 8 + x] * block[row * 8 + x];
                temp[row * 8 + u] = sum;
            }
        }

        for (int column = 0; column < 8; column++)
        {
            for (int v = 0; v < 8; v++)
            {
                float sum = 0f;
                for (int y = 0; y < 8; y++) sum += Cosines[v * 8 + y] * temp[y * 8 + column];
                int index = v * 8 + column;
                quantised[index] = (int)MathF.Round(sum / table[index]);
            }
        }

        int dcValue = quantised[0];
        WriteValue(bits, dcValue - previousDc, dc, isDc: true);
        int zeros = 0;
        for (int k = 1; k < 64; k++)
        {
            int value = quantised[ZigZag[k]];
            if (value == 0)
            {
                zeros++;
                continue;
            }

            while (zeros >= 16)
            {
                bits.Write(ac[0xF0].Code, ac[0xF0].Length);
                zeros -= 16;
            }

            int size = BitSize(value);
            var code = ac[(zeros << 4) | size];
            bits.Write(code.Code, code.Length);
            bits.Write(value < 0 ? value + (1 << size) - 1 : value, size);
            zeros = 0;
        }

        if (zeros > 0) bits.Write(ac[0].Code, ac[0].Length);
        return dcValue;
    }

    private static void WriteValue(BitWriter bits, int value, (int Code, int Length)[] table, bool isDc)
    {
        int size = BitSize(value);
        bits.Write(table[size].Code, table[size].Length);
        if (size > 0) bits.Write(value < 0 ? value + (1 << size) - 1 : value, size);
    }

    private static int BitSize(int value)
    {
        value = Math.Abs(value);
        int size = 0;
        while (value > 0)
        {
            size++;
            value >>= 1;
        }

        return size;
    }

    private static (int Code, int Length)[] Codes(byte[] counts, byte[] values)
    {
        var table = new (int, int)[256];
        int code = 0, at = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < counts[length - 1]; i++) table[values[at++]] = (code++, length);
            code <<= 1;
        }

        return table;
    }

    private static void WriteQuantTable(Stream output, int id, int[] table)
    {
        output.Write(new byte[] { 0xFF, 0xDB, 0, 67, (byte)id });
        for (int k = 0; k < 64; k++) output.WriteByte((byte)table[ZigZag[k]]);
    }

    private static void WriteHuffmanTable(Stream output, int id, byte[] counts, byte[] values)
    {
        int length = 2 + 1 + 16 + values.Length;
        output.Write(new byte[] { 0xFF, 0xC4, (byte)(length >> 8), (byte)length, (byte)id });
        output.Write(counts);
        output.Write(values);
    }

    private sealed class BitWriter
    {
        private readonly Stream _output;
        private int _buffer, _count;

        public BitWriter(Stream output) => _output = output;

        public void Write(int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((value >> i) & 1);
                if (++_count < 8) continue;
                _output.WriteByte((byte)_buffer);
                if (_buffer == 0xFF) _output.WriteByte(0);
                _buffer = 0;
                _count = 0;
            }
        }

        public void Flush()
        {
            // Pad the last byte with ones, as the standard asks.
            while (_count != 0) Write(1, 1);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Decoding

    private sealed class Component
    {
        public int Id, H, V, Quant, DcTable, AcTable, Previous;
        public float[] Plane;
        public int Stride;
    }

    private sealed class Huffman
    {
        public readonly Dictionary<(int Length, int Code), byte> Lookup = new();
    }

    /// <summary>Decodes a baseline JPEG to RGB bytes, row 0 at the top.</summary>
    public static byte[] Decode(byte[] file, out int width, out int height)
    {
        var quant = new int[4][];
        var huffman = new Huffman[8];
        var components = new List<Component>();
        width = height = 0;
        int at = 2;
        if (file.Length < 4 || file[0] != 0xFF || file[1] != 0xD8) throw new InvalidDataException("Not a JPEG file.");

        while (at < file.Length)
        {
            if (file[at] != 0xFF) throw new InvalidDataException("Damaged JPEG file.");
            int marker = file[at + 1];
            at += 2;
            if (marker == 0xD9) break;
            int length = (file[at] << 8) | file[at + 1];
            int body = at + 2, end = at + length;
            switch (marker)
            {
                case 0xDB:
                    for (int p = body; p < end;)
                    {
                        int id = file[p] & 15;
                        if (file[p] >> 4 != 0) throw new NotSupportedException("16-bit JPEG tables are not supported.");
                        quant[id] = new int[64];
                        for (int k = 0; k < 64; k++) quant[id][ZigZag[k]] = file[p + 1 + k];
                        p += 65;
                    }

                    break;
                case 0xC4:
                    for (int p = body; p < end;)
                    {
                        int id = file[p];
                        var table = new Huffman();
                        int code = 0, valueAt = p + 17;
                        for (int bitLength = 1; bitLength <= 16; bitLength++)
                        {
                            for (int i = 0; i < file[p + bitLength]; i++) table.Lookup[(bitLength, code++)] = file[valueAt++];
                            code <<= 1;
                        }

                        huffman[(id >> 4) * 4 + (id & 3)] = table;
                        p = valueAt;
                    }

                    break;
                case 0xC0:
                case 0xC1:
                    height = (file[body + 1] << 8) | file[body + 2];
                    width = (file[body + 3] << 8) | file[body + 4];
                    for (int i = 0; i < file[body + 5]; i++)
                    {
                        int p = body + 6 + i * 3;
                        components.Add(new Component { Id = file[p], H = file[p + 1] >> 4, V = file[p + 1] & 15, Quant = file[p + 2] });
                    }

                    break;
                case 0xC2:
                    throw new NotSupportedException("Progressive JPEGs are not supported.");
                case 0xDD:
                    throw new NotSupportedException("JPEGs with restart markers are not supported.");
                case 0xDA:
                    for (int i = 0; i < file[body]; i++)
                    {
                        Component component = components.Find(c => c.Id == file[body + 1 + i * 2])!;
                        component.DcTable = file[body + 2 + i * 2] >> 4;
                        component.AcTable = file[body + 2 + i * 2] & 15;
                    }

                    at = DecodeScan(file, end, components, quant, huffman, width, height);
                    continue;
            }

            at = end;
        }

        return ToRgb(components, width, height);
    }

    private static int DecodeScan(byte[] file, int at, List<Component> components, int[][] quant, Huffman[] huffman,
        int width, int height)
    {
        int maxH = 1, maxV = 1;
        foreach (Component c in components)
        {
            maxH = Math.Max(maxH, c.H);
            maxV = Math.Max(maxV, c.V);
        }

        int mcuWidth = 8 * maxH, mcuHeight = 8 * maxV;
        int mcusAcross = (width + mcuWidth - 1) / mcuWidth, mcusDown = (height + mcuHeight - 1) / mcuHeight;
        foreach (Component c in components)
        {
            c.Stride = mcusAcross * c.H * 8;
            c.Plane = new float[c.Stride * mcusDown * c.V * 8];
        }

        var reader = new BitReader(file, at);
        var coefficients = new float[64];
        Span<float> temp = stackalloc float[64];
        for (int mcuRow = 0; mcuRow < mcusDown; mcuRow++)
        {
            for (int mcuColumn = 0; mcuColumn < mcusAcross; mcuColumn++)
            {
                foreach (Component c in components)
                {
                    for (int v = 0; v < c.V; v++)
                    {
                        for (int h = 0; h < c.H; h++)
                        {
                            Array.Clear(coefficients);
                            Huffman dc = huffman[c.DcTable], ac = huffman[4 + c.AcTable];
                            int size = reader.Symbol(dc);
                            c.Previous += Extend(reader.Bits(size), size);
                            coefficients[0] = c.Previous * quant[c.Quant][0];
                            for (int k = 1; k < 64;)
                            {
                                int symbol = reader.Symbol(ac);
                                int run = symbol >> 4, bitSize = symbol & 15;
                                if (bitSize == 0)
                                {
                                    if (run != 15) break;
                                    k += 16;
                                    continue;
                                }

                                k += run;
                                if (k > 63) break;
                                int index = ZigZag[k];
                                coefficients[index] = Extend(reader.Bits(bitSize), bitSize) * quant[c.Quant][index];
                                k++;
                            }

                            // Inverse DCT: columns, then rows.
                            for (int column = 0; column < 8; column++)
                            {
                                for (int y = 0; y < 8; y++)
                                {
                                    float sum = 0f;
                                    for (int vv = 0; vv < 8; vv++) sum += Cosines[vv * 8 + y] * coefficients[vv * 8 + column];
                                    temp[y * 8 + column] = sum;
                                }
                            }

                            int x0 = (mcuColumn * c.H + h) * 8, y0 = (mcuRow * c.V + v) * 8;
                            for (int y = 0; y < 8; y++)
                            {
                                for (int x = 0; x < 8; x++)
                                {
                                    float sum = 0f;
                                    for (int u = 0; u < 8; u++) sum += Cosines[u * 8 + x] * temp[y * 8 + u];
                                    c.Plane[(y0 + y) * c.Stride + x0 + x] = sum;
                                }
                            }
                        }
                    }
                }
            }
        }

        return reader.End();
    }

    private static int Extend(int value, int size) => size == 0 ? 0 : value < (1 << (size - 1)) ? value - (1 << size) + 1 : value;

    private static byte[] ToRgb(List<Component> components, int width, int height)
    {
        int maxH = 1, maxV = 1;
        foreach (Component c in components)
        {
            maxH = Math.Max(maxH, c.H);
            maxV = Math.Max(maxV, c.V);
        }

        var rgb = new byte[width * height * 3];
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                float Sample(Component c) =>
                    c.Plane[(row * c.V / maxV) * c.Stride + column * c.H / maxH];

                float y = Sample(components[0]) + 128f;
                int to = (row * width + column) * 3;
                if (components.Count < 3)
                {
                    rgb[to] = rgb[to + 1] = rgb[to + 2] = Clamp(y);
                    continue;
                }

                float cb = Sample(components[1]), cr = Sample(components[2]);
                rgb[to] = Clamp(y + 1.402f * cr);
                rgb[to + 1] = Clamp(y - 0.344136f * cb - 0.714136f * cr);
                rgb[to + 2] = Clamp(y + 1.772f * cb);
            }
        }

        return rgb;
    }

    private static byte Clamp(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _at, _buffer, _count;

        public BitReader(byte[] data, int at)
        {
            _data = data;
            _at = at;
        }

        private int Bit()
        {
            if (_count == 0)
            {
                byte next = _data[_at++];
                if (next == 0xFF)
                {
                    byte after = _data[_at];
                    if (after == 0) _at++;
                    else
                    {
                        // A marker: the data has ended. Feed ones from here on.
                        _at--;
                        next = 0xFF;
                    }
                }

                _buffer = next;
                _count = 8;
            }

            _count--;
            return (_buffer >> _count) & 1;
        }

        public int Bits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++) value = (value << 1) | Bit();
            return value;
        }

        public int Symbol(Huffman table)
        {
            int code = 0;
            for (int length = 1; length <= 16; length++)
            {
                code = (code << 1) | Bit();
                if (table.Lookup.TryGetValue((length, code), out byte value)) return value;
            }

            throw new InvalidDataException("Damaged JPEG data.");
        }

        /// <summary>Where the next marker starts, after the scan data.</summary>
        public int End()
        {
            int at = _at;
            while (at + 1 < _data.Length)
            {
                byte next = _data[at + 1];
                if (_data[at] == 0xFF && next != 0 && next != 0xFF && (next < 0xD0 || next > 0xD7)) break;
                at++;
            }

            return at;
        }
    }
}
