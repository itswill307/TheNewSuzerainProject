using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Camera))]
public class MapControllerEqr : MonoBehaviour
{
    [Header("Scene References")]
    [SerializeField] Material mapMat;
    [SerializeField] Renderer mapRenderer; // mesh renderer for bounds calculation

    [Header("World Geometry")]
    float radius = 6371.0088f; // derived from earthRadiusKm / kmPerUnit
    [SerializeField] float earthRadiusKm = 6371.0088f;
    [SerializeField] float kmPerUnit = 1f;

    [Header("Real-World Height")]
    [SerializeField] float heightMinKm = -10.994f;
    [SerializeField] float heightMaxKm = 8.849f;
    [SerializeField] float heightExaggeration = 1f;
    [SerializeField, Tooltip("Enables elevation displacement on the global map mesh.")]
    bool enableGlobalElevation = false;
    [SerializeField, Tooltip("Multiplier applied to displaced elevation extent when calculating the minimum safe near-clip zoom distance.")]
    float nearBufferElevationMultiplier = 1f;
    [SerializeField, Tooltip("Additional fixed world-units added to the near-clip safety buffer.")]
    float nearBufferExtraUnits = 0f;

    [Header("Zoom")]
    [SerializeField, Tooltip("Base exponential scroll sensitivity.")]
    float zoomSpeed = 0.012f;
    [SerializeField] bool dynamicZoomSpeed = true;
    [SerializeField] float minZoomSpeedScale = 1f;
    [SerializeField] float maxZoomSpeedScale = 3f;
    [SerializeField] float zoomInBuffer = 0.001f;

    [Header("Camera Clipping")]
    [SerializeField] bool autoFarClip = true;
    [SerializeField] float farClipPaddingRadius = 3.5f;
    [SerializeField] float minFarClip = 20000f;

    [Header("Renderer Bounds")]
    [SerializeField] bool autoExpandRendererBounds = true;

    [Header("Panning")]
    [SerializeField, Tooltip("Keyboard pan speed in screen pixels per second.")]
    float panKeySpeed = 1000f;
    [SerializeField] float panDragSpeed = 1f;

    [Header("Rotation")]
    [SerializeField, Tooltip("Degrees of yaw/pitch per pixel when rotating (right mouse drag)")]
    float rotateSensitivity = 0.2f;
    [SerializeField, Tooltip("Minimum and maximum pitch (deg) to keep camera right-side up")]
    float minPitchDeg = -80f, maxPitchDeg = 80f;
    [SerializeField, Tooltip("Minimum and maximum yaw (deg) when rotating (right mouse drag)")]
    float minYawDeg = -80f, maxYawDeg = 80f;
    [SerializeField, Tooltip("Speed at which camera returns to default when RMB is released (deg/sec)")]
    float returnToDefaultSpeed = 240f;

    [Header("Projection Morph")]
    [SerializeField] float currentMorph = 0f;        // 0=equirectangular, 1=projection target
    [SerializeField] bool enableZoomMorph = true;    // enable automatic morph based on zoom level
    [SerializeField, Tooltip("Cubic morph vs zoom when enabled.")]
    bool useCubicMorph = false;

    [Header("Projection Mode")]
    [SerializeField, Tooltip("Render the map as a sphere in the shader.")]
    bool sphereMode = false;
    [SerializeField, Tooltip("Automatically switch to sphere mode when the sphere fills the camera width.")]
    bool autoSphereFromZoom = true;

    // ---------- private ----------
    Camera cam;
    InputSystem_Actions input;
    float mapWidth, mapHeight;
    float baseDistance;
    float minZoom, maxZoom;
    float currentZoom;

    // Panning state
    float focusLon = 0f; // longitude center (-180 to 180) - handled by UV offset only
    float cameraLat = 0f; // camera latitude in degrees - handled by camera position + Z distance correction

    // Orbit state (right-mouse drag)
    float orbitYawDeg = 0f;
    float orbitPitchDeg = 0f;
    bool wasMiddleMouseHeld;
    bool hasPanAnchor;
    Vector2 panAnchorUv;

    // Cached latitude limits
    float cachedMinLatLimit = -90f;
    float cachedMaxLatLimit = 90f;
    float lastZoomForLimits = -1f;
    int lastScreenWidth;
    int lastScreenHeight;
    float lastFieldOfView;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (input == null)
        {
            input = new InputSystem_Actions();
        }

        UpdateDerivedRadius();
        UpdateMapDimensions();
        SyncRendererBounds();

        CalculateZoomLimits();

        currentZoom = baseDistance;
        cameraLat = 0f;
        SyncCameraFarClip();

        transform.localRotation = Quaternion.identity;

