using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Bakes b1.7.3's fancy clouds (RenderGlobal.renderCloudsFancy) into REAL 3D meshes: one
/// 12x4x12 box per opaque cell of clouds.png, interior faces culled (a face is emitted only
/// when the neighbouring cell is NOT a cloud), each face carrying its vanilla per-face shade in
/// the vertex color (top 1.0, bottom 0.7, X-sides 0.9, Z-sides 0.8). The layer is a 2D GRID of
/// square CLUSTER CHUNKS (CloudChunk_i_j.asset) — X spans one texture period (256 cells = 3072
/// blocks) so two copies offset by a period tile seamlessly as it scrolls; Z is a fixed window
/// covering the world (+-256) + fog reach. Each chunk is a separate renderer that Unity frustum-
/// culls on BOTH axes, so only the ~2-4 chunks the player can actually see draw (a few thousand
/// tris) instead of the whole layer. Cloud color + 0.8 alpha are applied live by VRCM/MCClouds.
/// Run: Tools > VRCMinecraft > Bake b1.7.3 Cloud Mesh.
/// </summary>
public class CloudMeshBaker
{
    private const string TEX_PATH = "Assets/VRCMinecraft/textures/environment/clouds.png";
    private const string OUT_DIR = "Assets/VRCMinecraft/Meshes";
    private const float CELL = 12f;      // b1.7.3 fancy cloud cell width (blocks)
    private const float BOT = 108f;      // slab bottom (getCloudHeight)
    private const float TOP = 112f;      // slab top (bottom + 4)
    private const int PERIOD = 256;      // texture cells per period (X tiling)
    private const int CHUNK = 32;        // cells per square chunk (32*12 = 384 blocks)
    private const int NX = PERIOD / CHUNK;   // 8 X chunks (cover the period)
    private const int NZ = 3;                // Z chunks; j=0..2 cover cells [-48, 48) = +-576 blocks
    private const int CZ_BASE = -48;         // world Z window: +-256 world + fog, over-covered

    [MenuItem("Tools/VRCMinecraft/Bake b1.7.3 Cloud Mesh")]
    public static void Bake()
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(TEX_PATH);
        if (importer == null) { Debug.LogError("[CloudMeshBaker] clouds.png not found at " + TEX_PATH); return; }
        bool wasReadable = importer.isReadable;
        if (!wasReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(TEX_PATH);
        Color32[] px = tex.GetPixels32();
        int W = tex.width, H = tex.height; // 256 x 256
        if (!wasReadable) { importer.isReadable = false; importer.SaveAndReimport(); }

        // cloud[cx,cz] from the alpha channel. Pattern wraps both axes (period 256) so chunks and
        // the two period tiles cull faces consistently and join seamlessly across every boundary.
        System.Func<int, int, bool> isCloud = (cx, cz) =>
        {
            int u = ((cx % W) + W) % W;
            int v = ((cz % H) + H) % H;
            return px[v * W + u].a > 127;
        };

        Directory.CreateDirectory(OUT_DIR);
        int totalCells = 0, totalTris = 0, chunkCount = 0;
        for (int ci = 0; ci < NX; ci++)
        {
            for (int cj = 0; cj < NZ; cj++)
            {
                var verts = new List<Vector3>();
                var cols = new List<Color32>();
                var tris = new List<int>();
                System.Action<Vector3, Vector3, Vector3, Vector3, float> quad = (a, b, c, d, shade) =>
                {
                    int i0 = verts.Count;
                    verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
                    var col = new Color32((byte)(shade * 255f), (byte)(shade * 255f), (byte)(shade * 255f), 255);
                    cols.Add(col); cols.Add(col); cols.Add(col); cols.Add(col);
                    tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
                    tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
                };

                int cxStart = ci * CHUNK, cxEnd = cxStart + CHUNK;
                int czStart = CZ_BASE + cj * CHUNK, czEnd = czStart + CHUNK;
                for (int cx = cxStart; cx < cxEnd; cx++)
                {
                    for (int cz = czStart; cz < czEnd; cz++)
                    {
                        if (!isCloud(cx, cz)) continue;
                        totalCells++;
                        // Absolute X/Z (cx*12, cz*12) so a chunk's bounds sit at its true world
                        // column; the two period tiles place chunk children at local 0 and scroll
                        // the parent in X. Face culling uses the wrapped pattern neighbour, so
                        // chunk edges and the period-tile seam join with no doubled walls or gaps.
                        float x0 = cx * CELL, x1 = x0 + CELL;
                        float z0 = cz * CELL, z1 = z0 + CELL;
                        quad(new Vector3(x0, TOP, z1), new Vector3(x1, TOP, z1), new Vector3(x1, TOP, z0), new Vector3(x0, TOP, z0), 1.0f); // top +Y
                        quad(new Vector3(x0, BOT, z0), new Vector3(x1, BOT, z0), new Vector3(x1, BOT, z1), new Vector3(x0, BOT, z1), 0.7f); // bottom -Y
                        // Sides emitted only when the neighbour cell is NOT a cloud (exterior shell).
                        // Winding gives OUTWARD normals (verified) — rendered Cull Off like vanilla.
                        if (!isCloud(cx - 1, cz)) quad(new Vector3(x0, BOT, z1), new Vector3(x0, TOP, z1), new Vector3(x0, TOP, z0), new Vector3(x0, BOT, z0), 0.9f); // -X
                        if (!isCloud(cx + 1, cz)) quad(new Vector3(x1, BOT, z0), new Vector3(x1, TOP, z0), new Vector3(x1, TOP, z1), new Vector3(x1, BOT, z1), 0.9f); // +X
                        if (!isCloud(cx, cz - 1)) quad(new Vector3(x0, BOT, z0), new Vector3(x0, TOP, z0), new Vector3(x1, TOP, z0), new Vector3(x1, BOT, z0), 0.8f); // -Z
                        if (!isCloud(cx, cz + 1)) quad(new Vector3(x1, BOT, z1), new Vector3(x1, TOP, z1), new Vector3(x0, TOP, z1), new Vector3(x0, BOT, z1), 0.8f); // +Z
                    }
                }
                if (verts.Count == 0) continue; // empty cluster -> no renderer needed
                chunkCount++;

                var mesh = new Mesh();
                mesh.name = "CloudChunk_" + ci + "_" + cj;
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(verts);
                mesh.SetColors(cols);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                totalTris += tris.Count / 3;

                string path = OUT_DIR + "/CloudChunk_" + ci + "_" + cj + ".asset";
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing != null) { EditorUtility.CopySerialized(mesh, existing); Object.DestroyImmediate(mesh); }
                else AssetDatabase.CreateAsset(mesh, path);
            }
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[CloudMeshBaker] Baked {totalCells} cloud cells into {chunkCount} cluster chunks ({NX}x{NZ} grid) -> {totalTris} tris total ({OUT_DIR}/CloudChunk_i_j.asset)");
    }
}
