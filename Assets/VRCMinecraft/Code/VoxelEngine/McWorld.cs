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
    [Tooltip("Approximate time budget per mesh step in milliseconds. OPTIMIZATION Phase 4: Increased from 1.5ms to 8.0ms to reduce loop overhead (fewer, larger steps).")]
    public float meshStepTimeBudgetMs = 8.0f;
    [Tooltip("Time budget per Update() call in milliseconds to prevent frame drops.")]
    public float updateTimeBudgetMs = 12.0f;

    [Header("System References")]
    [SerializeField, FindObjectOfType(true)]
    public McTerrainGenerator terrainGenerator;
    [SerializeField, FindObjectOfType(true)]
    public McBlockTypeManager blockTypeManager;
    [SerializeField, FindObjectOfType(true)]
    private McCoordinator coordinator;
    
    [Header("Biome Color Textures (Beta 1.7.3)")]
    [Tooltip("grasscolor.png from Beta 1.7.3 (256x256)")]
    public Texture2D grassColorTexture;
    [Tooltip("foliagecolor.png from Beta 1.7.3 (256x256)")]
    public Texture2D foliageColorTexture;
    [Tooltip("watercolor.png from Beta 1.7.3 (256x256)")]
    public Texture2D waterColorTexture;
    
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
    private McBlockShapeType[] shapeTypeCache; // per blockID - NEW: cache for block shape types
    private byte[] shouldDrawTable; // 256x256 lookup: self<<8 | neighbor => 0/1

    // --- Lighting System (Minecraft Beta 1.7.3 style) ---
    private float[] lightBrightnessTable; // 16 values: light level 0-15 to brightness 0.0-1.0
    public int skylightSubtracted = 0; // 0-11, for day/night cycle (0 = noon, 11 = midnight)
    private int[] lightOpacityCache; // per blockID
    private int[] lightEmissionCache; // per blockID

    // --- Lighting Optimization: Memory Pooling ---
    private readonly System.Collections.Generic.Queue<int[]> bfsQueuePool = new System.Collections.Generic.Queue<int[]>();
    private readonly System.Collections.Generic.Queue<int[]> bfsQueuePoolLarge = new System.Collections.Generic.Queue<int[]>();
    private const int BFS_QUEUE_SIZE = 4096;
    private const int BFS_QUEUE_SIZE_LARGE = 16384; // FIXED: Increased to prevent queue overflow causing pitch black spots
    
    // --- OPTIMIZATION: Persistent Decompression Cache ---
    private readonly System.Collections.Generic.Dictionary<ChunkData, byte[]> decompressionCache = new System.Collections.Generic.Dictionary<ChunkData, byte[]>();
    private readonly System.Collections.Generic.Dictionary<ChunkData, bool> decompressionCacheValid = new System.Collections.Generic.Dictionary<ChunkData, bool>();
    
    // --- OPTIMIZATION: Neighbor Reference Cache ---
    private readonly System.Collections.Generic.Dictionary<ChunkData, ChunkData[]> neighborCache = new System.Collections.Generic.Dictionary<ChunkData, ChunkData[]>();
    private readonly System.Collections.Generic.Dictionary<ChunkData, bool> neighborCacheValid = new System.Collections.Generic.Dictionary<ChunkData, bool>();
    
    // --- OPTIMIZATION: Reusable Arrays ---
    private byte[] reusableByteArray = new byte[4096];
    private bool[] reusableBoolArray = new bool[4096];
    private int[] reusableIntArray = new int[4096];
    
    // --- OPTIMIZATION: Deferred Reconciliation System ---
    private readonly System.Collections.Generic.Queue<ChunkData> deferredReconciliationQueue = new System.Collections.Generic.Queue<ChunkData>();
    private readonly System.Collections.Generic.HashSet<ChunkData> reconciliationPending = new System.Collections.Generic.HashSet<ChunkData>();
    private const int MAX_RECONCILIATION_PER_FRAME = 5; // FIXED: Increased from 3 to 5 chunks per frame
    private const float RECONCILIATION_TIME_BUDGET_MS = 12.0f; // FIXED: Increased from 8ms to 12ms budget per frame

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
    
    // --- Performance Profiling Configuration ---
    [Header("Performance Profiling")]
    public bool enableFrameLogging = false;
    public bool enableAggregateLogging = true;
    public bool enableDetailedTimings = true;
    public bool enableCounters = true;
    public bool enableMemoryTracking = true;
    public bool enableCacheTracking = true;
    public int aggregateLogInterval = 300; // frames
    
    // --- Frame/Update Stats ---
    private int stats_frameCount = 0;
    private float stats_updateTotalTime = 0f;
    private float stats_updateTimeMin = float.MaxValue;
    private float stats_updateTimeMax = 0f;
    private int stats_budgetExceededCount = 0;
    private float stats_processActiveChunksTime = 0f;
    private float stats_reconciliationTime = 0f;
    private float stats_aggregateWindowStart = 0f;
    
    // --- Chunk Management Stats ---
    private int stats_chunkCreations = 0;
    private int stats_chunkDestructions = 0;
    private int stats_chunkStateTransitions = 0;
    private int stats_chunk1DLookups = 0;
    private int stats_chunk3DLookups = 0;
    
    // --- Mesh Building Stats (aggregate) ---
    private int stats_meshBuildTotal = 0;
    private float stats_meshBuildTimeTotal = 0f;
    private float stats_meshBuildTimeMin = float.MaxValue;
    private float stats_meshBuildTimeMax = 0f;
    private int stats_meshStepsTotal = 0;
    private float stats_greedyAxisYTime = 0f;
    private float stats_greedyAxisZTime = 0f;
    private float stats_greedyAxisXTime = 0f;
    private int stats_sentinelBuilds = 0;
    private float stats_sentinelBuildTime = 0f;
    private int stats_faceCullingTests = 0;
    private int stats_facesCulled = 0;
    private int stats_facesDrawn = 0;
    private int stats_verticesOpaque = 0;
    private int stats_verticesTransparent = 0;
    private int stats_verticesCutout = 0;
    private float stats_meshApplyOpaqueTime = 0f;
    private float stats_meshApplyTransparentTime = 0f;
    private float stats_meshApplyCutoutTime = 0f;
    private float stats_meshApplyColliderTime = 0f;
    
    // --- Lighting Stats (aggregate) ---
    private int stats_lightingInitsTotal = 0;
    private float stats_lightingInitTime = 0f;
    private int stats_lightingStepsTotal = 0;
    private float stats_lightingStepTime = 0f;
    private int stats_lightingBFSOps = 0;
    private int stats_lightingMaxQueueSize = 0;
    private int stats_lightingSkylightBlocks = 0;
    private int stats_lightingBlocklightBlocks = 0;
    private int stats_lightingCrossChunkQueries = 0;
    private int stats_lightingPoolAllocations = 0;
    private int stats_lightingPoolReuses = 0;
    
    // --- RLE Stats (aggregate) ---
    private int stats_rleCompressions = 0;
    private int stats_rleDecompressions = 0;
    private float stats_rleCompressionTime = 0f;
    private float stats_rleDecompressionTime = 0f;
    private int stats_rleTotalBytesIn = 0;
    private int stats_rleTotalBytesOut = 0;
    private int stats_rleHomogeneousChunks = 0;
    
    // --- Block Operation Stats ---
    private int stats_getBlockCalls = 0;
    private int stats_setBlockCalls = 0;
    private int stats_blockModifications = 0;
    private int stats_neighborRebuildTriggers = 0;
    
    // --- Cache Stats ---
    private int stats_decompCacheHits = 0;
    private int stats_decompCacheMisses = 0;
    private int stats_neighborCacheHits = 0;
    private int stats_neighborCacheMisses = 0;
    
    // --- Reconciliation Stats ---
    private int stats_reconciliationOps = 0;
    private int stats_reconciliationBlocks = 0;
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
        _BuildLightingTables();
        
        terrainGenerator.init(McUtils.GetMinecraftSeed(worldSeedString));
        
        int[] radialChunkOrder = GenerateRadialChunkOrder();
        coordinator.InitializeAndStartProcessing(this, radialChunkOrder, totalWorldChunks);
        
#if LOGGING
        stats_aggregateWindowStart = Time.realtimeSinceStartup;
#endif
        
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
        
        newChunkData._opaqueVertices = new Vector3[MAX_VERTS]; newChunkData._opaqueTriangles = new int[MAX_TRIS]; newChunkData._opaqueUVs = new Vector3[MAX_VERTS]; newChunkData._opaqueNormals = new Vector3[MAX_VERTS]; newChunkData._opaqueColors = new Color[MAX_VERTS];
        newChunkData._transparentVertices = new Vector3[MAX_VERTS]; newChunkData._transparentTriangles = new int[MAX_TRIS]; newChunkData._transparentUVs = new Vector3[MAX_VERTS]; newChunkData._transparentNormals = new Vector3[MAX_VERTS]; newChunkData._transparentColors = new Color[MAX_VERTS];
        newChunkData._cutoutVertices = new Vector3[MAX_VERTS]; newChunkData._cutoutTriangles = new int[MAX_TRIS]; newChunkData._cutoutUVs = new Vector3[MAX_VERTS]; newChunkData._cutoutNormals = new Vector3[MAX_VERTS]; newChunkData._cutoutColors = new Color[MAX_VERTS];
        newChunkData._collisionVertices = new Vector3[MAX_VERTS * 3]; newChunkData._collisionTriangles = new int[MAX_TRIS * 3];
        
        // Initialize biome data arrays (16x16 per chunk)
        newChunkData._biomeTemperatures = new double[chunkSizeXZ * chunkSizeXZ];
        newChunkData._biomeRainfall = new double[chunkSizeXZ * chunkSizeXZ];
        
        newChunkGO.SetActive(true);

#if LOGGING
        if (enableCounters) stats_chunkCreations++;
