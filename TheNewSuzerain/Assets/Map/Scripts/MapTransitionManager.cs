using CesiumForUnity;
using UnityEngine;

[DisallowMultipleComponent]
public class MapCesiumTransitionManager : MonoBehaviour
{
    const double WorldLongitudeDegrees = 360.0;
    const double WorldLatitudeDegrees = 180.0;

    public enum ViewMode
    {
        FlatMap,
        Cesium
    }

    [Header("References")]
    [SerializeField] MapControllerEqr mapController;
    [SerializeField] CesiumMapController cesiumController;
    [SerializeField] Cesium3DTileset cesiumTileset;
    [SerializeField] CesiumTileMapServiceRasterOverlay cesiumRasterOverlay;
    [SerializeField] LocalTerrainServer localTerrainServer;
    [SerializeField] Camera mapCamera;
    [SerializeField] Camera cesiumCamera;

    [Header("Optional Mode Toggles")]
    [SerializeField] Behaviour[] mapModeBehaviours;
    [SerializeField] Behaviour[] cesiumModeBehaviours;
    [SerializeField] GameObject[] mapModeObjects;
    [SerializeField] GameObject[] cesiumModeObjects;

    [Header("Switch Thresholds")]
    [SerializeField, Range(0.01f, 1f), Tooltip("Globe-fill fallback for switching to Cesium if the runtime resolution comparison cannot be evaluated.")]
    float switchToCesiumFitFraction = 0.95f;
    [SerializeField] float minSecondsBetweenSwitches = 0.4f;
    [SerializeField, Range(0f, 0.25f), Tooltip("Additional resolution advantage Cesium must have before switching away from the flat map. Switching back to the flat map still happens as soon as it would be higher resolution.")]
    float switchToCesiumResolutionHysteresis = 0.05f;

    [Header("Cesium Preload")]
    [SerializeField, Tooltip("Keep Cesium rendering offscreen while the flat map is visible so tiles stay warm.")]
    bool keepCesiumWarmInBackground = true;
    [SerializeField, Tooltip("How long Cesium stays offscreen after crossing the switch threshold before it becomes visible.")]
    float cesiumWarmupSeconds = 0.2f;
    [SerializeField, Tooltip("Require Cesium's current view to reach this load progress before making the switch visible.")]
    bool waitForCesiumLoadProgress = true;
    [SerializeField, Range(0f, 100f)] float minimumCesiumLoadProgress = 90f;
    [SerializeField, Min(1), Tooltip("Number of raster overlay tiles across at level 0. Global geodetic TMS overlays are typically 2.")]
    int cesiumRasterRootTilesX = 2;
    [SerializeField, Min(1), Tooltip("Number of raster overlay tiles down at level 0. Global geodetic TMS overlays are typically 1.")]
    int cesiumRasterRootTilesY = 1;
    [SerializeField, Min(1), Tooltip("Raster overlay tile width in pixels.")]
    int cesiumRasterTileWidth = 256;
    [SerializeField, Min(1), Tooltip("Raster overlay tile height in pixels.")]
    int cesiumRasterTileHeight = 256;
    [SerializeField, Tooltip("Maximum offscreen render texture size used while Cesium is only being kept warm in the background. During the actual switch warmup, Cesium uses its visible presentation size.")]
    int hiddenCesiumRenderTextureSize = 256;

    [Header("Startup")]
    [SerializeField] ViewMode startingMode = ViewMode.FlatMap;
    [SerializeField] bool manageMainCameraTag = true;

    ViewMode activeMode;
    float nextSwitchTime;
    bool hasInitializedMode;
    string mapCameraOriginalTag;
    string cesiumCameraOriginalTag;
    bool isPreparingCesiumSwitch;
    float cesiumSwitchReadyTime;
    Rect cesiumCameraOriginalRect;
    RenderTexture cesiumCameraOriginalTargetTexture;
    bool hasCachedCesiumCameraPresentation;
    RenderTexture hiddenCesiumRenderTexture;

    public ViewMode ActiveMode => activeMode;

    void Awake()
    {
        ResolveReferences();
        CacheOriginalTags();
    }

    void Start()
    {
        ResolveReferences();
        SetMode(startingMode);
    }

