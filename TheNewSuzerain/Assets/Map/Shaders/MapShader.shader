Shader "Map/MapShader"
{
    Properties
    {
        [Header(Textures)]
        _MainTex ("Base Color", 2D) = "white" {}
        _ProvinceIDTex ("Province ID Map", 2D) = "white" {}
        _HeightTex ("Height Map", 2D) = "black" {}

        [Header(Geometry)]
        _UVOffset ("UV Offset (X,Y)", Vector) = (0, 0, 0, 0)
        _Radius ("World Radius", Float) = 100
        [Range(0, 1)] _Morph ("Projection Morph", Float) = 0
        [Toggle] _Sphere ("Sphere Mode", Float) = 0

        [Header(Height)]
        _HeightScale ("Height Scale", Float) = 1
        _HeightBias ("Height Bias", Float) = 0
        _HeightLOD ("Height LOD", Float) = 0

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
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            TEXTURE2D(_ProvinceIDTex);
            SAMPLER(sampler_ProvinceIDTex);

            TEXTURE2D(_HeightTex);
            SAMPLER(sampler_HeightTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _HighlightColor;
                float4 _HoverColor;
                float2 _UVOffset;
                float _Radius;
                float _Morph;
                float _HeightScale;
                float _HeightBias;
                float _HeightLOD;
                float _SelectedID;
                float _HoverID;
                float _Sphere;
            CBUFFER_END

            static const float PI_ = 3.14159265359;
            static const float COS_PHI1_ = 2.0 / PI_;

            // Province RGB decode helpers (inlined from ProvinceRGBDecode.hlsl)
            inline uint DecodeProvinceId24(float3 rgb)
            {
                float3 rgb255 = round(saturate(rgb) * 255.0);
                uint r8 = (uint)rgb255.r;
                uint g8 = (uint)rgb255.g;
                uint b8 = (uint)rgb255.b;
                return (r8 | (g8 << 8) | (b8 << 16));
            }

            void ProvinceIdFromRGB_float(float3 idRGB, out float idOut)
            {
                idOut = (float)DecodeProvinceId24(idRGB);
            }

            void ProvinceIdMaskFromRGB_float(float3 idRGB, float selectedId, out float mask, out float idOut)
            {
                uint provinceId = DecodeProvinceId24(idRGB);
                idOut = (float)provinceId;
                // Gate: if selectedId < 0, disable the mask entirely
                float maskEnabled = step(0.0, selectedId + 0.5);
                // Avoid wrap when converting negative float to uint
                uint selectedIdInt = (uint)max(0.0, round(selectedId));
                mask = maskEnabled * ((provinceId == selectedIdInt) ? 1.0 : 0.0);
            }

            void ProvinceHighlightFromRGB_float(
                float3 idRGB, float selectedId, float4 highlightColor,
                float3 baseColor, out float3 outColor)
            {
                float selectMask, decodedId;
                ProvinceIdMaskFromRGB_float(idRGB, selectedId, selectMask, decodedId);
                float3 tint = highlightColor.rgb * highlightColor.a;
                outColor = lerp(baseColor, baseColor + tint, selectMask);
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
                // Ensure hover cannot apply when hoverId is negative (e.g. -1 sentinel from CPU)
                float hoverEnabled = step(0.0, hoverId + 0.5);
                hoverMask *= hoverEnabled;

                float3 color = baseColor;
                color = lerp(color, color + hoverColor.rgb * hoverColor.a, hoverMask);
                color = lerp(color, color + highlightColor.rgb * highlightColor.a, selectMask);
                outColor = color;
            }

            void EqrMorph_float(
                float2 UV,
                float Radius,
                float Morph,
                float Sphere,
                Texture2D HeightTex,
                SamplerState HeightSampler,
                float2 UVOffset,
                float HeightScale,
                float HeightBias,
                out float3 OutPosition,
                out float3 OutNormal)
            {
                float2 geometryUV = UV;
                if (Sphere > 0.5)
                {
                    geometryUV = float2(
                        frac(UV.x + UVOffset.x),
                        saturate(UV.y + UVOffset.y)
                    );
                }

                float longitude = (geometryUV.x - 0.5) * (2.0 * PI_);
                float latitude = (geometryUV.y - 0.5) * PI_;

                float3 equirectangularPos = float3(
                    longitude * Radius * COS_PHI1_,
                    latitude * Radius,
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

                float3 aitoffPos = float3(aitoffXY * Radius, 0.0);

                float3 basePos = lerp(equirectangularPos, aitoffPos, saturate(Morph));

                // Aitoff is planar; its geometric normal stays constant.
                float3 planeNormalGeom = float3(0.0, 0.0, 1.0);

                // Sphere position/normal from lat/lon (Y up).
                float cosLatitudeSphere = cos(latitude);
                float sinLatitudeSphere = sin(latitude);
                float cosLongitudeSphere = cos(longitude);
                float sinLongitudeSphere = sin(longitude);
                float3 spherePosition = float3(
                    cosLatitudeSphere * cosLongitudeSphere * Radius,
                    sinLatitudeSphere * Radius,
                    cosLatitudeSphere * sinLongitudeSphere * Radius
                );
                float3 sphereNormal = normalize(spherePosition);

                float2 heightSampleUV = float2(
                    frac(UV.x + UVOffset.x),
                    saturate(UV.y + UVOffset.y)
                );
                float heightSample01 = HeightTex.SampleLevel(HeightSampler, heightSampleUV, 0).r;
                float heightWorldUnits = (heightSample01 - HeightBias) * HeightScale;

                float3 planePosition = basePos + (-planeNormalGeom) * heightWorldUnits;
                float3 planeNormal = -planeNormalGeom;

                float3 spherePositionWithHeight = spherePosition + sphereNormal * heightWorldUnits;
                float3 sphereNormalWithHeight = sphereNormal;

                float sphereLerp = saturate(Sphere);
                OutPosition = lerp(planePosition, spherePositionWithHeight, sphereLerp);
                OutNormal = normalize(lerp(planeNormal, sphereNormalWithHeight, sphereLerp));
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
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 posOS;
                float3 normalOS;
                EqrMorph_float(
                    input.uv,
                    _Radius,
                    _Morph,
                    _Sphere,
                    _HeightTex,
                    sampler_HeightTex,
                    _UVOffset,
                    _HeightScale,
                    _HeightBias,
                    posOS,
                    normalOS
                );

                float3 posWS = TransformObjectToWorld(posOS);
                output.positionCS = TransformWorldToHClip(posWS);
                output.uv = input.uv;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float2 uv = input.uv + _UVOffset;
                float4 baseSample = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);
                float4 idSample = SAMPLE_TEXTURE2D(_ProvinceIDTex, sampler_ProvinceIDTex, uv);

                float3 outColor;
                ProvinceHoverSelectFromRGB_float(
                    idSample.rgb,
                    _SelectedID,
                    _HighlightColor,
                    _HoverID,
                    _HoverColor,
                    baseSample.rgb,
                    outColor
                );

                return float4(outColor, 1.0);
            }
            ENDHLSL
        }
    }
}
