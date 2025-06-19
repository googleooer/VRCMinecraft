using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using System.Text;

/// <summary>
/// This version of McChunk is updated to work with McWorld's new RLE (Run-Length Encoding) storage system.
/// 1.  ADAPTIVE DATA LOADING: Before meshing, it queries McWorld to determine if the chunk's data is
///     compressed (as uniform layers) or uncompressed.
/// 2.  ON-THE-FLY DECOMPRESSION: If the data is compressed, it rapidly decompresses the layer information
///     into its local voxel cache. If uncompressed, it performs a direct copy.
/// 3.  UNCHANGED MESHING CORE: After the local cache is populated, the core meshing logic proceeds as before,
///     benefiting from fast local data access without needing to know about the underlying storage format.
/// 4.  UPDATED NEIGHBOR CHECKS: Inter-chunk neighbor block checks now use the new world.GetBlock() method,
///     which abstracts away the complexity of compressed/uncompressed data lookups.
/// 5.  OPTIMIZED TEXTURE SLICE SUPPORT: Correctly selects texture slices for each face and passes them 
///     to the shader via the Z component of the primary UV channel (uv.z), eliminating the need for uv2.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McChunk : UdonSharpBehaviour
{
    public McWorld world;

    [HideInInspector] public int chunkSizeXZ = 16;
    [HideInInspector] public int chunkSizeY = 16;

    [Header("Component References")]
    public MeshFilter opaqueMeshFilter;
    public MeshFilter transparentMeshFilter;
    public MeshFilter cutoutMeshFilter;
    public MeshCollider meshCollider;
    
    // --- Mesh Data Buffers ---
    private const int MAX_VERTS = 12288;
    private const int MAX_TRIS = (MAX_VERTS / 4) * 6;

    // UVs are now Vector3 to pack in-atlas UVs (x,y) and texture slice index (z) together.
    private Vector3[] _opaqueVertices;
    private int[] _opaqueTriangles;
    private Vector3[] _opaqueUVs; // Using Vector3 for UVs now.
    private Vector3[] _opaqueNormals;
    private int _opaqueVertexCount;
    private int _opaqueTriangleCount;

    private Vector3[] _transparentVertices;
    private int[] _transparentTriangles;
    private Vector3[] _transparentUVs; // Using Vector3 for UVs now.
    private Vector3[] _transparentNormals;
    private int _transparentVertexCount;
    private int _transparentTriangleCount;

    private Vector3[] _cutoutVertices;
    private int[] _cutoutTriangles;
    private Vector3[] _cutoutUVs; // Using Vector3 for UVs now.
    private Vector3[] _cutoutNormals;
    private int _cutoutVertexCount;
    private int _cutoutTriangleCount;

    private Vector3[] _collisionVertices;
    private int[] _collisionTriangles;
    private int _collisionVertexCount;
    private int _collisionTriangleCount;
    
    // Local cache for this chunk's voxel data.
    private ushort[] _localBlockData;
    private int _chunkDataSize;

    [HideInInspector] public int chunkX_world, chunkY_world, chunkZ_world;
    [HideInInspector] public Vector3Int chunkPos_world; 
    [HideInInspector] public bool isBuildingMesh = false;
    
    // --- Face Data & Indices ---
    private const int FACE_INDEX_SIDE = 0;
    private const int FACE_INDEX_TOP = 2;
    private const int FACE_INDEX_BOTTOM = 3;

    private readonly Vector3[] FaceVertices_North = { new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 1) };
    private readonly Vector3[] FaceVertices_East = { new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1) };
    private readonly Vector3[] FaceVertices_South = { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) };
    private readonly Vector3[] FaceVertices_West = { new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0), new Vector3(0, 0, 0) };
    private readonly Vector3[] FaceVertices_Up = { new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0) };
    private readonly Vector3[] FaceVertices_Down = { new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 0) };
    private readonly Vector3 Normal_North = Vector3.forward;
    private readonly Vector3 Normal_East = Vector3.right;
    private readonly Vector3 Normal_South = Vector3.back;
    private readonly Vector3 Normal_West = Vector3.left;
    private readonly Vector3 Normal_Up = Vector3.up;
    private readonly Vector3 Normal_Down = Vector3.down;
    