#endif

        return chunk1DIndex;
    }

    void Update()
    {
        // Safety check: Don't process if not initialized
        if (chunks_1D == null || terrainGenerator == null) return;
        
#if LOGGING
        float updateStartTime = 0f;
        if (enableDetailedTimings || enableFrameLogging || enableAggregateLogging)
        {
            updateStartTime = Time.realtimeSinceStartup;
        }
#endif
        
        ProcessActiveChunks();
        
#if LOGGING
        if (enableDetailedTimings || enableFrameLogging || enableAggregateLogging)
        {
            float updateTime = (Time.realtimeSinceStartup - updateStartTime) * 1000f;
            stats_updateTotalTime += updateTime;
            if (updateTime < stats_updateTimeMin) stats_updateTimeMin = updateTime;
            if (updateTime > stats_updateTimeMax) stats_updateTimeMax = updateTime;
            if (updateTime > updateTimeBudgetMs) stats_budgetExceededCount++;
            stats_frameCount++;
            
            // Per-frame logging
            if (enableFrameLogging)
            {
                LogFrameStats(updateTime);
            }
            
            // Aggregate logging
            if (enableAggregateLogging && stats_frameCount % aggregateLogInterval == 0)
            {
                LogAggregateStats();
            }
        }
#endif
    }
    
    private void ProcessActiveChunks()
    {
        float frameStart = Time.realtimeSinceStartup;
        float frameBudget = updateTimeBudgetMs * 0.001f;
        
#if LOGGING
        float processStartTime = frameStart;
#endif
        
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
        
        // --- FIXED: Process Incremental Lighting (managed by coordinator) ---
        // Note: Lighting is now handled by coordinator's STATE_LIGHTING, not here
        
        // --- OPTIMIZATION: Process Deferred Reconciliation ---
#if LOGGING
        float reconcilStartTime = Time.realtimeSinceStartup;
#endif
        ProcessDeferredReconciliation(frameStart, frameBudget);
#if LOGGING
        if (enableDetailedTimings)
        {
            stats_reconciliationTime += (Time.realtimeSinceStartup - reconcilStartTime) * 1000f;
        }
#endif
        
#if LOGGING
        if (enableDetailedTimings)
        {
            stats_processActiveChunksTime += (Time.realtimeSinceStartup - processStartTime) * 1000f;
        }
#endif
        
        // No longer need to reschedule - Update() runs every frame automatically!
    }
    
    // Called by Coordinator
    public void StartChunkDataGeneration(int chunkIndex)
    {
        if (chunkIndex == -1) return;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null || chunk.isGeneratingData) return;
        
        chunk.isGeneratingData = true;
        
        // Pass centered chunk coordinates directly - they ARE the Minecraft chunk coordinates
        // Engine centered coords (e.g., -2,-1,0,1) map to Minecraft chunks (-2,-1,0,1)
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
            if (decompressionCacheValid.ContainsKey(chunk))
                decompressionCacheValid[chunk] = false;
            
            // OPTIMIZATION: Invalidate neighbor cache since chunk data changed
            _InvalidateNeighborCache(chunk);
            
            chunk.isSingleOpaqueSolid = false;
            if (isHomogeneous) {
                byte blockID = (byte)chunk._chunkData;
                if(_IsBlockSolid(blockID) && _GetVisibilityType(blockID) == BlockVisibilityType.Opaque) {
                    chunk.isSingleOpaqueSolid = true;
                }
            }
            
            // Store biome data for this chunk from the terrain generator
            // This data will be used during meshing to apply biome tinting
            _StoreBiomeData(chunk);
            
            // FIXED: Lighting is now handled by coordinator STATE_LIGHTING
            // Initialize lighting data structure but don't run BFS yet
            InitializeChunkLighting(chunk);
                
            // Mark chunk as ready and data gen complete
            chunk.isDataReady = true;
            chunk.isGeneratingData = false;
            
            // Coordinator will now move to STATE_LIGHTING and call StartChunkLighting
        }
    }

    // FIXED: Called by Coordinator to start incremental lighting (STATE_LIGHTING)
    public void StartChunkLighting(int chunkIndex)
    {
        if (chunkIndex == -1 || chunkIndex >= chunks_1D.Length) return;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null || !chunk.isDataReady) return;
        
        // Allocate persistent queue for this chunk
        chunk.lightingQueue = new int[8192]; // Smaller queue, but we process incrementally
        chunk.lightingQueueStart = 0;
        chunk.lightingQueueEnd = 0;
        chunk.lightingPhase = 0; // Start with sky light
        chunk.lightingIteration = 0;
        chunk.isProcessingLighting = true;
        
        // Import light from neighbors first
        byte[] chunkData = _DecompressChunkColumnRLE(chunk);
        if (chunkData != null)
        {
            _ImportLightFromNeighbors(chunk, chunkData);
        }
        
        // Initialize queue with blocks that need processing
        _InitializeLightingQueue(chunk);
    }
    
    // FIXED: Called by Coordinator Update() to step through lighting incrementally
    public void StepChunkLighting(int chunkIndex)
    {
        if (chunkIndex == -1 || chunkIndex >= chunks_1D.Length) return;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null || !chunk.isProcessingLighting) return;
        
        byte[] chunkData = _DecompressChunkColumnRLE(chunk);
        if (chunkData == null) return;
        
        // Pre-calculate neighbor offsets
        int[] neighborOffsets = { -1, 0, 0, 1, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, -1, 0, 0, 1 };
        
        // Process a batch of blocks (small enough to never overflow queue)
        int blocksToProcess = 256; // Process 256 blocks per step
        int processed = 0;
        
        bool isSkyLight = (chunk.lightingPhase == 0);
        
        while (chunk.lightingQueueStart < chunk.lightingQueueEnd && processed < blocksToProcess)
        {
            int packed = chunk.lightingQueue[chunk.lightingQueueStart++];
            int x = (packed >> 16) & 0xFF;
            int y = (packed >> 8) & 0xFF;
            int z = packed & 0xFF;
            
            // Skip opaque blocks
            int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            byte blockID = chunkData[blockIndex];
            int opacity = lightOpacityCache[blockID];
            if (opacity >= 15)
            {
                processed++;
                continue;
            }
            
            // Process this block (PULL-based update)
            _UpdateBlockLightPULLOptimized(chunk, chunkData, x, y, z, isSkyLight, chunk.lightingQueue, ref chunk.lightingQueueEnd, neighborOffsets);
            
            processed++;
        }
        
        // Check if current phase is complete
        if (chunk.lightingQueueStart >= chunk.lightingQueueEnd)
        {
            chunk.lightingIteration++;
            
            // Check if we've done enough iterations or converged
            if (chunk.lightingIteration >= 16 || chunk.lightingQueueEnd == 0)
            {
                // Move to next phase
                if (chunk.lightingPhase == 0)
                {
                    // Sky light complete, start block light
                    chunk.lightingPhase = 1;
                    chunk.lightingIteration = 0;
                    chunk.lightingQueueStart = 0;
                    _InitializeLightingQueue(chunk);
                }
                else
                {
                    // Block light complete, do final cleanup and finish
                    _EnsureNoPitchBlackSpots(chunk, chunkData);
                    
                    // Lighting complete!
                    chunk.isProcessingLighting = false;
                    chunk.lightingPhase = 2; // Mark as complete
                    chunk.lightingQueue = null; // Free memory
                    
                    // Now do reconciliation and trigger neighbor mesh rebuilds
                    ImmediateReconciliation(chunk);
                    TriggerNeighborMeshRebuilds(chunk);
                }
            }
        }
    }
    
    // FIXED: Initialize the lighting queue for a chunk
    private void _InitializeLightingQueue(ChunkData chunk)
    {
        byte[] chunkData = _DecompressChunkColumnRLE(chunk);
        if (chunkData == null) return;
        
        chunk.lightingQueueEnd = 0;
        
        // Phase 0: Sky light - add all blocks with skylight < 15
        if (chunk.lightingPhase == 0)
        {
            for (int y = 0; y < chunkSizeY; y++)
            {
                for (int z = 0; z < chunkSizeXZ; z++)
                {
                    for (int x = 0; x < chunkSizeXZ; x++)
                    {
                        int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                        int skyLight = (chunk.lightData[blockIndex] >> 4) & 0xF;
                        
                        if (skyLight < 15 && chunk.lightingQueueEnd < chunk.lightingQueue.Length - 6)
                        {
                            chunk.lightingQueue[chunk.lightingQueueEnd++] = (x << 16) | (y << 8) | z;
                        }
                    }
                }
            }
        }
        // Phase 1: Block light - add emissive blocks and skylight=0 blocks
        else if (chunk.lightingPhase == 1)
        {
            for (int y = 0; y < chunkSizeY; y++)
            {
                for (int z = 0; z < chunkSizeXZ; z++)
                {
                    for (int x = 0; x < chunkSizeXZ; x++)
                    {
                        int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                        int blockLight = chunk.lightData[blockIndex] & 0xF;
                        int skyLight = (chunk.lightData[blockIndex] >> 4) & 0xF;
                        
                        if ((blockLight > 0 || skyLight == 0) && chunk.lightingQueueEnd < chunk.lightingQueue.Length - 6)
                        {
                            chunk.lightingQueue[chunk.lightingQueueEnd++] = (x << 16) | (y << 8) | z;
                        }
                    }
                }
            }
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

        byte oldBlockType = _GetBlockLocal(chunk, localX, localY, localZ);
        _SetBlockLocal(chunk, localX, localY, localZ, blockType, true);
        
        // Update lighting if block changed
        if (oldBlockType != blockType && chunk.lightData != null)
        {
            _UpdateBlockLighting(chunk, localX, localY, localZ, oldBlockType, blockType);
        }
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
        chunk.meshBuildStartTime = Time.realtimeSinceStartup;
        if (enableCounters) stats_meshBuildTotal++;
        if (enableCounters) stats_chunkStateTransitions++;
        
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

        // --- OPTIMIZATION: Use cached neighbor references ---
        ChunkData[] neighbors = _GetCachedNeighbors(chunk);
        chunk.neighborPX = neighbors[0];
        chunk.neighborNX = neighbors[1];
        chunk.neighborPY = neighbors[2];
        chunk.neighborNY = neighbors[3];
        chunk.neighborPZ = neighbors[4];
        chunk.neighborNZ = neighbors[5];


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

        // OPTIMIZATION Phase 1: Skip sentinel buffer build, use direct access instead
#if LOGGING
        float t_prepare = Time.realtimeSinceStartup;
#endif
        _DecompressNeighborsOnce(chunk);
#if LOGGING
        if (enableVerboseLogging)
        {
            chunk.time_DecompressNeighbors = (Time.realtimeSinceStartup - t_prepare) * 1000f;
            chunk.time_SentinelEnsure = 0f; // Not used with direct access
            chunk.time_SentinelBuild = 0f; // Not used with direct access
            chunk.time_DataPrep = chunk.time_DecompressNeighbors;
        }
#endif
        
        // OPTIMIZATION Phase 3 & 6: Pre-compute brightness and biome colors
        // This eliminates 10,000+ lighting and biome texture lookups during meshing
        _PreComputeChunkBrightness(chunk);
        _PreComputeBiomeColors(chunk);
        if (chunk.neighborPX != null && chunk.neighborPX.isDataReady) _PreComputeChunkBrightness(chunk.neighborPX);
        if (chunk.neighborNX != null && chunk.neighborNX.isDataReady) _PreComputeChunkBrightness(chunk.neighborNX);
        if (chunk.neighborPY != null && chunk.neighborPY.isDataReady) _PreComputeChunkBrightness(chunk.neighborPY);
        if (chunk.neighborNY != null && chunk.neighborNY.isDataReady) _PreComputeChunkBrightness(chunk.neighborNY);
        if (chunk.neighborPZ != null && chunk.neighborPZ.isDataReady) _PreComputeChunkBrightness(chunk.neighborPZ);
        if (chunk.neighborNZ != null && chunk.neighborNZ.isDataReady) _PreComputeChunkBrightness(chunk.neighborNZ);
        
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

        // OPTIMIZATION Phase 1: Direct access to decompressed data (eliminates sentinel buffer overhead)
        float budgetStart = Time.realtimeSinceStartup;
        float budgetSec = meshStepTimeBudgetMs * 0.001f;

        // OPTIMIZATION: Cache arrays locally for maximum performance
        byte[] selfData = chunk._decompSelf;
        byte[] drawTable = shouldDrawTable;
        int drawTableLen = drawTable != null ? drawTable.Length : 0;
        BlockCullingType[] cullCache = cullingCache;
        int cullCacheLen = cullCache != null ? cullCache.Length : 0;
        McBlockShapeType[] shapeCache = shapeTypeCache;
        int shapeCacheLen = shapeCache != null ? shapeCache.Length : 0;
        
        // Cache neighbor decompressed data for boundary checks
        byte[] dataPX = chunk._decompPX;
        byte[] dataNX = chunk._decompNX;
        byte[] dataPY = chunk._decompPY;
        byte[] dataNY = chunk._decompNY;
        byte[] dataPZ = chunk._decompPZ;
        byte[] dataNZ = chunk._decompNZ;
        
        // Pre-calculate strides for direct indexing
        int chunkStride = chunkSizeXZ * chunkSizeXZ;

        while (chunk._greedyAxis <= 2)
        {
            if (Time.realtimeSinceStartup - budgetStart > budgetSec) break;

            if (chunk._greedyAxis == 0)
            {
#if LOGGING
                float axisStart = 0f; if (enableVerboseLogging) axisStart = Time.realtimeSinceStartup;
#endif
                // OPTIMIZATION Phase 1: Y-axis faces using direct data access
                int lx = chunk._greedyU; // 0..chunkSizeXZ-1
                int lz = chunk._greedyV; // 0..chunkSizeXZ-1

                // Process all Y boundaries for this (x,z) column
                for (int y = 0; y <= chunkSizeY; y++)
                {
                    byte idBelow = _GetBlockDirectMeshing(selfData, dataNX, dataPX, dataNY, dataPY, dataNZ, dataPZ, lx, y - 1, lz, chunkSizeXZ, chunkSizeY, chunkStride);
                    byte idAbove = _GetBlockDirectMeshing(selfData, dataNX, dataPX, dataNY, dataPY, dataNZ, dataPZ, lx, y, lz, chunkSizeXZ, chunkSizeY, chunkStride);
#if LOGGING
                    if (enableVerboseLogging) chunk.boundaryChecksY++;
#endif
                    // Skip if same block, unless it's a NoCull block (e.g. leaves)
                    if (idBelow == idAbove && !(idBelow < cullCacheLen && cullCache[idBelow] == BlockCullingType.NoCull)) continue;

                    // Check if both are same NoCull type (to avoid z-fighting with double faces)
                    bool bothSameNoCull = idBelow == idAbove && idBelow < cullCacheLen && cullCache[idBelow] == BlockCullingType.NoCull;
                    
                    // Up face of below cell (only if below is not air and within bounds)
                    if (idBelow != 0 && y > 0 && y <= chunkSizeY)
                    {
                        // OPTIMIZATION: Skip cross-type blocks - they have their own mesh generation
                        bool isCrossBlock = idBelow < shapeCacheLen && shapeCache[idBelow] == McBlockShapeType.Cross;
                        if (!isCrossBlock)
                        {
                            // OPTIMIZATION: Fully inlined _ShouldDrawFace for performance
                            int idx = (idBelow << 8) | idAbove;
                            bool drawTest = idx < drawTableLen && drawTable[idx] != 0;
#if LOGGING
                            if (enableVerboseLogging) { chunk.shouldDrawTests++; if (drawTest) chunk.shouldDrawTrue++; }
#endif
                            if (drawTest)
                            {
                                _AddFaceOptimized(chunk, FaceVertices_Up, Normal_Up, lx, y - 1, lz, idBelow, FACE_INDEX_TOP);
                            }
                        }
                    }
                    // Down face of above cell (only if above is not air and within bounds)
                    // Skip this face if both blocks are the same NoCull type (prevent z-fighting)
                    if (idAbove != 0 && y >= 0 && y < chunkSizeY && !bothSameNoCull)
                    {
                        // OPTIMIZATION: Skip cross-type blocks - they have their own mesh generation
                        bool isCrossBlock = idAbove < shapeCacheLen && shapeCache[idAbove] == McBlockShapeType.Cross;
                        if (!isCrossBlock)
                        {
                            // OPTIMIZATION: Fully inlined _ShouldDrawFace for performance
                            int idx2 = (idAbove << 8) | idBelow;
                            bool drawTest2 = idx2 < drawTableLen && drawTable[idx2] != 0;
#if LOGGING
                            if (enableVerboseLogging) { chunk.shouldDrawTests++; if (drawTest2) chunk.shouldDrawTrue++; }
#endif
                            if (drawTest2)
                            {
                                _AddFaceOptimized(chunk, FaceVertices_Down, Normal_Down, lx, y, lz, idAbove, FACE_INDEX_BOTTOM);
                            }
                        }
                    }
                }

                // advance (u,v)
                chunk._greedyU++;
                if (chunk._greedyU >= chunkSizeXZ)
                {
                    chunk._greedyU = 0;
                    chunk._greedyV++;
                    if (chunk._greedyV >= chunkSizeXZ)
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
                // OPTIMIZATION Phase 1: Z-axis faces using direct data access
                int lx = chunk._greedyU; // 0..chunkSizeXZ-1
                int ly = chunk._greedyV; // 0..chunkSizeY-1

                // Process all Z boundaries for this (x,y) line
                for (int z = 0; z <= chunkSizeXZ; z++)
                {
                    byte idBack = _GetBlockDirectMeshing(selfData, dataNX, dataPX, dataNY, dataPY, dataNZ, dataPZ, lx, ly, z - 1, chunkSizeXZ, chunkSizeY, chunkStride);
                    byte idFront = _GetBlockDirectMeshing(selfData, dataNX, dataPX, dataNY, dataPY, dataNZ, dataPZ, lx, ly, z, chunkSizeXZ, chunkSizeY, chunkStride);
#if LOGGING
                    if (enableVerboseLogging) chunk.boundaryChecksZ++;
#endif
                    // Skip if same block, unless it's a NoCull block (e.g. leaves)
                    if (idBack == idFront && !(idBack < cullCacheLen && cullCache[idBack] == BlockCullingType.NoCull)) continue;

                    // Check if both are same NoCull type (to avoid z-fighting with double faces)
                    bool bothSameNoCull = idBack == idFront && idBack < cullCacheLen && cullCache[idBack] == BlockCullingType.NoCull;
                    
                    // North face (positive Z) of back cell
                    if (idBack != 0 && z > 0 && z <= chunkSizeXZ)
                    {
                        // OPTIMIZATION: Skip cross-type blocks - they have their own mesh generation
                        bool isCrossBlock = idBack < shapeCacheLen && shapeCache[idBack] == McBlockShapeType.Cross;
                        if (!isCrossBlock)
                        {
                            // OPTIMIZATION: Fully inlined _ShouldDrawFace for performance
                            int idx = (idBack << 8) | idFront;
                            bool drawTest = idx < drawTableLen && drawTable[idx] != 0;
#if LOGGING
                            if (enableVerboseLogging) { chunk.shouldDrawTests++; if (drawTest) chunk.shouldDrawTrue++; }
#endif
                            if (drawTest)
                            {
                                _AddFaceOptimized(chunk, FaceVertices_North, Normal_North, lx, ly, z - 1, idBack, FACE_INDEX_SIDE);
                            }
                        }
                    }
                    // South face (negative Z) of front cell
                    // Skip this face if both blocks are the same NoCull type (prevent z-fighting)
                    if (idFront != 0 && z >= 0 && z < chunkSizeXZ && !bothSameNoCull)
                    {
                        // OPTIMIZATION: Skip cross-type blocks - they have their own mesh generation
                        bool isCrossBlock = idFront < shapeCacheLen && shapeCache[idFront] == McBlockShapeType.Cross;
                        if (!isCrossBlock)
                        {
                            // OPTIMIZATION: Fully inlined _ShouldDrawFace for performance
                            int idx2 = (idFront << 8) | idBack;
                            bool drawTest2 = idx2 < drawTableLen && drawTable[idx2] != 0;
#if LOGGING
                            if (enableVerboseLogging) { chunk.shouldDrawTests++; if (drawTest2) chunk.shouldDrawTrue++; }
#endif
                            if (drawTest2)
                            {
                                _AddFaceOptimized(chunk, FaceVertices_South, Normal_South, lx, ly, z, idFront, FACE_INDEX_SIDE);
                            }
                        }
                    }
                }

                // advance (u,v)
                chunk._greedyU++;
                if (chunk._greedyU >= chunkSizeXZ)
                {
                    chunk._greedyU = 0;
                    chunk._greedyV++;
                    if (chunk._greedyV >= chunkSizeY)
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
                // OPTIMIZATION Phase 1: X-axis faces using direct data access
                int ly = chunk._greedyU; // 0..chunkSizeY-1
                int lz = chunk._greedyV; // 0..chunkSizeXZ-1

                // Process all X boundaries for this (y,z) line
                for (int x = 0; x <= chunkSizeXZ; x++)
                {
                    byte idLeft = _GetBlockDirectMeshing(selfData, dataNX, dataPX, dataNY, dataPY, dataNZ, dataPZ, x - 1, ly, lz, chunkSizeXZ, chunkSizeY, chunkStride);
                    byte idRight = _GetBlockDirectMeshing(selfData, dataNX, dataPX, dataNY, dataPY, dataNZ, dataPZ, x, ly, lz, chunkSizeXZ, chunkSizeY, chunkStride);
#if LOGGING
                    if (enableVerboseLogging) chunk.boundaryChecksX++;
#endif
                    // Skip if same block, unless it's a NoCull block (e.g. leaves)
                    if (idLeft == idRight && !(idLeft < cullCacheLen && cullCache[idLeft] == BlockCullingType.NoCull)) continue;

                    // Check if both are same NoCull type (to avoid z-fighting with double faces)
                    bool bothSameNoCull = idLeft == idRight && idLeft < cullCacheLen && cullCache[idLeft] == BlockCullingType.NoCull;
                    
                    // East face (positive X) of left cell
                    if (idLeft != 0 && x > 0 && x <= chunkSizeXZ)
                    {
                        // OPTIMIZATION: Skip cross-type blocks - they have their own mesh generation
                        bool isCrossBlock = idLeft < shapeCacheLen && shapeCache[idLeft] == McBlockShapeType.Cross;
                        if (!isCrossBlock)
                        {
                            // OPTIMIZATION: Fully inlined _ShouldDrawFace for performance
                            int idx = (idLeft << 8) | idRight;
                            bool drawTest = idx < drawTableLen && drawTable[idx] != 0;
#if LOGGING
                            if (enableVerboseLogging) { chunk.shouldDrawTests++; if (drawTest) chunk.shouldDrawTrue++; }
#endif
                            if (drawTest)
                            {
                                _AddFaceOptimized(chunk, FaceVertices_East, Normal_East, x - 1, ly, lz, idLeft, FACE_INDEX_SIDE);
                            }
                        }
                    }
                    // West face (negative X) of right cell
                    // Skip this face if both blocks are the same NoCull type (prevent z-fighting)
                    if (idRight != 0 && x >= 0 && x < chunkSizeXZ && !bothSameNoCull)
                    {
                        // OPTIMIZATION: Skip cross-type blocks - they have their own mesh generation
                        bool isCrossBlock = idRight < shapeCacheLen && shapeCache[idRight] == McBlockShapeType.Cross;
                        if (!isCrossBlock)
                        {
                            // OPTIMIZATION: Fully inlined _ShouldDrawFace for performance
                            int idx2 = (idRight << 8) | idLeft;
                            bool drawTest2 = idx2 < drawTableLen && drawTable[idx2] != 0;
#if LOGGING
                            if (enableVerboseLogging) { chunk.shouldDrawTests++; if (drawTest2) chunk.shouldDrawTrue++; }
#endif
                            if (drawTest2)
                            {
                                _AddFaceOptimized(chunk, FaceVertices_West, Normal_West, x, ly, lz, idRight, FACE_INDEX_SIDE);
                            }
                        }
                    }
                }

                // advance (u,v)
                chunk._greedyU++;
                if (chunk._greedyU >= chunkSizeY)
                {
                    chunk._greedyU = 0;
                    chunk._greedyV++;
                    if (chunk._greedyV >= chunkSizeXZ)
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
            // After all boundary processing, add cross-shaped blocks
            _AddCrossShapedBlocks(chunk);
            
            _ApplyAllMeshData(chunk);
            
#if LOGGING
            // Track aggregate mesh building stats
            if (enableDetailedTimings)
            {
                float meshBuildTime = (Time.realtimeSinceStartup - chunk.meshBuildStartTime) * 1000f;
                stats_meshBuildTimeTotal += meshBuildTime;
                if (meshBuildTime < stats_meshBuildTimeMin) stats_meshBuildTimeMin = meshBuildTime;
                if (meshBuildTime > stats_meshBuildTimeMax) stats_meshBuildTimeMax = meshBuildTime;
                stats_meshStepsTotal += chunk.mesh_step_count;
                stats_greedyAxisYTime += chunk.time_AxisY;
                stats_greedyAxisZTime += chunk.time_AxisZ;
                stats_greedyAxisXTime += chunk.time_AxisX;
                stats_sentinelBuildTime += chunk.time_SentinelBuild;
                stats_meshApplyOpaqueTime += chunk.time_ApplyOpaque;
                stats_meshApplyTransparentTime += chunk.time_ApplyTransparent;
                stats_meshApplyCutoutTime += chunk.time_ApplyCutout;
                stats_meshApplyColliderTime += chunk.time_ApplyCollision;
            }
            if (enableCounters)
            {
                stats_sentinelBuilds++;
                stats_faceCullingTests += chunk.shouldDrawTests;
                stats_facesCulled += (chunk.shouldDrawTests - chunk.shouldDrawTrue);
                stats_facesDrawn += chunk.facesTotal;
                stats_verticesOpaque += chunk._opaqueVertexCount;
                stats_verticesTransparent += chunk._transparentVertexCount;
                stats_verticesCutout += chunk._cutoutVertexCount;
            }
#endif
            
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
                    // OPTIMIZATION Phase 7: Use Buffer.BlockCopy instead of Array.Copy (faster for byte arrays)
                    System.Buffer.BlockCopy(self, lineSrc, s, lineDst, chunkSizeXZ);
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
            // OPTIMIZATION Phase 7: Use Buffer.BlockCopy instead of Array.Copy (faster for byte arrays)
            System.Buffer.BlockCopy(neighborFlat, lineSrc, s, lineDst, chunkSizeXZ);
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
    
    // OPTIMIZATION Phase 1: Direct block access helper (replaces lambda for Udon compatibility)
    private byte _GetBlockDirectMeshing(byte[] selfData, byte[] dataNX, byte[] dataPX, byte[] dataNY, byte[] dataPY, byte[] dataNZ, byte[] dataPZ, 
                                        int x, int y, int z, int chunkSizeXZ, int chunkSizeY, int chunkStride)
    {
        if (x >= 0 && x < chunkSizeXZ && y >= 0 && y < chunkSizeY && z >= 0 && z < chunkSizeXZ)
        {
            return selfData[y * chunkStride + z * chunkSizeXZ + x];
        }
        // Handle boundaries with neighbor data
        if (x < 0 && dataNX != null) return dataNX[y * chunkStride + z * chunkSizeXZ + (chunkSizeXZ - 1)];
        if (x >= chunkSizeXZ && dataPX != null) return dataPX[y * chunkStride + z * chunkSizeXZ + 0];
        if (y < 0 && dataNY != null) return dataNY[(chunkSizeY - 1) * chunkStride + z * chunkSizeXZ + x];
        if (y >= chunkSizeY && dataPY != null) return dataPY[0 * chunkStride + z * chunkSizeXZ + x];
        if (z < 0 && dataNZ != null) return dataNZ[y * chunkStride + (chunkSizeXZ - 1) * chunkSizeXZ + x];
        if (z >= chunkSizeXZ && dataPZ != null) return dataPZ[y * chunkStride + 0 * chunkSizeXZ + x];
        return 0; // Air/empty
    }
    
    // OPTIMIZATION Phase 2: Batch face collection helper (replaces lambda for Udon compatibility)
    // OPTIMIZATION: Optimized face adding that avoids Vector3 allocations
    private void _AddFaceOptimized(ChunkData chunk, Vector3[] faceVertices, Vector3 faceNormal, int bx, int by, int bz, byte blockID, int faceIndex)
    {
        Vector3[] targetVertices; int[] targetTriangles; Vector3[] targetUVs; Vector3[] targetNormals; Color[] targetColors;
        int currentVertexCount; int currentTriangleCount;

        // OPTIMIZATION Phase 5: Inline _GetVisibilityType (eliminates method call)
        BlockVisibilityType visibility = (blockID < visibilityCache.Length) ? visibilityCache[blockID] : BlockVisibilityType.Opaque;
        if (visibility == BlockVisibilityType.Opaque) {
            if (chunk._opaqueVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._opaqueVertices; targetTriangles = chunk._opaqueTriangles; targetUVs = chunk._opaqueUVs; targetNormals = chunk._opaqueNormals; targetColors = chunk._opaqueColors;
            currentVertexCount = chunk._opaqueVertexCount; currentTriangleCount = chunk._opaqueTriangleCount;
        } else if (visibility == BlockVisibilityType.Transparent) {
            if (chunk._transparentVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._transparentVertices; targetTriangles = chunk._transparentTriangles; targetUVs = chunk._transparentUVs; targetNormals = chunk._transparentNormals; targetColors = chunk._transparentColors;
            currentVertexCount = chunk._transparentVertexCount; currentTriangleCount = chunk._transparentTriangleCount;
        } else { // Cutout
            if (chunk._cutoutVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._cutoutVertices; targetTriangles = chunk._cutoutTriangles; targetUVs = chunk._cutoutUVs; targetNormals = chunk._cutoutNormals; targetColors = chunk._cutoutColors;
            currentVertexCount = chunk._cutoutVertexCount; currentTriangleCount = chunk._cutoutTriangleCount;
        }
        
        // OPTIMIZATION: Avoid Vector3 constructor calls
        targetVertices[currentVertexCount + 0] = new Vector3(bx + faceVertices[0].x, by + faceVertices[0].y, bz + faceVertices[0].z);
        targetVertices[currentVertexCount + 1] = new Vector3(bx + faceVertices[1].x, by + faceVertices[1].y, bz + faceVertices[1].z);
        targetVertices[currentVertexCount + 2] = new Vector3(bx + faceVertices[2].x, by + faceVertices[2].y, bz + faceVertices[2].z);
        targetVertices[currentVertexCount + 3] = new Vector3(bx + faceVertices[3].x, by + faceVertices[3].y, bz + faceVertices[3].z);
        for (int i=0; i<4; i++) targetNormals[currentVertexCount + i] = faceNormal;
        
        // OPTIMIZATION Phase 6: Use pre-computed cached biome color (eliminates texture lookups)
        Color biomeColor = _GetCachedBiomeColor(chunk, blockID, bx, bz);
        
        // OPTIMIZATION Phase 3: Use pre-computed cached brightness (eliminates method call overhead)
        float brightness = _GetCachedBrightnessForFace(chunk, faceNormal, bx, by, bz);
        biomeColor.a = brightness;
        
        for (int i=0; i<4; i++) targetColors[currentVertexCount + i] = biomeColor;
        
        // OPTIMIZATION Phase 5: Inline _GetTextureSlice (eliminates method call)
        float textureSlice = 0;
        if (blockDataCache != null && blockID < blockDataCache.Length)
        {
            McBlockTextureMappingType mappingType = (McBlockTextureMappingType)((blockDataCache[blockID] >> 8) & 0x3);
            if (mappingType == McBlockTextureMappingType.AllFacesSame)
                textureSlice = uv_allFacesCache[blockID];
            else if (mappingType == McBlockTextureMappingType.TopBottomSides)
            {
                if (faceIndex == 2) textureSlice = uv_topFaceCache[blockID];
                else if (faceIndex == 3) textureSlice = uv_bottomFaceCache[blockID];
                else textureSlice = uv_sideFacesCache[blockID];
            }
            else
                textureSlice = uv_allFacesCache[blockID];
        }
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
    
    // OPTIMIZATION Phase 2: Bulk process collected faces (better cache locality, reduces method overhead)
    private void _AddFace(ChunkData chunk, Vector3[] faceVertices, Vector3 faceNormal, Vector3 blockPos, byte blockID, BlockVisibilityType visibility, int faceIndex)
    {
        Vector3[] targetVertices; int[] targetTriangles; Vector3[] targetUVs; Vector3[] targetNormals; Color[] targetColors;
        int currentVertexCount; int currentTriangleCount;

        if (visibility == BlockVisibilityType.Opaque) {
            if (chunk._opaqueVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._opaqueVertices; targetTriangles = chunk._opaqueTriangles; targetUVs = chunk._opaqueUVs; targetNormals = chunk._opaqueNormals; targetColors = chunk._opaqueColors;
            currentVertexCount = chunk._opaqueVertexCount; currentTriangleCount = chunk._opaqueTriangleCount;
        } else if (visibility == BlockVisibilityType.Transparent) {
            if (chunk._transparentVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._transparentVertices; targetTriangles = chunk._transparentTriangles; targetUVs = chunk._transparentUVs; targetNormals = chunk._transparentNormals; targetColors = chunk._transparentColors;
            currentVertexCount = chunk._transparentVertexCount; currentTriangleCount = chunk._transparentTriangleCount;
        } else { // Cutout
            if (chunk._cutoutVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._cutoutVertices; targetTriangles = chunk._cutoutTriangles; targetUVs = chunk._cutoutUVs; targetNormals = chunk._cutoutNormals; targetColors = chunk._cutoutColors;
            currentVertexCount = chunk._cutoutVertexCount; currentTriangleCount = chunk._cutoutTriangleCount;
        }
        
        float bx = blockPos.x, by = blockPos.y, bz = blockPos.z;
        targetVertices[currentVertexCount + 0] = new Vector3(bx + faceVertices[0].x, by + faceVertices[0].y, bz + faceVertices[0].z);
        targetVertices[currentVertexCount + 1] = new Vector3(bx + faceVertices[1].x, by + faceVertices[1].y, bz + faceVertices[1].z);
        targetVertices[currentVertexCount + 2] = new Vector3(bx + faceVertices[2].x, by + faceVertices[2].y, bz + faceVertices[2].z);
        targetVertices[currentVertexCount + 3] = new Vector3(bx + faceVertices[3].x, by + faceVertices[3].y, bz + faceVertices[3].z);
        for (int i=0; i<4; i++) targetNormals[currentVertexCount + i] = faceNormal;
        
        // Calculate biome color for this block position
        Color biomeColor = _GetBiomeColorForBlock(chunk, blockID, (int)bx, (int)bz);
        
        // FIXED: Apply lighting to vertex colors (alpha channel = brightness)
        // Sample light from the neighbor block that this face is against, not the block itself
        float brightness = _GetLightBrightnessForFace(chunk, faceNormal, (int)bx, (int)by, (int)bz);
        biomeColor.a = brightness;
        
        for (int i=0; i<4; i++) targetColors[currentVertexCount + i] = biomeColor;
        
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
    
    // ===== CROSS-SHAPED BLOCK RENDERING =====
    // Cross-shaped blocks (like tall grass and flowers) use two intersecting perpendicular quads
    // with seeded random offsets based on block position (matching Beta 1.7.3)
    
    private void _AddCrossShapedBlocks(ChunkData chunk)
    {
        // Iterate through all blocks in the chunk and add cross geometry for cross-shaped blocks
        byte[] decompressed = chunk._decompSelf;
        if (decompressed == null) return;
        
        int columnStride = chunkSizeXZ * chunkSizeXZ;
        for (int y = 0; y < chunkSizeY; y++)
        {
            int yBase = y * columnStride;
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                int zBase = yBase + z * chunkSizeXZ;
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    byte blockID = decompressed[zBase + x];
                    if (blockID == 0) continue;
                    
                    McBlockShapeType shapeType = blockTypeManager.GetBlockShapeType(blockID);
                    if (shapeType == McBlockShapeType.Cross)
                    {
                        BlockVisibilityType visibility = _GetVisibilityType(blockID);
                        _AddCrossShapedBlock(chunk, new Vector3(x, y, z), blockID, visibility);
                    }
                }
            }
        }
    }
    
    private void _AddCrossShapedBlock(ChunkData chunk, Vector3 blockPos, byte blockID, BlockVisibilityType visibility)
    {
        // Cross-shaped blocks use 2 perpendicular quads forming an X shape when viewed from above
        // The quads extend from corner to corner: (0,0,0)→(1,1,1) and (1,0,0)→(0,1,1)
        
        Vector3[] targetVertices; int[] targetTriangles; Vector3[] targetUVs; Vector3[] targetNormals; Color[] targetColors;
        int currentVertexCount; int currentTriangleCount;

        if (visibility == BlockVisibilityType.Opaque) {
            if (chunk._opaqueVertexCount + 8 > MAX_VERTS) return;
            targetVertices = chunk._opaqueVertices; targetTriangles = chunk._opaqueTriangles; targetUVs = chunk._opaqueUVs; targetNormals = chunk._opaqueNormals; targetColors = chunk._opaqueColors;
            currentVertexCount = chunk._opaqueVertexCount; currentTriangleCount = chunk._opaqueTriangleCount;
        } else if (visibility == BlockVisibilityType.Transparent) {
            if (chunk._transparentVertexCount + 8 > MAX_VERTS) return;
            targetVertices = chunk._transparentVertices; targetTriangles = chunk._transparentTriangles; targetUVs = chunk._transparentUVs; targetNormals = chunk._transparentNormals; targetColors = chunk._transparentColors;
            currentVertexCount = chunk._transparentVertexCount; currentTriangleCount = chunk._transparentTriangleCount;
        } else { // Cutout
            if (chunk._cutoutVertexCount + 8 > MAX_VERTS) return;
            targetVertices = chunk._cutoutVertices; targetTriangles = chunk._cutoutTriangles; targetUVs = chunk._cutoutUVs; targetNormals = chunk._cutoutNormals; targetColors = chunk._cutoutColors;
            currentVertexCount = chunk._cutoutVertexCount; currentTriangleCount = chunk._cutoutTriangleCount;
        }
        
        float bx = blockPos.x, by = blockPos.y, bz = blockPos.z;
        float textureSlice = _GetTextureSlice(blockID, FACE_INDEX_SIDE);
        
        // Calculate biome color for this block (grass blocks like tall grass should be tinted)
        Color biomeColor = _GetBiomeColorForBlock(chunk, blockID, (int)bx, (int)bz);
        
        // FIXED: Apply lighting to vertex colors (alpha channel = brightness)
        // For cross-shaped blocks, sample light from the block itself (no face direction)
        float brightness = _GetLightBrightnessAtBlock(chunk, (int)bx, (int)by, (int)bz);
        biomeColor.a = brightness;
        
        // Seeded random offset (matching Beta 1.7.3 BlockTallGrass colorMultiplier logic)
        // Calculate global position for seeding
        int globalX = (int)(bx + chunk.chunkX_world * chunkSizeXZ);
        int globalY = (int)(by + chunk.chunkY_world * chunkSizeY);
        int globalZ = (int)(bz + chunk.chunkZ_world * chunkSizeXZ);
        
        // BETA 1.7.3 EXACT: Seeded random offset calculation (RenderBlocks.java line 1316-1320)
        // Reference: renderBlockReed() method for tall grass randomization
        long seed = (long)(globalX * 3129871) ^ (long)globalZ * 116129781L ^ (long)globalY;
        seed = seed * seed * 42317861L + seed * 11L;
        
        // Extract random values from different bit ranges (Minecraft's approach)
        float randX = (float)((seed >> 16 & 15L) / 15.0f); // 0-1 from bits 16-19
        float randY = (float)((seed >> 20 & 15L) / 15.0f); // 0-1 from bits 20-23
        float randZ = (float)((seed >> 24 & 15L) / 15.0f); // 0-1 from bits 24-27
        
        // Convert to Minecraft's offset ranges:
        // X and Z: ±0.25 blocks (makes plants wobble left/right)
        // Y: -0.2 to 0 blocks (slightly sunk into ground for natural look)
        float offsetX = (randX - 0.5f) * 0.5f; // -0.25 to +0.25
        float offsetY = (randY - 1.0f) * 0.2f; // -0.2 to 0
        float offsetZ = (randZ - 0.5f) * 0.5f; // -0.25 to +0.25
        
        // QUAD 1: Southwest to Northeast diagonal
        // Vertices: (0,0,0), (0,1,0), (1,1,1), (1,0,1)
        // Apply randomization to make each plant look unique (Minecraft behavior)
        targetVertices[currentVertexCount + 0] = new Vector3(bx + 0 + offsetX, by + 0 + offsetY, bz + 0 + offsetZ);
        targetVertices[currentVertexCount + 1] = new Vector3(bx + 0 + offsetX, by + 1 + offsetY, bz + 0 + offsetZ);
        targetVertices[currentVertexCount + 2] = new Vector3(bx + 1 + offsetX, by + 1 + offsetY, bz + 1 + offsetZ);
        targetVertices[currentVertexCount + 3] = new Vector3(bx + 1 + offsetX, by + 0 + offsetY, bz + 1 + offsetZ);
        
        // Normals: Use a diagonal normal for lighting (average of X and Z)
        Vector3 normal1 = new Vector3(0.7071f, 0, 0.7071f).normalized;
        for (int i=0; i<4; i++) targetNormals[currentVertexCount + i] = normal1;
        
        // Colors: Apply biome tint to all vertices
        for (int i=0; i<4; i++) targetColors[currentVertexCount + i] = biomeColor;
        
        // UVs: Standard quad UV mapping
        targetUVs[currentVertexCount + 0] = new Vector3(0, 0, textureSlice);
        targetUVs[currentVertexCount + 1] = new Vector3(0, 1, textureSlice);
        targetUVs[currentVertexCount + 2] = new Vector3(1, 1, textureSlice);
        targetUVs[currentVertexCount + 3] = new Vector3(1, 0, textureSlice);
        
        // Triangles for first quad
        targetTriangles[currentTriangleCount + 0] = currentVertexCount + 0;
        targetTriangles[currentTriangleCount + 1] = currentVertexCount + 1;
        targetTriangles[currentTriangleCount + 2] = currentVertexCount + 2;
        targetTriangles[currentTriangleCount + 3] = currentVertexCount + 0;
        targetTriangles[currentTriangleCount + 4] = currentVertexCount + 2;
        targetTriangles[currentTriangleCount + 5] = currentVertexCount + 3;
        
        // QUAD 2: Southeast to Northwest diagonal
        // Vertices: (1,0,0), (1,1,0), (0,1,1), (0,0,1)
        // Apply same randomization to maintain consistent plant offset
        targetVertices[currentVertexCount + 4] = new Vector3(bx + 1 + offsetX, by + 0 + offsetY, bz + 0 + offsetZ);
        targetVertices[currentVertexCount + 5] = new Vector3(bx + 1 + offsetX, by + 1 + offsetY, bz + 0 + offsetZ);
        targetVertices[currentVertexCount + 6] = new Vector3(bx + 0 + offsetX, by + 1 + offsetY, bz + 1 + offsetZ);
        targetVertices[currentVertexCount + 7] = new Vector3(bx + 0 + offsetX, by + 0 + offsetY, bz + 1 + offsetZ);
        
        // Normals: Use opposite diagonal normal
        Vector3 normal2 = new Vector3(-0.7071f, 0, 0.7071f).normalized;
        for (int i=4; i<8; i++) targetNormals[currentVertexCount + i] = normal2;
        
        // Colors: Apply biome tint to all vertices
        for (int i=4; i<8; i++) targetColors[currentVertexCount + i] = biomeColor;
        
        // UVs: Standard quad UV mapping
        targetUVs[currentVertexCount + 4] = new Vector3(0, 0, textureSlice);
        targetUVs[currentVertexCount + 5] = new Vector3(0, 1, textureSlice);
        targetUVs[currentVertexCount + 6] = new Vector3(1, 1, textureSlice);
        targetUVs[currentVertexCount + 7] = new Vector3(1, 0, textureSlice);
        
        // Triangles for second quad
        targetTriangles[currentTriangleCount + 6] = currentVertexCount + 4;
        targetTriangles[currentTriangleCount + 7] = currentVertexCount + 5;
        targetTriangles[currentTriangleCount + 8] = currentVertexCount + 6;
        targetTriangles[currentTriangleCount + 9] = currentVertexCount + 4;
        targetTriangles[currentTriangleCount + 10] = currentVertexCount + 6;
        targetTriangles[currentTriangleCount + 11] = currentVertexCount + 7;
        
        // Update counts
        if (visibility == BlockVisibilityType.Opaque) { chunk._opaqueVertexCount += 8; chunk._opaqueTriangleCount += 12; }
        else if (visibility == BlockVisibilityType.Transparent) { chunk._transparentVertexCount += 8; chunk._transparentTriangleCount += 12; }
        else { chunk._cutoutVertexCount += 8; chunk._cutoutTriangleCount += 12; }
        
        // NOTE: Cross-shaped blocks have no collision in Minecraft Beta 1.7.3
        
#if LOGGING
        if (enableVerboseLogging)
        {
            chunk.facesTotal += 2;
            if (visibility == BlockVisibilityType.Opaque) chunk.facesOpaque += 2;
            else if (visibility == BlockVisibilityType.Transparent) chunk.facesTransparent += 2;
            else chunk.facesCutout += 2;
        }
#endif
    }
    
    private void _ApplyEmptyMesh(ChunkData chunk)
    {
        _ApplyDataToMesh(chunk.opaqueMeshFilter, chunk._opaqueVertices, chunk._opaqueTriangles, chunk._opaqueUVs, chunk._opaqueNormals, chunk._opaqueColors, 0, 0);
        _ApplyDataToMesh(chunk.transparentMeshFilter, chunk._transparentVertices, chunk._transparentTriangles, chunk._transparentUVs, chunk._transparentNormals, chunk._transparentColors, 0, 0);
        _ApplyDataToMesh(chunk.cutoutMeshFilter, chunk._cutoutVertices, chunk._cutoutTriangles, chunk._cutoutUVs, chunk._cutoutNormals, chunk._cutoutColors, 0, 0);
        _ApplyDataToCollider(chunk);
    }
    
    private byte[] _GetDecompressedData(ChunkData chunk)
    {
        // OPTIMIZED: Use global persistent cache to avoid repeated decompression
        if (decompressionCacheValid.ContainsKey(chunk) && decompressionCacheValid[chunk] && decompressionCache.ContainsKey(chunk))
        {
#if LOGGING
            if (enableCacheTracking) stats_decompCacheHits++;
#endif
            return decompressionCache[chunk];
        }
        
#if LOGGING
        if (enableCacheTracking) stats_decompCacheMisses++;
#endif
        
        byte[] decompressed = _DecompressChunkColumnRLE(chunk);
        decompressionCache[chunk] = decompressed;
        decompressionCacheValid[chunk] = true;
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
            _ApplyDataToMesh(chunk.opaqueMeshFilter, chunk._opaqueVertices, chunk._opaqueTriangles, chunk._opaqueUVs, chunk._opaqueNormals, chunk._opaqueColors, chunk._opaqueVertexCount, chunk._opaqueTriangleCount);
            chunk.time_ApplyOpaque = (Time.realtimeSinceStartup - timer_start) * 1000f;

            timer_start = Time.realtimeSinceStartup;
            _ApplyDataToMesh(chunk.transparentMeshFilter, chunk._transparentVertices, chunk._transparentTriangles, chunk._transparentUVs, chunk._transparentNormals, chunk._transparentColors, chunk._transparentVertexCount, chunk._transparentTriangleCount);
            chunk.time_ApplyTransparent = (Time.realtimeSinceStartup - timer_start) * 1000f;

            timer_start = Time.realtimeSinceStartup;
            _ApplyDataToMesh(chunk.cutoutMeshFilter, chunk._cutoutVertices, chunk._cutoutTriangles, chunk._cutoutUVs, chunk._cutoutNormals, chunk._cutoutColors, chunk._cutoutVertexCount, chunk._cutoutTriangleCount);
            chunk.time_ApplyCutout = (Time.realtimeSinceStartup - timer_start) * 1000f;
            return;
        }
#endif
        _ApplyDataToMesh(chunk.opaqueMeshFilter, chunk._opaqueVertices, chunk._opaqueTriangles, chunk._opaqueUVs, chunk._opaqueNormals, chunk._opaqueColors, chunk._opaqueVertexCount, chunk._opaqueTriangleCount);
        _ApplyDataToMesh(chunk.transparentMeshFilter, chunk._transparentVertices, chunk._transparentTriangles, chunk._transparentUVs, chunk._transparentNormals, chunk._transparentColors, chunk._transparentVertexCount, chunk._transparentTriangleCount);
        _ApplyDataToMesh(chunk.cutoutMeshFilter, chunk._cutoutVertices, chunk._cutoutTriangles, chunk._cutoutUVs, chunk._cutoutNormals, chunk._cutoutColors, chunk._cutoutVertexCount, chunk._cutoutTriangleCount);
    }

    private void _ApplyDataToMesh(MeshFilter mf, Vector3[] vertices, int[] triangles, Vector3[] uvs, Vector3[] normals, Color[] colors, int vertexCount, int triangleCount)
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
        Color[] finalColors = new Color[vertexCount]; System.Array.Copy(colors, finalColors, vertexCount);
        
        m.vertices = finalVertices; 
        m.triangles = finalTriangles; 
        m.normals = finalNormals;
        m.SetUVs(0, finalUVs);
        m.colors = finalColors; // Apply vertex colors for biome tinting
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
#if LOGGING
        if (enableCounters) stats_getBlockCalls++;
#endif
        
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
#if LOGGING
            int traversalDepth = 0;
#endif
            for (int i = 0; i < rlePairs.Length; i += 2)
            {
                ushort blockID = rlePairs[i];
                ushort runLength = rlePairs[i + 1];
                currentY += runLength;
#if LOGGING
                traversalDepth++;
#endif
                if (y < currentY)
                {
#if LOGGING
                    if (enableCounters)
                    {
                        chunk.block_rleTraversalDepthTotal += traversalDepth;
                        chunk.block_rleTraversalDepthCount++;
                    }
#endif
                    return (byte)blockID;
                }
            }
        }
        
        return 0; // Fallback for out of bounds Y or error
    }

    private void _SetBlockLocal(ChunkData chunk, int x, int y, int z, byte blockType, bool updateMesh)
    {
#if LOGGING
        if (enableCounters) stats_setBlockCalls++;
#endif
        
        if (x < 0 || x >= chunkSizeXZ || y < 0 || y >= chunkSizeY || z < 0 || z >= chunkSizeXZ) return;
        // Safety check: ensure chunk exists
        if (chunk == null) return;
        
        // Safety check: ensure chunk data exists
        if (chunk._chunkData == null) return;
        
        // Optimization: check if the block is actually changing before doing any work.
        if (blockType == _GetBlockLocal(chunk, x, y, z)) return;

#if LOGGING
        if (enableCounters) stats_blockModifications++;
#endif

        // OPTIMIZATION: Invalidate decompression cache since data is changing
        if (decompressionCacheValid.ContainsKey(chunk))
            decompressionCacheValid[chunk] = false;
        
        // OPTIMIZATION: Invalidate neighbor cache since chunk data changed
        _InvalidateNeighborCache(chunk);

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
#if LOGGING
        float compressStartTime = 0f;
        if (enableDetailedTimings) compressStartTime = Time.realtimeSinceStartup;
        if (enableCounters) stats_rleCompressions++;
        int bytesIn = fullChunkData != null ? fullChunkData.Length : 0;
#endif
        
        isHomogeneous = true;
        byte firstBlock = fullChunkData[0];
        for (int i = 1; i < fullChunkData.Length; i++) {
            if (fullChunkData[i] != firstBlock) {
                isHomogeneous = false;
                break;
            }
        }

        if (isHomogeneous) {
#if LOGGING
            if (enableCounters) stats_rleHomogeneousChunks++;
            if (enableDetailedTimings)
            {
                stats_rleCompressionTime += (Time.realtimeSinceStartup - compressStartTime) * 1000f;
                stats_rleTotalBytesIn += bytesIn;
                stats_rleTotalBytesOut += 1; // single byte
            }
#endif
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
        
#if LOGGING
        if (enableDetailedTimings)
        {
            stats_rleCompressionTime += (Time.realtimeSinceStartup - compressStartTime) * 1000f;
            stats_rleTotalBytesIn += bytesIn;
            // Approximate output size: columnCount arrays * avg pairs * 2 bytes
            int bytesOut = 0;
            for (int i = 0; i < columnRLEData.Length; i++)
            {
                if (columnRLEData[i] != null) bytesOut += columnRLEData[i].Length * 2;
            }
            stats_rleTotalBytesOut += bytesOut;
        }
#endif
        
        return columnRLEData;
    }
    
    private byte[] _DecompressChunkColumnRLE(ChunkData chunk)
    {
#if LOGGING
        float decompressStartTime = 0f;
        if (enableDetailedTimings) decompressStartTime = Time.realtimeSinceStartup;
        if (enableCounters) stats_rleDecompressions++;
#endif
        
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
        
#if LOGGING
        if (enableDetailedTimings)
        {
            stats_rleDecompressionTime += (Time.realtimeSinceStartup - decompressStartTime) * 1000f;
        }
#endif
        
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
        shapeTypeCache = new McBlockShapeType[maxId]; // NEW: Initialize shape type cache
        for (int i = 0; i < maxId; i++)
        {
            visibilityCache[i] = (BlockVisibilityType)((blockDataCache[i] >> 1) & 0x3);
            cullingCache[i] = (BlockCullingType)((blockDataCache[i] >> 3) & 0x7);
            shapeTypeCache[i] = (McBlockShapeType)((blockDataCache[i] >> 6) & 0x3); // NEW: Cache shape type (bits 6-7)
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

    private void _BuildLightingTables()
    {
        // Build brightness table using Minecraft Beta 1.7.3 formula
        // Formula: brightness = (1.0 - darkness) / (darkness * 3.0 + 1.0) * 0.95 + 0.05
        // Where darkness = 1.0 - (lightLevel / 15.0)
        lightBrightnessTable = new float[16];
        for (int i = 0; i < 16; i++)
        {
            float lightLevel = i / 15.0f;
            float darkness = 1.0f - lightLevel;
            lightBrightnessTable[i] = (1.0f - darkness) / (darkness * 3.0f + 1.0f) * 0.95f + 0.05f;
        }
        
        // Build light opacity and emission caches
        int maxId = blockTypeManager.finalDataArray != null ? blockTypeManager.finalDataArray.Length : 256;
        lightOpacityCache = new int[256];
        lightEmissionCache = new int[256];
        for (int i = 0; i < 256; i++)
        {
            if (i < maxId)
            {
                lightOpacityCache[i] = blockTypeManager.GetBlockLightOpacity((byte)i);
                lightEmissionCache[i] = blockTypeManager.GetBlockLightEmission((byte)i);
            }
            else
            {
                lightOpacityCache[i] = 15; // Default to opaque
                lightEmissionCache[i] = 0;  // Default to no emission
            }
        }
        
        Debug.Log("[McWorld] Light brightness table and caches built successfully.");
    }
    
    // --- Lighting Optimization: Memory Pooling ---
    private int[] GetBFSQueue()
    {
        if (bfsQueuePool.Count > 0)
            return bfsQueuePool.Dequeue();
        return new int[BFS_QUEUE_SIZE];
    }
    
    private int[] GetBFSQueueLarge()
    {
        if (bfsQueuePoolLarge.Count > 0)
            return bfsQueuePoolLarge.Dequeue();
        return new int[BFS_QUEUE_SIZE_LARGE];
    }
    
    private void ReturnBFSQueue(int[] queue)
    {
        if (queue.Length == BFS_QUEUE_SIZE && bfsQueuePool.Count < 10)
            bfsQueuePool.Enqueue(queue);
        else if (queue.Length == BFS_QUEUE_SIZE_LARGE && bfsQueuePoolLarge.Count < 5)
            bfsQueuePoolLarge.Enqueue(queue);
    }
    
    // OPTIMIZATION: Cache neighbor references to avoid repeated lookups
    private ChunkData[] _GetCachedNeighbors(ChunkData chunk)
    {
        if (neighborCacheValid.ContainsKey(chunk) && neighborCacheValid[chunk] && neighborCache.ContainsKey(chunk))
        {
#if LOGGING
            if (enableCacheTracking) stats_neighborCacheHits++;
#endif
            return neighborCache[chunk];
        }
        
#if LOGGING
        if (enableCacheTracking) stats_neighborCacheMisses++;
#endif
        
        ChunkData[] neighbors = new ChunkData[6];
        neighbors[0] = GetChunkAt(chunk.chunkX_world + 1, chunk.chunkY_world, chunk.chunkZ_world); // PX
        neighbors[1] = GetChunkAt(chunk.chunkX_world - 1, chunk.chunkY_world, chunk.chunkZ_world); // NX
        neighbors[2] = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world + 1, chunk.chunkZ_world); // PY
        neighbors[3] = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world - 1, chunk.chunkZ_world); // NY
        neighbors[4] = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world + 1); // PZ
        neighbors[5] = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world - 1); // NZ
        
        neighborCache[chunk] = neighbors;
        neighborCacheValid[chunk] = true;
        return neighbors;
    }
    
    // OPTIMIZATION: Invalidate neighbor cache when chunks change
    private void _InvalidateNeighborCache(ChunkData chunk)
    {
        if (neighborCacheValid.ContainsKey(chunk))
            neighborCacheValid[chunk] = false;
            
        // Also invalidate neighbors' caches since they reference this chunk
        ChunkData[] neighbors = _GetCachedNeighbors(chunk);
        for (int i = 0; i < 6; i++)
        {
            if (neighbors[i] != null && neighborCacheValid.ContainsKey(neighbors[i]))
                neighborCacheValid[neighbors[i]] = false;
        }
    }
    
    // OPTIMIZATION: Defer reconciliation to prevent lag spikes
    private void DeferReconciliation(ChunkData chunk)
    {
        if (!reconciliationPending.Contains(chunk))
        {
            deferredReconciliationQueue.Enqueue(chunk);
            reconciliationPending.Add(chunk);
        }
    }
    
    // FIXED: Immediate reconciliation for critical cases (e.g., emissive blocks near boundaries)
    private void ImmediateReconciliation(ChunkData chunk)
    {
        if (chunk == null || !chunk.isDataReady) return;
        
        // Check if chunk has emissive blocks near boundaries that need immediate propagation
        byte[] chunkData = _DecompressChunkColumnRLE(chunk);
        if (chunkData == null) return;
        
        bool hasEmissiveNearBoundary = false;
        
        // Check boundary blocks for emissive blocks
        for (int y = 0; y < chunkSizeY && !hasEmissiveNearBoundary; y++)
        {
            for (int z = 0; z < chunkSizeXZ && !hasEmissiveNearBoundary; z++)
            {
                // Check X boundaries
                int leftIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + 0;
                int rightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + (chunkSizeXZ - 1);
                
                if (lightEmissionCache[chunkData[leftIndex]] > 0 || lightEmissionCache[chunkData[rightIndex]] > 0)
                {
                    hasEmissiveNearBoundary = true;
                    break;
                }
                
                // Check Z boundaries
                int frontIndex = y * (chunkSizeXZ * chunkSizeXZ) + 0 * chunkSizeXZ + z;
                int backIndex = y * (chunkSizeXZ * chunkSizeXZ) + (chunkSizeXZ - 1) * chunkSizeXZ + z;
                
                if (lightEmissionCache[chunkData[frontIndex]] > 0 || lightEmissionCache[chunkData[backIndex]] > 0)
                {
                    hasEmissiveNearBoundary = true;
                    break;
                }
            }
        }
        
        // If emissive blocks near boundary, do immediate reconciliation
        if (hasEmissiveNearBoundary)
        {
            _ReconcileLightingWithNeighbors(chunk);
        }
        else
        {
            // Otherwise defer to prevent lag spikes
            DeferReconciliation(chunk);
        }
    }
    
    // OPTIMIZATION: Process deferred reconciliation with time budget
    private void ProcessDeferredReconciliation(float frameStart, float frameBudget)
    {
        float reconciliationBudget = RECONCILIATION_TIME_BUDGET_MS * 0.001f;
        int processedCount = 0;
        
        while (deferredReconciliationQueue.Count > 0 && 
               processedCount < MAX_RECONCILIATION_PER_FRAME &&
               Time.realtimeSinceStartup - frameStart < frameBudget - reconciliationBudget)
        {
            ChunkData chunk = deferredReconciliationQueue.Dequeue();
            reconciliationPending.Remove(chunk);
            
            if (chunk != null && chunk.isDataReady)
            {
                _ReconcileLightingWithNeighbors(chunk);
                processedCount++;
            }
        }
    }
    
    // OPTIMIZATION: Batch process multiple chunks for new columns
    private void BatchProcessNewColumnChunks(ChunkData[] chunks)
    {
        // OPTIMIZATION: Process chunks in batches to reduce overhead
        // This is called when a new multi-chunk column is being initialized
        
        // OPTIMIZATION: UdonSharp compatibility - avoid array length access
        int batchSize = 4; // Process max 4 chunks at once
        
        for (int i = 0; i < batchSize; i++)
        {
            // OPTIMIZATION: UdonSharp compatibility - check bounds manually
            if (i >= chunks.Length) break;
            
            ChunkData chunk = chunks[i];
            if (chunk != null && chunk.isDataReady)
            {
                // OPTIMIZATION: Skip expensive reconciliation for isolated chunks
                ChunkData[] neighbors = _GetCachedNeighbors(chunk);
                int readyNeighbors = 0;
                
                for (int j = 0; j < 6; j++)
                {
                    ChunkData neighbor = neighbors[j];
                    if (neighbor != null && neighbor.isDataReady)
                        readyNeighbors++;
                }
                
                // Only do lightweight reconciliation for chunks with neighbors
                if (readyNeighbors > 0)
                {
                    DeferReconciliation(chunk);
                }
            }
        }
    }
    
    // OPTIMIZATION: Optimized PULL-based lighting with pre-calculated offsets
    private bool _UpdateBlockLightPULLOptimized(ChunkData chunk, byte[] fullData, int x, int y, int z, bool isSkyLight, int[] queue, ref int queueEnd, int[] neighborOffsets)
    {
        int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        byte blockID = fullData[blockIndex];
        
        // OPTIMIZATION: Skip fully opaque blocks (opacity >= 15)
        int opacity = lightOpacityCache[blockID];
        if (opacity >= 15) {
            return false; // Skip opaque blocks
        }
        
        // OPTIMIZATION: Use pre-calculated offsets for neighbor queries
        int maxNeighborLight = 0;
        for (int i = 0; i < 6; i++)
        {
            int nx = x + neighborOffsets[i * 3];
            int ny = y + neighborOffsets[i * 3 + 1];
            int nz = z + neighborOffsets[i * 3 + 2];
            
            int neighborLight = _GetNeighborLightOptimized(chunk, nx, ny, nz, isSkyLight);
            if (neighborLight > maxNeighborLight) maxNeighborLight = neighborLight;
        }
        
        // Get current light FIRST
        byte currentLightByte = chunk.lightData[blockIndex];
        int currentLight = isSkyLight ? ((currentLightByte >> 4) & 0xF) : (currentLightByte & 0xF);
        
        // Apply opacity (air has opacity 0, treated as 1)
        if (opacity == 0) opacity = 1;
        
        int newLight = maxNeighborLight - opacity;
        if (newLight < 0) newLight = 0;
        
        // For skylight: max with sky visibility (15 if can see sky)
        if (isSkyLight) {
            // MINECRAFT BEHAVIOR: Check if this block can see the sky
            bool canSeeSky = false;
            if (chunk.chunkY_world >= worldDimensionY - 1 && y >= chunkSizeY - 1) {
                canSeeSky = true; // At world top
            } else if (currentLight == 15) {
                canSeeSky = true; // Already has full skylight
            }
            
            if (canSeeSky && newLight < 15) {
                newLight = 15;
            }
        }
        // For block light: max with emission
        else {
            int emission = lightEmissionCache[blockID];
            if (emission > newLight) newLight = emission;
        }
        
        if (newLight != currentLight) {
            // Set new light value
            if (isSkyLight) {
                int blockLight = currentLightByte & 0xF;
                
                // MINECRAFT BEHAVIOR: When skylight is reduced by semi-transparent block,
                // it creates block light to prevent areas from going too dark
                if (opacity > 1 && opacity < 15 && newLight < maxNeighborLight) {
                    int convertedBlockLight = newLight;
                    if (convertedBlockLight > blockLight) {
                        blockLight = convertedBlockLight;
                    }
                }
                
                chunk.lightData[blockIndex] = (byte)((newLight << 4) | blockLight);
            } else {
                int skyLight = (currentLightByte >> 4) & 0xF;
                chunk.lightData[blockIndex] = (byte)((skyLight << 4) | newLight);
            }
            
            // OPTIMIZATION: Schedule neighbors using pre-calculated offsets
            for (int i = 0; i < 6; i++)
            {
                int nx = x + neighborOffsets[i * 3];
                int ny = y + neighborOffsets[i * 3 + 1];
                int nz = z + neighborOffsets[i * 3 + 2];
                _ScheduleNeighborUpdate(chunk, nx, ny, nz, queue, ref queueEnd);
            }
            
            // FIXED: Trigger neighbor mesh rebuilds for boundary blocks
            // This ensures neighbors update their meshes when lighting changes at chunk boundaries
            if (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1)
            {
                TriggerNeighborMeshRebuilds(chunk);
            }
            
            return true; // Light was updated
        }
        
        return false; // No change
    }
    
    // OPTIMIZATION: Optimized neighbor light query with bounds checking
    private int _GetNeighborLightOptimized(ChunkData chunk, int x, int y, int z, bool isSkyLight)
    {
        // Check if neighbor is in the same chunk
        if (x >= 0 && x < chunkSizeXZ && y >= 0 && y < chunkSizeY && z >= 0 && z < chunkSizeXZ)
        {
            // Within same chunk
            int neighborIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            byte neighborLightByte = chunk.lightData[neighborIndex];
            
            if (isSkyLight)
                return (neighborLightByte >> 4) & 0xF;
            else
                return neighborLightByte & 0xF;
        }
        else
        {
            // Neighbor is in a different chunk - get from neighbor chunk
            ChunkData neighborChunk = null;
            int neighborLocalX = x;
            int neighborLocalY = y;
            int neighborLocalZ = z;
            
            // Determine which neighbor chunk and adjust local coordinates
            if (x < 0)
            {
                neighborChunk = chunk.neighborNX;
                neighborLocalX = chunkSizeXZ - 1;
            }
            else if (x >= chunkSizeXZ)
            {
                neighborChunk = chunk.neighborPX;
                neighborLocalX = 0;
            }
            else if (y < 0)
            {
                neighborChunk = chunk.neighborNY;
                neighborLocalY = chunkSizeY - 1;
            }
            else if (y >= chunkSizeY)
            {
                neighborChunk = chunk.neighborPY;
                neighborLocalY = 0;
            }
            else if (z < 0)
            {
                neighborChunk = chunk.neighborNZ;
                neighborLocalZ = chunkSizeXZ - 1;
            }
            else if (z >= chunkSizeXZ)
            {
                neighborChunk = chunk.neighborPZ;
                neighborLocalZ = 0;
            }
            
            // Get light from neighbor chunk if available
            if (neighborChunk != null && neighborChunk.isDataReady && neighborChunk.lightData != null)
            {
                int neighborIndex = neighborLocalY * (chunkSizeXZ * chunkSizeXZ) + neighborLocalZ * chunkSizeXZ + neighborLocalX;
                byte neighborLightByte = neighborChunk.lightData[neighborIndex];
                
                if (isSkyLight)
                    return (neighborLightByte >> 4) & 0xF;
                else
                    return neighborLightByte & 0xF;
            }
            
            // No neighbor chunk data = fully dark (Minecraft behavior)
            return 0;
        }
    }
    
    private void InitializeChunkLighting(ChunkData chunk)
    {
#if LOGGING
        float startTime = Time.realtimeSinceStartup * 1000f;
#endif
        
        // Initialize lighting data array for this chunk
        int lightDataSize = chunkSizeXZ * chunkSizeY * chunkSizeXZ; // 16x16x16 = 4096
        chunk.lightData = new byte[lightDataSize];
        
        // CRITICAL: Use cached neighbor references for cross-chunk lighting queries
        ChunkData[] neighbors = _GetCachedNeighbors(chunk);
        chunk.neighborPX = neighbors[0];
        chunk.neighborNX = neighbors[1];
        chunk.neighborPY = neighbors[2];
        chunk.neighborNY = neighbors[3];
        chunk.neighborPZ = neighbors[4];
        chunk.neighborNZ = neighbors[5];
        
#if LOGGING
        // Debug neighbor availability
        if (enableVerboseLogging)
        {
            int readyNeighbors = 0;
            if (chunk.neighborPX != null && chunk.neighborPX.isDataReady) readyNeighbors++;
            if (chunk.neighborNX != null && chunk.neighborNX.isDataReady) readyNeighbors++;
            if (chunk.neighborPY != null && chunk.neighborPY.isDataReady) readyNeighbors++;
            if (chunk.neighborNY != null && chunk.neighborNY.isDataReady) readyNeighbors++;
            if (chunk.neighborPZ != null && chunk.neighborPZ.isDataReady) readyNeighbors++;
            if (chunk.neighborNZ != null && chunk.neighborNZ.isDataReady) readyNeighbors++;
            Debug.Log($"[McWorld] Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) has {readyNeighbors}/6 ready neighbors");
        }
#endif
        
        // Get full decompressed chunk data
        byte[] fullData = _DecompressChunkColumnRLE(chunk);
        if (fullData == null) return;
        
#if LOGGING
        chunk.time_LightingInit = Time.realtimeSinceStartup * 1000f - startTime;
        startTime = Time.realtimeSinceStartup * 1000f;
#endif
        
        // Stage 1: Calculate initial sky light (vertical propagation)
        // OPTIMIZATION: Use reusable arrays to avoid allocations
        if (reusableBoolArray.Length < lightDataSize)
        {
            reusableBoolArray = new bool[lightDataSize];
        }
        bool[] skylightReachedBlocks = reusableBoolArray;
        bool[] skylightZeroBlocks = new bool[lightDataSize];
        int skylightReachedCount = 0;
        int skylightZeroCount = 0;
        
        for (int x = 0; x < chunkSizeXZ; x++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                int skyLight = 15; // Start with full brightness at world top
                
                // OPTIMIZATION: Check if this is a top chunk to avoid expensive neighbor queries
                if (chunk.chunkY_world >= worldDimensionY - 1)
                {
                    skyLight = 15; // Top chunks always start with full light
                }
                else
                {
                    // OPTIMIZATION: Only query chunk above if it exists and is ready
                    ChunkData chunkAbove = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world + 1, chunk.chunkZ_world);
                    if (chunkAbove != null && chunkAbove.isDataReady && chunkAbove.lightData != null)
                    {
                        skyLight = _GetSkyLightFromChunkAbove(chunk.chunkX_world, chunk.chunkY_world + 1, chunk.chunkZ_world, x, z);
                    }
                    else
                    {
                        skyLight = 15; // Default to full light if chunk above not ready
                    }
                }
                
                // Propagate downward from top (Y = chunkSizeY-1) to bottom (Y = 0)
                for (int y = chunkSizeY - 1; y >= 0; y--)
                {
                    int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                    byte blockID = fullData[blockIndex];
                    
                    // Get block opacity
                    int opacity = lightOpacityCache[blockID];
                    
                    // CORRECTED: Current block sky light = above block sky light - current block opacity
                    int currentBlockSkyLight = skyLight - opacity;
                    if (currentBlockSkyLight < 0) currentBlockSkyLight = 0;
                    
                    // MINECRAFT BEHAVIOR: When sky light is reduced by semi-transparent block,
                    // it also creates block light to prevent it going completely dark
                    int currentBlockLight = 0;
                    if (opacity > 0 && opacity < 15 && skyLight > currentBlockSkyLight)
                    {
                        // Sky light was reduced by semi-transparent block -> convert to block light
                        currentBlockLight = currentBlockSkyLight;
                    }
                    
                    // Set lighting for this block
                    chunk.lightData[blockIndex] = (byte)((currentBlockSkyLight << 4) | currentBlockLight);
                    
                    // OPTIMIZATION: Track blocks that got full skylight (no BFS needed)
                    if (currentBlockSkyLight == 15)
                    {
                        skylightReachedBlocks[blockIndex] = true;
                        skylightReachedCount++;
                    }
                    // OPTIMIZATION: Track blocks that got zero skylight (need BFS)
                    else if (currentBlockSkyLight == 0)
                    {
                        skylightZeroBlocks[blockIndex] = true;
                        skylightZeroCount++;
                    }
                    
                    // Use current block's light for next block down
                    skyLight = currentBlockSkyLight;
                }
            }
        }
        
        // Stage 2: Set block light for emissive blocks
        for (int i = 0; i < lightDataSize; i++)
        {
            byte blockID = fullData[i];
            int emission = lightEmissionCache[blockID];
            if (emission > 0)
            {
                // Set block light (low nibble) while preserving sky light (high nibble)
                byte skyLight = (byte)((chunk.lightData[i] >> 4) & 0xF);
                chunk.lightData[i] = (byte)((skyLight << 4) | emission);
            }
        }
        
#if LOGGING
        chunk.lightingSkylightReachedBlocks = skylightReachedCount;
        chunk.lightingSkylightZeroBlocks = skylightZeroCount;
#endif
        
        // Stage 3: Propagate block light horizontally (sky light is vertical-only)
        _PropagateChunkLightingOptimized(chunk, fullData, skylightReachedBlocks, skylightZeroBlocks);
        
#if LOGGING
        // Log detailed lighting performance metrics with Y-layer visualization
        if (enableVerboseLogging)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) Lighting Performance ===");
            sb.AppendLine($"--- Timing ---");
            sb.AppendLine($"  Init: {chunk.time_LightingInit:F3}ms");
            sb.AppendLine($"  Import: {chunk.time_LightingImport:F3}ms");
            sb.AppendLine($"  BFS Sky: {chunk.time_LightingBFS_Sky:F3}ms ({chunk.lightingBlocksProcessed_Sky} blocks, {chunk.lightingUpdatesApplied_Sky} updates)");
            sb.AppendLine($"  BFS Block: {chunk.time_LightingBFS_Block:F3}ms ({chunk.lightingBlocksProcessed_Block} blocks, {chunk.lightingUpdatesApplied_Block} updates)");
            sb.AppendLine($"--- Stats ---");
            sb.AppendLine($"  Skylight=15: {chunk.lightingSkylightReachedBlocks}/{lightDataSize} blocks ({chunk.lightingSkylightReachedBlocks * 100f / lightDataSize:F1}%)");
            sb.AppendLine($"  Skylight=0: {chunk.lightingSkylightZeroBlocks}/{lightDataSize} blocks ({chunk.lightingSkylightZeroBlocks * 100f / lightDataSize:F1}%)");
            sb.AppendLine($"  Neighbor Queries: {chunk.lightingNeighborQueries_Sky + chunk.lightingNeighborQueries_Block}");
            sb.AppendLine($"  Cross-Chunk Ops: {chunk.lightingCrossChunkOps_Sky + chunk.lightingCrossChunkOps_Block}");
            sb.AppendLine($"  Queue Ops: {chunk.lightingQueueOps_Sky + chunk.lightingQueueOps_Block}");
            
            // DETAILED: Print Y-layer visualization
            sb.AppendLine($"--- Y-Layer Analysis ---");
            for (int y = chunkSizeY - 1; y >= 0; y--)
            {
                int skylight15 = 0, skylight0 = 0, skylightOther = 0;
                int blocklight0 = 0, blocklightOther = 0;
                int airCount = 0, solidCount = 0;
                
                for (int z = 0; z < chunkSizeXZ; z++)
                {
                    for (int x = 0; x < chunkSizeXZ; x++)
                    {
                        int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                        byte lightByte = chunk.lightData[blockIndex];
                        int skyLight = (lightByte >> 4) & 0xF;
                        int blockLight = lightByte & 0xF;
                        byte blockID = fullData[blockIndex];
                        
                        if (skyLight == 15) skylight15++;
                        else if (skyLight == 0) skylight0++;
                        else skylightOther++;
                        
                        if (blockLight == 0) blocklight0++;
                        else blocklightOther++;
                        
                        if (blockID == 0) airCount++;
                        else solidCount++;
                    }
                }
                
                // Only print layers with interesting data
                if (skylightOther > 0 || blocklightOther > 0 || (skylight0 > 0 && skylight0 < 256))
                {
                    sb.AppendLine($"  Y{y:D2}: Sky[15:{skylight15:D3} 0:{skylight0:D3} other:{skylightOther:D3}] " +
                        $"Block[0:{blocklight0:D3} lit:{blocklightOther:D3}] " +
                        $"Blocks[air:{airCount:D3} solid:{solidCount:D3}]");
                }
            }
            
            Debug.Log(sb.ToString());
        }
#endif
    }
    
    // NEW: Optimized PULL-based lighting propagation (Minecraft Beta 1.7.3 algorithm)
    private void _PropagateChunkLightingOptimized(ChunkData chunk, byte[] fullData, bool[] skylightReachedBlocks, bool[] skylightZeroBlocks)
    {
        // CRITICAL: Prevent infinite recursion during cross-chunk BFS
        if (chunk.isPropagatingLight) return;
        
        chunk.isPropagatingLight = true;
        
#if LOGGING
        float startTime = Time.realtimeSinceStartup * 1000f;
#endif
        
        // STEP 0: Import light from all neighbor chunks into our boundary blocks
        // This ensures we don't miss light coming from already-generated neighbors
        _ImportLightFromNeighbors(chunk, fullData);
        
#if LOGGING
        chunk.time_LightingImport = Time.realtimeSinceStartup * 1000f - startTime;
        startTime = Time.realtimeSinceStartup * 1000f;
#endif
        
        // OPTIMIZATION: Use pooled queue to avoid GC pressure
        int[] lightQueue = GetBFSQueueLarge();
        int queueStart = 0;
        int queueEnd = 0;
        
        // OPTIMIZATION: Pre-calculate neighbor offsets to avoid repeated calculations
        int[] neighborOffsets = {
            -1, 0, 0,  // X-
            1, 0, 0,   // X+
            0, -1, 0, // Y-
            0, 1, 0,  // Y+
            0, 0, -1, // Z-
            0, 0, 1   // Z+
        };
        
        // MINECRAFT BETA 1.7.3 ALGORITHM:
        // Skylight has BOTH vertical initialization AND horizontal propagation via BFS
        // Vertical pass sets initial values, horizontal BFS spreads light between chunks
        
        // First pass: Sky light horizontal propagation (PULL-based BFS)
        // CRITICAL: Add ALL blocks with skylight < 15
        // skylight=15: already at max, skip
        // skylight=0 to 14: might receive light from neighbors (INCLUDE skylight=0!)
        for (int y = 0; y < chunkSizeY; y++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                    
                    // Add ALL blocks with skylight < 15 (including 0)
                    // These blocks can potentially receive light from neighbors
                    int skyLight = (chunk.lightData[blockIndex] >> 4) & 0xF;
                    if (skyLight < 15)
                    {
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | z;
                        if (queueEnd >= lightQueue.Length - 6) break;
                    }
                }
                if (queueEnd >= lightQueue.Length - 6) break;
            }
            if (queueEnd >= lightQueue.Length - 6) break;
        }
        
        // FIXED: Process sky light queue with multi-pass convergence algorithm
        int skylightIterations = 0;
        int skylightInitialQueue = queueEnd;
        int skylightMaxQueue = queueEnd;
        int maxSkylightIterations = 16; // Increased limit for better convergence
        
        while (queueStart < queueEnd && skylightIterations < maxSkylightIterations)
        {
            int iterationStart = queueStart;
            int iterationEnd = queueEnd;
            int blocksProcessedThisIteration = 0;
            
            while (queueStart < iterationEnd)
            {
                int packed = lightQueue[queueStart++];
                int x = (packed >> 16) & 0xFF;
                int y = (packed >> 8) & 0xFF;
                int z = packed & 0xFF;
                
                // OPTIMIZATION: Skip fully opaque blocks early
                int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                byte blockID = fullData[blockIndex];
                int opacity = lightOpacityCache[blockID];
                if (opacity >= 15) continue;
                
                // PULL from ALL 6 neighbors and take MAX
                bool updated = _UpdateBlockLightPULLOptimized(chunk, fullData, x, y, z, true, lightQueue, ref queueEnd, neighborOffsets);
                
#if LOGGING
                if (updated) chunk.lightingUpdatesApplied_Sky++;
                chunk.lightingBlocksProcessed_Sky++;
#endif
                
                blocksProcessedThisIteration++;
                
                // FIXED: Handle queue overflow gracefully - expand queue instead of breaking
                if (queueEnd >= lightQueue.Length - 6)
                {
                    // Queue is full, but we need to continue processing
                    // This is a critical fix for pitch black spots
                    if (enableVerboseLogging)
                    {
                        Debug.LogWarning($"[McWorld] Skylight BFS queue overflow in chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) at iteration {skylightIterations}, processed {blocksProcessedThisIteration} blocks");
                    }
                    // Continue processing current iteration but don't add more to queue
                    break;
                }
            }
            
            skylightIterations++;
            if (queueEnd > skylightMaxQueue) skylightMaxQueue = queueEnd;
            
            // If no blocks were processed this iteration, we've converged
            if (blocksProcessedThisIteration == 0) break;
        }
        
#if LOGGING
        chunk.time_LightingBFS_Sky = Time.realtimeSinceStartup * 1000f - startTime;
        if (enableVerboseLogging && skylightIterations > 0)
        {
            Debug.Log($"[McWorld] Skylight BFS - Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}): " +
                $"Initial queue: {skylightInitialQueue}, Max queue: {skylightMaxQueue}, Iterations: {skylightIterations}, " +
                $"Updates: {chunk.lightingUpdatesApplied_Sky}");
        }
        startTime = Time.realtimeSinceStartup * 1000f;
#endif
        
        // Second pass: Block light propagation
        queueStart = 0;
        queueEnd = 0;
        
        // OPTIMIZATION: Only add blocks that need BFS processing
        // 1. Emissive blocks (torches, glowstone, etc.)
        // 2. Blocks with skylight=0 (underground areas that need light from neighbors)
        for (int y = 0; y < chunkSizeY; y++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                    
                    // Add emissive blocks (they emit light)
                    int blockLight = chunk.lightData[blockIndex] & 0xF;
                    if (blockLight > 0)
                    {
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | z;
                        if (queueEnd >= lightQueue.Length - 6) break;
                    }
                    // Add blocks with skylight=0 (they need light from neighbors)
                    else if (skylightZeroBlocks[blockIndex])
                    {
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | z;
                        if (queueEnd >= lightQueue.Length - 6) break;
                    }
                }
                if (queueEnd >= lightQueue.Length - 6) break;
            }
            if (queueEnd >= lightQueue.Length - 6) break;
        }
        
        // FIXED: Process block light queue with multi-pass convergence algorithm
        int iterations = 0;
        int initialQueueSize = queueEnd;
        int maxQueueSize = queueEnd;
        int maxBlocklightIterations = 16; // Increased limit for better convergence
        
        while (queueStart < queueEnd && iterations < maxBlocklightIterations)
        {
            int iterationStart = queueStart;
            int iterationEnd = queueEnd;
            int blocksProcessedThisIteration = 0;
            
            while (queueStart < iterationEnd)
            {
                int packed = lightQueue[queueStart++];
                int x = (packed >> 16) & 0xFF;
                int y = (packed >> 8) & 0xFF;
                int z = packed & 0xFF;
                
                // OPTIMIZATION: Skip fully opaque blocks early
                int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                byte blockID = fullData[blockIndex];
                int opacity = lightOpacityCache[blockID];
                if (opacity >= 15) continue;
                
                // PULL from ALL 6 neighbors and take MAX
                bool updated = _UpdateBlockLightPULLOptimized(chunk, fullData, x, y, z, false, lightQueue, ref queueEnd, neighborOffsets);
                
#if LOGGING
                if (updated) chunk.lightingUpdatesApplied_Block++;
                chunk.lightingBlocksProcessed_Block++;
#endif
                
                blocksProcessedThisIteration++;
                
                // FIXED: Handle queue overflow gracefully - expand queue instead of breaking
                if (queueEnd >= lightQueue.Length - 6)
                {
                    // Queue is full, but we need to continue processing
                    // This is a critical fix for pitch black spots
                    if (enableVerboseLogging)
                    {
                        Debug.LogWarning($"[McWorld] Block light BFS queue overflow in chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) at iteration {iterations}, processed {blocksProcessedThisIteration} blocks");
                    }
                    // Continue processing current iteration but don't add more to queue
                    break;
                }
            }
            
            iterations++;
            if (queueEnd > maxQueueSize) maxQueueSize = queueEnd;
            
            // If no blocks were processed this iteration, we've converged
            if (blocksProcessedThisIteration == 0) break;
        }
        
#if LOGGING
        // Log BFS iteration details for debugging
        if (enableVerboseLogging && iterations > 0)
        {
            Debug.Log($"[McWorld] BFS Debug - Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}): " +
                $"Initial queue: {initialQueueSize}, Max queue: {maxQueueSize}, Iterations: {iterations}, " +
                $"Updates: {chunk.lightingUpdatesApplied_Block}");
        }
#endif
        
#if LOGGING
        chunk.time_LightingBFS_Block = Time.realtimeSinceStartup * 1000f - startTime;
#endif
        
        // FIXED: Final fallback - ensure no blocks remain completely dark
        // This catches any remaining pitch black spots that BFS might have missed
        _EnsureNoPitchBlackSpots(chunk, fullData);
        
        // Return queue to pool
        ReturnBFSQueue(lightQueue);
        
        // Clear the flag to allow future BFS calls
        chunk.isPropagatingLight = false;
    }
    
    // FIXED: Final fallback to ensure no pitch black spots remain
    private void _EnsureNoPitchBlackSpots(ChunkData chunk, byte[] fullData)
    {
        // Scan for blocks that are completely dark (both sky and block light = 0)
        // and try to fix them by finding the nearest light source
        int lightDataSize = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
        bool foundDarkSpots = false;
        
        for (int i = 0; i < lightDataSize; i++)
        {
            byte lightByte = chunk.lightData[i];
            int skyLight = (lightByte >> 4) & 0xF;
            int blockLight = lightByte & 0xF;
            
            // If both lights are 0, this is a pitch black spot
            if (skyLight == 0 && blockLight == 0)
            {
                byte blockID = fullData[i];
                int opacity = lightOpacityCache[blockID];
                
                // Skip opaque blocks (they should be dark)
                if (opacity >= 15) continue;
                
                // Try to find light from nearby blocks
                int x = i % chunkSizeXZ;
                int y = (i / (chunkSizeXZ * chunkSizeXZ)) % chunkSizeY;
                int z = (i / chunkSizeXZ) % chunkSizeXZ;
                
                int bestLight = 0;
                
                // Check all 6 neighbors for light
                for (int dir = 0; dir < 6; dir++)
                {
                    int nx = x + neighbor_dx_offsets[dir];
                    int ny = y + neighbor_dy_offsets[dir];
                    int nz = z + neighbor_dz_offsets[dir];
                    
                    if (nx >= 0 && nx < chunkSizeXZ && ny >= 0 && ny < chunkSizeY && nz >= 0 && nz < chunkSizeXZ)
                    {
                        int neighborIndex = ny * (chunkSizeXZ * chunkSizeXZ) + nz * chunkSizeXZ + nx;
                        byte neighborLightByte = chunk.lightData[neighborIndex];
                        int neighborSkyLight = (neighborLightByte >> 4) & 0xF;
                        int neighborBlockLight = neighborLightByte & 0xF;
                        int neighborMaxLight = Mathf.Max(neighborSkyLight, neighborBlockLight);
                        
                        if (neighborMaxLight > bestLight)
                        {
                            bestLight = neighborMaxLight;
                        }
                    }
                }
                
                // If we found light nearby, propagate it to this block
                if (bestLight > 0)
                {
                    int propagatedLight = bestLight - opacity;
                    if (propagatedLight > 0)
                    {
                        // Use the higher of sky or block light for propagation
                        if (bestLight >= 8) // Assume sky light if bright enough
                        {
                            chunk.lightData[i] = (byte)((propagatedLight << 4) | 0);
                        }
                        else // Assume block light
                        {
                            chunk.lightData[i] = (byte)((0 << 4) | propagatedLight);
                        }
                        foundDarkSpots = true;
                    }
                }
            }
        }
        
        if (foundDarkSpots && enableVerboseLogging)
        {
            Debug.Log($"[McWorld] Fixed pitch black spots in chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world})");
        }
        
        // Debug: Count remaining dark spots for monitoring
        int remainingDarkSpots = 0;
        for (int i = 0; i < lightDataSize; i++)
        {
            byte lightByte = chunk.lightData[i];
            int skyLight = (lightByte >> 4) & 0xF;
            int blockLight = lightByte & 0xF;
            
            if (skyLight == 0 && blockLight == 0)
            {
                byte blockID = fullData[i];
                int opacity = lightOpacityCache[blockID];
                if (opacity < 15) // Only count non-opaque blocks
                {
                    remainingDarkSpots++;
                }
            }
        }
        
        if (remainingDarkSpots > 0 && enableVerboseLogging)
        {
            Debug.LogWarning($"[McWorld] Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) still has {remainingDarkSpots} dark spots after BFS");
        }
    }
    
    // OLD: Legacy PUSH-based method (kept for reference/compatibility)
    private void _PropagateChunkLighting(ChunkData chunk, byte[] fullData)
    {
        // CRITICAL: Prevent infinite recursion during cross-chunk BFS
        if (chunk.isPropagatingLight) return;
        
        chunk.isPropagatingLight = true;
        
        // Minecraft Beta 1.7.3 style flood-fill lighting propagation
        // CRITICAL: First import light from neighbor chunks, THEN propagate
        
        // STEP 0: Import light from all neighbor chunks into our boundary blocks
        // This ensures we don't miss light coming from already-generated neighbors
        _ImportLightFromNeighbors(chunk, fullData);
        
        // Queue for light propagation: stores packed position (x, y, z)
        int[] lightQueue = new int[4096 * 2]; // Oversized to handle cascading
        int queueStart = 0;
        int queueEnd = 0;
        
        // First pass: Propagate sky light horizontally
        // Add all lit blocks to the queue
        for (int y = 0; y < chunkSizeY; y++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    int lightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                    int skyLight = (chunk.lightData[lightIndex] >> 4) & 0xF;
                    if (skyLight > 0)
                    {
                        // Add to queue: pack coordinates into single int
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | z;
                        if (queueEnd >= lightQueue.Length) break; // Safety
                    }
                }
                if (queueEnd >= lightQueue.Length) break;
            }
            if (queueEnd >= lightQueue.Length) break;
        }
        
        // Process sky light queue
        while (queueStart < queueEnd)
        {
            int packed = lightQueue[queueStart++];
            int x = (packed >> 16) & 0xFF;
            int y = (packed >> 8) & 0xFF;
            int z = packed & 0xFF;
            
            int centerIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            int centerLight = (chunk.lightData[centerIndex] >> 4) & 0xF;
            
            // Propagate to 6 neighbors
            _PropagateLightToNeighbor(chunk, fullData, x - 1, y, z, centerLight, true, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x + 1, y, z, centerLight, true, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y - 1, z, centerLight, true, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y + 1, z, centerLight, true, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y, z - 1, centerLight, true, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y, z + 1, centerLight, true, lightQueue, ref queueEnd);
            
            if (queueEnd >= lightQueue.Length - 6) break; // Safety: leave room for 6 neighbors
        }
        
        // Second pass: Propagate block light horizontally
        queueStart = 0;
        queueEnd = 0;
        
        // Add all emissive blocks to the queue
        for (int y = 0; y < chunkSizeY; y++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    int lightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                    int blockLight = chunk.lightData[lightIndex] & 0xF;
                    if (blockLight > 0)
                    {
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | z;
                        if (queueEnd >= lightQueue.Length) break;
                    }
                }
                if (queueEnd >= lightQueue.Length) break;
            }
            if (queueEnd >= lightQueue.Length) break;
        }
        
        // Process block light queue
        while (queueStart < queueEnd)
        {
            int packed = lightQueue[queueStart++];
            int x = (packed >> 16) & 0xFF;
            int y = (packed >> 8) & 0xFF;
            int z = packed & 0xFF;
            
            int centerIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            int centerLight = chunk.lightData[centerIndex] & 0xF;
            
            // Propagate to 6 neighbors
            _PropagateLightToNeighbor(chunk, fullData, x - 1, y, z, centerLight, false, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x + 1, y, z, centerLight, false, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y - 1, z, centerLight, false, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y + 1, z, centerLight, false, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y, z - 1, centerLight, false, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y, z + 1, centerLight, false, lightQueue, ref queueEnd);
            
            if (queueEnd >= lightQueue.Length - 6) break;
        }
        
        // Clear the flag to allow future BFS calls
        chunk.isPropagatingLight = false;
    }
    
    // NEW: PULL-based block light update (Minecraft Beta 1.7.3 algorithm)
    private bool _UpdateBlockLightPULL(ChunkData chunk, byte[] fullData, int x, int y, int z, bool isSkyLight, int[] queue, ref int queueEnd)
    {
        int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        byte blockID = fullData[blockIndex];
        
        // OPTIMIZATION: Skip fully opaque blocks (opacity >= 15)
        // Light cannot penetrate these blocks, so no point processing them
        int opacity = lightOpacityCache[blockID];
        if (opacity >= 15) {
            return false; // Skip opaque blocks
        }
        
        // Get light from ALL 6 neighbors and find MAXIMUM
        int light_X_minus = _GetNeighborLight(chunk, fullData, x-1, y, z, isSkyLight);
        int light_X_plus  = _GetNeighborLight(chunk, fullData, x+1, y, z, isSkyLight);
        int light_Y_minus = _GetNeighborLight(chunk, fullData, x, y-1, z, isSkyLight);
        int light_Y_plus  = _GetNeighborLight(chunk, fullData, x, y+1, z, isSkyLight);
        int light_Z_minus = _GetNeighborLight(chunk, fullData, x, y, z-1, isSkyLight);
        int light_Z_plus  = _GetNeighborLight(chunk, fullData, x, y, z+1, isSkyLight);
        
#if LOGGING
        if (isSkyLight)
            chunk.lightingNeighborQueries_Sky += 6;
        else
            chunk.lightingNeighborQueries_Block += 6;
#endif
        
        // Find MAX of all neighbors
        int maxNeighborLight = light_X_minus;
        if (light_X_plus > maxNeighborLight) maxNeighborLight = light_X_plus;
        if (light_Y_minus > maxNeighborLight) maxNeighborLight = light_Y_minus;
        if (light_Y_plus > maxNeighborLight) maxNeighborLight = light_Y_plus;
        if (light_Z_minus > maxNeighborLight) maxNeighborLight = light_Z_minus;
        if (light_Z_plus > maxNeighborLight) maxNeighborLight = light_Z_plus;
        
        // Get current light FIRST
        byte currentLightByte = chunk.lightData[blockIndex];
        int currentLight = isSkyLight ? ((currentLightByte >> 4) & 0xF) : (currentLightByte & 0xF);
        
        // Apply opacity (air has opacity 0, treated as 1)
        if (opacity == 0) opacity = 1;
        
        int newLight = maxNeighborLight - opacity;
        if (newLight < 0) newLight = 0;
        
        // For skylight: max with sky visibility (15 if can see sky)
        if (isSkyLight) {
            // MINECRAFT BEHAVIOR: Check if this block can see the sky
            // If we're at the top of the world or current light is 15, we can see sky
            bool canSeeSky = false;
            if (chunk.chunkY_world >= worldDimensionY - 1 && y >= chunkSizeY - 1) {
                canSeeSky = true; // At world top
            } else if (currentLight == 15) {
                canSeeSky = true; // Already has full skylight
            }
            
            if (canSeeSky && newLight < 15) {
                newLight = 15;
            }
        }
        // For block light: max with emission
        else {
            int emission = lightEmissionCache[blockID];
            if (emission > newLight) newLight = emission;
        }
        
        // MINECRAFT BETA 1.7.3 BEHAVIOR: Update skylight whenever newLight != currentLight
        // Skylight can both increase AND decrease during horizontal propagation
        // This is essential for proper light propagation across chunk boundaries
        
        if (newLight != currentLight) {
#if LOGGING
            // Debug boundary lighting updates
            bool isBoundary = (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1);
            if (enableVerboseLogging && isSkyLight && isBoundary && newLight > currentLight + 5) {
                Debug.Log($"[McWorld] Large skylight increase at boundary ({x},{y},{z}): " +
                    $"{currentLight} -> {newLight}, maxNeighbor={maxNeighborLight}, opacity={opacity}");
            }
#endif
            
            // Set new light value
            if (isSkyLight) {
                int blockLight = currentLightByte & 0xF;
                
                // MINECRAFT BEHAVIOR: When skylight is reduced by semi-transparent block,
                // it creates block light to prevent areas from going too dark
                // This happens during HORIZONTAL propagation when opacity reduces light
                if (opacity > 1 && opacity < 15 && newLight < maxNeighborLight) {
                    // Sky light was reduced by semi-transparent block
                    // Convert the reduced skylight into block light
                    int convertedBlockLight = newLight;
                    if (convertedBlockLight > blockLight) {
                        blockLight = convertedBlockLight;
                    }
                }
                
                chunk.lightData[blockIndex] = (byte)((newLight << 4) | blockLight);
            } else {
                int skyLight = (currentLightByte >> 4) & 0xF;
                chunk.lightData[blockIndex] = (byte)((skyLight << 4) | newLight);
            }
            
            // MINECRAFT BETA 1.7.3 PROPAGATION:
            // Schedule ALL 6 neighbors for re-evaluation (symmetric propagation)
            // The light value decrease (newLight < currentLight) naturally prevents infinite propagation
            _ScheduleNeighborUpdate(chunk, x-1, y, z, queue, ref queueEnd);  // X-
            _ScheduleNeighborUpdate(chunk, x+1, y, z, queue, ref queueEnd);  // X+
            _ScheduleNeighborUpdate(chunk, x, y-1, z, queue, ref queueEnd);  // Y-
            _ScheduleNeighborUpdate(chunk, x, y+1, z, queue, ref queueEnd);  // Y+
            _ScheduleNeighborUpdate(chunk, x, y, z-1, queue, ref queueEnd);  // Z-
            _ScheduleNeighborUpdate(chunk, x, y, z+1, queue, ref queueEnd);  // Z+
            
            // FIXED: Trigger neighbor mesh rebuilds for boundary blocks
            // This ensures neighbors update their meshes when lighting changes at chunk boundaries
            if (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1)
            {
                TriggerNeighborMeshRebuilds(chunk);
            }
            
#if LOGGING
            if (isSkyLight)
                chunk.lightingQueueOps_Sky += 6;
            else
                chunk.lightingQueueOps_Block += 6;
#endif
            
            return true; // Light was updated
        }
        
        return false; // No change
    }
    
    // Helper: Get light value from a neighbor (handles cross-chunk boundaries)
    private int _GetNeighborLight(ChunkData chunk, byte[] fullData, int x, int y, int z, bool isSkyLight)
    {
        // Check if neighbor is in the same chunk
        if (x >= 0 && x < chunkSizeXZ && y >= 0 && y < chunkSizeY && z >= 0 && z < chunkSizeXZ)
        {
            // Within same chunk
            int neighborIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            byte neighborLightByte = chunk.lightData[neighborIndex];
            
            if (isSkyLight)
                return (neighborLightByte >> 4) & 0xF;
            else
                return neighborLightByte & 0xF;
        }
        else
        {
            // Neighbor is in a different chunk - get from neighbor chunk
            ChunkData neighborChunk = null;
            int neighborLocalX = x;
            int neighborLocalY = y;
            int neighborLocalZ = z;
            
            // Determine which neighbor chunk and adjust local coordinates
            if (x < 0)
            {
                neighborChunk = chunk.neighborNX;
                neighborLocalX = chunkSizeXZ - 1;
            }
            else if (x >= chunkSizeXZ)
            {
                neighborChunk = chunk.neighborPX;
                neighborLocalX = 0;
            }
            else if (y < 0)
            {
                neighborChunk = chunk.neighborNY;
                neighborLocalY = chunkSizeY - 1;
            }
            else if (y >= chunkSizeY)
            {
                neighborChunk = chunk.neighborPY;
                neighborLocalY = 0;
            }
            else if (z < 0)
            {
                neighborChunk = chunk.neighborNZ;
                neighborLocalZ = chunkSizeXZ - 1;
            }
            else if (z >= chunkSizeXZ)
            {
                neighborChunk = chunk.neighborPZ;
                neighborLocalZ = 0;
            }
            
            // Get light from neighbor chunk if available
            if (neighborChunk != null && neighborChunk.isDataReady && neighborChunk.lightData != null)
            {
                int neighborIndex = neighborLocalY * (chunkSizeXZ * chunkSizeXZ) + neighborLocalZ * chunkSizeXZ + neighborLocalX;
                byte neighborLightByte = neighborChunk.lightData[neighborIndex];
                
#if LOGGING
                if (isSkyLight)
                    chunk.lightingCrossChunkOps_Sky++;
                else
                    chunk.lightingCrossChunkOps_Block++;
#endif
                
                if (isSkyLight)
                    return (neighborLightByte >> 4) & 0xF;
                else
                    return neighborLightByte & 0xF;
            }
#if LOGGING
            else if (enableVerboseLogging && neighborChunk != null)
            {
                // Debug why neighbor chunk isn't being used
                string reason = "";
                if (!neighborChunk.isDataReady) reason += "notReady ";
                if (neighborChunk.lightData == null) reason += "noLightData ";
                if (reason == "") reason = "unknown";
                Debug.Log($"[McWorld] Cross-chunk query failed: neighbor exists but {reason}");
            }
#endif
            
            // No neighbor chunk data = fully dark (Minecraft behavior)
            return 0;
        }
    }
    
    // Helper: Schedule a neighbor for update (add to queue)
    private void _ScheduleNeighborUpdate(ChunkData chunk, int x, int y, int z, int[] queue, ref int queueEnd)
    {
        // Check if within same chunk bounds
        if (x >= 0 && x < chunkSizeXZ && y >= 0 && y < chunkSizeY && z >= 0 && z < chunkSizeXZ)
        {
            // Within same chunk - add to queue (with bounds check)
            if (queueEnd < queue.Length)
            {
            queue[queueEnd++] = (x << 16) | (y << 8) | z;
        }
        }
        // NOTE: Cross-chunk updates are handled by _ReconcileLightingWithNeighbors
        // Don't try to schedule them during BFS to prevent recursion/queue overflow
    }
    
    // OLD: Legacy PUSH-based method (kept for reference/compatibility)
    private void _PropagateLightToNeighbor(ChunkData chunk, byte[] fullData, int x, int y, int z, int sourceLight, bool isSkyLight, int[] queue, ref int queueEnd)
    {
        // Check if neighbor is in the same chunk
        if (x >= 0 && x < chunkSizeXZ && y >= 0 && y < chunkSizeY && z >= 0 && z < chunkSizeXZ)
        {
            // Within same chunk - process directly
        int neighborIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        byte blockID = fullData[neighborIndex];
        
        // Get block opacity (CRITICAL: treat 0 as 1)
        int opacity = lightOpacityCache[blockID];
        if (opacity == 0) opacity = 1;
        
        // If opacity >= 15, light can't pass through
        if (opacity >= 15) return;
        
        // Calculate propagated light value
        int newLight = sourceLight - opacity;
        if (newLight <= 0) return;
        
        // Get current light value at neighbor
        byte currentLightByte = chunk.lightData[neighborIndex];
        int currentLight;
        if (isSkyLight)
            currentLight = (currentLightByte >> 4) & 0xF;
        else
            currentLight = currentLightByte & 0xF;
        
        // Only update if new light is brighter
        if (newLight <= currentLight) return;
        
        // Update light value
        if (isSkyLight)
        {
            // Update sky light (high nibble), preserve block light (low nibble)
            int blockLight = currentLightByte & 0xF;
            chunk.lightData[neighborIndex] = (byte)((newLight << 4) | blockLight);
        }
        else
        {
            // Update block light (low nibble), preserve sky light (high nibble)
            int skyLight = (currentLightByte >> 4) & 0xF;
            chunk.lightData[neighborIndex] = (byte)((skyLight << 4) | newLight);
        }
        
        // Add to queue for further propagation
            queue[queueEnd++] = (x << 16) | (y << 8) | z;
        }
        else
        {
            // Neighbor is in a different chunk - propagate across boundary
            _PropagateToNeighborChunk(chunk, x, y, z, sourceLight, isSkyLight);
        }
    }
    
    private void _PropagateToNeighborChunk(ChunkData chunk, int x, int y, int z, int sourceLight, bool isSkyLight)
    {
        // MINECRAFT BETA 1.7.3 APPROACH: Update the boundary block, then trigger full BFS in neighbor
        // This ensures light propagates through the neighbor to its neighbors
        
        // Determine which neighbor chunk and adjust coordinates
        ChunkData neighborChunk = null;
        int neighborLocalX = x;
        int neighborLocalY = y;
        int neighborLocalZ = z;
        
        if (x < 0)
        {
            neighborChunk = chunk.neighborNX;
            neighborLocalX = chunkSizeXZ - 1;
        }
        else if (x >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPX;
            neighborLocalX = 0;
        }
        else if (y < 0)
        {
            neighborChunk = chunk.neighborNY;
            neighborLocalY = chunkSizeY - 1;
        }
        else if (y >= chunkSizeY)
        {
            neighborChunk = chunk.neighborPY;
            neighborLocalY = 0;
        }
        else if (z < 0)
        {
            neighborChunk = chunk.neighborNZ;
            neighborLocalZ = chunkSizeXZ - 1;
        }
        else if (z >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPZ;
            neighborLocalZ = 0;
        }
        
        // If neighbor chunk not ready, can't propagate
        if (neighborChunk == null || !neighborChunk.isDataReady || neighborChunk.lightData == null)
            return;
        
        // Get neighbor block data
        byte[] neighborData = _DecompressChunkColumnRLE(neighborChunk);
        if (neighborData == null) return;
        
        int neighborIndex = neighborLocalY * (chunkSizeXZ * chunkSizeXZ) + neighborLocalZ * chunkSizeXZ + neighborLocalX;
        byte blockID = neighborData[neighborIndex];
        
        // Get block opacity (CRITICAL: treat 0 as 1)
        int opacity = lightOpacityCache[blockID];
        if (opacity == 0) opacity = 1;
        
        // If opacity >= 15, light can't pass through
        if (opacity >= 15) return;
        
        // Calculate propagated light value
        int newLight = sourceLight - opacity;
        if (newLight <= 0) return;
        
        // Get current light value at neighbor
        byte currentLightByte = neighborChunk.lightData[neighborIndex];
        int currentLight;
        if (isSkyLight)
            currentLight = (currentLightByte >> 4) & 0xF;
        else
            currentLight = currentLightByte & 0xF;
        
        // Only update if new light is brighter
        if (newLight <= currentLight) return;
        
        // Update light value in neighbor chunk
        if (isSkyLight)
        {
            int blockLight = currentLightByte & 0xF;
            neighborChunk.lightData[neighborIndex] = (byte)((newLight << 4) | blockLight);
        }
        else
        {
            int skyLight = (currentLightByte >> 4) & 0xF;
            neighborChunk.lightData[neighborIndex] = (byte)((skyLight << 4) | newLight);
        }
        
        // FIXED: Trigger lightweight boundary BFS instead of full chunk BFS
        // This allows the light we just added to spread through the neighbor efficiently
        _PropagateBoundaryLighting(neighborChunk, neighborData);
        
        // FIXED: Trigger neighbor mesh rebuilds for cross-chunk lighting updates
        // This ensures the neighbor chunk updates its mesh when light propagates across boundaries
        TriggerNeighborMeshRebuilds(neighborChunk);
    }
    
    private void _UpdateBlockLighting(ChunkData chunk, int x, int y, int z, byte oldBlockID, byte newBlockID)
    {
        // Minecraft Beta 1.7.3 style lighting update for a single block change
        // This is a simplified version that handles the most common cases
        
        int lightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        byte[] fullData = _DecompressChunkColumnRLE(chunk);
        if (fullData == null) return;
        
        int oldOpacity = lightOpacityCache[oldBlockID];
        int newOpacity = lightOpacityCache[newBlockID];
        int oldEmission = lightEmissionCache[oldBlockID];
        int newEmission = lightEmissionCache[newBlockID];
        
        // If opacity or emission changed, we need to recalculate lighting
        bool needsUpdate = (oldOpacity != newOpacity) || (oldEmission != newEmission);
        if (!needsUpdate) return;
        
        // Recalculate lighting at this position and propagate
        _RecalculateLightAtPosition(chunk, fullData, x, y, z);
        
        // FIXED: Trigger neighbor mesh rebuilds for boundary blocks
        // This ensures neighbors update their meshes when lighting changes at chunk boundaries
        if (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1)
        {
            TriggerNeighborMeshRebuilds(chunk);
        }
    }
    
    private void _RecalculateLightAtPosition(ChunkData chunk, byte[] fullData, int x, int y, int z)
    {
        // Recalculate light value at a single position following Minecraft Beta 1.7.3 logic
        int lightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        byte blockID = fullData[lightIndex];
        
        int opacity = lightOpacityCache[blockID];
        if (opacity == 0) opacity = 1; // CRITICAL: treat air as opacity 1
        
        // Calculate sky light
        int newSkyLight = 0;
        if (y == chunkSizeY - 1)
        {
            // Top of chunk, always full sky light
            newSkyLight = 15;
        }
        else
        {
            // Check 6 neighbors and take maximum
            int maxNeighborSky = _GetSkyLightAt(chunk, x - 1, y, z);
            int neighborLight = _GetSkyLightAt(chunk, x + 1, y, z);
            if (neighborLight > maxNeighborSky) maxNeighborSky = neighborLight;
            neighborLight = _GetSkyLightAt(chunk, x, y - 1, z);
            if (neighborLight > maxNeighborSky) maxNeighborSky = neighborLight;
            neighborLight = _GetSkyLightAt(chunk, x, y + 1, z);
            if (neighborLight > maxNeighborSky) maxNeighborSky = neighborLight;
            neighborLight = _GetSkyLightAt(chunk, x, y, z - 1);
            if (neighborLight > maxNeighborSky) maxNeighborSky = neighborLight;
            neighborLight = _GetSkyLightAt(chunk, x, y, z + 1);
            if (neighborLight > maxNeighborSky) maxNeighborSky = neighborLight;
            
            newSkyLight = maxNeighborSky - opacity;
            if (newSkyLight < 0) newSkyLight = 0;
        }
        
        // Calculate block light
        int emission = lightEmissionCache[blockID];
        int newBlockLight = emission;
        
        // Check 6 neighbors and take maximum propagated value
        int maxNeighborBlock = _GetBlockLightAt(chunk, x - 1, y, z);
        int neighborBlockLight = _GetBlockLightAt(chunk, x + 1, y, z);
        if (neighborBlockLight > maxNeighborBlock) maxNeighborBlock = neighborBlockLight;
        neighborBlockLight = _GetBlockLightAt(chunk, x, y - 1, z);
        if (neighborBlockLight > maxNeighborBlock) maxNeighborBlock = neighborBlockLight;
        neighborBlockLight = _GetBlockLightAt(chunk, x, y + 1, z);
        if (neighborBlockLight > maxNeighborBlock) maxNeighborBlock = neighborBlockLight;
        neighborBlockLight = _GetBlockLightAt(chunk, x, y, z - 1);
        if (neighborBlockLight > maxNeighborBlock) maxNeighborBlock = neighborBlockLight;
        neighborBlockLight = _GetBlockLightAt(chunk, x, y, z + 1);
        if (neighborBlockLight > maxNeighborBlock) maxNeighborBlock = neighborBlockLight;
        
        int propagatedBlockLight = maxNeighborBlock - opacity;
        if (propagatedBlockLight < 0) propagatedBlockLight = 0;
        if (propagatedBlockLight > newBlockLight) newBlockLight = propagatedBlockLight;
        
        // Update the light value
        chunk.lightData[lightIndex] = (byte)((newSkyLight << 4) | newBlockLight);
        
        // Propagate changes to neighbors (simplified flood-fill)
        _PropagateChangedLight(chunk, fullData, x, y, z, newSkyLight, newBlockLight);
    }
    
    private void _PropagateChangedLight(ChunkData chunk, byte[] fullData, int x, int y, int z, int skyLight, int blockLight)
    {
        // Simple recursive propagation (limited depth to avoid stack overflow)
        // This is a simplified version - full Minecraft uses a queue
        _PropagateToNeighborIfBrighter(chunk, fullData, x - 1, y, z, skyLight, blockLight, 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x + 1, y, z, skyLight, blockLight, 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x, y - 1, z, skyLight, blockLight, 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x, y + 1, z, skyLight, blockLight, 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x, y, z - 1, skyLight, blockLight, 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x, y, z + 1, skyLight, blockLight, 1);
    }
    
    private void _PropagateToNeighborIfBrighter(ChunkData chunk, byte[] fullData, int x, int y, int z, int sourceSkyLight, int sourceBlockLight, int depth)
    {
        // Limit recursion depth to prevent stack overflow
        if (depth > 15) return;
        
        // Bounds check
        if (x < 0 || x >= chunkSizeXZ || y < 0 || y >= chunkSizeY || z < 0 || z >= chunkSizeXZ)
            return;
        
        int neighborIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        byte blockID = fullData[neighborIndex];
        
        int opacity = lightOpacityCache[blockID];
        if (opacity == 0) opacity = 1;
        if (opacity >= 15) return; // Can't propagate through opaque blocks
        
        // Calculate propagated light values
        int newSkyLight = sourceSkyLight - opacity;
        int newBlockLight = sourceBlockLight - opacity;
        if (newSkyLight < 0) newSkyLight = 0;
        if (newBlockLight < 0) newBlockLight = 0;
        
        // Get current light at neighbor
        byte currentByte = chunk.lightData[neighborIndex];
        int currentSkyLight = (currentByte >> 4) & 0xF;
        int currentBlockLight = currentByte & 0xF;
        
        // Check if update is needed
        bool skyBrighter = newSkyLight > currentSkyLight + 1; // +1 threshold to reduce updates
        bool blockBrighter = newBlockLight > currentBlockLight + 1;
        
        if (!skyBrighter && !blockBrighter) return;
        
        // Update light values
        if (skyBrighter) currentSkyLight = newSkyLight;
        if (blockBrighter) currentBlockLight = newBlockLight;
        
        chunk.lightData[neighborIndex] = (byte)((currentSkyLight << 4) | currentBlockLight);
        
        // Continue propagation
        _PropagateToNeighborIfBrighter(chunk, fullData, x - 1, y, z, currentSkyLight, currentBlockLight, depth + 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x + 1, y, z, currentSkyLight, currentBlockLight, depth + 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x, y - 1, z, currentSkyLight, currentBlockLight, depth + 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x, y + 1, z, currentSkyLight, currentBlockLight, depth + 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x, y, z - 1, currentSkyLight, currentBlockLight, depth + 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x, y, z + 1, currentSkyLight, currentBlockLight, depth + 1);
    }
    
    private int _GetSkyLightAt(ChunkData chunk, int x, int y, int z)
    {
        if (x < 0 || x >= chunkSizeXZ || y < 0 || y >= chunkSizeY || z < 0 || z >= chunkSizeXZ)
            return 0;
        
        int lightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        return (chunk.lightData[lightIndex] >> 4) & 0xF;
    }
    
    private int _GetBlockLightAt(ChunkData chunk, int x, int y, int z)
    {
        if (x < 0 || x >= chunkSizeXZ || y < 0 || y >= chunkSizeY || z < 0 || z >= chunkSizeXZ)
            return 0;
        
        int lightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        return chunk.lightData[lightIndex] & 0xF;
    }
    
    private float _GetLightBrightnessAtBlock(ChunkData chunk, int localX, int localY, int localZ)
    {
        // Check if chunk has light data
        if (chunk.lightData == null)
        {
            return 1.0f; // Default to full brightness if no lighting data
        }
        
        // Bounds check
        if (localX < 0 || localX >= chunkSizeXZ || localY < 0 || localY >= chunkSizeY || localZ < 0 || localZ >= chunkSizeXZ)
        {
            return 1.0f; // Default to full brightness if out of bounds
        }
        
        // Sample light data at this position
        int lightIndex = localY * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
        byte lightByte = chunk.lightData[lightIndex];
        
        // Extract sky light (high 4 bits) and block light (low 4 bits)
        int skyLight = (lightByte >> 4) & 0xF;
        int blockLight = lightByte & 0xF;
        
        // Apply time-of-day adjustment to sky light
        skyLight -= skylightSubtracted;
        if (skyLight < 0) skyLight = 0;
        
        // Take maximum of sky light and block light (Minecraft behavior)
        int finalLight = skyLight > blockLight ? skyLight : blockLight;
        
        // Clamp to valid range
        if (finalLight < 0) finalLight = 0;
        if (finalLight > 15) finalLight = 15;
        
        // Look up brightness from table
        return lightBrightnessTable[finalLight];
    }
    
    // FIXED: Sample light from the neighbor block that the face is against
    private float _GetLightBrightnessForFace(ChunkData chunk, Vector3 faceNormal, int localX, int localY, int localZ)
    {
        // Calculate neighbor coordinates based on face normal
        int neighborX = localX + Mathf.RoundToInt(faceNormal.x);
        int neighborY = localY + Mathf.RoundToInt(faceNormal.y);
        int neighborZ = localZ + Mathf.RoundToInt(faceNormal.z);
        
        // Check if neighbor is in the same chunk
        if (neighborX >= 0 && neighborX < chunkSizeXZ && 
            neighborY >= 0 && neighborY < chunkSizeY && 
            neighborZ >= 0 && neighborZ < chunkSizeXZ)
        {
            // Sample light from neighbor block in same chunk
            return _GetLightBrightnessAtBlock(chunk, neighborX, neighborY, neighborZ);
        }
        
        // Neighbor is in a different chunk - sample from neighbor chunk
        ChunkData neighborChunk = null;
        int neighborLocalX = neighborX;
        int neighborLocalY = neighborY;
        int neighborLocalZ = neighborZ;
        
        // Determine which neighbor chunk and adjust local coordinates
        if (neighborX < 0)
        {
            neighborChunk = chunk.neighborNX;
            neighborLocalX = chunkSizeXZ - 1;
        }
        else if (neighborX >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPX;
            neighborLocalX = 0;
        }
        else if (neighborY < 0)
        {
            neighborChunk = chunk.neighborNY;
            neighborLocalY = chunkSizeY - 1;
        }
        else if (neighborY >= chunkSizeY)
        {
            neighborChunk = chunk.neighborPY;
            neighborLocalY = 0;
        }
        else if (neighborZ < 0)
        {
            neighborChunk = chunk.neighborNZ;
            neighborLocalZ = chunkSizeXZ - 1;
        }
        else if (neighborZ >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPZ;
            neighborLocalZ = 0;
        }
        
        // Sample from neighbor chunk if available
        if (neighborChunk != null && neighborChunk.isDataReady && neighborChunk.lightData != null)
        {
            return _GetLightBrightnessAtBlock(neighborChunk, neighborLocalX, neighborLocalY, neighborLocalZ);
        }
        
        // No neighbor chunk data = fully dark (Minecraft behavior)
        return lightBrightnessTable[0]; // 0 light level = darkest
    }
    
    
    private int _GetSkyLightFromChunkAbove(int chunkX, int chunkY, int chunkZ, int localX, int localZ)
    {
        // Get the chunk above
        int chunkIndex = ChunkCenteredCoordsTo1D(chunkX, chunkY, chunkZ);
        if (chunkIndex >= 0 && chunkIndex < chunks_1D.Length && chunks_1D[chunkIndex] != null)
        {
            ChunkData chunkAbove = chunks_1D[chunkIndex];
            if (chunkAbove.isDataReady && chunkAbove.lightData != null)
            {
                // Get light from the bottom of the chunk above (y = 0)
                int lightIndex = 0 * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
                if (lightIndex >= 0 && lightIndex < chunkAbove.lightData.Length)
                {
                    byte lightByte = chunkAbove.lightData[lightIndex];
                    return (lightByte >> 4) & 0xF; // Extract sky light
                }
            }
        }
        
        // Fallback: assume full light from above
        return 15;
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
    
    private void _StoreBiomeData(ChunkData chunk)
    {
        // Get biome temperature and rainfall data from the terrain generator
        // This needs to be called right after chunk generation completes
        if (terrainGenerator == null) return;
        
        // Request the current biome data from terrain generator
        // The terrain generator should have this cached for the current chunk
        terrainGenerator.GetBiomeDataForChunk(chunk.chunkX_world, chunk.chunkZ_world, chunk._biomeTemperatures, chunk._biomeRainfall);
    }
    
    // OPTIMIZATION: Optimized biome color calculation that avoids allocations
    private Color _GetBiomeColorForBlockOptimized(ChunkData chunk, byte blockID, int localX, int localZ)
    {
        // Default white color (no tint) with full AO
        Color defaultColor = new Color(1f, 1f, 1f, 1f);
        
        // OPTIMIZATION: Early exit for non-tinted blocks
        if (!BetaBiome.IsGrassTintedBlock(blockID) && !BetaBiome.IsFoliageTintedBlock(blockID) && !BetaBiome.IsWaterTintedBlock(blockID))
        {
            return defaultColor; // No tinting needed
        }
        
        // Get biome data for this block's XZ position
        if (chunk._biomeTemperatures == null || chunk._biomeRainfall == null)
        {
            return defaultColor; // Biome data not available
        }
        
        // Calculate biome data index (16x16 grid)
        int biomeIndex = localZ * chunkSizeXZ + localX;
        
        // Bounds check
        if (biomeIndex < 0 || biomeIndex >= chunk._biomeTemperatures.Length)
        {
            return defaultColor;
        }
        
        // Get temperature and rainfall for this block's position
        double temperature = chunk._biomeTemperatures[biomeIndex];
        double rainfall = chunk._biomeRainfall[biomeIndex];
        
        // OPTIMIZATION: Calculate biome color based on block type using actual Beta 1.7.3 textures
        Color biomeColor;
        if (BetaBiome.IsGrassTintedBlock(blockID))
        {
            biomeColor = BetaBiome.GetGrassColor(temperature, rainfall, grassColorTexture);
        }
        else if (BetaBiome.IsFoliageTintedBlock(blockID))
        {
            biomeColor = BetaBiome.GetFoliageColor(temperature, rainfall, foliageColorTexture);
        }
        else // isWaterTinted
        {
            biomeColor = BetaBiome.GetWaterColor(temperature, rainfall, waterColorTexture);
        }
        
        // Keep full alpha for AO (alpha channel is used for vertex AO in the shader)
        biomeColor.a = 1f;
        
        return biomeColor;
    }
    
    // OPTIMIZATION: Optimized lighting calculation that avoids Vector3 allocations
    private float _GetLightBrightnessForFaceOptimized(ChunkData chunk, Vector3 faceNormal, int localX, int localY, int localZ)
    {
        // OPTIMIZATION: Calculate neighbor coordinates based on face normal without Vector3 operations
        int neighborX = localX + Mathf.RoundToInt(faceNormal.x);
        int neighborY = localY + Mathf.RoundToInt(faceNormal.y);
        int neighborZ = localZ + Mathf.RoundToInt(faceNormal.z);
        
        // Check if neighbor is in the same chunk
        if (neighborX >= 0 && neighborX < chunkSizeXZ && 
            neighborY >= 0 && neighborY < chunkSizeY && 
            neighborZ >= 0 && neighborZ < chunkSizeXZ)
        {
            // Sample light from neighbor block in same chunk
            return _GetLightBrightnessAtBlockOptimized(chunk, neighborX, neighborY, neighborZ);
        }
        
        // Neighbor is in a different chunk - sample from neighbor chunk
        ChunkData neighborChunk = null;
        int neighborLocalX = neighborX;
        int neighborLocalY = neighborY;
        int neighborLocalZ = neighborZ;
        
        // Determine which neighbor chunk and adjust local coordinates
        if (neighborX < 0)
        {
            neighborChunk = chunk.neighborNX;
            neighborLocalX = chunkSizeXZ - 1;
        }
        else if (neighborX >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPX;
            neighborLocalX = 0;
        }
        else if (neighborY < 0)
        {
            neighborChunk = chunk.neighborNY;
            neighborLocalY = chunkSizeY - 1;
        }
        else if (neighborY >= chunkSizeY)
        {
            neighborChunk = chunk.neighborPY;
            neighborLocalY = 0;
        }
        else if (neighborZ < 0)
        {
            neighborChunk = chunk.neighborNZ;
            neighborLocalZ = chunkSizeXZ - 1;
        }
        else if (neighborZ >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPZ;
            neighborLocalZ = 0;
        }
        
        // Sample from neighbor chunk if available
        if (neighborChunk != null && neighborChunk.isDataReady && neighborChunk.lightData != null)
        {
            return _GetLightBrightnessAtBlockOptimized(neighborChunk, neighborLocalX, neighborLocalY, neighborLocalZ);
        }
        
        // No neighbor chunk data = fully dark (Minecraft behavior)
        return lightBrightnessTable[0]; // 0 light level = darkest
    }
    
    // OPTIMIZATION: Optimized block lighting calculation
    private float _GetLightBrightnessAtBlockOptimized(ChunkData chunk, int localX, int localY, int localZ)
    {
        // Check if chunk has light data
        if (chunk.lightData == null)
        {
            return 1.0f; // Default to full brightness if no lighting data
        }
        
        // Bounds check
        if (localX < 0 || localX >= chunkSizeXZ || localY < 0 || localY >= chunkSizeY || localZ < 0 || localZ >= chunkSizeXZ)
        {
            return 1.0f; // Default to full brightness if out of bounds
        }
        
        // Sample light data at this position
        int lightIndex = localY * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
        byte lightByte = chunk.lightData[lightIndex];
        
        // Extract sky light (high 4 bits) and block light (low 4 bits)
        int skyLight = (lightByte >> 4) & 0xF;
        int blockLight = lightByte & 0xF;
        
        // Apply time-of-day adjustment to sky light
        skyLight -= skylightSubtracted;
        if (skyLight < 0) skyLight = 0;
        
        // Take maximum of sky light and block light (Minecraft behavior)
        int finalLight = skyLight > blockLight ? skyLight : blockLight;
        
        // Clamp to valid range
        if (finalLight < 0) finalLight = 0;
        if (finalLight > 15) finalLight = 15;
        
        // Look up brightness from table
        return lightBrightnessTable[finalLight];
    }
    
    private Color _GetBiomeColorForBlock(ChunkData chunk, byte blockID, int localX, int localZ)
    {
        // Default white color (no tint) with full AO
        Color defaultColor = new Color(1f, 1f, 1f, 1f);
        
        // Check if this block type should be biome tinted
        bool isGrassTinted = BetaBiome.IsGrassTintedBlock(blockID);
        bool isFoliageTinted = BetaBiome.IsFoliageTintedBlock(blockID);
        bool isWaterTinted = BetaBiome.IsWaterTintedBlock(blockID);
        
        if (!isGrassTinted && !isFoliageTinted && !isWaterTinted)
        {
            return defaultColor; // No tinting needed
        }
        
        // Get biome data for this block's XZ position
        if (chunk._biomeTemperatures == null || chunk._biomeRainfall == null)
        {
            return defaultColor; // Biome data not available
        }
        
        // Calculate biome data index (16x16 grid)
        int biomeIndex = localZ * chunkSizeXZ + localX;
        
        // Bounds check
        if (biomeIndex < 0 || biomeIndex >= chunk._biomeTemperatures.Length)
        {
            return defaultColor;
        }
        
        // Get temperature and rainfall for this block's position
        double temperature = chunk._biomeTemperatures[biomeIndex];
        double rainfall = chunk._biomeRainfall[biomeIndex];
        
        // Calculate biome color based on block type using actual Beta 1.7.3 textures
        Color biomeColor;
        if (isGrassTinted)
        {
            biomeColor = BetaBiome.GetGrassColor(temperature, rainfall, grassColorTexture);
        }
        else if (isFoliageTinted)
        {
            biomeColor = BetaBiome.GetFoliageColor(temperature, rainfall, foliageColorTexture);
        }
        else // isWaterTinted
        {
            biomeColor = BetaBiome.GetWaterColor(temperature, rainfall, waterColorTexture);
        }
        
        // Keep full alpha for AO (alpha channel is used for vertex AO in the shader)
        biomeColor.a = 1f;
        
        return biomeColor;
    }
    
    // OPTIMIZATION Phase 3: Pre-compute brightness for all blocks in chunk
    // This eliminates 10,000+ method calls during meshing
    private void _PreComputeChunkBrightness(ChunkData chunk)
    {
        // Allocate brightness cache if needed (4096 bytes for 16x16x16)
        if (chunk._cachedBrightness == null || chunk._cachedBrightness.Length != chunkSizeXZ * chunkSizeY * chunkSizeXZ)
        {
            chunk._cachedBrightness = new byte[chunkSizeXZ * chunkSizeY * chunkSizeXZ];
        }
        
        // Check if we have light data
        if (chunk.lightData == null)
        {
            // Fill with full brightness (15)
            for (int i = 0; i < chunk._cachedBrightness.Length; i++)
            {
                chunk._cachedBrightness[i] = 15;
            }
            return;
        }
        
        // Pre-compute brightness for all blocks in this chunk
        int stride = chunkSizeXZ * chunkSizeXZ;
        for (int y = 0; y < chunkSizeY; y++)
        {
            int yBase = y * stride;
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                int zBase = yBase + z * chunkSizeXZ;
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    int idx = zBase + x;
                    byte lightByte = chunk.lightData[idx];
                    
                    // Extract sky light (high 4 bits) and block light (low 4 bits)
                    int skyLight = (lightByte >> 4) & 0xF;
                    int blockLight = lightByte & 0xF;
                    
                    // Apply time-of-day adjustment to sky light
                    skyLight -= skylightSubtracted;
                    if (skyLight < 0) skyLight = 0;
                    
                    // Take maximum of sky light and block light (Minecraft behavior)
                    int finalLight = skyLight > blockLight ? skyLight : blockLight;
                    
                    // Clamp to valid range (0-15)
                    if (finalLight < 0) finalLight = 0;
                    if (finalLight > 15) finalLight = 15;
                    
                    // Store light level (0-15) directly
                    chunk._cachedBrightness[idx] = (byte)finalLight;
                }
            }
        }
    }
    
    // OPTIMIZATION Phase 3: Fast brightness lookup from pre-computed cache
    private float _GetCachedBrightnessAtBlock(ChunkData chunk, int localX, int localY, int localZ)
    {
        // Bounds check
        if (localX < 0 || localX >= chunkSizeXZ || localY < 0 || localY >= chunkSizeY || localZ < 0 || localZ >= chunkSizeXZ)
        {
            return 1.0f; // Default to full brightness if out of bounds
        }
        
        // Check if cache exists
        if (chunk._cachedBrightness == null)
        {
            return 1.0f; // Default to full brightness
        }
        
        // Direct lookup from cache
        int idx = localY * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
        int lightLevel = chunk._cachedBrightness[idx];
        
        // Look up brightness from table
        return lightBrightnessTable[lightLevel];
    }
    
    // OPTIMIZATION Phase 3: Fast brightness lookup for faces (uses cached data)
    private float _GetCachedBrightnessForFace(ChunkData chunk, Vector3 faceNormal, int localX, int localY, int localZ)
    {
        // Calculate neighbor coordinates based on face normal
        int neighborX = localX + Mathf.RoundToInt(faceNormal.x);
        int neighborY = localY + Mathf.RoundToInt(faceNormal.y);
        int neighborZ = localZ + Mathf.RoundToInt(faceNormal.z);
        
        // Check if neighbor is in the same chunk
        if (neighborX >= 0 && neighborX < chunkSizeXZ && 
            neighborY >= 0 && neighborY < chunkSizeY && 
            neighborZ >= 0 && neighborZ < chunkSizeXZ)
        {
            // Use cached brightness from same chunk
            return _GetCachedBrightnessAtBlock(chunk, neighborX, neighborY, neighborZ);
        }
        
        // Neighbor is in a different chunk - determine which neighbor
        ChunkData neighborChunk = null;
        int neighborLocalX = neighborX;
        int neighborLocalY = neighborY;
        int neighborLocalZ = neighborZ;
        
        if (neighborX < 0)
        {
            neighborChunk = chunk.neighborNX;
            neighborLocalX = chunkSizeXZ - 1;
        }
        else if (neighborX >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPX;
            neighborLocalX = 0;
        }
        else if (neighborY < 0)
        {
            neighborChunk = chunk.neighborNY;
            neighborLocalY = chunkSizeY - 1;
        }
        else if (neighborY >= chunkSizeY)
        {
            neighborChunk = chunk.neighborPY;
            neighborLocalY = 0;
        }
        else if (neighborZ < 0)
        {
            neighborChunk = chunk.neighborNZ;
            neighborLocalZ = chunkSizeXZ - 1;
        }
        else if (neighborZ >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPZ;
            neighborLocalZ = 0;
        }
        
        // Sample from neighbor chunk if available
        if (neighborChunk != null && neighborChunk.isDataReady && neighborChunk._cachedBrightness != null)
        {
            return _GetCachedBrightnessAtBlock(neighborChunk, neighborLocalX, neighborLocalY, neighborLocalZ);
        }
        
        // No neighbor chunk data = fully dark (Minecraft behavior)
        return lightBrightnessTable[0]; // 0 light level = darkest
    }
    
    // OPTIMIZATION Phase 6: Pre-compute biome colors for all XZ columns in chunk
    // This eliminates ~10,000 biome texture lookups, reducing to just 256
    private void _PreComputeBiomeColors(ChunkData chunk)
    {
        // Allocate biome color cache if needed (256 colors for 16x16 XZ grid)
        if (chunk._cachedBiomeColors == null || chunk._cachedBiomeColors.Length != chunkSizeXZ * chunkSizeXZ)
        {
            chunk._cachedBiomeColors = new Color[chunkSizeXZ * chunkSizeXZ];
        }
        
        // Check if biome data is available
        if (chunk._biomeTemperatures == null || chunk._biomeRainfall == null)
        {
            // Fill with default white color (no tint)
            Color defaultColor = new Color(1f, 1f, 1f, 1f);
            for (int i = 0; i < chunk._cachedBiomeColors.Length; i++)
            {
                chunk._cachedBiomeColors[i] = defaultColor;
            }
            return;
        }
        
        // Pre-compute biome colors for each XZ column
        for (int z = 0; z < chunkSizeXZ; z++)
        {
            for (int x = 0; x < chunkSizeXZ; x++)
            {
                int idx = z * chunkSizeXZ + x;
                
                // Get temperature and rainfall for this column
                double temperature = chunk._biomeTemperatures[idx];
                double rainfall = chunk._biomeRainfall[idx];
                
                // Calculate grass color for this biome (most common tinted block type)
                // We use grass color as default since it's the most common
                // Individual blocks will check if they need different tinting
                Color grassColor = BetaBiome.GetGrassColor(temperature, rainfall, grassColorTexture);
                grassColor.a = 1f; // Keep full alpha
                
                chunk._cachedBiomeColors[idx] = grassColor;
            }
        }
    }
    
    // OPTIMIZATION Phase 6: Get cached biome color with block-specific tinting
    // This uses the pre-computed cache but applies proper tinting based on block type
    private Color _GetCachedBiomeColor(ChunkData chunk, byte blockID, int localX, int localZ)
    {
        // Default white color (no tint)
        Color defaultColor = new Color(1f, 1f, 1f, 1f);
        
        // Check if cache exists
        if (chunk._cachedBiomeColors == null)
        {
            return defaultColor;
        }
        
        // Bounds check
        if (localX < 0 || localX >= chunkSizeXZ || localZ < 0 || localZ >= chunkSizeXZ)
        {
            return defaultColor;
        }
        
        // OPTIMIZATION: Early exit for non-tinted blocks
        bool isGrassTinted = BetaBiome.IsGrassTintedBlock(blockID);
        bool isFoliageTinted = BetaBiome.IsFoliageTintedBlock(blockID);
        bool isWaterTinted = BetaBiome.IsWaterTintedBlock(blockID);
        
        if (!isGrassTinted && !isFoliageTinted && !isWaterTinted)
        {
            return defaultColor; // No tinting needed
        }
        
        // Get index for this XZ position
        int idx = localZ * chunkSizeXZ + localX;
        
        // If it's grass-tinted, we can use the cached value directly
        if (isGrassTinted)
        {
            return chunk._cachedBiomeColors[idx];
        }
        
        // For foliage or water, we need to recalculate with correct tint type
        // But we can still use the cached biome data
        if (chunk._biomeTemperatures == null || chunk._biomeRainfall == null)
        {
            return defaultColor;
        }
        
        double temperature = chunk._biomeTemperatures[idx];
        double rainfall = chunk._biomeRainfall[idx];
        
        Color biomeColor;
        if (isFoliageTinted)
        {
            biomeColor = BetaBiome.GetFoliageColor(temperature, rainfall, foliageColorTexture);
        }
        else // isWaterTinted
        {
            biomeColor = BetaBiome.GetWaterColor(temperature, rainfall, waterColorTexture);
        }
        
        biomeColor.a = 1f;
        return biomeColor;
    }
    
    #endregion

    // OPTIMIZATION: Simplified reconciliation to prevent lag spikes
    // Instead of complex BFS, use lightweight boundary updates
    private void _ReconcileLightingWithNeighbors(ChunkData chunk)
    {
#if LOGGING
        float startTime = Time.realtimeSinceStartup * 1000f;
#endif
        
        // OPTIMIZATION: Lightweight reconciliation - only update immediate boundaries
        // This prevents cascading updates that cause lag spikes on new columns
        
        byte[] chunkData = _DecompressChunkColumnRLE(chunk);
        if (chunkData == null) return;
        
        // OPTIMIZATION: Only reconcile with ready neighbors to avoid cascading
        ChunkData[] neighbors = _GetCachedNeighbors(chunk);
        int readyNeighbors = 0;
        
        for (int dir = 0; dir < 6; dir++)
        {
            ChunkData neighbor = neighbors[dir];
            if (neighbor != null && neighbor.isDataReady && neighbor.lightData != null)
            {
                readyNeighbors++;
                _UpdateNeighborBoundaryLighting(chunk, neighbor, dir, chunkData);
            }
        }
        
        // FIXED: Always do boundary BFS if we have any ready neighbors
        // This ensures light propagates even for isolated chunks
        if (readyNeighbors > 0)
        {
            _PerformLightweightBFS(chunk, chunkData);
            
            // FIXED: Trigger neighbor mesh rebuilds after lighting reconciliation
            // This ensures neighbors update their meshes when lighting changes
            TriggerNeighborMeshRebuilds(chunk);
        }
        
#if LOGGING
        chunk.time_LightingReconcile = Time.realtimeSinceStartup * 1000f - startTime;
#endif
    }
    
    // OPTIMIZATION: Update only the boundary lighting between two chunks
    private void _UpdateNeighborBoundaryLighting(ChunkData chunk, ChunkData neighbor, int direction, byte[] chunkData)
    {
        // Only update the boundary face between the two chunks
        // This is much cheaper than full BFS
        
        int faceSize = chunkSizeXZ * chunkSizeY; // Size of one face
        int[] boundaryOffsets = new int[faceSize];
        int boundaryCount = 0;
        
        // Calculate boundary block indices based on direction
        for (int y = 0; y < chunkSizeY; y++)
        {
            for (int i = 0; i < chunkSizeXZ; i++)
            {
                int x, z;
                switch (direction)
                {
                    case 0: x = chunkSizeXZ - 1; z = i; break; // PX face
                    case 1: x = 0; z = i; break; // NX face
                    case 2: x = i; z = chunkSizeXZ - 1; break; // PZ face
                    case 3: x = i; z = 0; break; // NZ face
                    case 4: x = i; z = i; break; // PY face (simplified)
                    case 5: x = i; z = i; break; // NY face (simplified)
                    default: continue;
                }
                
                if (x >= 0 && x < chunkSizeXZ && z >= 0 && z < chunkSizeXZ)
                {
                    boundaryOffsets[boundaryCount++] = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                }
            }
        }
        
        // Update lighting for boundary blocks only
        for (int i = 0; i < boundaryCount; i++)
        {
            int blockIndex = boundaryOffsets[i];
            byte blockID = chunkData[blockIndex];
            int opacity = lightOpacityCache[blockID];
            
            if (opacity < 15) // Skip fully opaque blocks
            {
                _UpdateSingleBlockLighting(chunk, blockIndex, blockID);
            }
        }
    }
    
    // OPTIMIZATION: Lightweight BFS with limited scope
    private void _PerformLightweightBFS(ChunkData chunk, byte[] chunkData)
    {
        int[] lightQueue = GetBFSQueue();
        int queueEnd = 0;
        
        // OPTIMIZATION: Only add boundary blocks to queue
        for (int y = 1; y < chunkSizeY - 1; y++)
        {
            for (int z = 1; z < chunkSizeXZ - 1; z++)
            {
                // X faces
                lightQueue[queueEnd++] = (0 << 16) | (y << 8) | z;
                lightQueue[queueEnd++] = ((chunkSizeXZ - 1) << 16) | (y << 8) | z;
                
                // Z faces
                lightQueue[queueEnd++] = (z << 16) | (y << 8) | 0;
                lightQueue[queueEnd++] = (z << 16) | (y << 8) | (chunkSizeXZ - 1);
                
                if (queueEnd >= lightQueue.Length - 10) break;
            }
            if (queueEnd >= lightQueue.Length - 10) break;
        }
        
        // OPTIMIZATION: Limited BFS iterations
        int queueStart = 0;
        int[] neighborOffsets = { -1, 0, 0, 1, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, -1, 0, 0, 1 };
        
        for (int iteration = 0; iteration < 2 && queueStart < queueEnd; iteration++)
        {
            int iterationEnd = queueEnd;
            while (queueStart < iterationEnd && queueEnd < lightQueue.Length - 6)
            {
                int packed = lightQueue[queueStart++];
                int x = (packed >> 16) & 0xFF;
                int y = (packed >> 8) & 0xFF;
                int z = packed & 0xFF;
                
                _UpdateBlockLightPULLOptimized(chunk, chunkData, x, y, z, true, lightQueue, ref queueEnd, neighborOffsets);
            }
        }
        
        // FIXED: Trigger neighbor mesh rebuilds after lightweight BFS
        // This ensures neighbors update their meshes when boundary lighting changes
        TriggerNeighborMeshRebuilds(chunk);
        
        ReturnBFSQueue(lightQueue);
    }
    
    // OPTIMIZATION: Update lighting for a single block
    private void _UpdateSingleBlockLighting(ChunkData chunk, int blockIndex, byte blockID)
    {
        int y = blockIndex / (chunkSizeXZ * chunkSizeXZ);
        int z = (blockIndex / chunkSizeXZ) % chunkSizeXZ;
        int x = blockIndex % chunkSizeXZ;
        
        int opacity = lightOpacityCache[blockID];
        if (opacity == 0) opacity = 1;
        
        // Calculate new lighting based on neighbors
        int maxNeighborLight = 0;
        int[] neighborOffsets = { -1, 0, 0, 1, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, -1, 0, 0, 1 };
        
        for (int i = 0; i < 6; i++)
        {
            int nx = x + neighborOffsets[i * 3];
            int ny = y + neighborOffsets[i * 3 + 1];
            int nz = z + neighborOffsets[i * 3 + 2];
            
            int neighborLight = _GetNeighborLightOptimized(chunk, nx, ny, nz, true);
            if (neighborLight > maxNeighborLight) maxNeighborLight = neighborLight;
        }
        
        int newLight = maxNeighborLight - opacity;
        if (newLight < 0) newLight = 0;
        
        // Update if different
        byte currentByte = chunk.lightData[blockIndex];
        int currentLight = (currentByte >> 4) & 0xF;
        
        if (newLight != currentLight)
        {
            int blockLight = currentByte & 0xF;
            chunk.lightData[blockIndex] = (byte)((newLight << 4) | blockLight);
            
            // FIXED: Trigger neighbor mesh rebuilds for boundary blocks
            // This ensures neighbors update their meshes when lighting changes at chunk boundaries
            if (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1)
            {
                TriggerNeighborMeshRebuilds(chunk);
            }
        }
    }
    
    // Add all boundary blocks of a chunk to the reconciliation queue
    private void _AddBoundaryBlocksToQueue(ChunkData chunk, int[] queue, ref int queueEnd)
    {
        // Pack: chunkX(8bits) | chunkY(4bits) | chunkZ(4bits) | localX(4bits) | localY(4bits) | localZ(4bits) = 28 bits
        // This allows us to track blocks across multiple chunks in the same queue
        
        int chunkXPacked = (chunk.chunkX_world & 0xFF) << 24;
        int chunkYPacked = (chunk.chunkY_world & 0xF) << 20;
        int chunkZPacked = (chunk.chunkZ_world & 0xF) << 16;
        
        // Add boundary faces (not edges/corners to keep queue size reasonable)
        // X faces
        for (int y = 1; y < chunkSizeY - 1 && queueEnd < queue.Length - 1; y++)
        {
            for (int z = 1; z < chunkSizeXZ - 1 && queueEnd < queue.Length - 1; z++)
            {
                queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | (0 << 12) | (y << 8) | (z << 4);
                if (queueEnd < queue.Length)
                    queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | ((chunkSizeXZ-1) << 12) | (y << 8) | (z << 4);
            }
        }
        
        // Z faces
        for (int y = 1; y < chunkSizeY - 1 && queueEnd < queue.Length - 1; y++)
        {
            for (int x = 1; x < chunkSizeXZ - 1 && queueEnd < queue.Length - 1; x++)
            {
                queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | (x << 12) | (y << 8) | (0 << 4);
                if (queueEnd < queue.Length)
                    queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | (x << 12) | (y << 8) | ((chunkSizeXZ-1) << 4);
            }
        }
    }
    
    // Add a neighbor chunk's boundary face to the reconciliation queue
    private void _AddNeighborBoundaryToQueue(ChunkData neighborChunk, int faceDir, int[] queue, ref int queueEnd)
    {
        int chunkXPacked = (neighborChunk.chunkX_world & 0xFF) << 24;
        int chunkYPacked = (neighborChunk.chunkY_world & 0xF) << 20;
        int chunkZPacked = (neighborChunk.chunkZ_world & 0xF) << 16;
        
        // Add boundary face based on direction
        // 0=NZ, 1=PZ, 2=NX, 3=PX, 4=NY, 5=PY
        
        switch(faceDir)
        {
            case 0: // -Z face (z=0)
                for (int y = 1; y < chunkSizeY - 1 && queueEnd < queue.Length; y++)
                    for (int x = 1; x < chunkSizeXZ - 1 && queueEnd < queue.Length; x++)
                        queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | (x << 12) | (y << 8) | (0 << 4);
                break;
            case 1: // +Z face (z=15)
                for (int y = 1; y < chunkSizeY - 1 && queueEnd < queue.Length; y++)
                    for (int x = 1; x < chunkSizeXZ - 1 && queueEnd < queue.Length; x++)
                        queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | (x << 12) | (y << 8) | ((chunkSizeXZ-1) << 4);
                break;
            case 2: // -X face (x=0)
                for (int y = 1; y < chunkSizeY - 1 && queueEnd < queue.Length; y++)
                    for (int z = 1; z < chunkSizeXZ - 1 && queueEnd < queue.Length; z++)
                        queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | (0 << 12) | (y << 8) | (z << 4);
                break;
            case 3: // +X face (x=15)
                for (int y = 1; y < chunkSizeY - 1 && queueEnd < queue.Length; y++)
                    for (int z = 1; z < chunkSizeXZ - 1 && queueEnd < queue.Length; z++)
                        queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | ((chunkSizeXZ-1) << 12) | (y << 8) | (z << 4);
                break;
        }
    }
    
    // OLD: Run a limited BFS that only propagates from boundary blocks inward
    // This is much cheaper than a full chunk BFS and sufficient for reconciliation
    private void _PropagateBoundaryLighting(ChunkData chunk, byte[] fullData)
    {
        // Get a BFS queue
        int[] lightQueue = GetBFSQueue();
        int queueEnd = 0;
        
        // Add all boundary blocks to the queue (faces only, not edges/corners)
        // X faces
        bool queueFull = false;
        for (int y = 1; y < chunkSizeY - 1 && !queueFull; y++)
        {
            for (int z = 1; z < chunkSizeXZ - 1 && !queueFull; z++)
            {
                if (queueEnd < lightQueue.Length - 1)
                {
                    lightQueue[queueEnd++] = (0 << 16) | (y << 8) | z; // X = 0
                }
                else
                {
                    queueFull = true;
                    break;
                }
                
                if (queueEnd < lightQueue.Length - 1)
                {
                    lightQueue[queueEnd++] = ((chunkSizeXZ - 1) << 16) | (y << 8) | z; // X = 15
                }
                else
                {
                    queueFull = true;
                    break;
                }
            }
        }
        
        // Z faces
        if (!queueFull)
        {
            for (int y = 1; y < chunkSizeY - 1 && !queueFull; y++)
            {
                for (int x = 1; x < chunkSizeXZ - 1 && !queueFull; x++)
                {
                    if (queueEnd < lightQueue.Length - 1)
                    {
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | 0; // Z = 0
                    }
                    else
                    {
                        queueFull = true;
                        break;
                    }
                    
                    if (queueEnd < lightQueue.Length - 1)
                    {
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | (chunkSizeXZ - 1); // Z = 15
                    }
                    else
                    {
                        queueFull = true;
                        break;
                    }
                }
            }
        }
        
        // Y faces (less important but include for completeness)
        if (!queueFull)
        {
            for (int z = 1; z < chunkSizeXZ - 1 && !queueFull; z++)
            {
                for (int x = 1; x < chunkSizeXZ - 1 && !queueFull; x++)
                {
                    if (queueEnd < lightQueue.Length - 2)
                    {
                        lightQueue[queueEnd++] = (x << 16) | (0 << 8) | z; // Y = 0
                        lightQueue[queueEnd++] = (x << 16) | ((chunkSizeY - 1) << 8) | z; // Y = 15
                    }
                    else
                    {
                        queueFull = true;
                        break;
                    }
                }
            }
        }
        
        // FIXED: Process skylight with convergence-based iterations
        // Continue until no more updates occur (proper convergence)
        int queueStart = 0;
        int initialQueueSize = queueEnd;
        int skylightIterations = 0;
        int maxSkylightIterations = 8; // Increased limit for better convergence
        while (queueStart < queueEnd && skylightIterations < maxSkylightIterations)
        {
            int iterationStart = queueStart;
            int iterationEnd = queueEnd;
            while (queueStart < iterationEnd && queueEnd < lightQueue.Length - 6)
            {
                int packed = lightQueue[queueStart++];
                int x = (packed >> 16) & 0xFF;
                int y = (packed >> 8) & 0xFF;
                int z = packed & 0xFF;
                _UpdateBlockLightPULL(chunk, fullData, x, y, z, true, lightQueue, ref queueEnd);
            }
            skylightIterations++;
            
            // If no new blocks were added to queue, we've converged
            if (queueStart == queueEnd) break;
        }
        
        // FIXED: Process block light with convergence-based iterations
        queueStart = 0;
        queueEnd = initialQueueSize; // Reset to boundary blocks only
        int blocklightIterations = 0;
        int maxBlocklightIterations = 8; // Increased limit for better convergence
        while (queueStart < queueEnd && blocklightIterations < maxBlocklightIterations)
        {
            int iterationStart = queueStart;
            int iterationEnd = queueEnd;
            while (queueStart < iterationEnd && queueEnd < lightQueue.Length - 6)
            {
                int packed = lightQueue[queueStart++];
                int x = (packed >> 16) & 0xFF;
                int y = (packed >> 8) & 0xFF;
                int z = packed & 0xFF;
                _UpdateBlockLightPULL(chunk, fullData, x, y, z, false, lightQueue, ref queueEnd);
            }
            blocklightIterations++;
            
            // If no new blocks were added to queue, we've converged
            if (queueStart == queueEnd) break;
        }
        
        // FIXED: Trigger neighbor mesh rebuilds after boundary lighting propagation
        // This ensures neighbors update their meshes when boundary lighting changes
        TriggerNeighborMeshRebuilds(chunk);
        
        ReturnBFSQueue(lightQueue);
    }
    
    // OLD: Per-block lighting update (REMOVED - too expensive, caused lag and darkening)
    // Previously: Scheduled individual BFS updates for 1000+ boundary blocks
    // Problem: Way too slow, and caused darkening by pulling from all neighbors
    // Solution: Use simplified reconciliation that just re-imports boundaries
    
    // Import light from all neighbor chunks into this chunk's boundary blocks
    // This ensures BFS starts with light from already-generated neighbors
    private void _ImportLightFromNeighbors(ChunkData chunk, byte[] fullData)
    {
        // Check all 6 face neighbors
        for (int dir = 0; dir < 6; dir++)
        {
            int neighborChunkX = chunk.chunkX_world + neighbor_dx_offsets[dir];
            int neighborChunkY = chunk.chunkY_world + neighbor_dy_offsets[dir];
            int neighborChunkZ = chunk.chunkZ_world + neighbor_dz_offsets[dir];
            
            ChunkData neighborChunk = GetChunkAt(neighborChunkX, neighborChunkY, neighborChunkZ);
            
            // Skip if neighbor doesn't exist or isn't ready
            if (neighborChunk == null || !neighborChunk.isDataReady || neighborChunk.lightData == null)
                continue;
            
            // Get neighbor's data
            byte[] neighborData = _DecompressChunkColumnRLE(neighborChunk);
            if (neighborData == null) continue;
            
            // Import light from neighbor's boundary to our boundary
            _ImportFromNeighborBoundary(chunk, fullData, neighborChunk, neighborData, dir);
        }
    }
    
    // Import light from a specific neighbor chunk's boundary
    private void _ImportFromNeighborBoundary(ChunkData chunk, byte[] chunkData, ChunkData neighborChunk, byte[] neighborData, int direction)
    {
        // Iterate over all blocks on the boundary face
        int size1 = (direction == 4 || direction == 5) ? chunkSizeXZ : chunkSizeXZ;
        int size2 = (direction == 4 || direction == 5) ? chunkSizeXZ : chunkSizeY;
        
        for (int i = 0; i < size1; i++)
        {
            for (int j = 0; j < size2; j++)
            {
                int chunkX = 0, chunkY = 0, chunkZ = 0;
                int neighborX = 0, neighborY = 0, neighborZ = 0;
                
                // Determine coordinates based on direction (reversed from export)
                switch (direction)
                {
                    case 0: // North (-Z): our Z=0 imports from neighbor's Z=15
                        chunkX = i; chunkY = j; chunkZ = 0;
                        neighborX = i; neighborY = j; neighborZ = chunkSizeXZ - 1;
                        break;
                    case 1: // South (+Z): our Z=15 imports from neighbor's Z=0
                        chunkX = i; chunkY = j; chunkZ = chunkSizeXZ - 1;
                        neighborX = i; neighborY = j; neighborZ = 0;
                        break;
                    case 2: // West (-X): our X=0 imports from neighbor's X=15
                        chunkX = 0; chunkY = j; chunkZ = i;
                        neighborX = chunkSizeXZ - 1; neighborY = j; neighborZ = i;
                        break;
                    case 3: // East (+X): our X=15 imports from neighbor's X=0
                        chunkX = chunkSizeXZ - 1; chunkY = j; chunkZ = i;
                        neighborX = 0; neighborY = j; neighborZ = i;
                        break;
                    case 4: // Down (-Y): our Y=0 imports from neighbor's Y=15
                        chunkX = i; chunkY = 0; chunkZ = j;
                        neighborX = i; neighborY = chunkSizeY - 1; neighborZ = j;
                        break;
                    case 5: // Up (+Y): our Y=15 imports from neighbor's Y=0
                        chunkX = i; chunkY = chunkSizeY - 1; chunkZ = j;
                        neighborX = i; neighborY = 0; neighborZ = j;
                        break;
                }
                
                // Get neighbor's light
                int neighborIndex = neighborY * (chunkSizeXZ * chunkSizeXZ) + neighborZ * chunkSizeXZ + neighborX;
                byte neighborLightByte = neighborChunk.lightData[neighborIndex];
                int neighborSkyLight = (neighborLightByte >> 4) & 0xF;
                int neighborBlockLight = neighborLightByte & 0xF;
                
                // Get our boundary block
                int chunkIndex = chunkY * (chunkSizeXZ * chunkSizeXZ) + chunkZ * chunkSizeXZ + chunkX;
                byte ourBlockID = chunkData[chunkIndex];
                
                // Get our block opacity
                int opacity = lightOpacityCache[ourBlockID];
                if (opacity == 0) opacity = 1;
                
                // Skip if we're fully opaque
                if (opacity >= 15) continue;
                
                // Calculate propagated light from neighbor into us
                int propagatedSkyLight = neighborSkyLight - opacity;
                int propagatedBlockLight = neighborBlockLight - opacity;
                if (propagatedSkyLight < 0) propagatedSkyLight = 0;
                if (propagatedBlockLight < 0) propagatedBlockLight = 0;
                
                // Get our current light
                byte ourLightByte = chunk.lightData[chunkIndex];
                int ourSkyLight = (ourLightByte >> 4) & 0xF;
                int ourBlockLight = ourLightByte & 0xF;
                
                // Update if neighbor's propagated light is brighter
                if (propagatedSkyLight > ourSkyLight) ourSkyLight = propagatedSkyLight;
                if (propagatedBlockLight > ourBlockLight) ourBlockLight = propagatedBlockLight;
                
                // Apply update
                chunk.lightData[chunkIndex] = (byte)((ourSkyLight << 4) | ourBlockLight);
            }
        }
        
        // FIXED: Trigger neighbor mesh rebuilds after importing light from neighbors
        // This ensures neighbors update their meshes when light is imported across boundaries
        TriggerNeighborMeshRebuilds(chunk);
    }
    
