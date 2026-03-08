using CesiumForUnity;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Camera))]
[RequireComponent(typeof(CesiumGlobeAnchor))]
[DisallowMultipleComponent]
public class CesiumCameraController : MonoBehaviour
{
    [Header("Cesium")]
    [SerializeField] CesiumGeoreference georeference;
    [SerializeField] bool initializeFromCurrentView = false;

    [Header("Focus")]
    [SerializeField] double focusLongitudeDeg = 0.0;
    [SerializeField] double focusLatitudeDeg = 0.0;
    [SerializeField] double focusHeightMeters = 0.0;
    [SerializeField] float minLatitudeDeg = -85f;
    [SerializeField] float maxLatitudeDeg = 85f;

    [Header("Zoom")]
    [SerializeField, Tooltip("Base exponential scroll sensitivity.")]
    float zoomSpeed = 0.012f;
    [SerializeField] bool dynamicZoomSpeed = true;
    [SerializeField] float minDynamicZoomAltitudeMeters = 200f;
    [SerializeField] float maxDynamicZoomAltitudeMeters = 3_000_000f;
    [SerializeField] float minZoomSpeedScale = 1.0f;
    [SerializeField] float maxZoomSpeedScale = 3.0f;
    [SerializeField] float minZoomDistanceMeters = 100f;
    [SerializeField] float zoomInBufferMeters = 25f;
    [SerializeField] float initialZoomDistanceMeters = 0f;

    [Header("Camera Clipping")]
    [SerializeField] bool autoFarClip = true;
    [SerializeField] bool autoNearClip = true;
    [SerializeField] float farClipPaddingMeters = 50_000f;
    [SerializeField] float minFarClip = 200_000f;
    [SerializeField] float maxFarClip = 50_000_000f;
    [SerializeField] float maxNearClip = 1_000f;
    [SerializeField] float maxNearToFarRatio = 100_000f;

    [Header("Panning")]
    [SerializeField, Tooltip("Keyboard pan speed in screen pixels per second.")]
    float panKeySpeed = 800f;
    [SerializeField] float panDragSpeed = 1f;

    [Header("Rotation")]
    [SerializeField, Tooltip("Degrees of tilt per pixel when rotating with RMB.")]
    float rotateSensitivity = 0.25f;
    [SerializeField] float minPitchDeg = -85f;
    [SerializeField] float maxPitchDeg = 85f;
    [SerializeField] float minYawDeg = -85f;
    [SerializeField] float maxYawDeg = 85f;
    [SerializeField] float returnToDefaultSpeed = 320f;

    Camera cam;
    CesiumGlobeAnchor globeAnchor;
    CesiumEllipsoid ellipsoid;
    InputSystem_Actions input;

    double3 ellipsoidRadii;
    double maxEllipsoidRadius;

    float currentDistanceMeters;
    float minZoomDistance;
    float maxZoomDistance;

    float orbitYawDeg;
    float orbitPitchDeg;
    bool wasMiddleMouseHeld;
    bool hasPanAnchor;
    double3 panAnchorEcef;

    int lastScreenWidth;
    int lastScreenHeight;
    float lastFieldOfView;
    float initialNearClipPlane;

    void Awake()
    {
        cam = GetComponent<Camera>();
        globeAnchor = GetComponent<CesiumGlobeAnchor>();
        georeference = georeference != null ? georeference : GetComponentInParent<CesiumGeoreference>();
        input ??= new InputSystem_Actions();
        initialNearClipPlane = cam.nearClipPlane;

        if (georeference == null)
        {
            Debug.LogError("CesiumCameraController requires a parent CesiumGeoreference.");
            enabled = false;
            return;
        }

        georeference.Initialize();
        ellipsoid = georeference.ellipsoid;
        ellipsoidRadii = ellipsoid.GetRadii();
        maxEllipsoidRadius = ellipsoid.GetMaximumRadius();

        globeAnchor.detectTransformChanges = false;
        globeAnchor.adjustOrientationForGlobeWhenMoving = false;

        if (initializeFromCurrentView && TryInitializeFromCurrentView())
        {
            UpdateZoomLimits();
            currentDistanceMeters = Mathf.Clamp(currentDistanceMeters, minZoomDistance, maxZoomDistance);
        }
        else
        {
            focusLongitudeDeg = WrapLongitude(focusLongitudeDeg);
            focusLatitudeDeg = math.clamp(focusLatitudeDeg, minLatitudeDeg, maxLatitudeDeg);
            focusHeightMeters = math.max(0.0, focusHeightMeters);
            UpdateZoomLimits();
            currentDistanceMeters = initialZoomDistanceMeters > 0f
                ? Mathf.Clamp(initialZoomDistanceMeters, minZoomDistance, maxZoomDistance)
                : Mathf.Clamp((float)GetWholeGlobeFillDistance(), minZoomDistance, maxZoomDistance);
        }

        CacheCameraShape();
        ApplyCameraPose();
    }

