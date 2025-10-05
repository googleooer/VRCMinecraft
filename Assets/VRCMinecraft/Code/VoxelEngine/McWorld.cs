#define LOGGING

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRRefAssist;

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McWorld : UdonSharpBehaviour
{
    [Header("World Configuration")]
    public string worldSeedString = "DefaultSeed";
    public int worldDimensionX = 4;
    public int worldDimensionY = 2;
    public int worldDimensionZ = 4;
    public int chunkSizeXZ = 16;
    public int chunkSizeY = 16;

    [Header("Chunk Management")]
    [Tooltip("Prefab for chunks. Must have 3 child objects named 'Opaque', 'Transparent', 'Cutout' with MeshFilters, and a root MeshCollider.")]
    public GameObject chunkPrefab;
    public ChunkData[] chunks_1D; // OPTIMIZED: Public for direct coordinator access

    [Header("Performance Tuning")]
    [Tooltip("How many voxels to check for meshing per step inside a chunk. Higher values build meshes faster but may cause lag spikes.")]
    public int voxelsPerMeshStep = 2048;
    [Tooltip("Approximate time budget per mesh step in milliseconds. OPTIMIZED: Increased from 0.5ms to 1.5ms due to mesh loop optimizations.")]
    public float meshStepTimeBudgetMs = 1.5f;
    [Tooltip("Time budget per Update() call in milliseconds to prevent frame drops.")]
    public float updateTimeBudgetMs = 12.0f;

    [Header("System References")]
    [SerializeField, FindObjectOfType(true)]
    public McTerrainGenerator terrainGenerator;
    [SerializeField, FindObjectOfType(true)]
    public McBlockTypeManager blockTypeManager;
    [SerializeField, FindObjectOfType(true)]
    private McCoordinator coordinator;
    
    [Header("Debugging")]
    public bool enableVerboseLogging = false;
    public bool enableGenerationTimings = false;
    
    // --- Private World State ---
    private int totalWorldChunks;
    [HideInInspector] public int chunkOffsetX;
    [HideInInspector] public int chunkOffsetY;
    [HideInInspector] public int chunkOffsetZ;
    [HideInInspector] public int globalVoxelOffsetX;
    [HideInInspector] public int globalVoxelOffsetY;
    [HideInInspector] public int globalVoxelOffsetZ;
    private ushort[] blockDataCache;
    private int[] uv_allFacesCache;
    private int[] uv_topFaceCache;
    private int[] uv_bottomFaceCache;
    private int[] uv_sideFacesCache;
    private BlockVisibilityType[] visibilityCache; // per blockID
    private BlockCullingType[] cullingCache; // per blockID
    private byte[] shouldDrawTable; // 256x256 lookup: self<<8 | neighbor => 0/1

    // --- Active Processing Queues ---
    private const int MAX_ACTIVE_CHUNKS = 16;
    private ChunkData[] activeDataGenChunks;
    private int activeDataGenCount = 0;
    private ChunkData[] activeMeshingChunks;
    private int activeMeshingCount = 0;
    private const int COLLIDER_DEFER_FRAMES = 2;

    // --- Meshing Constants & Buffers (could be pooled) ---
    private const int MAX_VERTS = 12288;
    private const int MAX_TRIS = (MAX_VERTS / 4) * 6;
    private readonly Vector3[] FaceVertices_North = { new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 1) };
    private readonly Vector3[] FaceVertices_East = { new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1) };
    private readonly Vector3[] FaceVertices_South = { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) };
    private readonly Vector3[] FaceVertices_West = { new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0), new Vector3(0, 0, 0) };
    private readonly Vector3[] FaceVertices_Up = { new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0) };
    private readonly Vector3[] FaceVertices_Down = { new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 0) };
    private readonly Vector3 Normal_North = Vector3.forward; private readonly Vector3 Normal_East = Vector3.right; private readonly Vector3 Normal_South = Vector3.back;
    private readonly Vector3 Normal_West = Vector3.left; private readonly Vector3 Normal_Up = Vector3.up; private readonly Vector3 Normal_Down = Vector3.down;
    private const int FACE_INDEX_SIDE = 0; private const int FACE_INDEX_TOP = 2; private const int FACE_INDEX_BOTTOM = 3;

    // --- RLE Constants ---
    private const ushort RLE_TYPE_1D = 0;
    private const ushort RLE_TYPE_2D_XZ_PLANE = 1;
    private const ushort RLE_TYPE_3D_FULL_CHUNK = 2;

    // --- Bitwise Constants for fast coordinate math (chunk size = 16) ---
    private const int CHUNK_SIZE_SHIFT = 4;
    private const int CHUNK_SIZE_MASK = 15; // 16 - 1
    
    // --- Neighbor Offsets ---
    private readonly int[] neighbor_dx_offsets = { 1, -1, 0,  0, 0,  0 };
    private readonly int[] neighbor_dy_offsets = { 0,  0, 1, -1, 0,  0 };
    private readonly int[] neighbor_dz_offsets = { 0,  0, 0,  0, 1, -1 };

#if LOGGING
    private System.Text.StringBuilder logBuilder;
