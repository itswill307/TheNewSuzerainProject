using System.IO;
using UnityEditor;
using UnityEngine;

public static class LandcoverTextureArrayGenerator
{
    const string LandcoverOutputPath = "Assets/Map/Textures/modis_lc_type1_texture_array.asset";
    const string GrasslandVariantsOutputPath = "Assets/Map/Textures/modis_lc_type1_10_grasslands_variants.asset";
    const int TargetTextureSize = 1024;

    static readonly string[] SourceTexturePaths =
    {
        "Assets/Map/Textures/modis_lc_type1_00_unclassified.png",
        "Assets/Map/Textures/modis_lc_type1_01_evergreen_needleleaf_forest.png",
        "Assets/Map/Textures/modis_lc_type1_02_evergreen_broadleaf_forest.png",
        "Assets/Map/Textures/modis_lc_type1_03_deciduous_needleleaf_forest.png",
        "Assets/Map/Textures/modis_lc_type1_04_deciduous_broadleaf_forest.png",
        "Assets/Map/Textures/modis_lc_type1_05_mixed_forest.png",
        "Assets/Map/Textures/modis_lc_type1_06_closed_shrublands.png",
        "Assets/Map/Textures/modis_lc_type1_07_open_shrublands.png",
        "Assets/Map/Textures/modis_lc_type1_08_woody_savannas.png",
        "Assets/Map/Textures/modis_lc_type1_09_savannas.png",
        "Assets/Map/Textures/modis_lc_type1_10_grasslands.png",
        "Assets/Map/Textures/modis_lc_type1_11_permanent_wetlands.png",
        "Assets/Map/Textures/natural_croplands_v2.png",
        "Assets/Map/Textures/natural_urban_v2.png",
        "Assets/Map/Textures/modis_lc_type1_14_cropland_natural_vegetation_mosaic.png",
        "Assets/Map/Textures/modis_lc_type1_15_snow_and_ice.png",
        "Assets/Map/Textures/modis_lc_type1_16_barren_or_sparsely_vegetated.png",
        "Assets/Map/Textures/modis_lc_type1_17_water_bodies.png",
    };

    static readonly string[] GrasslandVariantTexturePaths =
    {
        "Assets/Map/Textures/modis_lc_type1_10_grasslands_variant_00.png",
        "Assets/Map/Textures/modis_lc_type1_10_grasslands_variant_01.png",
        "Assets/Map/Textures/modis_lc_type1_10_grasslands_variant_02.png",
        "Assets/Map/Textures/modis_lc_type1_10_grasslands_variant_03.png",
    };

    static readonly string[] MaterialPaths =
    {
        "Assets/Map/Materials/FlatmapMat.mat",
        "Assets/Map/Materials/CesiumCustomTerrain.mat",
    };


    [MenuItem("Tools/Map/Generate MODIS Landcover Texture2DArray")]
    public static void Generate()
    {
        // Decode the PNGs directly: importer caps/compression must not discard source detail
        // before the array is built. Source import settings are never changed.
        Texture2DArray landcover = BuildTextureArray(SourceTexturePaths, "modis_lc_type1_texture_array");
        Texture2DArray variants = null;
        try
        {
            variants = BuildTextureArray(GrasslandVariantTexturePaths, "modis_lc_type1_10_grasslands_variants");
            SaveTextureArray(landcover, LandcoverOutputPath);
            SaveTextureArray(variants, GrasslandVariantsOutputPath);
            AssignToMaterials();
            TerrainSurfaceMapGenerator.Generate();
            Debug.Log("Natural terrain: generated full-source 1024px BC7 arrays with mipmaps and no CPU copies. " +
                "22 slices use approximately 29.3 MiB of GPU texture data (previously 117.3 MiB RGBA32).");
        }
        finally
        {
            if (landcover != null && !AssetDatabase.Contains(landcover)) Object.DestroyImmediate(landcover);
            if (variants != null && !AssetDatabase.Contains(variants)) Object.DestroyImmediate(variants);
        }
    }

