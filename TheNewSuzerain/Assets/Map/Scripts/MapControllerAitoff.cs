using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Camera))]
public class MapControllerAitoff : MonoBehaviour
{
    [Header("Scene References")]
    [SerializeField] Material mapMat;
    [SerializeField] Renderer mapRenderer; // mesh renderer for bounds calculation

    [Header("World Geometry")]
    [SerializeField] float radius = 100f; // must match shader

    [Header("Zoom")]
    [SerializeField] float zoomSpeed = 6f;
    [SerializeField] float zoomInBuffer = 0.01f;
    [SerializeField] bool useRendererBoundsForZoom = true; // fit actual mesh width at startup

    [Header("Panning")]
    [SerializeField] float panKeySpeed = 60f;
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
    [SerializeField, Tooltip("Quadratic morph vs zoom when enabled.")]
    bool useQuadraticMorph = false;

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

    // Cached latitude limits
    float cachedMinLatLimit = -90f;
    float cachedMaxLatLimit = 90f;
    float lastZoomForLimits = -1f;

    void Awake()
    {
        cam = GetComponent<Camera>();
        input = new InputSystem_Actions();

        UpdateMapDimensions();

        CalculateZoomLimits();

        currentZoom = baseDistance;
        cameraLat = 0f;

        transform.localRotation = Quaternion.identity;

        PositionCamera();
        SetupFlatMap();
    }

    void OnEnable() => input.Enable();
    void OnDisable() => input.Disable();

    void CalculateZoomLimits()
    {
        float vFOV = cam.fieldOfView * Mathf.Deg2Rad;
        float hFOV = 2f * Mathf.Atan(Mathf.Tan(vFOV * 0.5f) * cam.aspect);

        float fitWidth = mapWidth;
        if (useRendererBoundsForZoom && mapRenderer != null)
        {
            fitWidth = mapRenderer.bounds.size.x;
        }

        float horizontalDistance = (fitWidth * 0.5f) / Mathf.Tan(hFOV * 0.5f);

        baseDistance = horizontalDistance;
        maxZoom = horizontalDistance;
        minZoom = radius * zoomInBuffer;
    }

    void UpdateMapDimensions()
    {
        mapWidth = 4f * radius;
        mapHeight = 2f * radius;
    }