#endif

    void Start()
    {
        Debug.Log("[McWorld] ========== MCWORLD START =========="); 
        if (chunkPrefab == null || terrainGenerator == null || blockTypeManager == null || coordinator == null) {
            Debug.LogError("[McWorld] A critical component is not assigned! Aborting.");
            this.enabled = false;
            return;
        }

#if LOGGING
        logBuilder = new System.Text.StringBuilder(2048);
#endif
        
        InitializeWorldParameters();
        InitializeChunkStorage();
        
        blockDataCache = blockTypeManager.finalDataArray;
        uv_allFacesCache = blockTypeManager.uv_allFacesData;
        uv_topFaceCache = blockTypeManager.uv_topFaceData;
        uv_bottomFaceCache = blockTypeManager.uv_bottomFaceData;
        uv_sideFacesCache = blockTypeManager.uv_sideFacesData;
        _BuildBlockCaches();
        
        terrainGenerator.init(McUtils.GetMinecraftSeed(worldSeedString));
        
        int[] radialChunkOrder = GenerateRadialChunkOrder();
        coordinator.InitializeAndStartProcessing(this, radialChunkOrder, totalWorldChunks);
        
        // No longer using SendCustomEventDelayedSeconds - Update() will handle processing
    }

    void InitializeWorldParameters()
    {
        worldDimensionX = Mathf.Max(1, worldDimensionX);
        worldDimensionY = Mathf.Max(1, worldDimensionY);
        worldDimensionZ = Mathf.Max(1, worldDimensionZ);
        chunkSizeXZ = Mathf.Max(1, chunkSizeXZ);
        chunkSizeY = Mathf.Max(1, chunkSizeY);
        totalWorldChunks = worldDimensionX * worldDimensionY * worldDimensionZ;

        #if LOGGING
        // Print seed converted with McUtils.GetMinecraftSeed
        Debug.Log($"World Seed: {worldSeedString}");
        Debug.Log($"Converted World Seed: {McUtils.GetMinecraftSeed(worldSeedString)}");
        Debug.Log($"Permutation Table: {McUtils.GetPermutationTable(new JavaRandom(McUtils.GetMinecraftSeed(worldSeedString)))}");
        #endif
        
        // --- FIX: Remove vertical (Y-axis) centering ---
        // The world should be centered on XZ, but start at Y=0 and build upwards.
        chunkOffsetX = worldDimensionX / 2;
        chunkOffsetY = 0; 
        chunkOffsetZ = worldDimensionZ / 2;
        
        globalVoxelOffsetX = (worldDimensionX * chunkSizeXZ) / 2;
        globalVoxelOffsetY = 0;
        globalVoxelOffsetZ = (worldDimensionZ * chunkSizeXZ) / 2;
    }

    void InitializeChunkStorage()
    {
        chunks_1D = new ChunkData[totalWorldChunks];
        activeDataGenChunks = new ChunkData[MAX_ACTIVE_CHUNKS];
        activeMeshingChunks = new ChunkData[MAX_ACTIVE_CHUNKS];
    }
    
    public int InstantiateAndConfigureChunk(int array_cx, int array_cy, int array_cz)
    {
        int centered_dx = array_cx - chunkOffsetX;
        int centered_dy = array_cy - chunkOffsetY; 
        int centered_dz = array_cz - chunkOffsetZ;

        int chunk1DIndex = ChunkCenteredCoordsTo1D(centered_dx, centered_dy, centered_dz);
        if (chunk1DIndex == -1 || chunks_1D[chunk1DIndex] != null) return -1;
        
        GameObject newChunkGO = Instantiate(chunkPrefab);
        newChunkGO.name = $"Chunk_({centered_dx},{centered_dy},{centered_dz})";
        newChunkGO.transform.SetParent(this.transform, false);
        newChunkGO.transform.localPosition = new Vector3(centered_dx * chunkSizeXZ, centered_dy * chunkSizeY, centered_dz * chunkSizeXZ);

        ChunkData newChunkData = new ChunkData();
        chunks_1D[chunk1DIndex] = newChunkData;

        // --- Initialize ChunkData object ---
        newChunkData.world = this;
        newChunkData.chunkX_world = centered_dx;
        newChunkData.chunkY_world = centered_dy;
        newChunkData.chunkZ_world = centered_dz;
        newChunkData.gameObject = newChunkGO;
        
        // --- Assign Component References ---
        // This is a bit fragile, relies on child object names. A more robust solution
        // would be a simple script on the prefab to hold the references.
        newChunkData.opaqueMeshFilter = newChunkGO.transform.Find("Opaque").GetComponent<MeshFilter>();
        newChunkData.transparentMeshFilter = newChunkGO.transform.Find("Transparent").GetComponent<MeshFilter>();
        newChunkData.cutoutMeshFilter = newChunkGO.transform.Find("Cutout").GetComponent<MeshFilter>();
        newChunkData.meshCollider = newChunkGO.GetComponent<MeshCollider>();
        
        // --- Initialize Buffers & State ---
        newChunkData._chunkDataSize = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
        
        newChunkData._opaqueVertices = new Vector3[MAX_VERTS]; newChunkData._opaqueTriangles = new int[MAX_TRIS]; newChunkData._opaqueUVs = new Vector3[MAX_VERTS]; newChunkData._opaqueNormals = new Vector3[MAX_VERTS];
        newChunkData._transparentVertices = new Vector3[MAX_VERTS]; newChunkData._transparentTriangles = new int[MAX_TRIS]; newChunkData._transparentUVs = new Vector3[MAX_VERTS]; newChunkData._transparentNormals = new Vector3[MAX_VERTS];
        newChunkData._cutoutVertices = new Vector3[MAX_VERTS]; newChunkData._cutoutTriangles = new int[MAX_TRIS]; newChunkData._cutoutUVs = new Vector3[MAX_VERTS]; newChunkData._cutoutNormals = new Vector3[MAX_VERTS];
        newChunkData._collisionVertices = new Vector3[MAX_VERTS * 3]; newChunkData._collisionTriangles = new int[MAX_TRIS * 3];
        
        newChunkGO.SetActive(true);

        return chunk1DIndex;
    }

    void Update()
    {
        // Safety check: Don't process if not initialized
        if (chunks_1D == null || terrainGenerator == null) return;
        
        ProcessActiveChunks();
    }
    
    private void ProcessActiveChunks()
    {
        float frameStart = Time.realtimeSinceStartup;
        float frameBudget = updateTimeBudgetMs * 0.001f;
        
        // --- Process Data Generation ---
        for (int i = 0; i < activeDataGenCount; i++)
        {
            if (Time.realtimeSinceStartup - frameStart > frameBudget) break; // Don't exceed budget
            
            ChunkData chunk = activeDataGenChunks[i];
            if (chunk == null || !chunk.isGeneratingData)
            {
                // Remove from active list
                activeDataGenChunks[i] = activeDataGenChunks[activeDataGenCount - 1];
                activeDataGenCount--;
                i--; // Re-check this index
                continue;
            }
            StepChunkDataGeneration(chunk);
        }

        // --- Process Meshing ---
        for (int i = 0; i < activeMeshingCount; i++)
        {
            if (Time.realtimeSinceStartup - frameStart > frameBudget) break; // Don't exceed budget
            
            ChunkData chunk = activeMeshingChunks[i];
            if (chunk == null || !chunk.isBuildingMesh)
            {
                // Remove from active list
                activeMeshingChunks[i] = activeMeshingChunks[activeMeshingCount - 1];
                activeMeshingCount--;
                i--; // Re-check this index
                continue;
            }

            // OPTIMIZATION: Process multiple mesh steps per frame to reduce overhead
            int maxMeshStepsPerFrame = 5;
            for (int step = 0; step < maxMeshStepsPerFrame; step++)
            {
                if (!chunk.isBuildingMesh) break; // Mesh complete
                if (Time.realtimeSinceStartup - frameStart > frameBudget) break; // Don't exceed budget
                _BuildChunkMeshStep(chunk);
            }
        }
        
        // No longer need to reschedule - Update() runs every frame automatically!
    }
    
    // Called by Coordinator
    public void StartChunkDataGeneration(int chunkIndex)
    {
        if (chunkIndex == -1) return;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null || chunk.isGeneratingData) return;
        
        chunk.isGeneratingData = true;
        terrainGenerator.StartChunkGeneration(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);

        if (activeDataGenCount < MAX_ACTIVE_CHUNKS)
        {
            activeDataGenChunks[activeDataGenCount++] = chunk;
        }
    }
    
    // Called by ProcessActiveChunks loop
    private void StepChunkDataGeneration(ChunkData chunk)
    {
        if (!chunk.isGeneratingData) return;
        
        // OPTIMIZATION: Process 1 step per frame for maximum smoothness (120fps VR ready)
        // Each step = ~5-8ms, spread across more frames for buttery smooth generation
        byte[] generatedData = null;
        bool isComplete = false;
        float stepStart = Time.realtimeSinceStartup;
        float stepBudget = 0.008f; // 8ms budget for 120fps
        int maxStepsPerFrame = 1; // Process 1 step per frame for smoothest experience
        
        for (int step = 0; step < maxStepsPerFrame; step++)
        {
            isComplete = terrainGenerator.StepChunkGeneration(out generatedData);
            if (isComplete) break;
            
            // Stop if we've used our budget (prevents stutters)
            if (Time.realtimeSinceStartup - stepStart > stepBudget) break;
        }
        
        if (isComplete)
        {
            bool isHomogeneous;
            chunk._chunkData = _CompressChunkColumnRLE(generatedData, out isHomogeneous);
            
            // OPTIMIZATION: Invalidate decompression cache since we have new data
            chunk._decompCacheValid = false;
            
            chunk.isSingleOpaqueSolid = false;
            if (isHomogeneous) {
                byte blockID = (byte)chunk._chunkData;
                if(_IsBlockSolid(blockID) && _GetVisibilityType(blockID) == BlockVisibilityType.Opaque) {
                    chunk.isSingleOpaqueSolid = true;
                }
            }
                
            chunk.isDataReady = true;
            chunk.isGeneratingData = false;
            TriggerNeighborMeshRebuilds(chunk); // This will queue them in the coordinator
        }
    }

    // Called by Coordinator
    public void RequestChunkMeshUpdate(int chunkIndex)
    {
        if (coordinator != null) coordinator.RequestChunkMeshUpdate(chunkIndex);
    }
    
    public byte GetBlock(int globalX, int globalY, int globalZ)
    {
        // OPTIMIZATION: Use bitwise operations instead of division/modulus
        int centeredChunkX = globalX >> CHUNK_SIZE_SHIFT;
        int chunkY = globalY >> CHUNK_SIZE_SHIFT;
        int centeredChunkZ = globalZ >> CHUNK_SIZE_SHIFT;
        
        ChunkData chunk = GetChunkAt(centeredChunkX, chunkY, centeredChunkZ);
        if (chunk == null || !chunk.isDataReady) return 0;

        int localX = globalX & CHUNK_SIZE_MASK;
        int localY = globalY & CHUNK_SIZE_MASK;
        int localZ = globalZ & CHUNK_SIZE_MASK;
        
        return _GetBlockLocal(chunk, localX, localY, localZ);
    }
    
    public void SetBlock(int globalX, int globalY, int globalZ, byte blockType)
    {
        // OPTIMIZATION: Use bitwise operations instead of division/modulus
        int centeredChunkX = globalX >> CHUNK_SIZE_SHIFT;
        int chunkY = globalY >> CHUNK_SIZE_SHIFT;
        int centeredChunkZ = globalZ >> CHUNK_SIZE_SHIFT;
        
        ChunkData chunk = GetChunkAt(centeredChunkX, chunkY, centeredChunkZ);
        if (chunk == null) return;

        int localX = globalX & CHUNK_SIZE_MASK;
        int localY = globalY & CHUNK_SIZE_MASK;
        int localZ = globalZ & CHUNK_SIZE_MASK;

        _SetBlockLocal(chunk, localX, localY, localZ, blockType, true);
    }
    
    public void TriggerNeighborMeshRebuilds(ChunkData chunk)
    {
        for (int i = 0; i < 6; i++) {
            int neighborX = chunk.chunkX_world + neighbor_dx_offsets[i];
            int neighborY = chunk.chunkY_world + neighbor_dy_offsets[i];
            int neighborZ = chunk.chunkZ_world + neighbor_dz_offsets[i];
            int neighborIndex = ChunkCenteredCoordsTo1D(neighborX, neighborY, neighborZ);
            
            if (neighborIndex != -1 && GetChunkAt(neighborX, neighborY, neighborZ) != null)
            {
                 RequestChunkMeshUpdate(neighborIndex);
            }
        }
    }
    
    public ChunkData GetChunkAt(int centered_cx, int cy, int centered_cz)
    {
        int index = ChunkCenteredCoordsTo1D(centered_cx, cy, centered_cz);
        if (index == -1 || chunks_1D == null || index >= chunks_1D.Length) return null;
        return chunks_1D[index];
    }
    
    private int ChunkArrayCoordsTo1D(int arrayX, int arrayY, int arrayZ)
    {
        if (arrayX < 0 || arrayX >= worldDimensionX || arrayY < 0 || arrayY >= worldDimensionY || arrayZ < 0 || arrayZ >= worldDimensionZ) return -1;
        return (arrayZ * worldDimensionX * worldDimensionY) + (arrayY * worldDimensionX) + arrayX;
    }

    public int ChunkCenteredCoordsTo1D(int centeredX, int centeredY, int centeredZ)
    {
        // Y is no longer "centered", it's an absolute index.
        return ChunkArrayCoordsTo1D(centeredX + chunkOffsetX, centeredY + chunkOffsetY, centeredZ + chunkOffsetZ);
    }
    
    public void Chunk1DToArrrayCoords(int index, out int x, out int y, out int z)
    {
        z = index / (worldDimensionX * worldDimensionY);
        y = (index / worldDimensionX) % worldDimensionY;
        x = index % worldDimensionX;
    }

    private int[] GenerateRadialChunkOrder()
    {
        int[] radialOrder = new int[totalWorldChunks];
        bool[] chunkAdded = new bool[totalWorldChunks];
        int count = 0;
        int maxRadius = Mathf.Max(worldDimensionX / 2, worldDimensionZ / 2) + 1;

        #if LOGGING
        Debug.Log($"[McWorld] Generating chunk order: full columns, top-to-bottom, radially.");
        #endif

        for (int r = 0; r < maxRadius && count < totalWorldChunks; r++) {
            for (int z = -r; z <= r; z++) {
                for (int x = -r; x <= r; x++) {
                    if (Mathf.Abs(x) == r || Mathf.Abs(z) == r) {
                        int arrayX = x + chunkOffsetX;
                        int arrayZ = z + chunkOffsetZ;

                        if (arrayX < 0 || arrayX >= worldDimensionX || arrayZ < 0 || arrayZ >= worldDimensionZ) {
                            continue;
                        }

                        for (int y = worldDimensionY - 1; y >= 0; y--) {
                            int chunkIndex = ChunkArrayCoordsTo1D(arrayX, y, arrayZ);
                            if (chunkIndex != -1 && !chunkAdded[chunkIndex]) {
                                radialOrder[count++] = chunkIndex;
                                chunkAdded[chunkIndex] = true;
                            }
                        }
                    }
                }
            }
        }
        return radialOrder;
    }

    #region State Query Methods for Coordinator

    public bool IsChunkDataReady(int chunkIndex)
    {
        if (chunkIndex == -1) return false;
        ChunkData c = chunks_1D[chunkIndex];
        return c != null && c.isDataReady;
    }

    // OPTIMIZED: Direct chunk data access for coordinator (avoids method call overhead)
    public ChunkData GetChunkDataDirect(int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= chunks_1D.Length) return null;
        return chunks_1D[chunkIndex];
    }
    
    public bool IsChunkGeneratingData(int chunkIndex)
    {
        if (chunkIndex == -1) return false;
        ChunkData c = chunks_1D[chunkIndex];
        return c != null && c.isGeneratingData;
    }
    
    public bool IsChunkBuildingMesh(int chunkIndex)
    {
        if (chunkIndex == -1) return false;
        ChunkData c = chunks_1D[chunkIndex];
        return c != null && c.isBuildingMesh;
    }

    public bool AreAllNeighborsReady(int chunkIndex)
    {
        if (chunkIndex == -1) return false;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null) return false;
        
        for (int i = 0; i < 6; i++)
        {
            int n_cx = chunk.chunkX_world + neighbor_dx_offsets[i];
            int n_cy = chunk.chunkY_world + neighbor_dy_offsets[i];
            int n_cz = chunk.chunkZ_world + neighbor_dz_offsets[i];

            int neighbor_1D_index = ChunkCenteredCoordsTo1D(n_cx, n_cy, n_cz);
            if (neighbor_1D_index == -1) continue; 

            ChunkData neighborChunk = GetChunkAt(n_cx, n_cy, n_cz);

            if (neighborChunk != null && !neighborChunk.isDataReady) return false;
        }
        return true;
    }

    public bool IsChunkSingleOpaqueSolid(int centered_cx, int cy, int centered_cz)
    {
        ChunkData chunk = GetChunkAt(centered_cx, cy, centered_cz);
        if (chunk == null) return false; 
        return chunk.isSingleOpaqueSolid;
    }

    #endregion

    #region Mesh Building Logic (Moved from McChunk)

    public void BuildChunkMesh(int chunkIndex)
    {
        if (chunkIndex == -1) return;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null || chunk.isBuildingMesh || chunk._chunkData == null) return;
        
        chunk.isBuildingMesh = true;

