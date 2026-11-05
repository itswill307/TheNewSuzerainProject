Shader "Hidden/Map/TerrainSurfaceBake"
{
    Properties
    {
        _MainTex ("Categorical Landcover", 2D) = "black" {}
        _LandcoverTextureArray ("Terrain Artwork", 2DArray) = "" {}
        _GrasslandTextureVariants ("Grassland Variants", 2DArray) = "" {}
        _GrasslandVariantCount ("Variants", Float) = 4
        _TerrainDetailMeters ("Detail Span", Float) = 2048
        _TerrainDetailStrength ("Detail Strength", Float) = 0.75
        _TerrainDetailFadeStart ("Detail Reference", Float) = 15
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalRenderPipeline" }
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            TEXTURE2D_ARRAY(_LandcoverTextureArray); SAMPLER(sampler_LandcoverTextureArray);
            TEXTURE2D_ARRAY(_GrasslandTextureVariants); SAMPLER(sampler_GrasslandTextureVariants);
            float _GrasslandVariantCount, _TerrainDetailMeters, _TerrainDetailStrength, _TerrainDetailFadeStart;
            static const float _UseNaturalTerrain = 1, _TerrainBoundaryBlend = 1;
            static const float _TerrainVegetationEdges = 0, _TerrainShorelineDetail = 0;
            static const float _TerrainReliefStrength = 0, _TerrainElevationRangeMeters = 19843;
            float4 _BakeRect; // geographic offset XY and span ZW
            float4 _BakeSize; // destination width/height, source samples per axis
            #include "NaturalTerrain.hlsl"
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings vert(float4 position : POSITION, float2 uv : TEXCOORD0)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(position.xyz);
                output.uv = uv;
                return output;
            }
            float4 frag(Varyings input) : SV_Target
            {
                float2 uv = _BakeRect.xy + input.uv * _BakeRect.zw;
                TerrainSurface surface = (TerrainSurface)0;
                surface.dx = float2(1.0 / _BakeSize.x, 0);
                surface.dy = float2(0, 1.0 / _BakeSize.y);
                surface.reference = max(_TerrainDetailFadeStart, 0.01);
                float latitudeScale = max(0.05, cos((uv.y - 0.5) * 3.14159265359));
                surface.metersPerPixel = max(40075016.686 * latitudeScale / _BakeSize.x,
                    20037508.343 / _BakeSize.y);
                float4 result = 0;
                // Every source categorical texel contributes its rendered color.
                // The source is 16K; the 8K surface averages its 2x2 source footprint.
                [loop] for (int y = 0; y < (int)_BakeSize.w; y++)
                [loop] for (int x = 0; x < (int)_BakeSize.z; x++)
                {
                    surface.uv = uv + (float2((x+0.5)/_BakeSize.z, (y+0.5)/_BakeSize.w)-0.5) / _BakeSize.xy;
                    float id = round(SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, surface.uv, 0).r * 255.0);
                    // Missing data uses the unclassified artwork, never an interpolated ID.
                    id = id <= 17.0 ? id : 0.0;
                    result += float4(TerrainTextureAlbedo(id, surface), id < 16.5 ? 1.0 : 0.0);
                }
                return result / (_BakeSize.z * _BakeSize.w);
            }
            ENDHLSL
        }
    }
}
