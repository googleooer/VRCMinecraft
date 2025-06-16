using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRRefAssist;
using System.Text; // For StringBuilder

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
    [Tooltip("Prefab for McChunk. Must have McChunk script attached.")]
    public GameObject chunkPrefab;
    private McChunk[] chunks_1D;
    private bool[] chunkDataFinalized_1D;

    [Header("Voxel Data")]
    // OPTIMIZATION: World data is now ushort[] to store pre-packed block properties.
    public ushort[] data;

    private int worldSizeX_voxels;
    private int worldSizeY_voxels;
    private int worldSizeZ_voxels;
    private int layerSize_voxels_for_1D_data;
    private int totalWorldVoxels;
    private int totalWorldChunks;

    [HideInInspector] public int chunkOffsetX;
    [HideInInspector] public int chunkOffsetY;
    [HideInInspector] public int chunkOffsetZ;

    [HideInInspector] public int globalVoxelOffsetX;
    [HideInInspector] public int globalVoxelOffsetY;
    [HideInInspector] public int globalVoxelOffsetZ;

    private int currentColumnTerrainSurfaceY; // Used in PopulateDataSliceForCurrentTargetChunk

    [Header("Generators")]
    [Tooltip("Reference to the McTerrainGenerator for base terrain and structure placement.")]
    [SerializeField, FindObjectOfType(true)]
    private McTerrainGenerator terrainGenerator;
    [Tooltip("Reference to the McBlockTypeManager for getting block properties.")]
    [SerializeField, FindObjectOfType(true)]
    private McBlockTypeManager blockTypeManager;


    [Header("Performance Parameters (McWorld)")]
    [Tooltip("Number of voxels to process for the *current chunk's data* per step. Min 1.")]
    public int voxelsPerChunkDataProcessingSlice = 128;
    [Tooltip("Delay (seconds) between processing steps for world generation (data slices or moving to next chunk). 0 for frame-by-frame.")]
    public float worldProcessingStepDelay = 0f;
    [Tooltip("How often (seconds) McWorld checks its queue to start rebuilding chunk meshes.")]
    public float chunkRebuildInterval = 0.03f; // Approx 33 FPS check
    [Tooltip("Max number of chunks McWorld will start rebuilding in a single interval (if neighbors' data is ready).")]
    public int maxChunksToRebuildPerInterval = 1;
    [Tooltip("Neighboring chunks processing mode for async rebuilds. 0 = Off, 1 = Normal (wait for neighbors), 2 = Regenerate (build this, queue neighbors).")]
    public int neighboringChunksProcessingMode = 1;


    [Header("Performance Parameters (Passed to Chunks)")]
    [Tooltip("Number of voxels each McChunk processes per slice during its *asynchronous full* mesh build.")]
    public int voxelsPerSliceInChunks = 256;

    [Header("Debug")]
    #if UNITY_EDITOR
    public bool enableVerboseLogging = true;
    #endif
    private StringBuilder logBuilder_McWorld;
    private float currentChunkDataGen_StartTime_Total;
    private float currentChunkDataGen_StartTime_Slice;
    private int currentChunkDataGen_SliceCount;

    // Chunk Rebuild Queue (Circular Buffer)
    private McChunk[] chunkRebuildQueue;
    private int chunkRebuildQueue_head = 0;
    private int chunkRebuildQueue_tail = 0;
    private int chunkRebuildQueue_count = 0;
    private const int MAX_REBUILD_QUEUE_SIZE = 256;

    // World Processing State (Radial Iteration for Chunks)
    private int proc_radius = 0;
    private int proc_dx = 0, proc_dy = 0, proc_dz = 0;
    private bool inst_shellIterationInitialized = false;

    // Per-Chunk Data Generation State
    private int dataGen_vox_x_in_chunk = 0;
    private int dataGen_vox_y_in_chunk = 0;
    private int dataGen_vox_z_in_chunk = 0;
    private bool currentChunkDataBeingPopulated = false;

    private bool isProcessingWorld = false;
    private int proc_lastLoggedPercent = -1;
    private int debug_worldStepCallCount = 0;
    private int chunksProcessedAndInstantiatedCount = 0;

    // Radial Iterator state for chunk instantiation
    private int inst_radius_iterator = 0;
    private int inst_dx_iterator = 0;
    private int inst_dy_iterator = 0;
    private int inst_dz_iterator = 0;

    // Cached values from TerrainGenerator & BlockTypeManager
    private ushort cachedPackedGrassData;
    private ushort cachedPackedStoneData;
    private ushort cachedPackedDirtData;
    private ushort cachedPackedWaterData;
    private int cachedSeaLevel;
    
    // Caching for PackBlockData to avoid repeated lookups
    private byte _lastPackedBlockId = 255; // Use an invalid ID to ensure the first call is always a full pack
    private ushort _lastPackedUshort;


    // Neighbor offsets for performance.
    private readonly int[] neighbor_dx_offsets = new int[] { 1, -1, 0,  0, 0,  0 };
    private readonly int[] neighbor_dy_offsets = new int[] { 0,  0, 1, -1, 0,  0 };
    private readonly int[] neighbor_dz_offsets = new int[] { 0,  0, 0,  0, 1, -1 };


    void Start()
    {
        // Critical dependency checks
        if (chunkPrefab == null) { Debug.LogError("[McWorld] Chunk Prefab is not assigned! Aborting."); this.enabled = false; return; }
        if (chunkPrefab.GetComponent<McChunk>() == null) { Debug.LogError($"[McWorld] Chunk Prefab '{chunkPrefab.name}' missing McChunk script! Aborting."); this.enabled = false; return; }
        if (terrainGenerator == null) { Debug.LogError("[McWorld] McTerrainGenerator is not assigned! Aborting."); this.enabled = false; return; }
        if (blockTypeManager == null) { Debug.LogError("[McWorld] McBlockTypeManager is not assigned! Aborting."); this.enabled = false; return; }


        // Parameter validation
        if (voxelsPerChunkDataProcessingSlice < 1) voxelsPerChunkDataProcessingSlice = 1;
        if (worldProcessingStepDelay < 0f) worldProcessingStepDelay = 0f;

        logBuilder_McWorld = new StringBuilder(512);

        InitializeWorldParameters();
        InitializeChunkStorageAndFlags();
        InitializeAndAllocateVoxelData();

        int actualWorldSeed = worldSeedString.GetHashCode();
        terrainGenerator.InitializeGenerator(actualWorldSeed);

        // Pre-pack and cache common block types from the terrain generator
        cachedPackedGrassData = PackBlockData(terrainGenerator.grassBlockID);
        cachedPackedStoneData = PackBlockData(terrainGenerator.stoneBlockID);
        cachedPackedDirtData = PackBlockData(terrainGenerator.dirtBlockID);
        cachedPackedWaterData = PackBlockData(terrainGenerator.waterBlockID);
        cachedSeaLevel = terrainGenerator.seaLevel;

        // Initialize rebuild queue
        chunkRebuildQueue = new McChunk[MAX_REBUILD_QUEUE_SIZE];
        chunkRebuildQueue_head = 0;
        chunkRebuildQueue_tail = 0;
        chunkRebuildQueue_count = 0;

#if UNITY_EDITOR
        if (enableVerboseLogging) Debug.Log("[McWorld.Start] Initializing Interleaved Radial World Processing.");
#endif
        StartWorldProcessing();

        SendCustomEventDelayedSeconds(nameof(ProcessChunkRebuildQueue), chunkRebuildInterval);
    }

    void InitializeWorldParameters()
    {
        worldDimensionX = Mathf.Max(1, worldDimensionX);
        worldDimensionY = Mathf.Max(1, worldDimensionY);
        worldDimensionZ = Mathf.Max(1, worldDimensionZ);
        chunkSizeXZ = Mathf.Max(1, chunkSizeXZ);
        chunkSizeY = Mathf.Max(1, chunkSizeY);

        chunkOffsetX = worldDimensionX / 2;
        chunkOffsetY = worldDimensionY / 2;
        chunkOffsetZ = worldDimensionZ / 2;

        worldSizeX_voxels = worldDimensionX * chunkSizeXZ;
        worldSizeY_voxels = worldDimensionY * chunkSizeY;
        worldSizeZ_voxels = worldDimensionZ * chunkSizeXZ;

        globalVoxelOffsetX = worldSizeX_voxels / 2;
        globalVoxelOffsetY = worldSizeY_voxels / 2;
        globalVoxelOffsetZ = worldSizeZ_voxels / 2;

        layerSize_voxels_for_1D_data = worldSizeX_voxels * worldSizeZ_voxels;
        totalWorldVoxels = layerSize_voxels_for_1D_data * worldSizeY_voxels;
        totalWorldChunks = worldDimensionX * worldDimensionY * worldDimensionZ;
    }

    void InitializeChunkStorageAndFlags()
    {
        if (totalWorldChunks <= 0) {
            Debug.LogError("[McWorld] totalWorldChunks is zero or negative. Cannot initialize chunk storage.");
            this.enabled = false;
            return;
        }
        chunks_1D = new McChunk[totalWorldChunks];
        chunkDataFinalized_1D = new bool[totalWorldChunks];
    }
    
    // OPTIMIZATION: Packs block properties into a single ushort for efficient storage and access.
    private ushort PackBlockData(byte blockID)
    {
        // Caching optimization: if the requested ID is the same as the last one, return the cached packed value.
        // This is highly effective during terrain generation where long runs of the same block type are common.
        if (blockID == _lastPackedBlockId)
        {
            return _lastPackedUshort;
        }

        if (blockID == 0) // Air block
        {
            _lastPackedBlockId = 0;
            _lastPackedUshort = 0;
            return 0;
        }

        ushort packedData = blockID; // Lower 8 bits are the block ID

        // Pack IsSolid (bit 8)
        if (blockTypeManager.GetBlockIsSolid(blockID))
        {
            packedData |= (1 << 8);
        }

        // Pack VisibilityType (bits 9-11)
        BlockVisibilityType visibility = blockTypeManager.GetBlockVisibilityType(blockID);
        packedData |= (ushort)((int)visibility << 9);

        // Pack ShapeType (bit 12)
        McBlockShapeType shape = blockTypeManager.GetBlockShapeType(blockID);
        packedData |= (ushort)((int)shape << 12);
        
        // Cache the newly packed data
        _lastPackedBlockId = blockID;
        _lastPackedUshort = packedData;
        
        return packedData;
    }


    private int ChunkArrayCoordsTo1D(int arrayX, int arrayY, int arrayZ)
    {
        if (arrayX < 0 || arrayX >= worldDimensionX ||
            arrayY < 0 || arrayY >= worldDimensionY ||
            arrayZ < 0 || arrayZ >= worldDimensionZ)
        {
            return -1;
        }
        return (arrayZ * worldDimensionX * worldDimensionY) + (arrayY * worldDimensionX) + arrayX;
    }

    void InitializeAndAllocateVoxelData()
    {
        if (totalWorldVoxels <= 0) {
            Debug.LogError("[McWorld] Total world voxels is zero or negative. Cannot allocate data array.");
            this.enabled = false; return;
        }
        // OPTIMIZATION: Allocate ushort array.
        data = new ushort[totalWorldVoxels];
    }

    public void StartWorldProcessing()
    {
        if (isProcessingWorld) { Debug.LogWarning("[McWorld] StartWorldProcessing called while already processing."); return; }
        if (totalWorldChunks == 0) { Debug.LogWarning("[McWorld] World has zero total chunks. Skipping world processing."); isProcessingWorld = false; return; }

#if UNITY_EDITOR
        if (enableVerboseLogging) Debug.Log($"[McWorld] Initializing Interleaved Radial World Processing. Target chunk (Rel 0,0,0). Voxels per data slice: {voxelsPerChunkDataProcessingSlice}. Step delay: {worldProcessingStepDelay}s.");
#endif

        isProcessingWorld = true;
        proc_radius = 0; proc_dx = 0; proc_dy = 0; proc_dz = 0;

        inst_radius_iterator = 0; inst_dx_iterator = 0; inst_dy_iterator = 0; inst_dz_iterator = 0;
        inst_shellIterationInitialized = true;

        dataGen_vox_x_in_chunk = 0; dataGen_vox_y_in_chunk = 0; dataGen_vox_z_in_chunk = 0;
        currentChunkDataBeingPopulated = true;
        currentChunkDataGen_StartTime_Total = Time.realtimeSinceStartup;
        currentChunkDataGen_StartTime_Slice = Time.realtimeSinceStartup;
        currentChunkDataGen_SliceCount = 0;

        proc_lastLoggedPercent = -1;
        debug_worldStepCallCount = 0;
        chunksProcessedAndInstantiatedCount = 0;

        ProcessNextWorldStep();
    }

    public void ProcessNextWorldStep()
    {
        if (!isProcessingWorld) return;
        debug_worldStepCallCount++;

        int currentArrayCX = proc_dx + chunkOffsetX;
        int currentArrayCY = proc_dy + chunkOffsetY;
        int currentArrayCZ = proc_dz + chunkOffsetZ;

        if (currentArrayCX < 0 || currentArrayCX >= worldDimensionX ||
            currentArrayCY < 0 || currentArrayCY >= worldDimensionY ||
            currentArrayCZ < 0 || currentArrayCZ >= worldDimensionZ)
        {
#if UNITY_EDITOR
            if (enableVerboseLogging) Debug.LogWarning($"[McWorld] ProcessNextWorldStep: Current target chunk Arr({currentArrayCX},{currentArrayCY},{currentArrayCZ}) is out of bounds. Rel({proc_dx},{proc_dy},{proc_dz}). Advancing iterator.");
#endif

            bool foundNext = AdvanceRadialIterator();
            if (foundNext && isProcessingWorld) {
                currentChunkDataBeingPopulated = true;
                currentChunkDataGen_StartTime_Total = Time.realtimeSinceStartup;
                currentChunkDataGen_StartTime_Slice = Time.realtimeSinceStartup;
                currentChunkDataGen_SliceCount = 0;
                dataGen_vox_x_in_chunk = 0; dataGen_vox_y_in_chunk = 0; dataGen_vox_z_in_chunk = 0;
                ScheduleNextWorldStep();
            }
            else if (isProcessingWorld) {
#if UNITY_EDITOR
                if (enableVerboseLogging) Debug.Log($"[McWorld] World Processing seems complete (radial iterator exhausted). Processed {chunksProcessedAndInstantiatedCount}/{totalWorldChunks} chunks. Total steps: {debug_worldStepCallCount}.");
#endif
                isProcessingWorld = false;
            }
            return;
        }

        if (currentChunkDataBeingPopulated)
        {
            bool dataForThisChunkCompleted = PopulateDataSliceForCurrentTargetChunk();
            if (dataForThisChunkCompleted)
            {
#if UNITY_EDITOR
                if (enableVerboseLogging)
                {
                    float totalDataGenTime = (Time.realtimeSinceStartup - currentChunkDataGen_StartTime_Total) * 1000f;
                    logBuilder_McWorld.Clear();
                    logBuilder_McWorld.AppendFormat("[McWorld.DataGen] Finished data for chunk Arr({0},{1},{2}), Rel({3},{4},{5}). Slices: {6}. Total Time: {7:F2} ms.",
                                                   currentArrayCX, currentArrayCY, currentArrayCZ, proc_dx, proc_dy, proc_dz, currentChunkDataGen_SliceCount + 1, totalDataGenTime);
                    Debug.Log(logBuilder_McWorld.ToString());
                }
#endif
                
                // Note: Structure placement could also be optimized to work with packed data.
                terrainGenerator.PlaceFeaturesInChunk(currentArrayCX, currentArrayCY, currentArrayCZ);

                int chunk1DIndex_current = ChunkArrayCoordsTo1D(currentArrayCX, currentArrayCY, currentArrayCZ);
                if (chunk1DIndex_current != -1) {
                    chunkDataFinalized_1D[chunk1DIndex_current] = true;

                    if (chunks_1D[chunk1DIndex_current] == null)
                    {
                        InstantiateAndConfigureChunk(currentArrayCX, currentArrayCY, currentArrayCZ, proc_dx, proc_dy, proc_dz);
                    } else {
                        RequestChunkMeshUpdate(chunks_1D[chunk1DIndex_current]);
                    }
                } else {
                    Debug.LogError($"[McWorld] ProcessNextWorldStep: Invalid chunk 1D index for Arr({currentArrayCX},{currentArrayCY},{currentArrayCZ}) after data gen.");
                }

                chunksProcessedAndInstantiatedCount++;
                currentChunkDataBeingPopulated = false;

                if (totalWorldChunks > 0) {
                    int currentPercent = (chunksProcessedAndInstantiatedCount * 100) / totalWorldChunks;
                    if (currentPercent / 10 > proc_lastLoggedPercent / 10 && currentPercent <= 100) {
#if UNITY_EDITOR
                        if (enableVerboseLogging) Debug.Log($"[McWorld] World Processing: ~{currentPercent}% complete ({chunksProcessedAndInstantiatedCount}/{totalWorldChunks} chunks data finalized & instantiated).");
#endif
                        proc_lastLoggedPercent = currentPercent;
                    }
                }

                bool foundNext = AdvanceRadialIterator();
                if (foundNext) {
                    currentChunkDataBeingPopulated = true;
                    currentChunkDataGen_StartTime_Total = Time.realtimeSinceStartup;
                    currentChunkDataGen_StartTime_Slice = Time.realtimeSinceStartup;
                    currentChunkDataGen_SliceCount = 0;
                    dataGen_vox_x_in_chunk = 0; dataGen_vox_y_in_chunk = 0; dataGen_vox_z_in_chunk = 0;
                } else {
                    isProcessingWorld = false;
#if UNITY_EDITOR
                    if (enableVerboseLogging) Debug.Log($"[McWorld] All chunks processed and instantiated. Total: {chunksProcessedAndInstantiatedCount}. World steps: {debug_worldStepCallCount}.");
#endif
                }
            }
        }
        else
        {
#if UNITY_EDITOR
            if (enableVerboseLogging) Debug.LogWarning($"[McWorld] ProcessNextWorldStep: currentChunkDataBeingPopulated is false for C_rel({proc_dx},{proc_dy},{proc_dz}). Attempting to advance iterator.");
#endif
            bool foundNext = AdvanceRadialIterator();
            if (foundNext) {
                currentChunkDataBeingPopulated = true;
                currentChunkDataGen_StartTime_Total = Time.realtimeSinceStartup;
                currentChunkDataGen_StartTime_Slice = Time.realtimeSinceStartup;
                currentChunkDataGen_SliceCount = 0;
                dataGen_vox_x_in_chunk = 0; dataGen_vox_y_in_chunk = 0; dataGen_vox_z_in_chunk = 0;
            }
            else isProcessingWorld = false;
        }

        if (isProcessingWorld) ScheduleNextWorldStep();
    }

    private void ScheduleNextWorldStep() {
        if (worldProcessingStepDelay > 0.0001f) SendCustomEventDelayedSeconds(nameof(ProcessNextWorldStep), worldProcessingStepDelay);
        else SendCustomEventDelayedFrames(nameof(ProcessNextWorldStep), 1);
    }

    private bool PopulateDataSliceForCurrentTargetChunk()
    {
        int voxelsInDataSliceProcessed = 0;
        
        // Use pre-packed ushort data for efficiency
        ushort grassData = cachedPackedGrassData;
        ushort stoneData = cachedPackedStoneData;
        ushort dirtData = cachedPackedDirtData;
        ushort waterData = cachedPackedWaterData;
        int currentSeaLevel = cachedSeaLevel;

        while (voxelsInDataSliceProcessed < voxelsPerChunkDataProcessingSlice)
        {
            if (dataGen_vox_x_in_chunk >= chunkSizeXZ)
            {
#if UNITY_EDITOR
                if (enableVerboseLogging && voxelsInDataSliceProcessed > 0)
                {
                    float sliceDuration = (Time.realtimeSinceStartup - currentChunkDataGen_StartTime_Slice) * 1000f;
                    logBuilder_McWorld.Clear();
                    logBuilder_McWorld.AppendFormat("[McWorld.DataGen] Slice {0} (final segment for chunk C_Rel({1},{2},{3})) completed. Voxels: {4}. Time: {5:F2} ms.",
                                                    currentChunkDataGen_SliceCount, proc_dx, proc_dy, proc_dz, voxelsInDataSliceProcessed, sliceDuration);
                    Debug.Log(logBuilder_McWorld.ToString());
                }
#endif
                return true;
            }

            if (dataGen_vox_y_in_chunk == 0)
            {
                int currentGlobalX_forHeight = (proc_dx * chunkSizeXZ) + dataGen_vox_x_in_chunk;
                int currentGlobalZ_forHeight = (proc_dz * chunkSizeXZ) + dataGen_vox_z_in_chunk;
                currentColumnTerrainSurfaceY = terrainGenerator.GetBaseTerrainHeight(currentGlobalX_forHeight, currentGlobalZ_forHeight);
            }

            int currentGlobalY = (proc_dy * chunkSizeY) + dataGen_vox_y_in_chunk;
            ushort blockData = 0; // Default to air

            if (currentGlobalY == currentColumnTerrainSurfaceY) blockData = grassData;
            else if (currentGlobalY < currentColumnTerrainSurfaceY) {
                if (currentGlobalY >= currentColumnTerrainSurfaceY - 3) blockData = dirtData;
                else blockData = stoneData;
            }
            else {
                if (currentGlobalY <= currentSeaLevel) blockData = waterData;
            }

            int currentGlobalX_forIndex = (proc_dx * chunkSizeXZ) + dataGen_vox_x_in_chunk;
            int currentGlobalZ_forIndex = (proc_dz * chunkSizeXZ) + dataGen_vox_z_in_chunk;
            int dataIndex = GlobalPosToIndex(currentGlobalX_forIndex, currentGlobalY, currentGlobalZ_forIndex);
            if (dataIndex != -1) data[dataIndex] = blockData;

            voxelsInDataSliceProcessed++;

            dataGen_vox_y_in_chunk++;
            if (dataGen_vox_y_in_chunk >= chunkSizeY) {
                dataGen_vox_y_in_chunk = 0;
                dataGen_vox_z_in_chunk++;
                if (dataGen_vox_z_in_chunk >= chunkSizeXZ) {
                    dataGen_vox_z_in_chunk = 0;
                    dataGen_vox_x_in_chunk++;
                }
            }
        }
#if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            float sliceDuration = (Time.realtimeSinceStartup - currentChunkDataGen_StartTime_Slice) * 1000f;
            logBuilder_McWorld.Clear();
            logBuilder_McWorld.AppendFormat("[McWorld.DataGen] Slice {0} completed for C_Rel({1},{2},{3}). Voxels: {4}. Time: {5:F2} ms.",
                                            currentChunkDataGen_SliceCount, proc_dx, proc_dy, proc_dz, voxelsInDataSliceProcessed, sliceDuration);
            Debug.Log(logBuilder_McWorld.ToString());
        }
