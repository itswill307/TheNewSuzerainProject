using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Camera))]
public class WorldMapController : MonoBehaviour
{
    [Header("Scene References")]
    [SerializeField] Material  mapMat;      // morph shader material
    [SerializeField] Renderer  mapRenderer; // mesh renderer for bounds calculation

    [Header("World Geometry")]
    [SerializeField] float radius = 100f;   // must match shader

    [Header("Zoom")]
    [SerializeField] float zoomSpeed = 6f;           // zoom sensitivity
    [SerializeField] float zoomInBuffer = 0.01f;      // additional distance from mesh surface as a percentage of radius

    [Header("Panning")]
    [SerializeField] float panKeySpeed = 60f;        // degrees per second for keys
    [SerializeField] float panDragSpeed = 3f;        // mouse drag sensitivity multiplier
    [SerializeField] float panVelocityMultiplier = 0.1f;  // velocity-based acceleration factor
    [SerializeField] float maxVelocityBoost = 5f;    // maximum velocity multiplier
    
    [Header("Rotation")]
    [SerializeField, Tooltip("Degrees of yaw/pitch per pixel when rotating (right mouse drag)")]
    float rotateSensitivity = 0.2f;
    [SerializeField, Tooltip("Minimum and maximum pitch (deg) to keep camera right-side up")]
    float minPitchDeg = -80f, maxPitchDeg = 80f;
    [SerializeField, Tooltip("Speed at which camera returns to default when RMB is released (deg/sec)")]
    float returnToDefaultSpeed = 240f;
    
    [Header("Morph")]
    [SerializeField] float currentMorph = 0f;        // current morph value (0=flat, 1=sphere) - controlled by zoom
    [SerializeField] bool enableZoomMorph = true;    // enable automatic morph based on zoom level
    [SerializeField, Tooltip("Exponent shaping for morph vs zoom. >1 slows morph early; <1 speeds it up.")]
    float morphExponent = 4.0f;

    // ---------- private ----------
    Camera cam;
    InputSystem_Actions input;
    float mapWidth, mapHeight;
    float baseDistance;     // default distance to fit map width
    float minZoom, maxZoom; // zoom distance limits
    float currentZoom;      // current zoom distance
    
    // Panning state - hybrid approach for optimal visual quality
    float focusLon = 0f;    // longitude center (-180 to 180) - handled by UV offset only
    float cameraLat = 0f;   // camera latitude in degrees - handled by camera position + Z distance correction
    
    // Orbit state (right-mouse drag) – camera rotates around surface pivot without altering focusLon/cameraLat
    float orbitYawDeg = 0f;    // around world up
    float orbitPitchDeg = 0f;  // around camera right (kept clamped)
    
    // Cached latitude limits (only recalculated when zoom changes)
    float cachedMinLatLimit = -90f;
    float cachedMaxLatLimit = 90f;
    float lastZoomForLimits = -1f;

    void Awake()
    {
        cam = GetComponent<Camera>();
        input = new InputSystem_Actions();
        
        // Calculate map dimensions
        mapWidth = 2f * Mathf.PI * radius;  // circumference
        mapHeight = Mathf.PI * radius;      // height from pole to pole
        
        // Calculate base camera distance and zoom limits
        CalculateZoomLimits();
        
        // Start at default view
        currentZoom = baseDistance;
        cameraLat = 0f;  // Ensure camera starts at equator
        
        // Initialize camera with identity rotation to avoid -180 Y rotation issue
        transform.localRotation = Quaternion.identity;

        PositionCamera();
        
        // Set up map for flat viewing
        SetupFlatMap();
    }

    void OnEnable() => input.Enable();
    void OnDisable() => input.Disable();

    void CalculateZoomLimits()
    {
        // Calculate horizontal FOV from vertical FOV and aspect ratio
        float vFOV = cam.fieldOfView * Mathf.Deg2Rad;
        float hFOV = 2f * Mathf.Atan(Mathf.Tan(vFOV * 0.5f) * cam.aspect);

        // Calculate distance to fit map width and height
        float horizontalDistance = (mapWidth * 0.5f) / Mathf.Tan(hFOV * 0.5f);
        float verticalDistance = (mapHeight * 0.5f) / Mathf.Tan(vFOV * 0.5f);
        
        // Use the smaller distance to fill screen with no margins
        baseDistance = Mathf.Min(horizontalDistance, verticalDistance);
        
        // Use the smaller distance to fill screen with no margins
        maxZoom = Mathf.Min(horizontalDistance, verticalDistance);
        
        // Calculate zoom in limit based on actual mesh distance
        minZoom = radius * zoomInBuffer;
    }

