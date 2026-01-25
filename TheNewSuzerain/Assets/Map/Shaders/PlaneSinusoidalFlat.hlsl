// PlaneSinusoidalFlat.hlsl
#ifndef PLANE_SINUSOIDAL_FLAT_HLSL
#define PLANE_SINUSOIDAL_FLAT_HLSL

static const float PI_ = 3.14159265359;

// Shader Graph Custom Function (File):
//   Name:     PlaneSinusoidalFlat
//   Function: PlaneSinusoidalFlat_float
//   Inputs:   UV (Vector2), Radius (Float), Morph (Float)
//             HeightTex (Texture2D), HeightSampler (SamplerState)
//             UVOffset (Vector2), HeightScale (Float), HeightBias (Float)
//   Outputs:  OutPosition (Vector3), OutNormal (Vector3)
void PlaneSinusoidalFlat_float(
    float2       UV,
    float        Radius,
    float        Morph,
    UnityTexture2D    HeightTex,
    UnitySamplerState HeightSampler,
    float2       UVOffset,
    float        HeightScale,
    float        HeightBias,
    out float3   OutPosition,
    out float3   OutNormal)
{
    float longitude = (UV.x - 0.5) * (2.0 * PI_);
    float latitude = (UV.y - 0.5) * PI_;
    float cosLat = cos(latitude);
    float sinLat = sin(latitude);
    float scaleX = lerp(1.0, cosLat, saturate(Morph));

    float3 basePos = float3(
        longitude * scaleX * Radius,
        latitude * Radius,
        0.0
    );

    // Tangents for normal (surface stays in the XY plane).
    float dLon_dU = 2.0 * PI_;
    float dLat_dV = PI_;
    float3 dPdu = float3(scaleX * Radius * dLon_dU, 0.0, 0.0);
    float dScaleX_dV = saturate(Morph) * (-sinLat) * dLat_dV;
    float3 dPdv = float3(longitude * Radius * dScaleX_dV, Radius * dLat_dV, 0.0);

    float3 geoNormal = normalize(cross(dPdu, dPdv));
    if (length(geoNormal) < 1e-6)
    {
        geoNormal = float3(0.0, 0.0, 1.0);
    }

    float2 sampleUV = float2(
        frac(UV.x + UVOffset.x),
        saturate(UV.y + UVOffset.y)
    );
    float height01 = HeightTex.SampleLevel(HeightSampler, sampleUV, 0).r;
    float heightWU = (height01 - HeightBias) * HeightScale;

    OutPosition = basePos + (-geoNormal) * heightWU;
    OutNormal = -geoNormal;
}

// Shader Graph Custom Function (File):
//   Name:     PlaneAitoffFlat
//   Function: PlaneAitoffFlat_float
//   Inputs:   UV (Vector2), Radius (Float), Morph (Float)
//             HeightTex (Texture2D), HeightSampler (SamplerState)
//             UVOffset (Vector2), HeightScale (Float), HeightBias (Float)
//   Outputs:  OutPosition (Vector3), OutNormal (Vector3)
void PlaneAitoffFlat_float(
    float2       UV,
    float        Radius,
    float        Morph,
    UnityTexture2D    HeightTex,
    UnitySamplerState HeightSampler,
    float2       UVOffset,
    float        HeightScale,
    float        HeightBias,
    out float3   OutPosition,
    out float3   OutNormal)
{
    float longitude = (UV.x - 0.5) * (2.0 * PI_);
    float latitude = (UV.y - 0.5) * PI_;

    float3 equirectPos = float3(
        longitude * Radius,
        latitude * Radius,
        0.0
    );

    float halfLon = 0.5 * longitude;
    float cosLat = cos(latitude);
    float sinLat = sin(latitude);
    float cosHalfLon = cos(halfLon);
    float sinHalfLon = sin(halfLon);
    float alpha = acos(cosLat * cosHalfLon);
    float sinAlpha = sin(alpha);
    float invSinc = (abs(alpha) < 1e-6) ? 1.0 : (alpha / sinAlpha);

    float2 aitoffXY = float2(
        2.0 * cosLat * sinHalfLon * invSinc,
        sinLat * invSinc
    );

    float3 aitoffPos = float3(aitoffXY * Radius, 0.0);

    float3 basePos = lerp(equirectPos, aitoffPos, saturate(Morph));

    // Aitoff is planar; its geometric normal stays constant.
    float3 geoNormal = float3(0.0, 0.0, 1.0);

    float2 sampleUV = float2(
        frac(UV.x + UVOffset.x),
        saturate(UV.y + UVOffset.y)
    );
    float height01 = HeightTex.SampleLevel(HeightSampler, sampleUV, 0).r;
    float heightWU = (height01 - HeightBias) * HeightScale;

    OutPosition = basePos + (-geoNormal) * heightWU;
    OutNormal = -geoNormal;
}

#endif // PLANE_SINUSOIDAL_FLAT_HLSL
