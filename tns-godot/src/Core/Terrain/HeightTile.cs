using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Tns.Core.Terrain;

/// <summary>
/// One terrain tile: a square grid of ground heights in metres, 257 samples a side. Row 0 is the
/// south edge and column 0 the west edge. The outer rows and columns sit exactly on the tile
/// border, so neighbouring tiles repeat each other's edge samples and join without gaps.
///
/// File layout (little-endian), kept simple so other tools can read and write it:
///
///   bytes 0-3    "TNSH"
///   byte  4      version, currently 1
///   byte  5      flags; bit 0 set = every sample has the same value and there is no payload
///   bytes 6-7    samples per side (257)
///   bytes 8-11   float: metres per stored unit
///   bytes 12-13  int16: lowest stored value in the tile
///   bytes 14-15  int16: highest stored value in the tile
///   bytes 16-    zlib stream of 2 x samples x samples bytes: the low bytes of every value, then
///                the high bytes. Each value is stored as the difference from a prediction
///                (west + south - south-west neighbour), which is what makes it compress well.
///
/// Height in metres = stored value x metres per unit.
/// </summary>
public sealed class HeightTile
{
    public const int Samples = 257;
    public const int Intervals = Samples - 1;
    public const int SampleCount = Samples * Samples;
    public const string FileExtension = ".tnsh";
    private const int HeaderSize = 16;
    private const byte Version = 1;

    /// <summary>Heights in metres, index = row * Samples + column.</summary>
    public readonly float[] Heights;
    public float MinHeight { get; private set; }
    public float MaxHeight { get; private set; }

    public HeightTile(float[] heights)
    {
        if (heights.Length != SampleCount) throw new ArgumentException("A tile needs 257 x 257 samples.");
        Heights = heights;
        RefreshRange();
    }

    public void RefreshRange()
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (float h in Heights)
        {
            if (h < min) min = h;
            if (h > max) max = h;
        }

        MinHeight = min;
        MaxHeight = max;
    }

    /// <summary>Packs the tile into its file form. Heights are rounded to the nearest metresPerUnit.</summary>
    public byte[] Encode(float metresPerUnit = 1f) => Encode(metresPerUnit, CompressionLevel.SmallestSize);

    /// <summary>The same, with a chosen zlib effort (the reader does not need to know which).</summary>
    public byte[] Encode(float metresPerUnit, CompressionLevel level)
    {
        var stored = new short[SampleCount];
        short min = short.MaxValue, max = short.MinValue;
        for (int i = 0; i < SampleCount; i++)
        {
            double units = Math.Round(Heights[i] / metresPerUnit);
            short value = (short)Math.Clamp(units, short.MinValue, short.MaxValue);
            stored[i] = value;
            if (value < min) min = value;
            if (value > max) max = value;
        }

        bool constant = min == max;
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[HeaderSize];
        "TNSH"u8.CopyTo(header);
        header[4] = Version;
        header[5] = (byte)(constant ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], Samples);
        BinaryPrimitives.WriteSingleLittleEndian(header[8..], metresPerUnit);
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], min);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], max);
        output.Write(header);
        if (constant) return output.ToArray();

        var planes = new byte[SampleCount * 2];
        for (int row = 0; row < Samples; row++)
        {
            for (int column = 0; column < Samples; column++)
            {
                int i = row * Samples + column;
                int west = column > 0 ? stored[i - 1] : 0;
                int south = row > 0 ? stored[i - Samples] : 0;
                int southWest = row > 0 && column > 0 ? stored[i - Samples - 1] : 0;
                ushort difference = (ushort)(stored[i] - (west + south - southWest));
                planes[i] = (byte)difference;
                planes[SampleCount + i] = (byte)(difference >> 8);
            }
        }

        using (var zlib = new ZLibStream(output, level, leaveOpen: true))
        {
            zlib.Write(planes);
        }

        return output.ToArray();
    }

    public static HeightTile Decode(byte[] file)
    {
        if (file.Length < HeaderSize || !file.AsSpan(0, 4).SequenceEqual("TNSH"u8))
        {
            throw new InvalidDataException("Not a terrain height tile (missing TNSH header).");
        }

        if (file[4] != Version) throw new InvalidDataException($"Unsupported tile version {file[4]}.");
        int samples = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6));
        if (samples != Samples) throw new InvalidDataException($"Unsupported tile size {samples}.");
        float metresPerUnit = BinaryPrimitives.ReadSingleLittleEndian(file.AsSpan(8));
        short min = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(12));

        var heights = new float[SampleCount];
        if ((file[5] & 1) != 0)
        {
            Array.Fill(heights, min * metresPerUnit);
            return new HeightTile(heights);
        }

        var planes = new byte[SampleCount * 2];
        using (var zlib = new ZLibStream(
            new MemoryStream(file, HeaderSize, file.Length - HeaderSize), CompressionMode.Decompress))
        {
            zlib.ReadExactly(planes);
        }

        var stored = new short[SampleCount];
        for (int row = 0; row < Samples; row++)
        {
            for (int column = 0; column < Samples; column++)
            {
                int i = row * Samples + column;
                int west = column > 0 ? stored[i - 1] : 0;
                int south = row > 0 ? stored[i - Samples] : 0;
                int southWest = row > 0 && column > 0 ? stored[i - Samples - 1] : 0;
                int difference = (short)(planes[i] | (planes[SampleCount + i] << 8));
                stored[i] = (short)(difference + west + south - southWest);
                heights[i] = stored[i] * metresPerUnit;
            }
        }

        return new HeightTile(heights);
    }

    /// <summary>Height at a fractional position inside the tile, blended from the four samples round it.</summary>
    public float SampleBilinear(double column, double row)
    {
        column = Math.Clamp(column, 0.0, Intervals);
        row = Math.Clamp(row, 0.0, Intervals);
        int c0 = Math.Min((int)column, Intervals - 1);
        int r0 = Math.Min((int)row, Intervals - 1);
        float fc = (float)(column - c0);
        float fr = (float)(row - r0);
        int i = r0 * Samples + c0;
        float south = Heights[i] + (Heights[i + 1] - Heights[i]) * fc;
        float north = Heights[i + Samples] + (Heights[i + Samples + 1] - Heights[i + Samples]) * fc;
        return south + (north - south) * fr;
    }
}
