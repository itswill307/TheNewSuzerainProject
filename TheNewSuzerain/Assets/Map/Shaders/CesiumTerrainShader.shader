Shader "CesiumLandcoverOverlayLUT"
{
    Properties
    {
        [Header(Cesium Overlay Slot 0)]
        _overlayTexture_0 ("Overlay Texture 0", 2D) = "white" {}
        _overlayTextureCoordinateIndex_0 ("Overlay UV Set", Float) = 0
        _overlayTranslationAndScale_0 ("Overlay Translation (XY) Scale (ZW)", Vector) = (0, 0, 1, 1)

        [Header(Landcover)]
        _LandcoverLUT ("Landcover LUT (256x1)", 2D) = "white" {}
        [Toggle] _OverlayIndexedSRGB ("Overlay Index Texture Is sRGB", Float) = 1
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
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_overlayTexture_0);
            SAMPLER(sampler_overlayTexture_0);
            float4 _overlayTexture_0_TexelSize;

            TEXTURE2D(_LandcoverLUT);
            SAMPLER(sampler_LandcoverLUT);

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
                float _overlayTextureCoordinateIndex_0;
                float _OverlayIndexedSRGB;
                float _FlipOverlayV;
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
                float idx01 = saturate(encodedIndex);
                if (_OverlayIndexedSRGB > 0.5)
                {
                    idx01 = LinearToSrgbScalar(idx01);
                }
                return round(idx01 * 255.0);
            }

            float2 SnapUVToTexelCenter(float2 uv, float4 texelSize)
            {
                float2 texSize = max(texelSize.zw, float2(1.0, 1.0));
                float2 uvInTexels = uv * texSize - 0.5;
                float2 nearestTexel = floor(uvInTexels + 0.5);
                return (nearestTexel + 0.5) / texSize;
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

                // Geographic UV for the global province ID texture:
                // world pos → ECEF direction → lat/lon → equirectangular UV
                output.positionWS = TransformObjectToWorld(input.positionOS);

                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                // Landcover color from overlay
                float2 snappedOverlayUV = SnapUVToTexelCenter(input.overlayUV, _overlayTexture_0_TexelSize);
                float4 overlaySample = SAMPLE_TEXTURE2D_LOD(_overlayTexture_0, sampler_overlayTexture_0, snappedOverlayUV, 0);
                float idx = DecodeLandcoverIndex(overlaySample.r);
                float lutU = (idx + 0.5) / 256.0;
                float3 classColor = SAMPLE_TEXTURE2D(_LandcoverLUT, sampler_LandcoverLUT, float2(lutU, 0.5)).rgb;

                // Province selection — force mip 0 because provinceUV derives from
                // world position, giving wrong screen-space derivatives for auto-mip.
                float3 ecefPos = _CesiumGlobeCenterEcef.xyz +
                                 mul((float3x3)_CesiumWorldDirToEcef,
                                     input.positionWS - _CesiumGlobeCenterWorld.xyz);
                float lon = atan2(ecefPos.y, ecefPos.x);
                float3 geodeticNormal = normalize(ecefPos * _CesiumOneOverRadiiSquared.xyz);
                float lat = asin(clamp(geodeticNormal.z, -1.0, 1.0));
                float2 provinceUV = float2(lon / TWO_PI_ + 0.5, lat / PI_ + 0.5);
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