#if UNITY_EDITOR
    [Header("Debugging")]
    public bool enableVerboseLogging = false;
    private StringBuilder logBuilder_McChunk;
    private float time_ClearData, time_CachePopulation, time_MainLoop;
    private float time_ApplyOpaque, time_ApplyTransparent, time_ApplyCutout, time_ApplyCollision;
#endif

    void Start()
    {
        // Allocate all mesh buffers and the local data cache on start to prevent GC during runtime.
        _opaqueVertices = new Vector3[MAX_VERTS]; _opaqueTriangles = new int[MAX_TRIS]; _opaqueUVs = new Vector3[MAX_VERTS]; _opaqueNormals = new Vector3[MAX_VERTS];
        _transparentVertices = new Vector3[MAX_VERTS]; _transparentTriangles = new int[MAX_TRIS]; _transparentUVs = new Vector3[MAX_VERTS]; _transparentNormals = new Vector3[MAX_VERTS];
        _cutoutVertices = new Vector3[MAX_VERTS]; _cutoutTriangles = new int[MAX_TRIS]; _cutoutUVs = new Vector3[MAX_VERTS]; _cutoutNormals = new Vector3[MAX_VERTS];
        
        _collisionVertices = new Vector3[MAX_VERTS * 3]; _collisionTriangles = new int[MAX_TRIS * 3];

        _chunkDataSize = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
        _localBlockData = new ushort[_chunkDataSize];

        #if UNITY_EDITOR
        if (logBuilder_McChunk == null) logBuilder_McChunk = new StringBuilder(256);
        #endif
    }
    
    private int LocalPosToIndex(int x, int y, int z)
    {
        return y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
    }

    public void BuildMesh()
    {
        if (isBuildingMesh) return;
        if (world == null) return;
        isBuildingMesh = true;

        #if UNITY_EDITOR
        float meshBuildStartTime_total = 0f;
        float timer_start = 0f;
        if (enableVerboseLogging) { meshBuildStartTime_total = Time.realtimeSinceStartup; }
        #endif

        // --- 1. Clear Buffers ---
        ClearAllBuffers();

        // --- 2. Populate Local Cache ---
        PopulateLocalBlockData();

        // --- 3. Main Meshing Loop ---
        for (int y = 0; y < chunkSizeY; y++) {
            for (int z = 0; z < chunkSizeXZ; z++) {
                for (int x = 0; x < chunkSizeXZ; x++) {
                    ushort blockData = _localBlockData[LocalPosToIndex(x, y, z)];
                    if ((blockData & 0x0100) == 0) continue; // Skip air blocks

                    byte blockID = (byte)(blockData & 0xFF);
                    BlockVisibilityType visibility = (BlockVisibilityType)((blockData >> 9) & 0x7);
                    Vector3 blockPos = new Vector3(x, y, z);
                    CheckAndAddFaces(blockPos, blockID, visibility, x, y, z);
                }
            }
        }

        // --- 4. Apply Data to Meshes and Collider ---
        ApplyDataToMesh(opaqueMeshFilter, _opaqueVertices, _opaqueTriangles, _opaqueUVs, _opaqueNormals, _opaqueVertexCount, _opaqueTriangleCount);
        ApplyDataToMesh(transparentMeshFilter, _transparentVertices, _transparentTriangles, _transparentUVs, _transparentNormals, _transparentVertexCount, _transparentTriangleCount);
        ApplyDataToMesh(cutoutMeshFilter, _cutoutVertices, _cutoutTriangles, _cutoutUVs, _cutoutNormals, _cutoutVertexCount, _cutoutTriangleCount);
        ApplyDataToCollider();

        isBuildingMesh = false;
    }

    private void PopulateLocalBlockData()
    {
        object chunkDataObject = world.GetChunkDataObject(chunkX_world, chunkY_world, chunkZ_world);
        if (chunkDataObject == null) return;

        bool isCompressed = world.IsChunkDataLayerCompressed(chunkX_world, chunkY_world, chunkZ_world);

        if (isCompressed)
        {
            ushort[] layerData = (ushort[])chunkDataObject;
            int layerVoxelCount = chunkSizeXZ * chunkSizeXZ;
            for (int y = 0; y < chunkSizeY; y++)
            {
                ushort blockForLayer = layerData[y];
                int layerStartIndex = y * layerVoxelCount;
                for (int i = 0; i < layerVoxelCount; i++)
                {
                    _localBlockData[layerStartIndex + i] = blockForLayer;
                }
            }
        }
        else
        {
            ushort[] fullData = (ushort[])chunkDataObject;
            System.Array.Copy(fullData, _localBlockData, _chunkDataSize);
        }
    }

    private void ClearAllBuffers()
    {
        _opaqueVertexCount = 0; _opaqueTriangleCount = 0;
        _transparentVertexCount = 0; _transparentTriangleCount = 0;
        _cutoutVertexCount = 0; _cutoutTriangleCount = 0;
        _collisionVertexCount = 0; _collisionTriangleCount = 0;
    }
    
    private void CheckAndAddFaces(Vector3 blockPos, byte blockID, BlockVisibilityType visibility, int x, int y, int z)
    {
        chunkPos_world.x = chunkX_world * chunkSizeXZ;
        chunkPos_world.y = chunkY_world * chunkSizeY;
        chunkPos_world.z = chunkZ_world * chunkSizeXZ;

        ushort neighborData;

        // Up (+Y)
        neighborData = (y + 1 < chunkSizeY) ? _localBlockData[LocalPosToIndex(x, y + 1, z)] : GetWorldBlock(chunkPos_world.x + x, chunkPos_world.y + y + 1, chunkPos_world.z + z);
        if (ShouldDrawFace(visibility, neighborData)) AddFace(FaceVertices_Up, Normal_Up, blockPos, blockID, visibility, FACE_INDEX_TOP);

        // Down (-Y)
        neighborData = (y > 0) ? _localBlockData[LocalPosToIndex(x, y - 1, z)] : GetWorldBlock(chunkPos_world.x + x, chunkPos_world.y + y - 1, chunkPos_world.z + z);
        if (ShouldDrawFace(visibility, neighborData)) AddFace(FaceVertices_Down, Normal_Down, blockPos, blockID, visibility, FACE_INDEX_BOTTOM);

        // North (+Z)
        neighborData = (z + 1 < chunkSizeXZ) ? _localBlockData[LocalPosToIndex(x, y, z + 1)] : GetWorldBlock(chunkPos_world.x + x, chunkPos_world.y + y, chunkPos_world.z + z + 1);
        if (ShouldDrawFace(visibility, neighborData)) AddFace(FaceVertices_North, Normal_North, blockPos, blockID, visibility, FACE_INDEX_SIDE);
        
        // South (-Z)
        neighborData = (z > 0) ? _localBlockData[LocalPosToIndex(x, y, z - 1)] : GetWorldBlock(chunkPos_world.x + x, chunkPos_world.y + y, chunkPos_world.z + z - 1);
        if (ShouldDrawFace(visibility, neighborData)) AddFace(FaceVertices_South, Normal_South, blockPos, blockID, visibility, FACE_INDEX_SIDE);

        // East (+X)
        neighborData = (x + 1 < chunkSizeXZ) ? _localBlockData[LocalPosToIndex(x + 1, y, z)] : GetWorldBlock(chunkPos_world.x + x + 1, chunkPos_world.y + y, chunkPos_world.z + z);
        if (ShouldDrawFace(visibility, neighborData)) AddFace(FaceVertices_East, Normal_East, blockPos, blockID, visibility, FACE_INDEX_SIDE);

        // West (-X)
        neighborData = (x > 0) ? _localBlockData[LocalPosToIndex(x - 1, y, z)] : GetWorldBlock(chunkPos_world.x + x - 1, chunkPos_world.y + y, chunkPos_world.z + z);
        if (ShouldDrawFace(visibility, neighborData)) AddFace(FaceVertices_West, Normal_West, blockPos, blockID, visibility, FACE_INDEX_SIDE);
    }
    
    private ushort GetWorldBlock(int globalX, int globalY, int globalZ)
    {
        return world.GetBlock(globalX, globalY, globalZ);
    }
    
    private bool ShouldDrawFace(BlockVisibilityType selfVisibility, ushort neighborData)
    {
        bool neighborIsSolid = (neighborData & 0x0100) != 0;
        if (!neighborIsSolid) return true;

        BlockVisibilityType neighborVisibility = (BlockVisibilityType)((neighborData >> 9) & 0x7);
        
        if (neighborVisibility == BlockVisibilityType.Opaque && selfVisibility == BlockVisibilityType.Opaque) return false;
        if (selfVisibility == neighborVisibility && selfVisibility != BlockVisibilityType.Opaque) return false;
        
        return true;
    }

    private void AddFace(Vector3[] faceVertices, Vector3 faceNormal, Vector3 blockPos, byte blockID, BlockVisibilityType visibility, int faceIndex)
    {
        Vector3[] targetVertices; int[] targetTriangles; Vector3[] targetUVs; Vector3[] targetNormals;
        int currentVertexCount; int currentTriangleCount;
        
        if (visibility == BlockVisibilityType.Opaque) {
            targetVertices = _opaqueVertices; targetTriangles = _opaqueTriangles; targetUVs = _opaqueUVs; targetNormals = _opaqueNormals;
            currentVertexCount = _opaqueVertexCount; currentTriangleCount = _opaqueTriangleCount;
        } else if (visibility == BlockVisibilityType.Transparent) {
            targetVertices = _transparentVertices; targetTriangles = _transparentTriangles; targetUVs = _transparentUVs; targetNormals = _transparentNormals;
            currentVertexCount = _transparentVertexCount; currentTriangleCount = _transparentTriangleCount;
        } else { // Cutout
            targetVertices = _cutoutVertices; targetTriangles = _cutoutTriangles; targetUVs = _cutoutUVs; targetNormals = _cutoutNormals;
            currentVertexCount = _cutoutVertexCount; currentTriangleCount = _cutoutTriangleCount;
        }

        targetVertices[currentVertexCount + 0] = blockPos + faceVertices[0];
        targetVertices[currentVertexCount + 1] = blockPos + faceVertices[1];
        targetVertices[currentVertexCount + 2] = blockPos + faceVertices[2];
        targetVertices[currentVertexCount + 3] = blockPos + faceVertices[3];
        
        for (int i=0; i<4; i++) targetNormals[currentVertexCount + i] = faceNormal;
        
        float textureSlice = world.blockTypeManager.GetFinalBlockTextureSlice(blockID, faceIndex);
        targetUVs[currentVertexCount + 0] = new Vector3(0, 0, textureSlice); 
        targetUVs[currentVertexCount + 1] = new Vector3(0, 1, textureSlice);
        targetUVs[currentVertexCount + 2] = new Vector3(1, 1, textureSlice); 
        targetUVs[currentVertexCount + 3] = new Vector3(1, 0, textureSlice);
        
        targetTriangles[currentTriangleCount + 0] = currentVertexCount; 
        targetTriangles[currentTriangleCount + 1] = currentVertexCount + 1; 
        targetTriangles[currentTriangleCount + 2] = currentVertexCount + 2;
        targetTriangles[currentTriangleCount + 3] = currentVertexCount; 
        targetTriangles[currentTriangleCount + 4] = currentVertexCount + 2; 
        targetTriangles[currentTriangleCount + 5] = currentVertexCount + 3;

        if (visibility == BlockVisibilityType.Opaque) { _opaqueVertexCount += 4; _opaqueTriangleCount += 6; }
        else if (visibility == BlockVisibilityType.Transparent) { _transparentVertexCount += 4; _transparentTriangleCount += 6; }
        else { _cutoutVertexCount += 4; _cutoutTriangleCount += 6; }

        if (visibility != BlockVisibilityType.Transparent && _collisionVertexCount < MAX_VERTS * 3 - 4)
        {
            _collisionVertices[_collisionVertexCount + 0] = blockPos + faceVertices[0]; 
            _collisionVertices[_collisionVertexCount + 1] = blockPos + faceVertices[1];
            _collisionVertices[_collisionVertexCount + 2] = blockPos + faceVertices[2]; 
            _collisionVertices[_collisionVertexCount + 3] = blockPos + faceVertices[3];

            _collisionTriangles[_collisionTriangleCount++] = _collisionVertexCount; 
            _collisionTriangles[_collisionTriangleCount++] = _collisionVertexCount + 1; 
            _collisionTriangles[_collisionTriangleCount++] = _collisionVertexCount + 2;
            _collisionTriangles[_collisionTriangleCount++] = _collisionVertexCount; 
            _collisionTriangles[_collisionTriangleCount++] = _collisionVertexCount + 2; 
            _collisionTriangles[_collisionTriangleCount++] = _collisionVertexCount + 3;
            _collisionVertexCount += 4;
        }
    }

    private void ApplyDataToMesh(MeshFilter mf, Vector3[] vertices, int[] triangles, Vector3[] uvs, Vector3[] normals, int vertexCount, int triangleCount)
    {
        if (mf == null) return;
        Mesh m = mf.sharedMesh;
        if (m == null) { m = new Mesh(); m.name = $"ChunkMesh_{mf.gameObject.name}"; mf.sharedMesh = m; }
        
        m.Clear();
        if (vertexCount == 0) { mf.gameObject.SetActive(false); return; }
        
        mf.gameObject.SetActive(true);
        Vector3[] finalVertices = new Vector3[vertexCount]; System.Array.Copy(vertices, finalVertices, vertexCount);
        int[] finalTriangles = new int[triangleCount]; System.Array.Copy(triangles, finalTriangles, triangleCount);
        Vector3[] finalNormals = new Vector3[vertexCount]; System.Array.Copy(normals, finalNormals, vertexCount);
        Vector3[] finalUVs = new Vector3[vertexCount]; System.Array.Copy(uvs, finalUVs, vertexCount);
        
        m.vertices = finalVertices;
        m.triangles = finalTriangles;
        m.normals = finalNormals;
        
        // Apply the packed UVs (u, v, slice) to the primary UV channel (0) using a Udon-compatible array.
        m.SetUVs(0, finalUVs);

        // Clear other UV channels to ensure they don't contain old data.
        m.uv2 = null;
        m.uv3 = null;
        m.uv4 = null;
        
        m.RecalculateBounds();
    }

    private void ApplyDataToCollider()
    {
        if (meshCollider == null) return;
        Mesh colMesh = meshCollider.sharedMesh;
        if (colMesh == null) { colMesh = new Mesh(); colMesh.name = $"ChunkCollisionMesh_{gameObject.name}"; }
        
        colMesh.Clear();
        if (_collisionVertexCount == 0) { meshCollider.sharedMesh = null; meshCollider.enabled = false; return; }
        
        meshCollider.enabled = true;
        Vector3[] finalVertices = new Vector3[_collisionVertexCount]; System.Array.Copy(_collisionVertices, finalVertices, _collisionVertexCount);
        int[] finalTriangles = new int[_collisionTriangleCount]; System.Array.Copy(_collisionTriangles, finalTriangles, _collisionTriangleCount);

        colMesh.vertices = finalVertices;
        colMesh.triangles = finalTriangles;
        meshCollider.sharedMesh = colMesh;
    }
}