    void OnValidate()
    {
        switchToCesiumFitFraction = Mathf.Clamp(switchToCesiumFitFraction, 0.01f, 1f);
        minSecondsBetweenSwitches = Mathf.Max(0f, minSecondsBetweenSwitches);
        switchToCesiumResolutionHysteresis = Mathf.Clamp(switchToCesiumResolutionHysteresis, 0f, 0.25f);
        cesiumWarmupSeconds = Mathf.Max(0f, cesiumWarmupSeconds);
        minimumCesiumLoadProgress = Mathf.Clamp(minimumCesiumLoadProgress, 0f, 100f);
        cesiumRasterRootTilesX = Mathf.Max(1, cesiumRasterRootTilesX);
        cesiumRasterRootTilesY = Mathf.Max(1, cesiumRasterRootTilesY);
        cesiumRasterTileWidth = Mathf.Max(1, cesiumRasterTileWidth);
        cesiumRasterTileHeight = Mathf.Max(1, cesiumRasterTileHeight);
        hiddenCesiumRenderTextureSize = Mathf.Max(16, hiddenCesiumRenderTextureSize);
    }

    void Update()
    {
        if (!Application.isPlaying || !hasInitializedMode)
        {
            return;
        }

        if (activeMode == ViewMode.FlatMap && keepCesiumWarmInBackground)
        {
            SyncHiddenCesiumView();
        }

        switch (activeMode)
        {
            case ViewMode.FlatMap:
                if (isPreparingCesiumSwitch)
                {
                    UpdatePendingCesiumSwitch();
                }
                else if (Time.unscaledTime >= nextSwitchTime)
                {
                    TrySwitchToCesium();
                }
                break;
            case ViewMode.Cesium:
                if (Time.unscaledTime >= nextSwitchTime)
                {
                    TrySwitchToMap();
                }
                break;
        }
    }

    void OnDestroy()
    {
        ReleaseHiddenCesiumRenderTexture();
    }

    public void SwitchToCesiumNow()
    {
        ResolveReferences();
        if (mapController == null || cesiumController == null)
        {
            return;
        }

        MapCesiumTransitionViewState state = mapController.CaptureTransitionViewState();
        if (!state.isValid)
        {
            return;
        }

        isPreparingCesiumSwitch = false;
        CompleteCesiumSwitch(state);
    }

    public void SwitchToMapNow()
    {
        ResolveReferences();
        if (mapController == null || cesiumController == null)
        {
            return;
        }

        MapCesiumTransitionViewState state = cesiumController.CaptureTransitionViewState();
        if (!state.isValid)
        {
            return;
        }

        isPreparingCesiumSwitch = false;
        mapController.ApplyTransitionViewState(state);
        SetMode(ViewMode.FlatMap);
        nextSwitchTime = Time.unscaledTime + minSecondsBetweenSwitches;
    }

    void TrySwitchToCesium()
    {
        if (mapController == null || cesiumController == null)
        {
            return;
        }

        MapCesiumTransitionViewState state = mapController.CaptureTransitionViewState();
        if (!ShouldSwitchToCesium(state))
        {
            return;
        }

        BeginCesiumSwitch(state);
    }

    void TrySwitchToMap()
    {
        if (mapController == null || cesiumController == null)
        {
            return;
        }

        MapCesiumTransitionViewState state = cesiumController.CaptureTransitionViewState();
        if (!ShouldSwitchToMap(state))
        {
            return;
        }

        mapController.ApplyTransitionViewState(state);
        SetMode(ViewMode.FlatMap);
        nextSwitchTime = Time.unscaledTime + minSecondsBetweenSwitches;
    }

    void ResolveReferences()
    {
        if (mapController == null)
        {
            mapController = FindFirstObjectByType<MapControllerEqr>(FindObjectsInactive.Include);
        }

        if (cesiumController == null)
        {
            cesiumController = FindFirstObjectByType<CesiumMapController>(FindObjectsInactive.Include);
        }

        if (cesiumTileset == null)
        {
            cesiumTileset = FindFirstObjectByType<Cesium3DTileset>(FindObjectsInactive.Include);
        }

        if (cesiumRasterOverlay == null)
        {
            cesiumRasterOverlay = cesiumTileset != null
                ? cesiumTileset.GetComponent<CesiumTileMapServiceRasterOverlay>()
                : FindFirstObjectByType<CesiumTileMapServiceRasterOverlay>(FindObjectsInactive.Include);
        }

        if (localTerrainServer == null)
        {
            localTerrainServer = FindFirstObjectByType<LocalTerrainServer>(FindObjectsInactive.Include);
        }

        if (mapCamera == null && mapController != null)
        {
            mapCamera = mapController.ControlledCamera;
        }

        if (cesiumCamera == null && cesiumController != null)
        {
            cesiumCamera = cesiumController.ControlledCamera;
        }
    }

