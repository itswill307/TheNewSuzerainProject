using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Tns.Core.Terrain;

namespace Tns.Map.Terrain;

/// <summary>
/// Loads one kind of terrain tile (heights, colour or water) from disk in the background and
/// keeps it on the graphics card. All loaded tiles of a kind share one layered texture, one
/// layer per tile, so every tile can be drawn with the same material. When the layers run out,
/// the tile unused for longest gives its up. Layer 0 is never loaded into: it stays blank, for
/// tiles with nothing loaded yet. The coarsest tiles are loaded at the start and never given up
/// (<see cref="LoadPinned"/>), so every place always has at least a coarse picture to show while
/// its own tile loads.
/// </summary>
public sealed class TerrainTileCache
{
    private const int MaxLoadsInFlight = 8;

    private sealed class Entry
    {
        public int Layer = -1;
        public long LastUsedFrame;
        public bool Pinned;
    }

    private readonly TerrainPackSet _packs;
    private readonly Image.Format _format;
    private readonly int _size;
    private readonly Func<byte[], byte[]> _decode;
    private readonly Dictionary<TileId, Entry> _entries = new();
    private readonly HashSet<TileId> _loading = new();
    private readonly Stack<int> _freeLayers = new();
    private readonly ConcurrentQueue<(TileId Id, byte[] Pixels)> _decoded = new();
    private int _inFlight;

