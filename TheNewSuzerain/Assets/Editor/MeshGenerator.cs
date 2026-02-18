using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class MeshGenerator : EditorWindow
{
    const float PI = 3.1415926535897931f;
    const long SoftVertexWarning = 2_000_000;
    const long HardVertexLimit = 8_000_000;

    enum MeshMode
    {
        World,
        Local
    }

    MeshMode mode = MeshMode.World;

    // World mode
    float radius = 100f;
    int lonSubdiv = 512;
    int latSubdiv = 256;
    bool inXZPlane = false;

    // Local mode
    float patchWidthUnits = 400f;
    float patchHeightUnits = 225f;
    int xSubdiv = 1024;
    int ySubdiv = 576;

    [MenuItem("Tools/World Map/Create Mesh")]
    static void Open() => GetWindow<MeshGenerator>("Mesh Generator");

    void OnGUI()
    {
        EditorGUILayout.LabelField("Mesh Generator", EditorStyles.boldLabel);
        mode = (MeshMode)EditorGUILayout.EnumPopup("Mode", mode);

        EditorGUILayout.Space(4f);

        if (mode == MeshMode.World)
        {
            DrawWorldSettings();
        }
        else
        {
            DrawLocalSettings();
        }

        DrawStats();

        if (GUILayout.Button("Generate"))
        {
            if (!ValidateInput(out string message, out bool confirm))
            {
                EditorUtility.DisplayDialog("Mesh Generator", message, "OK");
                return;
            }

            if (confirm && !EditorUtility.DisplayDialog("Large Mesh Warning", message + "\n\nGenerate anyway?", "Generate", "Cancel"))
            {
                return;
            }

            if (mode == MeshMode.World)
            {
                CreateWorldMesh(radius, lonSubdiv, latSubdiv, inXZPlane);
            }
            else
            {
                CreateLocalMesh(patchWidthUnits, patchHeightUnits, xSubdiv, ySubdiv);
            }
        }
    }

    void DrawWorldSettings()
    {
        EditorGUILayout.LabelField("World Mesh", EditorStyles.boldLabel);
        radius = EditorGUILayout.FloatField("Sphere Radius", radius);
        lonSubdiv = Mathf.Clamp(EditorGUILayout.IntField("Longitude Subdivisions", lonSubdiv), 2, 8192);
        latSubdiv = Mathf.Clamp(EditorGUILayout.IntField("Latitude  Subdivisions", latSubdiv), 2, 8192);
        inXZPlane = EditorGUILayout.Toggle("Lie in X-Z plane (Y-up)", inXZPlane);

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("512 x 256")) { lonSubdiv = 512; latSubdiv = 256; }
            if (GUILayout.Button("1024 x 512")) { lonSubdiv = 1024; latSubdiv = 512; }
            if (GUILayout.Button("2048 x 1024")) { lonSubdiv = 2048; latSubdiv = 1024; }
        }
    }

    void DrawLocalSettings()
    {
        EditorGUILayout.LabelField("Local Detail Mesh", EditorStyles.boldLabel);
        patchWidthUnits = EditorGUILayout.FloatField("Patch Width (Units)", patchWidthUnits);
        patchHeightUnits = EditorGUILayout.FloatField("Patch Height (Units)", patchHeightUnits);
        xSubdiv = Mathf.Clamp(EditorGUILayout.IntField("X Subdivisions", xSubdiv), 2, 8192);
        ySubdiv = Mathf.Clamp(EditorGUILayout.IntField("Y Subdivisions", ySubdiv), 2, 8192);

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("1:1"))
            {
                patchWidthUnits = 400f;
                patchHeightUnits = 400f;
                xSubdiv = 1024;
                ySubdiv = 1024;
            }
            if (GUILayout.Button("2:1"))
            {
                patchWidthUnits = 400f;
                patchHeightUnits = 200f;
                xSubdiv = 1024;
                ySubdiv = 512;
            }
            if (GUILayout.Button("16:9"))
            {
                patchWidthUnits = 400f;
                patchHeightUnits = 225f;
                xSubdiv = 1024;
                ySubdiv = 576;
            }
        }
    }

    void DrawStats()
    {
        int cols = mode == MeshMode.World ? lonSubdiv : xSubdiv;
        int rows = mode == MeshMode.World ? latSubdiv : ySubdiv;

        long vertCount = GetVertexCount(cols, rows);
        long triCount = GetTriangleCount(cols, rows);
        long indexCount = triCount * 3L;
        long estimatedBytes = (vertCount * 32L) + (indexCount * sizeof(int));
        float estimatedMiB = estimatedBytes / (1024f * 1024f);

        float widthUnits = mode == MeshMode.World ? (2f * PI * radius) : patchWidthUnits;
        float heightUnits = mode == MeshMode.World ? (PI * radius) : patchHeightUnits;
        float spacingX = widthUnits / Mathf.Max(1, cols);
        float spacingY = heightUnits / Mathf.Max(1, rows);

        EditorGUILayout.HelpBox(
            $"Vertices: {vertCount:N0}\nTriangles: {triCount:N0}\nEstimated mesh memory: {estimatedMiB:N1} MiB\nSize: {widthUnits:N3} x {heightUnits:N3} units\nVertex spacing: {spacingX:N3} x {spacingY:N3} units",
            MessageType.Info
        );

        if (vertCount > SoftVertexWarning)
        {
            EditorGUILayout.HelpBox("Very large mesh. Generation can be slow and memory-intensive.", MessageType.Warning);
        }
    }

    static long GetVertexCount(int cols, int rows)
    {
        return (long)(cols + 1) * (rows + 1);
    }

    static long GetTriangleCount(int cols, int rows)
    {
        return (long)cols * rows * 2L;
    }

    bool ValidateInput(out string message, out bool confirm)
    {
        message = string.Empty;
        confirm = false;

        if (mode == MeshMode.World)
        {
            if (radius <= 0f)
            {
                message = "Sphere radius must be greater than zero.";
                return false;
            }

            return ValidateGrid(lonSubdiv, latSubdiv, out message, out confirm);
        }

        if (patchWidthUnits <= 0f)
        {
            message = "Patch width must be greater than zero.";
            return false;
        }

        if (patchHeightUnits <= 0f)
        {
            message = "Patch height must be greater than zero.";
            return false;
        }

        return ValidateGrid(xSubdiv, ySubdiv, out message, out confirm);
    }

    static bool ValidateGrid(int cols, int rows, out string message, out bool confirm)
    {
        message = string.Empty;
        confirm = false;

        long vertCount = GetVertexCount(cols, rows);
        if (vertCount > HardVertexLimit)
        {
            message = $"Requested mesh has {vertCount:N0} vertices.\nHard limit is {HardVertexLimit:N0} to avoid editor instability.";
            return false;
        }

        if (vertCount > SoftVertexWarning)
        {
            message = $"Requested mesh has {vertCount:N0} vertices.\nThis may take a while and use significant memory.";
            confirm = true;
        }

        return true;
    }

    static void CreateWorldMesh(float worldRadius, int cols, int rows, bool xzPlane)
    {
        float width = 2f * PI * worldRadius;
        float height = PI * worldRadius;
        float depthExtent = worldRadius;
        string meshName = $"WorldPlane_{cols}x{rows}";
        CreateGridMesh(width, height, cols, rows, xzPlane, depthExtent, meshName, "World plane");
    }

    static void CreateLocalMesh(float width, float height, int cols, int rows)
    {
        float depthExtent = Mathf.Max(width, height) * 0.5f;
        string meshName = $"LocalPatch_Rect_{cols}x{rows}_{width:0}x{height:0}";
        CreateGridMesh(width, height, cols, rows, false, depthExtent, meshName, "Local patch");
    }

    static void CreateGridMesh(
        float width,
        float height,
        int cols,
        int rows,
        bool xzPlane,
        float depthExtent,
        string meshName,
        string logPrefix)
    {
        int vertCount = checked((cols + 1) * (rows + 1));
        int triIndexCount = checked(cols * rows * 6);

        var verts = new Vector3[vertCount];
        var uvs = new Vector2[vertCount];
        var norms = new Vector3[vertCount];
        var tris = new int[triIndexCount];

        for (int y = 0; y <= rows; ++y)
        {
            float vT = (float)y / rows;
            float posV = Mathf.Lerp(-height * 0.5f, height * 0.5f, vT);

            for (int x = 0; x <= cols; ++x)
            {
                int i = y * (cols + 1) + x;
                float uT = (float)x / cols;
                float posU = Mathf.Lerp(-width * 0.5f, width * 0.5f, uT);

                verts[i] = xzPlane
                    ? new Vector3(posU, 0f, posV)
                    : new Vector3(posU, posV, 0f);

                uvs[i] = new Vector2(uT, vT);
                norms[i] = xzPlane ? Vector3.up : Vector3.forward;
            }
        }

        int t = 0;
        for (int y = 0; y < rows; ++y)
        {
            for (int x = 0; x < cols; ++x)
            {
                int i00 = y * (cols + 1) + x;
                int i10 = i00 + 1;
                int i01 = (y + 1) * (cols + 1) + x;
                int i11 = i01 + 1;

                tris[t++] = i00; tris[t++] = i11; tris[t++] = i10;
                tris[t++] = i00; tris[t++] = i01; tris[t++] = i11;
            }
        }

        var mesh = new Mesh { name = meshName };
        if (vertCount > 65535) mesh.indexFormat = IndexFormat.UInt32;

        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.normals = norms;
        mesh.triangles = tris;
        mesh.RecalculateBounds();

        var b = mesh.bounds;
        if (xzPlane)
        {
            b.extents = new Vector3(b.extents.x, depthExtent, b.extents.z);
        }
        else
        {
            b.extents = new Vector3(b.extents.x, b.extents.y, depthExtent);
        }
        mesh.bounds = b;

        string path = AssetDatabase.GenerateUniqueAssetPath($"Assets/{mesh.name}.asset");
        AssetDatabase.CreateAsset(mesh, path);
        AssetDatabase.SaveAssets();

        EditorUtility.FocusProjectWindow();
        Selection.activeObject = mesh;
        Debug.Log($"{logPrefix} mesh generated with {vertCount:N0} verts, {tris.Length / 3:N0} tris\nSaved to: {path}", mesh);
    }
}
