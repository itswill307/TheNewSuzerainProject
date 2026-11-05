using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

// Run with Unity -batchmode -executeMethod NaturalTerrainValidation.BuildAndValidate.
// Renders the real map shaders without changing or saving any scene.
public static class NaturalTerrainValidation
{
    const string Output = "Logs/NaturalTerrain";
    const string FlatPath = "Assets/Map/Materials/FlatmapMat.mat";
    const string CesiumPath = "Assets/Map/Materials/CesiumCustomTerrain.mat";

    [MenuItem("Tools/Map/Validate Natural Terrain")]
    public static void Validate()
    {
        Directory.CreateDirectory(Output);
        var report = new StringBuilder();
        report.AppendLine($"Graphics device: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})");
        report.AppendLine($"Color space: {QualitySettings.activeColorSpace}");
        Material flat = AssetDatabase.LoadAssetAtPath<Material>(FlatPath);
        Material cesium = AssetDatabase.LoadAssetAtPath<Material>(CesiumPath);
        Require(flat != null && cesium != null, "Both terrain materials must exist.");
        long bytes = 0, gpuBytes = 0;
        foreach (string property in new[] { "_LandcoverTextureArray", "_GrasslandTextureVariants" })
        {
            var array = flat.GetTexture(property) as Texture2DArray;
            Require(array != null && array.width == 1024 && array.height == 1024, property + " dimensions");
            Require(array.format == TextureFormat.BC7 && array.mipmapCount == 11, property + " format/mips");
            Require(!array.isReadable, property + " must not retain a CPU copy");
            Require(cesium.GetTexture(property) == array, property + " must be shared across both renderers");
            Require(array.depth == (property == "_LandcoverTextureArray" ? 18 : 4), property + " slices");
            bytes += Profiler.GetRuntimeMemorySizeLong(array);
            gpuBytes += BlockBytes(array.width, array.height, array.mipmapCount, array.depth, 16);
            report.AppendLine($"{property}: {array.width}x{array.height}x{array.depth}, {array.format}, " +
                $"{array.mipmapCount} mips, readable={array.isReadable}, allocated={Profiler.GetRuntimeMemorySizeLong(array)} bytes");
        }
        var surface = flat.GetTexture("_TerrainSurfaceTex") as Texture2D;
        Require(surface != null && surface.width == 8192 && surface.height == 4096, "Terrain surface dimensions");
        Require(surface.format == TextureFormat.BC7 && !surface.isReadable && surface.mipmapCount == 14,
            "Terrain surface compression/CPU copy/mips");
        Require(cesium.GetTexture("_TerrainSurfaceTex") == surface, "Terrain surface must be shared");
        Require(flat.GetTexture("_TerrainElevationTex") == flat.GetTexture("_HeightTex")
            && cesium.GetTexture("_TerrainElevationTex") == flat.GetTexture("_HeightTex"),
            "Relief must reuse the existing elevation texture across both materials");
        bytes += Profiler.GetRuntimeMemorySizeLong(surface);
        gpuBytes += BlockBytes(surface.width, surface.height, surface.mipmapCount, 1, 16);
        report.AppendLine($"Terrain-derived surface: {surface.width}x{surface.height}, {surface.format}, allocated={Profiler.GetRuntimeMemorySizeLong(surface)} bytes");
        report.AppendLine($"GPU color texture data (BC7 blocks, all mip levels): {gpuBytes / (1024.0 * 1024.0):F2} MiB");
        report.AppendLine($"Editor-reported allocation including authoring copies: {bytes / (1024.0 * 1024.0):F2} MiB");
        File.WriteAllText(Path.Combine(Output, "validation.txt"), report.ToString());
        Require(gpuBytes < 117L * 1024 * 1024, "Terrain color texture data exceeded 117 MiB.");

        // Constant classes exercise both data encodings: linear IDs on the flat map,
        // sRGB-encoded raster IDs in Cesium. Compare real shader output across scales.
        foreach (byte classId in new byte[] { 1, 10, 13, 15, 16, 17 })
        {
            foreach (float width in new[] { 20000f, 60000f, 150000f, 380000f, 1500000f })
            {
                var linearId = ClassTexture(classId, true);
                var srgbId = ClassTexture(classId, false);
                var a = new Material(flat);
                var b = new Material(cesium);
                try
                {
                    a.SetTexture("_MainTex", linearId);
                    a.SetFloat("_UseLandcoverTextures", 1);
                    a.SetFloat("_UseLandcoverLUT", 1);
                    b.SetTexture("_overlayTexture_0", srgbId);
                    Texture2D imageA = Render(a, 12, 30, width, 384, 216, false);
                    Texture2D imageB = Render(b, 12, 30, width, 384, 216, true);
                    try
                    {
                        float difference = Difference(imageA, imageB);
                        if (difference >= 0.015f || !HasImage(imageA))
                        {
                            File.WriteAllBytes(Path.Combine(Output, "failure-flat.png"), imageA.EncodeToPNG());
                            File.WriteAllBytes(Path.Combine(Output, "failure-cesium.png"), imageB.EncodeToPNG());
                        }
                        Require(difference < 0.015f, $"Renderer mismatch for class {classId}, width {width}: {difference}");
                        Require(HasImage(imageA), $"Blank render for class {classId}");
                        report.AppendLine($"PASS renderer parity: class={classId}, viewWidth={width}m, mean RGB difference={difference:F6}");
                    }
                    finally { Object.DestroyImmediate(imageA); Object.DestroyImmediate(imageB); }
                }
                finally
                {
                    Object.DestroyImmediate(a); Object.DestroyImmediate(b);
                    Object.DestroyImmediate(linearId); Object.DestroyImmediate(srgbId);
                }
            }
        }
        ValidateTextureDerivedSurface(flat, cesium, report);
        ValidateDetailCoverage(flat, report);
        ValidateBoundaries(flat, cesium, report);
        ValidateSurfaceTreatments(flat, cesium, report);
        foreach (Material material in new[] { flat, cesium })
        {
            Require(!ShaderUtil.ShaderHasError(material.shader), material.shader.name + " compilation failed");
            foreach (var message in ShaderUtil.GetShaderMessages(material.shader))
                report.AppendLine($"Shader {material.shader.name}: {message.severity}: {message.message} ({message.file}:{message.line})");
        }
        report.AppendLine("PASS: arrays, memory budget, shader compilation, texture-derived surface fixtures, 33 flat/Cesium comparisons, 28 detail-continuity renders, categorical boundaries, vegetation, shorelines and elevation relief.");
        report.AppendLine("These are offscreen shader checks. Full-game 1080p/60 FPS on RTX 3060 remains a player-build profiling target.");
        File.WriteAllText(Path.Combine(Output, "validation.txt"), report.ToString());
        Debug.Log(report.ToString());
        PreviewCurrent();
        Preview(null, flat, "terrain-derived-europe-6000km", 15, 45, 6000000);
        Preview(null, flat, "terrain-derived-world", 0, 0, 40075016);
    }