    // Timing, for tests: ticks spent reading and decoding files on worker threads, and from asking
    // for a tile to it reaching the graphics card.
    private readonly Dictionary<TileId, long> _askedAt = new();
    private long _readTicks, _decodeTicks, _queueTicks;
    public double QueueMs => Volatile.Read(ref _queueTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public int InFlight => Volatile.Read(ref _inFlight);
    public int WaitingForUpload => _decoded.Count;
    private int _loads;
    public int Loads => Volatile.Read(ref _loads);
    public double ReadMs => Volatile.Read(ref _readTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public double DecodeMs => Volatile.Read(ref _decodeTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public double WaitMs { get; private set; }
    public double LongestWaitMs { get; private set; }
    public void ResetTimes() { _loads = 0; _readTicks = 0; _decodeTicks = 0; _queueTicks = 0; WaitMs = 0; LongestWaitMs = 0; }

    public Texture2DArray Texture { get; }
    public int Layers { get; }
    public int BytesPerLayer { get; }
    public TerrainPackSet Packs => _packs;

    /// <summary>True while tiles are still being read or waiting to go to the graphics card.</summary>
    public bool Busy => Volatile.Read(ref _inFlight) > 0 || !_decoded.IsEmpty;

    public int LoadedTiles => _entries.Count;

    /// <param name="decode">Turns a tile file into the texture's pixel bytes. Runs on a worker thread.</param>
    /// <param name="blank">Pixel bytes of the blank layer.</param>
    /// <param name="size">Samples a side of each layer: 257, or 129 for tiles kept at half resolution.</param>
    public TerrainTileCache(TerrainPackSet packs, int layers, Image.Format format, Func<byte[], byte[]> decode, byte[] blank, int size = HeightTile.Samples)
    {
        _packs = packs;
        _format = format;
        _decode = decode;
        Layers = layers;
        BytesPerLayer = blank.Length;
        _size = size;
        Image empty = Image.CreateFromData(size, size, false, format, blank);
        var images = new Godot.Collections.Array<Image>();
        for (int i = 0; i < layers; i++) images.Add(empty);
        Texture = new Texture2DArray();
        Texture.CreateFromImages(images);
        for (int layer = layers - 1; layer >= 1; layer--) _freeLayers.Push(layer);
    }

    /// <summary>The texture layer holding a tile, if it is loaded. Counts as a use.</summary>
    public bool TryGetLayer(TileId id, long frame, out int layer)
    {
        if (_entries.TryGetValue(id, out Entry entry))
        {
            entry.LastUsedFrame = frame;
            layer = entry.Layer;
            return true;
        }

        layer = 0;
        return false;
    }

    /// <summary>Starts loading a tile if it is not loaded or loading already. False if the queue is full.</summary>
    public bool Request(TileId id)
    {
        if (_entries.ContainsKey(id) || _loading.Contains(id)) return true;
        if (_inFlight >= MaxLoadsInFlight) return false;
        string path = _packs.PathOf(id);
        if (path == null) return true;

        _loading.Add(id);
        long asked0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _askedAt[id] = asked0;
        Interlocked.Increment(ref _inFlight);
        Task.Run(() =>
        {
            byte[] pixels = null;
            try
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Interlocked.Add(ref _queueTicks, t0 - asked0);
                byte[] file = System.IO.File.ReadAllBytes(path);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                pixels = _decode(file);
                Interlocked.Add(ref _readTicks, t1 - t0);
                Interlocked.Add(ref _decodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t1);
                Interlocked.Increment(ref _loads);
                if (pixels.Length != BytesPerLayer) throw new System.IO.InvalidDataException("tile has the wrong size");
            }
            catch (Exception e)
            {
                GD.PushWarning($"Terrain tile {path} could not be read: {e.Message}");
                pixels = null;
            }

            _decoded.Enqueue((id, pixels));
        });
        return true;
    }

    /// <summary>Moves finished loads onto the graphics card. Call once a frame from the main thread.</summary>
    public void Upload(long frame, int maxUploads)
    {
        for (int i = 0; i < maxUploads && _decoded.TryPeek(out var item); i++)
        {
            int layer = -1;
            if (item.Pixels != null)
            {
                layer = TakeLayer(frame);
                // Every layer is in use this frame: leave the tile queued and try again later.
                if (layer < 0) return;
            }

            _decoded.TryDequeue(out _);
            _loading.Remove(item.Id);
            if (_askedAt.Remove(item.Id, out long asked))
            {
                double wait = (System.Diagnostics.Stopwatch.GetTimestamp() - asked) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                WaitMs += wait;
                if (wait > LongestWaitMs) LongestWaitMs = wait;
            }
            Interlocked.Decrement(ref _inFlight);
            if (item.Pixels == null) continue;

            Image image = Image.CreateFromData(_size, _size, false, _format, item.Pixels);
            Texture.UpdateLayer(image, layer);
            _entries[item.Id] = new Entry { Layer = layer, LastUsedFrame = frame };
        }
    }

    private int TakeLayer(long frame)
    {
        if (_freeLayers.Count > 0) return _freeLayers.Pop();

        TileId oldest = default;
        long oldestFrame = long.MaxValue;
        foreach (var (id, entry) in _entries)
        {
            if (!entry.Pinned && entry.LastUsedFrame < oldestFrame)
            {
                oldestFrame = entry.LastUsedFrame;
                oldest = id;
            }
        }

        // Never take a layer from a tile used this frame or the last.
        if (oldestFrame >= frame - 1) return -1;
        int layer = _entries[oldest].Layer;
        _entries.Remove(oldest);
        return layer;
    }

    /// <summary>
    /// Loads every tile the packs have down to <paramref name="maxLevel"/> straight away, and
    /// keeps them for good. Called once at the start; the files are read on several threads.
    /// Returns how many tiles it loaded.
    /// </summary>
    public int LoadPinned(int maxLevel)
    {
        var ids = new List<TileId>();
        for (int level = 0; level <= maxLevel; level++)
        {
            for (int y = 0; y < TileId.RowsAt(level); y++)
            {
                for (int x = 0; x < TileId.ColumnsAt(level); x++)
                {
                    var id = new TileId(level, x, y);
                    if (_packs.PathOf(id) != null && !_entries.ContainsKey(id)) ids.Add(id);
                }
            }
        }

        // Keep at least half the layers free for the tiles the view asks for.
        if (ids.Count > _freeLayers.Count / 2) ids.RemoveRange(_freeLayers.Count / 2, ids.Count - _freeLayers.Count / 2);
        var pixels = new byte[ids.Count][];
        Parallel.For(0, ids.Count, i =>
        {
            try
            {
                pixels[i] = _decode(System.IO.File.ReadAllBytes(_packs.PathOf(ids[i])));
                if (pixels[i].Length != BytesPerLayer) pixels[i] = null;
            }
            catch (Exception e)
            {
                GD.PushWarning($"Terrain tile {_packs.PathOf(ids[i])} could not be read: {e.Message}");
            }
        });

        int loaded = 0;
        for (int i = 0; i < ids.Count; i++)
        {
            if (pixels[i] == null) continue;
            int layer = _freeLayers.Pop();
            Texture.UpdateLayer(Image.CreateFromData(_size, _size, false, _format, pixels[i]), layer);
            _entries[ids[i]] = new Entry { Layer = layer, Pinned = true };
            loaded++;
        }

        return loaded;
    }
}