    bool ShouldSwitchToMap(MapCesiumTransitionViewState state)
    {
        if (!state.isValid)
        {
            return false;
        }

        if (TryGetResolutionThresholdReached(state, switchToMap: true, out bool resolutionBoundaryReached) &&
            resolutionBoundaryReached)
        {
            return true;
        }

        if (TryGetGlobeFillBoundaryReached(state, switchToMap: true, out bool fillBoundaryReached))
        {
            return fillBoundaryReached;
        }

        return false;
    }

    bool ShouldSwitchToCesium(MapCesiumTransitionViewState state)
    {
        if (!state.isValid)
        {
            return false;
        }

        if (TryGetResolutionThresholdReached(state, switchToMap: false, out bool resolutionBoundaryReached))
        {
            return resolutionBoundaryReached;
        }

        if (TryGetGlobeFillBoundaryReached(state, switchToMap: false, out bool fillBoundaryReached))
        {
            return fillBoundaryReached;
        }

        return false;
    }

    void BeginCesiumSwitch(MapCesiumTransitionViewState state)
    {
        if (!state.isValid)
        {
            return;
        }

        isPreparingCesiumSwitch = true;
        cesiumSwitchReadyTime = Time.unscaledTime + cesiumWarmupSeconds;
        cesiumController.ApplyTransitionViewState(state);
        ApplyCesiumPresentation(visibleOnScreen: false);

        if (cesiumWarmupSeconds <= 0f)
        {
            CompleteCesiumSwitch(state);
        }
    }

    void UpdatePendingCesiumSwitch()
    {
        if (mapController == null || cesiumController == null)
        {
            CancelPendingCesiumSwitch();
            return;
        }

        MapCesiumTransitionViewState state = mapController.CaptureTransitionViewState();
        if (!ShouldSwitchToCesium(state))
        {
            CancelPendingCesiumSwitch();
            return;
        }

        cesiumController.ApplyTransitionViewState(state);
        if (Time.unscaledTime >= cesiumSwitchReadyTime && IsCesiumReadyForVisibleSwitch())
        {
            CompleteCesiumSwitch(state);
        }
    }

    bool TryGetResolutionThresholdReached(
        MapCesiumTransitionViewState state,
        bool switchToMap,
        out bool thresholdReached)
    {
        thresholdReached = false;
        if (!TryGetFlatMapSourceTexelsPerScreenPixel(state, out float flatMapTexelsPerScreenPixel) ||
            !TryGetCesiumRequestedTexelsPerScreenPixel(state, out float cesiumRequestedTexelsPerScreenPixel))
        {
            return false;
        }

        if (switchToMap)
        {
            thresholdReached = flatMapTexelsPerScreenPixel >= cesiumRequestedTexelsPerScreenPixel;
            return true;
        }

        float requiredCesiumAdvantage = 1f + switchToCesiumResolutionHysteresis;
        thresholdReached = cesiumRequestedTexelsPerScreenPixel >= flatMapTexelsPerScreenPixel * requiredCesiumAdvantage;
        return true;
    }

