using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

public class LocalTerrainServer : MonoBehaviour
{
    private sealed class CacheItem
    {
        public byte[] Data;
        public LinkedListNode<string> Node;
    }

    private sealed class ServerState
    {
        public string Name;
        public int Port;
        public string ArchivePath;
        public FileStream ArchiveStream;
        public ZipArchive Archive;
        public Dictionary<string, ZipArchiveEntry> EntryIndex;
        public HttpListener Listener;
        public Thread Thread;
        public Dictionary<string, CacheItem> TileCache;
        public LinkedList<string> TileCacheLru;
        public int TileCacheLimit;
        public int MaxTerrainZoom = -1;
        public bool LoggedOutOfRangeTerrainWarning;
        public int MissingRequestLogCount;
    }

    private readonly List<ServerState> _servers = new List<ServerState>(2);
    private volatile bool _running;

    public int port = 8080;
    public string archiveName = "heightmap.zip"; // primary archive in StreamingAssets
    [SerializeField] private bool loadSecondaryArchive = true;
    [SerializeField] private int secondaryPort = 8081;
    [SerializeField] private string secondaryArchiveName = "terrain.zip";
    [SerializeField, Min(0)] private int maxCachedTilesPerServer = 512;
    [SerializeField] private int maxMissingRequestLogs = 20;
    [SerializeField] private bool logMissingRequests = false;

    void Start()
    {
        StopServer();

        try
        {
            int startedCount = 0;

            if (!TryCreateServer("primary", port, archiveName, required: true, out ServerState primaryServer))
            {
                StopServer();
                enabled = false;
                return;
            }
            if (primaryServer != null)
            {
                _servers.Add(primaryServer);
                startedCount++;
            }

            if (loadSecondaryArchive)
            {
                if (!TryCreateServer("secondary", secondaryPort, secondaryArchiveName, required: false, out ServerState secondaryServer))
                {
                    StopServer();
                    enabled = false;
                    return;
                }
                if (secondaryServer != null)
                {
                    _servers.Add(secondaryServer);
                    startedCount++;
                }
            }

            if (startedCount == 0)
            {
                Debug.LogError("LocalTerrainServer: no servers were started.");
                enabled = false;
                return;
            }

            _running = true;
            foreach (var server in _servers)
            {
                var localServer = server;
                localServer.Thread = new Thread(() => ListenLoop(localServer));
                localServer.Thread.IsBackground = true;
                localServer.Thread.Name = $"LocalTerrainServer-{localServer.Name}";
                localServer.Thread.Start();

                Debug.Log(
                    $"LocalTerrainServer: {localServer.Name} running at http://localhost:{localServer.Port}/ " +
                    $"using '{Path.GetFileName(localServer.ArchivePath)}'.");
            }

            Debug.Log($"LocalTerrainServer: started {startedCount} endpoint(s).");
        }
        catch (Exception e)
        {
            Debug.LogError($"LocalTerrainServer: startup failed: {e.Message}");
            StopServer();
            enabled = false;
        }
    }

    private bool TryCreateServer(string name, int serverPort, string archiveFileName, bool required, out ServerState server)
    {
        server = null;

        if (string.IsNullOrWhiteSpace(archiveFileName))
        {
            if (required)
            {
                Debug.LogError($"LocalTerrainServer: {name} archive name is empty.");
                return false;
            }

            return true;
        }

        if (serverPort < 1 || serverPort > 65535)
        {
            if (required)
            {
                Debug.LogError($"LocalTerrainServer: {name} port {serverPort} is invalid.");
                return false;
            }

            Debug.LogWarning($"LocalTerrainServer: skipping {name} server because port {serverPort} is invalid.");
            return true;
        }

        for (int i = 0; i < _servers.Count; i++)
        {
            if (_servers[i].Port == serverPort)
            {
                if (required)
                {
                    Debug.LogError($"LocalTerrainServer: duplicate port {serverPort} for required {name} server.");
                    return false;
                }

                Debug.LogWarning($"LocalTerrainServer: skipping {name} server because port {serverPort} is already in use by this component.");
                return true;
            }
        }

        string archivePath = Path.Combine(Application.streamingAssetsPath, archiveFileName);
        FileStream archiveStream;
        ZipArchive archive;
        Dictionary<string, ZipArchiveEntry> entryIndex;
        long totalBytes;
        int fileCount;
        int maxTerrainZoom;

        try
        {
            if (!TryOpenArchive(
                archivePath,
                out archiveStream,
                out archive,
                out entryIndex,
                out fileCount,
                out totalBytes,
                out maxTerrainZoom))
            {
                if (required)
                {
                    Debug.LogError($"LocalTerrainServer: required archive not found at '{archivePath}'.");
                    return false;
                }

                Debug.LogWarning($"LocalTerrainServer: optional archive not found at '{archivePath}'.");
                return true;
            }
        }
        catch (Exception e)
        {
            if (required)
            {
                Debug.LogError($"LocalTerrainServer: failed loading required archive '{archivePath}': {e.Message}");
                return false;
            }

            Debug.LogWarning($"LocalTerrainServer: optional archive '{archivePath}' failed to load: {e.Message}");
            return true;
        }

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{serverPort}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException e)
        {
            try
            {
                listener.Close();
            }
            catch
            {
            }

            try
            {
                archive.Dispose();
            }
            catch
            {
            }

            archive = null;
            archiveStream = null;
            entryIndex = null;

            if (required)
            {
                Debug.LogError($"LocalTerrainServer: failed to bind required endpoint http://localhost:{serverPort}/ (error {e.ErrorCode}): {e.Message}");
                return false;
            }

            Debug.LogWarning($"LocalTerrainServer: skipping optional endpoint http://localhost:{serverPort}/ (error {e.ErrorCode}): {e.Message}");
            return true;
        }