    static Texture2DArray BuildTextureArray(string[] paths, string name)
    {
        var array = new Texture2DArray(TargetTextureSize, TargetTextureSize, paths.Length, TextureFormat.BC7, true, false)
        {
            name = name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8,
        };
        try
        {
            for (int slice = 0; slice < paths.Length; slice++)
            {
                var source = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
                Texture2D resized = null;
                try
                {
                    if (!ImageConversion.LoadImage(source, File.ReadAllBytes(paths[slice]), false))
                        throw new IOException($"Unable to decode terrain texture {paths[slice]}");
                    resized = source.width == TargetTextureSize && source.height == TargetTextureSize
                        ? source : ResizeSource(source);
                    resized.Apply(true, false);
                    EditorUtility.CompressTexture(resized, TextureFormat.BC7, TextureCompressionQuality.Normal);
                    if (resized.format != TextureFormat.BC7 || resized.mipmapCount != array.mipmapCount)
                        throw new IOException($"Invalid compressed mip chain for {paths[slice]}");
                    for (int mip = 0; mip < array.mipmapCount; mip++)
                        array.SetPixelData(resized.GetPixelData<byte>(mip), mip, slice);
                }
                finally
                {
                    if (resized != null && resized != source) Object.DestroyImmediate(resized);
                    Object.DestroyImmediate(source);
                }
            }
            // Mips are already compressed. Do not regenerate them or retain a CPU copy.
            array.Apply(false, true);
            return array;
        }
        catch
        {
            Object.DestroyImmediate(array);
            throw;
        }
    }

    static Texture2D ResizeSource(Texture2D source)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture temporary = RenderTexture.GetTemporary(TargetTextureSize, TargetTextureSize, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            var resized = new Texture2D(TargetTextureSize, TargetTextureSize, TextureFormat.RGBA32, true, false);
            resized.ReadPixels(new Rect(0, 0, TargetTextureSize, TargetTextureSize), 0, 0);
            resized.Apply(true, false);
            return resized;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    static void SaveTextureArray(Texture2DArray array, string path)
    {
        // Preserve the GUID: deleting assets would break scene and material references.
        Texture2DArray existing = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
        if (existing == null)
            AssetDatabase.CreateAsset(array, path);
        else
        {
            EditorUtility.CopySerialized(array, existing);
            EditorUtility.SetDirty(existing);
        }
        AssetDatabase.SaveAssets();
    }

    [MenuItem("Tools/Map/Apply Natural Terrain Desktop Settings")]
    public static void ConfigureDesktop()
    {
        TerrainSurfaceMapGenerator.ConfigureImport();
        AssignToMaterials();
    }

    static void AssignToMaterials()
    {
        Texture2DArray landcover = AssetDatabase.LoadAssetAtPath<Texture2DArray>(LandcoverOutputPath);
        Texture2DArray variants = AssetDatabase.LoadAssetAtPath<Texture2DArray>(GrasslandVariantsOutputPath);
        Texture2D surface = AssetDatabase.LoadAssetAtPath<Texture2D>(TerrainSurfaceMapGenerator.OutputPath);
        Material flat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPaths[0]);
        Texture elevation = flat.GetTexture("_HeightTex");
        float elevationRange = (flat.GetFloat("_HeightMaxKm") - flat.GetFloat("_HeightMinKm")) * 1000f;
        foreach (string path in MaterialPaths)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) throw new FileNotFoundException(path);
            material.SetTexture("_LandcoverTextureArray", landcover);
            material.SetTexture("_GrasslandTextureVariants", variants);
            material.SetFloat("_GrasslandVariantCount", GrasslandVariantTexturePaths.Length);
            material.SetTexture("_TerrainSurfaceTex", surface);
            material.SetFloat("_UseNaturalTerrain", 1f);
            material.SetFloat("_TerrainDetailMeters", 2048f);
            material.SetFloat("_TerrainDetailStrength", 0.75f);
            material.SetFloat("_TerrainBoundaryBlend", 1f);
            material.SetTexture("_TerrainElevationTex", elevation);
            material.SetFloat("_TerrainElevationRangeMeters", elevationRange);
            material.SetFloat("_TerrainReliefStrength", 0.35f);
            material.SetFloat("_TerrainVegetationEdges", 0.8f);
            material.SetFloat("_TerrainShorelineDetail", 0.8f);
            material.SetFloat("_TerrainDetailFadeStart", 15f);
            EditorUtility.SetDirty(material);
        }
        AssetDatabase.SaveAssets();
    }
}
