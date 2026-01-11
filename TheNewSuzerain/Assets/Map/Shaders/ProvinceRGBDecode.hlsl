// ProvinceRGBDecode.hlsl
// Use AFTER you've sampled the Province ID texture with a Sample Texture 2D node.
// Expects ID texture to be imported as: sRGB Off, Filter Point, No Mips, Wrap Repeat.
// Set your Custom Function node Precision to Float (important on Metal).

#ifndef PROVINCE_RGB_DECODE_INCLUDED
#define PROVINCE_RGB_DECODE_INCLUDED

// Convert normalized RGB (0..1) from the ID map to a 24-bit integer ID.
inline uint DecodeProvinceId24(float3 rgb)
{
    float3 c255 = round(saturate(rgb) * 255.0);
    uint r = (uint)c255.r;
    uint g = (uint)c255.g;
    uint b = (uint)c255.b;
    return (r | (g << 8) | (b << 16));
}

// ---- Minimal outputs you might want ----

// 1) Return just the decoded ID (as float for SG wiring)
void ProvinceIdFromRGB_float(float3 idRGB, out float idOut)
{
    idOut = (float)DecodeProvinceId24(idRGB);
}

// 2) Produce a 0/1 mask for equality with selectedId, and also output the decoded ID.
//    Guards against negative selectedId (e.g. -1 sentinel) to ensure mask = 0 in that case.
void ProvinceIdMaskFromRGB_float(float3 idRGB, float selectedId, out float mask, out float idOut)
{
    uint pid = DecodeProvinceId24(idRGB);
    idOut = (float)pid;
    // Gate: if selectedId < 0, disable the mask entirely
    float enabled = step(0.0, selectedId + 0.5);
    // Avoid wrap when converting negative float to uint
    uint sid = (uint)max(0.0, round(selectedId));
    mask = enabled * ((pid == sid) ? 1.0 : 0.0);
}

// 3) Click highlight: baseColor + highlightColor.rgb * highlightColor.a when IDs match.
void ProvinceHighlightFromRGB_float(
    float3 idRGB, float selectedId, float4 highlightColor,
    float3 baseColor, out float3 outColor)
{
    float mask, idf;
    ProvinceIdMaskFromRGB_float(idRGB, selectedId, mask, idf);
    float3 tint = highlightColor.rgb * highlightColor.a;
    outColor = lerp(baseColor, baseColor + tint, mask);
}

// 4) Hover + Select (hover applied first, selection overrides).
void ProvinceHoverSelectFromRGB_float(
    float3 idRGB, float selectedId, float4 highlightColor,
    float hoverId, float4 hoverColor,
    float3 baseColor, out float3 outColor)
{
    float mSel, idSel;
    ProvinceIdMaskFromRGB_float(idRGB, selectedId, mSel, idSel);

    float mHover, idHover;
    ProvinceIdMaskFromRGB_float(idRGB, hoverId, mHover, idHover);
    // Ensure hover cannot apply when hoverId is negative (e.g. -1 sentinel from CPU)
    float hoverEnabled = step(0.0, hoverId + 0.5);
    mHover *= hoverEnabled;

    float3 c = baseColor;
    c = lerp(c, c + hoverColor.rgb * hoverColor.a, mHover);
    c = lerp(c, c + highlightColor.rgb * highlightColor.a, mSel);
    outColor = c;
}

#endif // PROVINCE_RGB_DECODE_INCLUDED