        server = new ServerState
        {
            Name = name,
            Port = serverPort,
            ArchivePath = archivePath,
            ArchiveStream = archiveStream,
            Archive = archive,
            EntryIndex = entryIndex,
            Listener = listener,
            TileCache = new Dictionary<string, CacheItem>(Math.Max(0, Math.Min(maxCachedTilesPerServer, 1024))),
            TileCacheLru = new LinkedList<string>(),
            TileCacheLimit = Math.Max(0, maxCachedTilesPerServer),
            MaxTerrainZoom = maxTerrainZoom,
            LoggedOutOfRangeTerrainWarning = false,
            MissingRequestLogCount = 0
        };

        Debug.Log(
            $"LocalTerrainServer: {name} indexed {fileCount} files ({totalBytes:N0} bytes uncompressed) from '{archivePath}'. " +
            $"RAM tile cache limit: {server.TileCacheLimit}.");
        if (maxTerrainZoom >= 0)
        {
            Debug.Log($"LocalTerrainServer: {name} detected max terrain zoom {maxTerrainZoom}.");
        }

        return true;
    }

    private static bool TryOpenArchive(
        string archivePath,
        out FileStream archiveStream,
        out ZipArchive archive,
        out Dictionary<string, ZipArchiveEntry> entryIndex,
        out int fileCount,
        out long totalBytes,
        out int maxTerrainZoom)
    {
        archiveStream = null;
        archive = null;
        entryIndex = null;
        fileCount = 0;
        totalBytes = 0;
        maxTerrainZoom = -1;

        if (!File.Exists(archivePath))
        {
            return false;
        }

        archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            archiveStream.Dispose();
            archiveStream = null;
            throw;
        }

        try
        {
            entryIndex = new Dictionary<string, ZipArchiveEntry>(archive.Entries.Count);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories

                string key = NormalizeKey(entry.FullName);
                if (string.IsNullOrEmpty(key)) continue;

                entryIndex[key] = entry;
                fileCount++;

                if (entry.Length > 0)
                {
                    totalBytes += entry.Length;
                }

                if (key.EndsWith(".terrain", StringComparison.OrdinalIgnoreCase) &&
                    TryGetZoomFromKey(key, out int zoom) &&
                    zoom > maxTerrainZoom)
                {
                    maxTerrainZoom = zoom;
                }
            }

            return true;
        }
        catch
        {
            try
            {
                archive.Dispose();
            }
            catch
            {
            }

            archive = null;
            archiveStream = null;
            entryIndex = null;
            fileCount = 0;
            totalBytes = 0;
            maxTerrainZoom = -1;
            throw;
        }
    }

    private void ListenLoop(ServerState server)
    {
        while (_running)
        {
            try
            {
                if (server.Listener == null || !server.Listener.IsListening) break;

                var context = server.Listener.GetContext();
                HandleRequest(server, context);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException e)
            {
                if (_running)
                    Debug.LogError($"LocalTerrainServer: {server.Name} listener error: {e.Message}");
                break;
            }
            catch (Exception e)
            {
                if (_running)
                    Debug.LogError($"LocalTerrainServer: {server.Name} server loop error: {e.Message}");
            }
        }
    }

    private void HandleRequest(ServerState server, HttpListenerContext context)
    {
        var response = context.Response;

        try
        {
            AddCorsHeaders(response);

            if (context.Request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                return;
            }

            if (context.Request.HttpMethod != "GET" && context.Request.HttpMethod != "HEAD")
            {
                response.StatusCode = 405;
                return;
            }

            if (!TryGetRequestKey(context.Request, out string key))
            {
                response.StatusCode = 400;
                return;
            }

            if (TryGetCachedTile(server, key, out string resolvedKey, out byte[] data))
            {
                if (resolvedKey.EndsWith(".terrain", StringComparison.OrdinalIgnoreCase))
                {
                    response.ContentType = "application/vnd.quantized-mesh";
                    if (data.Length >= 2 && data[0] == 0x1f && data[1] == 0x8b)
                        response.Headers.Add("Content-Encoding", "gzip");
                }
                else
                {
                    response.ContentType = GetContentTypeFromKey(resolvedKey);
                }

                response.ContentLength64 = data.Length;
                if (context.Request.HttpMethod != "HEAD")
                {
                    response.OutputStream.Write(data, 0, data.Length);
                }
            }
            else
            {
                response.StatusCode = 404;
                LogMissingRequest(server, key);
            }
        }
        catch (Exception e)
        {
            response.StatusCode = 500;
            Debug.LogError($"LocalTerrainServer: {server.Name} request error: {e.Message}");
        }
        finally
        {
            CloseResponseSafe(response);
        }
    }

    private static string GetContentTypeFromKey(string key)
    {
        if (key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return "application/json";
        if (key.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (key.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (key.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return "application/xml";
        if (key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return "text/plain";
        return "application/octet-stream";
    }

    private void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept");
        response.Headers.Add("Access-Control-Expose-Headers", "Content-Encoding");
    }

    private static string NormalizeKey(string rawKey)
    {
        if (string.IsNullOrEmpty(rawKey)) return string.Empty;

        var sb = new StringBuilder(rawKey.Length);
        bool previousWasSlash = false;

        for (int i = 0; i < rawKey.Length; i++)
        {
            char c = rawKey[i];
            if (c == '\\') c = '/';

            if (c == '/')
            {
                if (sb.Length == 0 || previousWasSlash) continue;
                previousWasSlash = true;
                sb.Append(c);
            }
            else
            {
                previousWasSlash = false;
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private bool TryGetRequestKey(HttpListenerRequest request, out string key)
    {
        key = string.Empty;
        if (request == null || request.Url == null) return false;

        string localPath = request.Url.LocalPath;
        if (string.IsNullOrWhiteSpace(localPath)) return false;

        string normalized = NormalizeKey(Uri.UnescapeDataString(localPath));
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (normalized.Contains("..")) return false;
        if (Path.IsPathRooted(normalized)) return false;

        for (int i = 0; i < normalized.Length; i++)
        {
            if (char.IsControl(normalized[i]))
            {
                return false;
            }
        }

        key = normalized;
        return true;
    }

    private void LogMissingRequest(ServerState server, string key)
    {
        if (!logMissingRequests) return;
        if (maxMissingRequestLogs < 0) return;

        if (IsOutOfRangeTerrainRequest(server, key))
        {
            if (!server.LoggedOutOfRangeTerrainWarning)
            {
                Debug.LogWarning(
                    $"LocalTerrainServer: {server.Name} received terrain requests above archive max zoom {server.MaxTerrainZoom} (example '{key}'). " +
                    "These 404s are expected unless your archive metadata includes higher zoom tiles.");
                server.LoggedOutOfRangeTerrainWarning = true;
            }
            return;
        }

        if (server.MissingRequestLogCount < maxMissingRequestLogs)
        {
            Debug.LogWarning($"LocalTerrainServer: {server.Name} 404 for '{key}'");
            server.MissingRequestLogCount++;

            if (server.MissingRequestLogCount == maxMissingRequestLogs)
            {
                Debug.LogWarning($"LocalTerrainServer: suppressing additional 404 logs for {server.Name}.");
            }
        }
    }

    private static bool IsOutOfRangeTerrainRequest(ServerState server, string key)
    {
        if (server.MaxTerrainZoom < 0) return false;
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!key.EndsWith(".terrain", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryGetZoomFromKey(key, out int zoom)) return false;
        return zoom > server.MaxTerrainZoom;
    }

    private static bool TryGetZoomFromKey(string key, out int zoom)
    {
        zoom = -1;
        if (string.IsNullOrWhiteSpace(key)) return false;

        int slash = key.IndexOf('/');
        if (slash <= 0) return false;

        int parsedZoom = 0;
        for (int i = 0; i < slash; i++)
        {
            char c = key[i];
            if (c < '0' || c > '9') return false;
            parsedZoom = (parsedZoom * 10) + (c - '0');
        }

        zoom = parsedZoom;
        return true;
    }

    private static bool TryGetCachedTile(ServerState server, string key, out string resolvedKey, out byte[] data)
    {
        resolvedKey = key;

        if (TryGetFromRamCache(server, key, out data))
        {
            return true;
        }

        if (TryReadTileFromArchive(server, key, out data))
        {
            AddToRamCache(server, key, data);
            return true;
        }

        string extension = Path.GetExtension(key);
        if (!string.IsNullOrEmpty(extension))
        {
            // TMS convention only for raster paths: exact z/x/y lookup.
            return false;
        }

        string terrainKey = key + ".terrain";
        if (TryGetFromRamCache(server, terrainKey, out data))
        {
            resolvedKey = terrainKey;
            return true;
        }

        if (TryReadTileFromArchive(server, terrainKey, out data))
        {
            AddToRamCache(server, terrainKey, data);
            resolvedKey = terrainKey;
            return true;
        }

        return false;
    }

    private static bool TryReadTileFromArchive(ServerState server, string key, out byte[] data)
    {
        data = null;

        var entryIndex = server.EntryIndex;
        if (entryIndex == null || !entryIndex.TryGetValue(key, out ZipArchiveEntry entry))
        {
            return false;
        }

        data = ReadEntryData(entry);
        return true;
    }

    private static bool TryGetFromRamCache(ServerState server, string key, out byte[] data)
    {
        data = null;
        var tileCache = server.TileCache;
        if (tileCache == null || !tileCache.TryGetValue(key, out CacheItem item))
        {
            return false;
        }

        data = item.Data;
        MoveCacheItemToHead(server.TileCacheLru, item.Node);
        return true;
    }

    private static void AddToRamCache(ServerState server, string key, byte[] data)
    {
        if (server.TileCacheLimit <= 0 || data == null)
        {
            return;
        }

        var tileCache = server.TileCache;
        var lru = server.TileCacheLru;
        if (tileCache == null || lru == null)
        {
            return;
        }

        if (tileCache.TryGetValue(key, out CacheItem existing))
        {
            existing.Data = data;
            MoveCacheItemToHead(lru, existing.Node);
            return;
        }

        var node = lru.AddFirst(key);
        tileCache[key] = new CacheItem
        {
            Data = data,
            Node = node
        };

        while (tileCache.Count > server.TileCacheLimit)
        {
            var tail = lru.Last;
            if (tail == null) break;

            lru.RemoveLast();
            tileCache.Remove(tail.Value);
        }
    }

    private static void MoveCacheItemToHead(LinkedList<string> lru, LinkedListNode<string> node)
    {
        if (lru == null || node == null || node.List != lru || node == lru.First)
        {
            return;
        }

        lru.Remove(node);
        lru.AddFirst(node);
    }

    private static byte[] ReadEntryData(ZipArchiveEntry entry)
    {
        if (entry.Length > int.MaxValue)
        {
            throw new InvalidDataException($"Entry '{entry.FullName}' is too large to load into memory.");
        }

        int expectedLength = (int)entry.Length;
        byte[] data = new byte[expectedLength];

        using (var stream = entry.Open())
        {
            int totalRead = 0;
            while (totalRead < expectedLength)
            {
                int read = stream.Read(data, totalRead, expectedLength - totalRead);
                if (read <= 0) break;
                totalRead += read;
            }

            if (totalRead == expectedLength)
            {
                return data;
            }

            if (totalRead == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] resized = new byte[totalRead];
            Buffer.BlockCopy(data, 0, resized, 0, totalRead);
            return resized;
        }
    }

    private static void CloseResponseSafe(HttpListenerResponse response)
    {
        if (response == null) return;

        try
        {
            response.Close();
        }
        catch
        {
            // Ignore close errors during shutdown/race conditions.
        }
    }

    private void StopServer()
    {
        _running = false;

        for (int i = 0; i < _servers.Count; i++)
        {
            var server = _servers[i];
            if (server.Listener == null) continue;

            try
            {
                if (server.Listener.IsListening)
                {
                    server.Listener.Stop();
                }
            }
            catch
            {
            }

            try
            {
                server.Listener.Close();
            }
            catch
            {
            }
        }

        for (int i = 0; i < _servers.Count; i++)
        {
            var server = _servers[i];
            if (server.Thread == null) continue;

            try
            {
                if (server.Thread.IsAlive && Thread.CurrentThread != server.Thread)
                {
                    if (!server.Thread.Join(500))
                    {
                        Debug.LogWarning($"LocalTerrainServer: {server.Name} thread did not stop within 500ms.");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"LocalTerrainServer: {server.Name} thread join error: {e.Message}");
            }
        }

        for (int i = 0; i < _servers.Count; i++)
        {
            var server = _servers[i];

            try
            {
                server.Archive?.Dispose();
            }
            catch
            {
            }

            server.Archive = null;
            server.ArchiveStream = null;
            server.EntryIndex = null;
            server.TileCache = null;
            server.TileCacheLru = null;
        }

        _servers.Clear();
    }

    void OnDisable()
    {
        StopServer();
    }

    void OnDestroy()
    {
        StopServer();
    }
}