    bool TryGetGlobeFillBoundaryReached(
        MapCesiumTransitionViewState state,
        bool switchToMap,
        out bool boundaryReached)
    {
        boundaryReached = false;

        float fillDistanceMeters = 0f;
        if (switchToMap)
        {
            fillDistanceMeters = cesiumController != null
                ? cesiumController.WholeGlobeFillDistanceMeters
                : 0f;
            if (state.surfaceDistanceMeters > 0f && fillDistanceMeters > 0f)
            {
                float toleranceMeters = Mathf.Max(1f, fillDistanceMeters * 0.001f);
                boundaryReached = state.surfaceDistanceMeters >= fillDistanceMeters - toleranceMeters;
                return true;
            }

            if (state.normalizedFillDistance > 0f)
            {
                boundaryReached = state.normalizedFillDistance >= 0.999f;
                return true;
            }

            return false;
        }

        fillDistanceMeters = mapController != null
            ? mapController.WholeGlobeFillDistanceMeters
            : 0f;
        if (state.surfaceDistanceMeters > 0f && fillDistanceMeters > 0f)
        {
            float switchToCesiumDistance = fillDistanceMeters * switchToCesiumFitFraction;
            float toleranceMeters = Mathf.Max(1f, switchToCesiumDistance * 0.001f);
            boundaryReached = state.surfaceDistanceMeters <= switchToCesiumDistance + toleranceMeters;
            return true;
        }

        if (state.normalizedFillDistance > 0f)
        {
            boundaryReached = state.normalizedFillDistance <= switchToCesiumFitFraction;
            return true;
        }

        return false;
    }

    bool TryGetFlatMapSourceTexelsPerScreenPixel(
        MapCesiumTransitionViewState state,
        out float texelsPerScreenPixel)
    {
        texelsPerScreenPixel = 0f;
        if (mapController == null || mapController.MapMaterial == null || !state.isValid)
        {
            return false;
        }

        Texture sourceTexture = mapController.MapMaterial.GetTexture("_MainTex");
        if (sourceTexture == null || sourceTexture.width <= 0 || sourceTexture.height <= 0)
        {
            return false;
        }

        float degreesPerPixelX = Mathf.Abs(state.visibleLongitudeSpanDeg) / Mathf.Max(1, Screen.width);
        float degreesPerPixelY = Mathf.Abs(state.visibleLatitudeSpanDeg) / Mathf.Max(1, Screen.height);
        if (degreesPerPixelX <= 0f || degreesPerPixelY <= 0f)
        {
            return false;
        }

        float texelsPerScreenPixelX = degreesPerPixelX * (sourceTexture.width / 360f);
        float texelsPerScreenPixelY = degreesPerPixelY * (sourceTexture.height / 180f);
        texelsPerScreenPixel = Mathf.Min(texelsPerScreenPixelX, texelsPerScreenPixelY);
        return texelsPerScreenPixel > 0f;
    }

    bool TryGetCesiumRequestedTexelsPerScreenPixel(
        MapCesiumTransitionViewState state,
        out float texelsPerScreenPixel)
    {
        texelsPerScreenPixel = 0f;
        if (!state.isValid)
        {
            return false;
        }

        if (!TryGetRequestedCesiumRasterZoomLevelForFocus(state, out int zoomLevel))
        {
            return false;
        }

        if (!TryGetCesiumVisibleDegreesPerScreenPixel(
                state,
                out double degreesPerPixelX,
                out double degreesPerPixelY) ||
            !TryGetCesiumLevelZeroTexelsPerDegree(out double levelZeroTexelsPerDegreeX, out double levelZeroTexelsPerDegreeY))
        {
            return false;
        }

        double levelScale = System.Math.Pow(2.0, zoomLevel);
        double texelsPerDegreeX = levelZeroTexelsPerDegreeX * levelScale;
        double texelsPerDegreeY = levelZeroTexelsPerDegreeY * levelScale;
        double texelsPerScreenPixelX = texelsPerDegreeX * degreesPerPixelX;
        double texelsPerScreenPixelY = texelsPerDegreeY * degreesPerPixelY;
        texelsPerScreenPixel = (float)System.Math.Min(texelsPerScreenPixelX, texelsPerScreenPixelY);
        return texelsPerScreenPixel > 0f;
    }

