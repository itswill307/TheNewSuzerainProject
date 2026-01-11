// PlaneSphereMorph.hlsl
#ifndef PLANE_SPHERE_MORPH_HLSL
#define PLANE_SPHERE_MORPH_HLSL

//–– Constants ––
// full π and half-π for lon/lat conversion
static const float PI_      = 3.14159265359;
static const float HALF_PI_ = 1.57079632679;

//––
// In Shader Graph Custom Function node (File mode):
//   • Name:        PlaneSphereMorph     ← no “_float” suffix here
//   • Function:    PlaneSphereMorph_float
//   • Inputs:      UV (Vector2), Radius (Float), Morph (Float)
//   • Outputs:     OutPosition (Vector3), OutNormal (Vector3)
// Note: the “_float” suffix matches Shader Graph’s precision-suffix rules. :contentReference[oaicite:0]{index=0}
void PlaneSphereMorph_float(
    float2       UV,
    float        Radius,
    float        Morph,
    // Height/Elevation inputs (R16 heightmap assumed 0..1 normalized)
    UnityTexture2D    HeightTex,
    UnitySamplerState HeightSampler,
    float2       UVOffset,     // should match your material's _UVOffset.x wrap
    float        HeightScale,  // world units per [0..1]; use negative to raise toward camera if desired
    float        HeightBias,   // world units to add/subtract after scaling (e.g., -seaLevel)
    out float3   OutPosition,
    out float3   OutNormal)
{
    // Single-phase morph: flat rectangle → sphere (with simultaneous longitude and latitude curving)
    // Center of texture (UV 0.5, 0.5) stays at world origin (0, 0, 0)
    
    // Sphere radius decreases during morph - mesh maintains original size
    // This creates partial coverage of smaller spheres (no lateral distortion)
    float sphereRadius = lerp(Radius * 3.0, Radius, Morph);
    
    // Flat plane position - center texture at origin
    // Maintain 2:1 aspect ratio: X = 2π*Radius, Y = π*Radius  
    float3 planePos = float3(
        (UV.x - 0.5) * (2.0 * PI_ * Radius),    // X: 2π*Radius wide
        (UV.y - 0.5) * (PI_ * Radius),          // Y: π*Radius tall (2:1 ratio)
        0.0
    );
    
    // Single-phase morph: flat → sphere (longitude and latitude curve simultaneously)
    // Mesh maintains original size, maps to partial coverage of decreasing sphere
    
    // Calculate angles based on maintaining arc length = flat distance (no lateral distortion)
    // Arc length = angle × sphereRadius, Flat distance = (UV.x - 0.5) × 2π × Radius
    // For no distortion: angle × sphereRadius = (UV.x - 0.5) × 2π × Radius
    //float longitude = (UV.x - 0.5) * (2.0 * PI_ * Radius) / sphereRadius;
    float longitude = (UV.x - 0.5) * (2.0 * PI_ );
    float latitude = (UV.y - 0.5) * PI_; // -π/2 to +π/2
    
    // Sphere center positioned so front face stays at Z=0
    float3 sphereCenter = float3(0, 0, sphereRadius);
    float3 spherePos = sphereCenter + sphereRadius * float3(
        cos(latitude) * sin(longitude),        // X: longitude wrapping
        sin(latitude),                         // Y: latitude curving  
        -cos(latitude) * cos(longitude)        // Z: depth (curves away from camera = concave)
    );
    
    // Single-phase transition: flat → sphere (both longitude and latitude curve together)
    float3 basePos = lerp(planePos, spherePos, Morph);

    // --- Recalculated geometric normal using consistent latitude/longitude values ---
    // Use the same latitude and longitude values as position calculation
    float cosLat = cos(latitude);
    float sinLat = sin(latitude);
    float cosLon = cos(longitude);
    float sinLon = sin(longitude);
    
    // Complex approach: Blend tangent vectors first for maximum accuracy
    
    // Tangents of the flat plane (object-space)
    float3 dPlane_du = float3( 2.0 * PI_ * Radius, 0.0, 0.0 );
    float3 dPlane_dv = float3( 0.0, PI_ * Radius, 0.0 );

    // Tangents of the morphed sphere patch - must match actual longitude/latitude derivatives
    float  dLon_dU = 2.0 * PI_;  // Matches position: longitude = (UV.x - 0.5) * (2π)
    float  dLat_dV = PI_;        // Matches position: latitude = (UV.y - 0.5) * π

    // ∂Psphere/∂u  (varying longitude only) - includes sphereRadius scaling from position
    float3 dSphere_du = sphereRadius * float3(
        cosLat * cosLon * dLon_dU,    // X: ∂(cos(lat)*sin(lon))/∂lon = cos(lat)*cos(lon)
        0.0,                          // Y: ∂(sin(lat))/∂lon = 0
        cosLat * sinLon * dLon_dU     // Z: ∂(-cos(lat)*cos(lon))/∂lon = cos(lat)*sin(lon)
    );

    // ∂Psphere/∂v  (varying latitude only) - includes sphereRadius scaling from position
    float3 dSphere_dv = sphereRadius * float3(
       -sinLat * sinLon * dLat_dV,    // X: ∂(cos(lat)*sin(lon))/∂lat = -sin(lat)*sin(lon)
        cosLat * dLat_dV,             // Y: ∂(sin(lat))/∂lat = cos(lat)
        sinLat * cosLon * dLat_dV     // Z: ∂(-cos(lat)*cos(lon))/∂lat = sin(lat)*cos(lon)
    );

    // Blend tangent vectors by Morph (represents actual intermediate surface geometry)
    float3 tangentU = lerp(dPlane_du, dSphere_du, Morph);
    float3 tangentV = lerp(dPlane_dv, dSphere_dv, Morph);

    // Calculate normal from blended tangents (geometrically accurate for intermediate surface)
    float3 geoNormal = normalize(cross(tangentU, tangentV));

    // --- Elevation displacement (sample R16 heightmap in 0..1) ---
    float2 sampleUV = float2(
        frac(UV.x + UVOffset.x),  // Normal sampling 
        saturate(UV.y + UVOffset.y)
    );
    float height01 = HeightTex.SampleLevel(HeightSampler, sampleUV, 0).r; // R16 normalized (0..1)
    float heightWU = (height01 - HeightBias) * HeightScale;                 // convert to world units

    // Test: Are waves caused by height displacement?
    OutPosition = basePos + (-geoNormal) * heightWU;

    // Ensure normal points outward (toward camera direction for accurate camera positioning)
    //float3 towardCamera = float3(0, 0, -1);
    //if (dot(geoNormal, towardCamera) < 0.0) geoNormal = -geoNormal;

    OutNormal = -geoNormal;
}

#endif // PLANE_SPHERE_MORPH_HLSL
