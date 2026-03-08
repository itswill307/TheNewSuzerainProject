Shader "GlobalMapShader"
{
    Properties
    {
        [Header(Textures)]
        _MainTex ("Base Color", 2D) = "white" {}
        _LandcoverLUT ("Landcover LUT (256x1)", 2D) = "white" {}
        _ProvinceIDTex ("Province ID Map", 2D) = "white" {}
        _HeightTex ("Height Map", 2D) = "black" {}
        [Toggle] _UseLandcoverLUT ("Use Landcover LUT", Float) = 0
        [Toggle] _MainTexIndexedSRGB ("Indexed MainTex Is sRGB", Float) = 1

        [Header(Geometry)]
        _UVOffset ("UV Offset (X,Y)", Vector) = (0, 0, 0, 0)
        _Radius ("World Radius", Float) = 100
        [Range(0, 1)] _Morph ("Projection Morph", Float) = 0
        [Toggle] _Sphere ("Sphere Mode", Float) = 0

        [Header(Height)]
        _HeightMinKm ("Height Min (km)", Float) = -10.994
        _HeightMaxKm ("Height Max (km)", Float) = 8.849
        _HeightExaggeration ("Height Exaggeration", Float) = 1
        _KmPerUnit ("Km Per Unit", Float) = 1

        [Header(Local Patch Mask)]
        [Toggle] _LocalPatchMaskEnable ("Mask Global Under Local Patch", Float) = 0
        _LocalPatchCenterLonDeg ("Patch Center Longitude (deg)", Float) = 0
        _LocalPatchCenterLatDeg ("Patch Center Latitude (deg)", Float) = 0
        _LocalPatchSpanLonDeg ("Patch Width in Longitude (deg)", Float) = 20
        _LocalPatchSpanLatDeg ("Patch Height in Latitude (deg)", Float) = 10

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
                float _LocalPatchMaskEnable;
                float _LocalPatchCenterLonDeg;
                float _LocalPatchCenterLatDeg;
                float _LocalPatchSpanLonDeg;
                float _LocalPatchSpanLatDeg;
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

            float ProvinceIdMaskFromRGB(float3 idRGB, float selectedId)
            {
                uint provinceId = DecodeProvinceId24(idRGB);
                float maskEnabled = step(0.0, selectedId + 0.5);
                uint selectedIdInt = (uint)max(0.0, round(selectedId));
                return maskEnabled * ((provinceId == selectedIdInt) ? 1.0 : 0.0);
            }

            float3 ProvinceHoverSelectFromRGB(
                float3 idRGB, float selectedId, float4 highlightColor,
                float hoverId, float4 hoverColor,
                float3 baseColor)
            {
                float selectMask = ProvinceIdMaskFromRGB(idRGB, selectedId);
                float hoverMask = ProvinceIdMaskFromRGB(idRGB, hoverId);
                float hoverEnabled = step(0.0, hoverId + 0.5);
                hoverMask *= hoverEnabled;

                float3 color = baseColor;
                color = lerp(color, color + hoverColor.rgb * hoverColor.a, hoverMask);
                color = lerp(color, color + highlightColor.rgb * highlightColor.a, selectMask);
                return color;
            }

            float AngularDeltaLon(float lonA, float lonB)
            {
                float d = abs(frac((lonA - lonB) / TWO_PI_ + 0.5) - 0.5) * TWO_PI_;
                return d;
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
                float2 UV,
                float Radius,
                float Morph,
                float Sphere,
                Texture2D HeightTex,
                SamplerState HeightSampler,
                float2 UVOffset,
                float HeightMinKm,
                float HeightMaxKm,
                float HeightExaggeration,
                float KmPerUnit)
            {
                float sphereLerp = saturate(Sphere);
                float2 planarPannedUV = float2(
                    frac(UV.x + UVOffset.x),
                    saturate(UV.y + UVOffset.y)
                );
                float2 sphereUV = float2(
                    UV.x,
                    saturate(UV.y)
                );
                float2 geometryUV = lerp(UV, sphereUV, sphereLerp);

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

                float3 planeNormalGeom = float3(0.0, 0.0, 1.0);

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

                float2 heightSampleUV = lerp(planarPannedUV, sphereUV, sphereLerp);
                float heightSample01 = HeightTex.SampleLevel(HeightSampler, heightSampleUV, 0).r;
                float elevationKm = lerp(HeightMinKm, HeightMaxKm, heightSample01);
                float heightWorldUnits = (elevationKm / max(1e-6, KmPerUnit)) * HeightExaggeration;

                float3 planePosition = basePos + (-planeNormalGeom) * heightWorldUnits;
                float3 spherePositionWithHeight = spherePosition + sphereNormal * heightWorldUnits;
                return lerp(planePosition, spherePositionWithHeight, sphereLerp);
            }

            void EqrMorph_float(
                float2 UV,
                float Radius,
                float Morph,
                float Sphere,
                Texture2D HeightTex,
                SamplerState HeightSampler,
                float2 UVOffset,
                float HeightMinKm,
                float HeightMaxKm,
                float HeightExaggeration,
                float KmPerUnit,
                out float3 OutPosition,
                out float3 OutNormal)
            {
                OutPosition = EvaluateDisplacedPosition(
                    UV,
                    Radius,
                    Morph,
                    Sphere,
                    HeightTex,
                    HeightSampler,
                    UVOffset,
                    HeightMinKm,
                    HeightMaxKm,
                    HeightExaggeration,
                    KmPerUnit
                );

                float sphereLerp = saturate(Sphere);
                float2 sphereUV = float2(UV.x, saturate(UV.y));
                float2 geometryUV = lerp(UV, sphereUV, sphereLerp);

                float longitude = (geometryUV.x - 0.5) * (2.0 * PI_);
                float latitude = (geometryUV.y - 0.5) * PI_;
                float cosLatitudeSphere = cos(latitude);
                float sinLatitudeSphere = sin(latitude);
                float cosLongitudeSphere = cos(longitude);
                float sinLongitudeSphere = sin(longitude);

                float3 planeNormal = float3(0.0, 0.0, -1.0);
                float3 sphereNormal = normalize(float3(
                    cosLatitudeSphere * cosLongitudeSphere,
                    sinLatitudeSphere,
                    cosLatitudeSphere * sinLongitudeSphere
                ));
                float3 fallbackNormal = normalize(lerp(planeNormal, sphereNormal, sphereLerp));

                float2 texelStep = float2(
                    max(1e-6, _HeightTex_TexelSize.x),
                    max(1e-6, _HeightTex_TexelSize.y)
                );
                float2 uvDx = float2(frac(UV.x + texelStep.x), UV.y);
                float2 uvDy = float2(UV.x, saturate(UV.y + texelStep.y));

                float3 posDx = EvaluateDisplacedPosition(
                    uvDx,
                    Radius,
                    Morph,
                    Sphere,
                    HeightTex,
                    HeightSampler,
                    UVOffset,
                    HeightMinKm,
                    HeightMaxKm,
                    HeightExaggeration,
                    KmPerUnit
                );
                float3 posDy = EvaluateDisplacedPosition(
                    uvDy,
                    Radius,
                    Morph,
                    Sphere,
                    HeightTex,
                    HeightSampler,
                    UVOffset,
                    HeightMinKm,
                    HeightMaxKm,
                    HeightExaggeration,
                    KmPerUnit
                );

                float3 normal = normalize(cross(posDy - OutPosition, posDx - OutPosition));
                if (dot(normal, fallbackNormal) < 0.0)
                {
                    normal = -normal;
                }

                float normalLenSq = dot(normal, normal);
                OutNormal = normalLenSq > 1e-8 ? normal : fallbackNormal;
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
                float3 normalWS : TEXCOORD1;
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
                    _HeightMinKm,
                    _HeightMaxKm,
                    _HeightExaggeration,
                    _KmPerUnit,
                    posOS,
                    normalOS
                );

                float3 posWS = TransformObjectToWorld(posOS);
                output.positionCS = TransformWorldToHClip(posWS);
                output.normalWS = TransformObjectToWorldNormal(normalOS);
                output.uv = input.uv;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float sphereLerp = saturate(_Sphere);
                if (sphereLerp > 0.5 && _LocalPatchMaskEnable > 0.5)
                {
                    float longitude = (input.uv.x - 0.5) * TWO_PI_;
                    float latitude = (saturate(input.uv.y) - 0.5) * PI_;
                    float centerLon = _LocalPatchCenterLonDeg * (PI_ / 180.0);
                    float centerLat = _LocalPatchCenterLatDeg * (PI_ / 180.0);
                    float halfSpanLon = max(1e-6, abs(_LocalPatchSpanLonDeg)) * (PI_ / 360.0);
                    float halfSpanLat = max(1e-6, abs(_LocalPatchSpanLatDeg)) * (PI_ / 360.0);

                    float dLon = AngularDeltaLon(longitude, centerLon);
                    float dLat = abs(latitude - centerLat);
                    float insideLon = step(dLon, halfSpanLon);
                    float insideLat = step(dLat, halfSpanLat);
                    float insidePatch = insideLon * insideLat;
                    clip(0.5 - insidePatch); // discard global map where local patch should render
                }

                float2 planarUV = input.uv + _UVOffset;
                float2 sphereUV = float2(
                    input.uv.x,
                    saturate(input.uv.y)
                );
                float2 uv = lerp(planarUV, sphereUV, sphereLerp);
                float3 baseColor = SampleBaseColor(uv);
                float4 idSample = SAMPLE_TEXTURE2D(_ProvinceIDTex, sampler_ProvinceIDTex, uv);

                float3 outColor = ProvinceHoverSelectFromRGB(
                    idSample.rgb,
                    _SelectedID,
                    _HighlightColor,
                    _HoverID,
                    _HoverColor,
                    baseColor);

                float3 normalWS = normalize(input.normalWS);
                Light mainLight = GetMainLight();
                float NdotL = saturate(dot(normalWS, mainLight.direction));
                float3 lighting = NdotL * mainLight.color;

                return float4(outColor * lighting, 1.0);
            }
            ENDHLSL
        }
    }
}
