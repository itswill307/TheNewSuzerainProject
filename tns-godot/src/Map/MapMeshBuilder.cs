using System.Collections.Generic;
using Godot;

namespace Tns.Map;

public static class MapMeshBuilder
{
    /// <summary>
    /// Builds the one grid every terrain tile is drawn with. Vertices hold (x, y, skirt): x and y
    /// run 0..1 across the tile from its south-west corner, and the terrain shader turns them into
    /// real positions. Skirt is 1 on an extra strip round the edge that the shader hangs downward
    /// to hide gaps between tiles of different detail.
    /// </summary>
    public static ArrayMesh BuildTileGrid(int quads)
    {
        int side = quads + 1;
        var vertices = new List<Vector3>(side * side + side * 4);
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++) vertices.Add(new Vector3((float)i / quads, (float)j / quads, 0f));
        }

        // Clockwise when seen from +Z, which is Godot's front face.
        var indices = new List<int>(quads * quads * 6 + quads * 4 * 12);
        for (int j = 0; j < quads; j++)
        {
            for (int i = 0; i < quads; i++)
            {
                int a = j * side + i;
                int b = a + 1;
                int c = a + side;
                int d = c + 1;
                indices.AddRange(new[] { a, c, b, b, c, d });
            }
        }

        // Skirt: walk each edge, adding a lowered copy of every edge vertex and joining the two
        // rows. Both faces are added so the strip shows from either side.
        void AddSkirt(System.Func<int, int> edgeVertex)
        {
            int firstLow = vertices.Count;
            for (int k = 0; k < side; k++)
            {
                Vector3 top = vertices[edgeVertex(k)];
                vertices.Add(new Vector3(top.X, top.Y, 1f));
            }

            for (int k = 0; k < quads; k++)
            {
                int top0 = edgeVertex(k), top1 = edgeVertex(k + 1);
                int low0 = firstLow + k, low1 = firstLow + k + 1;
                indices.AddRange(new[] { top0, top1, low1, top0, low1, low0 });
                indices.AddRange(new[] { top0, low1, top1, top0, low0, low1 });
            }
        }

        AddSkirt(k => k);                          // south
        AddSkirt(k => quads * side + k);           // north
        AddSkirt(k => k * side);                   // west
        AddSkirt(k => k * side + quads);           // east

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
