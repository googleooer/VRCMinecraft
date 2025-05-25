using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using Varneon.VUdon.ArrayExtensions;
using UnityEditor;
using VRC.Udon.Serialization.OdinSerializer.Utilities;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McChunk : UdonSharpBehaviour
{
    [SerializeField] private MinecraftGame minecraftGame;
    public bool template = false;
    
    // Profiling variables
    private float blockCacheTime;
    private float blockScanTime;
    private float blockCheckTime;
    private float faceCheckTime;
    private float vertexGenTime;
    private float uvGenTime;
    private float collisionMaskTime;
    private float collisionGreedyTime;
    private float collisionVertexTime;
    private float meshFinalizationTime;
    private float meshNormalsTime;
    private float collisionNormalsTime;
    private float totalTime;
    private int totalBlockChecks;
    private int totalFacesGenerated;
    private int totalCollisionFaces;

    Vector3[] newVertices = new Vector3[0];
    int[] newTriangles = new int[0];
    Vector2[] newUV = new Vector2[0];

    float tUnit = 0.0625f;
    float uvPadding = 0.00015f;
    Vector2 tStone = new Vector2(1,0);
    Vector2 tGrass = new Vector2(3,0);
    Vector2 tGrassTop = new Vector2(0,0);

    Mesh mesh;
    Mesh collisionMesh;
    MeshCollider col;

    int faceCount;


    public GameObject worldGO;
    private McWorld world;

    public int chunkSizeXZ = 32;
    public int chunkSizeY = 32;

    // Pre-allocated arrays to avoid constant resizing
    private const int MAX_FACES = 32 * 32 * 32 * 6; // Maximum possible faces
    private const int VERTICES_PER_FACE = 4;
    private Vector3[] vertexPool;
    private int[] trianglePool;
    private Vector2[] uvPool;
    private int vertexCount = 0;
    private int triangleCount = 0;
    private int uvCount = 0;

    // Pre-allocated arrays for collision mesh
    private Vector3[] collisionVertexPool;
    private int[] collisionTrianglePool;
    private int collisionVertexCount = 0;
    private int collisionTriangleCount = 0;
    private bool[] collisionMask;
    private bool[] sectionMask; // 4x4x4 sections for quick empty space skipping
    private const int SECTION_SIZE = 8; // Size of each section for skipping
    private int sectionsPerAxis; // Calculated based on chunkSize and SECTION_SIZE

    // Cache for block data to reduce world lookups
    private byte[] blockCache;
    private bool isCacheValid = false;

    // Pre-calculated values to avoid multiplication in loops
    private int chunkSizeXZY; // chunkSizeXZ * chunkSizeY
    private int chunkSizeXZXZ; // chunkSizeXZ * chunkSizeXZ

    public int chunkX;
    public int chunkY;
    public int chunkZ;

    [SerializeField] public VoxelCollider basicBlockCollider;



    public bool update;

    void LateUpdate()
    {
        if(update)
        {
            GenerateMesh();
            update = false;
        }
    }




    void Start()
    {
        if(template) return;
        world = worldGO.GetComponent<McWorld>();
        mesh = GetComponent<MeshFilter>().mesh;
        col = GetComponent<MeshCollider>();
        collisionMesh = new Mesh();

        // Pre-calculate array indices multipliers
        chunkSizeXZY = chunkSizeXZ * chunkSizeY;
        chunkSizeXZXZ = chunkSizeXZ * chunkSizeXZ;

        // Initialize pools
        vertexPool = new Vector3[MAX_FACES * VERTICES_PER_FACE];
        trianglePool = new int[MAX_FACES * 6]; // 6 indices per face (2 triangles)
        uvPool = new Vector2[MAX_FACES * VERTICES_PER_FACE];

        // Initialize collision pools
        collisionVertexPool = new Vector3[MAX_FACES * VERTICES_PER_FACE];
        collisionTrianglePool = new int[MAX_FACES * 6];
        
        // Ensure collisionMask is large enough for any 2D slice orientation
        int maxCollisionDim = Mathf.Max(chunkSizeXZ, chunkSizeY);
        collisionMask = new bool[maxCollisionDim * maxCollisionDim];
        
        sectionsPerAxis = chunkSizeXZ / SECTION_SIZE; // Assuming chunkSizeXZ is representative for all axes for sections.
        sectionMask = new bool[sectionsPerAxis * sectionsPerAxis * sectionsPerAxis];

        GenerateMesh();
    }

    public void OnInstance()
    {
        Start();
    }

    private void CacheChunkData() {
        if (blockCache == null) {
            blockCache = new byte[chunkSizeXZ * chunkSizeY * chunkSizeXZ];
            // sectionMask initialized in Start, ensure it's done if CacheChunkData is called before Start (e.g. OnInstance path)
            if (sectionMask == null) {
                sectionsPerAxis = chunkSizeXZ / SECTION_SIZE;
                sectionMask = new bool[sectionsPerAxis * sectionsPerAxis * sectionsPerAxis];
            }
        } else {
            // Clear section mask for reuse
            for(int i = 0; i < sectionMask.Length; i++) {
                sectionMask[i] = false;
            }
        }

        // Pre-fetch all block data in a more cache-friendly pattern
        int index = 0;
        for (int z = 0; z < chunkSizeXZ; z++) {
            for (int y = 0; y < chunkSizeY; y++) {
                for (int x = 0; x < chunkSizeXZ; x++) {
                    byte block = world.Block(x + chunkX, y + chunkY, z + chunkZ);
                    blockCache[index++] = block;
                    
                    // Mark section as non-empty if it contains any blocks
                    if (block != 0) {
                        int sectionX = x / SECTION_SIZE; // x / 8
                        int sectionY = y / SECTION_SIZE;
                        int sectionZ = z / SECTION_SIZE;
                        sectionMask[sectionX + (sectionY * sectionsPerAxis) + (sectionZ * sectionsPerAxis * sectionsPerAxis)] = true;
                    }
                }
            }
        }
        isCacheValid = true;
    }

    byte Block(int x, int y, int z) {
        // Use cached data if available and within bounds
        if (isCacheValid && x >= 0 && x < chunkSizeXZ && y >= 0 && y < chunkSizeY && z >= 0 && z < chunkSizeXZ) {
            return blockCache[x + y * chunkSizeXZ + z * chunkSizeXZY];
        }
        
        // Convert to world coordinates
        int worldX = x + chunkX;
        int worldY = y + chunkY;
        int worldZ = z + chunkZ;
        
        return world.Block(worldX, worldY, worldZ);
    }

    private void ResetPools()
    {
        vertexCount = 0;
        triangleCount = 0;
        uvCount = 0;
        faceCount = 0;
        collisionVertexCount = 0;
        collisionTriangleCount = 0;
    }

    void AddFaceVertices(Vector3 v1, Vector3 v2, Vector3 v3, Vector3 v4)
    {
        vertexPool[vertexCount++] = v1;
        vertexPool[vertexCount++] = v2;
        vertexPool[vertexCount++] = v3;
        vertexPool[vertexCount++] = v4;

        // Add triangles
        int baseIndex = faceCount * 4;
        trianglePool[triangleCount++] = baseIndex;
        trianglePool[triangleCount++] = baseIndex + 1;
        trianglePool[triangleCount++] = baseIndex + 2;
        trianglePool[triangleCount++] = baseIndex;
        trianglePool[triangleCount++] = baseIndex + 2;
        trianglePool[triangleCount++] = baseIndex + 3;

        faceCount++;
    }

    void AddQuadUVs_Standard() // For greedy quads using full texture space
    {
        float uvStart = Time.realtimeSinceStartup;
        // Standard UVs for a quad: (0,0), (1,0), (1,1), (0,1) or similar, depending on vertex order.
        // Assuming AddFaceVertices v0,v1,v2,v3 corresponds to bottom-left, bottom-right, top-right, top-left for a front-facing quad
        uvPool[uvCount++] = new Vector2(0f, 0f); // For v0
        uvPool[uvCount++] = new Vector2(1f, 0f); // For v1
        uvPool[uvCount++] = new Vector2(1f, 1f); // For v2
        uvPool[uvCount++] = new Vector2(0f, 1f); // For v3
        uvGenTime += Time.realtimeSinceStartup - uvStart;
    }

    private Vector2 GetTextureForFace(byte block, int faceDirection)
    {
        // faceDirection: 0=+X (East), 1=-X (West), 2=+Y (Top), 3=-Y (Bottom), 4=+Z (North), 5=-Z (South)
        if (block == 1) return tStone; // Stone is same on all sides
        if (block == 2) { // Grass block
            switch (faceDirection) {
                case 2: return tGrassTop; // +Y (Top face)
                default: return tGrass;   // Sides and Bottom
            }
        }
        // Fallback for unknown or air blocks (though should not be called for air)
        // You might want to define more textures for other block types here
        return new Vector2(0,0); // Default texture (e.g. an error texture or first in atlas)
    }

    private void ResetTimings()
    {
        blockCacheTime = 0;
        blockScanTime = 0;
        blockCheckTime = 0;
        faceCheckTime = 0;
        vertexGenTime = 0;
        uvGenTime = 0;
        collisionMaskTime = 0;
        collisionGreedyTime = 0;
        collisionVertexTime = 0;
        meshFinalizationTime = 0;
        meshNormalsTime = 0;
        collisionNormalsTime = 0;
        totalTime = 0;
        totalBlockChecks = 0;
        totalFacesGenerated = 0;
        totalCollisionFaces = 0;
    }

    public void GenerateMesh()
    {
        if(template) return;
        
        float startTime = Time.realtimeSinceStartup;
        ResetPools();
        ResetTimings();

        float cacheStart = Time.realtimeSinceStartup;
        CacheChunkData();
        blockCacheTime = Time.realtimeSinceStartup - cacheStart;
        
        float scanStart = Time.realtimeSinceStartup;
        bool hasBlocks = false;
        for (int i = 0; i < blockCache.Length; i++) {
            if (blockCache[i] != 0) {
                hasBlocks = true;
                break;
            }
        }
        blockScanTime = Time.realtimeSinceStartup - scanStart;
        
        if (hasBlocks) {
            float blockProcessingStart = Time.realtimeSinceStartup;

            // Temporary mask for one slice. Max dimension: Mathf.Max(chunkSizeXZ, chunkSizeY)
            int maxSliceDim = Mathf.Max(chunkSizeXZ, chunkSizeY);
            Vector2[] faceTextureMask = new Vector2[maxSliceDim * maxSliceDim]; // Stores texturePos. Use a sentinel like (-1,-1) for no face.
            Vector2 noFaceSentinel = new Vector2(-1, -1);

            // Iterate 3 axes (d = 0:X, 1:Y, 2:Z)
            for (int d = 0; d < 3; d++) {
                int u = (d + 1) % 3; // Orthogonal axis 1
                int v = (d + 2) % 3; // Orthogonal axis 2

                Vector3Int q = Vector3Int.zero; // Normal direction vector for current axis d
                q[d] = 1;

                // Loop for two directions/sides along axis d (+q and -q)
                // side 0 = +q face (e.g. +X faces of blocks at x_i, normal is +X)
                // side 1 = -q face (e.g. -X faces of blocks at x_i, normal is -X)
                for (int side = 0; side < 2; side++) {
                    
                    int dimD = (d == 1) ? chunkSizeY : chunkSizeXZ;
                    int dimU = (u == 1) ? chunkSizeY : chunkSizeXZ; // Width of the slice
                    int dimV = (v == 1) ? chunkSizeY : chunkSizeXZ; // Height of the slice

                    Vector3Int x = Vector3Int.zero; // Current scanner position in local chunk coordinates

                    // Sweep across axis d (slices of the chunk)
                    for (x[d] = 0; x[d] < dimD; x[d]++) {
                        
                        // 1. Populate faceTextureMask for this slice
                        // Clear mask with sentinel value
                        for(int i=0; i < dimU * dimV; i++) faceTextureMask[i] = noFaceSentinel; 
                        
                        float faceCheckStartTime = Time.realtimeSinceStartup;
                        for (x[u] = 0; x[u] < dimU; x[u]++) {
                            for (x[v] = 0; x[v] < dimV; x[v]++) {
                                byte blockCurrent;
                                byte blockNeighbor;
                                int faceDir;

                                if (side == 0) { // Positive face relative to x (e.g. +X face of block at x)
                                    blockCurrent = Block(x[0], x[1], x[2]);
                                    blockNeighbor = Block(x[0] + q[0], x[1] + q[1], x[2] + q[2]);
                                    faceDir = d * 2; // 0 (+X), 2 (+Y), 4 (+Z)
                                } else { // Negative face relative to x (e.g. -X face of block at x)
                                    blockCurrent = Block(x[0], x[1], x[2]); // This is the block whose -q face we consider
                                    blockNeighbor = Block(x[0] - q[0], x[1] - q[1], x[2] - q[2]); // Block in -q direction
                                    faceDir = d * 2 + 1; // 1 (-X), 3 (-Y), 5 (-Z)
                                }
                                
                                // We need a face if current is solid and neighbor is air.
                                if (blockCurrent != 0 && blockNeighbor == 0) {
                                    faceTextureMask[x[u] + x[v] * dimU] = GetTextureForFace(blockCurrent, faceDir);
                                }
                            }
                        }
                        faceCheckTime += Time.realtimeSinceStartup - faceCheckStartTime;

                        // 2. Perform greedy meshing on faceTextureMask
                        float vertexGenStartTime = Time.realtimeSinceStartup;
                        for (int vIdx = 0; vIdx < dimV; vIdx++) { // Iterate over rows (v-coordinate)
                            for (int uIdx = 0; uIdx < dimU;) { // Iterate over columns (u-coordinate)
                                Vector2 currentTexture = faceTextureMask[uIdx + vIdx * dimU];
                                if (currentTexture.x < 0) { // Sentinel for no face / already processed
                                    uIdx++;
                                    continue;
                                }

                                // Find width of quad
                                int width = 1;
                                while (uIdx + width < dimU && faceTextureMask[(uIdx + width) + vIdx * dimU] == currentTexture) {
                                    width++;
                                }

                                // Find height of quad
                                int height = 1;
                                bool canExpandHeight = true;
                                while (vIdx + height < dimV && canExpandHeight) {
                                    for (int k = 0; k < width; k++) { // Check all cells in the next row segment
                                        if (faceTextureMask[(uIdx + k) + (vIdx + height) * dimU] != currentTexture) {
                                            canExpandHeight = false;
                                            break;
                                        }
                                    }
                                    if (canExpandHeight) height++;
                                }

                                // 3. Add quad vertices and UVs
                                // Define quad corners based on x, uIdx, vIdx, width, height, d, side
                                Vector3 v0 = Vector3.zero, v1 = Vector3.zero, v2 = Vector3.zero, v3 = Vector3.zero;
                                float planeCoord = x[d]; // The slice index along axis d

                                // These are local coordinates for the quad within the slice
                                float u_start = uIdx;
                                float v_start = vIdx;
                                float u_end = uIdx + width;
                                float v_end = vIdx + height;

                                // Map d, u, v and side to actual X,Y,Z coordinates and winding order
                                // Based on old Cube functions: Y coord is actual face level.
                                // Example: +Y face (d=1, side=0, q=(0,1,0)), planeCoord = y_level_of_block
                                // Face is at y_level_of_block + 1.
                                // u,v are X,Z. Quad is (X, planeCoord+1, Z)
                                // v0 = (u_start, planeCoord+1, v_start)
                                // v1 = (u_end, planeCoord+1, v_start)
                                // v2 = (u_end, planeCoord+1, v_end)
                                // v3 = (u_start, planeCoord+1, v_end)
                                
                                // Simplified generic vertex setup (needs careful per-axis/side adjustment)
                                // This part is complex and needs to be exact for each of the 6 face orientations.

                                // For +X face (d=0, side=0, q=(1,0,0)). Plane coord is x_slice. Face is at x_slice+1 (X value).
                                // u (width) maps to Y, v (height) maps to Z for the face.
                                // Vertices are (X, Y, Z)
                                // v0_local = (u_start, v_start), v1_local = (u_end, v_start), ... on the U-V plane of the slice

                                // The planeCoord is the fixed coordinate for the axis d.
                                // Example: +Y face (Top face). d=1 (Y-axis), side=0. Normal is (0,1,0).
                                // x[d] (which is y_block_coord) is the coordinate of the block itself.
                                // The face is at y_block_coord + 1.
                                // u is X, v is Z. u_start is x_local_start, v_start is z_local_start.
                                // Quad vertices (bottom-left, bottom-right, top-right, top-left on the XZ plane at Y = y_block_coord+1):
                                // v0 = (u_start, planeCoord + 1, v_start)  -- This was the previous thinking, let's use the AddFaceVertices convention.
                                // AddFaceVertices takes them in order: (v_bl, v_br, v_tr, v_tl) for a CCW face. 

                                // x is the current base coordinate being scanned by the loops for d, u, v.
                                // uIdx, vIdx are offsets on the current slice plane (defined by u,v axes).
                                // width, height are extents on that plane.
                                
                                // Global chunk coordinates for the start of the quad on the slice plane
                                float gu = x[u] + uIdx;
                                float gv = x[v] + vIdx;

                                // Define the 4 corner vertices of the quad in world space
                                // These need to map correctly based on d (axis) and side (direction)
                                Vector3 p0 = Vector3.zero, p1 = Vector3.zero, p2 = Vector3.zero, p3 = Vector3.zero;
                                
                                // du and dv are vectors along the U and V axes of the slice plane, scaled by quad width/height
                                Vector3 du_vec = Vector3.zero;
                                Vector3 dv_vec = Vector3.zero;
                                du_vec[u] = width;
                                dv_vec[v] = height;

                                // Base point for the quad (corner x[d], x[u]+uIdx, x[v]+vIdx)
                                Vector3 base_pos = new Vector3(x[0], x[1], x[2]); // This x already incorporates slice and u,v iterators
                                // Adjust base_pos to be the actual corner of the quad for uIdx, vIdx
                                base_pos[u] = uIdx; // These should be global chunk coords
                                base_pos[v] = vIdx; // So, use x[u_original_loop] + uIdx, x[v_original_loop] + vIdx.
                                                // x is reset per slice, so x[u] and x[v] are 0 at start of slice's greedy pass.
                                                // So uIdx, vIdx are direct coords on slice.
                                                // We need to map slice u,v back to world X,Y,Z using d,u,v.

                                float fixedCoord = x[d]; // This is the coordinate of the current slice along axis d.

                                if (side == 0) { // Positive face (+q direction)
                                    // p0 is the origin of the quad on the slice plane
                                    p0[d] = fixedCoord + 1; p0[u] = uIdx;       p0[v] = vIdx;
                                    p1[d] = fixedCoord + 1; p1[u] = uIdx + width; p1[v] = vIdx;
                                    p2[d] = fixedCoord + 1; p2[u] = uIdx + width; p2[v] = vIdx + height;
                                    p3[d] = fixedCoord + 1; p3[u] = uIdx;       p3[v] = vIdx + height;
                                    // Winding for +q normal: p0,p1,p2,p3 (if u,v correspond to right,up on face)
                                    // Need to ensure this matches AddFaceVertices: (v_bl, v_br, v_tr, v_tl)
                                    // If d=X, u=Y, v=Z: (+X face). p0=(fixed+1, uIdx, vIdx), p1=(fixed+1, uIdx+w, vIdx), p2=(fixed+1, uIdx+w, vIdx+h), p3=(fixed+1, uIdx, vIdx+h)
                                    // This is (X,Y,Z). If Y is right, Z is up. Then p0,p1,p2,p3 is BL,BR,TR,TL.
                                    AddFaceVertices(p0, p1, p2, p3);
                                } else { // Negative face (-q direction)
                                    p0[d] = fixedCoord; p0[u] = uIdx;       p0[v] = vIdx;
                                    p1[d] = fixedCoord; p1[u] = uIdx + width; p1[v] = vIdx;
                                    p2[d] = fixedCoord; p2[u] = uIdx + width; p2[v] = vIdx + height;
                                    p3[d] = fixedCoord; p3[u] = uIdx;       p3[v] = vIdx + height;
                                    // Winding for -q normal: p0,p3,p2,p1 (reverse of above)
                                    AddFaceVertices(p0, p3, p2, p1);
                                }

                                AddQuadUVs_Standard();
                                totalFacesGenerated++;

                                // Clear mask for processed quad to avoid reprocessing
                                for (int h = 0; h < height; h++) {
                                    for (int w = 0; w < width; w++) {
                                        faceTextureMask[(uIdx + w) + (vIdx + h) * dimU] = noFaceSentinel;
                                    }
                                }
                                uIdx += width; // Move past the processed quad in u-direction
                            }
                        }
                        vertexGenTime += Time.realtimeSinceStartup - vertexGenStartTime;
                    }
                }
            }
            blockCheckTime = Time.realtimeSinceStartup - blockProcessingStart; // Renamed from blockCheckTime to blockProcessingStart
            
            float collisionStart = Time.realtimeSinceStartup;
            GenerateGreedyCollisionMesh();
            
            float finalizeStart = Time.realtimeSinceStartup;
            UpdateMesh();
            meshFinalizationTime = Time.realtimeSinceStartup - finalizeStart;
        } else {
            // Empty chunk, just clear everything
            mesh.Clear();
            col.sharedMesh = null;
        }
        
        totalTime = Time.realtimeSinceStartup - startTime;
        
        Debug.Log($"[CHUNK PROFILING] Chunk {chunkX},{chunkY},{chunkZ} generation complete:\n" +
                  $"Total time: {totalTime*1000:F2}ms\n" +
                  $"Block caching: {blockCacheTime*1000:F2}ms\n" +
                  $"Block scanning: {blockScanTime*1000:F2}ms\n" +
                  $"Mesh generation:\n" +
                  $"- Block checks ({totalBlockChecks}): {blockCheckTime*1000:F2}ms\n" +
                  $"- Face checks: {faceCheckTime*1000:F2}ms\n" +
                  $"- Vertex gen ({totalFacesGenerated} faces): {vertexGenTime*1000:F2}ms\n" +
                  $"- UV gen: {uvGenTime*1000:F2}ms\n" +
                  $"Collision mesh:\n" +
                  $"- Mask generation: {collisionMaskTime*1000:F2}ms\n" +
                  $"- Greedy meshing: {collisionGreedyTime*1000:F2}ms\n" +
                  $"- Vertex gen ({totalCollisionFaces} faces): {collisionVertexTime*1000:F2}ms\n" +
                  $"Finalization:\n" +
                  $"- Mesh: {meshFinalizationTime*1000:F2}ms\n" +
                  $"- Mesh normals: {meshNormalsTime*1000:F2}ms\n" +
                  $"- Collision normals: {collisionNormalsTime*1000:F2}ms");

        isCacheValid = false;
    }

    void UpdateMesh()
    {
        mesh.Clear();

        if(triangleCount > 0)
        {
            // Create arrays of exact size needed
            Vector3[] finalVertices = new Vector3[vertexCount];
            int[] finalTriangles = new int[triangleCount];
            Vector2[] finalUVs = new Vector2[uvCount];

            // Copy only the used portion of the pools
            System.Array.Copy(vertexPool, finalVertices, vertexCount);
            System.Array.Copy(trianglePool, finalTriangles, triangleCount);
            System.Array.Copy(uvPool, finalUVs, uvCount);

            // Set mesh data in one batch
            mesh.vertices = finalVertices;
            mesh.triangles = finalTriangles;
            mesh.uv = finalUVs;
            
            float normalsStart = Time.realtimeSinceStartup;
            mesh.RecalculateNormals();
            meshNormalsTime = Time.realtimeSinceStartup - normalsStart;
        }
        else
        {
            col.sharedMesh = null;
        }
    }

    private void GenerateGreedyCollisionMesh()
    {
        if(template) return;

        collisionVertexCount = 0;
        collisionTriangleCount = 0;

        // Pre-calculate chunk size values
        int maxSize = Mathf.Max(chunkSizeXZ, chunkSizeY);
        int maskSize = maxSize * maxSize;
        
        // Initialize mask only once
        if (collisionMask == null || collisionMask.Length < maskSize) {
            collisionMask = new bool[maskSize];
        }

        // Iterate through each axis (X=0, Y=1, Z=2)
        for (int d = 0; d < 3; d++)
        {
            int u = (d + 1) % 3;
            int v = (d + 2) % 3;
            
            // Pre-calculate axis-specific values
            int maxD = d == 1 ? chunkSizeY : chunkSizeXZ;
            int maxU = u == 1 ? chunkSizeY : chunkSizeXZ;
            int maxV = v == 1 ? chunkSizeY : chunkSizeXZ;
            
            // Cache coordinate arrays
            int[] x = new int[3];
            int[] q = new int[3];
            q[d] = 1;

            // Pre-calculate offsets for the current axis
            Vector3 offset1 = Vector3.zero;
            Vector3 offset2 = Vector3.zero;
            if (u == 0) offset1.x = 1;
            else if (u == 1) offset1.y = 1;
            else offset1.z = 1;
            if (v == 0) offset2.x = 1;
            else if (v == 1) offset2.y = 1;
            else offset2.z = 1;

            // Sweep over each slice in this axis
            for (x[d] = -1; x[d] < maxD;)
            {
                float maskStart = Time.realtimeSinceStartup;
                
                // Fast array clear using a single loop
                int maskLength = maxU * maxV;
                for (int i = 0; i < maskLength; i++) {
                    collisionMask[i] = false;
                }

                // Generate mask for this slice - marks where faces should be generated
                for (int sectionV = 0; sectionV < maxV; sectionV += SECTION_SIZE)
                {
                    for (int sectionU = 0; sectionU < maxU; sectionU += SECTION_SIZE)
                    {
                        // Check if this section needs processing
                        int sectionX = d == 0 ? x[d] / SECTION_SIZE : (u == 0 ? sectionU / SECTION_SIZE : sectionV / SECTION_SIZE);
                        int sectionY = d == 1 ? x[d] / SECTION_SIZE : (u == 1 ? sectionU / SECTION_SIZE : sectionV / SECTION_SIZE);
                        int sectionZ = d == 2 ? x[d] / SECTION_SIZE : (u == 2 ? sectionU / SECTION_SIZE : sectionV / SECTION_SIZE);
                        
                        if (sectionX >= 0 && sectionX < sectionsPerAxis && 
                            sectionY >= 0 && sectionY < sectionsPerAxis && 
                            sectionZ >= 0 && sectionZ < sectionsPerAxis)
                        {
                            int sectionIdx = sectionX + (sectionY * sectionsPerAxis) + (sectionZ * sectionsPerAxis * sectionsPerAxis);
                            if (!sectionMask[sectionIdx]) continue;
                        }

                        // Process blocks in this section
                        int endV = Mathf.Min(sectionV + SECTION_SIZE, maxV);
                        int endU = Mathf.Min(sectionU + SECTION_SIZE, maxU);
                        for (x[v] = sectionV; x[v] < endV; x[v]++)
                        {
                            for (x[u] = sectionU; x[u] < endU; x[u]++)
                            {
                                // Check if we need a face between current voxel and next voxel
                                bool blockCurrent = x[d] >= 0 && Block(x[0], x[1], x[2]) != 0;
                                bool blockCompare = x[d] < maxD - 1 && Block(x[0] + q[0], x[1] + q[1], x[2] + q[2]) != 0;
                                
                                // Only set mask if we have a transition between solid and air
                                if (blockCurrent != blockCompare) {
                                    collisionMask[x[u] + x[v] * maxU] = true;
                                }
                            }
                        }
                    }
                }
                collisionMaskTime += Time.realtimeSinceStartup - maskStart;

                x[d]++;

                float greedyStart = Time.realtimeSinceStartup;

                // Greedy meshing algorithm - merge quads in both U and V directions
                for (int vIdx = 0; vIdx < maxV; vIdx++)
                {
                    for (int uIdx = 0; uIdx < maxU;)
                    {
                        int idx = uIdx + vIdx * maxU;
                        if (!collisionMask[idx]) {
                            uIdx++;
                            continue;
                        }

                        // Find max width - how far we can expand in U direction
                        int width = 1;
                        while (uIdx + width < maxU && collisionMask[idx + width] &&
                               Block(x[0], x[1], x[2]) == Block(x[0] + width * q[0], x[1] + width * q[1], x[2] + width * q[2])) {
                            width++;
                        }

                        // Find max height - how far we can expand in V direction
                        int height = 1;
                        bool canExpand = true;
                        while (vIdx + height < maxV && canExpand)
                        {
                            // Check if we can add another row
                            for (int k = 0; k < width; k++)
                            {
                                int nextIdx = (uIdx + k) + (vIdx + height) * maxU;
                                if (!collisionMask[nextIdx] ||
                                    Block(x[0], x[1], x[2]) != Block(x[0] + k * q[0], x[1] + k * q[1], x[2] + k * q[2])) {
                                    canExpand = false;
                                        break;
                                }
                            }
                            if (canExpand) height++;
                        }

                        float vertexStart = Time.realtimeSinceStartup;

                        // Generate vertices for this quad
                        x[u] = uIdx;
                        x[v] = vIdx;
                        
                        // Create base vertex with correct Y position (subtract 1 to match visual mesh)
                        Vector3 v0 = new Vector3(x[0], x[1] - 1, x[2]);
                        
                        // Add vertices using pre-calculated offsets
                        collisionVertexPool[collisionVertexCount] = v0;
                        collisionVertexPool[collisionVertexCount + 1] = v0 + offset1 * width;
                        collisionVertexPool[collisionVertexCount + 2] = v0 + offset2 * height;
                        collisionVertexPool[collisionVertexCount + 3] = v0 + offset1 * width + offset2 * height;

                        // Add triangles with correct winding order based on face direction
                        bool isBackFace = x[d] >= 0 && Block(x[0] - q[0], x[1] - q[1], x[2] - q[2]) == 0;
                        int triIdx = collisionTriangleCount;
                            if (isBackFace)
                            {
                            collisionTrianglePool[triIdx] = collisionVertexCount;
                            collisionTrianglePool[triIdx + 1] = collisionVertexCount + 2;
                            collisionTrianglePool[triIdx + 2] = collisionVertexCount + 1;
                            collisionTrianglePool[triIdx + 3] = collisionVertexCount + 1;
                            collisionTrianglePool[triIdx + 4] = collisionVertexCount + 2;
                            collisionTrianglePool[triIdx + 5] = collisionVertexCount + 3;
                        }
                        else
                        {
                            collisionTrianglePool[triIdx] = collisionVertexCount;
                            collisionTrianglePool[triIdx + 1] = collisionVertexCount + 1;
                            collisionTrianglePool[triIdx + 2] = collisionVertexCount + 2;
                            collisionTrianglePool[triIdx + 3] = collisionVertexCount + 1;
                            collisionTrianglePool[triIdx + 4] = collisionVertexCount + 3;
                            collisionTrianglePool[triIdx + 5] = collisionVertexCount + 2;
                        }

                        collisionVertexCount += 4;
                        collisionTriangleCount += 6;
                        totalCollisionFaces++;
                        collisionVertexTime += Time.realtimeSinceStartup - vertexStart;

                        // Clear the mask for the quad we just generated
                        for (int h = 0; h < height; h++) {
                            for (int w = 0; w < width; w++) {
                                collisionMask[(uIdx + w) + (vIdx + h) * maxU] = false;
                            }
                        }

                        // Skip past the quad we just processed
                        uIdx += width;
                    }
                }
                collisionGreedyTime += Time.realtimeSinceStartup - greedyStart;
            }
        }

        // Create final mesh only if we have vertices
        if (collisionVertexCount > 0)
        {
            Vector3[] finalVertices = new Vector3[collisionVertexCount];
            int[] finalTriangles = new int[collisionTriangleCount];
            System.Array.Copy(collisionVertexPool, finalVertices, collisionVertexCount);
            System.Array.Copy(collisionTrianglePool, finalTriangles, collisionTriangleCount);

        collisionMesh.Clear();
            collisionMesh.vertices = finalVertices;
            collisionMesh.triangles = finalTriangles;
            
            float normalsStart = Time.realtimeSinceStartup;
        collisionMesh.RecalculateNormals();
            collisionNormalsTime = Time.realtimeSinceStartup - normalsStart;

        col.sharedMesh = null;
        col.sharedMesh = collisionMesh;
    }
        else
        {
            col.sharedMesh = null;
        }
    }
}
