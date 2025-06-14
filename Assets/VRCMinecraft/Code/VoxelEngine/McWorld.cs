using System.Collections.Generic;
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
    [Tooltip("Prefab for McChunk. Must have McChunk script attached.")]
    public GameObject chunkPrefab;
    private McChunk[][][] chunks; 
    private bool[][][] chunkDataFinalized; 

    [Header("Voxel Data")]
    public byte[] data; 

    // World size and offset parameters
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

    [Header("Generation Parameters")]
    // public float noiseScale = 70.0f; // Removed
    // public int baseTerrainHeight = 0; // Removed
    // public float heightVariationAmplitude = 10f; // Removed

    private int actualWorldSeed;
    // private float perlinSeedOffsetX; // Removed
    // private float perlinSeedOffsetZ; // Removed

    [Header("Generators")]
    [Tooltip("Reference to the McTerrainGenerator for base terrain and structure placement.")]
    [SerializeField, FindObjectOfType(true)] 
    private McTerrainGenerator terrainGenerator;

    [Header("Performance Parameters (McWorld)")]
    [Tooltip("Number of voxels to process for the *current chunk's data* per step. Min 1.")]
    public int voxelsPerChunkDataProcessingSlice = 128; 
    [Tooltip("Delay (seconds) between processing steps for world generation (data slices or moving to next chunk). 0 for frame-by-frame.")]
    public float worldProcessingStepDelay = 0f; 
    [Tooltip("How often (seconds) McWorld checks its queue to start rebuilding chunk meshes.")]
    public float chunkRebuildInterval = 0.03f;
    [Tooltip("Max number of chunks McWorld will start rebuilding in a single interval (if neighbors' data is ready).")]
    public int maxChunksToRebuildPerInterval = 1;
    [Tooltip("Neighboring chunks processing mode. 0 = Off (build chunk mesh immediately), 1 = Normal (wait for all direct neighbors' data), 2 = Regenerate (build chunk mesh immediately and queue ready neighbors for mesh rebuild).")]
    public int neighboringChunksProcessingMode = 1; // Default to 1 (Normal)

    [Header("Performance Parameters (Passed to Chunks)")]
    [Tooltip("Number of voxels each McChunk processes per slice during its mesh build.")]
    public int voxelsPerSliceInChunks = 256;
    [Tooltip("Use fast build mode for chunks (builds entire chunk without delays). Good for initial world gen.")]
    public bool useChunkFastBuildMode = false;
    [Tooltip("Automatically use fast build during world generation and normal mode during runtime.")]
    public bool useDynamicFastBuildMode = true;

    [Header("Performance Monitoring")]
    [Tooltip("Enable detailed timing logs")]
    public bool enableTimingLogs = true;
    [Tooltip("Log timing every N chunks processed")]
    public int timingLogInterval = 10;

    // --- State Variables ---
    private McChunk[] chunkRebuildQueue;
    private int chunkRebuildQueueCount = 0;
    private const int MAX_REBUILD_QUEUE_SIZE = 256;

    // Unified World Processing State (Radial Iteration for Chunks)
    private int proc_radius = 0; 
    private int proc_dx = 0, proc_dy = 0, proc_dz = 0; // Centered offset for current target chunk
    
    // MODIFIED: Corrected case for inst_shellIterationInitialized to match declaration
    private bool inst_shellIterationInitialized = false; // Was proc_shellIterationInitialized in V18, changed to inst_ to match other inst_ vars
    
    // Per-Chunk Data Generation State (within the current target chunk)
    private int dataGen_vox_x_in_chunk = 0; 
    private int dataGen_vox_y_in_chunk = 0;
    private int dataGen_vox_z_in_chunk = 0;
    private bool currentChunkDataBeingPopulated = false; 

    private bool isProcessingWorld = false; 
    private int proc_lastLoggedPercent = -1;
    private int debug_worldStepCallCount = 0;
    private int chunksProcessedAndInstantiatedCount = 0;

    // Phase 2: Radial Chunk Instantiation State - These were the problematic variables
    // Their usage was correct, but the declaration of inst_shellIterationInitialized had a typo in previous error reports (lowercase 'i')
    // Ensuring they are consistently named with `inst_` prefix for this phase.
    private int inst_radius_iterator = 0; // Renamed from proc_radius for clarity in this specific iterator context
    private int inst_dx_iterator = 0;     // Renamed from proc_dx
    private int inst_dy_iterator = 0;     // Renamed from proc_dy
    private int inst_dz_iterator = 0;     // Renamed from proc_dz

    // --- Timing Variables ---
    // Overall timing
    private float worldProcessingStartTime;
    private float worldProcessingEndTime;
    
    // Chunk processing timing
    private float currentChunkStartTime;
    private float totalChunkDataGenTime = 0f;
    private float totalChunkInstantiationTime = 0f;
    private float totalFeaturePlacementTime = 0f;
    private int chunksTimingLogged = 0;
    
    // Step timing
    private float lastStepStartTime;
    private float totalStepTime = 0f;
    private int stepTimingCount = 0;
    
    // Rebuild queue timing
    private float totalRebuildQueueTime = 0f;
    private int rebuildQueueProcessCount = 0;
    private float lastRebuildQueueStartTime;
    
    // Per-chunk detailed timing
    private float[] chunkDataGenTimes;
    private float[] chunkInstantiationTimes;
    private int chunkTimingIndex = 0;
    
    // Block modification timing
    private float totalSetBlockTime = 0f;
    private int setBlockCallCount = 0;
    private float lastSetBlockTime = 0f;

    void Start()
    {
        if (chunkPrefab == null) { Debug.LogError("[McWorld] Chunk Prefab is not assigned! Aborting."); this.enabled = false; return; }
        if (chunkPrefab.GetComponent<McChunk>() == null) { Debug.LogError($"[McWorld] Chunk Prefab '{chunkPrefab.name}' missing McChunk script! Aborting."); this.enabled = false; return; }
        if (terrainGenerator == null) { Debug.LogError("[McWorld] McTerrainGenerator is not assigned! Aborting."); this.enabled = false; return; }

        if (voxelsPerChunkDataProcessingSlice < 1) voxelsPerChunkDataProcessingSlice = 1;
        if (worldProcessingStepDelay < 0f) worldProcessingStepDelay = 0f;
        
        InitializeWorldParameters(); 
        InitializeChunkStorageAndFlags();
        InitializeAndAllocateVoxelData();

        actualWorldSeed = worldSeedString.GetHashCode();
        // perlinSeedOffsetX = (actualWorldSeed % 1000) * 1.23f + 10000.0f; // Removed
        // perlinSeedOffsetZ = ((actualWorldSeed / 1000) % 1000) * 1.45f + 20000.0f; // Removed

        terrainGenerator.InitializeGenerator(actualWorldSeed); // Modified to pass only actualWorldSeed

        chunkRebuildQueue = new McChunk[MAX_REBUILD_QUEUE_SIZE];
        chunkRebuildQueueCount = 0;

        // Initialize timing arrays
        if (enableTimingLogs)
        {
            chunkDataGenTimes = new float[totalWorldChunks];
            chunkInstantiationTimes = new float[totalWorldChunks];
            for (int i = 0; i < totalWorldChunks; i++)
            {
                chunkDataGenTimes[i] = 0f;
                chunkInstantiationTimes[i] = 0f;
            }
        }

        Debug.Log("[McWorld] Start(): Beginning Interleaved Radial World Processing.");
        StartWorldProcessing(); 

        SendCustomEventDelayedSeconds(nameof(ProcessChunkRebuildQueue), chunkRebuildInterval);
    }

    void InitializeWorldParameters()
    {
        worldDimensionX = Mathf.Max(1, worldDimensionX); worldDimensionY = Mathf.Max(1, worldDimensionY); worldDimensionZ = Mathf.Max(1, worldDimensionZ);
        chunkSizeXZ = Mathf.Max(1, chunkSizeXZ); chunkSizeY = Mathf.Max(1, chunkSizeY);
        
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
        chunks = new McChunk[worldDimensionX][][]; 
        chunkDataFinalized = new bool[worldDimensionX][][]; 

        for (int x = 0; x < worldDimensionX; x++)
        {
            chunks[x] = new McChunk[worldDimensionY][];
            chunkDataFinalized[x] = new bool[worldDimensionY][];
            for (int y = 0; y < worldDimensionY; y++) 
            {
                chunks[x][y] = new McChunk[worldDimensionZ];
                chunkDataFinalized[x][y] = new bool[worldDimensionZ]; 
            }
        }
    }

    void InitializeAndAllocateVoxelData()
    {
        if (totalWorldVoxels <= 0) { Debug.LogError("[McWorld] Total world voxels is zero."); this.enabled = false; return; }
        data = new byte[totalWorldVoxels]; 
    }

    public void StartWorldProcessing()
    {
        if (isProcessingWorld) { Debug.LogWarning("[McWorld] StartWorldProcessing called while already processing."); return; }
        if (totalWorldChunks == 0) { Debug.LogWarning("[McWorld] World has zero total chunks. Skipping."); isProcessingWorld = false; return; }
        
        Debug.Log($"[McWorld] Initializing Interleaved Radial World Processing. Target chunk (Rel 0,0,0). Voxels per data slice: {voxelsPerChunkDataProcessingSlice}. Step delay: {worldProcessingStepDelay}s.");
        
        // Reset timing variables
        if (enableTimingLogs)
        {
            worldProcessingStartTime = Time.realtimeSinceStartup;
            totalChunkDataGenTime = 0f;
            totalChunkInstantiationTime = 0f;
            totalFeaturePlacementTime = 0f;
            totalStepTime = 0f;
            stepTimingCount = 0;
            chunksTimingLogged = 0;
            chunkTimingIndex = 0;
        }
        
        isProcessingWorld = true;
        // proc_ variables are for the *current chunk being actively processed for data/instantiation*
        proc_radius = 0; proc_dx = 0; proc_dy = 0; proc_dz = 0; 
        
        // inst_ variables are for the *radial iterator itself*
        inst_radius_iterator = 0; inst_dx_iterator = 0; inst_dy_iterator = 0; inst_dz_iterator = 0;
        inst_shellIterationInitialized = true; 

        dataGen_vox_x_in_chunk = 0; dataGen_vox_y_in_chunk = 0; dataGen_vox_z_in_chunk = 0;
        currentChunkDataBeingPopulated = true; 

        proc_lastLoggedPercent = -1; debug_worldStepCallCount = 0; chunksProcessedAndInstantiatedCount = 0;
        
        // Record chunk start time
        if (enableTimingLogs)
        {
            currentChunkStartTime = Time.realtimeSinceStartup;
        }
        
        ProcessNextWorldStep();
    }

    public void ProcessNextWorldStep()
    {
        debug_worldStepCallCount++;
        if (!isProcessingWorld) return;

        // Start timing this step
        if (enableTimingLogs)
        {
            lastStepStartTime = Time.realtimeSinceStartup;
        }

        int currentArrayCX = proc_dx + chunkOffsetX;
        int currentArrayCY = proc_dy + chunkOffsetY;
        int currentArrayCZ = proc_dz + chunkOffsetZ;

        if (currentArrayCX < 0 || currentArrayCX >= worldDimensionX ||
            currentArrayCY < 0 || currentArrayCY >= worldDimensionY ||
            currentArrayCZ < 0 || currentArrayCZ >= worldDimensionZ)
        {
            Debug.LogError($"[McWorld] ProcessNextWorldStep: Current target chunk Arr({currentArrayCX},{currentArrayCY},{currentArrayCZ}) is out of bounds. Rel({proc_dx},{proc_dy},{proc_dz}). Advancing iterator.");
            bool foundNext = AdvanceRadialIterator(); // This will set new proc_dx, dy, dz
            if (foundNext && isProcessingWorld) { 
                currentChunkDataBeingPopulated = true; // Start populating for the new chunk
                if (enableTimingLogs) currentChunkStartTime = Time.realtimeSinceStartup;
                ScheduleNextWorldStep(); 
            }
            else if (isProcessingWorld) { 
                Debug.Log($"[McWorld] World Processing seems complete (iterator exhausted). Processed {chunksProcessedAndInstantiatedCount}/{totalWorldChunks}. Steps: {debug_worldStepCallCount}");
                if (enableTimingLogs) LogFinalTimingStats();
                isProcessingWorld = false;
            }
            return;
        }
        
        if (currentChunkDataBeingPopulated)
        {
            bool dataForThisChunkCompleted = PopulateDataSliceForCurrentTargetChunk();
            if (dataForThisChunkCompleted)
            {
                // Record data generation time
                float dataGenTime = 0f;
                if (enableTimingLogs)
                {
                    dataGenTime = Time.realtimeSinceStartup - currentChunkStartTime;
                    totalChunkDataGenTime += dataGenTime;
                    if (chunkTimingIndex < chunkDataGenTimes.Length)
                        chunkDataGenTimes[chunkTimingIndex] = dataGenTime;
                }
                
                // Time feature placement
                float featurePlacementStartTime = 0f;
                if (enableTimingLogs) featurePlacementStartTime = Time.realtimeSinceStartup;
                
                terrainGenerator.PlaceFeaturesInChunk(currentArrayCX, currentArrayCY, currentArrayCZ); 
                
                if (enableTimingLogs)
                {
                    float featurePlacementTime = Time.realtimeSinceStartup - featurePlacementStartTime;
                    totalFeaturePlacementTime += featurePlacementTime;
                }
                
                chunkDataFinalized[currentArrayCX][currentArrayCY][currentArrayCZ] = true; 

                if (chunks[currentArrayCX][currentArrayCY][currentArrayCZ] == null) 
                {
                    float instantiationStartTime = 0f;
                    if (enableTimingLogs) instantiationStartTime = Time.realtimeSinceStartup;
                    
                    InstantiateAndConfigureChunk(currentArrayCX, currentArrayCY, currentArrayCZ, proc_dx, proc_dy, proc_dz);
                    
                    if (enableTimingLogs)
                    {
                        float instantiationTime = Time.realtimeSinceStartup - instantiationStartTime;
                        totalChunkInstantiationTime += instantiationTime;
                        if (chunkTimingIndex < chunkInstantiationTimes.Length)
                            chunkInstantiationTimes[chunkTimingIndex] = instantiationTime;
                    }
                }
                chunksProcessedAndInstantiatedCount++;
                currentChunkDataBeingPopulated = false; 
                
                if (enableTimingLogs)
                {
                    chunkTimingIndex++;
                    chunksTimingLogged++;
                    
                    // Log timing stats periodically
                    if (chunksTimingLogged % timingLogInterval == 0)
                    {
                        LogPeriodicTimingStats();
                    }
                }

                if (totalWorldChunks > 0) {
                    int currentPercent = (chunksProcessedAndInstantiatedCount * 100) / totalWorldChunks;
                    if (currentPercent / 10 > proc_lastLoggedPercent / 10 && currentPercent <= 100) { 
                        Debug.Log($"[McWorld] World Processing: ~{currentPercent}% complete ({chunksProcessedAndInstantiatedCount}/{totalWorldChunks} chunks data finalized & instantiated).");
                        proc_lastLoggedPercent = currentPercent;
                    }
                }
                
                bool foundNext = AdvanceRadialIterator(); // Find next chunk for proc_dx,dy,dz
                if (foundNext) {
                    currentChunkDataBeingPopulated = true; 
                    if (enableTimingLogs) currentChunkStartTime = Time.realtimeSinceStartup;
                } else { 
                    isProcessingWorld = false; 
                    Debug.Log($"[McWorld] All chunks processed and instantiated. Total: {chunksProcessedAndInstantiatedCount}. World steps: {debug_worldStepCallCount}.");
                    if (enableTimingLogs) LogFinalTimingStats();
                }
            }
        }
        else 
        {
            // This case means data was done, and we should have advanced. If we are here, advance.
            Debug.LogWarning($"[McWorld] ProcessNextWorldStep: currentChunkDataBeingPopulated is false for C_rel({proc_dx},{proc_dy},{proc_dz}). Advancing iterator.");
            bool foundNext = AdvanceRadialIterator();
            if (foundNext) {
                currentChunkDataBeingPopulated = true;
                if (enableTimingLogs) currentChunkStartTime = Time.realtimeSinceStartup;
            }
            else isProcessingWorld = false;
        }
        
        // Record step timing
        if (enableTimingLogs)
        {
            float stepTime = Time.realtimeSinceStartup - lastStepStartTime;
            totalStepTime += stepTime;
            stepTimingCount++;
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
        while (voxelsInDataSliceProcessed < voxelsPerChunkDataProcessingSlice)
        {
            if (dataGen_vox_x_in_chunk >= chunkSizeXZ) return true; 

            int currentGlobalX = (proc_dx * chunkSizeXZ) + dataGen_vox_x_in_chunk;
            int currentGlobalY = (proc_dy * chunkSizeY) + dataGen_vox_y_in_chunk;
            int currentGlobalZ = (proc_dz * chunkSizeXZ) + dataGen_vox_z_in_chunk;
            
            int terrainSurfaceY = terrainGenerator.GetBaseTerrainHeight(currentGlobalX, currentGlobalZ);
            byte blockType = 0; // Default to air

            byte grassID = terrainGenerator.grassBlockID;
            byte stoneID = terrainGenerator.stoneBlockID;
            byte dirtID = terrainGenerator.dirtBlockID;
            byte waterID = terrainGenerator.waterBlockID;
            int currentSeaLevel = terrainGenerator.seaLevel;

            if (currentGlobalY == terrainSurfaceY) {
                blockType = grassID;
            }
            else if (currentGlobalY < terrainSurfaceY) { // Below the surface
                if (currentGlobalY >= terrainSurfaceY - 3) { // Up to 3 layers of dirt below grass
                    blockType = dirtID;
                }
                else { // Deeper is stone
                    blockType = stoneID;
                }
            }
            else { // Above the surface (currentGlobalY > terrainSurfaceY)
                if (currentGlobalY <= currentSeaLevel) {
                    blockType = waterID;
                }
                // Else, it remains air (blockType = 0)
            }

            int dataIndex = GlobalPosToIndex(currentGlobalX, currentGlobalY, currentGlobalZ);
            if (dataIndex != -1) data[dataIndex] = blockType;

            voxelsInDataSliceProcessed++;

            dataGen_vox_z_in_chunk++;
            if (dataGen_vox_z_in_chunk >= chunkSizeXZ) {
                dataGen_vox_z_in_chunk = 0; dataGen_vox_y_in_chunk++;
                if (dataGen_vox_y_in_chunk >= chunkSizeY) {
                    dataGen_vox_y_in_chunk = 0; dataGen_vox_x_in_chunk++;
                    if (dataGen_vox_x_in_chunk >= chunkSizeXZ) return true; 
                }
            }
        }
        return false; 
    }
    
    private bool AdvanceRadialIterator() 
    {
        dataGen_vox_x_in_chunk = 0; dataGen_vox_y_in_chunk = 0; dataGen_vox_z_in_chunk = 0;

        while(true) 
        {
            // Use inst_ variables for the iterator's state
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

            int max_abs_coord_x = (worldDimensionX -1) / 2; 
            int max_abs_coord_y = (worldDimensionY -1) / 2;
            int max_abs_coord_z = (worldDimensionZ -1) / 2;
            
            if (inst_radius_iterator > Mathf.Max(max_abs_coord_x, max_abs_coord_y, max_abs_coord_z) + 1 ) 
            {
                return false; 
            }
            
            if (inst_radius_iterator == 0 || Mathf.Abs(inst_dx_iterator) == inst_radius_iterator || Mathf.Abs(inst_dy_iterator) == inst_radius_iterator || Mathf.Abs(inst_dz_iterator) == inst_radius_iterator) 
            {
                // Set proc_dx,dy,dz to the newly found iterator values
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
                    if (!chunkDataFinalized[array_cx][array_cy][array_cz]) 
                    {
                        return true; 
                    }
                }
            }
        }
    }

    void InstantiateAndConfigureChunk(int array_cx, int array_cy, int array_cz, int centered_dx, int centered_dy, int centered_dz)
    {
        if (chunks[array_cx][array_cy][array_cz] != null) { RequestChunkMeshUpdate(chunks[array_cx][array_cy][array_cz]); return; }

        GameObject newChunkGO = (GameObject)Instantiate(chunkPrefab); 
        if (newChunkGO == null) { Debug.LogError($"[McWorld] Instantiate FAILED for chunkPrefab at C_array({array_cx},{array_cy},{array_cz})."); return; }
        
        newChunkGO.name = $"Chunk_arr({array_cx},{array_cy},{array_cz})_cen({centered_dx},{centered_dy},{centered_dz})";
        newChunkGO.transform.SetParent(this.transform, false);
        newChunkGO.transform.localPosition = new Vector3(centered_dx * chunkSizeXZ, centered_dy * chunkSizeY, centered_dz * chunkSizeXZ);
        newChunkGO.transform.localRotation = Quaternion.identity;
        McChunk newChunkScript = newChunkGO.GetComponent<McChunk>();
        if (newChunkScript != null) {
            chunks[array_cx][array_cy][array_cz] = newChunkScript; 
            newChunkScript.chunkSizeXZ = this.chunkSizeXZ; newChunkScript.chunkSizeY = this.chunkSizeY;
            newChunkScript.voxelsPerSlice = this.voxelsPerSliceInChunks;
            
            // Determine fast build mode based on settings and world generation state
            if (useDynamicFastBuildMode) {
                newChunkScript.useFastBuildMode = isProcessingWorld; // Fast during world gen, normal during runtime
            } else {
                newChunkScript.useFastBuildMode = useChunkFastBuildMode; // Use manual setting
            }
            
            newChunkScript.chunkX = centered_dx * this.chunkSizeXZ; 
            newChunkScript.chunkY = centered_dy * this.chunkSizeY; 
            newChunkScript.chunkZ = centered_dz * this.chunkSizeXZ;
            newChunkScript.template = false; //newChunkScript.world = this;
            newChunkScript.SetWorld(this); 
            newChunkGO.SetActive(true);
        } else { Debug.LogError($"[McWorld] Failed to get McChunk script for C_array({array_cx},{array_cy},{array_cz}) from GO {newChunkGO.name}."); if(newChunkGO != null) Destroy(newChunkGO); }
    }

    public void RequestChunkMeshUpdate(McChunk chunkToUpdate) 
    {
        if (chunkToUpdate == null || chunkToUpdate.isBuildingMesh) return;
        for (int i = 0; i < chunkRebuildQueueCount; i++) if (chunkRebuildQueue[i] == chunkToUpdate) return;
        
        if (chunkRebuildQueueCount < MAX_REBUILD_QUEUE_SIZE) { 
            chunkRebuildQueue[chunkRebuildQueueCount] = chunkToUpdate; 
            chunkRebuildQueueCount++; 
        }
    }
    
    private void RequestChunkMeshUpdate_Prioritized(McChunk chunkToUpdate)
    {
        if (chunkToUpdate == null || chunkToUpdate.isBuildingMesh) return;
        for (int i = 0; i < chunkRebuildQueueCount; i++) if (chunkRebuildQueue[i] == chunkToUpdate) return;

        if (chunkRebuildQueueCount < MAX_REBUILD_QUEUE_SIZE) {
            for (int i = chunkRebuildQueueCount; i > 0; i--) {
                chunkRebuildQueue[i] = chunkRebuildQueue[i-1];
            }
            chunkRebuildQueue[0] = chunkToUpdate;
            chunkRebuildQueueCount++;
        }
    }

    public void ProcessChunkRebuildQueue() 
    {
        if (enableTimingLogs)
        {
            lastRebuildQueueStartTime = Time.realtimeSinceStartup;
        }
        
        int processedThisCall = 0;
        for(int k=0; k < chunkRebuildQueueCount && processedThisCall < maxChunksToRebuildPerInterval; )
        {
            McChunk chunkToBuild = chunkRebuildQueue[k]; 
            
            bool canBuild = false;
            switch (neighboringChunksProcessingMode)
            {
                case 0: // Off - always build if chunk is in queue
                    canBuild = true;
                    break;
                case 1: // Normal - wait for neighbors
                    canBuild = AreAllNeighborsDataFinalized(chunkToBuild);
                    break;
                case 2: // Regenerate - build this chunk, then try to queue neighbors
                    canBuild = true; // Build this chunk if it's in the queue
                    break;
                default: 
                    canBuild = AreAllNeighborsDataFinalized(chunkToBuild); // Default to Normal mode
                    Debug.LogWarning($"[McWorld] Unknown neighboringChunksProcessingMode: {neighboringChunksProcessingMode} in ProcessChunkRebuildQueue. Defaulting to mode 1.");
                    break;
            }

            if (canBuild) 
            {
                for (int j = k; j < chunkRebuildQueueCount - 1; j++) {
                    chunkRebuildQueue[j] = chunkRebuildQueue[j + 1];
                }
                chunkRebuildQueue[chunkRebuildQueueCount - 1] = null;
                chunkRebuildQueueCount--;

                if (chunkToBuild != null && !chunkToBuild.isBuildingMesh) {
                    // Set build mode for runtime rebuilds
                    if (useDynamicFastBuildMode) {
                        chunkToBuild.useFastBuildMode = false; // Always use normal mode for runtime rebuilds
                    } else {
                        chunkToBuild.useFastBuildMode = useChunkFastBuildMode;
                    }
                    
                    chunkToBuild.StartBuildMesh();
                    processedThisCall++;

                    if (neighboringChunksProcessingMode == 2)
                    {
                        TriggerNeighborMeshRebuilds(chunkToBuild);
                    }
                }
            }
            else 
            {
                k++; 
            }
        }
        
        if (enableTimingLogs)
        {
            float rebuildTime = Time.realtimeSinceStartup - lastRebuildQueueStartTime;
            totalRebuildQueueTime += rebuildTime;
            rebuildQueueProcessCount++;
        }
        
        SendCustomEventDelayedSeconds(nameof(ProcessChunkRebuildQueue), chunkRebuildInterval);
    }

    private bool AreAllNeighborsDataFinalized(McChunk chunk)
    {
        if (chunk == null) return false; 

        int centeredCX = chunk.chunkX / chunkSizeXZ;
        int centeredCY = chunk.chunkY / chunkSizeY;
        int centeredCZ = chunk.chunkZ / chunkSizeXZ;

        int[] dx_offsets = { 1, -1, 0,  0, 0,  0 };
        int[] dy_offsets = { 0,  0, 1, -1, 0,  0 };
        int[] dz_offsets = { 0,  0, 0,  0, 1, -1 };

        for (int i = 0; i < 6; i++) 
        {
            int neighborCenteredX = centeredCX + dx_offsets[i];
            int neighborCenteredY = centeredCY + dy_offsets[i];
            int neighborCenteredZ = centeredCZ + dz_offsets[i];

            int neighborArrayX = neighborCenteredX + chunkOffsetX;
            int neighborArrayY = neighborCenteredY + chunkOffsetY;
            int neighborArrayZ = neighborCenteredZ + chunkOffsetZ;

            if (neighborArrayX >= 0 && neighborArrayX < worldDimensionX &&
                neighborArrayY >= 0 && neighborArrayY < worldDimensionY &&
                neighborArrayZ >= 0 && neighborArrayZ < worldDimensionZ)
            {
                if (!chunkDataFinalized[neighborArrayX][neighborArrayY][neighborArrayZ])
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

        int[] dx_offsets = { 1, -1, 0,  0, 0,  0 };
        int[] dy_offsets = { 0,  0, 1, -1, 0,  0 };
        int[] dz_offsets = { 0,  0, 0,  0, 1, -1 };

        for (int i = 0; i < 6; i++) 
        {
            int neighborCenteredX = centeredCX + dx_offsets[i];
            int neighborCenteredY = centeredCY + dy_offsets[i];
            int neighborCenteredZ = centeredCZ + dz_offsets[i];

            int neighborArrayX = neighborCenteredX + chunkOffsetX;
            int neighborArrayY = neighborCenteredY + chunkOffsetY;
            int neighborArrayZ = neighborCenteredZ + chunkOffsetZ;

            if (neighborArrayX >= 0 && neighborArrayX < worldDimensionX &&
                neighborArrayY >= 0 && neighborArrayY < worldDimensionY &&
                neighborArrayZ >= 0 && neighborArrayZ < worldDimensionZ)
            {
                if (chunkDataFinalized[neighborArrayX][neighborArrayY][neighborArrayZ])
                {
                    McChunk neighborChunk = chunks[neighborArrayX][neighborArrayY][neighborArrayZ];
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

    public byte GetBlock(int globalX, int globalY, int globalZ)
    {
        int index = GlobalPosToIndex(globalX, globalY, globalZ);
        if (index == -1) return 0; return data[index];
    }

    public void SetBlock(int globalX, int globalY, int globalZ, byte blockType)
    {
        float setBlockStartTime = 0f;
        if (enableTimingLogs)
        {
            setBlockStartTime = Time.realtimeSinceStartup;
            setBlockCallCount++;
        }
        
        int dataIndex = GlobalPosToIndex(globalX, globalY, globalZ);
        if (dataIndex == -1) { 
            if (enableTimingLogs) RecordSetBlockTime(setBlockStartTime);
            return; 
        }
        if (data[dataIndex] == blockType) { 
            if (enableTimingLogs) RecordSetBlockTime(setBlockStartTime);
            return; 
        } 
        
        data[dataIndex] = blockType;

        int centeredChunkX = Mathf.FloorToInt((float)globalX / chunkSizeXZ);
        int centeredChunkY = Mathf.FloorToInt((float)globalY / chunkSizeY);
        int centeredChunkZ = Mathf.FloorToInt((float)globalZ / chunkSizeXZ);
        
        int array_cx = centeredChunkX + chunkOffsetX; int array_cy = centeredChunkY + chunkOffsetY; int array_cz = centeredChunkZ + chunkOffsetZ;
        
        McChunk targetChunk = GetChunkScript(array_cx, array_cy, array_cz);
        if (targetChunk != null) { 
            RequestChunkMeshUpdate_Prioritized(targetChunk); 
        } 

        int localX_in_chunk = globalX - (centeredChunkX * chunkSizeXZ); int localY_in_chunk = globalY - (centeredChunkY * chunkSizeY); int localZ_in_chunk = globalZ - (centeredChunkZ * chunkSizeXZ);

        if (localX_in_chunk == 0) UpdateNeighborChunkViaQueue_Prioritized(centeredChunkX - 1, centeredChunkY, centeredChunkZ); 
        if (localX_in_chunk == chunkSizeXZ - 1) UpdateNeighborChunkViaQueue_Prioritized(centeredChunkX + 1, centeredChunkY, centeredChunkZ);
        if (localY_in_chunk == 0) UpdateNeighborChunkViaQueue_Prioritized(centeredChunkX, centeredChunkY - 1, centeredChunkZ);
        if (localY_in_chunk == chunkSizeY - 1) UpdateNeighborChunkViaQueue_Prioritized(centeredChunkX, centeredChunkY + 1, centeredChunkZ);
        if (localZ_in_chunk == 0) UpdateNeighborChunkViaQueue_Prioritized(centeredChunkX, centeredChunkY, centeredChunkZ - 1);
        if (localZ_in_chunk == chunkSizeXZ - 1) UpdateNeighborChunkViaQueue_Prioritized(centeredChunkX, centeredChunkY, centeredChunkZ + 1);

        if (Networking.LocalPlayer != null) 
        {
            ProcessPlayerActionRebuildsImmediately();
        }
        
        if (enableTimingLogs) RecordSetBlockTime(setBlockStartTime);
    }
    
    private void RecordSetBlockTime(float startTime)
    {
        float setBlockTime = Time.realtimeSinceStartup - startTime;
        totalSetBlockTime += setBlockTime;
        lastSetBlockTime = setBlockTime;
        
        // Log every 100 block modifications
        if (setBlockCallCount % 100 == 0)
        {
            float avgSetBlockTime = totalSetBlockTime / setBlockCallCount;
            Debug.Log($"[McWorld Timing] Block Modifications - Count: {setBlockCallCount}, Avg Time: {avgSetBlockTime * 1000f:F2}ms, Last: {lastSetBlockTime * 1000f:F2}ms");
        }
    }

    private void ProcessPlayerActionRebuildsImmediately() {
        int processedThisCall = 0;
        int initialQueueCount = chunkRebuildQueueCount;
        int currentQueueIndex = 0;

        while(currentQueueIndex < initialQueueCount && chunkRebuildQueueCount > 0 && processedThisCall < maxChunksToRebuildPerInterval) {
            McChunk chunkToBuild = chunkRebuildQueue[0]; 
            
            bool canBuildPlayerAction = false;
            switch (neighboringChunksProcessingMode)
            {
                case 0: // Off
                    canBuildPlayerAction = true;
                    break;
                case 1: // Normal
                    canBuildPlayerAction = AreAllNeighborsDataFinalized(chunkToBuild);
                    break;
                case 2: // Regenerate
                    canBuildPlayerAction = true; 
                    break;
                default:
                    canBuildPlayerAction = AreAllNeighborsDataFinalized(chunkToBuild); 
                    Debug.LogWarning($"[McWorld] Unknown neighboringChunksProcessingMode: {neighboringChunksProcessingMode} in ProcessPlayerActionRebuildsImmediately. Defaulting to mode 1.");
                    break;
            }
            
            if (canBuildPlayerAction) { 
                for (int j = 0; j < chunkRebuildQueueCount - 1; j++) {
                    chunkRebuildQueue[j] = chunkRebuildQueue[j + 1];
                }
                chunkRebuildQueue[chunkRebuildQueueCount - 1] = null;
                chunkRebuildQueueCount--; 
                initialQueueCount--; 

                if (chunkToBuild != null && !chunkToBuild.isBuildingMesh) {
                    // For player actions, we might want faster response
                    if (useDynamicFastBuildMode) {
                        chunkToBuild.useFastBuildMode = false; // Use normal sliced mode for smooth gameplay
                    } else {
                        chunkToBuild.useFastBuildMode = useChunkFastBuildMode;
                    }
                    
                    chunkToBuild.StartBuildMesh();
                    processedThisCall++;

                    if (neighboringChunksProcessingMode == 2)
                    {
                        TriggerNeighborMeshRebuilds(chunkToBuild);
                    }
                }
            } else {
                if (chunkRebuildQueueCount > 1) { 
                    McChunk tempChunk = chunkRebuildQueue[0];
                    for (int j = 0; j < chunkRebuildQueueCount - 1; j++) {
                        chunkRebuildQueue[j] = chunkRebuildQueue[j + 1];
                    }
                    chunkRebuildQueue[chunkRebuildQueueCount - 1] = tempChunk;
                }
                currentQueueIndex++; 
            }
        }
    }

    private void UpdateNeighborChunkViaQueue_Prioritized(int centeredNeighborCX, int centeredNeighborCY, int centeredNeighborCZ)
    {
        int array_nx = centeredNeighborCX + chunkOffsetX; int array_ny = centeredNeighborCY + chunkOffsetY; int array_nz = centeredNeighborCZ + chunkOffsetZ;
        McChunk neighborChunk = GetChunkScript(array_nx, array_ny, array_nz);
        if (neighborChunk != null) RequestChunkMeshUpdate_Prioritized(neighborChunk);
    }

    public McChunk GetChunkScript(int array_cx, int array_cy, int array_cz) 
    {
        //List<int> array_coords = new List<int> { array_cx, array_cy, array_cz };
        if (array_cx < 0 || array_cx >= worldDimensionX || array_cy < 0 || array_cy >= worldDimensionY || array_cz < 0 || array_cz >= worldDimensionZ) return null;
        return chunks[array_cx][array_cy][array_cz];
    }

    private void LogPeriodicTimingStats()
    {
        if (!enableTimingLogs) return;
        
        float avgChunkDataGen = chunksProcessedAndInstantiatedCount > 0 ? totalChunkDataGenTime / chunksProcessedAndInstantiatedCount : 0f;
        float avgChunkInstantiation = chunksProcessedAndInstantiatedCount > 0 ? totalChunkInstantiationTime / chunksProcessedAndInstantiatedCount : 0f;
        float avgStepTime = stepTimingCount > 0 ? totalStepTime / stepTimingCount : 0f;
        
        Debug.Log($"[McWorld Timing] Periodic Stats - Chunks Processed: {chunksProcessedAndInstantiatedCount}" +
                  $"\n  Avg Chunk Data Gen: {avgChunkDataGen * 1000f:F2}ms" +
                  $"\n  Avg Chunk Instantiation: {avgChunkInstantiation * 1000f:F2}ms" +
                  $"\n  Avg Step Time: {avgStepTime * 1000f:F2}ms" +
                  $"\n  Total Feature Placement: {totalFeaturePlacementTime:F2}s");
    }
    
    private void LogFinalTimingStats()
    {
        if (!enableTimingLogs) return;
        
        worldProcessingEndTime = Time.realtimeSinceStartup;
        float totalTime = worldProcessingEndTime - worldProcessingStartTime;
        
        float avgChunkDataGen = chunksProcessedAndInstantiatedCount > 0 ? totalChunkDataGenTime / chunksProcessedAndInstantiatedCount : 0f;
        float avgChunkInstantiation = chunksProcessedAndInstantiatedCount > 0 ? totalChunkInstantiationTime / chunksProcessedAndInstantiatedCount : 0f;
        float avgStepTime = stepTimingCount > 0 ? totalStepTime / stepTimingCount : 0f;
        float avgRebuildQueueTime = rebuildQueueProcessCount > 0 ? totalRebuildQueueTime / rebuildQueueProcessCount : 0f;
        
        // Calculate min/max for chunk data generation
        float minDataGen = float.MaxValue;
        float maxDataGen = 0f;
        for (int i = 0; i < chunkTimingIndex && i < chunkDataGenTimes.Length; i++)
        {
            if (chunkDataGenTimes[i] < minDataGen) minDataGen = chunkDataGenTimes[i];
            if (chunkDataGenTimes[i] > maxDataGen) maxDataGen = chunkDataGenTimes[i];
        }
        if (minDataGen == float.MaxValue) minDataGen = 0f;
        
        // Calculate min/max for chunk instantiation
        float minInstantiation = float.MaxValue;
        float maxInstantiation = 0f;
        for (int i = 0; i < chunkTimingIndex && i < chunkInstantiationTimes.Length; i++)
        {
            if (chunkInstantiationTimes[i] < minInstantiation) minInstantiation = chunkInstantiationTimes[i];
            if (chunkInstantiationTimes[i] > maxInstantiation) maxInstantiation = chunkInstantiationTimes[i];
        }
        if (minInstantiation == float.MaxValue) minInstantiation = 0f;
        
        Debug.Log($"[McWorld Timing] FINAL WORLD GENERATION STATISTICS" +
                  $"\n========================================" +
                  $"\nTotal Processing Time: {totalTime:F2} seconds" +
                  $"\nTotal Chunks Processed: {chunksProcessedAndInstantiatedCount}" +
                  $"\nTotal World Steps: {debug_worldStepCallCount}" +
                  $"\n" +
                  $"\nCHUNK DATA GENERATION:" +
                  $"\n  Total Time: {totalChunkDataGenTime:F2}s" +
                  $"\n  Average: {avgChunkDataGen * 1000f:F2}ms" +
                  $"\n  Min: {minDataGen * 1000f:F2}ms" +
                  $"\n  Max: {maxDataGen * 1000f:F2}ms" +
                  $"\n" +
                  $"\nCHUNK INSTANTIATION:" +
                  $"\n  Total Time: {totalChunkInstantiationTime:F2}s" +
                  $"\n  Average: {avgChunkInstantiation * 1000f:F2}ms" +
                  $"\n  Min: {minInstantiation * 1000f:F2}ms" +
                  $"\n  Max: {maxInstantiation * 1000f:F2}ms" +
                  $"\n" +
                  $"\nFEATURE PLACEMENT:" +
                  $"\n  Total Time: {totalFeaturePlacementTime:F2}s" +
                  $"\n  Average per chunk: {(totalFeaturePlacementTime / chunksProcessedAndInstantiatedCount) * 1000f:F2}ms" +
                  $"\n" +
                  $"\nPROCESSING STEPS:" +
                  $"\n  Total Steps: {stepTimingCount}" +
                  $"\n  Total Time: {totalStepTime:F2}s" +
                  $"\n  Average Step: {avgStepTime * 1000f:F2}ms" +
                  $"\n" +
                  $"\nREBUILD QUEUE:" +
                  $"\n  Total Calls: {rebuildQueueProcessCount}" +
                  $"\n  Total Time: {totalRebuildQueueTime:F2}s" +
                  $"\n  Average per call: {avgRebuildQueueTime * 1000f:F2}ms" +
                  $"\n" +
                  $"\nBLOCK MODIFICATIONS:" +
                  $"\n  Total SetBlock Calls: {setBlockCallCount}" +
                  $"\n  Total Time: {totalSetBlockTime:F2}s" +
                  $"\n  Average: {(setBlockCallCount > 0 ? totalSetBlockTime / setBlockCallCount * 1000f : 0f):F2}ms" +
                  $"\n" +
                  $"\nPERFORMANCE METRICS:" +
                  $"\n  Chunks per second: {chunksProcessedAndInstantiatedCount / totalTime:F2}" +
                  $"\n  Voxels per chunk data slice: {voxelsPerChunkDataProcessingSlice}" +
                  $"\n  World processing step delay: {worldProcessingStepDelay}s" +
                  $"\n========================================");
    }

    public void LogCurrentTimingStats()
    {
        if (!enableTimingLogs) {
            Debug.Log("[McWorld] Timing logs are disabled. Set enableTimingLogs to true to see timing statistics.");
            return;
        }
        
        float currentTime = Time.realtimeSinceStartup;
        float elapsedTime = currentTime - worldProcessingStartTime;
        
        float avgChunkDataGen = chunksProcessedAndInstantiatedCount > 0 ? totalChunkDataGenTime / chunksProcessedAndInstantiatedCount : 0f;
        float avgChunkInstantiation = chunksProcessedAndInstantiatedCount > 0 ? totalChunkInstantiationTime / chunksProcessedAndInstantiatedCount : 0f;
        float avgStepTime = stepTimingCount > 0 ? totalStepTime / stepTimingCount : 0f;
        float avgRebuildQueueTime = rebuildQueueProcessCount > 0 ? totalRebuildQueueTime / rebuildQueueProcessCount : 0f;
        float chunksPerSecond = elapsedTime > 0 ? chunksProcessedAndInstantiatedCount / elapsedTime : 0f;
        float avgSetBlockTime = setBlockCallCount > 0 ? totalSetBlockTime / setBlockCallCount : 0f;
        
        string processingStatus = isProcessingWorld ? "ACTIVE" : "COMPLETE";
        
        Debug.Log($"[McWorld Timing] CURRENT STATISTICS - Status: {processingStatus}" +
                  $"\n========================================" +
                  $"\nElapsed Time: {elapsedTime:F2} seconds" +
                  $"\nChunks Processed: {chunksProcessedAndInstantiatedCount}/{totalWorldChunks}" +
                  $"\nWorld Steps: {debug_worldStepCallCount}" +
                  $"\n" +
                  $"\nCHUNK PROCESSING:" +
                  $"\n  Avg Data Gen: {avgChunkDataGen * 1000f:F2}ms" +
                  $"\n  Avg Instantiation: {avgChunkInstantiation * 1000f:F2}ms" +
                  $"\n  Avg Feature Placement: {(chunksProcessedAndInstantiatedCount > 0 ? (totalFeaturePlacementTime / chunksProcessedAndInstantiatedCount) * 1000f : 0f):F2}ms" +
                  $"\n  Chunks per second: {chunksPerSecond:F2}" +
                  $"\n" +
                  $"\nSTEP PERFORMANCE:" +
                  $"\n  Total Steps: {stepTimingCount}" +
                  $"\n  Avg Step Time: {avgStepTime * 1000f:F2}ms" +
                  $"\n" +
                  $"\nREBUILD QUEUE:" +
                  $"\n  Current Queue Size: {chunkRebuildQueueCount}" +
                  $"\n  Total Process Calls: {rebuildQueueProcessCount}" +
                  $"\n  Avg Process Time: {avgRebuildQueueTime * 1000f:F2}ms" +
                  $"\n" +
                  $"\nBLOCK MODIFICATIONS:" +
                  $"\n  Total SetBlock Calls: {setBlockCallCount}" +
                  $"\n  Avg SetBlock Time: {avgSetBlockTime * 1000f:F2}ms" +
                  $"\n  Last SetBlock Time: {lastSetBlockTime * 1000f:F2}ms" +
                  $"\n========================================");
    }
    
    // Test method to rebuild a specific chunk with fast mode
    public void TestFastChunkBuild(int worldX, int worldY, int worldZ)
    {
        int centeredChunkX = Mathf.FloorToInt((float)worldX / chunkSizeXZ);
        int centeredChunkY = Mathf.FloorToInt((float)worldY / chunkSizeY);
        int centeredChunkZ = Mathf.FloorToInt((float)worldZ / chunkSizeXZ);
        
        int array_cx = centeredChunkX + chunkOffsetX;
        int array_cy = centeredChunkY + chunkOffsetY;
        int array_cz = centeredChunkZ + chunkOffsetZ;
        
        McChunk chunk = GetChunkScript(array_cx, array_cy, array_cz);
        if (chunk != null)
        {
            bool previousFastMode = chunk.useFastBuildMode;
            chunk.useFastBuildMode = true;
            Debug.Log($"[McWorld] Testing fast build for chunk at ({chunk.chunkX},{chunk.chunkY},{chunk.chunkZ})...");
            chunk.StartBuildMesh();
            chunk.useFastBuildMode = previousFastMode;
        }
        else
        {
            Debug.LogWarning($"[McWorld] No chunk found at world position ({worldX},{worldY},{worldZ})");
        }
    }
    
    public void LogRecommendedSettings()
    {
        Debug.Log("[McWorld] RECOMMENDED SETTINGS FOR BETTER PERFORMANCE:" +
                  "\n========================================" +
                  "\nOPTION 1: DYNAMIC MODE (RECOMMENDED)" +
                  "\n  - useDynamicFastBuildMode = true" +
                  "\n  - voxelsPerSliceInChunks = 2048" +
                  "\n  This automatically uses fast build during world gen," +
                  "\n  and switches to sliced mode for runtime updates." +
                  "\n" +
                  "\nOPTION 2: MANUAL SETTINGS" +
                  "\nFOR INITIAL WORLD GENERATION:" +
                  "\n  - useDynamicFastBuildMode = false" +
                  "\n  - useChunkFastBuildMode = true" +
                  "\n  - voxelsPerSliceInChunks = 4096 or higher" +
                  "\n  - worldProcessingStepDelay = 0" +
                  "\n  - chunkRebuildInterval = 0.1" +
                  "\n" +
                  "\nFOR RUNTIME (PLAYER INTERACTIONS):" +
                  "\n  - useDynamicFastBuildMode = false" +
                  "\n  - useChunkFastBuildMode = false" +
                  "\n  - voxelsPerSliceInChunks = 1024-2048" +
                  "\n  - chunkRebuildInterval = 0.03" +
                  "\n  - maxChunksToRebuildPerInterval = 2-3" +
                  "\n" +
                  "\nCURRENT SETTINGS:" +
                  $"\n  - useDynamicFastBuildMode = {useDynamicFastBuildMode}" +
                  $"\n  - useChunkFastBuildMode = {useChunkFastBuildMode}" +
                  $"\n  - voxelsPerSliceInChunks = {voxelsPerSliceInChunks}" +
                  $"\n  - worldProcessingStepDelay = {worldProcessingStepDelay}" +
                  $"\n  - chunkRebuildInterval = {chunkRebuildInterval}" +
                  "\n" +
                  "\nYOUR CURRENT ISSUE:" +
                  "\n  With 256 voxels/slice, you get 128 slices per chunk." +
                  "\n  Each slice = 1 frame delay = 3.3 seconds total!" +
                  "\n  Try: voxelsPerSliceInChunks = 2048 (16x fewer slices)" +
                  "\n  Or enable fast build mode for instant generation." +
                  "\n========================================");
    }
}