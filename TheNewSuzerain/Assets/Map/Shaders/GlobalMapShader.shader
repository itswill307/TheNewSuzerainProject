Shader "GlobalMapShader"
{
    Properties
    {
        [Header(Textures)]
        _MainTex ("Base Color", 2D) = "white" {}
        _LandcoverLUT ("Landcover LUT (256x1)", 2D) = "white" {}
        [NoScaleOffset] _LandcoverTextureArray ("Landcover Texture Array", 2DArray) = "" {}
        [NoScaleOffset] _GrasslandTextureVariants ("Grassland Texture Variants", 2DArray) = "" {}
        [HideInInspector] _GrasslandVariantCount ("Grassland Variant Count", Float) = 0
        _ProvinceIDTex ("Province ID Map", 2D) = "white" {}
        _HeightTex ("Height Map", 2D) = "black" {}
        [Toggle] _UseLandcoverLUT ("Use Landcover LUT", Float) = 0
        [Toggle] _UseLandcoverTextures ("Use Landcover Textures", Float) = 0

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
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;

            TEXTURE2D(_LandcoverLUT);
            SAMPLER(sampler_LandcoverLUT);
            TEXTURE2D_ARRAY(_LandcoverTextureArray);
            SAMPLER(sampler_LandcoverTextureArray);
            TEXTURE2D_ARRAY(_GrasslandTextureVariants);
            SAMPLER(sampler_GrasslandTextureVariants);
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
                float _UseLandcoverTextures;
                float _GrasslandVariantCount;
                float _Sphere;
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

            #include "NaturalTerrain.hlsl"

            float3 SampleBaseColor(float2 uv)
            {
                float3 distant;
                if (_UseLandcoverTextures > 0.5 && TerrainDistantColor(uv, distant)) return distant;
                float3 sampled = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, 0).rgb;
                if (_UseLandcoverLUT <= 0.5 && _UseLandcoverTextures <= 0.5)
                {
                    return sampled;
                }

                float idx = round(saturate(sampled.r) * 255.0);
                if (_UseLandcoverTextures > 0.5 && idx <= 17.5)
                {
                    [branch] if (TerrainShouldBlendClasses(uv))
                    {
                        float2 size = max(_MainTex_TexelSize.zw, 1.0);
                        float2 pixel = uv * size - 0.5;
                        float2 origin = (floor(pixel) + 0.5) / size;
                        // Sample categorical IDs at texel centers, then blend
                        // their rendered surfaces. Never interpolate ID numbers.
                        float4 ids = round(saturate(float4(
                            SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, origin, 0).r,
                            SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, origin + float2(1,0)/size, 0).r,
                            SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, origin + float2(0,1)/size, 0).r,
                            SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, origin + 1.0/size, 0).r)) * 255.0);
                        return ResolveTerrainNeighborhood(idx, ids, frac(pixel), uv, 1.0/size);
                    }
                    return ResolveNaturalTerrain(idx, uv);
                }

                if (_UseLandcoverLUT <= 0.5)
                {
                    return sampled;
                }

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