#if LOGGING
        if (enableVerboseLogging)
        {
            logBuilder.Clear();
            logBuilder.AppendLine($"--- BuildMesh for Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) ---");
            chunk.timer_start_stage = Time.realtimeSinceStartup;
            chunk.time_MainLoop = 0;
            chunk.mesh_step_count = 0;
        }
#endif

        _ClearAllMeshBuffers(chunk);

#if LOGGING
        if (enableVerboseLogging)
        {
            // Reset extended profiling counters
            chunk.time_SentinelEnsure = 0f; chunk.time_DecompressNeighbors = 0f; chunk.time_SentinelBuild = 0f;
            chunk.time_AxisY = 0f; chunk.time_AxisZ = 0f; chunk.time_AxisX = 0f;
            chunk.boundaryChecksY = 0; chunk.boundaryChecksZ = 0; chunk.boundaryChecksX = 0;
            chunk.shouldDrawTests = 0; chunk.shouldDrawTrue = 0;
            chunk.facesOpaque = 0; chunk.facesTransparent = 0; chunk.facesCutout = 0; chunk.facesTotal = 0;
            chunk.sentinelInteriorCopied = 0; chunk.sentinelBorderCopied = 0;
        }
#endif

        if (chunk.isSingleOpaqueSolid)
        {
            bool isFullyOccluded = true;
            for (int i = 0; i < 6; i++)
            {
                if (!IsChunkSingleOpaqueSolid(chunk.chunkX_world + neighbor_dx_offsets[i], chunk.chunkY_world + neighbor_dy_offsets[i], chunk.chunkZ_world + neighbor_dz_offsets[i]))
                {
                    isFullyOccluded = false; break;
                }
            }
            if (isFullyOccluded) { _ApplyEmptyMesh(chunk); chunk.isBuildingMesh = false; return; }
        }

        // --- OPTIMIZATION: Fetch neighbor references once ---
        chunk.neighborPX = GetChunkAt(chunk.chunkX_world + 1, chunk.chunkY_world, chunk.chunkZ_world);
        chunk.neighborNX = GetChunkAt(chunk.chunkX_world - 1, chunk.chunkY_world, chunk.chunkZ_world);
        chunk.neighborPY = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world + 1, chunk.chunkZ_world);
        chunk.neighborNY = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world - 1, chunk.chunkZ_world);
        chunk.neighborPZ = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world + 1);
        chunk.neighborNZ = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world - 1);


#if LOGGING
        if (enableVerboseLogging)
        {
            chunk.time_NeighborCache = (Time.realtimeSinceStartup - chunk.timer_start_stage) * 1000f;
            chunk.timer_start_stage = Time.realtimeSinceStartup;
        }
#endif

#if LOGGING
        if (enableVerboseLogging)
        {
            // Will be replaced by detailed breakdown, but keep timer for total DataPrep
            chunk.time_DataPrep = 0f;
        }
#endif

        chunk._greedyAxis = 0;
        chunk._greedyU = 0;
        chunk._greedyV = 0;

        // Build sentinel occupancy buffer once per mesh build with detailed timings
#if LOGGING
        float t_prepare = Time.realtimeSinceStartup;
#endif
        _EnsureSentinelBuffer(chunk);
#if LOGGING
        if (enableVerboseLogging) chunk.time_SentinelEnsure += (Time.realtimeSinceStartup - t_prepare) * 1000f;
#endif

#if LOGGING
        if (enableVerboseLogging) t_prepare = Time.realtimeSinceStartup;
#endif
        _DecompressNeighborsOnce(chunk);
#if LOGGING
        if (enableVerboseLogging) chunk.time_DecompressNeighbors += (Time.realtimeSinceStartup - t_prepare) * 1000f;
#endif

#if LOGGING
        if (enableVerboseLogging) t_prepare = Time.realtimeSinceStartup;
#endif
        _BuildSentinel(chunk);
#if LOGGING
        if (enableVerboseLogging)
        {
            chunk.time_SentinelBuild += (Time.realtimeSinceStartup - t_prepare) * 1000f;
            chunk.time_DataPrep = chunk.time_SentinelEnsure + chunk.time_DecompressNeighbors + chunk.time_SentinelBuild;
        }