    bool TryGetRequestedCesiumRasterZoomLevelForFocus(
        MapCesiumTransitionViewState state,
        out int zoomLevel)
    {
        zoomLevel = -1;
        if (!state.isValid ||
            !TryGetCesiumVisibleDegreesPerScreenPixel(
                state,
                out double degreesPerPixelX,
                out double degreesPerPixelY) ||
            !TryGetCesiumLevelZeroTexelsPerDegree(out double levelZeroTexelsPerDegreeX, out double levelZeroTexelsPerDegreeY) ||
            !TryGetCesiumTargetTexelsPerScreenPixel(out double targetTexelsPerScreenPixel) ||
            !TryGetCesiumRasterZoomRange(out int minimumZoomLevel, out int maximumZoomLevel))
        {
            return false;
        }

        double baseTexelsPerScreenPixelX = levelZeroTexelsPerDegreeX * degreesPerPixelX;
        double baseTexelsPerScreenPixelY = levelZeroTexelsPerDegreeY * degreesPerPixelY;
        double baseTexelsPerScreenPixel = System.Math.Min(baseTexelsPerScreenPixelX, baseTexelsPerScreenPixelY);
        if (baseTexelsPerScreenPixel <= 0.0)
        {
            return false;
        }

        int requiredZoomLevel = 0;
        double zoomRatio = targetTexelsPerScreenPixel / baseTexelsPerScreenPixel;
        if (zoomRatio > 1.0)
        {
            requiredZoomLevel = (int)System.Math.Ceiling(System.Math.Log(zoomRatio, 2.0) - 1e-9);
        }

        zoomLevel = Mathf.Clamp(requiredZoomLevel, minimumZoomLevel, maximumZoomLevel);
        return true;
    }

    bool TryGetCesiumVisibleDegreesPerScreenPixel(
        MapCesiumTransitionViewState state,
        out double degreesPerPixelX,
        out double degreesPerPixelY)
    {
        degreesPerPixelX = 0.0;
        degreesPerPixelY = 0.0;
        if (!state.isValid || !TryGetCesiumVisibleViewportSize(out int viewportWidth, out int viewportHeight))
        {
            return false;
        }

        degreesPerPixelX = System.Math.Abs(state.visibleLongitudeSpanDeg) / System.Math.Max(1, viewportWidth);
        degreesPerPixelY = System.Math.Abs(state.visibleLatitudeSpanDeg) / System.Math.Max(1, viewportHeight);
        return degreesPerPixelX > 0.0 && degreesPerPixelY > 0.0;
    }

    bool TryGetCesiumLevelZeroTexelsPerDegree(
        out double texelsPerDegreeX,
        out double texelsPerDegreeY)
    {
        texelsPerDegreeX = 0.0;
        texelsPerDegreeY = 0.0;

        int rootTilesX = System.Math.Max(1, cesiumRasterRootTilesX);
        int rootTilesY = System.Math.Max(1, cesiumRasterRootTilesY);
        int tileWidth = System.Math.Max(1, cesiumRasterTileWidth);
        int tileHeight = System.Math.Max(1, cesiumRasterTileHeight);

        texelsPerDegreeX = (rootTilesX * tileWidth) / WorldLongitudeDegrees;
        texelsPerDegreeY = (rootTilesY * tileHeight) / WorldLatitudeDegrees;
        return texelsPerDegreeX > 0.0 && texelsPerDegreeY > 0.0;
    }

    bool TryGetCesiumTargetTexelsPerScreenPixel(out double texelsPerScreenPixel)
    {
        texelsPerScreenPixel = 0.0;

        float maximumScreenSpaceError = cesiumRasterOverlay != null
            ? cesiumRasterOverlay.maximumScreenSpaceError
            : 2f;
        if (maximumScreenSpaceError <= 0f)
        {
            return false;
        }

        texelsPerScreenPixel = 1.0 / maximumScreenSpaceError;
        return texelsPerScreenPixel > 0.0;
    }

    bool TryGetCesiumRasterZoomRange(out int minimumZoomLevel, out int maximumZoomLevel)
    {
        minimumZoomLevel = 0;
        maximumZoomLevel = int.MaxValue;

        if (cesiumRasterOverlay != null)
        {
            minimumZoomLevel = Mathf.Max(0, cesiumRasterOverlay.minimumLevel);
            maximumZoomLevel = Mathf.Max(minimumZoomLevel, cesiumRasterOverlay.maximumLevel);
        }

        if (localTerrainServer != null &&
            localTerrainServer.TryGetSecondaryMaxRasterZoom(out int maximumAvailableRasterZoom))
        {
            maximumZoomLevel = Mathf.Min(
                maximumZoomLevel,
                Mathf.Max(minimumZoomLevel, maximumAvailableRasterZoom));
        }

        if (maximumZoomLevel == int.MaxValue)
        {
            maximumZoomLevel = Mathf.Max(minimumZoomLevel, 30);
        }

        return maximumZoomLevel >= minimumZoomLevel;
    }

