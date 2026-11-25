using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Tns.Tools.Terrain;

/// <summary>
/// A small reader for elevation GeoTIFFs, with no outside dependencies. It covers what elevation
/// data normally uses: classic TIFF and BigTIFF, strips or tiles, no compression, LZW or Deflate,
/// any predictor, and 8/16/32-bit integer or 32/64-bit float samples. Only the first image in the
/// file is read (overviews are ignored) and, if there are several bands, only the first.
///
/// For anything else (ZSTD, JPEG, odd layouts), convert the file first:
///   gdal_translate -co COMPRESS=DEFLATE input.tif output.tif
/// </summary>
public sealed class TiffFile : IDisposable
{
    public string FilePath { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int BitsPerSample { get; private set; } = 1;
    /// <summary>1 = unsigned integer, 2 = signed integer, 3 = floating point.</summary>
    public int SampleFormat { get; private set; } = 1;
    public int SamplesPerPixel { get; private set; } = 1;
    public int Compression { get; private set; } = 1;
    public int Predictor { get; private set; } = 1;
    public bool IsTiled { get; private set; }
    /// <summary>Size of one stored block: a tile, or a strip (full width, RowsPerStrip tall).</summary>
    public int BlockWidth { get; private set; }
    public int BlockHeight { get; private set; }
    public int BlocksAcross { get; private set; }
    public int BlockCount => _blockOffsets.Length;

    /// <summary>Georeferencing: longitude of the west edge and latitude of the north edge.</summary>
    public double OriginX { get; private set; } = double.NaN;
    public double OriginY { get; private set; } = double.NaN;
    /// <summary>Size of one pixel in degrees. Both positive.</summary>
    public double PixelSizeX { get; private set; } = double.NaN;
    public double PixelSizeY { get; private set; } = double.NaN;
    public bool HasGeoreference => !double.IsNaN(OriginX) && !double.IsNaN(PixelSizeX);
    public double? NoData { get; private set; }

    private readonly FileStream _stream;
    private readonly object _streamLock = new();
    private bool _bigEndian;
    private bool _bigTiff;
    private long[] _blockOffsets = Array.Empty<long>();
    private long[] _blockByteCounts = Array.Empty<long>();
    private int _planarConfiguration = 1;

    private TiffFile(string path)
    {
        FilePath = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
    }

    public static TiffFile Open(string path)
    {
        var tiff = new TiffFile(path);
        try
        {
            tiff.ReadHeader();
            return tiff;
        }
        catch
        {
            tiff.Dispose();
            throw;
        }
    }

    public void Dispose() => _stream.Dispose();

    public string Describe()
    {
        string kind = SampleFormat switch { 3 => "float", 2 => "int", _ => "uint" };
        string compression = Compression switch
        {
            1 => "none", 5 => "LZW", 8 or 32946 => "Deflate", _ => $"unsupported ({Compression})",
        };
        string layout = IsTiled ? $"tiles {BlockWidth}x{BlockHeight}" : $"strips of {BlockHeight} rows";
        string geo = HasGeoreference
            ? $"west {OriginX:0.######}, north {OriginY:0.######}, pixel {PixelSizeX:0.########} x {PixelSizeY:0.########} deg"
            : "no georeference";
        return $"{Width} x {Height}, {kind}{BitsPerSample}, {SamplesPerPixel} band(s), {compression}, predictor {Predictor}, " +
               $"{layout}{(_bigTiff ? ", BigTIFF" : "")}\n  {geo}{(NoData.HasValue ? $", no-data {NoData}" : "")}";
    }

    // ---------------------------------------------------------------------------------------------
    // Header

    private void ReadHeader()
    {
        byte[] start = ReadAt(0, 16);
        if (start[0] == 'I' && start[1] == 'I') _bigEndian = false;
        else if (start[0] == 'M' && start[1] == 'M') _bigEndian = true;
        else throw new InvalidDataException("Not a TIFF file.");

        int magic = U16(start, 2);
        long directoryOffset;
        if (magic == 42) directoryOffset = U32(start, 4);
        else if (magic == 43)
        {
            _bigTiff = true;
            directoryOffset = (long)U64(start, 8);
        }
        else throw new InvalidDataException("Not a TIFF file.");

        int countSize = _bigTiff ? 8 : 2;
        int entrySize = _bigTiff ? 20 : 12;
        byte[] countBytes = ReadAt(directoryOffset, countSize);
        long entryCount = _bigTiff ? (long)U64(countBytes, 0) : U16(countBytes, 0);
        byte[] entries = ReadAt(directoryOffset + countSize, (int)(entryCount * entrySize));

        int rowsPerStrip = int.MaxValue;
        long[] stripOffsets = null, stripCounts = null, tileOffsets = null, tileCounts = null;
        int tileWidth = 0, tileHeight = 0;
        double[] pixelScale = null, tiepoint = null, geoKeys = null;

        for (int i = 0; i < entryCount; i++)
        {
            int at = i * entrySize;
            int tag = U16(entries, at);
            switch (tag)
            {
                case 256: Width = (int)Values(entries, at)[0]; break;
                case 257: Height = (int)Values(entries, at)[0]; break;
                case 258: BitsPerSample = (int)Values(entries, at)[0]; break;
                case 259: Compression = (int)Values(entries, at)[0]; break;
                case 273: stripOffsets = Longs(entries, at); break;
                case 277: SamplesPerPixel = (int)Values(entries, at)[0]; break;
                case 278: rowsPerStrip = (int)Math.Min(int.MaxValue, Values(entries, at)[0]); break;
                case 279: stripCounts = Longs(entries, at); break;
                case 284: _planarConfiguration = (int)Values(entries, at)[0]; break;
                case 317: Predictor = (int)Values(entries, at)[0]; break;
                case 322: tileWidth = (int)Values(entries, at)[0]; break;
                case 323: tileHeight = (int)Values(entries, at)[0]; break;
                case 324: tileOffsets = Longs(entries, at); break;
                case 325: tileCounts = Longs(entries, at); break;
                case 339: SampleFormat = (int)Values(entries, at)[0]; break;
                case 33550: pixelScale = Values(entries, at); break;
                case 33922: tiepoint = Values(entries, at); break;
                case 34735: geoKeys = Values(entries, at); break;
                case 42113:
                    string text = Ascii(entries, at);
                    if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double noData))
                    {
                        NoData = noData;
                    }

                    break;
            }
        }