#endif
        
        if (activeMeshingCount < MAX_ACTIVE_CHUNKS)
        {
            activeMeshingChunks[activeMeshingCount++] = chunk;
        }
    }

    private void _BuildChunkMeshStep(ChunkData chunk)
    {
        if (!chunk.isBuildingMesh) return;
        chunk._lastMeshStepFrame = Time.frameCount;

#if LOGGING
        float timer_start_stage = 0f;
        if (enableVerboseLogging)
        {
            timer_start_stage = Time.realtimeSinceStartup;
            chunk.mesh_step_count++;
        }
#endif

        // Time-budgeted, boundary-mask meshing using sentinel occupancy
        float budgetStart = Time.realtimeSinceStartup;
        float budgetSec = meshStepTimeBudgetMs * 0.001f;

        if (!chunk._sentinelReady)
        {
#if LOGGING
            float t0 = 0f;
            if (enableVerboseLogging) t0 = Time.realtimeSinceStartup;
#endif
            _EnsureSentinelBuffer(chunk);
#if LOGGING
            if (enableVerboseLogging) chunk.time_SentinelEnsure += (Time.realtimeSinceStartup - t0) * 1000f;
            if (enableVerboseLogging) t0 = Time.realtimeSinceStartup;
#endif
            _BuildSentinel(chunk);
#if LOGGING
            if (enableVerboseLogging) chunk.time_SentinelBuild += (Time.realtimeSinceStartup - t0) * 1000f;
#endif
        }

        int SX = chunk._sentinelSX;
        int SY = chunk._sentinelSY;
        int SZ = chunk._sentinelSZ;
        
        // Pre-calculate strides for faster index calculation
        int strideY = SX * (chunkSizeXZ + 2);
        int strideZ = SX;
        
        // Cache arrays locally for maximum performance
        byte[] sentinel = chunk._sentinel;
        byte[] drawTable = shouldDrawTable;
        int drawTableLen = drawTable != null ? drawTable.Length : 0;
        
        // Pre-calculate validity ranges to eliminate redundant checks
        int maxSY = SY - 3;
        int maxSZ = SZ - 3;
        int maxSX = SX - 3;

        while (chunk._greedyAxis <= 2)
        {
            if (Time.realtimeSinceStartup - budgetStart > budgetSec) break;

            if (chunk._greedyAxis == 0)
            {
#if LOGGING
                float axisStart = 0f; if (enableVerboseLogging) axisStart = Time.realtimeSinceStartup;
#endif
                // Y-axis faces (Up/Down), iterate a column along Y for fixed (sx, sz)
                int sx = 1 + chunk._greedyU; // 1..SX-2
                int sz = 1 + chunk._greedyV; // 1..SZ-2

                // Pre-calculate base index and local coords
                int baseIdx = sz * strideZ + sx;
                int lx = sx - 1;
                int lz = sz - 1;
                
                // Process all Y boundaries for this (sx,sz) - optimized with incremental indexing
                for (int sy = 0; sy < SY - 1; sy++)
                {
                    int idxBelow = baseIdx + sy * strideY;
                    int idxAbove = idxBelow + strideY;
                    byte idBelow = sentinel[idxBelow];
                    byte idAbove = sentinel[idxAbove];
#if LOGGING
                    if (enableVerboseLogging) chunk.boundaryChecksY++;
#endif
                    if (idBelow == idAbove) continue;

                    // Up face of below cell (only if below is interior)
                    if (sy >= 1)
                    {
                        // Fully inlined _ShouldDrawFace for performance
                        int idx = (idBelow << 8) | idAbove;
                        bool drawTest = idx < drawTableLen && drawTable[idx] != 0;
#if LOGGING
                        if (enableVerboseLogging) { chunk.shouldDrawTests++; if (drawTest) chunk.shouldDrawTrue++; }
#endif
                        if (drawTest)
                        {
                            _AddFace(chunk, FaceVertices_Up, Normal_Up, new Vector3(lx, sy - 1, lz), idBelow, _GetVisibilityType(idBelow), FACE_INDEX_TOP);
                        }
                    }
                    // Down face of above cell (only if above is interior)
                    if (sy <= maxSY)
                    {
                        // Fully inlined _ShouldDrawFace for performance
                        int idx2 = (idAbove << 8) | idBelow;
                        bool drawTest2 = idx2 < drawTableLen && drawTable[idx2] != 0;
#if LOGGING
                        if (enableVerboseLogging) { chunk.shouldDrawTests++; if (drawTest2) chunk.shouldDrawTrue++; }
#endif
                        if (drawTest2)
                        {
                            _AddFace(chunk, FaceVertices_Down, Normal_Down, new Vector3(lx, sy, lz), idAbove, _GetVisibilityType(idAbove), FACE_INDEX_BOTTOM);
                        }
                    }
                }

                // advance (u,v)
                chunk._greedyU++;
                int limitU = SX - 2; // interior range length
                int limitV = SZ - 2;
                if (chunk._greedyU >= limitU)
                {
                    chunk._greedyU = 0;
                    chunk._greedyV++;
                    if (chunk._greedyV >= limitV)
                    {
                        chunk._greedyV = 0;
                        chunk._greedyAxis++;
                    }
                }
#if LOGGING
                if (enableVerboseLogging) chunk.time_AxisY += (Time.realtimeSinceStartup - axisStart) * 1000f;
#endif
            }
            else if (chunk._greedyAxis == 1)
            {
#if LOGGING
                float axisStart = 0f; if (enableVerboseLogging) axisStart = Time.realtimeSinceStartup;
#endif
                // Z-axis faces (North/South), iterate a line along Z for fixed (sx, sy)
                int sx = 1 + chunk._greedyU; // 1..SX-2
                int sy = 1 + chunk._greedyV; // 1..SY-2

                // Pre-calculate base index and local coords
                int baseIdx = sy * strideY + sx;
                int lx = sx - 1;
                int ly = sy - 1;
                
                for (int sz = 0; sz < SZ - 1; sz++)
                {
                    int idxBack = baseIdx + sz * strideZ;
                    int idxFront = idxBack + strideZ;
                    byte idBack = sentinel[idxBack];
                    byte idFront = sentinel[idxFront];
#if LOGGING
                    if (enableVerboseLogging) chunk.boundaryChecksZ++;
#endif
                    if (idBack == idFront) continue;

                    // North face (positive Z) of back cell (only if back is interior)
                    if (sz >= 1)
                    {
                        // Fully inlined _ShouldDrawFace for performance
                        int idx = (idBack << 8) | idFront;
                        bool drawTest = idx < drawTableLen && drawTable[idx] != 0;
#if LOGGING
                        if (enableVerboseLogging) { chunk.shouldDrawTests++; if (drawTest) chunk.shouldDrawTrue++; }
#endif
                        if (drawTest)
                        {
                            _AddFace(chunk, FaceVertices_North, Normal_North, new Vector3(lx, ly, sz - 1), idBack, _GetVisibilityType(idBack), FACE_INDEX_SIDE);
                        }
                    }
                    // South face (negative Z) of front cell (only if front is interior)
                    if (sz <= maxSZ)
                    {
                        // Fully inlined _ShouldDrawFace for performance
                        int idx2 = (idFront << 8) | idBack;
                        bool drawTest2 = idx2 < drawTableLen && drawTable[idx2] != 0;
#if LOGGING
                        if (enableVerboseLogging) { chunk.shouldDrawTests++; if (drawTest2) chunk.shouldDrawTrue++; }
#endif
                        if (drawTest2)
                        {
                            _AddFace(chunk, FaceVertices_South, Normal_South, new Vector3(lx, ly, sz), idFront, _GetVisibilityType(idFront), FACE_INDEX_SIDE);
                        }
                    }
                }

                // advance (u,v)
                chunk._greedyU++;
                int limitU = SX - 2;
                int limitV = SY - 2;
                if (chunk._greedyU >= limitU)
                {
                    chunk._greedyU = 0;
                    chunk._greedyV++;
                    if (chunk._greedyV >= limitV)
                    {
                        chunk._greedyV = 0;
                        chunk._greedyAxis++;
                    }
                }
#if LOGGING
                if (enableVerboseLogging) chunk.time_AxisZ += (Time.realtimeSinceStartup - axisStart) * 1000f;
#endif
            }
            else // X-axis
            {
#if LOGGING
                float axisStart = 0f; if (enableVerboseLogging) axisStart = Time.realtimeSinceStartup;
#endif
                // X-axis faces (East/West), iterate a line along X for fixed (sy, sz)
                int sy = 1 + chunk._greedyU; // 1..SY-2
                int sz = 1 + chunk._greedyV; // 1..SZ-2

                // Pre-calculate base index and local coords
                int baseIdx = sy * strideY + sz * strideZ;
                int ly = sy - 1;
                int lz = sz - 1;
                
                for (int sx = 0; sx < SX - 1; sx++)
                {
                    int idxLeft = baseIdx + sx;
                    int idxRight = idxLeft + 1;
                    byte idLeft = sentinel[idxLeft];
                    byte idRight = sentinel[idxRight];
#if LOGGING
                    if (enableVerboseLogging) chunk.boundaryChecksX++;
#endif
                    if (idLeft == idRight) continue;

                    // East face (positive X) of left cell (only if left is interior)
                    if (sx >= 1)
                    {
                        // Fully inlined _ShouldDrawFace for performance
                        int idx = (idLeft << 8) | idRight;
                        bool drawTest = idx < drawTableLen && drawTable[idx] != 0;
#if LOGGING
                        if (enableVerboseLogging) { chunk.shouldDrawTests++; if (drawTest) chunk.shouldDrawTrue++; }
#endif
                        if (drawTest)
                        {
                            _AddFace(chunk, FaceVertices_East, Normal_East, new Vector3(sx - 1, ly, lz), idLeft, _GetVisibilityType(idLeft), FACE_INDEX_SIDE);
                        }
                    }
                    // West face (negative X) of right cell (only if right is interior)
                    if (sx <= maxSX)
                    {
                        // Fully inlined _ShouldDrawFace for performance
                        int idx2 = (idRight << 8) | idLeft;
                        bool drawTest2 = idx2 < drawTableLen && drawTable[idx2] != 0;
#if LOGGING
                        if (enableVerboseLogging) { chunk.shouldDrawTests++; if (drawTest2) chunk.shouldDrawTrue++; }
#endif
                        if (drawTest2)
                        {
                            _AddFace(chunk, FaceVertices_West, Normal_West, new Vector3(sx, ly, lz), idRight, _GetVisibilityType(idRight), FACE_INDEX_SIDE);
                        }
                    }
                }

                // advance (u,v)
                chunk._greedyU++;
                int limitU = SY - 2;
                int limitV = SZ - 2;
                if (chunk._greedyU >= limitU)
                {
                    chunk._greedyU = 0;
                    chunk._greedyV++;
                    if (chunk._greedyV >= limitV)
                    {
                        chunk._greedyV = 0;
                        chunk._greedyAxis++;
                    }
                }
#if LOGGING
                if (enableVerboseLogging) chunk.time_AxisX += (Time.realtimeSinceStartup - axisStart) * 1000f;
#endif
            }
        }

#if LOGGING
        if (enableVerboseLogging) chunk.time_MainLoop += (Time.realtimeSinceStartup - timer_start_stage) * 1000f;
#endif

        if (chunk._greedyAxis > 2)
        {
            _ApplyAllMeshData(chunk);
            chunk.isBuildingMesh = false;
            
            // Null out neighbor references to free memory
            chunk.neighborPX = null; chunk.neighborNX = null;
            chunk.neighborPY = null; chunk.neighborNY = null;
            chunk.neighborPZ = null; chunk.neighborNZ = null;
            // Release decompressed caches
            chunk._decompSelf = null;
            chunk._decompNX = null; chunk._decompPX = null;
            chunk._decompNY = null; chunk._decompPY = null;
            chunk._decompNZ = null; chunk._decompPZ = null;
            
            // Can't use SendCustomEventDelayedFrames here easily without more state,
            // so we'll just apply the collider directly after a few frames in the main loop.
            // A more robust solution might involve another queue. For now, this is simpler.
            _ApplyDataToCollider(chunk); 

#if LOGGING
            if (enableVerboseLogging)
            {
                logBuilder.AppendLine("--- Timings ---");
                logBuilder.AppendLine($"1. Neighbor Caching: {chunk.time_NeighborCache:F3} ms");
                logBuilder.AppendLine($"2. Data Prep (Total): {chunk.time_DataPrep:F3} ms");
                logBuilder.AppendLine($"   2a. Sentinel Ensure: {chunk.time_SentinelEnsure:F3} ms");
                logBuilder.AppendLine($"   2b. Decompress Neighbors: {chunk.time_DecompressNeighbors:F3} ms");
                logBuilder.AppendLine($"   2c. Sentinel Build: {chunk.time_SentinelBuild:F3} ms");
                logBuilder.AppendLine($"3. Main Loop (Total): {chunk.time_MainLoop:F3} ms ({chunk.mesh_step_count} steps)");
                logBuilder.AppendLine($"   3a. Axis Y: {chunk.time_AxisY:F3} ms, Boundaries: {chunk.boundaryChecksY}");
                logBuilder.AppendLine($"   3b. Axis Z: {chunk.time_AxisZ:F3} ms, Boundaries: {chunk.boundaryChecksZ}");
                logBuilder.AppendLine($"   3c. Axis X: {chunk.time_AxisX:F3} ms, Boundaries: {chunk.boundaryChecksX}");
                logBuilder.AppendLine($"   3d. ShouldDraw tests: {chunk.shouldDrawTests}, True: {chunk.shouldDrawTrue}");
                logBuilder.AppendLine($"   3e. Faces - Total: {chunk.facesTotal}, Opaque: {chunk.facesOpaque}, Transparent: {chunk.facesTransparent}, Cutout: {chunk.facesCutout}");
                logBuilder.AppendLine($"   3f. Sentinel Copies - Interior: {chunk.sentinelInteriorCopied}, Border: {chunk.sentinelBorderCopied}");
                logBuilder.AppendLine($"4. Apply Opaque: {chunk.time_ApplyOpaque:F3} ms ({chunk._opaqueVertexCount} verts)");
                logBuilder.AppendLine($"5. Apply Transparent: {chunk.time_ApplyTransparent:F3} ms ({chunk._transparentVertexCount} verts)");
                logBuilder.AppendLine($"6. Apply Cutout: {chunk.time_ApplyCutout:F3} ms ({chunk._cutoutVertexCount} verts)");
                logBuilder.AppendLine($"7. Apply Collision: {chunk.time_ApplyCollision:F3} ms ({chunk._collisionVertexCount} verts)");
                Debug.Log(logBuilder.ToString());
            }
#endif
        }
    }

    private void _EnsureSentinelBuffer(ChunkData chunk)
    {
        int SX = chunkSizeXZ + 2;
        int SY = chunkSizeY + 2;
        int SZ = chunkSizeXZ + 2;
        if (chunk._sentinel == null || chunk._sentinelSX != SX || chunk._sentinelSY != SY || chunk._sentinelSZ != SZ)
        {
            chunk._sentinel = new byte[SX * SY * SZ];
            chunk._sentinelSX = SX; chunk._sentinelSY = SY; chunk._sentinelSZ = SZ;
            chunk._sentinelStrideZ = SX;
            chunk._sentinelStrideY = SX * SZ;
        }
        chunk._sentinelReady = false;
    }

    private int _SentinelIndex(int sx, int sy, int sz, int SX, int SY)
    {
        return (sy * SX * (chunkSizeXZ + 2)) + (sz * SX) + sx; // SX==chunk._sentinelSX, SZ not needed for stride
    }

    private void _BuildSentinel(ChunkData chunk)
    {
        int SX = chunk._sentinelSX;
        int SY = chunk._sentinelSY;
        int SZ = chunk._sentinelSZ;
        int strideY = chunk._sentinelStrideY;
        int strideZ = chunk._sentinelStrideZ;
        byte[] s = chunk._sentinel;

        // Clear once
        System.Array.Clear(s, 0, s.Length);

        // 1) Interior from self decompressed
        byte[] self = chunk._decompSelf;
        if (self != null)
        {
            int innerStride = chunkSizeXZ * chunkSizeXZ;
            for (int y = 0; y < chunkSizeY; y++)
            {
                int srcBase = y * innerStride;
                int dstBase = (y + 1) * strideY + strideZ + 1; // (sy=y+1, sz=1, sx=1)
                for (int z = 0; z < chunkSizeXZ; z++)
                {
                    int lineSrc = srcBase + z * chunkSizeXZ;
                    int lineDst = dstBase + z * strideZ;
                    System.Array.Copy(self, lineSrc, s, lineDst, chunkSizeXZ);
                }
            }
#if LOGGING
            if (enableVerboseLogging) chunk.sentinelInteriorCopied = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
#endif
        }

        // 2) Borders from neighbors
        // NX (sx=0) from neighborNX x=chunkSizeXZ-1
        _FillSentinelBorderX(chunk, 0, chunk._decompNX, chunkSizeXZ - 1);
        // PX (sx=SX-1) from neighborPX x=0
        _FillSentinelBorderX(chunk, SX - 1, chunk._decompPX, 0);
        // NZ (sz=0) from neighborNZ z=chunkSizeXZ-1
        _FillSentinelBorderZ(chunk, 0, chunk._decompNZ, chunkSizeXZ - 1);
        // PZ (sz=SZ-1) from neighborPZ z=0
        _FillSentinelBorderZ(chunk, SZ - 1, chunk._decompPZ, 0);
        // NY (sy=0) from neighborNY y=chunkSizeY-1
        _FillSentinelBorderY(chunk, 0, chunk._decompNY, chunkSizeY - 1);
        // PY (sy=SY-1) from neighborPY y=0
        _FillSentinelBorderY(chunk, SY - 1, chunk._decompPY, 0);

        chunk._sentinelReady = true;
    }

    private void _FillSentinelBorderX(ChunkData chunk, int sx, byte[] neighborFlat, int neighborX)
    {
        if (neighborFlat == null) return;
        int SX = chunk._sentinelSX; int SY = chunk._sentinelSY; int SZ = chunk._sentinelSZ;
        int strideY = chunk._sentinelStrideY; int strideZ = chunk._sentinelStrideZ;
        int innerStride = chunkSizeXZ * chunkSizeXZ;
        byte[] s = chunk._sentinel;
        for (int y = 0; y < chunkSizeY; y++)
        {
            int srcY = y * innerStride;
            int dstY = (y + 1) * strideY;
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                int src = srcY + z * chunkSizeXZ + neighborX;
                int dst = dstY + (z + 1) * strideZ + sx;
                s[dst] = neighborFlat[src];
#if LOGGING
                if (enableVerboseLogging) chunk.sentinelBorderCopied++;
#endif
            }
        }
    }

    private void _FillSentinelBorderZ(ChunkData chunk, int sz, byte[] neighborFlat, int neighborZ)
    {
        if (neighborFlat == null) return;
        int SX = chunk._sentinelSX; int SY = chunk._sentinelSY; int SZ = chunk._sentinelSZ;
        int strideY = chunk._sentinelStrideY; int strideZ = chunk._sentinelStrideZ;
        int innerStride = chunkSizeXZ * chunkSizeXZ;
        byte[] s = chunk._sentinel;
        for (int y = 0; y < chunkSizeY; y++)
        {
            int srcY = y * innerStride;
            int dstY = (y + 1) * strideY;
            for (int x = 0; x < chunkSizeXZ; x++)
            {
                int src = srcY + neighborZ * chunkSizeXZ + x;
                int dst = dstY + sz * strideZ + (x + 1);
                s[dst] = neighborFlat[src];
            }
#if LOGGING
            if (enableVerboseLogging) chunk.sentinelBorderCopied += chunkSizeXZ;
#endif
        }
    }

    private void _FillSentinelBorderY(ChunkData chunk, int sy, byte[] neighborFlat, int neighborY)
    {
        if (neighborFlat == null) return;
        int SX = chunk._sentinelSX; int SY = chunk._sentinelSY; int SZ = chunk._sentinelSZ;
        int strideY = chunk._sentinelStrideY; int strideZ = chunk._sentinelStrideZ;
        int innerStride = chunkSizeXZ * chunkSizeXZ;
        byte[] s = chunk._sentinel;
        int dstBase = sy * strideY + strideZ + 1;
        int srcBase = neighborY * innerStride;
        for (int z = 0; z < chunkSizeXZ; z++)
        {
            int lineSrc = srcBase + z * chunkSizeXZ;
            int lineDst = dstBase + z * strideZ;
            System.Array.Copy(neighborFlat, lineSrc, s, lineDst, chunkSizeXZ);
#if LOGGING
            if (enableVerboseLogging) chunk.sentinelBorderCopied += chunkSizeXZ;
#endif
        }
    }

    private void _DecompressNeighborsOnce(ChunkData chunk)
    {
        // Decompress self and any ready neighbors once
        chunk._decompSelf = _GetDecompressedData(chunk);
        chunk._decompNX = (chunk.neighborNX != null && chunk.neighborNX.isDataReady) ? _GetDecompressedData(chunk.neighborNX) : null;
        chunk._decompPX = (chunk.neighborPX != null && chunk.neighborPX.isDataReady) ? _GetDecompressedData(chunk.neighborPX) : null;
        chunk._decompNY = (chunk.neighborNY != null && chunk.neighborNY.isDataReady) ? _GetDecompressedData(chunk.neighborNY) : null;
        chunk._decompPY = (chunk.neighborPY != null && chunk.neighborPY.isDataReady) ? _GetDecompressedData(chunk.neighborPY) : null;
        chunk._decompNZ = (chunk.neighborNZ != null && chunk.neighborNZ.isDataReady) ? _GetDecompressedData(chunk.neighborNZ) : null;
        chunk._decompPZ = (chunk.neighborPZ != null && chunk.neighborPZ.isDataReady) ? _GetDecompressedData(chunk.neighborPZ) : null;
    }

    private void _ProcessBoundary(ChunkData chunk, byte id1, byte id2, Vector3 pos1, Vector3 pos2, int axis)
    {
        if (id1 == id2) return;

        BlockVisibilityType vis1 = _GetVisibilityType(id1);
        BlockVisibilityType vis2 = _GetVisibilityType(id2);

        if (_ShouldDrawFace(id1, id2))
        {
            if (axis == 0) _AddFace(chunk, FaceVertices_Up, Normal_Up, pos1, id1, vis1, FACE_INDEX_TOP);
            else if (axis == 1) _AddFace(chunk, FaceVertices_North, Normal_North, pos1, id1, vis1, FACE_INDEX_SIDE);
            else _AddFace(chunk, FaceVertices_East, Normal_East, pos1, id1, vis1, FACE_INDEX_SIDE);
        }

        if (_ShouldDrawFace(id2, id1))
        {
            if (axis == 0) _AddFace(chunk, FaceVertices_Down, Normal_Down, pos2, id2, vis2, FACE_INDEX_BOTTOM);
            else if (axis == 1) _AddFace(chunk, FaceVertices_South, Normal_South, pos2, id2, vis2, FACE_INDEX_SIDE);
            else _AddFace(chunk, FaceVertices_West, Normal_West, pos2, id2, vis2, FACE_INDEX_SIDE);
        }
    }

    private byte _GetBlockForGreedyMesher(ChunkData chunk, int x, int y, int z)
    {
        // --- OPTIMIZATION: Check boundaries and query neighbor chunk directly ---
        if (x < 0) {
            ChunkData n = chunk.neighborNX;
            if (n == null || !n.isDataReady) return 0;
            return _GetBlockLocal(n, chunkSizeXZ - 1, y, z);
        }
        if (x >= chunkSizeXZ) {
            ChunkData n = chunk.neighborPX;
            if (n == null || !n.isDataReady) return 0;
            return _GetBlockLocal(n, 0, y, z);
        }
        if (y < 0) {
            ChunkData n = chunk.neighborNY;
            if (n == null || !n.isDataReady) return 0;
            return _GetBlockLocal(n, x, chunkSizeY - 1, z);
        }
        if (y >= chunkSizeY) {
            ChunkData n = chunk.neighborPY;
            if (n == null || !n.isDataReady) return 0;
            return _GetBlockLocal(n, x, 0, z);
        }
        if (z < 0) {
            ChunkData n = chunk.neighborNZ;
            if (n == null || !n.isDataReady) return 0;
            return _GetBlockLocal(n, x, y, chunkSizeXZ - 1);
        }
        if (z >= chunkSizeXZ) {
            ChunkData n = chunk.neighborPZ;
            if (n == null || !n.isDataReady) return 0;
            return _GetBlockLocal(n, x, y, 0);
        }

        // If within bounds, use the optimized getter on the current chunk's compressed data
        return _GetBlockLocal(chunk, x, y, z);
    }
    
    private void _AddFace(ChunkData chunk, Vector3[] faceVertices, Vector3 faceNormal, Vector3 blockPos, byte blockID, BlockVisibilityType visibility, int faceIndex)
    {
        Vector3[] targetVertices; int[] targetTriangles; Vector3[] targetUVs; Vector3[] targetNormals;
        int currentVertexCount; int currentTriangleCount;

        if (visibility == BlockVisibilityType.Opaque) {
            if (chunk._opaqueVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._opaqueVertices; targetTriangles = chunk._opaqueTriangles; targetUVs = chunk._opaqueUVs; targetNormals = chunk._opaqueNormals;
            currentVertexCount = chunk._opaqueVertexCount; currentTriangleCount = chunk._opaqueTriangleCount;
        } else if (visibility == BlockVisibilityType.Transparent) {
            if (chunk._transparentVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._transparentVertices; targetTriangles = chunk._transparentTriangles; targetUVs = chunk._transparentUVs; targetNormals = chunk._transparentNormals;
            currentVertexCount = chunk._transparentVertexCount; currentTriangleCount = chunk._transparentTriangleCount;
        } else { // Cutout
            if (chunk._cutoutVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._cutoutVertices; targetTriangles = chunk._cutoutTriangles; targetUVs = chunk._cutoutUVs; targetNormals = chunk._cutoutNormals;
            currentVertexCount = chunk._cutoutVertexCount; currentTriangleCount = chunk._cutoutTriangleCount;
        }
        
        float bx = blockPos.x, by = blockPos.y, bz = blockPos.z;
        targetVertices[currentVertexCount + 0] = new Vector3(bx + faceVertices[0].x, by + faceVertices[0].y, bz + faceVertices[0].z);
        targetVertices[currentVertexCount + 1] = new Vector3(bx + faceVertices[1].x, by + faceVertices[1].y, bz + faceVertices[1].z);
        targetVertices[currentVertexCount + 2] = new Vector3(bx + faceVertices[2].x, by + faceVertices[2].y, bz + faceVertices[2].z);
        targetVertices[currentVertexCount + 3] = new Vector3(bx + faceVertices[3].x, by + faceVertices[3].y, bz + faceVertices[3].z);
        for (int i=0; i<4; i++) targetNormals[currentVertexCount + i] = faceNormal;
        
        float textureSlice = _GetTextureSlice(blockID, faceIndex);
        targetUVs[currentVertexCount + 0] = new Vector3(0, 0, textureSlice); targetUVs[currentVertexCount + 1] = new Vector3(0, 1, textureSlice);
        targetUVs[currentVertexCount + 2] = new Vector3(1, 1, textureSlice); targetUVs[currentVertexCount + 3] = new Vector3(1, 0, textureSlice);
        
        targetTriangles[currentTriangleCount + 0] = currentVertexCount; targetTriangles[currentTriangleCount + 1] = currentVertexCount + 1;
        targetTriangles[currentTriangleCount + 2] = currentVertexCount + 2; targetTriangles[currentTriangleCount + 3] = currentVertexCount;
        targetTriangles[currentTriangleCount + 4] = currentVertexCount + 2; targetTriangles[currentTriangleCount + 5] = currentVertexCount + 3;
        
        if (visibility == BlockVisibilityType.Opaque) { chunk._opaqueVertexCount += 4; chunk._opaqueTriangleCount += 6; }
        else if (visibility == BlockVisibilityType.Transparent) { chunk._transparentVertexCount += 4; chunk._transparentTriangleCount += 6; }
        else { chunk._cutoutVertexCount += 4; chunk._cutoutTriangleCount += 6; }
        
        if (visibility != BlockVisibilityType.Transparent && chunk._collisionVertexCount < MAX_VERTS * 3 - 4) {
            chunk._collisionVertices[chunk._collisionVertexCount + 0] = targetVertices[currentVertexCount + 0];
            chunk._collisionVertices[chunk._collisionVertexCount + 1] = targetVertices[currentVertexCount + 1];
            chunk._collisionVertices[chunk._collisionVertexCount + 2] = targetVertices[currentVertexCount + 2];
            chunk._collisionVertices[chunk._collisionVertexCount + 3] = targetVertices[currentVertexCount + 3];
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount; chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 1;
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 2; chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount;
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 2; chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 3;
            chunk._collisionVertexCount += 4;
        }
#if LOGGING
        if (enableVerboseLogging)
        {
            chunk.facesTotal++;
            if (visibility == BlockVisibilityType.Opaque) chunk.facesOpaque++;
            else if (visibility == BlockVisibilityType.Transparent) chunk.facesTransparent++;
            else chunk.facesCutout++;
        }
#endif
    }
    
    private void _ApplyEmptyMesh(ChunkData chunk)
    {
        _ApplyDataToMesh(chunk.opaqueMeshFilter, chunk._opaqueVertices, chunk._opaqueTriangles, chunk._opaqueUVs, chunk._opaqueNormals, 0, 0);
        _ApplyDataToMesh(chunk.transparentMeshFilter, chunk._transparentVertices, chunk._transparentTriangles, chunk._transparentUVs, chunk._transparentNormals, 0, 0);
        _ApplyDataToMesh(chunk.cutoutMeshFilter, chunk._cutoutVertices, chunk._cutoutTriangles, chunk._cutoutUVs, chunk._cutoutNormals, 0, 0);
        _ApplyDataToCollider(chunk);
    }
    
    private byte[] _GetDecompressedData(ChunkData chunk)
    {
        // OPTIMIZED: Use persistent cache to avoid repeated decompression
        if (chunk._decompCacheValid && chunk._cachedDecompressedData != null)
        {
            return chunk._cachedDecompressedData;
        }
        
        byte[] decompressed = _DecompressChunkColumnRLE(chunk);
        chunk._cachedDecompressedData = decompressed;
        chunk._decompCacheValid = true;
        return decompressed;
    }
    
    private void _ClearAllMeshBuffers(ChunkData chunk)
    {
        chunk._opaqueVertexCount = 0; chunk._opaqueTriangleCount = 0;
        chunk._transparentVertexCount = 0; chunk._transparentTriangleCount = 0;
        chunk._cutoutVertexCount = 0; chunk._cutoutTriangleCount = 0;
        chunk._collisionVertexCount = 0; chunk._collisionTriangleCount = 0;
    }
    
    private bool _ShouldDrawFace(byte selfID, byte neighborID)
    {
        int idx = (selfID << 8) | neighborID;
        return shouldDrawTable != null && idx >= 0 && idx < shouldDrawTable.Length && shouldDrawTable[idx] != 0;
    }
    
    private void _ApplyAllMeshData(ChunkData chunk)
    {
#if LOGGING
        if (enableVerboseLogging)
        {
            float timer_start = Time.realtimeSinceStartup;
            _ApplyDataToMesh(chunk.opaqueMeshFilter, chunk._opaqueVertices, chunk._opaqueTriangles, chunk._opaqueUVs, chunk._opaqueNormals, chunk._opaqueVertexCount, chunk._opaqueTriangleCount);
            chunk.time_ApplyOpaque = (Time.realtimeSinceStartup - timer_start) * 1000f;

            timer_start = Time.realtimeSinceStartup;
            _ApplyDataToMesh(chunk.transparentMeshFilter, chunk._transparentVertices, chunk._transparentTriangles, chunk._transparentUVs, chunk._transparentNormals, chunk._transparentVertexCount, chunk._transparentTriangleCount);
            chunk.time_ApplyTransparent = (Time.realtimeSinceStartup - timer_start) * 1000f;

            timer_start = Time.realtimeSinceStartup;
            _ApplyDataToMesh(chunk.cutoutMeshFilter, chunk._cutoutVertices, chunk._cutoutTriangles, chunk._cutoutUVs, chunk._cutoutNormals, chunk._cutoutVertexCount, chunk._cutoutTriangleCount);
            chunk.time_ApplyCutout = (Time.realtimeSinceStartup - timer_start) * 1000f;
            return;
        }
#endif
        _ApplyDataToMesh(chunk.opaqueMeshFilter, chunk._opaqueVertices, chunk._opaqueTriangles, chunk._opaqueUVs, chunk._opaqueNormals, chunk._opaqueVertexCount, chunk._opaqueTriangleCount);
        _ApplyDataToMesh(chunk.transparentMeshFilter, chunk._transparentVertices, chunk._transparentTriangles, chunk._transparentUVs, chunk._transparentNormals, chunk._transparentVertexCount, chunk._transparentTriangleCount);
        _ApplyDataToMesh(chunk.cutoutMeshFilter, chunk._cutoutVertices, chunk._cutoutTriangles, chunk._cutoutUVs, chunk._cutoutNormals, chunk._cutoutVertexCount, chunk._cutoutTriangleCount);
    }

    private void _ApplyDataToMesh(MeshFilter mf, Vector3[] vertices, int[] triangles, Vector3[] uvs, Vector3[] normals, int vertexCount, int triangleCount)
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
        m.SetUVs(0, finalUVs);
        m.RecalculateBounds();
    }

    private void _ApplyDataToCollider(ChunkData chunk)
    {
#if LOGGING
        float timer_start = 0f;
        if(enableVerboseLogging) timer_start = Time.realtimeSinceStartup;
#endif

        if (chunk.meshCollider == null) return;
        Mesh colMesh = chunk.meshCollider.sharedMesh;
        if (colMesh == null) { colMesh = new Mesh(); colMesh.name = $"ChunkCollisionMesh_{chunk.gameObject.name}"; }
        
        colMesh.Clear();
        if (chunk._collisionVertexCount == 0) { chunk.meshCollider.sharedMesh = null; chunk.meshCollider.enabled = false; return; }
        
        chunk.meshCollider.enabled = true;
        Vector3[] finalVertices = new Vector3[chunk._collisionVertexCount]; System.Array.Copy(chunk._collisionVertices, finalVertices, chunk._collisionVertexCount);
        int[] finalTriangles = new int[chunk._collisionTriangleCount]; System.Array.Copy(chunk._collisionTriangles, finalTriangles, chunk._collisionTriangleCount);
        
        colMesh.vertices = finalVertices; 
        colMesh.triangles = finalTriangles;
        chunk.meshCollider.sharedMesh = colMesh;

#if LOGGING
        if(enableVerboseLogging) chunk.time_ApplyCollision = (Time.realtimeSinceStartup - timer_start) * 1000f;
#endif
    }

    #endregion

    #region Block Data & RLE Logic (Refactored for Column-Based RLE)

    private byte _GetBlockLocal(ChunkData chunk, int x, int y, int z)
    {
        if (chunk._chunkData == null || !chunk.isDataReady) return 0;
        
        System.Type dataType = chunk._chunkData.GetType();

        // Case 1: Homogeneous chunk, data is a single ushort
        if (dataType == typeof(byte))
        {
            return (byte)chunk._chunkData;
        }

        // Case 2: Column RLE chunk, data is ushort[][]
        if (dataType == typeof(ushort[][]))
        {
            ushort[][] columnData = (ushort[][])chunk._chunkData;
            int columnIndex = z * chunkSizeXZ + x;

            if (columnIndex < 0 || columnIndex >= columnData.Length) return 0; // Should not happen

            ushort[] rlePairs = columnData[columnIndex];
            if (rlePairs == null) return 0;

            int currentY = 0;
            for (int i = 0; i < rlePairs.Length; i += 2)
            {
                ushort blockID = rlePairs[i];
                ushort runLength = rlePairs[i + 1];
                currentY += runLength;
                if (y < currentY)
                {
                    return (byte)blockID;
                }
            }
        }
        
        return 0; // Fallback for out of bounds Y or error
    }

    private void _SetBlockLocal(ChunkData chunk, int x, int y, int z, byte blockType, bool updateMesh)
    {
        if (x < 0 || x >= chunkSizeXZ || y < 0 || y >= chunkSizeY || z < 0 || z >= chunkSizeXZ) return;
        
        // Optimization: check if the block is actually changing before doing any work.
        if (blockType == _GetBlockLocal(chunk, x, y, z)) return;

        // OPTIMIZATION: Invalidate decompression cache since data is changing
        chunk._decompCacheValid = false;

        // --- New Optimized Set-Block Logic ---
        if (chunk._chunkData.GetType() == typeof(ushort[][])) // Case 1: Standard Column RLE chunk
        {
            ushort[][] columnData = (ushort[][])chunk._chunkData;
            int columnIndex = z * chunkSizeXZ + x;

            if (columnIndex < 0 || columnIndex >= columnData.Length) return; // Should not happen

            // Decompress the single column, modify it, and re-compress it.
            byte[] decompressedColumn = _DecompressSingleColumn(columnData[columnIndex]);
            decompressedColumn[y] = blockType;
            columnData[columnIndex] = _CompressSingleColumn(decompressedColumn);

            // Since the chunk was already heterogeneous, isSingleOpaqueSolid must be false.
        }
        else if (chunk._chunkData.GetType() == typeof(byte)) // Case 2: Homogeneous chunk being modified
        {
            // The chunk is no longer homogeneous. Decompress the whole thing once.
            byte[] fullData = _DecompressChunkColumnRLE(chunk);
            if (fullData == null) return; 

            // Modify the block
            int localIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            fullData[localIndex] = blockType;

            // Re-compress into the new Column RLE format.
            bool isHomogeneous; // Will be false
            chunk._chunkData = _CompressChunkColumnRLE(fullData, out isHomogeneous);
            chunk.isSingleOpaqueSolid = false;
        }

        if (!updateMesh) return;

        int chunkIndex = ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
        RequestChunkMeshUpdate(chunkIndex);
        
        if (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1)
        {
            TriggerNeighborMeshRebuilds(chunk);
        }
    }
    
    private byte[] _DecompressSingleColumn(ushort[] rlePairs)
    {
        byte[] columnData = new byte[chunkSizeY];
        if (rlePairs == null) return columnData;

        int currentY = 0;
        for (int p = 0; p < rlePairs.Length; p += 2)
        {
            ushort blockID = rlePairs[p];
            ushort runLength = rlePairs[p + 1];
            for (int j = 0; j < runLength; j++)
            {
                if (currentY + j < chunkSizeY)
                {
                    columnData[currentY + j] = (byte)blockID;
                }
            }
            currentY += runLength;
        }
        return columnData;
    }

    private ushort[] _CompressSingleColumn(byte[] columnData)
    {
        var columnRuns = new System.Collections.Generic.List<ushort>();
        for (int y = 0; y < chunkSizeY; )
        {
            byte currentBlock = columnData[y];
            ushort runLength = 1;
            while (y + runLength < chunkSizeY && columnData[y + runLength] == currentBlock && runLength < ushort.MaxValue)
            {
                runLength++;
            }
            columnRuns.Add(currentBlock);
            columnRuns.Add(runLength);
            y += runLength;
        }
        return columnRuns.ToArray();
    }

    private object _CompressChunkColumnRLE(byte[] fullChunkData, out bool isHomogeneous)
    {
        isHomogeneous = true;
        byte firstBlock = fullChunkData[0];
        for (int i = 1; i < fullChunkData.Length; i++) {
            if (fullChunkData[i] != firstBlock) {
                isHomogeneous = false;
                break;
            }
        }

        if (isHomogeneous) {
            return firstBlock;
        }

        int columnCount = chunkSizeXZ * chunkSizeXZ;
        ushort[][] columnRLEData = new ushort[columnCount][];
        
        for (int x = 0; x < chunkSizeXZ; x++) {
            for (int z = 0; z < chunkSizeXZ; z++) {
                int columnIndex = z * chunkSizeXZ + x;
                
                // Using a list here because we don't know the final size. This is fine as it's a one-off operation.
                var columnRuns = new System.Collections.Generic.List<ushort>();
                
                for (int y = 0; y < chunkSizeY; ) {
                    byte currentBlock = fullChunkData[y * columnCount + columnIndex];
                    ushort runLength = 1;
                    while (y + runLength < chunkSizeY && fullChunkData[(y + runLength) * columnCount + columnIndex] == currentBlock && runLength < ushort.MaxValue) {
                        runLength++;
                    }
                    columnRuns.Add(currentBlock);
                    columnRuns.Add(runLength);
                    y += runLength;
                }
                columnRLEData[columnIndex] = columnRuns.ToArray();
            }
        }
        return columnRLEData;
    }
    
    private byte[] _DecompressChunkColumnRLE(ChunkData chunk)
    {
        byte[] fullData = new byte[chunk._chunkDataSize];
        if (chunk._chunkData == null) return fullData;
        
        System.Type dataType = chunk._chunkData.GetType();

        // Case 1: Homogeneous chunk (optimized with Array.Fill equivalent)
        if (dataType == typeof(byte)) {
            byte blockValue = (byte)chunk._chunkData;
            // OPTIMIZED: Use manual loop unrolling for better performance
            int size = chunk._chunkDataSize;
            int i = 0;
            int limit = size - 15;
            
            // Process 16 at a time
            for (; i < limit; i += 16) {
                fullData[i] = blockValue;
                fullData[i+1] = blockValue;
                fullData[i+2] = blockValue;
                fullData[i+3] = blockValue;
                fullData[i+4] = blockValue;
                fullData[i+5] = blockValue;
                fullData[i+6] = blockValue;
                fullData[i+7] = blockValue;
                fullData[i+8] = blockValue;
                fullData[i+9] = blockValue;
                fullData[i+10] = blockValue;
                fullData[i+11] = blockValue;
                fullData[i+12] = blockValue;
                fullData[i+13] = blockValue;
                fullData[i+14] = blockValue;
                fullData[i+15] = blockValue;
            }
            // Finish remaining
            for (; i < size; i++) {
                fullData[i] = blockValue;
            }
            return fullData;
        }

        // Case 2: Column RLE chunk (optimized indexing)
        if (dataType == typeof(ushort[][])) {
            ushort[][] columnRLEData = (ushort[][])chunk._chunkData;
            int columnCount = chunkSizeXZ * chunkSizeXZ;
            int maxY = chunkSizeY;

            for(int col = 0; col < columnCount; col++) {
                ushort[] rlePairs = columnRLEData[col];
                if (rlePairs == null) continue;

                int currentY = 0;
                int pairCount = rlePairs.Length;
                
                for (int p = 0; p < pairCount; p += 2) {
                    byte blockID = (byte)rlePairs[p];
                    int runLength = rlePairs[p+1];
                    int endY = currentY + runLength;
                    if (endY > maxY) endY = maxY;
                    
                    // OPTIMIZED: Direct calculation without nested loop when possible
                    int baseIdx = currentY * columnCount + col;
                    for (int y = currentY; y < endY; y++) {
                        fullData[baseIdx] = blockID;
                        baseIdx += columnCount;
                    }
                    currentY = endY;
                }
            }
        }
        
        return fullData;
    }

    #endregion
    
    #region Local Block Data Getters

    private bool _IsBlockSolid(byte blockID)
    {
        if (blockDataCache != null && blockID < blockDataCache.Length)
            return (blockDataCache[blockID] & 1) != 0;
        return false;
    }

    private BlockVisibilityType _GetVisibilityType(byte blockID)
    {
        if (visibilityCache != null && blockID < visibilityCache.Length) return visibilityCache[blockID];
        if (blockDataCache != null && blockID < blockDataCache.Length) return (BlockVisibilityType)((blockDataCache[blockID] >> 1) & 0x3);
        return BlockVisibilityType.Opaque;
    }

    private BlockCullingType _GetCullingType(byte blockID)
    {
        if (cullingCache != null && blockID < cullingCache.Length) return cullingCache[blockID];
        if (blockDataCache != null && blockID < blockDataCache.Length) return (BlockCullingType)((blockDataCache[blockID] >> 3) & 0x7);
        return BlockCullingType.CullAll;
    }

    private void _BuildBlockCaches()
    {
        int maxId = blockDataCache != null ? blockDataCache.Length : 256;
        visibilityCache = new BlockVisibilityType[maxId];
        cullingCache = new BlockCullingType[maxId];
        for (int i = 0; i < maxId; i++)
        {
            visibilityCache[i] = (BlockVisibilityType)((blockDataCache[i] >> 1) & 0x3);
            cullingCache[i] = (BlockCullingType)((blockDataCache[i] >> 3) & 0x7);
        }
        // Precompute should-draw table for all pairs
        int tableSize = 256 * 256; // supports up to 256 block IDs
        shouldDrawTable = new byte[tableSize];
        for (int a = 0; a < 256; a++)
        {
            BlockVisibilityType visA = a < maxId ? visibilityCache[a] : BlockVisibilityType.Opaque;
            BlockCullingType cullA = a < maxId ? cullingCache[a] : BlockCullingType.CullAll;
            for (int b = 0; b < 256; b++)
            {
                BlockVisibilityType visB = b < maxId ? visibilityCache[b] : BlockVisibilityType.Opaque;
                bool draw;
                if (b == 0) draw = true; // air
                else if (visA == BlockVisibilityType.Invisible) draw = false;
                else
                {
                    switch (cullA)
                    {
                        case BlockCullingType.NoCull: draw = true; break;
                        case BlockCullingType.CullSelf: draw = (b != a); break;
                        case BlockCullingType.CullSelfAndOpaque: draw = !(b == a || visB == BlockVisibilityType.Opaque); break;
                        case BlockCullingType.CullSelfAndCutout: draw = !(b == a || visB == BlockVisibilityType.Cutout); break;
                        case BlockCullingType.CullSelfAndTransparent: draw = !(b == a || visB == BlockVisibilityType.Transparent); break;
                        case BlockCullingType.CullAll: draw = false; break;
                        default: draw = (visB == BlockVisibilityType.Transparent || visB == BlockVisibilityType.Invisible); break;
                    }
                }
                shouldDrawTable[(a << 8) | b] = (byte)(draw ? 1 : 0);
            }
        }
    }

    private int _GetTextureSlice(byte blockID, int faceIndex)
    {
        if (blockDataCache != null && blockID >= 0 && blockID < blockDataCache.Length)
        {
            // Bits 8-9 for mapping type
            McBlockTextureMappingType mappingType = (McBlockTextureMappingType)((blockDataCache[blockID] >> 8) & 0x3);
            switch (mappingType)
            {
                case McBlockTextureMappingType.AllFacesSame: return uv_allFacesCache[blockID];
                case McBlockTextureMappingType.TopBottomSides:
                    if (faceIndex == 2) return uv_topFaceCache[blockID];
                    if (faceIndex == 3) return uv_bottomFaceCache[blockID];
                    return uv_sideFacesCache[blockID];
                default:
                    return uv_allFacesCache[blockID];
            }
        }
        return 0;
    }
    
    #endregion
}
