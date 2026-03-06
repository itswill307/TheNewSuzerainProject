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
        [Toggle] _UseOverlayAlpha ("Use Overlay Alpha As Valid Mask", Float) = 1
        _FallbackColor ("Fallback Color", Color) = (0.2, 0.2, 0.2, 1)
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

            CBUFFER_START(UnityPerMaterial)
                float4 _overlayTranslationAndScale_0;
                float _overlayTextureCoordinateIndex_0;
                float _OverlayIndexedSRGB;
                float _FlipOverlayV;
                float _UseOverlayAlpha;
                float4 _FallbackColor;
            CBUFFER_END

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
                float2 overlayUV : TEXCOORD0;
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

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);

                float2 rawUV = SelectOverlayUV(input, _overlayTextureCoordinateIndex_0);
                float2 overlayUV = rawUV * _overlayTranslationAndScale_0.zw + _overlayTranslationAndScale_0.xy;
                if (_FlipOverlayV > 0.5)
                {
                    overlayUV.y = 1.0 - overlayUV.y;
                }
                output.overlayUV = overlayUV;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float2 snappedOverlayUV = SnapUVToTexelCenter(input.overlayUV, _overlayTexture_0_TexelSize);
                float4 overlaySample = SAMPLE_TEXTURE2D_LOD(_overlayTexture_0, sampler_overlayTexture_0, snappedOverlayUV, 0);

                float idx = DecodeLandcoverIndex(overlaySample.r);
                float lutU = (idx + 0.5) / 256.0;
                float3 classColor = SAMPLE_TEXTURE2D(_LandcoverLUT, sampler_LandcoverLUT, float2(lutU, 0.5)).rgb;
                return float4(classColor, 1.0);
            }
            ENDHLSL
        }
    }
}
