using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Tns.Core.Terrain;

/// <summary>
/// One surface tile: what the ground looks like, on the same 257 x 257 grid as a height tile
/// (row 0 south, edge samples shared with the neighbours). Each sample holds one or more bytes,
/// called channels. Three kinds of pack use it:
///
///   cover  (.tnsk),  1 channel: what kind of ground the sample is (Cover class 0-10, such as
///                    forest, cropland, city or water). Used up close, where the game draws
///                    sharp edges between kinds and colours each kind from a colour map.
///   colour (.tnsc), 4 channels: red, green, blue of the land in sRGB, and the water's tone
///                    (0-254 from the shallowest to the deepest sea, 255 for a lake).
///                    The land colour is the average over all the kinds of ground round the
///                    sample, for when the map is too far away to show them one by one, and
///                    carries on a little way out over water.
///   water  (.tnsw),  1 channel: how much of the ground round the sample is water, 0 to 255,
///                    also for distant views.
///
/// File layout (little-endian):
///
///   bytes 0-3    "TNSS"
///   byte  4      version, currently 1
///   byte  5      flags; bit 0 set = every sample of every channel is the same, no payload;
///                bit 1 set = the first three channels are a JPEG picture (colour tiles);
///                bit 2 set = bytes are stored as they are, without the prediction below
///                (cover tiles, whose values are names, not amounts)
///   bytes 6-7    samples per side (257)
///   byte  8      channels
///   bytes 9-15   the constant value of each channel when bit 0 is set (up to 4), else 0
///   bytes 16-    if bit 1 is set: a 4-byte length, then that many bytes of baseline JPEG
///                holding channels 0-2 as RGB, row 0 = south, first. Then a zlib stream of
///                the remaining channels x samples x samples bytes, one channel after the
///                other. Each byte is stored as the difference from a prediction (west +
///                south - south-west), wrapping round at 256.
///
/// JPEG keeps colour tiles about a tenth of the size of lossless ones; the colours it changes
/// by a few levels are far below what can be seen on a map.
/// </summary>
public sealed class SurfaceTile
{
    public const int Samples = HeightTile.Samples;
    public const int SampleCount = HeightTile.SampleCount;
    public const string ColourExtension = ".tnsc";
    public const string WaterExtension = ".tnsw";
    public const string CoverExtension = ".tnsk";
    public const string DetailExtension = ".tnsr";
    private const int HeaderSize = 16;
    private const byte Version = 1;

    public readonly int Channels;

    /// <summary>Channel planes: index = channel * SampleCount + row * Samples + column.</summary>
    public readonly byte[] Data;

    public SurfaceTile(int channels, byte[] data)
    {
        if (data.Length != channels * SampleCount) throw new ArgumentException("Wrong amount of tile data.");
        Channels = channels;
        Data = data;
    }

    /// <summary>The samples interleaved, as a graphics card wants them: RGBA RGBA ... or R R R ...</summary>
    public byte[] Interleaved()
    {
        if (Channels == 1) return Data;
        var pixels = new byte[Data.Length];
        for (int c = 0; c < Channels; c++)
        {
            int plane = c * SampleCount;
            for (int i = 0; i < SampleCount; i++) pixels[i * Channels + c] = Data[plane + i];
        }

        return pixels;
    }

    /// <summary>
    /// Packs the tile into its file form. With <paramref name="compressColour"/> (RGB bytes in,
    /// JPEG out), the first three channels are stored as a JPEG.
    /// </summary>
    public byte[] Encode(Func<byte[], byte[]> compressColour = null, bool predict = true) =>
        Encode(compressColour, predict, CompressionLevel.SmallestSize);

