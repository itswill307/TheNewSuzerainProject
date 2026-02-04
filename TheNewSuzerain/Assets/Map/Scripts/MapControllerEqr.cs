using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Camera))]
[ExecuteAlways]
public class MapControllerEqr : MonoBehaviour
{
    [Header("Scene References")]
    [SerializeField] Material mapMat;
    [SerializeField] Renderer mapRenderer; // mesh renderer for bounds calculation

    [Header("World Geometry")]
    [SerializeField] float radius = 100f; // must match shader

    [Header("Zoom")]
    [SerializeField] float zoomSpeed = 15f;
    [SerializeField] float zoomInBuffer = 0.01f;

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

    // Cached latitude limits
    float cachedMinLatLimit = -90f;
    float cachedMaxLatLimit = 90f;
    float lastZoomForLimits = -1f;

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (input == null)
        {
            input = new InputSystem_Actions();
        }

        UpdateMapDimensions();

        CalculateZoomLimits();

        currentZoom = baseDistance;
        cameraLat = 0f;

        transform.localRotation = Quaternion.identity;

        PositionCamera();
        SetupFlatMap();
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
        float nearBuffer = cam.nearClipPlane + (radius * 0.01f);
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

        float scroll = input.Map.Zoom.ReadValue<float>();
        float zoomT = Mathf.InverseLerp(minZoom, maxZoom, currentZoom);
        float speedScale = Mathf.Lerp(0.2f, 1f, Mathf.Pow(Mathf.Clamp01(zoomT), 1f / 3f));
        currentZoom = Mathf.Clamp(currentZoom - scroll * zoomSpeed * speedScale, minZoom, maxZoom);

        if (mapMat != null)
        {
            if (autoSphereFromZoom)
            {
                float switchZoom = GetSphereSwitchZoom();
                sphereMode = currentZoom <= switchZoom;
            }

            if (enableZoomMorph)
            {
                float zoomRange = maxZoom - minZoom;
                if (zoomRange > 0f)
                {
                    float normalizedZoom = (maxZoom - currentZoom) / zoomRange;
                    float linear = Mathf.Clamp01(normalizedZoom);
                    float cubic = linear * linear * linear;
                    float t = useCubicMorph ? cubic : Mathf.Clamp01(linear);
                    currentMorph = Mathf.Lerp(0.5f, 1f, t);
                }
            }
            else
            {
                currentMorph = 0.5f;
            }

            mapMat.SetFloat("_Morph", currentMorph);
            mapMat.SetFloat("_Sphere", sphereMode ? 1f : 0f);
        }

        Vector2 moveKeys = input.Map.Move.ReadValue<Vector2>();
        Vector2 dragPan = input.Map.DragPan.ReadValue<Vector2>();
        Vector2 cursorPos = input.Map.Point.ReadValue<Vector2>();

        float verticalFovRad = cam.fieldOfView * Mathf.Deg2Rad;
        float horizontalFovRad = 2f * Mathf.Atan(Mathf.Tan(verticalFovRad * 0.5f) * cam.aspect);
        float worldUnitsPerPixelX = (2f * currentZoom * Mathf.Tan(horizontalFovRad * 0.5f)) / Screen.width;
        float worldUnitsPerPixelY = (2f * currentZoom * Mathf.Tan(verticalFovRad * 0.5f)) / Screen.height;

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
        if (cam == null)
        {
            cam = GetComponent<Camera>();
        }

        UpdateMapDimensions();
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

        if (enableZoomMorph && maxZoom > minZoom)
        {
            float zoomRange = maxZoom - minZoom;
            float normalizedZoom = (maxZoom - currentZoom) / zoomRange;
            float linear = Mathf.Clamp01(normalizedZoom);
            float cubic = linear * linear * linear;
            float t = useCubicMorph ? cubic : Mathf.Clamp01(linear);
            currentMorph = Mathf.Lerp(0.5f, 1f, t);
        }
        else
        {
            currentMorph = 0.5f;
        }

        if (mapMat != null)
        {
            if (autoSphereFromZoom)
            {
                float switchZoom = GetSphereSwitchZoom();
                sphereMode = currentZoom <= switchZoom;
            }

            mapMat.SetFloat("_Morph", currentMorph);
            mapMat.SetFloat("_Sphere", sphereMode ? 1f : 0f);
        }

        if (cam != null)
        {
            PositionCamera();
            UpdateUVOffset();
        }
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

        return TryProjectUVFromAitoff(planeHitPoint, out uv);
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
        const float tolerance = 1e-4f;
        for (int i = 0; i < 8; i++)
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
