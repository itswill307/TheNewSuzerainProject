Shader "CesiumTerrainShader"
{
    Properties
    {
        [Header(Cesium Overlay Slot 0)]
        _overlayTexture_0 ("Overlay Texture 0", 2D) = "white" {}
        _overlayTextureCoordinateIndex_0 ("Overlay UV Set", Float) = 0
        _overlayTranslationAndScale_0 ("Overlay Translation (XY) Scale (ZW)", Vector) = (0, 0, 1, 1)
        [HideInInspector] _OverlayGeoUvScaleOffset_0 ("Overlay Geo UV Scale Offset 0", Vector) = (1, 1, 0, 0)
        [HideInInspector] _OverlayGeoRectValid_0 ("Overlay Geo Rect Valid 0", Float) = 0

        [Header(Landcover)]
        _LandcoverLUT ("Landcover LUT (256x1)", 2D) = "white" {}
        [NoScaleOffset] _LandcoverTextureArray ("Landcover Texture Array", 2DArray) = "" {}
        [NoScaleOffset] _GrasslandTextureVariants ("Grassland Texture Variants", 2DArray) = "" {}
        [HideInInspector] _GrasslandVariantCount ("Grassland Variant Count", Float) = 0
        [Toggle] _UseLandcoverTextures ("Use Landcover Textures", Float) = 0
        [Toggle] _FlipOverlayV ("Flip Overlay V", Float) = 1
        [Header(Selection)]
        _ProvinceIDTex ("Province ID Map", 2D) = "black" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalRenderPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Cull Off
        ZWrite On
        ZTest LEqual

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_overlayTexture_0);
            SAMPLER(sampler_overlayTexture_0);
            float4 _overlayTexture_0_TexelSize;

            TEXTURE2D(_LandcoverLUT);
            SAMPLER(sampler_LandcoverLUT);
            TEXTURE2D_ARRAY(_LandcoverTextureArray);
            SAMPLER(sampler_LandcoverTextureArray);
            TEXTURE2D_ARRAY(_GrasslandTextureVariants);
            SAMPLER(sampler_GrasslandTextureVariants);
            TEXTURE2D(_ProvinceIDTex);
            SAMPLER(sampler_ProvinceIDTex);

            // Set globally via Shader.SetGlobal by CesiumProvinceShaderSetup / ProvincePickerCesium.
            // NOT in Properties block so Cesium per-tile material clones don't override with defaults.
            float4 _CesiumGlobeCenterWorld;
            float4 _CesiumGlobeCenterEcef;
            float4 _CesiumOneOverRadiiSquared;
            float4x4 _CesiumWorldDirToEcef;
            float4 _HighlightColor;
            float4 _HoverColor;
            float _SelectedID;
            float _HoverID;

            CBUFFER_START(UnityPerMaterial)
                float4 _overlayTranslationAndScale_0;
                float4 _OverlayGeoUvScaleOffset_0;
                float _overlayTextureCoordinateIndex_0;
                float _UseLandcoverTextures;
                float _GrasslandVariantCount;
                float _FlipOverlayV;
                float _OverlayGeoRectValid_0;
            CBUFFER_END

            static const float PI_ = 3.14159265359;
            static const float TWO_PI_ = 6.28318530718;
            static const float LANDCOVER_METERS_PER_REPEAT = 100000.0;
            static const float LANDCOVER_STOCHASTIC_CELL_REPEATS = 1.0;
            static const float EARTH_EQUATORIAL_CIRCUMFERENCE_METERS = 40075016.686;
            static const float EARTH_NORTH_SOUTH_METERS = 20037508.343;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float2 uv2 : TEXCOORD2;
                float2 uv3 : TEXCOORD3;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 overlayUV  : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float2 overlayGeoUV : TEXCOORD2;
            };

            float2 SelectOverlayUV(Attributes input, float indexFloat)
            {
                float index = round(indexFloat);
                if (index < 0.5) return input.uv0;
                if (index < 1.5) return input.uv1;
                if (index < 2.5) return input.uv2;
                return input.uv3;
            }

            float LinearToSrgbScalar(float linearValue)
            {
                float v = saturate(linearValue);
                return (v <= 0.0031308) ? (12.92 * v) : (1.055 * pow(v, 1.0 / 2.4) - 0.055);
            }

            float DecodeLandcoverIndex(float encodedIndex)
            {
                float idx01 = LinearToSrgbScalar(encodedIndex);
                return round(idx01 * 255.0);
            }

            float2 SnapUVToTexelCenter(float2 uv, float4 texelSize)
            {
                float2 texSize = max(texelSize.zw, float2(1.0, 1.0));
                float2 uvInTexels = uv * texSize - 0.5;
                float2 nearestTexel = floor(uvInTexels + 0.5);
                return (nearestTexel + 0.5) / texSize;
            }

            float2 OverlayUVToGeographicUV(float2 overlayUV)
            {
                return overlayUV * _OverlayGeoUvScaleOffset_0.xy + _OverlayGeoUvScaleOffset_0.zw;
            }

            float2 GetLandcoverTextureUV(float2 geographicUV)
            {
                float2 repeats = float2(
                    EARTH_EQUATORIAL_CIRCUMFERENCE_METERS,
                    EARTH_NORTH_SOUTH_METERS
                ) / LANDCOVER_METERS_PER_REPEAT;
                return float2(geographicUV.x * repeats.x, geographicUV.y * repeats.y);
            }

            float2 Hash22(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.xx + p3.yz) * p3.zy);
            }

            float2 SkewLandcoverStochasticUV(float2 uv)
            {
                return float2(
                    uv.x - uv.y * 0.57735026919,
                    uv.y * 1.15470053838
                );
            }

            float2 UnskewLandcoverStochasticCell(float2 cell)
            {
                return float2(
                    cell.x + cell.y * 0.5,
                    cell.y * 0.86602540378
                ) * LANDCOVER_STOCHASTIC_CELL_REPEATS;
            }

            float2 TransformLandcoverUV(float2 tiledUV, float2 cell, float classId)
            {
                float2 seed = cell + float2(classId * 17.17, classId * 31.31);
                float2 hash = Hash22(seed);
                float2 cellOrigin = UnskewLandcoverStochasticCell(cell);
                float2 cellCenter = LANDCOVER_STOCHASTIC_CELL_REPEATS * 0.5;
                float2 uv = tiledUV - cellOrigin - cellCenter;
                float transform = floor(hash.x * 8.0);

                if (transform < 1.0) uv = uv;
                else if (transform < 2.0) uv = float2(-uv.x, uv.y);
                else if (transform < 3.0) uv = float2(uv.x, -uv.y);
                else if (transform < 4.0) uv = -uv;
                else if (transform < 5.0) uv = uv.yx;
                else if (transform < 6.0) uv = float2(-uv.y, uv.x);
                else if (transform < 7.0) uv = float2(uv.y, -uv.x);
                else if (transform >= 7.0) uv = -uv.yx;

                return uv + cellCenter + (hash - 0.5) * 128.0;
            }

            float GrasslandVariantIndex(float2 cell, float classId)
            {
                float variantCount = max(0.0, _GrasslandVariantCount);
                float2 seed = cell + float2(classId * 41.41, classId * 59.59);
                return clamp(floor(Hash22(seed).y * variantCount), 0.0, max(0.0, variantCount - 1.0));
            }

            float3 SampleLandcoverTextureRaw(float classId, float2 tiledUV, float2 cell)
            {
                if (abs(classId - 10.0) < 0.5 && _GrasslandVariantCount > 0.5)
                {
                    return SAMPLE_TEXTURE2D_ARRAY(
                        _GrasslandTextureVariants,
                        sampler_GrasslandTextureVariants,
                        tiledUV,
                        GrasslandVariantIndex(cell, classId)).rgb;
                }

                return SAMPLE_TEXTURE2D_ARRAY(_LandcoverTextureArray, sampler_LandcoverTextureArray, tiledUV, classId).rgb;
            }

            float3 SampleLandcoverTextureById(float landcoverId, float2 tiledUV)
            {
                float classId = clamp(round(landcoverId), 0.0, 17.0);

                float2 stochasticUV = SkewLandcoverStochasticUV(tiledUV / LANDCOVER_STOCHASTIC_CELL_REPEATS);
                float2 cell = floor(stochasticUV);
                float2 f = frac(stochasticUV);
                float3 bary = float3(f, 1.0 - f.x - f.y);

                float2 cellA;
                float2 cellB;
                float2 cellC;
                float3 weights;
                if (bary.z > 0.0)
                {
                    cellA = cell;
                    cellB = cell + float2(0.0, 1.0);
                    cellC = cell + float2(1.0, 0.0);
                    weights = float3(bary.z, bary.y, bary.x);
                }
                else
                {
                    cellA = cell + float2(1.0, 1.0);
                    cellB = cell + float2(1.0, 0.0);
                    cellC = cell + float2(0.0, 1.0);
                    weights = float3(-bary.z, 1.0 - bary.y, 1.0 - bary.x);
                }

                weights /= max(1e-5, weights.x + weights.y + weights.z);

                float3 a = SampleLandcoverTextureRaw(classId, TransformLandcoverUV(tiledUV, cellA, classId), cellA);
                float3 b = SampleLandcoverTextureRaw(classId, TransformLandcoverUV(tiledUV, cellB, classId), cellB);
                float3 c = SampleLandcoverTextureRaw(classId, TransformLandcoverUV(tiledUV, cellC, classId), cellC);
                return a * weights.x + b * weights.y + c * weights.z;
            }

            float3 ResolveLandcoverColor(float landcoverId, float2 geographicUV)
            {
                float lutU = (landcoverId + 0.5) / 256.0;
                float3 classColor = SAMPLE_TEXTURE2D(_LandcoverLUT, sampler_LandcoverLUT, float2(lutU, 0.5)).rgb;
                if (_UseLandcoverTextures <= 0.5 || landcoverId > 17.5)
                {
                    return classColor;
                }

                return SampleLandcoverTextureById(landcoverId, GetLandcoverTextureUV(geographicUV));
            }

            inline uint DecodeProvinceId24(float3 rgb)
            {
                float3 rgb255 = round(saturate(rgb) * 255.0);
                uint r8 = (uint)rgb255.r;
                uint g8 = (uint)rgb255.g;
                uint b8 = (uint)rgb255.b;
                return (r8 | (g8 << 8) | (b8 << 16));
            }

            float ProvinceSelectionMask(float3 idRGB, float selectedId)
            {
                float maskEnabled = step(0.0, selectedId + 0.5);
                uint provinceId = DecodeProvinceId24(idRGB);
                uint selectedIdInt = (uint)max(0.0, round(selectedId));
                return maskEnabled * ((provinceId == selectedIdInt) ? 1.0 : 0.0);
            }

            float3 ApplyProvinceHoverSelect(float3 idRGB, float3 baseColor)
            {
                float selectMask = ProvinceSelectionMask(idRGB, _SelectedID);
                float hoverEnabled = step(0.0, _HoverID + 0.5);
                float hoverMask = ProvinceSelectionMask(idRGB, _HoverID) * hoverEnabled;

                float3 color = baseColor;
                color = lerp(color, color + _HoverColor.rgb * _HoverColor.a, hoverMask);
                color = lerp(color, color + _HighlightColor.rgb * _HighlightColor.a, selectMask);
                return color;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);

                // Overlay UV for landcover
                float2 rawUV = SelectOverlayUV(input, _overlayTextureCoordinateIndex_0);
                float2 overlayUV = rawUV * _overlayTranslationAndScale_0.zw + _overlayTranslationAndScale_0.xy;
                if (_FlipOverlayV > 0.5)
                    overlayUV.y = 1.0 - overlayUV.y;
                output.overlayUV = overlayUV;
                output.overlayGeoUV = OverlayUVToGeographicUV(overlayUV);

                // Geographic UV for the global province ID texture:
                // world pos → ECEF direction → lat/lon → equirectangular UV
                output.positionWS = TransformObjectToWorld(input.positionOS);

                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                // Landcover class from overlay
                float2 snappedOverlayUV = SnapUVToTexelCenter(input.overlayUV, _overlayTexture_0_TexelSize);
                float4 overlaySample = SAMPLE_TEXTURE2D_LOD(_overlayTexture_0, sampler_overlayTexture_0, snappedOverlayUV, 0);
                float idx = DecodeLandcoverIndex(overlaySample.r);

                // Province selection — force mip 0 because provinceUV derives from
                // world position, giving wrong screen-space derivatives for auto-mip.
                float3 ecefPos = _CesiumGlobeCenterEcef.xyz +
                                 mul((float3x3)_CesiumWorldDirToEcef,
                                     input.positionWS - _CesiumGlobeCenterWorld.xyz);
                float lon = atan2(ecefPos.y, ecefPos.x);
                float3 geodeticNormal = normalize(ecefPos * _CesiumOneOverRadiiSquared.xyz);
                float lat = asin(clamp(geodeticNormal.z, -1.0, 1.0));
                float2 provinceUV = float2(lon / TWO_PI_ + 0.5, lat / PI_ + 0.5);
                float2 landcoverDetailUV = _OverlayGeoRectValid_0 > 0.5 ? input.overlayGeoUV : provinceUV;
                float3 classColor = ResolveLandcoverColor(idx, landcoverDetailUV);
                float4 provinceSample = SAMPLE_TEXTURE2D_LOD(_ProvinceIDTex, sampler_ProvinceIDTex, provinceUV, 0);
                float3 outColor = ApplyProvinceHoverSelect(provinceSample.rgb, classColor);

                // DEBUG: uncomment one of these to diagnose province selection issues:
                // return float4(provinceUV, 0.0, 1.0);  // UV mapping (should look like a world map)
                // return float4(provinceSample.rgb, 1.0);       // raw province IDs (colored regions)

                return float4(outColor, 1.0);
            }
            ENDHLSL
        }
    }
}