    void PositionCamera()
    {
        Vector3 pivot = CalculateSurfacePositionAtLatitude(cameraLat);
        Vector3 surfaceNormal = CalculateSurfaceNormalAtLatitude(cameraLat);

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

    Vector3 CalculateSurfacePositionAtLatitude(float latitudeDegrees)
    {
        float latitudeRad = latitudeDegrees * Mathf.Deg2Rad;
        return new Vector3(0f, latitudeRad * radius, 0f);
    }

    Vector3 CalculateSurfaceNormalAtLatitude(float latitudeDegrees)
    {
        return Vector3.back;
    }

    (float, float) CalculateLatitudeLimitsFromFOV()
    {
        float vFOV = cam.fieldOfView * Mathf.Deg2Rad;
        float halfFOV = vFOV * 0.5f;

        float minLat = 0f;
        float maxLat = 90f;

        for (int i = 0; i < 12; i++)
        {
            float testLat = (minLat + maxLat) * 0.5f;

            Vector3 cameraPos = CalculateSurfacePositionAtLatitude(testLat) +
                               CalculateSurfaceNormalAtLatitude(testLat) * currentZoom;

            Vector3 cameraForward = -CalculateSurfaceNormalAtLatitude(testLat);
            Vector3 cameraUp = Vector3.up;
            cameraUp = Vector3.Cross(Vector3.Cross(cameraForward, cameraUp), cameraForward).normalized;

            Vector3 northPolePos = CalculateSurfacePositionAtLatitude(90f);
            Vector3 toNorthPole = northPolePos - cameraPos;

            float forwardDistance = Vector3.Dot(toNorthPole, cameraForward);
            float upDistance = Vector3.Dot(toNorthPole, cameraUp);

            float angleToNorthPole = Mathf.Atan2(upDistance, forwardDistance);

            if (angleToNorthPole >= 0f && angleToNorthPole < halfFOV)
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
        float scroll = input.Map.Zoom.ReadValue<float>();
        currentZoom = Mathf.Clamp(currentZoom - scroll * zoomSpeed, minZoom, maxZoom);

        if (mapMat != null)
        {
            if (enableZoomMorph)
            {
                float zoomRange = maxZoom - minZoom;
                if (zoomRange > 0f)
                {
                    float normalizedZoom = (maxZoom - currentZoom) / zoomRange;
                    float linear = Mathf.Clamp01(normalizedZoom);
                    float quadratic = linear * linear;
                    currentMorph = useQuadraticMorph ? quadratic : Mathf.Clamp01(linear);
                }
            }

            mapMat.SetFloat("_Morph", currentMorph);
        }

        Vector2 moveKeys = input.Map.Move.ReadValue<Vector2>();
        Vector2 dragPan = input.Map.DragPan.ReadValue<Vector2>();
        Vector2 cursorPos = input.Map.Point.ReadValue<Vector2>();

        float vFOV = cam.fieldOfView * Mathf.Deg2Rad;
        float hFOV = 2f * Mathf.Atan(Mathf.Tan(vFOV * 0.5f) * cam.aspect);
        float worldUnitsPerPixelX = (2f * currentZoom * Mathf.Tan(hFOV * 0.5f)) / Screen.width;
        float worldUnitsPerPixelY = (2f * currentZoom * Mathf.Tan(vFOV * 0.5f)) / Screen.height;

        float degreesPerPixelY = (worldUnitsPerPixelY / mapHeight) * 180f;

        float panLon = moveKeys.x * panKeySpeed * Time.deltaTime;
        float panLat = moveKeys.y * panKeySpeed * Time.deltaTime;

        if (dragPan.sqrMagnitude > 0.0f && mapRenderer != null)
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
                float cosLat = Mathf.Cos(cameraLat * Mathf.Deg2Rad);
                float widthFactor = Mathf.Lerp(1f, cosLat, currentMorph * 0.5f);
                widthFactor = Mathf.Max(0.01f, widthFactor);
                float degreesPerPixelX = (worldUnitsPerPixelX / (mapWidth * widthFactor)) * 360f;

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

        float heightLod = 0.0f;
        mapMat.SetFloat("_HeightLod", heightLod);
    }

    void OnValidate()
    {
        if (Application.isPlaying && cam != null)
        {
            UpdateMapDimensions();
            CalculateZoomLimits();
            currentZoom = Mathf.Clamp(currentZoom, minZoom, maxZoom);

            if (enableZoomMorph && maxZoom > minZoom)
            {
                float zoomRange = maxZoom - minZoom;
                float normalizedZoom = (maxZoom - currentZoom) / zoomRange;
                float linear = Mathf.Clamp01(normalizedZoom);
                float quadratic = linear * linear;
                currentMorph = useQuadraticMorph ? quadratic : Mathf.Clamp01(linear);
            }

            if (mapMat != null)
            {
                mapMat.SetFloat("_Morph", currentMorph);
            }

            PositionCamera();
            UpdateUVOffset();
        }
    }

    bool TryGetUVAtScreen(Vector2 screenPos, out Vector2 uv)
    {
        uv = default;
        if (mapRenderer == null || cam == null) return false;

        Ray sRay = cam.ScreenPointToRay(new Vector3(screenPos.x, screenPos.y, 0f));
        Transform tr = mapRenderer.transform;
        Vector3 ro = tr.InverseTransformPoint(sRay.origin);
        Vector3 rd = tr.InverseTransformDirection(sRay.direction).normalized;

        const float EPS = 1e-6f;
        if (Mathf.Abs(rd.z) < EPS) return false;
        float t = -ro.z / rd.z;
        if (t <= 0f) return false;

        Vector3 p = ro + rd * t;

        return TryProjectUVFromAitoff(p, out uv);
    }

    bool TryProjectUVFromAitoff(Vector3 p, out Vector2 uv)
    {
        uv = default;
        if (!TryInverseAitoffBlended(new Vector2(p.x, p.y), currentMorph, out float lat, out float lon))
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

        const float eps = 1e-4f;
        const float tolerance = 1e-4f;
        for (int i = 0; i < 8; i++)
        {
            Vector2 f = ProjectAitoffBlended(latitude, longitude, morph) - targetXY;
            if (f.sqrMagnitude < tolerance * tolerance)
            {
                return true;
            }

            Vector2 fLatPlus = ProjectAitoffBlended(latitude + eps, longitude, morph);
            Vector2 fLatMinus = ProjectAitoffBlended(latitude - eps, longitude, morph);
            Vector2 fLonPlus = ProjectAitoffBlended(latitude, longitude + eps, morph);
            Vector2 fLonMinus = ProjectAitoffBlended(latitude, longitude - eps, morph);

            Vector2 dF_dLat = (fLatPlus - fLatMinus) * (0.5f / eps);
            Vector2 dF_dLon = (fLonPlus - fLonMinus) * (0.5f / eps);

            float det = dF_dLat.x * dF_dLon.y - dF_dLat.y * dF_dLon.x;
            if (Mathf.Abs(det) < 1e-6f)
            {
                break;
            }

            float invDet = 1f / det;
            float deltaLat = (-f.x * dF_dLon.y + f.y * dF_dLon.x) * invDet;
            float deltaLon = (-dF_dLat.x * f.y + dF_dLat.y * f.x) * invDet;

            latitude = Mathf.Clamp(latitude + deltaLat, -Mathf.PI * 0.5f, Mathf.PI * 0.5f);
            longitude = Mathf.Repeat(longitude + deltaLon + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
        }

        Vector2 finalError = ProjectAitoffBlended(latitude, longitude, morph) - targetXY;
        return finalError.sqrMagnitude < tolerance * tolerance;
    }

    Vector2 ProjectAitoffBlended(float latitude, float longitude, float morph)
    {
        Vector2 equirect = new Vector2(longitude * radius, latitude * radius);
        Vector2 aitoff = ProjectAitoff(latitude, longitude);
        return Vector2.Lerp(equirect, aitoff, Mathf.Clamp01(morph));
    }

    Vector2 ProjectAitoff(float latitude, float longitude)
    {
        float halfLon = 0.5f * longitude;
        float cosLat = Mathf.Cos(latitude);
        float sinLat = Mathf.Sin(latitude);
        float cosHalfLon = Mathf.Cos(halfLon);
        float sinHalfLon = Mathf.Sin(halfLon);
        float alpha = Mathf.Acos(Mathf.Clamp(cosLat * cosHalfLon, -1f, 1f));
        float sinAlpha = Mathf.Sin(alpha);
        float invSinc = Mathf.Abs(alpha) < 1e-6f ? 1f : (alpha / sinAlpha);

        float x = 2f * cosLat * sinHalfLon * invSinc * radius;
        float y = sinLat * invSinc * radius;
        return new Vector2(x, y);
    }
}