    static void ValidateTextureDerivedSurface(Material flat, Material cesium, StringBuilder report)
    {
        Require(!flat.HasProperty("_BlueMarbleTex") && !cesium.HasProperty("_BlueMarbleTex"),
            "Map shaders must not depend on Blue Marble");
        var artwork = new Texture2DArray(1, 1, 18, TextureFormat.RGBA32, false, true);
        var ids = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
        var baker = new Material(Shader.Find("Hidden/Map/TerrainSurfaceBake"));
        var target = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        var pixel = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
        RenderTexture previous = RenderTexture.active;
        try
        {
            for (int i = 0; i < 18; i++) artwork.SetPixels(new[] { new Color(0.2f, 0.3f, 0.1f) }, i);
            artwork.SetPixels(new[] { new Color(0.6f, 0.4f, 0.2f) }, 12);
            artwork.SetPixels(new[] { new Color(0.01f, 0.03f, 0.1f) }, 17);
            artwork.Apply();
            baker.CopyPropertiesFromMaterial(flat);
            baker.SetTexture("_LandcoverTextureArray", artwork);
            baker.SetVector("_BakeRect", new Vector4(0, 0, 1, 1));
            baker.SetVector("_BakeSize", new Vector4(1, 1, 2, 2));
            ids.SetPixels32(new[] { new Color32(1,0,0,255), new Color32(12,0,0,255),
                new Color32(1,0,0,255), new Color32(17,0,0,255) });
            ids.Apply();
            Graphics.Blit(ids, target, baker);
            RenderTexture.active = target;
            pixel.ReadPixels(new Rect(0,0,1,1),0,0); pixel.Apply();
            Color actual = pixel.GetPixel(0,0);
            // Averaging IDs would invent class 8. Expected colors are from the
            // three actual artwork layers, and water contributes zero land mask.
            Color expected = new Color((0.2f*2+0.6f+0.01f)/4, (0.3f*2+0.4f+0.03f)/4,
                (0.1f*2+0.2f+0.1f)/4, 0.75f);
            Require(Mathf.Abs(actual.r-expected.r)<0.01f && Mathf.Abs(actual.g-expected.g)<0.01f
                && Mathf.Abs(actual.b-expected.b)<0.01f && Mathf.Abs(actual.a-expected.a)<0.01f,
                $"Surface bake must average artwork colors and land coverage: {actual} vs {expected}");
            artwork.SetPixels(new[] { new Color(0.05f, 0.8f, 0.05f) }, 12); artwork.Apply();
            Graphics.Blit(ids, target, baker);
            pixel.ReadPixels(new Rect(0,0,1,1),0,0); pixel.Apply();
            Require(pixel.GetPixel(0,0).g > actual.g+0.08f, "Distant surface must respond to edited terrain artwork");
            Require(!ShaderUtil.ShaderHasError(baker.shader), "Surface bake shader compilation");
            report.AppendLine("PASS distant bake: artwork color averaging, artwork edits, no interpolated IDs, water relief mask, no Blue Marble property");
        }
        finally
        {
            RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target);
            Object.DestroyImmediate(artwork); Object.DestroyImmediate(ids); Object.DestroyImmediate(baker); Object.DestroyImmediate(pixel);
        }
        foreach (float width in new[] { 3000000f, 6000000f, 20000000f })
        {
            Texture2D a = Render(flat, 0, 20, width, 384, 216, false);
            Texture2D b = Render(cesium, 0, 20, width, 384, 216, true);
            try
            {
                float delta = Difference(a,b);
                Require(HasImage(a) && delta<0.015f, $"Distant surface renderer mismatch at {width}m: {delta}");
                report.AppendLine($"PASS distant surface parity: width={width}m, mean RGB difference={delta:F6}");
            }
            finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
        }
    }

    [MenuItem("Tools/Map/Build Terrain Surface Improvements")]
    public static void BuildSurfaceImprovements()
    {
        Directory.CreateDirectory(Output);
        Material current = AssetDatabase.LoadAssetAtPath<Material>(FlatPath);
        string directory = ".utmp/terrain-pass/realism/";
        Shader oldShader = null;
        Material before = null;
        Texture2DArray previousArray = null;
        try
        {
            if (File.Exists(directory+"GlobalMapShader.before.shader"))
            {
                oldShader = ShaderUtil.CreateShaderAsset(File.ReadAllText(directory+"GlobalMapShader.before.shader")
                    .Replace("Shader \"GlobalMapShader\"", "Shader \"Hidden/TerrainBeforeSurfaceImprovements\"")
                    .Replace("#include \"NaturalTerrain.hlsl\"", File.ReadAllText(directory+"NaturalTerrain.before.hlsl")));
                before = new Material(oldShader); before.CopyPropertiesFromMaterial(current);
                previousArray = Object.Instantiate(current.GetTexture("_LandcoverTextureArray")) as Texture2DArray;
                before.SetTexture("_LandcoverTextureArray", previousArray);
            }
            LandcoverTextureArrayGenerator.Generate();
            Validate();
            Preview(before,current,"surfaces-london-8km",-0.12f,51.5f,8000);
            Preview(before,current,"surfaces-farmland-12km",10.5f,51f,12000);
            Preview(before,current,"surfaces-forest-8km",-60f,-3f,8000);
            Preview(before,current,"surfaces-shoreline-8km",-0.12f,51.5f,8000);
            Preview(before,current,"surfaces-alps-150km",8f,46.5f,150000);
            // Relief-only comparison separates the elevation change from new artwork.
            var unshaded = new Material(current); unshaded.SetFloat("_TerrainReliefStrength",0);
            try { Preview(unshaded,current,"surfaces-relief-alps-150km",8f,46.5f,150000); }
            finally { Object.DestroyImmediate(unshaded); }
        }
        finally
        {
            if (before != null) Object.DestroyImmediate(before);
            if (oldShader != null) Object.DestroyImmediate(oldShader);
            if (previousArray != null) Object.DestroyImmediate(previousArray);
        }
    }

    static void ValidateSurfaceTreatments(Material flat, Material cesium, StringBuilder report)
    {
        var a = new Material(flat); var b = new Material(cesium);
        var color = new Texture2D(2,2,TextureFormat.RGBA32,false,false);
        color.SetPixels(new[] { new Color(0.4f,0.45f,0.35f),new Color(0.4f,0.45f,0.35f),
            new Color(0.4f,0.45f,0.35f),new Color(0.4f,0.45f,0.35f) }); color.Apply();
        var elevation = new Texture2D(1024,512,TextureFormat.RFloat,false,true)
            { wrapModeU=TextureWrapMode.Repeat, wrapModeV=TextureWrapMode.Clamp, filterMode=FilterMode.Bilinear };
        var heights = new Color[1024*512];
        for (int y=0;y<512;y++) for(int x=0;x<1024;x++)
            heights[y*1024+x] = new Color(0.5f+0.18f*(Mathf.Sin(x*0.61f)+Mathf.Cos(y*0.41f)),0,0,1);
        elevation.SetPixels(heights); elevation.Apply();
        Texture2D linear = ClassTexture(12,true), srgb = ClassTexture(12,false);
        try
        {
            foreach(Material material in new[] {a,b})
            {
                material.SetTexture("_TerrainSurfaceTex",color);
                material.SetTexture("_TerrainElevationTex",elevation);
                material.SetFloat("_TerrainReliefStrength",0.35f);
                material.SetFloat("_TerrainDetailStrength",0);
            }
            a.SetTexture("_MainTex",linear); b.SetTexture("_overlayTexture_0",srgb);
            Texture2D shaded = Render(a,12,30,120000,384,216,false);
            Texture2D globe = Render(b,12,30,120000,384,216,true);
            a.SetFloat("_TerrainReliefStrength",0);
            Texture2D unshaded = Render(a,12,30,120000,384,216,false);
            try
            {
                float change = Difference(shaded,unshaded), parity = Difference(shaded,globe);
                Require(change>0.001f && parity<0.015f,"Relief must respond to real slopes and match renderers");
                report.AppendLine($"PASS elevation slopes: image change={change:F6}, renderer difference={parity:F6}");
                File.WriteAllBytes(Path.Combine(Output,"relief-fixture-on.png"),shaded.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(shaded);Object.DestroyImmediate(globe);Object.DestroyImmediate(unshaded); }
            Object.DestroyImmediate(linear);Object.DestroyImmediate(srgb);
            linear = ClassTexture(17,true);srgb = ClassTexture(17,false);
            a.SetTexture("_MainTex",linear);a.SetFloat("_TerrainReliefStrength",1);
            Texture2D waterOn = Render(a,12,30,120000,384,216,false);
            a.SetFloat("_TerrainReliefStrength",0);
            Texture2D waterOff = Render(a,12,30,120000,384,216,false);
            try { Require(Difference(waterOn,waterOff)<0.00001f,"Water must remain flat under relief shading"); }
            finally { Object.DestroyImmediate(waterOn);Object.DestroyImmediate(waterOff); }
            report.AppendLine("PASS water excludes elevation relief");

            Object.DestroyImmediate(linear);Object.DestroyImmediate(srgb);
            linear = BoundaryFixture(true,false);srgb = BoundaryFixture(false,false);
            a.SetTexture("_MainTex",linear);b.SetTexture("_overlayTexture_0",srgb);
            foreach(Material material in new[] {a,b})
            {
                material.SetFloat("_TerrainReliefStrength",0);
                material.SetFloat("_TerrainDetailFadeStart",1000);
                material.SetFloat("_TerrainVegetationEdges",0.8f);
            }
            Texture2D patches = Render(a,0,30,8000,768,432,false);
            Texture2D patchesGlobe = Render(b,0,30,8000,768,432,true,true);
            a.SetFloat("_TerrainVegetationEdges",0);
            Texture2D plain = Render(a,0,30,8000,768,432,false);
            try
            {
                float change = Difference(patches,plain), parity = Difference(patches,patchesGlobe);
                Require(change>0.0001f && parity<0.015f,"Vegetation edge treatment missing or inconsistent");
                RequireBlendBrightness(patches,"Vegetation");
                report.AppendLine($"PASS vegetation patches: image change={change:F6}, renderer difference={parity:F6}");
                File.WriteAllBytes(Path.Combine(Output,"vegetation-fixture.png"),patches.EncodeToPNG());
            }
            finally {Object.DestroyImmediate(patches);Object.DestroyImmediate(patchesGlobe);Object.DestroyImmediate(plain);}
            // Replace the forest/farm split with a straight farm/water coast.
            var pixels = linear.GetPixels32();
            for(int i=0;i<pixels.Length;i++) pixels[i].r = pixels[i].r==1 ? (byte)12 : (byte)17;
            linear.SetPixels32(pixels);linear.Apply();srgb.SetPixels32(pixels);srgb.Apply();
            Texture2D shore = Render(a,0,30,100000,768,432,false);
            Texture2D shoreGlobe = Render(b,0,30,100000,768,432,true,true);
            try
            {
                int mixed = ShoreTransitionPixels(shore);
                Require(mixed>0 && mixed<=5,"Shoreline must be sharp and antialiased: "+mixed);
                Require(Difference(shore,shoreGlobe)<0.015f,"Shoreline renderer mismatch");
                RequireBlendBrightness(shore,"Shoreline");
                report.AppendLine($"PASS shoreline: {mixed} transition pixels across 100km/768px view");
                File.WriteAllBytes(Path.Combine(Output,"shoreline-fixture.png"),shore.EncodeToPNG());
            }
            finally {Object.DestroyImmediate(shore);Object.DestroyImmediate(shoreGlobe);}
        }
        finally
        {
            Object.DestroyImmediate(a);Object.DestroyImmediate(b);Object.DestroyImmediate(color);
            Object.DestroyImmediate(elevation);Object.DestroyImmediate(linear);Object.DestroyImmediate(srgb);
        }
    }

    static int ShoreTransitionPixels(Texture2D image)
    {
        int y=image.height/2, mixed=0;
        float land=image.GetPixel(0,y).r, water=image.GetPixel(image.width-1,y).r;
        for(int x=0;x<image.width;x++)
        {
            float coverage=(image.GetPixel(x,y).r-water)/(land-water);
            if(coverage>0.05f && coverage<0.95f) mixed++;
        }
        return mixed;
    }

    static void RequireBlendBrightness(Texture2D image, string label)
    {
        // These fixtures use constant colors and no detail or relief. A blend
        // must not become darker than either endpoint (e.g. dropped tiny weights).
        Color32[] pixels=image.GetPixels32();
        Color32 left=pixels[image.width*(image.height/2)], right=pixels[image.width*(image.height/2)+image.width-1];
        int minimum=(int)(Math.Min(left.r+left.g+left.b,right.r+right.g+right.b)*0.85f);
        foreach(Color32 pixel in pixels)
            Require(pixel.r+pixel.g+pixel.b>=minimum,label+" blending introduced a dark seam");
    }

    static Texture2D BoundaryFixture(bool linear, bool junction)
    {
        var texture = new Texture2D(1024, 512, TextureFormat.RGBA32, false, linear)
            { filterMode = FilterMode.Point, wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp };
        var pixels = new Color32[1024 * 512];
        for (int y = 0; y < 512; y++)
        for (int x = 0; x < 1024; x++)
        {
            byte id = x < 512 ? (byte)1 : (byte)12;
            if (junction && y < 341) id = x < 512 ? (byte)13 : (byte)17;
            pixels[y * 1024 + x] = new Color32(id,0,0,255);
        }
        texture.SetPixels32(pixels); texture.Apply();
        return texture;
    }

    static void ValidateBoundaries(Material flat, Material cesium, StringBuilder report)
    {
        var linear = BoundaryFixture(true, false);
        var srgb = BoundaryFixture(false, false);
        var a = new Material(flat);
        var b = new Material(cesium);
        var color = new Texture2D(2,2,TextureFormat.RGBA32,false,false);
        color.SetPixels32(new[] { new Color32(110,120,95,255), new Color32(110,120,95,255),
            new Color32(110,120,95,255), new Color32(110,120,95,255) }); color.Apply();
        try
        {
            a.SetTexture("_MainTex", linear); b.SetTexture("_overlayTexture_0", srgb);
            foreach (Material material in new[] { a, b })
            {
                material.SetTexture("_TerrainSurfaceTex", color);
                material.SetFloat("_UseLandcoverTextures", 1);
                material.SetFloat("_UseNaturalTerrain", 1);
                material.SetFloat("_TerrainBoundaryBlend", 1);
                material.SetFloat("_TerrainDetailStrength", 0);
                material.SetFloat("_TerrainDetailFadeStart", 1000);
            }
            Texture2D smooth = Render(a,0,30,100000,384,216,false);
            Texture2D other = Render(b,0,30,100000,384,216,true,true);
            a.SetFloat("_TerrainBoundaryBlend", 0);
            Texture2D hard = Render(a,0,30,100000,384,216,false);
            try
            {
                float hardJump = BoundaryJump(hard), softJump = BoundaryJump(smooth);
                Require(hardJump > 0.003f && softJump < hardJump * 0.4f,
                    $"Boundary still abrupt: hard={hardJump}, blended={softJump}");
                float difference = Difference(smooth, other);
                Require(difference < 0.015f, "Flat/Cesium boundary blending mismatch: " + difference);
                report.AppendLine($"PASS categorical boundary: peak step {hardJump:F6} -> {softJump:F6}; renderer difference={difference:F6}");
                File.WriteAllBytes(Path.Combine(Output,"boundary-fixture-flat.png"),smooth.EncodeToPNG());
                File.WriteAllBytes(Path.Combine(Output,"boundary-fixture-cesium.png"),other.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(smooth); Object.DestroyImmediate(other); Object.DestroyImmediate(hard); }
            Object.DestroyImmediate(linear); Object.DestroyImmediate(srgb);
            linear = BoundaryFixture(true, true); srgb = BoundaryFixture(false, true);
            a.SetTexture("_MainTex", linear); b.SetTexture("_overlayTexture_0", srgb);
            foreach (Material material in new[] { a, b })
            {
                material.SetFloat("_TerrainBoundaryBlend",1);
                material.SetFloat("_TerrainDetailStrength",0.75f);
                material.SetFloat("_TerrainDetailFadeStart",15);
            }
            foreach (float viewWidth in new[] { 20000f, 100000f, 300000f })
            {
                Texture2D x = Render(a,0,30,viewWidth,384,216,false);
                Texture2D y = Render(b,0,30,viewWidth,384,216,true,true);
                try
                {
                    float difference = Difference(x,y);
                    Require(HasImage(x) && difference < 0.015f, "Multi-class junction mismatch: " + difference);
                    report.AppendLine($"PASS forest/farm/urban/water junction: width={viewWidth}m, renderer difference={difference:F6}");
                }
                finally { Object.DestroyImmediate(x); Object.DestroyImmediate(y); }
            }
        }
        finally
        {
            Object.DestroyImmediate(linear); Object.DestroyImmediate(srgb);
            Object.DestroyImmediate(a); Object.DestroyImmediate(b); Object.DestroyImmediate(color);
        }
    }

    static float BoundaryJump(Texture2D image)
    {
        Color32[] pixels = image.GetPixels32(); float maximum = 0;
        for (int y = 4; y < image.height - 4; y++)
        for (int x = image.width/2-12; x < image.width/2+12; x++)
        {
            Color32 a = pixels[y*image.width+x], b = pixels[y*image.width+x+1];
            maximum = Math.Max(maximum, (Math.Abs(a.r-b.r)+Math.Abs(a.g-b.g)+Math.Abs(a.b-b.b))/(3f*255));
        }
        return maximum;
    }

    [MenuItem("Tools/Map/Preview Terrain Boundaries")]
    public static void PreviewBoundaries()
    {
        string directory = ".utmp/terrain-pass/boundaries/";
        if (!File.Exists(directory+"NaturalTerrain.before.hlsl")) return;
        Shader shader = ShaderUtil.CreateShaderAsset(File.ReadAllText(directory+"GlobalMapShader.before.shader")
            .Replace("Shader \"GlobalMapShader\"", "Shader \"Hidden/NaturalTerrainBeforeBoundaries\"")
            .Replace("#include \"NaturalTerrain.hlsl\"", File.ReadAllText(directory+"NaturalTerrain.before.hlsl")));
        Material current = AssetDatabase.LoadAssetAtPath<Material>(FlatPath);
        var before = new Material(shader); before.CopyPropertiesFromMaterial(current);
        try
        {
            Preview(before,current,"boundaries-london-20km",-0.12f,51.5f,20000);
            Preview(before,current,"boundaries-amazon-20km",-60f,-3f,20000);
            Preview(before,current,"boundaries-germany-150km",10.5f,51f,150000);
            Preview(before,current,"boundaries-sahara-150km",10f,25f,150000);
        }
        finally { Object.DestroyImmediate(before); Object.DestroyImmediate(shader); }
    }

    static void ValidateDetailCoverage(Material source, StringBuilder report)
    {
        // A tiny surface-map fixture keeps the detail path active. Check that
        // bounded detail and broad artwork variation remain present across scales.
        var color = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        color.SetPixels32(new[] { new Color32(110,120,95,255), new Color32(110,120,95,255),
            new Color32(110,120,95,255), new Color32(110,120,95,255) });
        color.Apply();
        try
        {
            foreach (byte classId in new byte[] { 1, 10, 13, 16 })
            {
                var id = ClassTexture(classId, true);
                var material = new Material(source);
                try
                {
                    material.SetTexture("_MainTex", id);
                    material.SetTexture("_TerrainSurfaceTex", color);
                    material.SetFloat("_UseLandcoverTextures", 1);
                    material.SetFloat("_UseNaturalTerrain", 1);
                    float closeEnergy = 0;
                    foreach (float metersPerPixel in new[] { 15f, 30f, 60f, 120f, 240f, 480f, 960f })
                    {
                        Texture2D image = Render(material, 12, 30, metersPerPixel * 384, 384, 216, false);
                        try
                        {
                            float energy = DetailEnergy(image);
                            if (closeEnergy == 0) closeEnergy = energy;
                            Require(energy > 0.0005f && energy > closeEnergy * 0.25f,
                                $"Detail disappeared: class {classId}, {metersPerPixel} m/pixel, energy {energy}");
                            report.AppendLine($"PASS continuous detail: class={classId}, {metersPerPixel}m/pixel, edge energy={energy:F6}");
                        }
                        finally { Object.DestroyImmediate(image); }
                    }
                }
                finally { Object.DestroyImmediate(id); Object.DestroyImmediate(material); }
            }
        }
        finally { Object.DestroyImmediate(color); }
    }

    static float DetailEnergy(Texture2D image)
    {
        Color32[] pixels = image.GetPixels32();
        long sum = 0, count = 0;
        for (int y = 4; y < image.height - 4; y++)
        for (int x = 4; x < image.width - 4; x++)
        {
            int i = y * image.width + x;
            Color32 a = pixels[i], b = pixels[i + 1], c = pixels[i + image.width];
            sum += Math.Abs(a.r-b.r) + Math.Abs(a.g-b.g) + Math.Abs(a.b-b.b)
                 + Math.Abs(a.r-c.r) + Math.Abs(a.g-c.g) + Math.Abs(a.b-c.b);
            count += 6;
        }
        return sum / (count * 255f);
    }

    [MenuItem("Tools/Map/Preview Middle Zoom Terrain")]
    public static void PreviewMidZoom()
    {
        string oldInclude = ".utmp/terrain-pass/midzoom/NaturalTerrain.before.hlsl";
        if (!File.Exists(oldInclude)) return;
        string previousShader = ".utmp/terrain-pass/boundaries/GlobalMapShader.before.shader";
        string source = File.ReadAllText(File.Exists(previousShader) ? previousShader : "Assets/Map/Shaders/GlobalMapShader.shader")
            .Replace("Shader \"GlobalMapShader\"", "Shader \"Hidden/NaturalTerrainBeforeMidZoom\"")
            .Replace("#include \"NaturalTerrain.hlsl\"", File.ReadAllText(oldInclude));
        Shader shader = ShaderUtil.CreateShaderAsset(source);
        Material current = AssetDatabase.LoadAssetAtPath<Material>(FlatPath);
        var before = new Material(shader);
        before.CopyPropertiesFromMaterial(current);
        try
        {
            Preview(before, current, "middle-london-150km", -0.12f, 51.5f, 150000);
            Preview(before, current, "middle-germany-300km", 10.5f, 51f, 300000);
            Preview(before, current, "middle-sahara-150km", 10f, 25f, 150000);
            Preview(before, current, "endpoint-london-8km", -0.12f, 51.5f, 8000);
            Preview(before, current, "endpoint-germany-1200km", 10.5f, 51f, 1200000);
        }
        finally { Object.DestroyImmediate(before); Object.DestroyImmediate(shader); }
    }

    static long BlockBytes(int width, int height, int mips, int slices, int bytesPerBlock)
    {
        long bytes = 0;
        for (int mip = 0; mip < mips; mip++)
            bytes += (long)Math.Max(1, ((width >> mip) + 3) / 4)
                * Math.Max(1, ((height >> mip) + 3) / 4) * bytesPerBlock * slices;
        return bytes;
    }

    [MenuItem("Tools/Map/Preview Natural Terrain")]
    public static void PreviewCurrent()
    {
        Material flat = AssetDatabase.LoadAssetAtPath<Material>(FlatPath);
        Shader oldShader = null;
        Material before = null;
        try
        {
            string path = ".utmp/terrain-pass/GlobalMapShader.before.shader";
            if (File.Exists(path))
            {
                oldShader = ShaderUtil.CreateShaderAsset(File.ReadAllText(path)
                    .Replace("Shader \"GlobalMapShader\"", "Shader \"Hidden/NaturalTerrainBefore\""));
                before = new Material(oldShader);
                before.CopyPropertiesFromMaterial(flat);
            }
            Directory.CreateDirectory(Output);
            Preview(before, flat, "country-germany", 10.5f, 51f, 1200000);
            Preview(before, flat, "city-london", -0.12f, 51.5f, 20000);
            Preview(before, flat, "forest-amazon", -60f, -3f, 20000);
            Preview(before, flat, "desert-sahara", 10f, 25f, 20000);
        }
        finally
        {
            if (before != null) Object.DestroyImmediate(before);
            if (oldShader != null) Object.DestroyImmediate(oldShader);
        }
    }

    [MenuItem("Tools/Map/Build and Validate Natural Terrain")]
    public static void BuildAndValidate()
    {
        Directory.CreateDirectory(Output);
        Material flat = AssetDatabase.LoadAssetAtPath<Material>(FlatPath);
        Material before = null;
        Texture2DArray oldArray = null, oldVariants = null;
        Shader oldShader = null;
        try
        {
            string originalPath = ".utmp/terrain-pass/GlobalMapShader.before.shader";
            if (File.Exists(originalPath))
            {
                oldShader = ShaderUtil.CreateShaderAsset(File.ReadAllText(originalPath)
                    .Replace("Shader \"GlobalMapShader\"", "Shader \"Hidden/NaturalTerrainBefore\""));
                before = new Material(oldShader);
                before.CopyPropertiesFromMaterial(flat);
                oldArray = Object.Instantiate(flat.GetTexture("_LandcoverTextureArray")) as Texture2DArray;
                oldVariants = Object.Instantiate(flat.GetTexture("_GrasslandTextureVariants")) as Texture2DArray;
                before.SetTexture("_LandcoverTextureArray", oldArray);
                before.SetTexture("_GrasslandTextureVariants", oldVariants);
            }
            LandcoverTextureArrayGenerator.Generate();
            LandcoverTextureArrayGenerator.ConfigureDesktop();
            Validate();
            flat = AssetDatabase.LoadAssetAtPath<Material>(FlatPath);
            Preview(before, flat, "country-germany", 10.5f, 51f, 1200000);
            Preview(before, flat, "city-london", -0.12f, 51.5f, 20000);
            Preview(before, flat, "forest-amazon", -60f, -3f, 20000);
            Preview(before, flat, "desert-sahara", 10f, 25f, 20000);
        }
        finally
        {
            if (before != null) Object.DestroyImmediate(before);
            if (oldArray != null) Object.DestroyImmediate(oldArray);
            if (oldVariants != null) Object.DestroyImmediate(oldVariants);
            if (oldShader != null) Object.DestroyImmediate(oldShader);
        }
    }

    static Texture2D ClassTexture(byte classId, bool linear)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear) { filterMode = FilterMode.Point };
        var value = new Color32(classId, 0, 0, 255);
        texture.SetPixels32(new[] { value, value, value, value });
        texture.Apply();
        return texture;
    }

    static void Preview(Material before, Material after, string name, float lon, float lat, float width)
    {
        Texture2D right = Render(after, lon, lat, width, 768, 432, false);
        Texture2D left = before == null ? null : Render(before, lon, lat, width, 768, 432, false);
        var combined = new Texture2D(left == null ? 768 : 1536, 432, TextureFormat.RGBA32, false);
        try
        {
            if (left != null) combined.SetPixels32(0, 0, 768, 432, left.GetPixels32());
            combined.SetPixels32(left == null ? 0 : 768, 0, 768, 432, right.GetPixels32());
            combined.Apply();
            File.WriteAllBytes(Path.Combine(Output, name + ".png"), combined.EncodeToPNG());
        }
        finally
        {
            Object.DestroyImmediate(right);
            if (left != null) Object.DestroyImmediate(left);
            Object.DestroyImmediate(combined);
        }
    }

    static Texture2D Render(Material source, float lon, float lat, float widthMeters, int width, int height, bool cesium, bool globalOverlay = false)
    {
        var material = new Material(source);
        var mesh = new Mesh();
        var command = new CommandBuffer { name = "Natural terrain validation" };
        var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        RenderTexture previous = RenderTexture.active;
        Vector4 oldLight = Shader.GetGlobalVector("_MainLightPosition");
        Color oldColor = Shader.GetGlobalColor("_MainLightColor");
        float oldHover = Shader.GetGlobalFloat("_HoverID"), oldSelected = Shader.GetGlobalFloat("_SelectedID");
        try
        {
            float du = widthMeters / (40075016.686f * Mathf.Max(0.05f, Mathf.Cos(lat * Mathf.Deg2Rad)));
            float dv = widthMeters * height / width / 20037508.343f;
            float u = lon / 360 + 0.5f, v = lat / 180 + 0.5f;
            float x0 = (u - du * 0.5f - 0.5f) * 4, x1 = x0 + du * 4;
            float y0 = (v - dv * 0.5f - 0.5f) * Mathf.PI, y1 = y0 + dv * Mathf.PI;
            mesh.vertices = new[] { new Vector3(x0,y0), new Vector3(x1,y0), new Vector3(x0,y1), new Vector3(x1,y1) };
            mesh.triangles = new[] { 0,2,1, 2,3,1 };
            mesh.uv = cesium ? new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one } : new[] {
                new Vector2(u-du/2,v-dv/2), new Vector2(u+du/2,v-dv/2),
                new Vector2(u-du/2,v+dv/2), new Vector2(u+du/2,v+dv/2) };
            material.SetFloat("_SelectedID", -1); material.SetFloat("_HoverID", -1);
            if (cesium)
            {
                material.SetFloat("_FlipOverlayV", 0);
                material.SetFloat("_OverlayGeoRectValid_0", 1);
                material.SetFloat("_overlayTextureCoordinateIndex_0", 0);
                material.SetVector("_overlayTranslationAndScale_0", globalOverlay
                    ? new Vector4(u-du/2,v-dv/2,du,dv) : new Vector4(0,0,1,1));
                material.SetVector("_OverlayGeoUvScaleOffset_0", globalOverlay
                    ? new Vector4(1,1,0,0) : new Vector4(du,dv,u-du/2,v-dv/2));
            }
            else
            {
                material.SetFloat("_Radius", 1); material.SetFloat("_Sphere", 0);
                material.SetFloat("_Morph", 0); material.SetFloat("_HeightExaggeration", 0);
                material.SetVector("_UVOffset", Vector4.zero);
            }
            target.Create();
            command.SetRenderTarget(target);
            command.ClearRenderTarget(true, true, Color.magenta);
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1,1,-1)) * Matrix4x4.Translate(new Vector3(0,0,10));
            // This command renders directly, without a camera's projection flip.
            // Keep geographic north at the top of the ReadPixels/PNG output.
            command.SetViewProjectionMatrices(view, GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(x0,x1,y0,y1,0.1f,100), false));
            command.SetGlobalVector("_MainLightPosition", new Vector4(0,0,-1,0));
            command.SetGlobalColor("_MainLightColor", Color.white);
            command.SetGlobalFloat("_HoverID", -1); command.SetGlobalFloat("_SelectedID", -1);
            command.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0);
            Graphics.ExecuteCommandBuffer(command);
            RenderTexture.active = target;
            var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0,0,width,height),0,0); image.Apply();
            return image;
        }
        finally
        {
            RenderTexture.active = previous;
            Shader.SetGlobalVector("_MainLightPosition", oldLight); Shader.SetGlobalColor("_MainLightColor", oldColor);
            Shader.SetGlobalFloat("_HoverID", oldHover); Shader.SetGlobalFloat("_SelectedID", oldSelected);
            command.Release(); target.Release();
            Object.DestroyImmediate(target); Object.DestroyImmediate(mesh); Object.DestroyImmediate(material);
        }
    }

    static float Difference(Texture2D a, Texture2D b)
    {
        Color32[] x = a.GetPixels32(), y = b.GetPixels32();
        long sum = 0;
        for (int i = 0; i < x.Length; i++)
            sum += Math.Abs(x[i].r-y[i].r) + Math.Abs(x[i].g-y[i].g) + Math.Abs(x[i].b-y[i].b);
        return sum / (x.Length * 3f * 255f);
    }

    static bool HasImage(Texture2D image)
    {
        Color32 pixel = image.GetPixel(image.width / 2, image.height / 2);
        return pixel.g > 0 && !(pixel.r > 240 && pixel.b > 240 && pixel.g < 10);
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Natural terrain validation: " + message);
    }
}
