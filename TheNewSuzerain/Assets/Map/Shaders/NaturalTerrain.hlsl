#ifndef NATURAL_TERRAIN_INCLUDED
#define NATURAL_TERRAIN_INCLUDED

// Shared by the flat map, Cesium, and the texture-derived distant-surface baker.
TEXTURE2D(_TerrainSurfaceTex);
SAMPLER(sampler_TerrainSurfaceTex);
float4 _TerrainSurfaceTex_TexelSize;
TEXTURE2D(_TerrainElevationTex);
SAMPLER(sampler_TerrainElevationTex);
float4 _TerrainElevationTex_TexelSize;

float2 TerrainHash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

float2 TerrainTransformDirection(float2 uv, float transform)
{
    if (transform < 1.0) return uv;
    if (transform < 2.0) return float2(-uv.x, uv.y);
    if (transform < 3.0) return float2(uv.x, -uv.y);
    if (transform < 4.0) return -uv;
    if (transform < 5.0) return uv.yx;
    if (transform < 6.0) return float2(-uv.y, uv.x);
    if (transform < 7.0) return float2(uv.y, -uv.x);
    return -uv.yx;
}

float3 TerrainSampleCell(float classId, float2 uv, float2 cell, float2 dx, float2 dy)
{
    float2 hash = TerrainHash(cell + float2(classId * 17.17, classId * 31.31));
    float transform = floor(hash.x * 8.0);
    float2 origin = float2(cell.x + cell.y * 0.5, cell.y * 0.86602540378);
    uv = TerrainTransformDirection(uv - origin - 0.5, transform) + 0.5 + (hash - 0.5) * 128.0;
    // Derive gradients BEFORE floor/hash/variant branches and rotate them with the UVs.
    // Implicit derivatives across randomized joins otherwise select blurry mip levels.
    dx = TerrainTransformDirection(dx, transform);
    dy = TerrainTransformDirection(dy, transform);
    float3 sampled = 0.0;
    if (abs(classId - 10.0) < 0.5 && _GrasslandVariantCount > 0.5)
    {
        float variant = min(floor(TerrainHash(cell + float2(classId * 41.41, classId * 59.59)).y
            * _GrasslandVariantCount), _GrasslandVariantCount - 1.0);
        sampled = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GrasslandTextureVariants, sampler_GrasslandTextureVariants,
            uv, variant, dx, dy).rgb;
    }
    else
    {
        sampled = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LandcoverTextureArray, sampler_LandcoverTextureArray,
            uv, classId, dx, dy).rgb;
    }
    return sampled;
}

float3 TerrainSampleStochastic(float classId, float2 uv, float2 dx, float2 dy)
{
    float2 skew = float2(uv.x - uv.y * 0.57735026919, uv.y * 1.15470053838);
    float2 cell = floor(skew);
    float2 f = frac(skew);
    float3 bary = float3(f, 1.0 - f.x - f.y);
    float2 a = cell, b = cell, c = cell;
    float3 weights = 0.0;
    if (bary.z > 0.0)
    {
        a = cell; b = cell + float2(0, 1); c = cell + float2(1, 0);
        weights = float3(bary.z, bary.y, bary.x);
    }
    else
    {
        a = cell + 1.0; b = cell + float2(1, 0); c = cell + float2(0, 1);
        weights = float3(-bary.z, 1.0 - bary.y, 1.0 - bary.x);
    }
    return TerrainSampleCell(classId, uv, a, dx, dy) * weights.x
         + TerrainSampleCell(classId, uv, b, dx, dy) * weights.y
         + TerrainSampleCell(classId, uv, c, dx, dy) * weights.z;
}

float2 TerrainGeographicDerivative(float2 derivative)
{
    // Shortest longitude difference keeps mip choice stable at the date line.
    derivative.x -= round(derivative.x);
    return derivative;
}

