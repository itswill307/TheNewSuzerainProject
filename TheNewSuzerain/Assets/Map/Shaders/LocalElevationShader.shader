Shader "LocalElevationShader"
{
    Properties
    {
        [Header(Textures)]
        _MainTex ("Base Color", 2D) = "white" {}
        _LandcoverLUT ("Landcover LUT (256x1)", 2D) = "white" {}
        _ProvinceIDTex ("Province ID Map", 2D) = "white" {}
        _HeightTex ("Local Patch Height Map", 2D) = "black" {}
        [Toggle] _UseLandcoverLUT ("Use Landcover LUT", Float) = 0
        [Toggle] _MainTexIndexedSRGB ("Indexed MainTex Is sRGB", Float) = 1

        [Header(Geometry)]
        _UVOffset ("UV Offset (X,Y)", Vector) = (0, 0, 0, 0)
        _Radius ("World Radius", Float) = 100
        [Range(0, 1)] _Morph ("Projection Morph", Float) = 0
        [Toggle] _Sphere ("Sphere Mode", Float) = 0

        [Header(Local Patch Geo Referencing)]
        _PatchCenterLonDeg ("Patch Center Longitude (deg)", Float) = 0
        _PatchCenterLatDeg ("Patch Center Latitude (deg)", Float) = 0
        _PatchSpanLonDeg ("Patch Width in Longitude (deg)", Float) = 20
        _PatchSpanLatDeg ("Patch Height in Latitude (deg)", Float) = 10
        [Range(0, 0.5)] _PatchEdgeFade ("Patch Edge Fade (UV)", Float) = 0.06
        _PatchSurfaceBiasUnits ("Patch Surface Bias (Units)", Float) = 0.25

        [Header(Height)]
        _HeightMinKm ("Height Min (km)", Float) = -10.994
        _HeightMaxKm ("Height Max (km)", Float) = 8.849
        _HeightExaggeration ("Height Exaggeration", Float) = 1
        _KmPerUnit ("Km Per Unit", Float) = 1

        [Header(Selection)]
        _SelectedID ("Selected ID", Float) = -1
        _HoverID ("Hover ID", Float) = -1
        _HighlightColor ("Highlight Color", Color) = (0, 0, 0, 0)
        _HoverColor ("Hover Color", Color) = (0, 0, 0, 0)
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
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            TEXTURE2D(_LandcoverLUT);
            SAMPLER(sampler_LandcoverLUT);

            TEXTURE2D(_ProvinceIDTex);
            SAMPLER(sampler_ProvinceIDTex);

            TEXTURE2D(_HeightTex);
            SAMPLER(sampler_HeightTex);
            float4 _HeightTex_TexelSize;

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _HighlightColor;
                float4 _HoverColor;
                float2 _UVOffset;
                float _Radius;
                float _Morph;
                float _HeightMinKm;
                float _HeightMaxKm;
                float _HeightExaggeration;
                float _KmPerUnit;
                float _SelectedID;
                float _HoverID;
                float _UseLandcoverLUT;
                float _MainTexIndexedSRGB;
                float _Sphere;
                float _PatchCenterLonDeg;
                float _PatchCenterLatDeg;
                float _PatchSpanLonDeg;
                float _PatchSpanLatDeg;
                float _PatchEdgeFade;
                float _PatchSurfaceBiasUnits;
            CBUFFER_END

            static const float PI_ = 3.14159265359;
            static const float TWO_PI_ = 6.28318530718;
            static const float COS_PHI1_ = 2.0 / PI_;

            inline uint DecodeProvinceId24(float3 rgb)
            {
                float3 rgb255 = round(saturate(rgb) * 255.0);
                uint r8 = (uint)rgb255.r;
                uint g8 = (uint)rgb255.g;
                uint b8 = (uint)rgb255.b;
                return (r8 | (g8 << 8) | (b8 << 16));
            }

            void ProvinceIdMaskFromRGB_float(float3 idRGB, float selectedId, out float mask, out float idOut)
            {
                uint provinceId = DecodeProvinceId24(idRGB);
                idOut = (float)provinceId;
                float maskEnabled = step(0.0, selectedId + 0.5);
                uint selectedIdInt = (uint)max(0.0, round(selectedId));
                mask = maskEnabled * ((provinceId == selectedIdInt) ? 1.0 : 0.0);
            }

            void ProvinceHoverSelectFromRGB_float(
                float3 idRGB, float selectedId, float4 highlightColor,
                float hoverId, float4 hoverColor,
                float3 baseColor, out float3 outColor)
            {
                float selectMask, selectedIdDecoded;
                ProvinceIdMaskFromRGB_float(idRGB, selectedId, selectMask, selectedIdDecoded);

                float hoverMask, hoverIdDecoded;
                ProvinceIdMaskFromRGB_float(idRGB, hoverId, hoverMask, hoverIdDecoded);
                float hoverEnabled = step(0.0, hoverId + 0.5);
                hoverMask *= hoverEnabled;

                float3 color = baseColor;
                color = lerp(color, color + hoverColor.rgb * hoverColor.a, hoverMask);
                color = lerp(color, color + highlightColor.rgb * highlightColor.a, selectMask);
                outColor = color;
            }

            float WrapPi(float x)
            {
                return frac((x + PI_) / TWO_PI_) * TWO_PI_ - PI_;
            }

            void PatchCoordinatesFromUV(float2 uvLocal, out float longitude, out float latitude, out float2 uvGlobal)
            {
                float centerLon = _PatchCenterLonDeg * (PI_ / 180.0);
                float centerLat = _PatchCenterLatDeg * (PI_ / 180.0);
                float spanLon = max(1e-6, abs(_PatchSpanLonDeg)) * (PI_ / 180.0);
                float spanLat = max(1e-6, abs(_PatchSpanLatDeg)) * (PI_ / 180.0);

                longitude = WrapPi(centerLon + (uvLocal.x - 0.5) * spanLon);
                latitude = clamp(centerLat + (uvLocal.y - 0.5) * spanLat, -0.5 * PI_, 0.5 * PI_);

                uvGlobal = float2(
                    longitude / TWO_PI_ + 0.5,
                    latitude / PI_ + 0.5
                );
            }

            float ComputeEdgeFade(float2 uvLocal)
            {
                float edgeDist = min(min(uvLocal.x, 1.0 - uvLocal.x), min(uvLocal.y, 1.0 - uvLocal.y));
                float fadeWidth = max(1e-5, _PatchEdgeFade);
                return saturate(edgeDist / fadeWidth);
            }

            float3 SampleBaseColor(float2 uv)
            {
                float3 sampled = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb;
                if (_UseLandcoverLUT <= 0.5)
                {
                    return sampled;
                }

                float idx01 = saturate(sampled.r);
                if (_MainTexIndexedSRGB > 0.5)
                {
                    idx01 = LinearToSRGB(float3(idx01, idx01, idx01)).r;
                }

                float idx = round(saturate(idx01) * 255.0);
                float lutU = (idx + 0.5) / 256.0;
                return SAMPLE_TEXTURE2D(_LandcoverLUT, sampler_LandcoverLUT, float2(lutU, 0.5)).rgb;
            }

            float3 EvaluateDisplacedPosition(
                float2 uvLocal,
                out float2 uvGlobal)
            {
                float sphereLerp = saturate(_Sphere);

                float longitude;
                float latitude;
                PatchCoordinatesFromUV(uvLocal, longitude, latitude, uvGlobal);

                float3 equirectangularPos = float3(
                    longitude * _Radius * COS_PHI1_,
                    latitude * _Radius,
                    0.0
                );

                float halfLongitude = 0.5 * longitude;
                float cosLatitude = cos(latitude);
                float sinLatitude = sin(latitude);
                float cosHalfLongitude = cos(halfLongitude);
                float sinHalfLongitude = sin(halfLongitude);
                float alphaAngle = acos(clamp(cosLatitude * cosHalfLongitude, -1.0, 1.0));
                float sinAlpha = sin(alphaAngle);
                float invSincAlpha = (abs(alphaAngle) < 1e-6) ? 1.0 : (alphaAngle / sinAlpha);

                float2 aitoffXY = float2(
                    2.0 * cosLatitude * sinHalfLongitude * invSincAlpha,
                    sinLatitude * invSincAlpha
                );
                float3 aitoffPos = float3(aitoffXY * _Radius, 0.0);
                float3 basePos = lerp(equirectangularPos, aitoffPos, saturate(_Morph));

                float3 spherePosition = float3(
                    cosLatitude * cos(longitude) * _Radius,
                    sinLatitude * _Radius,
                    cosLatitude * sin(longitude) * _Radius
                );
                float3 sphereNormal = normalize(spherePosition);

                // Height map for this shader is patch-local: UV 0..1 spans the local DEM window.
                float heightSample01 = _HeightTex.SampleLevel(sampler_HeightTex, saturate(uvLocal), 0).r;
                float elevationKm = lerp(_HeightMinKm, _HeightMaxKm, heightSample01);
                float heightWorldUnits = (elevationKm / max(1e-6, _KmPerUnit)) * _HeightExaggeration;
                heightWorldUnits *= ComputeEdgeFade(uvLocal);
                float surfaceBias = max(0.0, _PatchSurfaceBiasUnits);

                float3 planePosition = basePos + float3(0.0, 0.0, -(heightWorldUnits + surfaceBias));
                float3 spherePositionWithHeight = spherePosition + sphereNormal * (heightWorldUnits + surfaceBias);
                return lerp(planePosition, spherePositionWithHeight, sphereLerp);
            }

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uvLocal : TEXCOORD0;
                float2 uvGlobal : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float2 uvGlobal;
                float3 posOS = EvaluateDisplacedPosition(input.uv, uvGlobal);

                float2 texelStep = float2(
                    max(1e-6, _HeightTex_TexelSize.x),
                    max(1e-6, _HeightTex_TexelSize.y)
                );
                float2 uvDx = float2(saturate(input.uv.x + texelStep.x), input.uv.y);
                float2 uvDy = float2(input.uv.x, saturate(input.uv.y + texelStep.y));

                float2 uvGlobalDx;
                float2 uvGlobalDy;
                float3 posDx = EvaluateDisplacedPosition(uvDx, uvGlobalDx);
                float3 posDy = EvaluateDisplacedPosition(uvDy, uvGlobalDy);

                float3 normalOS = normalize(cross(posDy - posOS, posDx - posOS));
                if (dot(normalOS, float3(0.0, 0.0, -1.0)) < 0.0)
                {
                    normalOS = -normalOS;
                }

                float3 posWS = TransformObjectToWorld(posOS);
                output.positionCS = TransformWorldToHClip(posWS);
                output.normalWS = TransformObjectToWorldNormal(normalOS);
                output.uvLocal = input.uv;
                output.uvGlobal = uvGlobal;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float sphereLerp = saturate(_Sphere);

                float2 planarUV = float2(
                    frac(input.uvGlobal.x + _UVOffset.x),
                    saturate(input.uvGlobal.y + _UVOffset.y)
                );
                float2 sphereUV = input.uvGlobal;
                float2 sampleUV = lerp(planarUV, sphereUV, sphereLerp);

                float3 baseColor = SampleBaseColor(sampleUV);
                float4 idSample = SAMPLE_TEXTURE2D(_ProvinceIDTex, sampler_ProvinceIDTex, sampleUV);

                float3 outColor;
                ProvinceHoverSelectFromRGB_float(
                    idSample.rgb,
                    _SelectedID,
                    _HighlightColor,
                    _HoverID,
                    _HoverColor,
                    baseColor,
                    outColor
                );

                float3 normalWS = normalize(input.normalWS);
                Light mainLight = GetMainLight();
                float NdotL = saturate(dot(normalWS, mainLight.direction));
                float3 lighting = 0.25.xxx + (NdotL * mainLight.color);

                return float4(outColor * lighting, 1.0);
            }
            ENDHLSL
        }
    }
}
