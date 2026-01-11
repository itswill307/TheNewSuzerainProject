// Assets/Editor/WorldPlaneMeshGenerator.cs
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System.IO;

public class WorldPlaneMeshGenerator : EditorWindow
{
    const float PI = 3.1415926535897931f;

    [MenuItem("Tools/World Map/Create World Plane Mesh")]
    static void Open() => GetWindow<WorldPlaneMeshGenerator>("World Plane Mesh");

    float radius = 100f;
    int   lonSubdiv = 512;
    int   latSubdiv = 256;
    bool  inXZPlane = true;
    bool  addMeshCollider = false;

    void OnGUI()
    {
        radius        = EditorGUILayout.FloatField("Sphere Radius", radius);
        lonSubdiv     = Mathf.Clamp(EditorGUILayout.IntField("Longitude Subdivisions", lonSubdiv), 2, 8192);
        latSubdiv     = Mathf.Clamp(EditorGUILayout.IntField("Latitude  Subdivisions",  latSubdiv), 2, 8192);
        inXZPlane     = EditorGUILayout.Toggle("Lie in X-Z plane (Y-up)", inXZPlane);
        addMeshCollider = EditorGUILayout.Toggle("Add MeshCollider", addMeshCollider);

        if (GUILayout.Button("Generate"))
            CreateMesh(radius, lonSubdiv, latSubdiv, inXZPlane, addMeshCollider);
    }

    static void CreateMesh(float R, int cols, int rows, bool xzPlane, bool addCollider)
    {
        float width  = 2f * PI * R;
        float height = PI * R;

        int vertCount = (cols + 1) * (rows + 1);
        int triCount  = cols * rows * 6;

        var verts = new Vector3[vertCount];
        var uvs   = new Vector2[vertCount];
        var norms = new Vector3[vertCount];
        var tris  = new int[triCount];

        // --- build vertex data
        for (int y = 0; y <= rows; ++y)
        {
            float vT   = (float)y / rows;
            float posV = Mathf.Lerp(-height * 0.5f, height * 0.5f, vT);

            for (int x = 0; x <= cols; ++x)
            {
                int   i   = y * (cols + 1) + x;
                float uT  = (float)x / cols;
                float posU = Mathf.Lerp(-width * 0.5f, width * 0.5f, uT);

                verts[i] = xzPlane
                           ? new Vector3(posU, 0f, posV)   // X-Z plane
                           : new Vector3(posU, posV, 0f);  // X-Y plane

                uvs[i]   = new Vector2(uT, vT);
                norms[i] = xzPlane ? Vector3.up : Vector3.forward;
            }
        }

        // --- build triangle indices
        int t = 0;
        for (int y = 0; y < rows; ++y)
            for (int x = 0; x < cols; ++x)
            {
                int i00 =  y      * (cols + 1) + x;
                int i10 =  i00 + 1;
                int i01 = (y + 1) * (cols + 1) + x;
                int i11 =  i01 + 1;

                tris[t++] = i00; tris[t++] = i11; tris[t++] = i10;
                tris[t++] = i00; tris[t++] = i01; tris[t++] = i11;
            }

        // --- assemble mesh
        var mesh = new Mesh { name = $"WorldPlane_{cols}x{rows}" };

        // >>> set index format FIRST  <<<
        if (vertCount > 65535) mesh.indexFormat = IndexFormat.UInt32;

        mesh.vertices  = verts;
        mesh.uv        = uvs;
        mesh.normals   = norms;
        mesh.triangles = tris;
        mesh.RecalculateBounds();

        var b = mesh.bounds;                         // current box: size.x ≈ 2πR, size.y ≈ πR, size.z ≈ 0
        b.extents = new Vector3(b.extents.x,         // keep existing X
                                b.extents.y,         // keep existing Y
                                R);                 // allow ±R in Z for the sphere morph
        mesh.bounds = b;

        // --- save mesh asset only
        string path = AssetDatabase.GenerateUniqueAssetPath($"Assets/{mesh.name}.asset");
        AssetDatabase.CreateAsset(mesh, path);
        AssetDatabase.SaveAssets();

        EditorUtility.FocusProjectWindow();
        Selection.activeObject = mesh;
        Debug.Log($"World plane mesh generated with {vertCount:N0} verts, {tris.Length/3:N0} tris\nSaved to: {path}", mesh);
    }
}