float3 TerrainSampleContinuousDetail(float classId, float2 geographicUV, float2 dx, float2 dy,
    float2 baseRepeats, float metersPerPixel, float referenceMetersPerPixel, float3 mean)
{
    // Two fixed geographic bands, base and four times wider. Bound the handoff
    // so distant views filter the patterns instead of continually enlarging them.
    float level = min(1.0, max(0.0, log2(max(metersPerPixel / referenceMetersPerPixel, 1.0)) * 0.5));
    float band = floor(level);
    float blend = smoothstep(0.0, 1.0, frac(level));
    float2 repeats = baseRepeats * exp2(-2.0 * band);
    float3 detail = TerrainSampleStochastic(classId, geographicUV * repeats, dx * repeats, dy * repeats);
    [branch] if (blend > 0.001)
    {
        float2 nextRepeats = repeats * 0.25;
        float3 nextDetail = TerrainSampleStochastic(classId, geographicUV * nextRepeats,
            dx * nextRepeats, dy * nextRepeats);
        detail = lerp(detail, nextDetail, blend);
        // Independent patterns lose contrast halfway through a crossfade. Keep
        // their variance approximately constant so the handoff does not look soft.
        float contrast = rsqrt(max(0.5, (1.0 - blend) * (1.0 - blend) + blend * blend));
        detail = mean + (detail - mean) * contrast;
    }
    return detail;
}

struct TerrainSurface
{
    float2 uv;
    float2 dx;
    float2 dy;
    float3 distantColor;
    float landCoverage;
    float metersPerPixel;
    float coverage;
    float reference;
    float relief;
};

float TerrainRelief(float2 uv, float2 groundMeters, float metersPerPixel)
{
    float strength = saturate(_TerrainReliefStrength)
        * (1.0 - smoothstep(1500.0, 12000.0, metersPerPixel));
    float relief = 1.0;
    [branch] if (strength > 0.001)
    {
        // Use the existing elevation raster, independent of mesh density and camera
        // orientation. Explicit clamping prevents opposite poles wrapping together.
        float2 stepUV = max(_TerrainElevationTex_TexelSize.xy, metersPerPixel / groundMeters);
        float west = SAMPLE_TEXTURE2D_LOD(_TerrainElevationTex, sampler_TerrainElevationTex,
            float2(frac(uv.x-stepUV.x), saturate(uv.y)), 0).r;
        float east = SAMPLE_TEXTURE2D_LOD(_TerrainElevationTex, sampler_TerrainElevationTex,
            float2(frac(uv.x+stepUV.x), saturate(uv.y)), 0).r;
        float southV = saturate(uv.y-stepUV.y), northV = saturate(uv.y+stepUV.y);
        float south = SAMPLE_TEXTURE2D_LOD(_TerrainElevationTex, sampler_TerrainElevationTex,
            float2(frac(uv.x), southV), 0).r;
        float north = SAMPLE_TEXTURE2D_LOD(_TerrainElevationTex, sampler_TerrainElevationTex,
            float2(frac(uv.x), northV), 0).r;
        float2 distanceMeters = float2(2.0*stepUV.x, max(northV-southV, 0.000001)) * groundMeters;
        float2 slope = float2(east-west, north-south) * _TerrainElevationRangeMeters / max(distanceMeters, 1.0);
        float3 normal = normalize(float3(-slope, 1.0));
        float3 light = normalize(float3(-0.5, 0.65, 0.75));
        // Neutral on flat ground; albedo contains no baked elevation shading.
        relief = clamp(1.0 + (dot(normal, light)-light.z)*strength*1.6, 0.80, 1.16);
    }
    return relief;
}

bool TerrainShouldBlendClasses(float2 uv)
{
    float2 dx = TerrainGeographicDerivative(ddx(uv));
    float2 dy = TerrainGeographicDerivative(ddy(uv));
    float footprint = max(length(dx * _TerrainSurfaceTex_TexelSize.zw), length(dy * _TerrainSurfaceTex_TexelSize.zw));
    return _UseNaturalTerrain > 0.5 && _TerrainBoundaryBlend > 0.001 && footprint < 0.99;
}