        if (Width <= 0 || Height <= 0) throw new InvalidDataException("TIFF has no image size.");
        if (SamplesPerPixel > 1 && _planarConfiguration != 1)
        {
            throw new NotSupportedException("Multi-band TIFFs stored band by band are not supported.");
        }

        if (tileOffsets != null)
        {
            IsTiled = true;
            BlockWidth = tileWidth;
            BlockHeight = tileHeight;
            BlocksAcross = (Width + tileWidth - 1) / tileWidth;
            _blockOffsets = tileOffsets;
            _blockByteCounts = tileCounts;
        }
        else if (stripOffsets != null)
        {
            BlockWidth = Width;
            BlockHeight = Math.Min(rowsPerStrip, Height);
            BlocksAcross = 1;
            _blockOffsets = stripOffsets;
            _blockByteCounts = stripCounts;
        }
        else throw new InvalidDataException("TIFF has no image data.");

        if (pixelScale is { Length: >= 2 } && tiepoint is { Length: >= 6 })
        {
            PixelSizeX = pixelScale[0];
            PixelSizeY = pixelScale[1];
            OriginX = tiepoint[3] - tiepoint[0] * PixelSizeX;
            OriginY = tiepoint[4] + tiepoint[1] * PixelSizeY;

            // GeoKey 1025 = 2 means coordinates refer to pixel centres, not corners.
            if (geoKeys != null)
            {
                for (int k = 4; k + 3 < geoKeys.Length; k += 4)
                {
                    if ((int)geoKeys[k] == 1025 && (int)geoKeys[k + 1] == 0 && (int)geoKeys[k + 3] == 2)
                    {
                        OriginX -= PixelSizeX * 0.5;
                        OriginY += PixelSizeY * 0.5;
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Pixels

    /// <summary>Decodes one stored block to floats: BlockWidth x BlockHeight, row 0 at the top.</summary>
    public float[] ReadBlock(int index)
    {
        int rows = BlockHeight;
        if (!IsTiled)
        {
            rows = Math.Min(BlockHeight, Height - index * BlockHeight);
        }

        int bytesPerSample = BitsPerSample / 8;
        int rowBytes = BlockWidth * SamplesPerPixel * bytesPerSample;
        var raw = new byte[rowBytes * rows];
        byte[] stored = ReadAt(_blockOffsets[index], (int)_blockByteCounts[index]);

        switch (Compression)
        {
            case 1:
                Array.Copy(stored, raw, Math.Min(stored.Length, raw.Length));
                break;
            case 5:
                LzwDecode(stored, raw);
                break;
            case 8:
            case 32946:
                using (var zlib = new ZLibStream(new MemoryStream(stored), CompressionMode.Decompress))
                {
                    int total = 0;
                    while (total < raw.Length)
                    {
                        int read = zlib.Read(raw, total, raw.Length - total);
                        if (read == 0) break;
                        total += read;
                    }
                }

                break;
            default:
                throw new NotSupportedException(
                    $"{Path.GetFileName(FilePath)} uses TIFF compression {Compression}, which this tool cannot read. " +
                    "Convert it with: gdal_translate -co COMPRESS=DEFLATE input.tif output.tif");
        }

        var pixels = new float[BlockWidth * BlockHeight];
        bool swap = _bigEndian;

        if (Predictor == 3)
        {
            // Floating-point predictor: bytes are differenced, then stored most significant byte
            // plane first. Undo both; the result is always in this machine's byte order.
            var row = new byte[rowBytes];
            for (int r = 0; r < rows; r++)
            {
                int start = r * rowBytes;
                for (int i = SamplesPerPixel; i < rowBytes; i++) raw[start + i] += raw[start + i - SamplesPerPixel];
                int samplesInRow = BlockWidth * SamplesPerPixel;
                for (int s = 0; s < samplesInRow; s++)
                {
                    for (int b = 0; b < bytesPerSample; b++)
                    {
                        row[s * bytesPerSample + (bytesPerSample - 1 - b)] = raw[start + b * samplesInRow + s];
                    }
                }

                Buffer.BlockCopy(row, 0, raw, start, rowBytes);
            }

            swap = false;
        }

        for (int r = 0; r < rows; r++)
        {
            int rowStart = r * rowBytes;
            long previousInt = 0;
            for (int c = 0; c < BlockWidth; c++)
            {
                int at = rowStart + c * SamplesPerPixel * bytesPerSample;
                float value;
                if (SampleFormat == 3)
                {
                    value = bytesPerSample == 8
                        ? (float)BitConverter.Int64BitsToDouble((long)Raw64(raw, at, swap))
                        : BitConverter.Int32BitsToSingle((int)Raw32(raw, at, swap));
                }
                else
                {
                    long integer = bytesPerSample switch
                    {
                        1 => SampleFormat == 2 ? (sbyte)raw[at] : raw[at],
                        2 => SampleFormat == 2 ? (short)Raw16(raw, at, swap) : Raw16(raw, at, swap),
                        4 => SampleFormat == 2 ? (int)Raw32(raw, at, swap) : Raw32(raw, at, swap),
                        _ => throw new NotSupportedException($"{BitsPerSample}-bit integer samples are not supported."),
                    };

                    if (Predictor == 2)
                    {
                        // Horizontal differencing: each sample is stored minus the one before it,
                        // wrapping at the sample's own width.
                        integer += previousInt;
                        integer = bytesPerSample switch
                        {
                            1 => SampleFormat == 2 ? (sbyte)integer : (byte)integer,
                            2 => SampleFormat == 2 ? (short)integer : (ushort)integer,
                            _ => SampleFormat == 2 ? (int)integer : (uint)integer,
                        };
                        previousInt = integer;
                    }

                    value = integer;
                }

                pixels[r * BlockWidth + c] = value;
            }
        }

        return pixels;
    }

    /// <summary>
    /// Copies a rectangle of pixels into destination (width x height, row 0 at the top). The
    /// rectangle must lie inside the image.
    /// </summary>
    public void ReadWindow(int x0, int y0, int width, int height, float[] destination, BlockCache cache)
    {
        int firstBlockRow = y0 / BlockHeight;
        int lastBlockRow = (y0 + height - 1) / BlockHeight;
        int firstBlockColumn = x0 / BlockWidth;
        int lastBlockColumn = (x0 + width - 1) / BlockWidth;

        for (int blockRow = firstBlockRow; blockRow <= lastBlockRow; blockRow++)
        {
            for (int blockColumn = firstBlockColumn; blockColumn <= lastBlockColumn; blockColumn++)
            {
                float[] block = cache.Get(this, blockRow * BlocksAcross + blockColumn);
                int blockX = blockColumn * BlockWidth;
                int blockY = blockRow * BlockHeight;
                int copyX0 = Math.Max(x0, blockX);
                int copyX1 = Math.Min(x0 + width, Math.Min(blockX + BlockWidth, Width));
                int copyY0 = Math.Max(y0, blockY);
                int copyY1 = Math.Min(y0 + height, Math.Min(blockY + BlockHeight, Height));
                for (int y = copyY0; y < copyY1; y++)
                {
                    Array.Copy(
                        block, (y - blockY) * BlockWidth + (copyX0 - blockX),
                        destination, (y - y0) * width + (copyX0 - x0),
                        copyX1 - copyX0);
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Writing

    /// <summary>
    /// Writes a single-band 32-bit float GeoTIFF in plain latitude/longitude, uncompressed, which
    /// every GIS and most image editors can open. Row 0 of pixels is the north edge.
    /// </summary>
    public static void WriteFloatGeoTiff(
        string path, int width, int height, float[] pixels, double west, double north, double pixelSizeDeg)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        long dataBytes = (long)width * height * 4;
        if (dataBytes > uint.MaxValue - 4096) throw new NotSupportedException("Image too large for a classic TIFF.");

        // Header, then pixel data, then the directory and the values that do not fit inside it.
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        uint directoryOffset = (uint)(8 + dataBytes);
        writer.Write(directoryOffset);

        var bytes = new byte[width * 4];
        for (int row = 0; row < height; row++)
        {
            Buffer.BlockCopy(pixels, row * width * 4, bytes, 0, bytes.Length);
            writer.Write(bytes);
        }

        // WGS 84 geographic, pixel-is-area.
        ushort[] geoKeys = { 1, 1, 0, 3, 1024, 0, 1, 2, 1025, 0, 1, 1, 2048, 0, 1, 4326 };
        const int entryCount = 13;
        uint extra = directoryOffset + 2 + entryCount * 12 + 4;
        uint scaleOffset = extra;
        uint tiepointOffset = scaleOffset + 3 * 8;
        uint geoKeyOffset = tiepointOffset + 6 * 8;

        writer.Write((ushort)entryCount);
        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            writer.Write(tag);
            writer.Write(type);
            writer.Write(count);
            writer.Write(value);
        }

        Entry(256, 4, 1, (uint)width);
        Entry(257, 4, 1, (uint)height);
        Entry(258, 3, 1, 32);
        Entry(259, 3, 1, 1);
        Entry(262, 3, 1, 1);
        Entry(273, 4, 1, 8);
        Entry(277, 3, 1, 1);
        Entry(278, 4, 1, (uint)height);
        Entry(279, 4, 1, (uint)dataBytes);
        Entry(339, 3, 1, 3);
        Entry(33550, 12, 3, scaleOffset);
        Entry(33922, 12, 6, tiepointOffset);
        Entry(34735, 3, (uint)geoKeys.Length, geoKeyOffset);
        writer.Write(0u);

        writer.Write(pixelSizeDeg);
        writer.Write(pixelSizeDeg);
        writer.Write(0.0);
        writer.Write(0.0);
        writer.Write(0.0);
        writer.Write(0.0);
        writer.Write(west);
        writer.Write(north);
        writer.Write(0.0);
        foreach (ushort key in geoKeys) writer.Write(key);
    }

    // ---------------------------------------------------------------------------------------------
    // Low-level helpers

    private byte[] ReadAt(long offset, int count)
    {
        var buffer = new byte[count];
        lock (_streamLock)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.ReadExactly(buffer);
        }

        return buffer;
    }

    private ushort U16(byte[] b, int at) =>
        _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));

    private uint U32(byte[] b, int at) =>
        _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    private ulong U64(byte[] b, int at) =>
        _bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));

    private static ushort Raw16(byte[] b, int at, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));