    void OnEnable()
    {
        input ??= new InputSystem_Actions();
        if (Application.isPlaying)
        {
            input.Enable();
        }
    }

    void OnDisable()
    {
        if (Application.isPlaying)
        {
            input?.Disable();
        }

        wasMiddleMouseHeld = false;
        hasPanAnchor = false;
    }

    void Update()
    {
        if (!Application.isPlaying || georeference == null || globeAnchor == null)
        {
            return;
        }

        if (CameraShapeChanged())
        {
            UpdateZoomLimits();
            currentDistanceMeters = Mathf.Clamp(currentDistanceMeters, minZoomDistance, maxZoomDistance);
        }

        UpdateZoom();
        UpdatePan();
        UpdateRotation();
        ApplyCameraPose();
    }

    void OnValidate()
    {
        minLatitudeDeg = Mathf.Clamp(minLatitudeDeg, -89.9f, 89.9f);
        maxLatitudeDeg = Mathf.Clamp(maxLatitudeDeg, minLatitudeDeg, 89.9f);
        minZoomDistanceMeters = Mathf.Max(0.01f, minZoomDistanceMeters);
        zoomInBufferMeters = Mathf.Max(0f, zoomInBufferMeters);
        zoomSpeed = Mathf.Max(0.0001f, zoomSpeed);
        minDynamicZoomAltitudeMeters = Mathf.Max(0.01f, minDynamicZoomAltitudeMeters);
        maxDynamicZoomAltitudeMeters = Mathf.Max(minDynamicZoomAltitudeMeters + 1f, maxDynamicZoomAltitudeMeters);
        minZoomSpeedScale = Mathf.Max(0.01f, minZoomSpeedScale);
        maxZoomSpeedScale = Mathf.Max(minZoomSpeedScale, maxZoomSpeedScale);
        panKeySpeed = Mathf.Max(0f, panKeySpeed);
        panDragSpeed = Mathf.Max(0f, panDragSpeed);
        rotateSensitivity = Mathf.Max(0f, rotateSensitivity);
        returnToDefaultSpeed = Mathf.Max(0f, returnToDefaultSpeed);
        farClipPaddingMeters = Mathf.Max(0f, farClipPaddingMeters);
        minFarClip = Mathf.Max(1000f, minFarClip);
        maxFarClip = Mathf.Max(minFarClip, maxFarClip);
        maxNearClip = Mathf.Max(0.01f, maxNearClip);
        maxNearToFarRatio = Mathf.Max(1f, maxNearToFarRatio);
    }

    void UpdateZoom()
    {
        float scroll = input.Map.Zoom.ReadValue<float>();
        if (Mathf.Abs(scroll) < 0.001f)
        {
            return;
        }

        float zoomFactor = Mathf.Exp(-scroll * GetZoomExponent());
        currentDistanceMeters = Mathf.Clamp(currentDistanceMeters * zoomFactor, minZoomDistance, maxZoomDistance);
    }

