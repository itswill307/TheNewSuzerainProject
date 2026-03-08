using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Province picking for the Cesium globe view. Raycasts the WGS84 ellipsoid,
/// converts the hit to lat/lon, samples the province ID texture, and sets
/// selection state via global shader properties (since Cesium creates
/// per-tile material instances).
///
/// Also sets the coordinate transform globals (_CesiumGlobeCenterWorld,
/// _CesiumWorldDirToEcef) that the shader needs to derive geographic UV
/// from world position for sampling the province ID texture.
/// </summary>
public class ProvincePickerCesium : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] CesiumGeoreference georeference;
    [SerializeField] Camera cesiumCamera;
    [SerializeField] MapCesiumTransitionManager transitionManager;
    public Texture2D provinceIdTex;

    [Header("Highlight")]
    [SerializeField] bool enableProvinceHoverSelect = true;
    public bool highlightHovered = true;
    public Color highlightColor = new Color(1f, 0.75f, 0f, 0.6f);
    public Color hoverColor = new Color(0f, 1f, 1f, 0.5f);

    [Header("Masking")]
    [SerializeField] bool blockOcean = true;
    [SerializeField] int oceanId = 0;

    CesiumEllipsoid ellipsoid;
    double3 ellipsoidRadii;
    InputSystem_Actions input;
    Texture2D cachedProvinceTexture;
    Color32[] provincePixels;
    int provinceWidth;
    int provinceHeight;
    bool canSampleProvinceOnCpu;
    bool hasWarnedGpuFallback;
    RenderTexture provinceSampleRt;
    Texture2D provinceSampleCpuTex;

    static readonly int SelectedIdProp = Shader.PropertyToID("_SelectedID");
    static readonly int HoverIdProp = Shader.PropertyToID("_HoverID");
    static readonly int HighlightColorProp = Shader.PropertyToID("_HighlightColor");
    static readonly int HoverColorProp = Shader.PropertyToID("_HoverColor");
    static readonly int GlobeCenterProp = Shader.PropertyToID("_CesiumGlobeCenterWorld");
    static readonly int GlobeCenterEcefProp = Shader.PropertyToID("_CesiumGlobeCenterEcef");
    static readonly int OneOverRadiiSquaredProp = Shader.PropertyToID("_CesiumOneOverRadiiSquared");
    static readonly int WorldDirToEcefProp = Shader.PropertyToID("_CesiumWorldDirToEcef");
    static readonly int ProvinceIdTexProp = Shader.PropertyToID("_ProvinceIDTex");

    void Awake()
    {
        input = new InputSystem_Actions();

        if (georeference == null)
            georeference = GetComponentInParent<CesiumGeoreference>();

        if (cesiumCamera == null)
        {
            var controller = GetComponentInParent<CesiumMapController>();
            if (controller != null)
                cesiumCamera = controller.ControlledCamera;
        }

        if (transitionManager == null)
            transitionManager = FindFirstObjectByType<MapCesiumTransitionManager>();

        if (!provinceIdTex)
        {
            Debug.LogError("ProvincePickerCesium: Province ID texture must be assigned.");
            enabled = false;
            return;
        }

        if (georeference == null)
        {
            Debug.LogError("ProvincePickerCesium: CesiumGeoreference not found.");
            enabled = false;
            return;
        }

        georeference.Initialize();
        ellipsoid = georeference.ellipsoid;
        ellipsoidRadii = ellipsoid.GetRadii();
        RefreshProvinceSamplingMode();
        ApplyProvinceShaderGlobals();
        ApplySharedSelectionState();
    }

    void OnEnable()
    {
        input ??= new InputSystem_Actions();
        input.Enable();
        RefreshProvinceSamplingMode();
        ApplyProvinceShaderGlobals();
        ApplySharedSelectionState();
    }

    void OnDisable()
    {
        input?.Disable();
        ReleaseGpuSamplingResources();
    }

    void OnDestroy()
    {
        ReleaseGpuSamplingResources();
    }

    void Update()
    {
        if (cesiumCamera == null || georeference == null)
            return;

        ApplyProvinceShaderGlobals();
        ApplySharedSelectionState();

        // Always update the coordinate transform so the shader can
        // compute province UV even while Cesium renders in the background.
        UpdateCoordinateTransformGlobals();

        // Only pick/highlight when Cesium is the visible mode,
        // not during background warm-up.
        if (transitionManager != null &&
            transitionManager.ActiveMode != MapCesiumTransitionManager.ViewMode.Cesium)
        {
            return;
        }

        if (!enableProvinceHoverSelect)
        {
            if (MapProvinceSelectionState.HoverId >= 0)
            {
                MapProvinceSelectionState.ClearHover();
                ApplySharedSelectionState();
            }
            return;
        }

        Vector2 screenPos = input.Map.Point.ReadValue<Vector2>();

        if (!TryGetLongitudeLatitudeAtScreen(screenPos, out double lonDeg, out double latDeg))
        {
            MapProvinceSelectionState.ClearHover();
            ApplySharedSelectionState();
            return;
        }

        float u = (float)(lonDeg / 360.0) + 0.5f;
        float v = (float)(latDeg / 180.0) + 0.5f;
        u = u - Mathf.Floor(u);
        v = Mathf.Clamp01(v);

        int pid = SampleProvinceId(u, v);
        if (pid < 0)
        {
            MapProvinceSelectionState.ClearHover();
            ApplySharedSelectionState();
            return;
        }

        if (blockOcean && pid == oceanId)
        {
            if (input.Map.LMB.WasPressedThisFrame())
            {
                MapProvinceSelectionState.ClearSelected();
            }
            MapProvinceSelectionState.ClearHover();
            ApplySharedSelectionState();
            return;
        }

        if (highlightHovered)
        {
            MapProvinceSelectionState.SetHover(pid);
        }
        else
        {
            MapProvinceSelectionState.ClearHover();
        }
        ApplySharedSelectionState();

        if (input.Map.LMB.WasPressedThisFrame())
        {
            MapProvinceSelectionState.SetSelected(pid);
            ApplySharedSelectionState();
            Debug.Log($"Clicked province ID = {pid}");
        }
    }

    void UpdateCoordinateTransformGlobals()
    {
        Shader.SetGlobalVector(GlobeCenterProp, georeference.transform.position);

        double3 globeCenterEcef = georeference.TransformUnityPositionToEarthCenteredEarthFixed(double3.zero);
        Shader.SetGlobalVector(
            GlobeCenterEcefProp,
            new Vector4((float)globeCenterEcef.x, (float)globeCenterEcef.y, (float)globeCenterEcef.z, 0f));
        Shader.SetGlobalVector(
            OneOverRadiiSquaredProp,
            new Vector4(
                1f / ((float)ellipsoidRadii.x * (float)ellipsoidRadii.x),
                1f / ((float)ellipsoidRadii.y * (float)ellipsoidRadii.y),
                1f / ((float)ellipsoidRadii.z * (float)ellipsoidRadii.z),
                0f));

        double lonRad = georeference.longitude * Mathf.Deg2Rad;
        double latRad = georeference.latitude * Mathf.Deg2Rad;

        float sinLon = (float)System.Math.Sin(lonRad);
        float cosLon = (float)System.Math.Cos(lonRad);
        float sinLat = (float)System.Math.Sin(latRad);
        float cosLat = (float)System.Math.Cos(latRad);

        // CesiumForUnity local frame: X = East, Y = Up, Z = North.
        // Columns = where each local axis maps in ECEF.
        Matrix4x4 localToEcef = new Matrix4x4(
            new Vector4(-sinLon, cosLon, 0f, 0f),
            new Vector4(cosLat * cosLon, cosLat * sinLon, sinLat, 0f),
            new Vector4(-sinLat * cosLon, -sinLat * sinLon, cosLat, 0f),
            new Vector4(0f, 0f, 0f, 1f)
        );

        Matrix4x4 worldToLocal = Matrix4x4.Rotate(Quaternion.Inverse(georeference.transform.rotation));
        Shader.SetGlobalMatrix(WorldDirToEcefProp, localToEcef * worldToLocal);
    }

    void ApplyProvinceShaderGlobals()
    {
        if (provinceIdTex != null)
        {
            Shader.SetGlobalTexture(ProvinceIdTexProp, provinceIdTex);
        }
    }

    bool TryGetLongitudeLatitudeAtScreen(Vector2 screenPos, out double longitudeDeg, out double latitudeDeg)
    {
        longitudeDeg = 0.0;
        latitudeDeg = 0.0;

        Ray worldRay = cesiumCamera.ScreenPointToRay(new Vector3(screenPos.x, screenPos.y, 0f));
        if (!TryIntersectEllipsoid(worldRay, out double3 hitEcef))
            return false;

        double3 llh = ellipsoid.CenteredFixedToLongitudeLatitudeHeight(hitEcef);
        longitudeDeg = llh.x;
        latitudeDeg = llh.y;
        return true;
    }

    bool TryIntersectEllipsoid(Ray worldRay, out double3 hitEcef)
    {
        hitEcef = default;

        Transform geoTransform = georeference.transform;
        Vector3 rayOriginLocal = geoTransform.InverseTransformPoint(worldRay.origin);
        Vector3 rayDirectionLocal = geoTransform.InverseTransformDirection(worldRay.direction).normalized;

        double3 rayOriginEcef = georeference.TransformUnityPositionToEarthCenteredEarthFixed(
            new double3(rayOriginLocal.x, rayOriginLocal.y, rayOriginLocal.z));
        double3 rayDirectionEcef = math.normalize(
            georeference.TransformUnityDirectionToEarthCenteredEarthFixed(
                new double3(rayDirectionLocal.x, rayDirectionLocal.y, rayDirectionLocal.z)));

        double3 scaledOrigin = new double3(
            rayOriginEcef.x / ellipsoidRadii.x,
            rayOriginEcef.y / ellipsoidRadii.y,
            rayOriginEcef.z / ellipsoidRadii.z);
        double3 scaledDirection = new double3(
            rayDirectionEcef.x / ellipsoidRadii.x,
            rayDirectionEcef.y / ellipsoidRadii.y,
            rayDirectionEcef.z / ellipsoidRadii.z);

        double a = math.dot(scaledDirection, scaledDirection);
        double b = 2.0 * math.dot(scaledOrigin, scaledDirection);
        double c = math.dot(scaledOrigin, scaledOrigin) - 1.0;
        double discriminant = b * b - 4.0 * a * c;

        if (discriminant < 0.0)
            return false;

        double sqrtDiscriminant = math.sqrt(discriminant);
        double t0 = (-b - sqrtDiscriminant) / (2.0 * a);
        double t1 = (-b + sqrtDiscriminant) / (2.0 * a);
        double t = t0 > 0.0 ? t0 : t1;

        if (t <= 0.0)
            return false;

        hitEcef = rayOriginEcef + rayDirectionEcef * t;
        return true;
    }

    int SampleProvinceId(float u, float v)
    {
        RefreshProvinceSamplingMode();
        if (canSampleProvinceOnCpu)
        {
            int x = Mathf.Clamp((int)(u * provinceWidth), 0, provinceWidth - 1);
            int y = Mathf.Clamp((int)(v * provinceHeight), 0, provinceHeight - 1);
            int index = y * provinceWidth + x;
            if ((uint)index < (uint)provincePixels.Length)
            {
                return DecodeProvinceId(provincePixels[index]);
            }
        }

        if (!EnsureGpuSamplingResources())
            return -1;

        Graphics.Blit(provinceIdTex, provinceSampleRt, Vector2.zero, new Vector2(u, v));

        RenderTexture previous = RenderTexture.active;
        try
        {
            RenderTexture.active = provinceSampleRt;
            provinceSampleCpuTex.ReadPixels(new Rect(0f, 0f, 1f, 1f), 0, 0, false);
            provinceSampleCpuTex.Apply(false, false);
        }
        finally
        {
            RenderTexture.active = previous;
        }

        Color32 pixel = provinceSampleCpuTex.GetPixel(0, 0);
        return DecodeProvinceId(pixel);
    }

    void ApplySharedSelectionState()
    {
        int selectedId = enableProvinceHoverSelect ? MapProvinceSelectionState.SelectedId : -1;
        int hoverId = enableProvinceHoverSelect && highlightHovered ? MapProvinceSelectionState.HoverId : -1;

        Shader.SetGlobalFloat(SelectedIdProp, selectedId);
        Shader.SetGlobalFloat(HoverIdProp, hoverId);
        Shader.SetGlobalColor(HighlightColorProp, highlightColor);
        Shader.SetGlobalColor(HoverColorProp, hoverColor);
    }

    bool EnsureGpuSamplingResources()
    {
        if (provinceSampleRt == null)
        {
            provinceSampleRt = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            provinceSampleRt.Create();
        }

        if (provinceSampleCpuTex == null)
        {
            provinceSampleCpuTex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
        }

        return provinceSampleRt != null && provinceSampleCpuTex != null;
    }

    void RefreshProvinceSamplingMode()
    {
        if (cachedProvinceTexture == provinceIdTex)
            return;

        cachedProvinceTexture = provinceIdTex;
        provincePixels = null;
        provinceWidth = 0;
        provinceHeight = 0;
        canSampleProvinceOnCpu = false;
        hasWarnedGpuFallback = false;

        if (provinceIdTex == null)
            return;

        if (provinceIdTex.isReadable)
        {
            provincePixels = provinceIdTex.GetPixels32();
            provinceWidth = provinceIdTex.width;
            provinceHeight = provinceIdTex.height;
            canSampleProvinceOnCpu =
                provincePixels != null &&
                provincePixels.Length == provinceWidth * provinceHeight &&
                provinceWidth > 0 &&
                provinceHeight > 0;
        }

        if (!canSampleProvinceOnCpu)
        {
            EnsureGpuSamplingResources();
            if (!hasWarnedGpuFallback)
            {
                Debug.LogWarning(
                    $"ProvincePickerCesium: Province ID texture '{provinceIdTex.name}' is not readable; using slower GPU readback fallback.");
                hasWarnedGpuFallback = true;
            }
        }
    }

    static int DecodeProvinceId(Color32 pixel)
    {
        return pixel.r | (pixel.g << 8) | (pixel.b << 16);
    }

    void ReleaseGpuSamplingResources()
    {
        if (provinceSampleRt != null)
        {
            provinceSampleRt.Release();
            Destroy(provinceSampleRt);
            provinceSampleRt = null;
        }

        if (provinceSampleCpuTex != null)
        {
            Destroy(provinceSampleCpuTex);
            provinceSampleCpuTex = null;
        }
    }
}