TerrainSurface TerrainPrepareSurface(float2 uv)
{
    TerrainSurface surface = (TerrainSurface)0;
    surface.uv = uv;
    surface.dx = TerrainGeographicDerivative(ddx(uv));
    surface.dy = TerrainGeographicDerivative(ddy(uv));
    float4 distant = SAMPLE_TEXTURE2D_GRAD(_TerrainSurfaceTex, sampler_TerrainSurfaceTex,
        uv, surface.dx, surface.dy);
    surface.distantColor = distant.rgb;
    surface.landCoverage = distant.a;
    float cosLatitude = max(0.05, cos((uv.y - 0.5) * 3.14159265359));
    float2 groundMeters = float2(40075016.686 * cosLatitude, 20037508.343);
    surface.metersPerPixel = max(length(surface.dx * groundMeters), length(surface.dy * groundMeters));
    surface.reference = max(_TerrainDetailFadeStart, 0.01);
    float footprint = max(length(surface.dx * _TerrainSurfaceTex_TexelSize.zw),
        length(surface.dy * _TerrainSurfaceTex_TexelSize.zw));
    surface.coverage = 1.0 - smoothstep(0.25, 1.0, footprint);
    surface.relief = _UseNaturalTerrain > 0.5 ? TerrainRelief(uv, groundMeters, surface.metersPerPixel) : 1.0;
    return surface;
}

float TerrainClassSpan(float classId)
{
    // Calibrated to features in the source images: canopy crowns and buildings
    // are tens of meters, fields hundreds, and rock/snow formations broader.
    float scale = 1.0;
    if (classId < 0.5 || classId > 14.5) scale = 2.0;
    else if (classId < 5.5) scale = abs(classId - 2.0) < 0.5 ? 0.5 : 0.25;
    else if (classId < 7.5) scale = 0.25;
    else if (classId < 9.5) scale = 0.5;
    else if (abs(classId - 13.0) < 0.5) scale = 0.75;
    return max(1.0, _TerrainDetailMeters * scale);
}

// Fixed broad patterns use a coarse mip of the same terrain artwork. Individual
// trees, fields, and buildings never grow indefinitely with camera distance.
float3 TerrainTextureAlbedo(float classId, TerrainSurface surface)
{
    float3 mean = SAMPLE_TEXTURE2D_ARRAY_LOD(_LandcoverTextureArray, sampler_LandcoverTextureArray,
        float2(0.5, 0.5), classId, 10).rgb;
    float2 globeMeters = float2(40075016.686, 20037508.343);
    float2 macroRepeats = globeMeters / 32768.0;
    float2 macroDx = float2(max(length(surface.dx * macroRepeats), 0.03125), 0);
    float2 macroDy = float2(0, max(length(surface.dy * macroRepeats), 0.03125));
    float3 broad = TerrainSampleStochastic(classId, surface.uv * macroRepeats, macroDx, macroDy);
    float3 broadRatio = clamp(broad / max(mean, 0.005), 0.65, 1.5);
    float3 baseColor = mean * lerp(1.0, broadRatio, 0.35);
    float2 repeats = globeMeters / TerrainClassSpan(classId);
    float3 detail = TerrainSampleContinuousDetail(classId, surface.uv, surface.dx, surface.dy,
        repeats, surface.metersPerPixel, surface.reference, mean);
    float3 lumaWeights = float3(0.2126, 0.7152, 0.0722);
    float meanLuma = max(dot(mean, lumaWeights), 0.005);
    float luminanceDetail = dot(detail, lumaWeights) / meanLuma;
    float3 modulation = clamp(lerp(luminanceDetail.xxx, detail / max(mean, 0.005), 0.25), 0.4, 2.2);
    return baseColor * lerp(1.0, modulation, saturate(_TerrainDetailStrength));
}

float3 TerrainClassColor(float landcoverId, TerrainSurface surface)
{
    float classId = clamp(round(landcoverId), 0.0, 17.0);
    float2 globeMeters = float2(40075016.686, 20037508.343);
    float3 result = 0.0;
    if (_UseNaturalTerrain < 0.5)
    {
        float2 repeats = globeMeters / 100000.0;
        result = TerrainSampleStochastic(classId, surface.uv * repeats, surface.dx * repeats, surface.dy * repeats);
    }
    else
    {
        result = surface.distantColor * lerp(1.0, surface.relief, surface.landCoverage);
        [branch] if (surface.coverage > 0.001)
            result = lerp(result, TerrainTextureAlbedo(classId, surface)
                * (classId < 16.5 ? surface.relief : 1.0), surface.coverage);
    }
    return result;
}