    void UpdatePan()
    {
        Vector2 moveKeys = input.Map.Move.ReadValue<Vector2>();
        Vector2 dragPan = input.Map.DragPan.ReadValue<Vector2>();
        Vector2 cursorPos = input.Map.Point.ReadValue<Vector2>();
        bool middleMouseHeld = Mouse.current?.middleButton.isPressed ?? false;
        bool cursorOverGlobe = TryIntersectEllipsoid(cam.ScreenPointToRay(cursorPos), out _);

        if (middleMouseHeld && !wasMiddleMouseHeld)
        {
            TryBeginPanAnchor(cursorPos);
        }
        else if (middleMouseHeld && !hasPanAnchor)
        {
            TryBeginPanAnchor(cursorPos);
        }
        else if (!middleMouseHeld)
        {
            hasPanAnchor = false;
        }

        wasMiddleMouseHeld = middleMouseHeld;

        GetFallbackPanDegreesPerPixel(out float degreesPerPixelX, out float degreesPerPixelY);
        TryGetPanDegreesPerPixelAtScreen(
            new Vector2(Screen.width * 0.5f, Screen.height * 0.5f),
            ref degreesPerPixelX,
            ref degreesPerPixelY);

        double panLon = moveKeys.x * panKeySpeed * degreesPerPixelX * Time.deltaTime;
        double panLat = moveKeys.y * panKeySpeed * degreesPerPixelY * Time.deltaTime;

        if (middleMouseHeld && cursorOverGlobe && hasPanAnchor && TryGetScreenPositionForEcef(panAnchorEcef, out Vector2 anchorScreenPos))
        {
            float anchorDegreesPerPixelX = degreesPerPixelX;
            float anchorDegreesPerPixelY = degreesPerPixelY;
            TryGetPanDegreesPerPixelAtScreen(anchorScreenPos, ref anchorDegreesPerPixelX, ref anchorDegreesPerPixelY);

            Vector2 screenError = cursorPos - anchorScreenPos;
            panLon += -screenError.x * anchorDegreesPerPixelX * panDragSpeed;
            panLat += -screenError.y * anchorDegreesPerPixelY * panDragSpeed;
        }
        else if (middleMouseHeld && cursorOverGlobe && dragPan.sqrMagnitude > 0f)
        {
            Vector2 prevCursorPos = cursorPos - dragPan;
            if (TryGetLongitudeLatitudeAtScreen(cursorPos, out double lonNow, out double latNow) &&
                TryGetLongitudeLatitudeAtScreen(prevCursorPos, out double lonPrev, out double latPrev))
            {
                panLon += -Mathf.DeltaAngle((float)lonPrev, (float)lonNow) * panDragSpeed;
                panLat += -(latNow - latPrev) * panDragSpeed;
            }
            else
            {
                panLon += -dragPan.x * degreesPerPixelX * panDragSpeed;
                panLat += -dragPan.y * degreesPerPixelY * panDragSpeed;
            }
        }

        focusLongitudeDeg = WrapLongitude(focusLongitudeDeg + panLon);
        focusLatitudeDeg = math.clamp(focusLatitudeDeg + panLat, minLatitudeDeg, maxLatitudeDeg);
    }

    void UpdateRotation()
    {
        Vector2 rotateDelta = input.Map.Rotate.ReadValue<Vector2>();
        bool rmbHeld = input.Map.RMB.IsPressed();

        if (rmbHeld)
        {
            orbitYawDeg = Mathf.Clamp(orbitYawDeg - rotateDelta.x * rotateSensitivity, minYawDeg, maxYawDeg);
            orbitPitchDeg = Mathf.Clamp(orbitPitchDeg - rotateDelta.y * rotateSensitivity, minPitchDeg, maxPitchDeg);
            return;
        }

        float step = returnToDefaultSpeed * Time.deltaTime;
        orbitYawDeg = Mathf.Clamp(Mathf.MoveTowards(orbitYawDeg, 0f, step), minYawDeg, maxYawDeg);
        orbitPitchDeg = Mathf.Clamp(Mathf.MoveTowards(orbitPitchDeg, 0f, step), minPitchDeg, maxPitchDeg);
    }

    void ApplyCameraPose()
    {
        double3 focusLlh = new double3(focusLongitudeDeg, focusLatitudeDeg, focusHeightMeters);
        double3 focusEcef = ellipsoid.LongitudeLatitudeHeightToCenteredFixed(focusLlh);

        EnuBasis focusBasis = GetEnuBasis(focusEcef);
        Vector3 offsetEnu = GetOrbitOffsetEnu();
        double3 cameraEcef = focusEcef
            + focusBasis.East * offsetEnu.x
            + focusBasis.Up * offsetEnu.y
            + focusBasis.North * offsetEnu.z;

        double3 forwardEcef = math.normalize(focusEcef - cameraEcef);
        EnuBasis cameraBasis = GetEnuBasis(cameraEcef);

        double3 upReferenceEcef = cameraBasis.North;
        double3 upEcef = Normalize(ProjectOnPlane(upReferenceEcef, forwardEcef));
        if (math.lengthsq(upEcef) < 1e-10)
        {
            upEcef = Normalize(ProjectOnPlane(cameraBasis.Up, forwardEcef));
        }

        Vector3 forwardEnu = ToVector3(ToEnu(forwardEcef, cameraBasis));
        Vector3 upEnu = ToVector3(ToEnu(upEcef, cameraBasis));
        Quaternion rotationEnu = Quaternion.LookRotation(forwardEnu, upEnu);

        globeAnchor.positionGlobeFixed = cameraEcef;
        globeAnchor.rotationEastUpNorth = ToQuaternion(rotationEnu);

        SyncCameraFarClip();
    }