    bool TryGetCesiumVisibleViewportSize(out int viewportWidth, out int viewportHeight)
    {
        viewportWidth = 0;
        viewportHeight = 0;

        if (cesiumCamera == null)
        {
            return false;
        }

        CacheCesiumCameraPresentation();

        RenderTexture targetTexture = hasCachedCesiumCameraPresentation
            ? cesiumCameraOriginalTargetTexture
            : cesiumCamera.targetTexture;
        if (targetTexture != null)
        {
            viewportWidth = Mathf.Max(1, targetTexture.width);
            viewportHeight = Mathf.Max(1, targetTexture.height);
            return true;
        }

        Rect visibleRect = hasCachedCesiumCameraPresentation ? cesiumCameraOriginalRect : cesiumCamera.rect;
        viewportWidth = Mathf.Max(1, Mathf.RoundToInt(Screen.width * visibleRect.width));
        viewportHeight = Mathf.Max(1, Mathf.RoundToInt(Screen.height * visibleRect.height));
        return true;
    }

    bool IsCesiumReadyForVisibleSwitch()
    {
        if (!waitForCesiumLoadProgress || cesiumTileset == null)
        {
            return true;
        }

        return cesiumTileset.ComputeLoadProgress() >= minimumCesiumLoadProgress;
    }

    void CompleteCesiumSwitch(MapCesiumTransitionViewState state)
    {
        isPreparingCesiumSwitch = false;
        SetMode(ViewMode.Cesium);
        cesiumController.ApplyTransitionViewState(state);
        nextSwitchTime = Time.unscaledTime + minSecondsBetweenSwitches;
    }

    void CancelPendingCesiumSwitch()
    {
        isPreparingCesiumSwitch = false;
        if (activeMode == ViewMode.FlatMap)
        {
            ApplyCesiumPresentation(visibleOnScreen: false);
        }
    }

    void SyncHiddenCesiumView()
    {
        if (mapController == null || cesiumController == null)
        {
            return;
        }

        MapCesiumTransitionViewState state = mapController.CaptureTransitionViewState();
        if (!state.isValid)
        {
            return;
        }

        cesiumController.ApplyTransitionViewState(state);
        if (!isPreparingCesiumSwitch)
        {
            ApplyCesiumPresentation(visibleOnScreen: false);
        }
    }

    void SetMode(ViewMode mode)
    {
        activeMode = mode;
        hasInitializedMode = true;

        bool mapActive = mode == ViewMode.FlatMap;
        bool cesiumActive = mode == ViewMode.Cesium;
        bool keepCesiumHidden = keepCesiumWarmInBackground && mapActive;
        bool cesiumRuntimeActive = cesiumActive || keepCesiumHidden;

        SetComponentEnabled(mapController, mapActive);
        SetComponentEnabled(cesiumController, cesiumActive);
        SetCameraEnabled(mapCamera, mapActive);
        ApplyCesiumPresentation(visibleOnScreen: cesiumActive);
        SetBehavioursEnabled(mapModeBehaviours, mapActive);
        SetBehavioursEnabled(cesiumModeBehaviours, cesiumRuntimeActive);
        SetObjectsActive(mapModeObjects, mapActive);
        SetObjectsActive(cesiumModeObjects, cesiumRuntimeActive);
        UpdateCameraTags(mapActive);
    }

    void CacheOriginalTags()
    {
        if (mapCamera != null)
        {
            mapCameraOriginalTag = mapCamera.tag;
        }

        if (cesiumCamera != null)
        {
            cesiumCameraOriginalTag = cesiumCamera.tag;
        }
    }

    void ApplyCesiumPresentation(bool visibleOnScreen)
    {
        if (cesiumCamera == null)
        {
            return;
        }

        if (!visibleOnScreen &&
            !keepCesiumWarmInBackground &&
            !isPreparingCesiumSwitch &&
            activeMode != ViewMode.Cesium)
        {
            SetCameraEnabled(cesiumCamera, false);
            return;
        }

        CacheCesiumCameraPresentation();
        cesiumCamera.enabled = true;

        AudioListener listener = cesiumCamera.GetComponent<AudioListener>();
        if (listener != null)
        {
            listener.enabled = visibleOnScreen;
        }

        if (visibleOnScreen)
        {
            cesiumCamera.targetTexture = cesiumCameraOriginalTargetTexture;
            cesiumCamera.rect = cesiumCameraOriginalRect;
            return;
        }

        EnsureHiddenCesiumRenderTexture();
        cesiumCamera.targetTexture = hiddenCesiumRenderTexture;
        cesiumCamera.rect = new Rect(0f, 0f, 1f, 1f);
    }