    /// <summary>The same, with a chosen zlib effort (the reader does not need to know which).</summary>
    public byte[] Encode(Func<byte[], byte[]> compressColour, bool predict, CompressionLevel level)
    {
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[HeaderSize];
        "TNSS"u8.CopyTo(header);
        header[4] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], Samples);
        header[8] = (byte)Channels;

        bool constant = true;
        for (int c = 0; c < Channels && constant; c++)
        {
            byte first = Data[c * SampleCount];
            for (int i = 1; i < SampleCount; i++)
            {
                if (Data[c * SampleCount + i] != first)
                {
                    constant = false;
                    break;
                }
            }
        }

        if (constant && Channels <= 7)
        {
            header[5] = 1;
            for (int c = 0; c < Channels; c++) header[9 + c] = Data[c * SampleCount];
            output.Write(header);
            return output.ToArray();
        }

        bool jpeg = compressColour != null && Channels >= 3;
        int firstPlain = 0;
        if (jpeg)
        {
            header[5] = 2;
            output.Write(header);
            var rgb = new byte[SampleCount * 3];
            for (int i = 0; i < SampleCount; i++)
            {
                rgb[i * 3] = Data[i];
                rgb[i * 3 + 1] = Data[SampleCount + i];
                rgb[i * 3 + 2] = Data[2 * SampleCount + i];
            }

            byte[] picture = compressColour(rgb);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, picture.Length);
            output.Write(length);
            output.Write(picture);
            firstPlain = 3;
            if (Channels == 3) return output.ToArray();
        }
        else
        {
            if (!predict) header[5] = 4;
            output.Write(header);
        }

        var residuals = new byte[(Channels - firstPlain) * SampleCount];
        if (!predict) Array.Copy(Data, firstPlain * SampleCount, residuals, 0, residuals.Length);
        for (int c = firstPlain; c < Channels && predict; c++)
        {
            int plane = c * SampleCount;
            for (int row = 0; row < Samples; row++)
            {
                for (int column = 0; column < Samples; column++)
                {
                    int i = plane + row * Samples + column;
                    int west = column > 0 ? Data[i - 1] : 0;
                    int south = row > 0 ? Data[i - Samples] : 0;
                    int southWest = row > 0 && column > 0 ? Data[i - Samples - 1] : 0;
                    residuals[i - firstPlain * SampleCount] = (byte)(Data[i] - (west + south - southWest));
                }
            }
        }

        using (var zlib = new ZLibStream(output, level, leaveOpen: true))
        {
            zlib.Write(residuals);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Unpacks a tile. <paramref name="decodeColour"/> turns a JPEG into RGB bytes (3 a sample, in
    /// file order); it is only needed for colour tiles.
    /// </summary>
    public static SurfaceTile Decode(byte[] file, Func<byte[], byte[]> decodeColour = null)
    {
        if (file.Length < HeaderSize || !file.AsSpan(0, 4).SequenceEqual("TNSS"u8))
        {
            throw new InvalidDataException("Not a surface tile (missing TNSS header).");
        }

        if (file[4] != Version) throw new InvalidDataException($"Unsupported surface tile version {file[4]}.");
        int samples = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6));
        if (samples != Samples) throw new InvalidDataException($"Unsupported tile size {samples}.");
        int channels = file[8];
        var data = new byte[channels * SampleCount];

        if ((file[5] & 1) != 0)
        {
            for (int c = 0; c < channels; c++) data.AsSpan(c * SampleCount, SampleCount).Fill(file[9 + c]);
            return new SurfaceTile(channels, data);
        }

        int firstPlain = 0, at = HeaderSize;
        if ((file[5] & 2) != 0)
        {
            if (decodeColour == null) throw new InvalidOperationException("This tile holds a JPEG; pass a JPEG decoder.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(HeaderSize));
            byte[] rgb = decodeColour(file.AsSpan(HeaderSize + 4, length).ToArray());
            if (rgb.Length < SampleCount * 3) throw new InvalidDataException("The tile's JPEG is the wrong size.");
            for (int i = 0; i < SampleCount; i++)
            {
                data[i] = rgb[i * 3];
                data[SampleCount + i] = rgb[i * 3 + 1];
                data[2 * SampleCount + i] = rgb[i * 3 + 2];
            }

            firstPlain = 3;
            at = HeaderSize + 4 + length;
        }

        if (firstPlain < channels)
        {
            using var zlib = new ZLibStream(new MemoryStream(file, at, file.Length - at), CompressionMode.Decompress);
            zlib.ReadExactly(data.AsSpan(firstPlain * SampleCount));
        }

        bool predicted = (file[5] & 4) == 0;
        for (int c = firstPlain; c < channels && predicted; c++)
        {
            int plane = c * SampleCount;
            for (int row = 0; row < Samples; row++)
            {
                for (int column = 0; column < Samples; column++)
                {
                    int i = plane + row * Samples + column;
                    int west = column > 0 ? data[i - 1] : 0;
                    int south = row > 0 ? data[i - Samples] : 0;
                    int southWest = row > 0 && column > 0 ? data[i - Samples - 1] : 0;
                    data[i] = (byte)(data[i] + west + south - southWest);
                }
            }
        }

        return new SurfaceTile(channels, data);
    }
}