        PositionCamera();
        SyncMaterialConstants();
        SetupFlatMap();
        CacheCameraShape();
    }

    void OnEnable()
    {
        if (input == null)
        {
            input = new InputSystem_Actions();
        }

        if (Application.isPlaying)
        {
            input.Enable();
        }
    }

    void OnDisable()
    {
        if (input != null && Application.isPlaying)
        {
            input.Disable();
        }

        wasMiddleMouseHeld = false;
        hasPanAnchor = false;
    }

    void CalculateZoomLimits()
    {
        float verticalFovRad = cam.fieldOfView * Mathf.Deg2Rad;
        float horizontalFovRad = 2f * Mathf.Atan(Mathf.Tan(verticalFovRad * 0.5f) * cam.aspect);

        // Match the most zoomed-out morph (Winkel Tripel at 0.5).
        float fitWidth = GetProjectionWidthAtMorph(0.5f);

        float horizontalDistance = (fitWidth * 0.5f) / Mathf.Tan(horizontalFovRad * 0.5f);

        baseDistance = horizontalDistance;
        maxZoom = horizontalDistance;
        float minByBuffer = radius * zoomInBuffer;
        float elevationExtentUnits = GetElevationExtentUnits();
        float nearBuffer = cam.nearClipPlane
            + (elevationExtentUnits * Mathf.Max(0f, nearBufferElevationMultiplier))
            + Mathf.Max(0f, nearBufferExtraUnits);
        float maxPitchAbs = Mathf.Max(Mathf.Abs(minPitchDeg), Mathf.Abs(maxPitchDeg));
        float cosMaxPitch = Mathf.Cos(maxPitchAbs * Mathf.Deg2Rad);
        if (cosMaxPitch < 0.01f) cosMaxPitch = 0.01f;
        float minByNear = nearBuffer / cosMaxPitch;
        minZoom = Mathf.Max(minByBuffer, minByNear);
    }

    float GetSphereSwitchZoom()
    {
        if (cam == null) return 0f;
        float verticalFovRad = cam.fieldOfView * Mathf.Deg2Rad;
        float horizontalFovRad = 2f * Mathf.Atan(Mathf.Tan(verticalFovRad * 0.5f) * cam.aspect);
        float sinHalfHfov = Mathf.Sin(horizontalFovRad * 0.5f);
        if (sinHalfHfov <= 1e-4f) return 0f;
        return radius * ((1f / sinHalfHfov) - 1f);
    }

    void UpdateMapDimensions()
    {
        mapWidth = 4f * radius;
        mapHeight = 2f * radius;
    }

    void UpdateDerivedRadius()
    {
        radius = earthRadiusKm / Mathf.Max(1e-6f, kmPerUnit);
    }

    void SyncMaterialConstants()
    {
        if (mapMat == null) return;
        mapMat.SetFloat("_Radius", radius);
        mapMat.SetFloat("_KmPerUnit", kmPerUnit);
        mapMat.SetFloat("_HeightMinKm", heightMinKm);
        mapMat.SetFloat("_HeightMaxKm", heightMaxKm);
        mapMat.SetFloat("_HeightExaggeration", GetGlobalHeightExaggeration());
    }

    float GetGlobalHeightExaggeration()
    {
        if (!enableGlobalElevation) return 0f;
        return Mathf.Max(0f, heightExaggeration);
    }

    void SyncRendererBounds()
    {
        if (!autoExpandRendererBounds || mapRenderer == null) return;

        MeshFilter meshFilter = mapRenderer.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null) return;

        Mesh mesh = meshFilter.sharedMesh;
        if (mesh == null) return;

        float elevationExtentUnits = GetElevationExtentUnits();

        // Cover both Aitoff-planar extents and sphere extents used by the vertex shader.
        float halfX = Mathf.Max(Mathf.PI * radius, radius + elevationExtentUnits);
        float halfY = Mathf.Max(0.5f * Mathf.PI * radius, radius + elevationExtentUnits);
        float halfZ = radius + elevationExtentUnits;

        mesh.bounds = new Bounds(
            Vector3.zero,
            new Vector3(halfX * 2f, halfY * 2f, halfZ * 2f)
        );
    }

    void SyncCameraFarClip()
    {
        if (cam == null || !autoFarClip) return;

        float safeZoom = currentZoom > 0f ? currentZoom : Mathf.Max(baseDistance, maxZoom);
        float elevationExtentUnits = GetElevationExtentUnits();
        float requiredFar = safeZoom + (radius * Mathf.Max(0f, farClipPaddingRadius)) + elevationExtentUnits;
        cam.farClipPlane = Mathf.Max(minFarClip, requiredFar);
    }

    float GetElevationExtentUnits()
    {
        float maxAbsElevationKm = Mathf.Max(Mathf.Abs(heightMinKm), Mathf.Abs(heightMaxKm));
        return (maxAbsElevationKm * Mathf.Max(0f, heightExaggeration)) / Mathf.Max(1e-6f, kmPerUnit);
    }

    void PositionCamera()
    {
        float lonDeg = GetFocusLongitudeDeg();
        Vector3 pivot = CalculateSurfacePositionAtLatLon(cameraLat, lonDeg);
        Vector3 surfaceNormal = CalculateSurfaceNormalAtLatLon(cameraLat, lonDeg);

        Vector3 offset = surfaceNormal * currentZoom;

        Quaternion yawQ = Quaternion.AngleAxis(orbitYawDeg, Vector3.up);
        Vector3 afterYaw = yawQ * offset;

        Vector3 rightAxis = Vector3.Cross(Vector3.up, afterYaw).normalized;
        if (rightAxis.sqrMagnitude < 1e-6f)
        {
            rightAxis = Vector3.right;
        }

        orbitPitchDeg = Mathf.Clamp(orbitPitchDeg, minPitchDeg, maxPitchDeg);
        Quaternion pitchQ = Quaternion.AngleAxis(orbitPitchDeg, rightAxis);
        Vector3 finalOffset = pitchQ * afterYaw;

        Vector3 cameraPosition = pivot + finalOffset;
        cam.transform.localPosition = cameraPosition;

        Vector3 forward = (pivot - cameraPosition).normalized;
        transform.localRotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    float GetFocusLongitudeDeg()
    {
        return Mathf.DeltaAngle(0f, focusLon);
    }

    Vector3 CalculateSurfacePositionAtLatLon(float latitudeDegrees, float longitudeDegrees)
    {
        float latitudeRad = latitudeDegrees * Mathf.Deg2Rad;
        if (!sphereMode)
        {
            return new Vector3(0f, latitudeRad * radius, 0f);
        }

        float longitudeRad = longitudeDegrees * Mathf.Deg2Rad;
        float cosLat = Mathf.Cos(latitudeRad);
        float sinLat = Mathf.Sin(latitudeRad);
        float cosLon = Mathf.Cos(longitudeRad);
        float sinLon = Mathf.Sin(longitudeRad);
        return new Vector3(
            cosLat * cosLon * radius,
            sinLat * radius,
            cosLat * sinLon * radius
        );
    }

    Vector3 CalculateSurfaceNormalAtLatLon(float latitudeDegrees, float longitudeDegrees)
    {
        if (!sphereMode) return Vector3.back;

        Vector3 pos = CalculateSurfacePositionAtLatLon(latitudeDegrees, longitudeDegrees);
        return pos.sqrMagnitude < 1e-6f ? Vector3.up : pos.normalized;
    }

    (float, float) CalculateLatitudeLimitsFromFOV()
    {
        if (sphereMode)
        {
            return (-85f, 85f);
        }

        float verticalFovRad = cam.fieldOfView * Mathf.Deg2Rad;
        float halfVerticalFov = verticalFovRad * 0.5f;

        float minLat = 0f;
        float maxLat = 90f;

        for (int i = 0; i < 12; i++)
        {
            float testLat = (minLat + maxLat) * 0.5f;

            float lonDeg = GetFocusLongitudeDeg();
            Vector3 cameraPos = CalculateSurfacePositionAtLatLon(testLat, lonDeg) +
                               CalculateSurfaceNormalAtLatLon(testLat, lonDeg) * currentZoom;

            Vector3 cameraForward = -CalculateSurfaceNormalAtLatLon(testLat, lonDeg);
            Vector3 cameraUp = Vector3.up;
            cameraUp = Vector3.Cross(Vector3.Cross(cameraForward, cameraUp), cameraForward).normalized;

            Vector3 northPolePos = CalculateSurfacePositionAtLatLon(90f, lonDeg);
            Vector3 toNorthPole = northPolePos - cameraPos;

            float forwardDistance = Vector3.Dot(toNorthPole, cameraForward);
            float upDistance = Vector3.Dot(toNorthPole, cameraUp);

            float angleToNorthPole = Mathf.Atan2(upDistance, forwardDistance);

            if (angleToNorthPole >= 0f && angleToNorthPole < halfVerticalFov)
            {
                maxLat = testLat;
            }
            else
            {
                minLat = testLat;
            }
        }

        float limitLatitude = (minLat + maxLat) * 0.5f;
        return (-limitLatitude, limitLatitude);
    }

    void SetupFlatMap()
    {
        if (mapMat != null)
        {
            UpdateUVOffset();
        }
    }

    void Update()
    {
        if (!Application.isPlaying) return;

        if (CameraShapeChanged())
        {
            CalculateZoomLimits();
            currentZoom = Mathf.Clamp(currentZoom, minZoom, maxZoom);
            CacheCameraShape();
        }

        UpdateZoom();
        RefreshProjectionState();

        Vector2 moveKeys = input.Map.Move.ReadValue<Vector2>();
        Vector2 dragPan = input.Map.DragPan.ReadValue<Vector2>();
        Vector2 cursorPos = input.Map.Point.ReadValue<Vector2>();
        bool middleMouseHeld = Mouse.current?.middleButton.isPressed ?? false;
        bool cursorOverMap = TryGetUVAtScreen(cursorPos, out _);

        if (middleMouseHeld && !wasMiddleMouseHeld)
        {
            TryBeginPanAnchor(cursorPos);
        }
        else if (middleMouseHeld && !hasPanAnchor && cursorOverMap)
        {
            TryBeginPanAnchor(cursorPos);
        }
        else if (!middleMouseHeld)
        {
            hasPanAnchor = false;
        }

        wasMiddleMouseHeld = middleMouseHeld;

        float verticalFovRad = cam.fieldOfView * Mathf.Deg2Rad;
        float horizontalFovRad = 2f * Mathf.Atan(Mathf.Tan(verticalFovRad * 0.5f) * cam.aspect);
        float worldUnitsPerPixelX = (2f * currentZoom * Mathf.Tan(horizontalFovRad * 0.5f)) / Screen.width;
        float worldUnitsPerPixelY = (2f * currentZoom * Mathf.Tan(verticalFovRad * 0.5f)) / Screen.height;

        float degreesPerPixelY = (worldUnitsPerPixelY / mapHeight) * 180f;
        float cosLat = Mathf.Cos(cameraLat * Mathf.Deg2Rad);
        float widthFactor = Mathf.Lerp(1f, cosLat, currentMorph * 0.5f);
        widthFactor = Mathf.Max(0.01f, widthFactor);
        float degreesPerPixelX = (worldUnitsPerPixelX / (mapWidth * widthFactor)) * 360f;

        if (TryGetPanDegreesPerPixelAtScreen(
            new Vector2(Screen.width * 0.5f, Screen.height * 0.5f),
            out float sampledDegreesPerPixelX,
            out float sampledDegreesPerPixelY))
        {
            degreesPerPixelX = sampledDegreesPerPixelX;
            degreesPerPixelY = sampledDegreesPerPixelY;
        }

        // panKeySpeed is interpreted as screen pixels per second.
        float panLon = moveKeys.x * panKeySpeed * degreesPerPixelX * Time.deltaTime;
        float panLat = moveKeys.y * panKeySpeed * degreesPerPixelY * Time.deltaTime;

        if (middleMouseHeld && cursorOverMap && hasPanAnchor && TryGetScreenPositionForUV(panAnchorUv, out Vector2 anchorScreenPos))
        {
            float anchorDegreesPerPixelX = degreesPerPixelX;
            float anchorDegreesPerPixelY = degreesPerPixelY;
            if (TryGetPanDegreesPerPixelAtScreen(anchorScreenPos, out float sampledAnchorDegreesPerPixelX, out float sampledAnchorDegreesPerPixelY))
            {
                anchorDegreesPerPixelX = sampledAnchorDegreesPerPixelX;
                anchorDegreesPerPixelY = sampledAnchorDegreesPerPixelY;
            }

            Vector2 screenError = cursorPos - anchorScreenPos;
            panLon += -screenError.x * anchorDegreesPerPixelX * panDragSpeed;
            panLat += -screenError.y * anchorDegreesPerPixelY * panDragSpeed;
        }
        else if (middleMouseHeld && cursorOverMap && dragPan.sqrMagnitude > 0.0f && mapRenderer != null)
        {
            Vector2 prevCursorPos = cursorPos - dragPan;
            if (TryGetUVAtScreen(cursorPos, out Vector2 uvNow) &&
                TryGetUVAtScreen(prevCursorPos, out Vector2 uvPrev))
            {
                float lonNow = (uvNow.x - 0.5f) * 360f;
                float lonPrev = (uvPrev.x - 0.5f) * 360f;
                float latNow = (uvNow.y - 0.5f) * 180f;
                float latPrev = (uvPrev.y - 0.5f) * 180f;

                float dLon = Mathf.DeltaAngle(lonPrev, lonNow);
                float dLat = latNow - latPrev;

                // Move the map opposite the cursor delta to keep the grabbed point under the cursor.
                panLon += -dLon * panDragSpeed;
                panLat += -dLat * panDragSpeed;
            }
            else
            {
                // Fallback to center-lat scaling if UV lookup fails.
                panLon += -dragPan.x * degreesPerPixelX * panDragSpeed;
                panLat += -dragPan.y * degreesPerPixelY * panDragSpeed;
            }
        }

        Vector2 rotateDelta = input.Map.Rotate.ReadValue<Vector2>();
        bool rmbHeld = input.Map.RMB.IsPressed();
        if (rmbHeld)
        {
            orbitYawDeg += rotateDelta.x * rotateSensitivity;
            orbitPitchDeg += rotateDelta.y * rotateSensitivity;
            orbitYawDeg = Mathf.Clamp(orbitYawDeg, minYawDeg, maxYawDeg);
            orbitPitchDeg = Mathf.Clamp(orbitPitchDeg, minPitchDeg, maxPitchDeg);
        }
        else
        {
            float step = returnToDefaultSpeed * Time.deltaTime;
            orbitYawDeg = Mathf.MoveTowardsAngle(orbitYawDeg, 0f, step);
            orbitPitchDeg = Mathf.MoveTowards(orbitPitchDeg, 0f, step);
            orbitYawDeg = Mathf.Clamp(orbitYawDeg, minYawDeg, maxYawDeg);
        }

        focusLon = Mathf.Repeat(focusLon + panLon, 360f);

        float newCameraLat = cameraLat + panLat;
        if (Mathf.Abs(currentZoom - lastZoomForLimits) > 0.01f)
        {
            (cachedMinLatLimit, cachedMaxLatLimit) = CalculateLatitudeLimitsFromFOV();
            lastZoomForLimits = currentZoom;
        }

        cameraLat = Mathf.Clamp(newCameraLat, cachedMinLatLimit, cachedMaxLatLimit);

        PositionCamera();
        UpdateUVOffset();
    }

    void UpdateUVOffset()
    {
        if (mapMat == null) return;

        float uvOffsetX = focusLon / 360f;
        float uvOffsetY = 0f;

        mapMat.SetVector("_UVOffset", new Vector2(uvOffsetX, uvOffsetY));

    }

    void OnValidate()
    {
        if (cam == null)
        {
            cam = GetComponent<Camera>();
        }

        nearBufferElevationMultiplier = Mathf.Max(0f, nearBufferElevationMultiplier);
        nearBufferExtraUnits = Mathf.Max(0f, nearBufferExtraUnits);
        zoomSpeed = Mathf.Max(0.0001f, zoomSpeed);
        minZoomSpeedScale = Mathf.Max(0.01f, minZoomSpeedScale);
        maxZoomSpeedScale = Mathf.Max(minZoomSpeedScale, maxZoomSpeedScale);

        UpdateDerivedRadius();
        UpdateMapDimensions();
        SyncRendererBounds();
        if (cam != null)
        {
            CalculateZoomLimits();
        }

        if (currentZoom <= 0f || !Application.isPlaying)
        {
            currentZoom = baseDistance;
        }

        if (maxZoom > minZoom)
        {
            currentZoom = Mathf.Clamp(currentZoom, minZoom, maxZoom);
        }
        RefreshProjectionState();

        if (cam != null)
        {
            PositionCamera();
            UpdateUVOffset();
            CacheCameraShape();
        }
    }

    void UpdateZoom()
    {
        float scroll = input.Map.Zoom.ReadValue<float>();
        if (Mathf.Abs(scroll) < 0.001f)
        {
            return;
        }

        float zoomFactor = Mathf.Exp(-scroll * GetZoomExponent());
        currentZoom = Mathf.Clamp(currentZoom * zoomFactor, minZoom, maxZoom);
    }

    float GetZoomExponent()
    {
        float exponent = zoomSpeed;
        if (!dynamicZoomSpeed)
        {
            return exponent;
        }

        float safeMinZoom = Mathf.Max(0.01f, minZoom);
        float safeMaxZoom = Mathf.Max(safeMinZoom + 0.01f, maxZoom);
        float referenceZoom = Mathf.Max(safeMinZoom, currentZoom);
        float logMin = Mathf.Log10(safeMinZoom);
        float logMax = Mathf.Log10(safeMaxZoom);
        float logReference = Mathf.Log10(referenceZoom);
        float t = Mathf.InverseLerp(logMin, logMax, logReference);

        return exponent * Mathf.Lerp(minZoomSpeedScale, maxZoomSpeedScale, t);
    }

    bool CameraShapeChanged()
    {
        return lastScreenWidth != Screen.width ||
               lastScreenHeight != Screen.height ||
               !Mathf.Approximately(lastFieldOfView, cam.fieldOfView);
    }

    void CacheCameraShape()
    {
        lastScreenWidth = Screen.width;
        lastScreenHeight = Screen.height;
        lastFieldOfView = cam.fieldOfView;
    }

    // Public read-only state for systems that need to stay in sync with map projection/camera state.
    public Material MapMaterial => mapMat;
    public float RadiusUnits => radius;
    public float KmPerUnit => kmPerUnit;
    public float HeightMinKm => heightMinKm;
    public float HeightMaxKm => heightMaxKm;
    public float HeightExaggeration => heightExaggeration;
    public float CurrentMorph => currentMorph;
    public float CurrentZoom => currentZoom;
    public bool SphereMode => sphereMode;
    public float FocusLongitudeDeg => GetFocusLongitudeDeg();
    public float CameraLatitudeDeg => cameraLat;
    public float OrbitYawDeg => orbitYawDeg;
    public float OrbitPitchDeg => orbitPitchDeg;
    public Camera ControlledCamera => cam != null ? cam : GetComponent<Camera>();
    public Vector2 CurrentUvOffset => new Vector2(focusLon / 360f, 0f);
    public Quaternion GetBaseCameraLookRotation()
    {
        float lonDeg = GetFocusLongitudeDeg();
        Vector3 surfaceNormal = CalculateSurfaceNormalAtLatLon(cameraLat, lonDeg);
        Vector3 forward = -surfaceNormal;
        if (forward.sqrMagnitude < 1e-6f)
        {
            forward = transform.forward;
        }

        return Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    public bool TryGetLatLonAtScreen(Vector2 screenPos, out float latitudeDeg, out float longitudeDeg)
    {
        latitudeDeg = 0f;
        longitudeDeg = 0f;
        if (!TryGetUVAtScreen(screenPos, out Vector2 uv))
        {
            return false;
        }

        longitudeDeg = (uv.x - 0.5f) * 360f;
        latitudeDeg = (uv.y - 0.5f) * 180f;
        return true;
    }

    public MapCesiumTransitionViewState CaptureTransitionViewState()
    {
        if (cam == null)
        {
            cam = GetComponent<Camera>();
        }

        GetFallbackPanDegreesPerPixel(out float degreesPerPixelX, out float degreesPerPixelY);
        TryGetPanDegreesPerPixelAtScreen(
            new Vector2(Screen.width * 0.5f, Screen.height * 0.5f),
            out degreesPerPixelX,
            out degreesPerPixelY);

        return new MapCesiumTransitionViewState
        {
            isValid = cam != null,
            focusLongitudeDeg = GetFocusLongitudeDeg(),
            focusLatitudeDeg = cameraLat,
            focusHeightMeters = 0.0,
            orbitYawDeg = orbitYawDeg,
            orbitPitchDeg = orbitPitchDeg,
            fieldOfViewDeg = cam != null ? cam.fieldOfView : 60f,
            visibleLongitudeSpanDeg = Mathf.Abs(degreesPerPixelX) * Mathf.Max(1, Screen.width),
            visibleLatitudeSpanDeg = Mathf.Abs(degreesPerPixelY) * Mathf.Max(1, Screen.height)
        };
    }

    public void ApplyTransitionViewState(MapCesiumTransitionViewState state)
    {
        if (!state.isValid)
        {
            return;
        }

        if (cam == null)
        {
            cam = GetComponent<Camera>();
        }

        if (cam == null)
        {
            return;
        }

        cam.fieldOfView = Mathf.Clamp(state.fieldOfViewDeg, 1f, 179f);
        CalculateZoomLimits();

        focusLon = Mathf.Repeat((float)state.focusLongitudeDeg, 360f);
        cameraLat = Mathf.Clamp((float)state.focusLatitudeDeg, -89.9f, 89.9f);
        orbitYawDeg = Mathf.Clamp(state.orbitYawDeg, minYawDeg, maxYawDeg);
        orbitPitchDeg = Mathf.Clamp(state.orbitPitchDeg, minPitchDeg, maxPitchDeg);

        currentZoom = CalculateZoomFromVisibleSpans(
            state.visibleLongitudeSpanDeg,
            state.visibleLatitudeSpanDeg);
        currentZoom = Mathf.Clamp(currentZoom, minZoom, maxZoom);

        RefreshProjectionState();
        (cachedMinLatLimit, cachedMaxLatLimit) = CalculateLatitudeLimitsFromFOV();
        lastZoomForLimits = currentZoom;
        cameraLat = Mathf.Clamp(cameraLat, cachedMinLatLimit, cachedMaxLatLimit);

        PositionCamera();
        UpdateUVOffset();
    }

    void RefreshProjectionState()
    {
        SyncCameraFarClip();

        if (enableZoomMorph && maxZoom > minZoom)
        {
            float zoomRange = maxZoom - minZoom;
            float normalizedZoom = (maxZoom - currentZoom) / zoomRange;
            float linear = Mathf.Clamp01(normalizedZoom);
            float cubic = linear * linear * linear;
            float t = useCubicMorph ? cubic : linear;
            currentMorph = Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(t));
        }
        else
        {
            currentMorph = 0.5f;
        }

        if (autoSphereFromZoom)
        {
            float switchZoom = GetSphereSwitchZoom();
            sphereMode = currentZoom <= switchZoom;
        }

        if (mapMat == null)
        {
            return;
        }

        SyncMaterialConstants();
        mapMat.SetFloat("_Morph", currentMorph);
        mapMat.SetFloat("_Sphere", sphereMode ? 1f : 0f);
    }

    void GetFallbackPanDegreesPerPixel(out float degreesPerPixelX, out float degreesPerPixelY)
    {
        float verticalFovRad = cam.fieldOfView * Mathf.Deg2Rad;
        float horizontalFovRad = 2f * Mathf.Atan(Mathf.Tan(verticalFovRad * 0.5f) * cam.aspect);
        float worldUnitsPerPixelX = (2f * currentZoom * Mathf.Tan(horizontalFovRad * 0.5f)) / Mathf.Max(1, Screen.width);
        float worldUnitsPerPixelY = (2f * currentZoom * Mathf.Tan(verticalFovRad * 0.5f)) / Mathf.Max(1, Screen.height);

        degreesPerPixelY = (worldUnitsPerPixelY / mapHeight) * 180f;
        float cosLat = Mathf.Cos(cameraLat * Mathf.Deg2Rad);
        float widthFactor = Mathf.Lerp(1f, cosLat, currentMorph * 0.5f);
        widthFactor = Mathf.Max(0.01f, widthFactor);
        degreesPerPixelX = (worldUnitsPerPixelX / (mapWidth * widthFactor)) * 360f;
    }

    void GetCurrentVisibleSpans(out float longitudeSpanDeg, out float latitudeSpanDeg)
    {
        GetFallbackPanDegreesPerPixel(out float degreesPerPixelX, out float degreesPerPixelY);
        TryGetPanDegreesPerPixelAtScreen(
            new Vector2(Screen.width * 0.5f, Screen.height * 0.5f),
            out degreesPerPixelX,
            out degreesPerPixelY);

        longitudeSpanDeg = Mathf.Abs(degreesPerPixelX) * Mathf.Max(1, Screen.width);
        latitudeSpanDeg = Mathf.Abs(degreesPerPixelY) * Mathf.Max(1, Screen.height);
    }

    float CalculateZoomFromVisibleSpans(float longitudeSpanDeg, float latitudeSpanDeg)
    {
        float targetLongitudeSpan = Mathf.Max(0.01f, longitudeSpanDeg);
        float targetLatitudeSpan = Mathf.Max(0.01f, latitudeSpanDeg);

        float originalZoom = currentZoom;
        float bestZoom = Mathf.Clamp(originalZoom, minZoom, maxZoom);
        float low = minZoom;
        float high = maxZoom;

        currentZoom = high;
        RefreshProjectionState();
        PositionCamera();
        UpdateUVOffset();
        GetCurrentVisibleSpans(out float maxLongitudeSpan, out float maxLatitudeSpan);
        if (maxLongitudeSpan < targetLongitudeSpan || maxLatitudeSpan < targetLatitudeSpan)
        {
            currentZoom = bestZoom;
            RefreshProjectionState();
            PositionCamera();
            UpdateUVOffset();
            return high;
        }

        for (int i = 0; i < 20; i++)
        {
            float mid = 0.5f * (low + high);
            currentZoom = mid;
            RefreshProjectionState();
            PositionCamera();
            UpdateUVOffset();
            GetCurrentVisibleSpans(out float measuredLongitudeSpan, out float measuredLatitudeSpan);

            bool fitsTarget = measuredLongitudeSpan >= targetLongitudeSpan &&
                              measuredLatitudeSpan >= targetLatitudeSpan;
            if (fitsTarget)
            {
                bestZoom = mid;
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        currentZoom = bestZoom;
        RefreshProjectionState();
        PositionCamera();
        UpdateUVOffset();
        return bestZoom;
    }

    void TryBeginPanAnchor(Vector2 screenPos)
    {
        hasPanAnchor = TryGetUVAtScreen(screenPos, out panAnchorUv);
    }

    bool TryGetScreenPositionForUV(Vector2 uv, out Vector2 screenPos)
    {
        screenPos = default;
        if (mapRenderer == null || cam == null) return false;

        Vector3 localPoint;
        if (sphereMode)
        {
            float lon = (uv.x - 0.5f) * 2f * Mathf.PI;
            float lat = (uv.y - 0.5f) * Mathf.PI;
            float cosLat = Mathf.Cos(lat);
            localPoint = new Vector3(
                cosLat * Mathf.Cos(lon) * radius,
                Mathf.Sin(lat) * radius,
                cosLat * Mathf.Sin(lon) * radius
            );
        }
        else
        {
            float geoLonDeg = (uv.x - 0.5f) * 360f;
            float lon = Mathf.DeltaAngle(GetFocusLongitudeDeg(), geoLonDeg) * Mathf.Deg2Rad;
            float lat = (uv.y - 0.5f) * Mathf.PI;
            Vector2 projectedPoint = ProjectAitoffBlended(lat, lon, currentMorph);
            localPoint = new Vector3(projectedPoint.x, projectedPoint.y, 0f);
        }

        Vector3 worldPoint = mapRenderer.transform.TransformPoint(localPoint);
        Vector3 projected = cam.WorldToScreenPoint(worldPoint);
        if (projected.z <= 0f) return false;

        screenPos = new Vector2(projected.x, projected.y);
        return true;
    }

    bool TryGetUVAtScreen(Vector2 screenPos, out Vector2 uv)
    {
        uv = default;
        if (mapRenderer == null || cam == null) return false;

        Ray screenRay = cam.ScreenPointToRay(new Vector3(screenPos.x, screenPos.y, 0f));
        Transform rendererTransform = mapRenderer.transform;
        Vector3 rayOriginOS = rendererTransform.InverseTransformPoint(screenRay.origin);
        Vector3 rayDirOS = rendererTransform.InverseTransformDirection(screenRay.direction).normalized;

        if (sphereMode)
        {
            // Ray-sphere intersection in object space (sphere centered at origin).
            float bTerm = Vector3.Dot(rayOriginOS, rayDirOS);
            float cTerm = Vector3.Dot(rayOriginOS, rayOriginOS) - radius * radius;
            float discriminant = bTerm * bTerm - cTerm;
            if (discriminant < 0f) return false;
            float sphereHitDistance = -bTerm - Mathf.Sqrt(discriminant);
            if (sphereHitDistance <= 0f) sphereHitDistance = -bTerm + Mathf.Sqrt(discriminant);
            if (sphereHitDistance <= 0f) return false;

            Vector3 sphereHitPoint = rayOriginOS + rayDirOS * sphereHitDistance;
            float lon = Mathf.Atan2(sphereHitPoint.z, sphereHitPoint.x);
            float lat = Mathf.Asin(Mathf.Clamp(sphereHitPoint.y / radius, -1f, 1f));

            float u = (lon / (2f * Mathf.PI)) + 0.5f;
            float v = (lat / Mathf.PI) + 0.5f;
            uv = new Vector2(u - Mathf.Floor(u), v);
            return true;
        }

        const float EPS = 1e-6f;
        if (Mathf.Abs(rayDirOS.z) < EPS) return false;
        float planeHitDistance = -rayOriginOS.z / rayDirOS.z;
        if (planeHitDistance <= 0f) return false;

        Vector3 planeHitPoint = rayOriginOS + rayDirOS * planeHitDistance;

        if (!TryProjectUVFromAitoff(planeHitPoint, out uv))
        {
            return false;
        }

        uv.x = Mathf.Repeat(uv.x + (focusLon / 360f), 1f);
        return true;
    }

    bool TryGetPanDegreesPerPixelAtScreen(Vector2 screenPos, out float degreesPerPixelX, out float degreesPerPixelY)
    {
        degreesPerPixelX = 0f;
        degreesPerPixelY = 0f;
        if (cam == null) return false;

        const float sampleStepPixels = 12f;
        Vector2 right = screenPos + new Vector2(sampleStepPixels, 0f);
        Vector2 up = screenPos + new Vector2(0f, sampleStepPixels);

        if (!TryGetUVAtScreen(screenPos, out Vector2 uvCenter))
        {
            return false;
        }

        bool hasRight = TryGetUVAtScreen(right, out Vector2 uvRight);
        bool hasUp = TryGetUVAtScreen(up, out Vector2 uvUp);
        if (!hasRight || !hasUp)
        {
            return false;
        }

        float lonCenter = (uvCenter.x - 0.5f) * 360f;
        float latCenter = (uvCenter.y - 0.5f) * 180f;
        float lonRight = (uvRight.x - 0.5f) * 360f;
        float latUp = (uvUp.y - 0.5f) * 180f;

        degreesPerPixelX = Mathf.DeltaAngle(lonCenter, lonRight) / sampleStepPixels;
        degreesPerPixelY = (latUp - latCenter) / sampleStepPixels;
        return true;
    }

    bool TryProjectUVFromAitoff(Vector3 projectedPoint, out Vector2 uv)
    {
        uv = default;
        if (!TryInverseAitoffBlended(new Vector2(projectedPoint.x, projectedPoint.y), currentMorph, out float lat, out float lon))
        {
            return false;
        }

        float v = (lat / Mathf.PI) + 0.5f;
        if (v < 0f || v > 1f) return false;

        float u = (lon / (2f * Mathf.PI)) + 0.5f;
        uv = new Vector2(u - Mathf.Floor(u), v);
        return true;
    }

    bool TryInverseAitoffBlended(Vector2 targetXY, float morph, out float latitude, out float longitude)
    {
        latitude = Mathf.Clamp(targetXY.y / radius, -Mathf.PI * 0.5f, Mathf.PI * 0.5f);
        longitude = Mathf.Clamp(targetXY.x / radius, -Mathf.PI, Mathf.PI);

        const float stepEps = 1e-4f;
        // Residual is in world units; scale tolerance with radius so convergence remains stable at large map scales.
        float tolerance = Mathf.Max(1e-4f, radius * 1e-6f);
        for (int i = 0; i < 12; i++)
        {
            Vector2 residual = ProjectAitoffBlended(latitude, longitude, morph) - targetXY;
            if (residual.sqrMagnitude < tolerance * tolerance)
            {
                return true;
            }

            Vector2 residualLatPlus = ProjectAitoffBlended(latitude + stepEps, longitude, morph);
            Vector2 residualLatMinus = ProjectAitoffBlended(latitude - stepEps, longitude, morph);
            Vector2 residualLonPlus = ProjectAitoffBlended(latitude, longitude + stepEps, morph);
            Vector2 residualLonMinus = ProjectAitoffBlended(latitude, longitude - stepEps, morph);

            Vector2 dResidual_dLat = (residualLatPlus - residualLatMinus) * (0.5f / stepEps);
            Vector2 dResidual_dLon = (residualLonPlus - residualLonMinus) * (0.5f / stepEps);

            float determinant = dResidual_dLat.x * dResidual_dLon.y - dResidual_dLat.y * dResidual_dLon.x;
            if (Mathf.Abs(determinant) < 1e-6f)
            {
                break;
            }

            float invDeterminant = 1f / determinant;
            float deltaLatitude = (-residual.x * dResidual_dLon.y + residual.y * dResidual_dLon.x) * invDeterminant;
            float deltaLongitude = (-dResidual_dLat.x * residual.y + dResidual_dLat.y * residual.x) * invDeterminant;

            latitude = Mathf.Clamp(latitude + deltaLatitude, -Mathf.PI * 0.5f, Mathf.PI * 0.5f);
            longitude = Mathf.Repeat(longitude + deltaLongitude + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
        }

        Vector2 finalResidual = ProjectAitoffBlended(latitude, longitude, morph) - targetXY;
        return finalResidual.sqrMagnitude < tolerance * tolerance;
    }

    float GetProjectionWidthAtMorph(float morph)
    {
        Vector2 atEdge = ProjectAitoffBlended(0f, Mathf.PI, Mathf.Clamp01(morph));
        return Mathf.Abs(atEdge.x) * 2f;
    }

    Vector2 ProjectAitoffBlended(float latitude, float longitude, float morph)
    {
        float cosStandardParallel = 2f / Mathf.PI; // Winkel Tripel standard parallel
        Vector2 equirectangular = new Vector2(longitude * radius * cosStandardParallel, latitude * radius);
        Vector2 aitoffProjected = ProjectAitoff(latitude, longitude);
        return Vector2.Lerp(equirectangular, aitoffProjected, Mathf.Clamp01(morph));
    }

    Vector2 ProjectAitoff(float latitude, float longitude)
    {
        float halfLongitude = 0.5f * longitude;
        float cosLatitude = Mathf.Cos(latitude);
        float sinLatitude = Mathf.Sin(latitude);
        float cosHalfLongitude = Mathf.Cos(halfLongitude);
        float sinHalfLongitude = Mathf.Sin(halfLongitude);
        float alphaAngle = Mathf.Acos(Mathf.Clamp(cosLatitude * cosHalfLongitude, -1f, 1f));
        float sinAlpha = Mathf.Sin(alphaAngle);
        float invSincAlpha = Mathf.Abs(alphaAngle) < 1e-6f ? 1f : (alphaAngle / sinAlpha);

        float x = 2f * cosLatitude * sinHalfLongitude * invSincAlpha * radius;
        float y = sinLatitude * invSincAlpha * radius;
        return new Vector2(x, y);
    }
}