    private static uint Raw32(byte[] b, int at, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    private static ulong Raw64(byte[] b, int at, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));

    private static int TypeSize(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 1,
    };

    /// <summary>Raw bytes of a directory entry's values, wherever they are stored.</summary>
    private byte[] EntryBytes(byte[] entries, int at, out int type, out long count)
    {
        type = U16(entries, at + 2);
        count = _bigTiff ? (long)U64(entries, at + 4) : U32(entries, at + 4);
        int valueAt = at + (_bigTiff ? 12 : 8);
        int inlineSize = _bigTiff ? 8 : 4;
        long size = count * TypeSize(type);
        if (size <= inlineSize)
        {
            var inline = new byte[inlineSize];
            Array.Copy(entries, valueAt, inline, 0, inlineSize);
            return inline;
        }

        long offset = _bigTiff ? (long)U64(entries, valueAt) : U32(entries, valueAt);
        return ReadAt(offset, (int)size);
    }

    private double[] Values(byte[] entries, int at)
    {
        byte[] data = EntryBytes(entries, at, out int type, out long count);
        var values = new double[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = type switch
            {
                1 or 7 => data[i],
                6 => (sbyte)data[i],
                3 => U16(data, i * 2),
                8 => (short)U16(data, i * 2),
                4 or 13 => U32(data, i * 4),
                9 => (int)U32(data, i * 4),
                11 => BitConverter.Int32BitsToSingle((int)U32(data, i * 4)),
                12 => BitConverter.Int64BitsToDouble((long)U64(data, i * 8)),
                16 or 18 => U64(data, i * 8),
                17 => (long)U64(data, i * 8),
                _ => 0.0,
            };
        }

        return values;
    }