#endif
        currentChunkDataGen_SliceCount++;
        currentChunkDataGen_StartTime_Slice = Time.realtimeSinceStartup;
        return false;
    }

    private bool AdvanceRadialIterator()
    {
        dataGen_vox_x_in_chunk = 0; dataGen_vox_y_in_chunk = 0; dataGen_vox_z_in_chunk = 0;

        while (true)
        {
            if (!inst_shellIterationInitialized)
            {
                inst_dx_iterator = -inst_radius_iterator;
                inst_dy_iterator = -inst_radius_iterator;
                inst_dz_iterator = -inst_radius_iterator;
                inst_shellIterationInitialized = true;
            }
            else
            {
                inst_dz_iterator++;
                if (inst_dz_iterator > inst_radius_iterator) {
                    inst_dz_iterator = -inst_radius_iterator; inst_dy_iterator++;
                    if (inst_dy_iterator > inst_radius_iterator) {
                        inst_dy_iterator = -inst_radius_iterator; inst_dx_iterator++;
                        if (inst_dx_iterator > inst_radius_iterator) {
                            inst_radius_iterator++;
                            inst_shellIterationInitialized = false;
                            continue;
                        }
                    }
                }
            }

            int max_abs_coord_x = (worldDimensionX - 1) / 2 + ((worldDimensionX - 1) % 2);
            int max_abs_coord_y = (worldDimensionY - 1) / 2 + ((worldDimensionY - 1) % 2);
            int max_abs_coord_z = (worldDimensionZ - 1) / 2 + ((worldDimensionZ - 1) % 2);

            if (inst_radius_iterator > Mathf.Max(max_abs_coord_x, Mathf.Max(max_abs_coord_y, max_abs_coord_z)) + 1)
            {
                return false;
            }

            if (inst_radius_iterator == 0 || Mathf.Abs(inst_dx_iterator) == inst_radius_iterator || Mathf.Abs(inst_dy_iterator) == inst_radius_iterator || Mathf.Abs(inst_dz_iterator) == inst_radius_iterator)
            {
                proc_dx = inst_dx_iterator;
                proc_dy = inst_dy_iterator;
                proc_dz = inst_dz_iterator;

                int array_cx = proc_dx + chunkOffsetX;
                int array_cy = proc_dy + chunkOffsetY;
                int array_cz = proc_dz + chunkOffsetZ;

                if (array_cx >= 0 && array_cx < worldDimensionX &&
                    array_cy >= 0 && array_cy < worldDimensionY &&
                    array_cz >= 0 && array_cz < worldDimensionZ)
                {
                    int chunk1DIndex_radial = ChunkArrayCoordsTo1D(array_cx, array_cy, array_cz);
                    if (chunk1DIndex_radial != -1 && !chunkDataFinalized_1D[chunk1DIndex_radial])
                    {
                        return true;
                    }
                }
            }
        }
    }

    void InstantiateAndConfigureChunk(int array_cx, int array_cy, int array_cz, int centered_dx, int centered_dy, int centered_dz)
    {
        int chunk1DIndex = ChunkArrayCoordsTo1D(array_cx, array_cy, array_cz);
        if (chunk1DIndex == -1) {
            Debug.LogError($"[McWorld] InstantiateAndConfigureChunk: Invalid chunk 1D index for Arr({array_cx},{array_cy},{array_cz})");
            return;
        }

        if (chunks_1D[chunk1DIndex] != null) {
#if UNITY_EDITOR
            if (enableVerboseLogging) Debug.LogWarning($"[McWorld] InstantiateAndConfigureChunk: Chunk Arr({array_cx},{array_cy},{array_cz}) already exists. Requesting mesh update.");
#endif
            RequestChunkMeshUpdate(chunks_1D[chunk1DIndex]);
            return;
        }

        GameObject newChunkGO = (GameObject)Instantiate(chunkPrefab);
        if (newChunkGO == null) { Debug.LogError($"[McWorld] Instantiate FAILED for chunkPrefab at C_array({array_cx},{array_cy},{array_cz})."); return; }

        newChunkGO.name = $"Chunk_arr({array_cx},{array_cy},{array_cz})_cen({centered_dx},{centered_dy},{centered_dz})";
        newChunkGO.transform.SetParent(this.transform, false);
        newChunkGO.transform.localPosition = new Vector3(centered_dx * chunkSizeXZ, centered_dy * chunkSizeY, centered_dz * chunkSizeXZ);
        newChunkGO.transform.localRotation = Quaternion.identity;

        McChunk newChunkScript = newChunkGO.GetComponent<McChunk>();
        if (newChunkScript != null) {
            chunks_1D[chunk1DIndex] = newChunkScript;
            newChunkScript.chunkSizeXZ = this.chunkSizeXZ;
            newChunkScript.chunkSizeY = this.chunkSizeY;
            newChunkScript.voxelsPerSlice = this.voxelsPerSliceInChunks;
            newChunkScript.chunkX = centered_dx * this.chunkSizeXZ;
            newChunkScript.chunkY = centered_dy * this.chunkSizeY;
            newChunkScript.chunkZ = centered_dz * this.chunkSizeXZ;
            newChunkScript.template = false;
            newChunkScript.SetWorld(this);
            newChunkGO.SetActive(true);
        } else {
            Debug.LogError($"[McWorld] Failed to get McChunk script from instantiated prefab for C_array({array_cx},{array_cy},{array_cz}). GO: {newChunkGO.name}. Destroying GO.");
            if (newChunkGO != null) Destroy(newChunkGO);
        }
    }
    
    public void RequestChunkMeshUpdate(McChunk chunkToUpdate)
    {
        if (chunkToUpdate == null) return;
        
        for (int i = 0; i < chunkRebuildQueue_count; i++)
        {
            int index = (chunkRebuildQueue_head + i) % MAX_REBUILD_QUEUE_SIZE;
            if (chunkRebuildQueue[index] == chunkToUpdate) {
                return;
            }
        }

        if (chunkRebuildQueue_count < MAX_REBUILD_QUEUE_SIZE)
        {
            chunkRebuildQueue[chunkRebuildQueue_tail] = chunkToUpdate;
            chunkRebuildQueue_tail = (chunkRebuildQueue_tail + 1) % MAX_REBUILD_QUEUE_SIZE;
            chunkRebuildQueue_count++;
        }
        else {
#if UNITY_EDITOR
            if (enableVerboseLogging) Debug.LogWarning($"[McWorld] Chunk rebuild queue is full ({MAX_REBUILD_QUEUE_SIZE}). Dropping request for {chunkToUpdate.gameObject.name}.");
#endif
        }
    }

    public void ProcessChunkRebuildQueue()
    {
        int processedThisCall = 0;
        int itemsToPotentiallyCheck = chunkRebuildQueue_count;

        for (int i = 0; i < itemsToPotentiallyCheck && processedThisCall < maxChunksToRebuildPerInterval && chunkRebuildQueue_count > 0; i++)
        {
            McChunk chunkToBuild = chunkRebuildQueue[chunkRebuildQueue_head];

            if (chunkToBuild == null) {
                chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE;
                chunkRebuildQueue_count--;
                continue;
            }
            if (chunkToBuild.isBuildingMesh) {
                if (chunkRebuildQueue_count > 1) {
                    chunkRebuildQueue[chunkRebuildQueue_tail] = chunkToBuild;
                    chunkRebuildQueue_tail = (chunkRebuildQueue_tail + 1) % MAX_REBUILD_QUEUE_SIZE;
                    chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE;
                }
                continue;
            }

            bool canBuild = false;
            switch (neighboringChunksProcessingMode)
            {
                case 0: canBuild = true; break;
                case 1: canBuild = AreAllNeighborsDataFinalized(chunkToBuild); break;
                case 2: canBuild = true; break;
                default:
                    canBuild = AreAllNeighborsDataFinalized(chunkToBuild);
                    Debug.LogWarning($"[McWorld] Unknown neighboringChunksProcessingMode: {neighboringChunksProcessingMode}. Defaulting to mode 1.");
                    break;
            }

            if (canBuild)
            {
                chunkRebuildQueue[chunkRebuildQueue_head] = null;
                chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE;
                chunkRebuildQueue_count--;
                
                chunkToBuild.StartBuildMesh(false);
                processedThisCall++;

                if (neighboringChunksProcessingMode == 2)
                {
                    TriggerNeighborMeshRebuilds(chunkToBuild);
                }
            }
            else
            {
                if (chunkRebuildQueue_count > 1)
                {
                    chunkRebuildQueue[chunkRebuildQueue_tail] = chunkToBuild;
                    chunkRebuildQueue_tail = (chunkRebuildQueue_tail + 1) % MAX_REBUILD_QUEUE_SIZE;
                    chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE;
                }
            }
        }
        SendCustomEventDelayedSeconds(nameof(ProcessChunkRebuildQueue), chunkRebuildInterval);
    }

    private bool AreAllNeighborsDataFinalized(McChunk chunk)
    {
        if (chunk == null) return false;

        int centeredCX = chunk.chunkX / chunkSizeXZ;
        int centeredCY = chunk.chunkY / chunkSizeY;
        int centeredCZ = chunk.chunkZ / chunkSizeXZ;

        for (int i = 0; i < 6; i++)
        {
            int neighborCenteredX = centeredCX + neighbor_dx_offsets[i];
            int neighborCenteredY = centeredCY + neighbor_dy_offsets[i];
            int neighborCenteredZ = centeredCZ + neighbor_dz_offsets[i];

            int neighborArrayX = neighborCenteredX + chunkOffsetX;
            int neighborArrayY = neighborCenteredY + chunkOffsetY;
            int neighborArrayZ = neighborCenteredZ + chunkOffsetZ;

            int neighbor1DIndex = ChunkArrayCoordsTo1D(neighborArrayX, neighborArrayY, neighborArrayZ);
            if (neighbor1DIndex != -1)
            {
                if (!chunkDataFinalized_1D[neighbor1DIndex])
                {
                    return false;
                }
            }
        }
        return true;
    }

    private void TriggerNeighborMeshRebuilds(McChunk chunk)
    {
        if (chunk == null) return;

        int centeredCX = chunk.chunkX / chunkSizeXZ;
        int centeredCY = chunk.chunkY / chunkSizeY;
        int centeredCZ = chunk.chunkZ / chunkSizeXZ;

        for (int i = 0; i < 6; i++)
        {
            int neighborCenteredX = centeredCX + neighbor_dx_offsets[i];
            int neighborCenteredY = centeredCY + neighbor_dy_offsets[i];
            int neighborCenteredZ = centeredCZ + neighbor_dz_offsets[i];

            int neighborArrayX = neighborCenteredX + chunkOffsetX;
            int neighborArrayY = neighborCenteredY + chunkOffsetY;
            int neighborArrayZ = neighborCenteredZ + chunkOffsetZ;

            int neighbor1DIndex = ChunkArrayCoordsTo1D(neighborArrayX, neighborArrayY, neighborArrayZ);
            if (neighbor1DIndex != -1)
            {
                if (chunkDataFinalized_1D[neighbor1DIndex])
                {
                    McChunk neighborChunk = chunks_1D[neighbor1DIndex];
                    if (neighborChunk != null && !neighborChunk.isBuildingMesh)
                    {
                        RequestChunkMeshUpdate(neighborChunk);
                    }
                }
            }
        }
    }

    private int GlobalPosToIndex(int globalX, int globalY, int globalZ)
    {
        int arrayCoordX = globalX + globalVoxelOffsetX;
        int arrayCoordY = globalY + globalVoxelOffsetY;
        int arrayCoordZ = globalZ + globalVoxelOffsetZ;

        if (arrayCoordX < 0 || arrayCoordX >= worldSizeX_voxels ||
            arrayCoordY < 0 || arrayCoordY >= worldSizeY_voxels ||
            arrayCoordZ < 0 || arrayCoordZ >= worldSizeZ_voxels) return -1;

        return arrayCoordY * layerSize_voxels_for_1D_data + arrayCoordZ * worldSizeX_voxels + arrayCoordX;
    }
    
    // Gets the full packed ushort data at global voxel coordinates.
    public ushort GetBlock(int globalX, int globalY, int globalZ)
    {
        int index = GlobalPosToIndex(globalX, globalY, globalZ);
        if (index == -1) return 0; // Air for out-of-bounds
        if (data == null || index < 0 || index >= data.Length) {
            return 0;
        }
        return data[index];
    }
    
    private void _UpdateAffectedNeighbor(int neighborCenteredCX, int neighborCenteredCY, int neighborCenteredCZ,
                                     int neighborLocalPoiX, int neighborLocalPoiY, int neighborLocalPoiZ,
                                     bool rebuildImmediately) {
        int neighborArrX = neighborCenteredCX + chunkOffsetX;
        int neighborArrY = neighborCenteredCY + chunkOffsetY;
        int neighborArrZ = neighborCenteredCZ + chunkOffsetZ;

        int neighbor1DIdx = ChunkArrayCoordsTo1D(neighborArrX, neighborArrY, neighborArrZ);
        if (neighbor1DIdx != -1 && chunkDataFinalized_1D[neighbor1DIdx])
        {
            McChunk neighborChunk = GetChunkScript(neighborArrX, neighborArrY, neighborArrZ);
            if (neighborChunk != null) {
                if (rebuildImmediately) {
                    neighborChunk.StartBuildMesh(true);
                    RequestChunkMeshUpdate(neighborChunk);
                } else {
                    RequestChunkMeshUpdate(neighborChunk);
                }
            }
        }
    }

    // Sets the block type at global voxel coordinates and triggers mesh updates.
    public void SetBlock(int globalX, int globalY, int globalZ, byte blockType, bool rebuildImmediately)
    {
        int dataIndex = GlobalPosToIndex(globalX, globalY, globalZ);
        if (dataIndex == -1) {
#if UNITY_EDITOR
            if (enableVerboseLogging) Debug.LogWarning($"[McWorld.SetBlock] Attempted to set block outside world bounds at G({globalX},{globalY},{globalZ}).");
#endif
            return;
        }
        if (data == null || dataIndex < 0 || dataIndex >= data.Length) {
            Debug.LogError($"[McWorld.SetBlock] Index {dataIndex} out of bounds for data array at G({globalX},{globalY},{globalZ}). Aborting SetBlock.");
            return;
        }
        
        // Pack the new block data and check if it's actually different.
        ushort newPackedData = PackBlockData(blockType);
        if (data[dataIndex] == newPackedData) {
            return; // No change needed
        }

        data[dataIndex] = newPackedData; // Update voxel data

        int centeredChunkX = Mathf.FloorToInt((float)globalX / chunkSizeXZ);
        int centeredChunkY = Mathf.FloorToInt((float)globalY / chunkSizeY);
        int centeredChunkZ = Mathf.FloorToInt((float)globalZ / chunkSizeXZ);

        int array_cx = centeredChunkX + chunkOffsetX;
        int array_cy = centeredChunkY + chunkOffsetY;
        int array_cz = centeredChunkZ + chunkOffsetZ;

        McChunk targetChunk = GetChunkScript(array_cx, array_cy, array_cz);
        int localPoiX = globalX - (centeredChunkX * chunkSizeXZ);
        int localPoiY = globalY - (centeredChunkY * chunkSizeY);
        int localPoiZ = globalZ - (centeredChunkZ * chunkSizeXZ);


        if (targetChunk != null) {
            if (rebuildImmediately) {
                targetChunk.StartBuildMesh(true);
                RequestChunkMeshUpdate(targetChunk);
            } else {
                RequestChunkMeshUpdate(targetChunk);
            }
        } else {
#if UNITY_EDITOR
            if (enableVerboseLogging) Debug.LogWarning($"[McWorld.SetBlock] Target chunk at Arr({array_cx},{array_cy},{array_cz}) for G({globalX},{globalY},{globalZ}) is null. Cannot update mesh.");
#endif
        }

        // Update affected neighbors
        if (localPoiX == 0) _UpdateAffectedNeighbor(centeredChunkX - 1, centeredChunkY, centeredChunkZ, chunkSizeXZ - 1, localPoiY, localPoiZ, rebuildImmediately);
        if (localPoiX == chunkSizeXZ - 1) _UpdateAffectedNeighbor(centeredChunkX + 1, centeredChunkY, centeredChunkZ, 0, localPoiY, localPoiZ, rebuildImmediately);
        if (localPoiY == 0) _UpdateAffectedNeighbor(centeredChunkX, centeredChunkY - 1, centeredChunkZ, localPoiX, chunkSizeY - 1, localPoiZ, rebuildImmediately);
        if (localPoiY == chunkSizeY - 1) _UpdateAffectedNeighbor(centeredChunkX, centeredChunkY + 1, centeredChunkZ, localPoiX, 0, localPoiZ, rebuildImmediately);
        if (localPoiZ == 0) _UpdateAffectedNeighbor(centeredChunkX, centeredChunkY, centeredChunkZ - 1, localPoiX, localPoiY, chunkSizeXZ - 1, rebuildImmediately);
        if (localPoiZ == chunkSizeXZ - 1) _UpdateAffectedNeighbor(centeredChunkX, centeredChunkY, centeredChunkZ + 1, localPoiX, localPoiY, 0, rebuildImmediately);
    }

    public McChunk GetChunkScript(int array_cx, int array_cy, int array_cz)
    {
        int index = ChunkArrayCoordsTo1D(array_cx, array_cy, array_cz);
        if (index == -1) return null;
        if (chunks_1D == null || index < 0 || index >= chunks_1D.Length) {
            return null;
        }
        return chunks_1D[index];
    }
}