// Filter albedo colors before minifying landcover; never average class numbers.
bool TerrainDistantColor(float2 uv, out float3 color)
{
    color = 0;
    float2 dx = TerrainGeographicDerivative(ddx(uv));
    float2 dy = TerrainGeographicDerivative(ddy(uv));
    float footprint = max(length(dx * _TerrainSurfaceTex_TexelSize.zw), length(dy * _TerrainSurfaceTex_TexelSize.zw));
    // Near views prepare their surface in the class resolver. Avoid duplicating
    // its surface-map sample and four elevation lookups merely to test distance.
    if (_UseNaturalTerrain < 0.5 || 1.0-smoothstep(0.25, 1.0, footprint) > 0.001) return false;
    TerrainSurface surface = TerrainPrepareSurface(uv);
    color = surface.distantColor;
    color *= lerp(1.0, surface.relief, surface.landCoverage);
    return true;
}

float3 ResolveNaturalTerrain(float landcoverId, float2 geographicUV)
{
    return TerrainClassColor(landcoverId, TerrainPrepareSurface(geographicUV));
}

float2 TerrainBoundaryNoise(float2 uv)
{
    // Smooth geographic variation confined to the edge band, never screen noise.
    float2 p = uv * float2(40075016.686, 20037508.343) / 600.0;
    float2 cell = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(TerrainHash(cell), TerrainHash(cell + float2(1,0)), f.x),
        lerp(TerrainHash(cell + float2(0,1)), TerrainHash(cell + 1.0), f.x), f.y);
}

float TerrainNoise(float2 p)
{
    float2 cell = floor(p), f = frac(p);
    f = f*f*(3.0-2.0*f);
    return lerp(lerp(TerrainHash(cell).x, TerrainHash(cell+float2(1,0)).x, f.x),
        lerp(TerrainHash(cell+float2(0,1)).x, TerrainHash(cell+1.0).x, f.x), f.y);
}

float TerrainVegetation(float id)
{
    return id >= 0.5 && id < 5.5 ? 1.0 : (id >= 5.5 && id < 9.5 ? 0.45 : 0.0);
}

