using UnityEngine;

[DisallowMultipleComponent]
public class MapCesiumTransitionManager : MonoBehaviour
{
    public enum ViewMode
    {
        FlatMap,
        Cesium
    }

    [Header("References")]
    [SerializeField] MapControllerEqr mapController;
    [SerializeField] CesiumMapController cesiumController;
    [SerializeField] Camera mapCamera;
    [SerializeField] Camera cesiumCamera;

    [Header("Optional Mode Toggles")]
    [SerializeField] Behaviour[] mapModeBehaviours;
    [SerializeField] Behaviour[] cesiumModeBehaviours;
    [SerializeField] GameObject[] mapModeObjects;
    [SerializeField] GameObject[] cesiumModeObjects;

    [Header("Switch Thresholds")]
    [SerializeField, Tooltip("Switch to Cesium when the visible latitude span shrinks to this value or lower.")]
    float switchToCesiumLatitudeSpanDeg = 20f;
    [SerializeField, Tooltip("Switch back to the flat map when the visible latitude span expands to this value or higher.")]
    float switchToMapLatitudeSpanDeg = 21f;
    [SerializeField] float minSecondsBetweenSwitches = 0.4f;

    [Header("Cesium Preload")]
    [SerializeField, Tooltip("Keep Cesium rendering offscreen while the flat map is visible so tiles stay warm.")]
    bool keepCesiumWarmInBackground = true;
    [SerializeField, Tooltip("How long Cesium stays offscreen after crossing the switch threshold before it becomes visible.")]
    float cesiumWarmupSeconds = 0.2f;
    [SerializeField, Tooltip("Square render texture size used while Cesium is hidden offscreen.")]
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
        switchToCesiumLatitudeSpanDeg = Mathf.Max(0.1f, switchToCesiumLatitudeSpanDeg);
        switchToMapLatitudeSpanDeg = Mathf.Max(0.1f, switchToMapLatitudeSpanDeg);
        minSecondsBetweenSwitches = Mathf.Max(0f, minSecondsBetweenSwitches);
        cesiumWarmupSeconds = Mathf.Max(0f, cesiumWarmupSeconds);
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
        if (!state.isValid || state.visibleLatitudeSpanDeg > switchToCesiumLatitudeSpanDeg)
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
        if (!state.isValid || state.visibleLatitudeSpanDeg < switchToMapLatitudeSpanDeg)
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

        if (mapCamera == null && mapController != null)
        {
            mapCamera = mapController.ControlledCamera;
        }

        if (cesiumCamera == null && cesiumController != null)
        {
            cesiumCamera = cesiumController.ControlledCamera;
        }
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
        if (!state.isValid || state.visibleLatitudeSpanDeg > switchToCesiumLatitudeSpanDeg)
        {
            CancelPendingCesiumSwitch();
            return;
        }

        cesiumController.ApplyTransitionViewState(state);
        if (Time.unscaledTime >= cesiumSwitchReadyTime)
        {
            CompleteCesiumSwitch(state);
        }
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
        int maxSize = Mathf.Max(16, hiddenCesiumRenderTextureSize);
        int screenWidth = Mathf.Max(1, Screen.width);
        int screenHeight = Mathf.Max(1, Screen.height);
        float scale = (float)maxSize / Mathf.Max(screenWidth, screenHeight);
        int width = Mathf.Max(16, Mathf.RoundToInt(screenWidth * scale));
        int height = Mathf.Max(16, Mathf.RoundToInt(screenHeight * scale));

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
