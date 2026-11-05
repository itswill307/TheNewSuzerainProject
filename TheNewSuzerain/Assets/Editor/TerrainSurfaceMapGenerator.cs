using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// This is a filtered render of the project's terrain, not external satellite color.
public static class TerrainSurfaceMapGenerator
{
    public const string OutputPath = "Assets/Map/Textures/terrain_surface.generated.png";
    const string FlatPath = "Assets/Map/Materials/FlatmapMat.mat";
    const string CesiumPath = "Assets/Map/Materials/CesiumCustomTerrain.mat";
    const int Width = 8192, Height = 4096, Tile = 512;

    [MenuItem("Tools/Map/Rebuild Terrain-Derived Distant Surface")]
    public static void Generate()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Material flat = AssetDatabase.LoadAssetAtPath<Material>(FlatPath);
        // Load both before importing the generated texture, including on the first
        // run after changing shader properties in an already-open editor.
        Material cesium = AssetDatabase.LoadAssetAtPath<Material>(CesiumPath);
        var source = flat.GetTexture("_MainTex") as Texture2D;
        Shader shader = Shader.Find("Hidden/Map/TerrainSurfaceBake");
        if (source == null || shader == null || flat.GetTexture("_LandcoverTextureArray") == null)
            throw new InvalidOperationException("Build the landcover texture arrays before the distant surface.");
        if (source.width % Width != 0 || source.height % Height != 0)
            throw new InvalidOperationException("Distant-surface dimensions must divide the categorical source dimensions.");
        var material = new Material(shader);
        material.CopyPropertiesFromMaterial(flat);
        var image = new Texture2D(Width, Height, TextureFormat.RGBA32, false, false);
        var target = RenderTexture.GetTemporary(Tile, Tile, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        RenderTexture previous = RenderTexture.active;
        try
        {
            material.SetVector("_BakeSize", new Vector4(Width, Height, source.width / Width, source.height / Height));
            // Small render tiles avoid long individual GPU submissions and huge targets.
            for (int y = 0; y < Height; y += Tile)
            for (int x = 0; x < Width; x += Tile)
            {
                material.SetVector("_BakeRect", new Vector4((float)x/Width, (float)y/Height, (float)Tile/Width, (float)Tile/Height));
                Graphics.Blit(source, target, material);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, Tile, Tile), x, y, false);
            }
            image.Apply(false, false);
            if (ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Terrain surface bake shader failed to compile.");
            File.WriteAllBytes(OutputPath, image.EncodeToPNG());
            AssetDatabase.ImportAsset(OutputPath, ImportAssetOptions.ForceSynchronousImport);
            ConfigureImport();
            Texture2D surface = AssetDatabase.LoadAssetAtPath<Texture2D>(OutputPath);
            foreach (Material terrain in new[] { flat, cesium })
            {
                terrain.SetTexture("_TerrainSurfaceTex", surface);
                EditorUtility.SetDirty(terrain);
            }
            AssetDatabase.SaveAssets();
            if (flat.GetTexture("_TerrainSurfaceTex") != surface || cesium.GetTexture("_TerrainSurfaceTex") != surface)
                throw new InvalidOperationException("Both map materials must share the generated terrain surface.");
            Debug.Log("Generated shared 8192x4096 BC7 terrain surface from landcover and terrain artwork, with color mips and a land relief mask.");
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            Object.DestroyImmediate(material);
            Object.DestroyImmediate(image);
        }
    }

    public static void ConfigureImport()
    {
        var importer = AssetImporter.GetAtPath(OutputPath) as TextureImporter;
        if (importer == null) { Generate(); return; }
        importer.maxTextureSize = Width;
        importer.sRGBTexture = true;
        importer.mipmapEnabled = true;
        importer.isReadable = false;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = false; // Alpha is land coverage, not transparency.
        importer.filterMode = FilterMode.Trilinear;
        importer.wrapModeU = TextureWrapMode.Repeat;
        importer.wrapModeV = TextureWrapMode.Clamp;
        importer.anisoLevel = 4;
        importer.streamingMipmaps = false;
        var platform = importer.GetPlatformTextureSettings("Standalone");
        platform.overridden = true;
        platform.maxTextureSize = Width;
        platform.format = TextureImporterFormat.BC7;
        platform.compressionQuality = 100;
        importer.SetPlatformTextureSettings(platform);
        importer.SaveAndReimport();
    }
}
