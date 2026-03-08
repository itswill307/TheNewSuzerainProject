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
        public sealed class DataEntry
        {
            public ZipArchiveEntry ZipEntry;
            public string FilePath;
            public long Length;
        }

        public string Name;
        public int Port;
        public string SourcePath;
        public FileStream ArchiveStream;
        public ZipArchive Archive;
        public Dictionary<string, DataEntry> EntryIndex;
        public HttpListener Listener;
        public Thread Thread;
        public Dictionary<string, CacheItem> TileCache;
        public LinkedList<string> TileCacheLru;
        public int TileCacheLimit;
        public int MaxTerrainZoom = -1;
        public int MaxRasterZoom = -1;
        public bool LoggedOutOfRangeTerrainWarning;
        public int MissingRequestLogCount;
    }

    private readonly List<ServerState> _servers = new List<ServerState>(2);
    private volatile bool _running;

    public int port = 8080;
    public string archiveName = "heightmap"; // resolved from project root/MapData for relative paths: folder first, zip second
    [SerializeField] private bool loadSecondaryArchive = true;
    [SerializeField] private int secondaryPort = 8081;
    [SerializeField] private string secondaryArchiveName = "terrain";
    [SerializeField] private bool useDirectoryManifestIndex = true;
    [SerializeField] private string directoryManifestFileName = "manifest.tsv";
    [SerializeField] private bool writeDirectoryManifestWhenMissing = true;
    [SerializeField, Min(0)] private int maxCachedTilesPerServer = 512;
    [SerializeField] private int maxMissingRequestLogs = 20;
    [SerializeField] private bool logMissingRequests = false;
    public bool TryGetSecondaryMaxRasterZoom(out int zoomLevel)
    {
        return TryGetMaxRasterZoom(secondaryPort, out zoomLevel);
    }

    public bool TryGetMaxRasterZoom(int serverPort, out int zoomLevel)
    {
        zoomLevel = -1;

        ServerState server = FindServerByPort(serverPort);
        if (server == null)
        {
            return false;
        }

        zoomLevel = server.MaxRasterZoom;
        return zoomLevel >= 0;
    }

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
                    $"using '{Path.GetFileName(localServer.SourcePath)}'.");
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

        string sourcePath = ResolveSourcePath(archiveFileName);
        if (string.IsNullOrEmpty(sourcePath))
        {
            if (required)
            {
                Debug.LogError(
                    $"LocalTerrainServer: required source '{archiveFileName}' not found. " +
                    "Looked in project root/MapData (for relative paths).");
                return false;
            }

            Debug.LogWarning(
                $"LocalTerrainServer: optional source '{archiveFileName}' not found. " +
                "Looked in project root/MapData (for relative paths).");
            return true;
        }

        FileStream archiveStream;
        ZipArchive archive;
        Dictionary<string, ServerState.DataEntry> entryIndex;
        long totalBytes;
        int fileCount;
        int maxTerrainZoom;
        int maxRasterZoom;
        bool sourceIsArchive;
        bool usedManifestIndex;

        try
        {
            if (!TryOpenSource(
                sourcePath,
                useDirectoryManifestIndex,
                directoryManifestFileName,
                writeDirectoryManifestWhenMissing,
                out archiveStream,
                out archive,
                out entryIndex,
                out fileCount,
                out totalBytes,
                out maxTerrainZoom,
                out maxRasterZoom,
                out sourceIsArchive,
                out usedManifestIndex))
            {
                if (required)
                {
                    Debug.LogError($"LocalTerrainServer: required source not found at '{sourcePath}'.");
                    return false;
                }

                Debug.LogWarning($"LocalTerrainServer: optional source not found at '{sourcePath}'.");
                return true;
            }
        }
        catch (Exception e)
        {
            if (required)
            {
                Debug.LogError($"LocalTerrainServer: failed loading required source '{sourcePath}': {e.Message}");
                return false;
            }

            Debug.LogWarning($"LocalTerrainServer: optional source '{sourcePath}' failed to load: {e.Message}");
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
            SourcePath = sourcePath,
            ArchiveStream = archiveStream,
            Archive = archive,
            EntryIndex = entryIndex,
            Listener = listener,
            TileCache = new Dictionary<string, CacheItem>(Math.Max(0, Math.Min(maxCachedTilesPerServer, 1024))),
            TileCacheLru = new LinkedList<string>(),
            TileCacheLimit = Math.Max(0, maxCachedTilesPerServer),
            MaxTerrainZoom = maxTerrainZoom,
            MaxRasterZoom = maxRasterZoom,
            LoggedOutOfRangeTerrainWarning = false,
            MissingRequestLogCount = 0
        };

        Debug.Log(
            $"LocalTerrainServer: {name} indexed {fileCount} files ({totalBytes:N0} bytes uncompressed) from '{sourcePath}' " +
            $"({GetSourceModeLabel(sourceIsArchive, usedManifestIndex, directoryManifestFileName)}). " +
            $"RAM tile cache limit: {server.TileCacheLimit}.");
        if (maxTerrainZoom >= 0)
        {
            Debug.Log($"LocalTerrainServer: {name} detected max terrain zoom {maxTerrainZoom}.");
        }

        if (maxRasterZoom >= 0)
        {
            Debug.Log($"LocalTerrainServer: {name} detected max raster zoom {maxRasterZoom}.");
        }

        return true;
    }

    private static bool TryOpenSource(
        string sourcePath,
        bool useDirectoryManifestIndex,
        string directoryManifestFileName,
        bool writeDirectoryManifestWhenMissing,
        out FileStream archiveStream,
        out ZipArchive archive,
        out Dictionary<string, ServerState.DataEntry> entryIndex,
        out int fileCount,
        out long totalBytes,
        out int maxTerrainZoom,
        out int maxRasterZoom,
        out bool sourceIsArchive,
        out bool usedManifestIndex)
    {
        archiveStream = null;
        archive = null;
        entryIndex = null;
        fileCount = 0;
        totalBytes = 0;
        maxTerrainZoom = -1;
        maxRasterZoom = -1;
        sourceIsArchive = false;
        usedManifestIndex = false;

        if (Directory.Exists(sourcePath))
        {
            if (useDirectoryManifestIndex && TryLoadDirectoryManifest(
                sourcePath,
                directoryManifestFileName,
                out entryIndex,
                out fileCount,
                out totalBytes,
                out maxTerrainZoom,
                out maxRasterZoom))
            {
                usedManifestIndex = true;
                return true;
            }

            entryIndex = new Dictionary<string, ServerState.DataEntry>(StringComparer.Ordinal);
            foreach (string filePath in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                string key = GetDirectoryEntryKey(sourcePath, filePath);
                if (string.IsNullOrEmpty(key)) continue;

                var fileInfo = new FileInfo(filePath);
                entryIndex[key] = new ServerState.DataEntry
                {
                    FilePath = filePath,
                    ZipEntry = null,
                    Length = fileInfo.Exists ? fileInfo.Length : 0
                };
                fileCount++;

                if (fileInfo.Exists && fileInfo.Length > 0)
                {
                    totalBytes += fileInfo.Length;
                }

                if (key.EndsWith(".terrain", StringComparison.OrdinalIgnoreCase) &&
                    TryGetZoomFromKey(key, out int zoom) &&
                    zoom > maxTerrainZoom)
                {
                    maxTerrainZoom = zoom;
                }

                if (IsRasterImageKey(key) &&
                    TryGetZoomFromKey(key, out int rasterZoom) &&
                    rasterZoom > maxRasterZoom)
                {
                    maxRasterZoom = rasterZoom;
                }
            }

            if (useDirectoryManifestIndex && writeDirectoryManifestWhenMissing)
            {
                TryWriteDirectoryManifest(
                    sourcePath,
                    directoryManifestFileName,
                    entryIndex,
                    maxTerrainZoom,
                    maxRasterZoom);
            }

            return true;
        }

        if (!File.Exists(sourcePath))
        {
            return false;
        }

        sourceIsArchive = true;
        archiveStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
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
            entryIndex = new Dictionary<string, ServerState.DataEntry>(archive.Entries.Count, StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories

                string key = NormalizeKey(entry.FullName);
                if (string.IsNullOrEmpty(key)) continue;

                entryIndex[key] = new ServerState.DataEntry
                {
                    ZipEntry = entry,
                    FilePath = null,
                    Length = entry.Length
                };
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

                if (IsRasterImageKey(key) &&
                    TryGetZoomFromKey(key, out int rasterZoom) &&
                    rasterZoom > maxRasterZoom)
                {
                    maxRasterZoom = rasterZoom;
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
            maxRasterZoom = -1;
            throw;
        }
    }

    private static string GetSourceModeLabel(bool sourceIsArchive, bool usedManifestIndex, string directoryManifestFileName)
    {
        if (sourceIsArchive)
        {
            return "zip archive";
        }

        if (usedManifestIndex)
        {
            return $"directory source via manifest '{directoryManifestFileName}'";
        }

        return "directory source (full scan)";
    }

    private static bool TryLoadDirectoryManifest(
        string sourcePath,
        string manifestFileName,
        out Dictionary<string, ServerState.DataEntry> entryIndex,
        out int fileCount,
        out long totalBytes,
        out int maxTerrainZoom,
        out int maxRasterZoom)
    {
        entryIndex = null;
        fileCount = 0;
        totalBytes = 0;
        maxTerrainZoom = -1;
        maxRasterZoom = -1;

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        string fileName = string.IsNullOrWhiteSpace(manifestFileName) ? "_tile_index.tsv" : manifestFileName.Trim();
        string manifestPath = Path.Combine(sourcePath, fileName);
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        string rootPath = Path.GetFullPath(sourcePath);
        string rootWithSeparator = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var parsedIndex = new Dictionary<string, ServerState.DataEntry>(StringComparer.Ordinal);
        bool hasMaxZoomHeader = false;
        bool hasMaxRasterZoomHeader = false;

        foreach (string rawLine in File.ReadLines(manifestPath))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                const string maxZoomPrefix = "#maxTerrainZoom=";
                if (line.StartsWith(maxZoomPrefix, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.Substring(maxZoomPrefix.Length), out int parsedMaxZoom))
                {
                    maxTerrainZoom = parsedMaxZoom;
                    hasMaxZoomHeader = true;
                }

                const string maxRasterZoomPrefix = "#maxRasterZoom=";
                if (line.StartsWith(maxRasterZoomPrefix, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.Substring(maxRasterZoomPrefix.Length), out int parsedMaxRasterZoom))
                {
                    maxRasterZoom = parsedMaxRasterZoom;
                    hasMaxRasterZoomHeader = true;
                }
                continue;
            }

            int separatorIndex = line.IndexOf('\t');
            string rawKey = separatorIndex >= 0 ? line.Substring(0, separatorIndex) : line;
            string rawLength = separatorIndex >= 0 ? line.Substring(separatorIndex + 1) : string.Empty;

            string key = NormalizeKey(rawKey);
            if (string.IsNullOrWhiteSpace(key) || key.Contains("..") || Path.IsPathRooted(key))
            {
                continue;
            }

            long length = 0;
            if (!string.IsNullOrWhiteSpace(rawLength))
            {
                long.TryParse(rawLength, out length);
                if (length < 0) length = 0;
            }

            string candidatePath = Path.Combine(rootPath, key.Replace('/', Path.DirectorySeparatorChar));
            string fullPath = Path.GetFullPath(candidatePath);
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parsedIndex.TryGetValue(key, out ServerState.DataEntry existing))
            {
                if (existing.Length > 0) totalBytes -= existing.Length;
            }
            else
            {
                fileCount++;
            }

            parsedIndex[key] = new ServerState.DataEntry
            {
                FilePath = fullPath,
                ZipEntry = null,
                Length = length
            };

            if (length > 0)
            {
                totalBytes += length;
            }

            if (!hasMaxZoomHeader &&
                key.EndsWith(".terrain", StringComparison.OrdinalIgnoreCase) &&
                TryGetZoomFromKey(key, out int zoom) &&
                zoom > maxTerrainZoom)
            {
                maxTerrainZoom = zoom;
            }

            if (!hasMaxRasterZoomHeader &&
                IsRasterImageKey(key) &&
                TryGetZoomFromKey(key, out int rasterZoom) &&
                rasterZoom > maxRasterZoom)
            {
                maxRasterZoom = rasterZoom;
            }
        }

        if (parsedIndex.Count == 0)
        {
            return false;
        }

        entryIndex = parsedIndex;
        return true;
    }

    private static void TryWriteDirectoryManifest(
        string sourcePath,
        string manifestFileName,
        Dictionary<string, ServerState.DataEntry> entryIndex,
        int maxTerrainZoom,
        int maxRasterZoom)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || entryIndex == null)
        {
            return;
        }

        string fileName = string.IsNullOrWhiteSpace(manifestFileName) ? "_tile_index.tsv" : manifestFileName.Trim();
        string manifestPath = Path.Combine(sourcePath, fileName);
        if (File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            using (var writer = new StreamWriter(manifestPath, append: false, Encoding.UTF8))
            {
                writer.WriteLine("# LocalTerrainServer manifest v1");
                writer.WriteLine($"#maxTerrainZoom={maxTerrainZoom}");
                writer.WriteLine($"#maxRasterZoom={maxRasterZoom}");

                foreach (var pair in entryIndex)
                {
                    string key = pair.Key;
                    long length = pair.Value != null ? Math.Max(0, pair.Value.Length) : 0;
                    writer.Write(key);
                    writer.Write('\t');
                    writer.WriteLine(length);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"LocalTerrainServer: failed to write directory manifest in '{sourcePath}': {e.Message}");
        }
    }

    private static string ResolveSourcePath(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return null;
        }

        string trimmed = sourceName.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return ResolveSourcePathCandidates(trimmed);
        }

        string projectRoot = GetProjectRootPath();
        string mapDataRoot = ResolveMapDataRoot(projectRoot);
        string mapDataCandidate = ResolveSourcePathCandidates(Path.Combine(mapDataRoot, trimmed));
        if (!string.IsNullOrEmpty(mapDataCandidate))
        {
            return mapDataCandidate;
        }

        return null;
    }

    private static string ResolveSourcePathCandidates(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(candidatePath);
        string extension = Path.GetExtension(fullPath);

        // If a zip path is configured, prefer an extracted sibling folder first.
        if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            string folderPath = fullPath.Substring(0, fullPath.Length - 4);
            if (Directory.Exists(folderPath))
            {
                return folderPath;
            }

            if (File.Exists(fullPath))
            {
                return fullPath;
            }

            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }

            return null;
        }

        if (Directory.Exists(fullPath))
        {
            return fullPath;
        }

        // If no extension was provided (e.g. "heightmap"), try "<name>.zip" second.
        if (string.IsNullOrEmpty(extension))
        {
            string zipPath = fullPath + ".zip";
            if (File.Exists(zipPath))
            {
                return zipPath;
            }
        }

        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        return null;
    }

    private static string ResolveMapDataRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "MapData");
        }

        string exactPath = Path.Combine(projectRoot, "MapData");
        if (Directory.Exists(exactPath))
        {
            return exactPath;
        }

        try
        {
            foreach (string directoryPath in Directory.EnumerateDirectories(projectRoot))
            {
                string directoryName = Path.GetFileName(directoryPath);
                if (string.Equals(directoryName, "MapData", StringComparison.OrdinalIgnoreCase))
                {
                    return directoryPath;
                }
            }
        }
        catch
        {
            // Fall back to the conventional MapData path if enumeration fails.
        }

        return exactPath;
    }

    private static string GetProjectRootPath()
    {
        string dataPath = Application.dataPath;
        if (string.IsNullOrEmpty(dataPath))
        {
            return Directory.GetCurrentDirectory();
        }

        DirectoryInfo parent = Directory.GetParent(dataPath);
        return parent != null ? parent.FullName : dataPath;
    }

    private static string GetDirectoryEntryKey(string rootDirectoryPath, string filePath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectoryPath) || string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        string root = rootDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullFile = Path.GetFullPath(filePath);
        if (!fullFile.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string relative = fullFile.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return NormalizeKey(relative);
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

    private ServerState FindServerByPort(int serverPort)
    {
        for (int i = 0; i < _servers.Count; i++)
        {
            if (_servers[i] != null && _servers[i].Port == serverPort)
            {
                return _servers[i];
            }
        }

        return null;
    }

    private static bool IsRasterImageKey(string key)
    {
        return key.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
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
        if (entryIndex == null || !entryIndex.TryGetValue(key, out ServerState.DataEntry entry))
        {
            return false;
        }

        if (entry.ZipEntry != null)
        {
            data = ReadEntryData(entry.ZipEntry);
            return true;
        }

        if (string.IsNullOrEmpty(entry.FilePath) || !File.Exists(entry.FilePath))
        {
            return false;
        }

        data = File.ReadAllBytes(entry.FilePath);
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
