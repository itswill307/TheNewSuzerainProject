using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
public class LocalPatchController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] MapControllerEqr mapController;
    [SerializeField] Renderer localPatchRenderer;
    [SerializeField] Material localPatchMaterial;
    [SerializeField] TextAsset etopoManifestCsv;

    [Header("Patch Tracking")]
    [SerializeField] bool sphereModeOnly = true;
    [SerializeField] bool maskGlobalUnderPatch = true;
    [SerializeField] bool trackScreenAnchor = true;
    [SerializeField] Vector2 viewportAnchor01 = new Vector2(0.5f, 0.5f);
    [SerializeField] float recenterThresholdDeg = 1f;

    [Header("Patch Span")]
    [SerializeField] bool autoSpanFromMesh = true;
    [SerializeField] float manualSpanLonDeg = 8f;
    [SerializeField] float manualSpanLatDeg = 5f;
    [SerializeField] float spanScale = 1f;

    [Header("Height Tiles")]
    [SerializeField] bool composeHeightFromTiles = true;
    [SerializeField] int outputWidth = 1024;
    [SerializeField] int outputHeight = 512;
    [SerializeField] bool bilinearTileSampling = true;
    [SerializeField, Range(0f, 1f)] float missingSampleFallback01 = 0.5f;
    [SerializeField] bool releaseComposedTextureWhenInactive = false;
    [SerializeField] float minSecondsBetweenComposes = 0.15f;

    [Header("Tile Cache")]
    [SerializeField] bool useAddressables = true;
    [SerializeField] bool allowEditorAssetDatabaseFallback = true;
    [SerializeField] int maxCachedTiles = 24;
    [SerializeField] bool releaseUnusedTilesImmediately = false;
    [SerializeField] bool releaseTileCacheWhenInactive = false;

    [Header("Debug")]
    [SerializeField] bool verboseLogs = false;

    const float TileStepDeg = 15f;
    const int LonTileCount = 24;
    const int LatTileCount = 12;

    class TileInfo
    {
        public string key;
        public string assetPath;
        public float lonMin;
        public float lonMax;
        public float latMin;
        public float latMax;
        public bool warnedUnreadable;
        public bool warnedMissing;
        public bool warnedAddressables;
    }

    class TileCacheEntry
    {
        public Texture2D texture;
        public long lastAccessTick;
        public int lastComposePass;
        public bool loadedViaAddressables;
        public bool hasAddressHandle;
        public AsyncOperationHandle<Texture2D> addressHandle;
    }

    readonly Dictionary<int, TileInfo> tilesByCell = new Dictionary<int, TileInfo>(LonTileCount * LatTileCount);
    readonly List<TileInfo> tiles = new List<TileInfo>(LonTileCount * LatTileCount);
    ushort[] composedHeightData;
    Texture2D composedHeightTex;
    bool manifestLoaded;

    bool hasAppliedState;
    float lastCenterLonDeg;
    float lastCenterLatDeg;
    float lastSpanLonDeg;
    float lastSpanLatDeg;
    bool wasPatchActiveLastFrame;
    bool pendingCompose;
    float lastComposeTime = -999f;

    Mesh cachedMesh;
    float cachedMeshWidthUnits;
    float cachedMeshHeightUnits;
    readonly Dictionary<string, TileCacheEntry> tileCache = new Dictionary<string, TileCacheEntry>(64);
    long tileAccessCounter;
    int composePassId;

    void Reset()
    {
        localPatchRenderer = GetComponent<Renderer>();
        if (localPatchRenderer != null)
        {
            localPatchMaterial = localPatchRenderer.sharedMaterial;
        }

        if (mapController == null && Camera.main != null)
        {
            mapController = Camera.main.GetComponent<MapControllerEqr>();
        }
    }

    void Awake()
    {
        ResolveReferences();
        CacheMeshPlanarSizeIfNeeded();
        ExpandLocalBoundsForShaderDisplacement();
        EnsureManifestLoaded();
    }

    void OnEnable()
    {
        ResolveReferences();
        CacheMeshPlanarSizeIfNeeded();
        ExpandLocalBoundsForShaderDisplacement();
        EnsureManifestLoaded();
        wasPatchActiveLastFrame = false;
        RefreshNow(force: true);
    }

    void OnValidate()
    {
        viewportAnchor01.x = Mathf.Clamp01(viewportAnchor01.x);
        viewportAnchor01.y = Mathf.Clamp01(viewportAnchor01.y);
        recenterThresholdDeg = Mathf.Max(1e-4f, recenterThresholdDeg);
        minSecondsBetweenComposes = Mathf.Max(0f, minSecondsBetweenComposes);
        manualSpanLonDeg = Mathf.Clamp(manualSpanLonDeg, 1e-4f, 360f);
        manualSpanLatDeg = Mathf.Clamp(manualSpanLatDeg, 1e-4f, 180f);
        spanScale = Mathf.Max(1e-4f, spanScale);
        outputWidth = Mathf.Clamp(outputWidth, 16, 4096);
        outputHeight = Mathf.Clamp(outputHeight, 16, 4096);
        maxCachedTiles = Mathf.Clamp(maxCachedTiles, 1, 512);
    }

    void LateUpdate()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        RefreshNow(force: false);
    }

    public void ForceRefresh()
    {
        RefreshNow(force: true);
    }

    void RefreshNow(bool force)
    {
        if (!ResolveReferences())
        {
            return;
        }

        bool patchActive = !sphereModeOnly || mapController.SphereMode;
        SetPatchActive(patchActive);
        if (!patchActive)
        {
            wasPatchActiveLastFrame = false;
            pendingCompose = false;
            return;
        }

        if (!wasPatchActiveLastFrame)
        {
            force = true;
        }
        wasPatchActiveLastFrame = true;

        SyncSharedMaterialState();

        if (!TryGetPatchCenter(out float centerLatDeg, out float centerLonDeg))
        {
            centerLatDeg = mapController.CameraLatitudeDeg;
            centerLonDeg = mapController.FocusLongitudeDeg;
        }

        ComputePatchSpan(centerLatDeg, out float spanLonDeg, out float spanLatDeg);
        centerLonDeg = WrapLongitude180(centerLonDeg);
        centerLatDeg = Mathf.Clamp(centerLatDeg, -90f, 90f);

        bool centerChanged = !hasAppliedState || AngularDeltaDeg(centerLonDeg, centerLatDeg, lastCenterLonDeg, lastCenterLatDeg) >= recenterThresholdDeg;
        bool spanChanged = !hasAppliedState || Mathf.Abs(spanLonDeg - lastSpanLonDeg) > 1e-4f || Mathf.Abs(spanLatDeg - lastSpanLatDeg) > 1e-4f;
        bool needsRefresh = force || centerChanged || spanChanged || pendingCompose;
        if (!needsRefresh)
        {
            return;
        }

        ApplyPatchGeoProperties(centerLonDeg, centerLatDeg, spanLonDeg, spanLatDeg);
        SyncGlobalPatchMask(centerLonDeg, centerLatDeg, spanLonDeg, spanLatDeg, true);

        bool composedThisUpdate = false;
        if (composeHeightFromTiles && EnsureManifestLoaded())
        {
            bool allowCompose = force || !Application.isPlaying || (Time.unscaledTime - lastComposeTime >= minSecondsBetweenComposes);
            if (allowCompose)
            {
                ComposeAndAssignHeightTexture(centerLonDeg, centerLatDeg, spanLonDeg, spanLatDeg);
                lastComposeTime = Time.unscaledTime;
                pendingCompose = false;
                composedThisUpdate = true;
            }
            else
            {
                pendingCompose = true;
            }
        }
        else
        {
            pendingCompose = false;
        }

        lastCenterLonDeg = centerLonDeg;
        lastCenterLatDeg = centerLatDeg;
        lastSpanLonDeg = spanLonDeg;
        lastSpanLatDeg = spanLatDeg;
        hasAppliedState = true;

        if (verboseLogs && composedThisUpdate)
        {
            Debug.Log($"Local patch updated. Center=({centerLatDeg:F3}, {centerLonDeg:F3}) Span=({spanLatDeg:F3}, {spanLonDeg:F3})");
        }
    }

    bool ResolveReferences()
    {
        if (mapController == null)
        {
            mapController = FindFirstObjectByType<MapControllerEqr>();
        }

        if (localPatchRenderer == null)
        {
            localPatchRenderer = GetComponent<Renderer>();
        }

        if (localPatchMaterial == null && localPatchRenderer != null)
        {
            localPatchMaterial = localPatchRenderer.material;
        }

        return mapController != null && localPatchRenderer != null && localPatchMaterial != null;
    }

    void SyncSharedMaterialState()
    {
        Material source = mapController.MapMaterial;
        if (source != null)
        {
            CopyTextureIfExists(source, localPatchMaterial, "_MainTex");
            CopyTextureIfExists(source, localPatchMaterial, "_ProvinceIDTex");
            CopyFloatIfExists(source, localPatchMaterial, "_Radius");
            CopyFloatIfExists(source, localPatchMaterial, "_Morph");
            CopyFloatIfExists(source, localPatchMaterial, "_Sphere");
            CopyFloatIfExists(source, localPatchMaterial, "_KmPerUnit");
            CopyFloatIfExists(source, localPatchMaterial, "_HeightMinKm");
            CopyFloatIfExists(source, localPatchMaterial, "_HeightMaxKm");
            if (localPatchMaterial.HasProperty("_HeightExaggeration")) localPatchMaterial.SetFloat("_HeightExaggeration", mapController.HeightExaggeration);
        }
        else
        {
            if (localPatchMaterial.HasProperty("_Radius")) localPatchMaterial.SetFloat("_Radius", mapController.RadiusUnits);
            if (localPatchMaterial.HasProperty("_Morph")) localPatchMaterial.SetFloat("_Morph", mapController.CurrentMorph);
            if (localPatchMaterial.HasProperty("_Sphere")) localPatchMaterial.SetFloat("_Sphere", mapController.SphereMode ? 1f : 0f);
            if (localPatchMaterial.HasProperty("_KmPerUnit")) localPatchMaterial.SetFloat("_KmPerUnit", mapController.KmPerUnit);
            if (localPatchMaterial.HasProperty("_HeightMinKm")) localPatchMaterial.SetFloat("_HeightMinKm", mapController.HeightMinKm);
            if (localPatchMaterial.HasProperty("_HeightMaxKm")) localPatchMaterial.SetFloat("_HeightMaxKm", mapController.HeightMaxKm);
            if (localPatchMaterial.HasProperty("_HeightExaggeration")) localPatchMaterial.SetFloat("_HeightExaggeration", mapController.HeightExaggeration);
        }

        if (localPatchMaterial.HasProperty("_UVOffset"))
        {
            localPatchMaterial.SetVector("_UVOffset", mapController.CurrentUvOffset);
        }
    }

    static void CopyFloatIfExists(Material source, Material destination, string prop)
    {
        if (!source || !destination) return;
        if (!source.HasProperty(prop) || !destination.HasProperty(prop)) return;
        destination.SetFloat(prop, source.GetFloat(prop));
    }

    static void CopyTextureIfExists(Material source, Material destination, string prop)
    {
        if (!source || !destination) return;
        if (!source.HasProperty(prop) || !destination.HasProperty(prop)) return;
        destination.SetTexture(prop, source.GetTexture(prop));
    }

    bool TryGetPatchCenter(out float latitudeDeg, out float longitudeDeg)
    {
        latitudeDeg = mapController.CameraLatitudeDeg;
        longitudeDeg = mapController.FocusLongitudeDeg;

        if (!trackScreenAnchor)
        {
            return true;
        }

        Camera cam = mapController.GetComponent<Camera>();
        if (!cam)
        {
            return false;
        }

        Vector2 screenPos = new Vector2(viewportAnchor01.x * Screen.width, viewportAnchor01.y * Screen.height);
        return mapController.TryGetLatLonAtScreen(screenPos, out latitudeDeg, out longitudeDeg);
    }

    void ComputePatchSpan(float centerLatDeg, out float spanLonDeg, out float spanLatDeg)
    {
        if (autoSpanFromMesh && TryGetMeshPlanarSize(out float widthUnits, out float heightUnits))
        {
            float radius = Mathf.Max(1e-6f, mapController.RadiusUnits);
            spanLatDeg = (heightUnits / radius) * Mathf.Rad2Deg;

            if (mapController.SphereMode)
            {
                // In sphere mode, east-west distance scales by cos(latitude).
                float cosLat = Mathf.Abs(Mathf.Cos(centerLatDeg * Mathf.Deg2Rad));
                cosLat = Mathf.Max(0.17364818f, cosLat); // Clamp at cos(80 deg) to avoid runaway spans near poles.
                spanLonDeg = (widthUnits / (radius * cosLat)) * Mathf.Rad2Deg;
            }
            else
            {
                // Match equirectangular X scaling used by the map shader in planar mode.
                spanLonDeg = (widthUnits * Mathf.PI / (2f * radius)) * Mathf.Rad2Deg;
            }
        }
        else
        {
            spanLonDeg = manualSpanLonDeg;
            spanLatDeg = manualSpanLatDeg;
        }

        spanLonDeg = Mathf.Clamp(spanLonDeg * spanScale, 1e-4f, 360f);
        spanLatDeg = Mathf.Clamp(spanLatDeg * spanScale, 1e-4f, 180f);
    }

    bool TryGetMeshPlanarSize(out float widthUnits, out float heightUnits)
    {
        widthUnits = 0f;
        heightUnits = 0f;

        if (localPatchRenderer == null) return false;
        MeshFilter mf = localPatchRenderer.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return false;

        Mesh mesh = mf.sharedMesh;
        if (mesh != cachedMesh)
        {
            CacheMeshPlanarSizeIfNeeded();
        }

        if (cachedMeshWidthUnits <= 0f || cachedMeshHeightUnits <= 0f) return false;

        Vector3 scale = localPatchRenderer.transform.lossyScale;
        widthUnits = cachedMeshWidthUnits * Mathf.Abs(scale.x);
        heightUnits = cachedMeshHeightUnits * Mathf.Abs(scale.y);
        return true;
    }

    void CacheMeshPlanarSizeIfNeeded()
    {
        if (localPatchRenderer == null) return;
        MeshFilter mf = localPatchRenderer.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;

        Mesh mesh = mf.sharedMesh;
        if (mesh == cachedMesh && cachedMeshWidthUnits > 0f && cachedMeshHeightUnits > 0f) return;

        cachedMesh = mesh;
        if (!TryComputeMeshPlanarSize(mesh, out cachedMeshWidthUnits, out cachedMeshHeightUnits))
        {
            cachedMeshWidthUnits = 0f;
            cachedMeshHeightUnits = 0f;
        }
    }

    static bool TryComputeMeshPlanarSize(Mesh mesh, out float width, out float height)
    {
        width = 0f;
        height = 0f;
        if (mesh == null) return false;

        Vector3[] vertices = mesh.vertices;
        if (vertices == null || vertices.Length == 0) return false;

        Vector3 min = vertices[0];
        Vector3 max = vertices[0];
        for (int i = 1; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i];
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        Vector3 size = max - min;
        // Use the two largest axes as planar dimensions.
        float x = Mathf.Abs(size.x);
        float y = Mathf.Abs(size.y);
        float z = Mathf.Abs(size.z);

        if (x <= y && x <= z)
        {
            width = y;
            height = z;
        }
        else if (y <= x && y <= z)
        {
            width = x;
            height = z;
        }
        else
        {
            width = x;
            height = y;
        }

        return width > 0f && height > 0f;
    }

    void ExpandLocalBoundsForShaderDisplacement()
    {
        if (localPatchRenderer == null || mapController == null) return;

        MeshFilter mf = localPatchRenderer.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;

        Mesh mesh = mf.sharedMesh;
        float maxAbsElevationKm = Mathf.Max(Mathf.Abs(mapController.HeightMinKm), Mathf.Abs(mapController.HeightMaxKm));
        float elevationExtentUnits = (maxAbsElevationKm * Mathf.Max(0f, mapController.HeightExaggeration)) / Mathf.Max(1e-6f, mapController.KmPerUnit);
        float radius = Mathf.Max(1e-6f, mapController.RadiusUnits);

        float halfX = Mathf.Max(Mathf.PI * radius, radius + elevationExtentUnits);
        float halfY = Mathf.Max(0.5f * Mathf.PI * radius, radius + elevationExtentUnits);
        float halfZ = radius + elevationExtentUnits;

        mesh.bounds = new Bounds(
            Vector3.zero,
            new Vector3(halfX * 2f, halfY * 2f, halfZ * 2f)
        );
    }

    void ApplyPatchGeoProperties(float centerLonDeg, float centerLatDeg, float spanLonDeg, float spanLatDeg)
    {
        if (localPatchMaterial.HasProperty("_PatchCenterLonDeg")) localPatchMaterial.SetFloat("_PatchCenterLonDeg", centerLonDeg);
        if (localPatchMaterial.HasProperty("_PatchCenterLatDeg")) localPatchMaterial.SetFloat("_PatchCenterLatDeg", centerLatDeg);
        if (localPatchMaterial.HasProperty("_PatchSpanLonDeg")) localPatchMaterial.SetFloat("_PatchSpanLonDeg", spanLonDeg);
        if (localPatchMaterial.HasProperty("_PatchSpanLatDeg")) localPatchMaterial.SetFloat("_PatchSpanLatDeg", spanLatDeg);
    }

    bool EnsureManifestLoaded()
    {
        if (manifestLoaded) return tiles.Count > 0;
        manifestLoaded = true;

        tiles.Clear();
        tilesByCell.Clear();

        if (etopoManifestCsv == null)
        {
            if (composeHeightFromTiles)
            {
                Debug.LogWarning("LocalPatchController: ETOPO manifest CSV is not assigned. Tile compositing is disabled on this component.", this);
            }
            return false;
        }

        string[] lines = etopoManifestCsv.text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= 1)
        {
            Debug.LogWarning("LocalPatchController: ETOPO manifest CSV is empty.", this);
            return false;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            if (!TryParseManifestLine(lines[i], out TileInfo tile))
            {
                continue;
            }

            int cellKey = GetCellKeyFromBounds(tile.lonMin, tile.latMin);
            if (!tilesByCell.ContainsKey(cellKey))
            {
                tilesByCell[cellKey] = tile;
            }

            tiles.Add(tile);
        }

        if (verboseLogs)
        {
            Debug.Log($"LocalPatchController: loaded {tiles.Count} ETOPO tiles from manifest.", this);
        }
        return tiles.Count > 0;
    }

    bool TryParseManifestLine(string line, out TileInfo tile)
    {
        tile = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        List<string> fields = ParseCsvLine(line);
        if (fields.Count < 8) return false;

        if (!TryParseFloat(fields[2], out float lonMin)) return false;
        if (!TryParseFloat(fields[3], out float lonMax)) return false;
        if (!TryParseFloat(fields[4], out float latMin)) return false;
        if (!TryParseFloat(fields[5], out float latMax)) return false;

        tile = new TileInfo
        {
            key = fields[0],
            assetPath = fields[1],
            lonMin = lonMin,
            lonMax = lonMax,
            latMin = latMin,
            latMax = latMax
        };
        return true;
    }

    static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>(8);
        bool inQuotes = false;
        var current = new System.Text.StringBuilder(line.Length);

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Length = 0;
            }
            else
            {
                current.Append(ch);
            }
        }

        values.Add(current.ToString());

        return values;
    }

    static bool TryParseFloat(string s, out float value)
    {
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    void ComposeAndAssignHeightTexture(float centerLonDeg, float centerLatDeg, float spanLonDeg, float spanLatDeg)
    {
        EnsureOutputTexture();
        composePassId++;

        int width = outputWidth;
        int height = outputHeight;
        int index = 0;
        bool usedMissingFallback = false;

        for (int y = 0; y < height; y++)
        {
            float v = (y + 0.5f) / height;
            float lat = centerLatDeg + (v - 0.5f) * spanLatDeg;
            lat = Mathf.Clamp(lat, -89.999f, 89.999f);

            for (int x = 0; x < width; x++, index++)
            {
                float u = (x + 0.5f) / width;
                float lon = WrapLongitude180(centerLonDeg + (u - 0.5f) * spanLonDeg);

                float sample01;
                if (!TrySampleHeight01(lon, lat, out sample01))
                {
                    sample01 = missingSampleFallback01;
                    usedMissingFallback = true;
                }

                composedHeightData[index] = (ushort)Mathf.Clamp(Mathf.RoundToInt(sample01 * 65535f), 0, 65535);
            }
        }

        composedHeightTex.SetPixelData(composedHeightData, 0);
        composedHeightTex.Apply(false, false);
        localPatchMaterial.SetTexture("_HeightTex", composedHeightTex);
        EnforceTileCachePolicy();

        if (verboseLogs && usedMissingFallback)
        {
            Debug.LogWarning("LocalPatchController: some samples used fallback height because source tiles were missing or unreadable.", this);
        }
    }

    void EnsureOutputTexture()
    {
        int targetCount = outputWidth * outputHeight;
        if (composedHeightData == null || composedHeightData.Length != targetCount)
        {
            composedHeightData = new ushort[targetCount];
        }

        bool recreate =
            composedHeightTex == null ||
            composedHeightTex.width != outputWidth ||
            composedHeightTex.height != outputHeight;

        if (recreate)
        {
            if (composedHeightTex != null)
            {
                Destroy(composedHeightTex);
            }

            composedHeightTex = new Texture2D(outputWidth, outputHeight, TextureFormat.R16, false, true)
            {
                name = "LocalPatchHeightRuntime",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
        }
    }

    bool TrySampleHeight01(float lonDeg, float latDeg, out float height01)
    {
        height01 = 0f;
        int cellKey = GetCellKey(lonDeg, latDeg);
        if (!tilesByCell.TryGetValue(cellKey, out TileInfo tile))
        {
            return false;
        }

        Texture2D tex = GetTileTexture(tile);
        if (tex == null)
        {
            if (!tile.warnedMissing)
            {
                tile.warnedMissing = true;
                Debug.LogWarning($"LocalPatchController: tile texture not found at '{tile.assetPath}'.", this);
            }
            return false;
        }

        if (!tex.isReadable)
        {
            if (!tile.warnedUnreadable)
            {
                tile.warnedUnreadable = true;
                Debug.LogWarning($"LocalPatchController: tile '{tile.assetPath}' is not readable. Enable Read/Write in Unity import settings for compositing.", this);
            }
            return false;
        }

        float lonWrapped = WrapLongitude180(lonDeg);
        float u = Mathf.InverseLerp(tile.lonMin, tile.lonMax, lonWrapped);
        float v = Mathf.InverseLerp(tile.latMin, tile.latMax, latDeg);
        u = Mathf.Clamp01(u);
        v = Mathf.Clamp01(v);

        if (bilinearTileSampling)
        {
            height01 = tex.GetPixelBilinear(u, v).r;
            return true;
        }

        int x = Mathf.Clamp(Mathf.RoundToInt(u * Mathf.Max(1, tex.width - 1)), 0, tex.width - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(v * Mathf.Max(1, tex.height - 1)), 0, tex.height - 1);
        height01 = tex.GetPixel(x, y).r;
        return true;
    }

    Texture2D GetTileTexture(TileInfo tile)
    {
        if (tile == null || string.IsNullOrEmpty(tile.key)) return null;
        if (tileCache.TryGetValue(tile.key, out TileCacheEntry cached) && cached.texture != null)
        {
            TouchCacheEntry(cached);
            return cached.texture;
        }

        Texture2D loaded = null;
        bool loadedViaAddressables = false;
        bool hasAddressHandle = false;
        AsyncOperationHandle<Texture2D> handle = default;

        if (useAddressables)
        {
            loaded = TryLoadTileAddressable(tile, out handle, out hasAddressHandle);
            loadedViaAddressables = loaded != null && hasAddressHandle;
        }

        if (loaded == null)
        {
#if UNITY_EDITOR
            if (allowEditorAssetDatabaseFallback)
            {
                loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(tile.assetPath);
            }
#endif
        }

        if (loaded == null)
        {
            string resourcesPath = TryGetResourcesPath(tile.assetPath);
            if (!string.IsNullOrEmpty(resourcesPath))
            {
                loaded = Resources.Load<Texture2D>(resourcesPath);
            }
        }

        if (loaded == null)
        {
            if (hasAddressHandle)
            {
                Addressables.Release(handle);
            }
            return null;
        }

        var entry = new TileCacheEntry
        {
            texture = loaded,
            loadedViaAddressables = loadedViaAddressables,
            hasAddressHandle = hasAddressHandle,
            addressHandle = handle
        };
        TouchCacheEntry(entry);
        tileCache[tile.key] = entry;
        EnforceTileCachePolicy();
        return loaded;
    }

    Texture2D TryLoadTileAddressable(TileInfo tile, out AsyncOperationHandle<Texture2D> handle, out bool hasHandle)
    {
        handle = default;
        hasHandle = false;

        Texture2D texture = TryLoadAddressableInternal(tile.key, out handle, out hasHandle);
        if (texture != null) return texture;

        if (hasHandle)
        {
            Addressables.Release(handle);
            hasHandle = false;
        }

        texture = TryLoadAddressableInternal(tile.assetPath, out handle, out hasHandle);
        if (texture != null) return texture;

        if (!tile.warnedAddressables && verboseLogs)
        {
            tile.warnedAddressables = true;
            Debug.Log($"LocalPatchController: Addressables miss for tile '{tile.key}' and path '{tile.assetPath}', falling back.", this);
        }
        return null;
    }

    static Texture2D TryLoadAddressableInternal(string address, out AsyncOperationHandle<Texture2D> handle, out bool hasHandle)
    {
        handle = default;
        hasHandle = false;
        if (string.IsNullOrEmpty(address)) return null;
        if (!HasAddressableLocation(address)) return null;

        AsyncOperationHandle<Texture2D> op = Addressables.LoadAssetAsync<Texture2D>(address);
        Texture2D result = op.WaitForCompletion();
        if (op.Status == AsyncOperationStatus.Succeeded && result != null)
        {
            handle = op;
            hasHandle = true;
            return result;
        }

        Addressables.Release(op);
        return null;
    }

    static bool HasAddressableLocation(string address)
    {
        foreach (var locator in Addressables.ResourceLocators)
        {
            if (locator != null && locator.Locate(address, typeof(Texture2D), out var locations) && locations != null && locations.Count > 0)
            {
                return true;
            }
        }
        return false;
    }

    static string TryGetResourcesPath(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return null;

        const string marker = "/Resources/";
        int idx = assetPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        string relative = assetPath.Substring(idx + marker.Length);
        int dot = relative.LastIndexOf('.');
        if (dot > 0) relative = relative.Substring(0, dot);
        return relative.Replace('\\', '/');
    }

    void TouchCacheEntry(TileCacheEntry entry)
    {
        tileAccessCounter++;
        entry.lastAccessTick = tileAccessCounter;
        entry.lastComposePass = composePassId;
    }

    void EnforceTileCachePolicy()
    {
        if (tileCache.Count == 0) return;

        if (releaseUnusedTilesImmediately)
        {
            var staleKeys = new List<string>();
            foreach (KeyValuePair<string, TileCacheEntry> kv in tileCache)
            {
                if (kv.Value.lastComposePass != composePassId)
                {
                    staleKeys.Add(kv.Key);
                }
            }

            for (int i = 0; i < staleKeys.Count; i++)
            {
                RemoveTileFromCache(staleKeys[i]);
            }
        }

        while (tileCache.Count > maxCachedTiles)
        {
            string lruKey = null;
            long lruTick = long.MaxValue;
            foreach (KeyValuePair<string, TileCacheEntry> kv in tileCache)
            {
                if (kv.Value.lastAccessTick < lruTick)
                {
                    lruTick = kv.Value.lastAccessTick;
                    lruKey = kv.Key;
                }
            }

            if (string.IsNullOrEmpty(lruKey))
            {
                break;
            }

            RemoveTileFromCache(lruKey);
        }
    }

    void RemoveTileFromCache(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!tileCache.TryGetValue(key, out TileCacheEntry entry)) return;

        if (entry.loadedViaAddressables && entry.hasAddressHandle)
        {
            Addressables.Release(entry.addressHandle);
        }
        else if (entry.texture != null)
        {
            Resources.UnloadAsset(entry.texture);
        }

        tileCache.Remove(key);
    }

    void ClearTileCache()
    {
        if (tileCache.Count == 0) return;
        var keys = new List<string>(tileCache.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            RemoveTileFromCache(keys[i]);
        }
        tileCache.Clear();
    }

    static int GetCellKeyFromBounds(float lonMin, float latMin)
    {
        float lonWrapped = WrapLongitude180(lonMin);
        int lonCell = Mathf.FloorToInt((lonWrapped + 180f) / TileStepDeg);
        lonCell = Mathf.Clamp(lonCell, 0, LonTileCount - 1);

        int latCell = Mathf.FloorToInt((latMin + 90f) / TileStepDeg);
        latCell = Mathf.Clamp(latCell, 0, LatTileCount - 1);

        return latCell * LonTileCount + lonCell;
    }

    static int GetCellKey(float lonDeg, float latDeg)
    {
        float lonWrapped = WrapLongitude180(lonDeg);
        float latClamped = Mathf.Clamp(latDeg, -89.999f, 89.999f);

        int lonCell = Mathf.FloorToInt((lonWrapped + 180f) / TileStepDeg);
        lonCell = Mathf.Clamp(lonCell, 0, LonTileCount - 1);

        int latCell = Mathf.FloorToInt((latClamped + 90f) / TileStepDeg);
        latCell = Mathf.Clamp(latCell, 0, LatTileCount - 1);

        return latCell * LonTileCount + lonCell;
    }

    static float WrapLongitude180(float lonDeg)
    {
        float wrapped = Mathf.Repeat(lonDeg + 180f, 360f) - 180f;
        if (wrapped >= 180f) wrapped = -180f;
        return wrapped;
    }

    static float AngularDeltaDeg(float lonA, float latA, float lonB, float latB)
    {
        float dLon = Mathf.Abs(Mathf.DeltaAngle(lonA, lonB));
        float dLat = Mathf.Abs(latA - latB);
        return Mathf.Max(dLon, dLat);
    }

    void SetPatchActive(bool active)
    {
        if (localPatchRenderer != null)
        {
            localPatchRenderer.enabled = active;
        }

        if (!active)
        {
            SyncGlobalPatchMask(0f, 0f, 0f, 0f, false);
        }

        if (!active && releaseComposedTextureWhenInactive)
        {
            ReleaseComposedHeightTexture();
        }

        if (!active && releaseTileCacheWhenInactive)
        {
            ClearTileCache();
        }
    }

    void ReleaseComposedHeightTexture()
    {
        if (composedHeightTex != null)
        {
            Destroy(composedHeightTex);
            composedHeightTex = null;
        }

        composedHeightData = null;
    }

    void SyncGlobalPatchMask(float centerLonDeg, float centerLatDeg, float spanLonDeg, float spanLatDeg, bool enable)
    {
        Material globalMat = mapController != null ? mapController.MapMaterial : null;
        if (globalMat == null) return;

        bool finalEnable = enable && maskGlobalUnderPatch && mapController.SphereMode;

        if (globalMat.HasProperty("_LocalPatchMaskEnable"))
        {
            globalMat.SetFloat("_LocalPatchMaskEnable", finalEnable ? 1f : 0f);
        }

        if (!finalEnable) return;

        if (globalMat.HasProperty("_LocalPatchCenterLonDeg")) globalMat.SetFloat("_LocalPatchCenterLonDeg", centerLonDeg);
        if (globalMat.HasProperty("_LocalPatchCenterLatDeg")) globalMat.SetFloat("_LocalPatchCenterLatDeg", centerLatDeg);
        if (globalMat.HasProperty("_LocalPatchSpanLonDeg")) globalMat.SetFloat("_LocalPatchSpanLonDeg", spanLonDeg);
        if (globalMat.HasProperty("_LocalPatchSpanLatDeg")) globalMat.SetFloat("_LocalPatchSpanLatDeg", spanLatDeg);
    }

    void OnDestroy()
    {
        SyncGlobalPatchMask(0f, 0f, 0f, 0f, false);
        ReleaseComposedHeightTexture();
        ClearTileCache();
    }
}