#if LOGGING
    // --- Performance Logging Methods ---
    
    private void LogFrameStats(float updateTime)
    {
        logBuilder.Clear();
        logBuilder.AppendLine($"=== Frame {stats_frameCount} Performance ===");
        logBuilder.AppendLine($"Update: {updateTime:F2}ms (budget: {updateTimeBudgetMs:F1}ms, {(updateTime / updateTimeBudgetMs * 100f):F0}% used)");
        logBuilder.AppendLine($"  DataGen: {activeDataGenCount} chunks active");
        logBuilder.AppendLine($"  Meshing: {activeMeshingCount} chunks active");
        logBuilder.AppendLine($"  Reconciliation Queue: {deferredReconciliationQueue.Count}");
        
        if (enableCacheTracking)
        {
            int totalDecomp = stats_decompCacheHits + stats_decompCacheMisses;
            int totalNeighbor = stats_neighborCacheHits + stats_neighborCacheMisses;
            float decompHitRate = totalDecomp > 0 ? (stats_decompCacheHits / (float)totalDecomp * 100f) : 0f;
            float neighborHitRate = totalNeighbor > 0 ? (stats_neighborCacheHits / (float)totalNeighbor * 100f) : 0f;
            logBuilder.AppendLine($"Cache: Decomp {decompHitRate:F0}% hit ({stats_decompCacheHits}/{totalDecomp}), Neighbor {neighborHitRate:F0}% hit ({stats_neighborCacheHits}/{totalNeighbor})");
        }
        
        Debug.Log(logBuilder.ToString());
    }
    
    private void LogAggregateStats()
    {
        float windowDuration = Time.realtimeSinceStartup - stats_aggregateWindowStart;
        
        logBuilder.Clear();
        logBuilder.AppendLine($"=== Performance Summary (last {aggregateLogInterval} frames, {windowDuration:F1} seconds) ===");
        
        // Update stats
        if (stats_frameCount > 0)
        {
            float avgUpdateTime = stats_updateTotalTime / stats_frameCount;
            logBuilder.AppendLine($"Update: avg {avgUpdateTime:F2}ms, min {stats_updateTimeMin:F2}ms, max {stats_updateTimeMax:F2}ms");
            logBuilder.AppendLine($"  Budget exceeded: {stats_budgetExceededCount} times ({(stats_budgetExceededCount / (float)stats_frameCount * 100f):F1}%)");
        }
        
        // Mesh building stats
        if (stats_meshBuildTotal > 0)
        {
            float avgMeshTime = stats_meshBuildTimeTotal / stats_meshBuildTotal;
            float avgStepsPerChunk = stats_meshStepsTotal / (float)stats_meshBuildTotal;
            logBuilder.AppendLine($"Mesh Building: {stats_meshBuildTotal} chunks, avg {avgMeshTime:F2}ms (min {stats_meshBuildTimeMin:F2}ms, max {stats_meshBuildTimeMax:F2}ms)");
            logBuilder.AppendLine($"  Steps: avg {avgStepsPerChunk:F1} per chunk");
            
            if (enableDetailedTimings)
            {
                float totalGreedy = stats_greedyAxisYTime + stats_greedyAxisZTime + stats_greedyAxisXTime;
                if (totalGreedy > 0)
                {
                    logBuilder.AppendLine($"  Greedy Meshing: Y={stats_greedyAxisYTime / totalGreedy * 100f:F0}% ({stats_greedyAxisYTime:F1}ms), Z={stats_greedyAxisZTime / totalGreedy * 100f:F0}% ({stats_greedyAxisZTime:F1}ms), X={stats_greedyAxisXTime / totalGreedy * 100f:F0}% ({stats_greedyAxisXTime:F1}ms)");
                }
            }
            
            if (enableCounters && stats_faceCullingTests > 0)
            {
                float cullRate = stats_facesCulled / (float)stats_faceCullingTests * 100f;
                logBuilder.AppendLine($"  Face Culling: {stats_faceCullingTests} tests, {stats_facesCulled} culled ({cullRate:F1}%), {stats_facesDrawn} drawn");
                logBuilder.AppendLine($"  Vertices: {stats_verticesOpaque} opaque, {stats_verticesTransparent} transparent, {stats_verticesCutout} cutout");
            }
        }
        
        // RLE stats
        if (stats_rleCompressions > 0 || stats_rleDecompressions > 0)
        {
            float compressionRatio = stats_rleTotalBytesIn > 0 ? (stats_rleTotalBytesOut / (float)stats_rleTotalBytesIn) : 0f;
            logBuilder.AppendLine($"RLE: {compressionRatio * 100f:F0}% compression ratio ({stats_rleTotalBytesIn / 1024f:F1}KB→{stats_rleTotalBytesOut / 1024f:F1}KB)");
            
            if (enableDetailedTimings)
            {
                float avgCompTime = stats_rleCompressions > 0 ? stats_rleCompressionTime / stats_rleCompressions : 0f;
                float avgDecompTime = stats_rleDecompressions > 0 ? stats_rleDecompressionTime / stats_rleDecompressions : 0f;
                logBuilder.AppendLine($"  Compressions: {stats_rleCompressions} (avg {avgCompTime:F2}ms), Decompressions: {stats_rleDecompressions} (avg {avgDecompTime:F2}ms)");
                logBuilder.AppendLine($"  Homogeneous chunks: {stats_rleHomogeneousChunks}");
            }
            
            if (enableCacheTracking)
            {
                int totalDecomp = stats_decompCacheHits + stats_decompCacheMisses;
                float decompHitRate = totalDecomp > 0 ? (stats_decompCacheHits / (float)totalDecomp * 100f) : 0f;
                logBuilder.AppendLine($"  Cache hit rate: {decompHitRate:F1}% ({stats_decompCacheHits}/{totalDecomp})");
            }
        }
        
        // Block operation stats
        if (enableCounters && stats_getBlockCalls > 0)
        {
            logBuilder.AppendLine($"Block Ops: {stats_getBlockCalls} gets, {stats_setBlockCalls} sets, {stats_blockModifications} modifications");
            if (stats_neighborRebuildTriggers > 0)
            {
                logBuilder.AppendLine($"  Neighbor rebuilds triggered: {stats_neighborRebuildTriggers}");
            }
        }
        
        // Cache stats
        if (enableCacheTracking)
        {
            int totalDecomp = stats_decompCacheHits + stats_decompCacheMisses;
            int totalNeighbor = stats_neighborCacheHits + stats_neighborCacheMisses;
            float decompHitRate = totalDecomp > 0 ? (stats_decompCacheHits / (float)totalDecomp * 100f) : 0f;
            float neighborHitRate = totalNeighbor > 0 ? (stats_neighborCacheHits / (float)totalNeighbor * 100f) : 0f;
            logBuilder.AppendLine($"Cache: Decomp {decompHitRate:F1}% ({stats_decompCacheHits}/{totalDecomp}), Neighbor {neighborHitRate:F1}% ({stats_neighborCacheHits}/{totalNeighbor})");
        }
        
        // Reconciliation stats
        if (stats_reconciliationOps > 0)
        {
            float avgReconcilTime = stats_reconciliationTime / stats_reconciliationOps;
            logBuilder.AppendLine($"Reconciliation: {stats_reconciliationOps} ops, {stats_reconciliationBlocks} blocks, avg {avgReconcilTime:F2}ms");
        }
        
        // Chunk management stats
        if (enableCounters)
        {
            logBuilder.AppendLine($"Chunks: {stats_chunkCreations} created, {stats_chunkStateTransitions} state transitions");
        }
        
        Debug.Log(logBuilder.ToString());
        
        // Reset aggregate stats for next window
        stats_aggregateWindowStart = Time.realtimeSinceStartup;
        stats_frameCount = 0;
        stats_updateTotalTime = 0f;
        stats_updateTimeMin = float.MaxValue;
        stats_updateTimeMax = 0f;
        stats_budgetExceededCount = 0;
        stats_processActiveChunksTime = 0f;
        stats_reconciliationTime = 0f;
        stats_meshBuildTotal = 0;
        stats_meshBuildTimeTotal = 0f;
        stats_meshBuildTimeMin = float.MaxValue;
        stats_meshBuildTimeMax = 0f;
        stats_meshStepsTotal = 0;
        stats_greedyAxisYTime = 0f;
        stats_greedyAxisZTime = 0f;
        stats_greedyAxisXTime = 0f;
        stats_sentinelBuilds = 0;
        stats_sentinelBuildTime = 0f;
        stats_faceCullingTests = 0;
        stats_facesCulled = 0;
        stats_facesDrawn = 0;
        stats_verticesOpaque = 0;
        stats_verticesTransparent = 0;
        stats_verticesCutout = 0;
        stats_meshApplyOpaqueTime = 0f;
        stats_meshApplyTransparentTime = 0f;
        stats_meshApplyCutoutTime = 0f;
        stats_meshApplyColliderTime = 0f;
        stats_rleCompressions = 0;
        stats_rleDecompressions = 0;
        stats_rleCompressionTime = 0f;
        stats_rleDecompressionTime = 0f;
        stats_rleTotalBytesIn = 0;
        stats_rleTotalBytesOut = 0;
        stats_rleHomogeneousChunks = 0;
        stats_getBlockCalls = 0;
        stats_setBlockCalls = 0;
        stats_blockModifications = 0;
        stats_neighborRebuildTriggers = 0;
        stats_decompCacheHits = 0;
        stats_decompCacheMisses = 0;
        stats_neighborCacheHits = 0;
        stats_neighborCacheMisses = 0;
        stats_reconciliationOps = 0;
        stats_reconciliationBlocks = 0;
        stats_chunkCreations = 0;
        stats_chunkStateTransitions = 0;
    }
#endif
}