    bool TryInitializeFromCurrentView()
    {
        double3 cameraEcef = globeAnchor.positionGlobeFixed;

        if (!TryIntersectEllipsoid(cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)), out double3 focusEcef))
        {
            double3 llh = ellipsoid.CenteredFixedToLongitudeLatitudeHeight(cameraEcef);
            focusLongitudeDeg = WrapLongitude(llh.x);
            focusLatitudeDeg = math.clamp(llh.y, minLatitudeDeg, maxLatitudeDeg);
            focusHeightMeters = 0.0;
            orbitYawDeg = 0f;
            orbitPitchDeg = 0f;

            double surfaceDistance = math.max(0.0, llh.z - focusHeightMeters);
            currentDistanceMeters = (float)(surfaceDistance > 1.0 ? surfaceDistance : GetWholeGlobeFillDistance() * 0.25);
            return true;
        }

        double3 focusLlh = ellipsoid.CenteredFixedToLongitudeLatitudeHeight(focusEcef);
        focusLongitudeDeg = WrapLongitude(focusLlh.x);
        focusLatitudeDeg = math.clamp(focusLlh.y, minLatitudeDeg, maxLatitudeDeg);
        focusHeightMeters = math.max(0.0, focusLlh.z);

        double3 offsetEcef = cameraEcef - focusEcef;
        EnuBasis focusBasis = GetEnuBasis(focusEcef);
        double3 offsetEnu = ToEnu(offsetEcef, focusBasis);

        double distance = math.length(offsetEnu);
        if (distance < 1.0)
        {
            return false;
        }

        currentDistanceMeters = (float)distance;
        orbitPitchDeg = Mathf.Clamp(Mathf.Asin(Mathf.Clamp((float)(offsetEnu.z / distance), -1f, 1f)) * Mathf.Rad2Deg, minPitchDeg, maxPitchDeg);
        orbitYawDeg = Mathf.Clamp(Mathf.Atan2((float)offsetEnu.x, (float)offsetEnu.y) * Mathf.Rad2Deg, minYawDeg, maxYawDeg);
        return true;
    }

    void UpdateZoomLimits()
    {
        minZoomDistance = Mathf.Max(minZoomDistanceMeters, cam.nearClipPlane + zoomInBufferMeters);
        maxZoomDistance = Mathf.Max(minZoomDistance + 1f, (float)GetWholeGlobeFillDistance());
    }

    double GetWholeGlobeFillDistance()
    {
        return GetWholeGlobeFillDistance(cam.fieldOfView);
    }

    double GetWholeGlobeFillDistance(float fieldOfViewDeg)
    {
        float verticalFovRad = fieldOfViewDeg * Mathf.Deg2Rad;
        float horizontalFovRad = 2f * Mathf.Atan(Mathf.Tan(verticalFovRad * 0.5f) * cam.aspect);
        double fillingHalfFov = math.max(verticalFovRad, horizontalFovRad) * 0.5;
        double sinFillingHalfFov = math.sin(fillingHalfFov);
        if (sinFillingHalfFov <= 1e-5)
        {
            return maxEllipsoidRadius * 2.0;
        }

        return maxEllipsoidRadius * ((1.0 / sinFillingHalfFov) - 1.0);
    }

    Vector3 GetOrbitOffsetEnu()
    {
        float yawRad = orbitYawDeg * Mathf.Deg2Rad;
        float pitchRad = orbitPitchDeg * Mathf.Deg2Rad;
        float cosPitch = Mathf.Cos(pitchRad);

        return new Vector3(
            Mathf.Sin(yawRad) * cosPitch,
            Mathf.Cos(yawRad) * cosPitch,
            Mathf.Sin(pitchRad)
        ) * currentDistanceMeters;
    }

    void SyncCameraFarClip()
    {
        if (!autoFarClip)
        {
            return;
        }

        float altitudeMeters = GetCurrentCameraAltitudeMeters();
        float farClipPlane = altitudeMeters + (float)(2.0 * maxEllipsoidRadius) + farClipPaddingMeters;
        farClipPlane = Mathf.Clamp(farClipPlane, minFarClip, maxFarClip);

        if (autoNearClip)
        {
            float nearClipPlane = initialNearClipPlane;
            float farClipRatio = farClipPlane / maxNearToFarRatio;
            if (farClipRatio > nearClipPlane)
            {
                nearClipPlane = Mathf.Min(farClipRatio, maxNearClip);
            }

            cam.nearClipPlane = nearClipPlane;
        }
        else
        {
            cam.nearClipPlane = initialNearClipPlane;
        }

        cam.farClipPlane = Mathf.Max(cam.nearClipPlane, farClipPlane);
    }

    float GetZoomExponent()
    {
        float exponent = zoomSpeed;
        if (!dynamicZoomSpeed)
        {
            return exponent;
        }

        float referenceAltitude = Mathf.Max(
            minDynamicZoomAltitudeMeters,
            GetCurrentCameraAltitudeMeters(),
            currentDistanceMeters);

        float logMin = Mathf.Log10(minDynamicZoomAltitudeMeters);
        float logMax = Mathf.Log10(maxDynamicZoomAltitudeMeters);
        float logReference = Mathf.Log10(referenceAltitude);
        float t = Mathf.InverseLerp(logMin, logMax, logReference);

        return exponent * Mathf.Lerp(minZoomSpeedScale, maxZoomSpeedScale, t);
    }

    float GetCurrentCameraAltitudeMeters()
    {
        double3 cameraLlh = ellipsoid.CenteredFixedToLongitudeLatitudeHeight(globeAnchor.positionGlobeFixed);
        return Mathf.Max(0f, (float)cameraLlh.z);
    }

    bool TryGetLongitudeLatitudeAtScreen(Vector2 screenPos, out double longitudeDeg, out double latitudeDeg)
    {
        longitudeDeg = 0.0;
        latitudeDeg = 0.0;

        if (!TryIntersectEllipsoid(cam.ScreenPointToRay(screenPos), out double3 hitEcef))
        {
            return false;
        }

        double3 llh = ellipsoid.CenteredFixedToLongitudeLatitudeHeight(hitEcef);
        longitudeDeg = WrapLongitude(llh.x);
        latitudeDeg = llh.y;
        return true;
    }

    void TryBeginPanAnchor(Vector2 screenPos)
    {
        hasPanAnchor = TryIntersectEllipsoid(cam.ScreenPointToRay(screenPos), out panAnchorEcef);
    }

    bool TryGetScreenPositionForEcef(double3 ecef, out Vector2 screenPos)
    {
        double3 unityLocal = georeference.TransformEarthCenteredEarthFixedPositionToUnity(ecef);
        Vector3 unityWorld = georeference.transform.TransformPoint(ToVector3(unityLocal));
        Vector3 projected = cam.WorldToScreenPoint(unityWorld);

        if (projected.z <= 0f)
        {
            screenPos = default;
            return false;
        }

        screenPos = new Vector2(projected.x, projected.y);
        return true;
    }

    bool TryGetPanDegreesPerPixelAtScreen(Vector2 screenPos, ref float degreesPerPixelX, ref float degreesPerPixelY)
    {
        const float sampleStepPixels = 12f;
        Vector2 right = screenPos + new Vector2(sampleStepPixels, 0f);
        Vector2 up = screenPos + new Vector2(0f, sampleStepPixels);

        if (!TryGetLongitudeLatitudeAtScreen(screenPos, out double lonCenter, out double latCenter) ||
            !TryGetLongitudeLatitudeAtScreen(right, out double lonRight, out _) ||
            !TryGetLongitudeLatitudeAtScreen(up, out _, out double latUp))
        {
            return false;
        }

        degreesPerPixelX = Mathf.DeltaAngle((float)lonCenter, (float)lonRight) / sampleStepPixels;
        degreesPerPixelY = (float)((latUp - latCenter) / sampleStepPixels);
        return true;
    }

    void GetFallbackPanDegreesPerPixel(out float degreesPerPixelX, out float degreesPerPixelY)
    {
        float verticalFovRad = cam.fieldOfView * Mathf.Deg2Rad;
        float horizontalFovRad = 2f * Mathf.Atan(Mathf.Tan(verticalFovRad * 0.5f) * cam.aspect);

        double metersPerPixelX = (2.0 * currentDistanceMeters * math.tan(horizontalFovRad * 0.5f)) / math.max(1, Screen.width);
        double metersPerPixelY = (2.0 * currentDistanceMeters * math.tan(verticalFovRad * 0.5f)) / math.max(1, Screen.height);

        double latRad = math.radians(focusLatitudeDeg);
        double metersPerDegreeLat = (2.0 * math.PI * maxEllipsoidRadius) / 360.0;
        double metersPerDegreeLon = math.max(1.0, math.cos(latRad) * metersPerDegreeLat);

        degreesPerPixelX = (float)(metersPerPixelX / metersPerDegreeLon);
        degreesPerPixelY = (float)(metersPerPixelY / metersPerDegreeLat);
    }

    bool TryIntersectEllipsoid(Ray worldRay, out double3 hitEcef)
    {
        Transform geoTransform = georeference.transform;
        Vector3 rayOriginLocal = geoTransform.InverseTransformPoint(worldRay.origin);
        Vector3 rayDirectionLocal = geoTransform.InverseTransformDirection(worldRay.direction).normalized;

        double3 rayOriginEcef = georeference.TransformUnityPositionToEarthCenteredEarthFixed(ToDouble3(rayOriginLocal));
        double3 rayDirectionEcef = Normalize(georeference.TransformUnityDirectionToEarthCenteredEarthFixed(ToDouble3(rayDirectionLocal)));

        double3 scaledOrigin = new double3(
            rayOriginEcef.x / ellipsoidRadii.x,
            rayOriginEcef.y / ellipsoidRadii.y,
            rayOriginEcef.z / ellipsoidRadii.z
        );
        double3 scaledDirection = new double3(
            rayDirectionEcef.x / ellipsoidRadii.x,
            rayDirectionEcef.y / ellipsoidRadii.y,
            rayDirectionEcef.z / ellipsoidRadii.z
        );

        double a = math.dot(scaledDirection, scaledDirection);
        double b = 2.0 * math.dot(scaledOrigin, scaledDirection);
        double c = math.dot(scaledOrigin, scaledOrigin) - 1.0;
        double discriminant = b * b - 4.0 * a * c;
        if (discriminant < 0.0)
        {
            hitEcef = default;
            return false;
        }

        double sqrtDiscriminant = math.sqrt(discriminant);
        double t0 = (-b - sqrtDiscriminant) / (2.0 * a);
        double t1 = (-b + sqrtDiscriminant) / (2.0 * a);

        double t = t0 > 0.0 ? t0 : t1;
        if (t <= 0.0)
        {
            hitEcef = default;
            return false;
        }

        hitEcef = rayOriginEcef + rayDirectionEcef * t;
        return true;
    }

    EnuBasis GetEnuBasis(double3 ecef)
    {
        double3 llh = ellipsoid.CenteredFixedToLongitudeLatitudeHeight(ecef);
        double lonRad = math.radians(llh.x);

        double3 up = Normalize(ellipsoid.GeodeticSurfaceNormal(ecef));
        double3 east = Normalize(new double3(-math.sin(lonRad), math.cos(lonRad), 0.0));
        double3 north = Normalize(math.cross(up, east));

        return new EnuBasis(east, up, north);
    }

    static double3 ToEnu(double3 ecefVector, EnuBasis basis)
    {
        return new double3(
            math.dot(ecefVector, basis.East),
            math.dot(ecefVector, basis.Up),
            math.dot(ecefVector, basis.North)
        );
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

    static double WrapLongitude(double longitudeDeg)
    {
        double wrapped = math.fmod(longitudeDeg + 180.0, 360.0);
        if (wrapped < 0.0)
        {
            wrapped += 360.0;
        }

        return wrapped - 180.0;
    }

    static double3 ProjectOnPlane(double3 vector, double3 planeNormal)
    {
        return vector - planeNormal * math.dot(vector, planeNormal);
    }

    static double3 Normalize(double3 value)
    {
        double length = math.length(value);
        return length > 1e-10 ? value / length : double3.zero;
    }

    static Vector3 ToVector3(double3 value)
    {
        return new Vector3((float)value.x, (float)value.y, (float)value.z);
    }

    static double3 ToDouble3(Vector3 value)
    {
        return new double3(value.x, value.y, value.z);
    }

    static quaternion ToQuaternion(Quaternion value)
    {
        return new quaternion(value.x, value.y, value.z, value.w);
    }

    Vector3 EarthCenteredEarthFixedToWorld(double3 ecef)
    {
        double3 unityLocal = georeference.TransformEarthCenteredEarthFixedPositionToUnity(ecef);
        return georeference.transform.TransformPoint(ToVector3(unityLocal));
    }

    public MapTransitionViewState CaptureTransitionViewState()
    {
        if (!EnsureInitialized())
        {
            return default;
        }

        GetFallbackPanDegreesPerPixel(out float degreesPerPixelX, out float degreesPerPixelY);
        TryGetPanDegreesPerPixelAtScreen(
            new Vector2(Screen.width * 0.5f, Screen.height * 0.5f),
            ref degreesPerPixelX,
            ref degreesPerPixelY);

        float fillDistanceMeters = (float)GetWholeGlobeFillDistance();

        return new MapTransitionViewState
        {
            isValid = true,
            focusLongitudeDeg = focusLongitudeDeg,
            focusLatitudeDeg = focusLatitudeDeg,
            focusHeightMeters = focusHeightMeters,
            orbitYawDeg = orbitYawDeg,
            orbitPitchDeg = orbitPitchDeg,
            fieldOfViewDeg = cam.fieldOfView,
            surfaceDistanceMeters = currentDistanceMeters,
            normalizedFillDistance = fillDistanceMeters > 0f ? currentDistanceMeters / fillDistanceMeters : 0f,
            visibleLongitudeSpanDeg = Mathf.Abs(degreesPerPixelX) * Mathf.Max(1, Screen.width),
            visibleLatitudeSpanDeg = Mathf.Abs(degreesPerPixelY) * Mathf.Max(1, Screen.height)
        };
    }

    public void ApplyTransitionViewState(MapTransitionViewState state)
    {
        if (!state.isValid || !EnsureInitialized())
        {
            return;
        }

        cam.fieldOfView = Mathf.Clamp(state.fieldOfViewDeg, 1f, 179f);
        CacheCameraShape();

        focusLongitudeDeg = WrapLongitude(state.focusLongitudeDeg);
        focusLatitudeDeg = math.clamp(state.focusLatitudeDeg, minLatitudeDeg, maxLatitudeDeg);
        focusHeightMeters = math.max(0.0, state.focusHeightMeters);
        orbitYawDeg = Mathf.Clamp(state.orbitYawDeg, minYawDeg, maxYawDeg);
        orbitPitchDeg = Mathf.Clamp(state.orbitPitchDeg, minPitchDeg, maxPitchDeg);

        UpdateZoomLimits();
        if (state.surfaceDistanceMeters > 0f)
        {
            currentDistanceMeters = state.surfaceDistanceMeters;
        }
        else if (state.normalizedFillDistance > 0f)
        {
            currentDistanceMeters = state.normalizedFillDistance * (float)GetWholeGlobeFillDistance();
        }
        else
        {
            currentDistanceMeters = CalculateDistanceFromVisibleSpan(
                (float)focusLatitudeDeg,
                state.visibleLongitudeSpanDeg,
                state.visibleLatitudeSpanDeg);
        }
        currentDistanceMeters = Mathf.Clamp(currentDistanceMeters, minZoomDistance, maxZoomDistance);

        ApplyCameraPose();
    }

    public Camera ControlledCamera => cam != null ? cam : GetComponent<Camera>();
    public double FocusLongitudeDeg => focusLongitudeDeg;
    public double FocusLatitudeDeg => focusLatitudeDeg;
    public double FocusHeightMeters => focusHeightMeters;
    public float CurrentDistanceMeters => currentDistanceMeters;
    public float WholeGlobeFillDistanceMeters => EnsureInitialized() ? (float)GetWholeGlobeFillDistance() : 0f;
    public float OrbitYawDeg => orbitYawDeg;
    public float OrbitPitchDeg => orbitPitchDeg;
    public Quaternion GetBaseCameraLookRotation()
    {
        if (!EnsureInitialized())
        {
            return transform.rotation;
        }

        double3 focusLlh = new double3(focusLongitudeDeg, focusLatitudeDeg, focusHeightMeters);
        double3 focusEcef = ellipsoid.LongitudeLatitudeHeightToCenteredFixed(focusLlh);
        EnuBasis focusBasis = GetEnuBasis(focusEcef);
        double3 cameraEcef = focusEcef + focusBasis.Up * currentDistanceMeters;
        double3 forwardEcef = Normalize(focusEcef - cameraEcef);
        EnuBasis cameraBasis = GetEnuBasis(cameraEcef);

        double3 upEcef = Normalize(ProjectOnPlane(cameraBasis.North, forwardEcef));
        if (math.lengthsq(upEcef) < 1e-10)
        {
            upEcef = Normalize(ProjectOnPlane(cameraBasis.Up, forwardEcef));
        }

        Vector3 cameraWorld = EarthCenteredEarthFixedToWorld(cameraEcef);
        Vector3 focusWorld = EarthCenteredEarthFixedToWorld(focusEcef);
        Vector3 forwardWorld = focusWorld - cameraWorld;
        Vector3 upWorld = EarthCenteredEarthFixedToWorld(cameraEcef + upEcef * 1000.0) - cameraWorld;
        if (forwardWorld.sqrMagnitude < 1e-6f)
        {
            return transform.rotation;
        }

        if (upWorld.sqrMagnitude < 1e-6f)
        {
            upWorld = Vector3.up;
        }

        return Quaternion.LookRotation(forwardWorld.normalized, upWorld.normalized);
    }

    bool EnsureInitialized()
    {
        cam = cam != null ? cam : GetComponent<Camera>();
        globeAnchor = globeAnchor != null ? globeAnchor : GetComponent<CesiumGlobeAnchor>();
        georeference = georeference != null ? georeference : GetComponentInParent<CesiumGeoreference>();
        if (cam == null || globeAnchor == null || georeference == null)
        {
            return false;
        }

        georeference.Initialize();
        ellipsoid = georeference.ellipsoid;
        ellipsoidRadii = ellipsoid.GetRadii();
        maxEllipsoidRadius = ellipsoid.GetMaximumRadius();
        globeAnchor.detectTransformChanges = false;
        globeAnchor.adjustOrientationForGlobeWhenMoving = false;

        if (input == null)
        {
            input = new InputSystem_Actions();
        }

        if (initialNearClipPlane <= 0f)
        {
            initialNearClipPlane = cam.nearClipPlane;
        }

        return true;
    }

    float CalculateDistanceFromVisibleSpan(
        float latitudeDeg,
        float longitudeSpanDeg,
        float latitudeSpanDeg)
    {
        return CalculateDistanceFromVisibleSpan(
            latitudeDeg,
            longitudeSpanDeg,
            latitudeSpanDeg,
            cam.fieldOfView);
    }

    float CalculateDistanceFromVisibleSpan(
        float latitudeDeg,
        float longitudeSpanDeg,
        float latitudeSpanDeg,
        float fieldOfViewDeg)
    {
        float safeLatitudeSpan = Mathf.Max(0.01f, latitudeSpanDeg);
        float safeLongitudeSpan = Mathf.Max(0.01f, longitudeSpanDeg);
        float verticalFovRad = fieldOfViewDeg * Mathf.Deg2Rad;
        float horizontalFovRad = 2f * Mathf.Atan(Mathf.Tan(verticalFovRad * 0.5f) * cam.aspect);
        float tanHalfVertical = Mathf.Tan(verticalFovRad * 0.5f);
        float tanHalfHorizontal = Mathf.Tan(horizontalFovRad * 0.5f);
        if (tanHalfVertical < 1e-4f || tanHalfHorizontal < 1e-4f)
        {
            return currentDistanceMeters;
        }

        float metersPerDegreeLat = (float)((2.0 * math.PI * maxEllipsoidRadius) / 360.0);
        float metersPerDegreeLon = Mathf.Max(
            1f,
            Mathf.Cos(latitudeDeg * Mathf.Deg2Rad) * metersPerDegreeLat);

        float halfVerticalMeters = safeLatitudeSpan * 0.5f * metersPerDegreeLat;
        float halfHorizontalMeters = safeLongitudeSpan * 0.5f * metersPerDegreeLon;

        float verticalDistance = halfVerticalMeters / tanHalfVertical;
        float horizontalDistance = halfHorizontalMeters / tanHalfHorizontal;
        return Mathf.Max(verticalDistance, horizontalDistance);
    }

    readonly struct EnuBasis
    {
        public EnuBasis(double3 east, double3 up, double3 north)
        {
            East = east;
            Up = up;
            North = north;
        }

        public double3 East { get; }
        public double3 Up { get; }
        public double3 North { get; }
    }
}