    void CacheCesiumCameraPresentation()
    {
        if (hasCachedCesiumCameraPresentation || cesiumCamera == null)
        {
            return;
        }

        cesiumCameraOriginalRect = cesiumCamera.rect;
        cesiumCameraOriginalTargetTexture = cesiumCamera.targetTexture;
        hasCachedCesiumCameraPresentation = true;
    }

    void EnsureHiddenCesiumRenderTexture()
    {
        int width;
        int height;
        if (isPreparingCesiumSwitch && TryGetCesiumVisibleViewportSize(out int visibleWidth, out int visibleHeight))
        {
            width = Mathf.Max(16, visibleWidth);
            height = Mathf.Max(16, visibleHeight);
        }
        else
        {
            int maxSize = Mathf.Max(16, hiddenCesiumRenderTextureSize);
            int screenWidth = Mathf.Max(1, Screen.width);
            int screenHeight = Mathf.Max(1, Screen.height);
            float scale = (float)maxSize / Mathf.Max(screenWidth, screenHeight);
            width = Mathf.Max(16, Mathf.RoundToInt(screenWidth * scale));
            height = Mathf.Max(16, Mathf.RoundToInt(screenHeight * scale));
        }

        if (hiddenCesiumRenderTexture != null &&
            hiddenCesiumRenderTexture.width == width &&
            hiddenCesiumRenderTexture.height == height)
        {
            return;
        }

        ReleaseHiddenCesiumRenderTexture();
        hiddenCesiumRenderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = "CesiumHiddenPreload",
            useMipMap = false,
            autoGenerateMips = false
        };
        hiddenCesiumRenderTexture.Create();
    }

    void ReleaseHiddenCesiumRenderTexture()
    {
        if (hiddenCesiumRenderTexture == null)
        {
            return;
        }

        if (hiddenCesiumRenderTexture.IsCreated())
        {
            hiddenCesiumRenderTexture.Release();
        }

        Destroy(hiddenCesiumRenderTexture);
        hiddenCesiumRenderTexture = null;
    }

    void UpdateCameraTags(bool mapActive)
    {
        if (!manageMainCameraTag)
        {
            return;
        }

        if (mapCamera != null)
        {
            mapCamera.tag = mapActive ? "MainCamera" : GetInactiveTag(mapCameraOriginalTag);
        }

        if (cesiumCamera != null)
        {
            cesiumCamera.tag = mapActive ? GetInactiveTag(cesiumCameraOriginalTag) : "MainCamera";
        }
    }

    static string GetInactiveTag(string originalTag)
    {
        return string.IsNullOrEmpty(originalTag) || originalTag == "MainCamera"
            ? "Untagged"
            : originalTag;
    }

    static void SetComponentEnabled(Behaviour behaviour, bool enabled)
    {
        if (behaviour != null)
        {
            behaviour.enabled = enabled;
        }
    }

    static void SetCameraEnabled(Camera targetCamera, bool enabled)
    {
        if (targetCamera == null)
        {
            return;
        }

        targetCamera.enabled = enabled;

        AudioListener listener = targetCamera.GetComponent<AudioListener>();
        if (listener != null)
        {
            listener.enabled = enabled;
        }
    }

    static void SetBehavioursEnabled(Behaviour[] behaviours, bool enabled)
    {
        if (behaviours == null)
        {
            return;
        }

        foreach (Behaviour behaviour in behaviours)
        {
            if (behaviour != null)
            {
                behaviour.enabled = enabled;
            }
        }
    }

    static void SetObjectsActive(GameObject[] objects, bool active)
    {
        if (objects == null)
        {
            return;
        }

        foreach (GameObject target in objects)
        {
            if (target != null)
            {
                target.SetActive(active);
            }
        }
    }
}