float3 ResolveTerrainNeighborhood(float centerId, float4 ids, float2 fraction, float2 uv, float2 cellGeoSpan)
{
    TerrainSurface surface = TerrainPrepareSurface(uv);
    float3 result = surface.distantColor;
    float latitudeScale = max(0.05, cos((uv.y - 0.5) * 3.14159265359));
    float2 cellMeters = abs(cellGeoSpan) * float2(40075016.686 * latitudeScale, 20037508.343);
    float minCellMeters = max(1.0, min(cellMeters.x, cellMeters.y));
    float noiseScale = min(1.0, 2000.0 / minCellMeters);
    float2 edge = saturate(fraction + (TerrainBoundaryNoise(uv) - 0.5)
        * 0.12 * noiseScale * saturate(_TerrainBoundaryBlend) * 4.0 * fraction * (1.0 - fraction));
    float4 weights = float4((1-edge.x)*(1-edge.y), edge.x*(1-edge.y), (1-edge.x)*edge.y, edge.x*edge.y);
    // Merge matching classes before shading. Interiors cost one class; a common
    // two-biome edge costs two. Only rare multi-biome junctions need more.
    [unroll] for (int i = 0; i < 4; i++)
    {
        [unroll] for (int j = i + 1; j < 4; j++)
        {
            if (ids[i] == ids[j]) { weights[i] += weights[j]; weights[j] = 0; }
        }
        if (ids[i] > 17.5) weights[i] = 0;
    }
    // Round corners using the interpolated class coverage, then narrow the
    // texture crossfade around the winning classes. This preserves sharp detail
    // and small water features instead of washing out an entire source pixel.
    float4 water = float4(ids.x == 17.0, ids.y == 17.0, ids.z == 17.0, ids.w == 17.0);
    float validTotal = max(dot(weights, 1.0), 0.00001);
    float waterCoverage = dot(weights, water) / validTotal;
    float4 active = step(0.00001, weights);
    // Only mixed vegetation/open-ground neighborhoods need ecotone noise.
    float4 vegetation = float4(TerrainVegetation(ids.x), TerrainVegetation(ids.y),
        TerrainVegetation(ids.z), TerrainVegetation(ids.w));
    float vegetationCoverage = dot(weights, vegetation) / validTotal;
    float largestRaw = max(max(weights.x, weights.y), max(weights.z, weights.w));
    float vegetationEffect = 0.0;
    [branch] if (vegetationCoverage > 0.001 && vegetationCoverage < 0.999 && waterCoverage < 0.999
        && largestRaw < validTotal*0.999)
    {
        float2 meters = uv * float2(40075016.686, 20037508.343);
        float patches = TerrainNoise(meters/180.0)*0.65 + TerrainNoise(meters/55.0)*0.35;
        float resolved = 1.0-smoothstep(15.0, 100.0, surface.metersPerPixel);
        float edgeBand = 4.0*vegetationCoverage*(1.0-vegetationCoverage);
        vegetationEffect = saturate(_TerrainVegetationEdges)*resolved*edgeBand;
        float bias = (patches-0.5)*min(0.40, 600.0/minCellMeters)*vegetationEffect;
        // Competing coverage creates actual patches of each surface, instead of
        // only fading their colors. Never introduce an absent terrain class.
        weights = max(0.0, weights + (vegetation-vegetationCoverage)*bias*active*(1.0-water));
    }
    float largest = max(max(weights.x, weights.y), max(weights.z, weights.w));
    float vegetationCrossfade = clamp(240.0/minCellMeters, 0.006, 0.08);
    float crossfade = lerp(0.2, vegetationCrossfade, saturate(vegetationEffect*1.5))
        *saturate(_TerrainBoundaryBlend);
    weights = max(weights - largest + crossfade, 0.0) * active;
    weights *= weights;
    [branch] if (waterCoverage > 0.00001 && waterCoverage < 0.99999)
    {
        float2 meters = uv * float2(40075016.686, 20037508.343);
        float shoreNoise = TerrainNoise(meters/110.0)*0.7 + TerrainNoise(meters/35.0)*0.3;
        float resolved = 1.0-smoothstep(10.0, 80.0, surface.metersPerPixel);
        float offset = (shoreNoise-0.5)*min(0.08, 80.0/minCellMeters)
            *saturate(_TerrainShorelineDetail)*resolved*4.0*waterCoverage*(1.0-waterCoverage);
        // A narrow antialiased shoreline, about a screen pixel per side. It does
        // not inherit the much wider vegetation crossfade or erase small lakes.
        float aa = clamp(surface.metersPerPixel/minCellMeters*0.75, 0.002, 0.10);
        float shore = smoothstep(0.5-aa, 0.5+aa, waterCoverage+offset);
        float oldLand = dot(weights, 1.0-water);
        // Sharpening can remove all land at a multi-class junction. Recover its
        // original coverage so the shoreline still has a valid surface to meet.
        float4 land = weights*(1.0-water);
        if (oldLand < 0.00001) { land = active*(1.0-water); oldLand = dot(land,1.0); }
        float4 waterWeight = active*water;
        weights = land/max(oldLand,0.00001)*(1.0-shore)
            + waterWeight/max(dot(waterWeight,1.0),0.00001)*shore;
    }
    float total = dot(weights, 1.0);
    if (total > 0.0000000001)
    {
        // Narrow edge bands have very small squared weights. Normalize before
        // testing significance, otherwise their valid colors can be skipped.
        weights /= total;
        result = 0;
        [loop] for (int index = 0; index < 4; index++)
        {
            [branch] if (weights[index] > 0.000001)
                result += TerrainClassColor(ids[index], surface) * weights[index];
        }
    }
    else result = TerrainClassColor(centerId, surface);
    return result;
}

#endif