    private long[] Longs(byte[] entries, int at)
    {
        double[] values = Values(entries, at);
        var longs = new long[values.Length];
        for (int i = 0; i < values.Length; i++) longs[i] = (long)values[i];
        return longs;
    }

    private string Ascii(byte[] entries, int at)
    {
        byte[] data = EntryBytes(entries, at, out _, out long count);
        return Encoding.ASCII.GetString(data, 0, (int)Math.Min(count, data.Length)).TrimEnd('\0', ' ');
    }

    /// <summary>TIFF-flavoured LZW: codes packed most significant bit first, widening one code early.</summary>
    private static void LzwDecode(byte[] source, byte[] destination)
    {
        const int clearCode = 256, endCode = 257;
        var prefix = new int[4096];
        var suffix = new byte[4096];
        var length = new int[4096];
        for (int i = 0; i < 256; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            length[i] = 1;
        }

        int nextCode = 258, codeBits = 9, previous = -1;
        int bitBuffer = 0, bitsHeld = 0, sourceAt = 0, written = 0;

        while (written < destination.Length)
        {
            while (bitsHeld < codeBits)
            {
                if (sourceAt >= source.Length) return;
                bitBuffer = (bitBuffer << 8) | source[sourceAt++];
                bitsHeld += 8;
            }

            int code = (bitBuffer >> (bitsHeld - codeBits)) & ((1 << codeBits) - 1);
            bitsHeld -= codeBits;

            if (code == endCode) return;
            if (code == clearCode)
            {
                nextCode = 258;
                codeBits = 9;
                previous = -1;
                continue;
            }

            int entry;
            if (previous == -1)
            {
                entry = code;
            }
            else if (nextCode >= 4096)
            {
                // Table full: a well-formed file clears before this, but never write past the end.
                entry = Math.Min(code, 4095);
            }
            else if (code < nextCode)
            {
                entry = code;
                int first = code;
                while (prefix[first] != -1) first = prefix[first];
                prefix[nextCode] = previous;
                suffix[nextCode] = suffix[first];
                length[nextCode] = length[previous] + 1;
                nextCode++;
            }
            else
            {
                int first = previous;
                while (prefix[first] != -1) first = prefix[first];
                prefix[nextCode] = previous;
                suffix[nextCode] = suffix[first];
                length[nextCode] = length[previous] + 1;
                entry = nextCode;
                nextCode++;
            }

            int entryLength = length[entry];
            int writeAt = written + entryLength - 1;
            for (int walk = entry; walk != -1; walk = prefix[walk], writeAt--)
            {
                if (writeAt < destination.Length) destination[writeAt] = suffix[walk];
            }

            written += entryLength;
            previous = entry;
            if (nextCode >= (1 << codeBits) - 1 && codeBits < 12) codeBits++;
        }
    }
}

