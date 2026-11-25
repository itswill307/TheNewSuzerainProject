using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Tns.Tools.Terrain;

/// <summary>
/// Reads an 8-bit PNG (grey, grey + alpha, RGB or RGBA, not interlaced) into RGB bytes, so the
/// tool can use a colour picture of the world without any imaging library.
/// </summary>
public static class PngImage
{
    public static byte[] ReadRgb(string path, out int width, out int height)
    {
        using var file = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[8];
        file.ReadExactly(signature);
        if (!signature.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            throw new InvalidDataException($"{path} is not a PNG file.");
        }

        width = height = 0;
        int channels = 0;
        var compressed = new List<(long Offset, int Length)>();
        Span<byte> chunkHeader = stackalloc byte[8];
        while (true)
        {
            file.ReadExactly(chunkHeader);
            int length = BinaryPrimitives.ReadInt32BigEndian(chunkHeader);
            string type = System.Text.Encoding.ASCII.GetString(chunkHeader[4..]);
            if (type == "IHDR")
            {
                var header = new byte[length];
                file.ReadExactly(header);
                width = BinaryPrimitives.ReadInt32BigEndian(header);
                height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
                if (header[8] != 8) throw new NotSupportedException("Only 8-bit PNGs can be read.");
                if (header[12] != 0) throw new NotSupportedException("Interlaced PNGs cannot be read.");
                channels = header[9] switch
                {
                    0 => 1,
                    2 => 3,
                    4 => 2,
                    6 => 4,
                    _ => throw new NotSupportedException("Palette PNGs cannot be read; save it as RGB."),
                };
                file.Seek(4, SeekOrigin.Current);
            }
            else if (type == "IDAT")
            {
                compressed.Add((file.Position, length));
                file.Seek(length + 4, SeekOrigin.Current);
            }
            else if (type == "IEND")
            {
                break;
            }
            else
            {
                file.Seek(length + 4, SeekOrigin.Current);
            }
        }

        var rgb = new byte[(long)width * height * 3];
        int stride = width * channels;
        var previous = new byte[stride];
        var current = new byte[stride];
        using var zlib = new ZLibStream(new ChunkStream(file, compressed), CompressionMode.Decompress);
        for (int row = 0; row < height; row++)
        {
            int filter = zlib.ReadByte();
            zlib.ReadExactly(current);
            Unfilter(filter, current, previous, channels);
            long at = (long)row * width * 3;
            for (int x = 0; x < width; x++)
            {
                int source = x * channels;
                if (channels >= 3)
                {
                    rgb[at + x * 3] = current[source];
                    rgb[at + x * 3 + 1] = current[source + 1];
                    rgb[at + x * 3 + 2] = current[source + 2];
                }
                else
                {
                    rgb[at + x * 3] = rgb[at + x * 3 + 1] = rgb[at + x * 3 + 2] = current[source];
                }
            }

            (previous, current) = (current, previous);
        }

        return rgb;
    }

    private static void Unfilter(int filter, byte[] row, byte[] above, int bytesPerPixel)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (int i = bytesPerPixel; i < row.Length; i++) row[i] += row[i - bytesPerPixel];
                break;
            case 2:
                for (int i = 0; i < row.Length; i++) row[i] += above[i];
                break;
            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                    row[i] += (byte)((left + above[i]) >> 1);
                }

                break;
            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int a = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                    int b = above[i];
                    int c = i >= bytesPerPixel ? above[i - bytesPerPixel] : 0;
                    int p = a + b - c;
                    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }

                break;
            default:
                throw new InvalidDataException($"Unknown PNG row filter {filter}.");
        }
    }

    /// <summary>The IDAT chunks of a PNG read back to back, as one stream.</summary>
    private sealed class ChunkStream : Stream
    {
        private readonly Stream _file;
        private readonly List<(long Offset, int Length)> _chunks;
        private int _chunk;
        private int _used;

        public ChunkStream(Stream file, List<(long Offset, int Length)> chunks)
        {
            _file = file;
            _chunks = chunks;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_chunk < _chunks.Count && _used == _chunks[_chunk].Length)
            {
                _chunk++;
                _used = 0;
            }

            if (_chunk >= _chunks.Count) return 0;
            var (start, length) = _chunks[_chunk];
            _file.Position = start + _used;
            int read = _file.Read(buffer, offset, Math.Min(count, length - _used));
            _used += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
