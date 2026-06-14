using System.Collections.Generic;
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
        "Assets/Map/Textures/modis_lc_type1_12_croplands.png",
        "Assets/Map/Textures/modis_lc_type1_13_urban_and_built_up.png",
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

    struct ImportSettings
    {
        public TextureImporter Importer;
        public bool IsReadable;
        public bool MipmapEnabled;
        public bool SRgbTexture;
        public TextureImporterCompression TextureCompression;
    }

    [MenuItem("Tools/Map/Generate MODIS Landcover Texture2DArray")]
    public static void Generate()
    {
        var importSettings = new List<ImportSettings>(SourceTexturePaths.Length + GrasslandVariantTexturePaths.Length);
        try
        {
            Texture2D[] sources = LoadReadableSources(SourceTexturePaths, importSettings);
            Texture2DArray textureArray = BuildTextureArray(sources, "modis_lc_type1_texture_array");
            SaveTextureArray(textureArray, LandcoverOutputPath);

            Texture2D[] grasslandVariantSources = LoadReadableSources(GrasslandVariantTexturePaths, importSettings);
            Texture2DArray grasslandVariantArray = BuildTextureArray(grasslandVariantSources, "modis_lc_type1_10_grasslands_variants");
            SaveTextureArray(grasslandVariantArray, GrasslandVariantsOutputPath);

            Texture2DArray savedArray = AssetDatabase.LoadAssetAtPath<Texture2DArray>(LandcoverOutputPath);
            Texture2DArray savedGrasslandVariants = AssetDatabase.LoadAssetAtPath<Texture2DArray>(GrasslandVariantsOutputPath);
            AssignToMaterials(
                savedArray != null ? savedArray : textureArray,
                savedGrasslandVariants != null ? savedGrasslandVariants : grasslandVariantArray,
                GrasslandVariantTexturePaths.Length);

            Debug.Log($"Generated {TargetTextureSize}x{TargetTextureSize} landcover Texture2DArray with {SourceTexturePaths.Length} slices at {LandcoverOutputPath}.");
            Debug.Log($"Generated {TargetTextureSize}x{TargetTextureSize} grassland variant Texture2DArray with {GrasslandVariantTexturePaths.Length} slices at {GrasslandVariantsOutputPath}.");
        }
        finally
        {
            RestoreImportSettings(importSettings);
        }
    }

    static Texture2D[] LoadReadableSources(string[] texturePaths, List<ImportSettings> importSettings)
    {
        var sources = new Texture2D[texturePaths.Length];
        for (int i = 0; i < texturePaths.Length; i++)
        {
            string path = texturePaths[i];
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
                throw new FileNotFoundException($"Could not find landcover source texture importer: {path}");

            importSettings.Add(new ImportSettings
            {
                Importer = importer,
                IsReadable = importer.isReadable,
                MipmapEnabled = importer.mipmapEnabled,
                SRgbTexture = importer.sRGBTexture,
                TextureCompression = importer.textureCompression,
            });

            importer.isReadable = true;
            importer.mipmapEnabled = true;
            importer.sRGBTexture = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();

            Texture2D source = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (source == null)
                throw new FileNotFoundException($"Could not load landcover source texture: {path}");

            sources[i] = source;
        }

        return sources;
    }

    static Texture2DArray BuildTextureArray(Texture2D[] sources, string textureArrayName)
    {
        int width = TargetTextureSize;
        int height = TargetTextureSize;
        var textureArray = new Texture2DArray(width, height, sources.Length, TextureFormat.RGBA32, true, false)
        {
            name = textureArrayName,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8,
        };

        for (int slice = 0; slice < sources.Length; slice++)
        {
            Texture2D source = sources[slice];
            Texture2D resized = source.width == width && source.height == height
                ? source
                : ResizeSource(source, width, height);

            textureArray.SetPixels32(resized.GetPixels32(), slice, 0);

            if (resized != source)
            {
                Object.DestroyImmediate(resized);
            }
        }

        textureArray.Apply(true, false);
        return textureArray;
    }

    static void SaveTextureArray(Texture2DArray textureArray, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        AssetDatabase.DeleteAsset(outputPath);
        AssetDatabase.CreateAsset(textureArray, outputPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceUpdate);
    }

    static Texture2D ResizeSource(Texture2D source, int width, int height)
    {
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

        try
        {
            Graphics.Blit(source, renderTexture);
            RenderTexture.active = renderTexture;

            var resized = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            resized.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            resized.Apply(false, false);
            return resized;
        }
        finally
        {
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(renderTexture);
        }
    }

    static void AssignToMaterials(Texture2DArray textureArray, Texture2DArray grasslandVariantArray, int grasslandVariantCount)
    {
        if (textureArray == null)
            return;

        int textureArrayProp = Shader.PropertyToID("_LandcoverTextureArray");
        int grasslandVariantArrayProp = Shader.PropertyToID("_GrasslandTextureVariants");
        int grasslandVariantCountProp = Shader.PropertyToID("_GrasslandVariantCount");
        foreach (string materialPath in MaterialPaths)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null || !material.HasProperty(textureArrayProp))
                continue;

            material.SetTexture(textureArrayProp, textureArray);
            if (grasslandVariantArray != null && material.HasProperty(grasslandVariantArrayProp))
            {
                material.SetTexture(grasslandVariantArrayProp, grasslandVariantArray);
            }

            if (material.HasProperty(grasslandVariantCountProp))
            {
                material.SetFloat(grasslandVariantCountProp, grasslandVariantArray != null ? grasslandVariantCount : 0);
            }

            EditorUtility.SetDirty(material);
        }

        AssetDatabase.SaveAssets();
    }

    static void RestoreImportSettings(List<ImportSettings> importSettings)
    {
        for (int i = 0; i < importSettings.Count; i++)
        {
            ImportSettings settings = importSettings[i];
            if (settings.Importer == null)
                continue;

            settings.Importer.isReadable = settings.IsReadable;
            settings.Importer.mipmapEnabled = settings.MipmapEnabled;
            settings.Importer.sRGBTexture = settings.SRgbTexture;
            settings.Importer.textureCompression = settings.TextureCompression;
            settings.Importer.SaveAndReimport();
        }
    }
}
