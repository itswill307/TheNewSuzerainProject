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
        [Header(Natural Terrain)]
        [NoScaleOffset] _TerrainSurfaceTex ("Terrain-Derived Distant Surface", 2D) = "white" {}
        [NoScaleOffset] _TerrainElevationTex ("Relief Elevation", 2D) = "black" {}
        [Toggle] _UseNaturalTerrain ("Use Natural Terrain", Float) = 1
        _TerrainDetailMeters ("Detail Base Span (meters)", Float) = 2048
        [Range(0, 1)] _TerrainDetailStrength ("Detail Strength", Float) = 0.75
        [Range(0, 1)] _TerrainBoundaryBlend ("Terrain Boundary Blend", Float) = 1
        [Range(0, 1)] _TerrainVegetationEdges ("Vegetation Edge Patches", Float) = 0.8
        [Range(0, 1)] _TerrainShorelineDetail ("Shoreline Irregularity", Float) = 0.8
        [Range(0, 1)] _TerrainReliefStrength ("Elevation Relief Strength", Float) = 0.35
        [HideInInspector] _TerrainElevationRangeMeters ("Elevation Range (meters)", Float) = 19843
        _TerrainDetailFadeStart ("Close Detail Reference (meters per pixel)", Float) = 15

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
                float _UseNaturalTerrain;
                float _TerrainDetailMeters;
                float _TerrainDetailStrength;
                float _TerrainBoundaryBlend;
                float _TerrainVegetationEdges;
                float _TerrainShorelineDetail;
                float _TerrainReliefStrength;
                float _TerrainElevationRangeMeters;
                float _TerrainDetailFadeStart;
            CBUFFER_END

            static const float PI_ = 3.14159265359;
            static const float TWO_PI_ = 6.28318530718;

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

            #include "NaturalTerrain.hlsl"

            float3 ResolveLandcoverColor(float landcoverId, float2 geographicUV, float2 overlayUV)
            {
                float lutU = (landcoverId + 0.5) / 256.0;
                float3 classColor = SAMPLE_TEXTURE2D(_LandcoverLUT, sampler_LandcoverLUT, float2(lutU, 0.5)).rgb;
                if (_UseLandcoverTextures <= 0.5 || landcoverId > 17.5)
                {
                    return classColor;
                }

                [branch] if (TerrainShouldBlendClasses(geographicUV))
                {
                    float2 size = max(_overlayTexture_0_TexelSize.zw, 1.0);
                    float2 pixel = overlayUV * size - 0.5;
                    float2 origin = (floor(pixel) + 0.5) / size;
                    float4 ids = float4(
                        DecodeLandcoverIndex(SAMPLE_TEXTURE2D_LOD(_overlayTexture_0, sampler_overlayTexture_0, origin, 0).r),
                        DecodeLandcoverIndex(SAMPLE_TEXTURE2D_LOD(_overlayTexture_0, sampler_overlayTexture_0, origin + float2(1,0)/size, 0).r),
                        DecodeLandcoverIndex(SAMPLE_TEXTURE2D_LOD(_overlayTexture_0, sampler_overlayTexture_0, origin + float2(0,1)/size, 0).r),
                        DecodeLandcoverIndex(SAMPLE_TEXTURE2D_LOD(_overlayTexture_0, sampler_overlayTexture_0, origin + 1.0/size, 0).r));
                    float2 cellGeoSpan = _OverlayGeoRectValid_0 > 0.5
                        ? abs(_OverlayGeoUvScaleOffset_0.xy)/size : float2(1,1)/size;
                    return ResolveTerrainNeighborhood(landcoverId, ids, frac(pixel), geographicUV, cellGeoSpan);
                }
                return ResolveNaturalTerrain(landcoverId, geographicUV);
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
                float3 classColor;
                if (_UseLandcoverTextures <= 0.5 || !TerrainDistantColor(landcoverDetailUV, classColor))
                    classColor = ResolveLandcoverColor(idx, landcoverDetailUV, input.overlayUV);
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