/// <summary>
/// Keeps recently decoded TIFF blocks in memory so neighbouring terrain tiles do not decode the
/// same block again. Safe to use from several threads.
/// </summary>
public sealed class BlockCache
{
    private readonly long _budgetBytes;
    private readonly Dictionary<(TiffFile, int), LinkedListNode<Entry>> _lookup = new();
    private readonly LinkedList<Entry> _order = new();
    private long _heldBytes;

    private sealed record Entry((TiffFile, int) Key, float[] Pixels);

    public BlockCache(long budgetBytes) => _budgetBytes = budgetBytes;

    public float[] Get(TiffFile file, int blockIndex)
    {
        var key = (file, blockIndex);
        lock (_lookup)
        {
            if (_lookup.TryGetValue(key, out LinkedListNode<Entry> node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Pixels;
            }
        }

        // Decode outside the lock. Two threads may decode the same block at once; that only
        // wastes a little work.
        float[] pixels = file.ReadBlock(blockIndex);

        lock (_lookup)
        {
            if (_lookup.TryGetValue(key, out LinkedListNode<Entry> existing)) return existing.Value.Pixels;
            _lookup[key] = _order.AddFirst(new Entry(key, pixels));
            _heldBytes += pixels.Length * 4L;
            while (_heldBytes > _budgetBytes && _order.Count > 1)
            {
                LinkedListNode<Entry> oldest = _order.Last;
                _order.RemoveLast();
                _lookup.Remove(oldest.Value.Key);
                _heldBytes -= oldest.Value.Pixels.Length * 4L;
            }
        }

        return pixels;
    }
}