    void PositionCamera()
    {
        // Pivot at current surface point
        Vector3 pivot = CalculateSurfacePositionAtLatitude(cameraLat);
        Vector3 surfaceNormal = CalculateSurfaceNormalAtLatitude(cameraLat);

        // Start from default offset aligned with surface normal (outward)
        Vector3 offset = surfaceNormal * currentZoom;

        // Apply yaw around world up
        Quaternion yawQ = Quaternion.AngleAxis(orbitYawDeg, Vector3.up);
        Vector3 afterYaw = yawQ * offset;

        // Right axis for pitch (perpendicular to world up and current view direction from pivot)
        Vector3 rightAxis = Vector3.Cross(Vector3.up, afterYaw).normalized;
        if (rightAxis.sqrMagnitude < 1e-6f)
        {
            // Fallback to a stable axis if near singularity
            rightAxis = Vector3.right;
        }

        // Clamp pitch to keep camera right-side up
        orbitPitchDeg = Mathf.Clamp(orbitPitchDeg, minPitchDeg, maxPitchDeg);
        Quaternion pitchQ = Quaternion.AngleAxis(orbitPitchDeg, rightAxis);
        Vector3 finalOffset = pitchQ * afterYaw;

        Vector3 cameraPosition = pivot + finalOffset;
        cam.transform.localPosition = cameraPosition;

        // Look at pivot with world up to avoid roll
        Vector3 forward = (pivot - cameraPosition).normalized;
        transform.localRotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    Vector3 CalculateSurfacePositionAtLatitude(float latitudeDegrees)
    {
        float sphereRadius = Mathf.Lerp(radius * 3.0f, radius, currentMorph);

        // uvX is always 0.5 because the camera always looks at center longitude
        float uvY = (latitudeDegrees / 180f) + 0.5f;
        Vector3 planePos = new(
            0f, 
            (uvY - 0.5f) * Mathf.PI * radius, 
            0f
        );

        float longitude = 0f; // Camera always looks at center longitude
        float latitude = (uvY - 0.5f) * Mathf.PI;

        // Sphere position at this latitude (same as shader)
        Vector3 sphereCenter = new Vector3(0, 0, sphereRadius);
        Vector3 spherePos = sphereCenter + sphereRadius * new Vector3(
            Mathf.Cos(latitude) * Mathf.Sin(longitude),     // = 0 (longitude = 0)
            Mathf.Sin(latitude),
            -Mathf.Cos(latitude) * Mathf.Cos(longitude)     // = -Cos(latitude)
        );

        return Vector3.Lerp(planePos, spherePos, currentMorph);
    }
    
    Vector3 CalculateSurfaceNormalAtLatitude(float latitudeDegrees)
    {
        float sphereRadius = Mathf.Lerp(radius * 3.0f, radius, currentMorph);

        float uvY = (latitudeDegrees / 180f) + 0.5f;
        
        // Use shader's exact calculation: lat = (UV.y - 0.5) * PI
        float longitude = 0f; // Camera always looks at center longitude
        float latitude = (uvY - 0.5f) * Mathf.PI;

        // --- Geometric normal via analytic derivatives ---
        // Tangents of the flat plane (object-space)
        Vector3 dPlane_du = new( 2.0f * Mathf.PI * radius, 0.0f, 0.0f );
        Vector3 dPlane_dv = new( 0.0f, Mathf.PI * radius, 0.0f );

        // Tangents of the morphed sphere patch
        float dLon_dU = 2.0f * Mathf.PI; // ∂longitude / ∂u
        float dLat_dV = Mathf.PI;        // ∂latitude  / ∂v

        // ∂Psphere/∂u  (varying longitude only)
        Vector3 dSphere_du = sphereRadius * new Vector3(
            Mathf.Cos(latitude) * Mathf.Cos(longitude) * dLon_dU,   // X
            0.0f,                         // Y
            Mathf.Cos(latitude) * Mathf.Sin(longitude) * dLon_dU    // Z
        );

        // ∂Psphere/∂v  (varying latitude only)
        Vector3 dSphere_dv = sphereRadius * new Vector3(
            -Mathf.Sin(latitude) * Mathf.Sin(longitude) * dLat_dV,    // X
            Mathf.Cos(latitude) * dLat_dV,                            // Y
            Mathf.Sin(latitude) * Mathf.Cos(longitude) * dLat_dV      // Z
        );

        // Blend plane and sphere tangents by Morph (same as position blend)
        Vector3 tangentU = Vector3.Lerp(dPlane_du, dSphere_du, currentMorph);
        Vector3 tangentV = Vector3.Lerp(dPlane_dv, dSphere_dv, currentMorph);
        Vector3 sphereNormal = Vector3.Cross(tangentU, tangentV).normalized;

        return -sphereNormal;
    }

    (float, float) CalculateLatitudeLimitsFromFOV()
    {
        // Calculate the maximum latitude where the FOV edge ray intersects the mesh surface
        // This is independent of current camera position and gives symmetric limits
        
        float vFOV = cam.fieldOfView * Mathf.Deg2Rad;
        float halfFOV = vFOV * 0.5f; // in radians
        
        // For a camera positioned at latitude L and zoom distance Z:
        // - Camera position: surface(L) + normal(L) * Z
        // - FOV edge ray: camera_pos + t * (forward + up * tan(halfFOV))
        // - We need to find the maximum L where the FOV edge can see latitude 90°
        // Binary search to find the maximum latitude where north pole is exactly at FOV edge
        float minLat = 0f;
        float maxLat = 90f;
        
        for (int i = 0; i < 12; i++) // Reduced iterations: 12 gives ~0.02° precision
        {
            float testLat = (minLat + maxLat) * 0.5f;
            
            // Calculate camera position for this test latitude
            Vector3 cameraPos = CalculateSurfacePositionAtLatitude(testLat) + 
                               CalculateSurfaceNormalAtLatitude(testLat) * currentZoom;
            
            // Calculate camera forward direction
            Vector3 cameraForward = -CalculateSurfaceNormalAtLatitude(testLat);
            Vector3 cameraUp = Vector3.up;
            
            // Make sure up vector is perpendicular to forward
            cameraUp = Vector3.Cross(Vector3.Cross(cameraForward, cameraUp), cameraForward).normalized;
            
            // Get north pole position
            Vector3 northPolePos = CalculateSurfacePositionAtLatitude(90f);
            
            // Calculate vector from camera to north pole
            Vector3 toNorthPole = northPolePos - cameraPos;
            
            // Project onto camera's local coordinate system
            float forwardDistance = Vector3.Dot(toNorthPole, cameraForward);
            float upDistance = Vector3.Dot(toNorthPole, cameraUp);
            
            // Calculate angle from camera center to north pole (in radians)
            float angleToNorthPole = Mathf.Atan2(upDistance, forwardDistance);
                        
            // We want to find where north pole is exactly at the TOP edge of FOV
            // angleToNorthPole > 0 means north pole is above camera center (in upper half)
            // We're looking for the latitude where angleToNorthPole = halfFOV
            if (angleToNorthPole >= 0f && angleToNorthPole < halfFOV)
            {
                maxLat = testLat; // North pole is at/above top edge, too far north
            }
            else
            {
                minLat = testLat; // North pole is below top edge, can go further north
            }
        }
        
        float limitLatitude = (minLat + maxLat) * 0.5f;
        
        return (-limitLatitude, limitLatitude);
    }

    void SetupFlatMap()
    {
        // Morph is now controlled by zoom level, just update UV offset
        if (mapMat != null)
        {
            UpdateUVOffset();
        }
    }

    void Update()
    {
        // Get scroll input for zooming
        float scroll = input.Map.Zoom.ReadValue<float>();
        
        // Apply zoom
        currentZoom = Mathf.Clamp(currentZoom - scroll * zoomSpeed, minZoom, maxZoom);
        
        // Calculate morph based on zoom level (if enabled)
        if (enableZoomMorph)
        {
            // When zoomed out (currentZoom = maxZoom), morph = 0 (flat)
            // When zoomed in (currentZoom = minZoom), morph = 1 (sphere)
            float zoomRange = maxZoom - minZoom;
            if (zoomRange > 0) // Avoid division by zero
            {
                float normalizedZoom = (maxZoom - currentZoom) / zoomRange; // 0 at max zoom, 1 at min zoom
                // Exponential shaping
                float shaped = Mathf.Pow(Mathf.Clamp01(normalizedZoom), Mathf.Max(0.01f, morphExponent));
                currentMorph = Mathf.Clamp01(shaped);
            }
        }
        
        // Apply morph to material
        if (mapMat != null)
        {
            mapMat.SetFloat("_Morph", currentMorph);
        }

        // Get panning input
        Vector2 moveKeys = input.Map.Move.ReadValue<Vector2>();
        Vector2 dragPan = input.Map.DragPan.ReadValue<Vector2>();
        
        // Calculate how much world space one pixel represents at current zoom
        // Use absolute currentZoom value, not camera position which moves with panning
        float vFOV = cam.fieldOfView * Mathf.Deg2Rad;
        float hFOV = 2f * Mathf.Atan(Mathf.Tan(vFOV * 0.5f) * cam.aspect);
        float worldUnitsPerPixelX = (2f * currentZoom * Mathf.Tan(hFOV * 0.5f)) / Screen.width;
        float worldUnitsPerPixelY = (2f * currentZoom * Mathf.Tan(vFOV * 0.5f)) / Screen.height;
        
        // Convert world units to degrees for consistent panning
        float degreesPerPixelX = (worldUnitsPerPixelX / mapWidth) * 360f;
        float degreesPerPixelY = (worldUnitsPerPixelY / mapHeight) * 180f;

        // Calculate velocity-based panning acceleration
        float mouseVelocity = dragPan.magnitude; // pixels per frame
        float velocityBoost = 1f + (mouseVelocity * panVelocityMultiplier);
        velocityBoost = Mathf.Clamp(velocityBoost, 1f, maxVelocityBoost);
        
        // Calculate panning from keys (WASD/arrows) and mouse drag with velocity boost
        float panLon = (moveKeys.x * panKeySpeed * Time.deltaTime) + (-dragPan.x * degreesPerPixelX * panDragSpeed * velocityBoost);
        float panLat = (moveKeys.y * panKeySpeed * Time.deltaTime) + (-dragPan.y * degreesPerPixelY * panDragSpeed * velocityBoost);

        // Camera rotation (right mouse drag): orbit around surface pivot without changing focusLon/cameraLat
        Vector2 rotateDelta = input.Map.Rotate.ReadValue<Vector2>();
        bool rmbHeld = input.Map.RMB.IsPressed();
        if (rmbHeld)
        {
            orbitYawDeg   += rotateDelta.x * rotateSensitivity;
            // Invert fixed: dragging up increases pitch (camera moves up)
            orbitPitchDeg += rotateDelta.y * rotateSensitivity;
            orbitPitchDeg = Mathf.Clamp(orbitPitchDeg, minPitchDeg, maxPitchDeg);
        }
        else
        {
            // Smoothly return to default orientation (0 yaw/pitch) when RMB released
            float step = returnToDefaultSpeed * Time.deltaTime;
            orbitYawDeg = Mathf.MoveTowardsAngle(orbitYawDeg, 0f, step);
            orbitPitchDeg = Mathf.MoveTowards(orbitPitchDeg, 0f, step);
        }
        
        // Apply horizontal panning with UV offset (infinite scrolling)
        focusLon = Mathf.Repeat(focusLon + panLon, 360f);  // Wrap around horizontally
        
        // Apply vertical panning with camera movement (with latitude limits)
        float newCameraLat = cameraLat + panLat;
        
        // Only recalculate latitude limits when zoom changes
        if (Mathf.Abs(currentZoom - lastZoomForLimits) > 0.01f)
        {
            (cachedMinLatLimit, cachedMaxLatLimit) = CalculateLatitudeLimitsFromFOV();
            lastZoomForLimits = currentZoom;
        }
        
        cameraLat = Mathf.Clamp(newCameraLat, cachedMinLatLimit, cachedMaxLatLimit);

        // Update camera position
        PositionCamera();
        
        // Update UV offset for panning
        UpdateUVOffset();
    }

    void UpdateUVOffset()
    {
        if (mapMat == null) return;

        // Set UV offset for horizontal panning only (vertical panning uses camera movement)
        float uvOffsetX = focusLon / 360f;
        float uvOffsetY = 0f;  // No vertical UV offset - camera handles vertical movement
        
        // Set UV offset: only X for horizontal infinite panning
        mapMat.SetVector("_UVOffset", new Vector2(uvOffsetX, uvOffsetY));
        
        // Keep full detail at all zoom levels - no LOD smoothing
        float heightLod = 0.0f; // Always use highest resolution
        mapMat.SetFloat("_HeightLod", heightLod);
        
        // No mesh movement - texture panning handled purely by UV offset
    }

    // Recalculate if camera settings change
    void OnValidate()
    {
        if (Application.isPlaying && cam != null)
        {
            mapWidth = 2f * Mathf.PI * radius;
            mapHeight = Mathf.PI * radius;
            CalculateZoomLimits();
            currentZoom = Mathf.Clamp(currentZoom, minZoom, maxZoom);
            
            // Recalculate morph based on current zoom level (if enabled)
            if (enableZoomMorph && maxZoom > minZoom) // Avoid division by zero
            {
                float zoomRange = maxZoom - minZoom;
                float normalizedZoom = (maxZoom - currentZoom) / zoomRange;
                float shaped = Mathf.Pow(Mathf.Clamp01(normalizedZoom), Mathf.Max(0.01f, morphExponent));
                currentMorph = Mathf.Clamp01(shaped);
            }
            
            // Always apply current morph value to material
            if (mapMat != null)
            {
                mapMat.SetFloat("_Morph", currentMorph);
            }
            
            PositionCamera();
            UpdateUVOffset();
        }
    }
}
