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
    public byte[] data; 

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

    [Header("Performance Parameters (McWorld)")]
    [Tooltip("Number of voxels to process for the *current chunk's data* per step. Min 1.")]
    public int voxelsPerChunkDataProcessingSlice = 128; 
    [Tooltip("Delay (seconds) between processing steps for world generation (data slices or moving to next chunk). 0 for frame-by-frame.")]
    public float worldProcessingStepDelay = 0f; 
    [Tooltip("How often (seconds) McWorld checks its queue to start rebuilding chunk meshes.")]
    public float chunkRebuildInterval = 0.03f; // Approx 33 FPS check
    [Tooltip("Max number of chunks McWorld will start rebuilding in a single interval (if neighbors' data is ready).")]
    public int maxChunksToRebuildPerInterval = 1;
    [Tooltip("Neighboring chunks processing mode for async rebuilds. 0 = Off (build chunk mesh immediately if data ready), 1 = Normal (wait for all direct neighbors' data), 2 = Regenerate (build chunk mesh, then queue ready neighbors).")]
    public int neighboringChunksProcessingMode = 1; 


    [Header("Performance Parameters (Passed to Chunks)")]
    [Tooltip("Number of voxels each McChunk processes per slice during its *asynchronous full* mesh build.")]
    public int voxelsPerSliceInChunks = 256;

    [Header("Debug")]
    public bool enableVerboseLogging = true; 
    private StringBuilder logBuilder_McWorld;
    private float currentChunkDataGen_StartTime_Total; 
    private float currentChunkDataGen_StartTime_Slice; 
    private int currentChunkDataGen_SliceCount; 

    // Chunk Rebuild Queue (Circular Buffer)
    private McChunk[] chunkRebuildQueue;
    private int chunkRebuildQueue_head = 0;
    private int chunkRebuildQueue_tail = 0;
    private int chunkRebuildQueue_count = 0;
    private const int MAX_REBUILD_QUEUE_SIZE = 256; // Max chunks in rebuild queue

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

    // Cached values from TerrainGenerator
    private byte cachedGrassBlockID;
    private byte cachedStoneBlockID;
    private byte cachedDirtBlockID;
    private byte cachedWaterBlockID;
    private int cachedSeaLevel;

    // Neighbor offsets for performance. Changed from readonly to private.
    private int[] neighbor_dx_offsets = new int[] { 1, -1, 0,  0, 0,  0 };
    private int[] neighbor_dy_offsets = new int[] { 0,  0, 1, -1, 0,  0 };
    private int[] neighbor_dz_offsets = new int[] { 0,  0, 0,  0, 1, -1 };


    void Start()
    {
        // Critical dependency checks
        if (chunkPrefab == null) { Debug.LogError("[McWorld] Chunk Prefab is not assigned! Aborting."); this.enabled = false; return; }
        if (chunkPrefab.GetComponent<McChunk>() == null) { Debug.LogError($"[McWorld] Chunk Prefab '{chunkPrefab.name}' missing McChunk script! Aborting."); this.enabled = false; return; }
        if (terrainGenerator == null) { Debug.LogError("[McWorld] McTerrainGenerator is not assigned! Aborting."); this.enabled = false; return; }

        // Parameter validation
        if (voxelsPerChunkDataProcessingSlice < 1) voxelsPerChunkDataProcessingSlice = 1;
        if (worldProcessingStepDelay < 0f) worldProcessingStepDelay = 0f;
        
        logBuilder_McWorld = new StringBuilder(512); 
        
        InitializeWorldParameters(); 
        InitializeChunkStorageAndFlags();
        InitializeAndAllocateVoxelData(); // Depends on totalWorldVoxels

        int actualWorldSeed = worldSeedString.GetHashCode();
        terrainGenerator.InitializeGenerator(actualWorldSeed); 

        // Cache block IDs from terrain generator
        cachedGrassBlockID = terrainGenerator.grassBlockID;
        cachedStoneBlockID = terrainGenerator.stoneBlockID;
        cachedDirtBlockID = terrainGenerator.dirtBlockID;
        cachedWaterBlockID = terrainGenerator.waterBlockID;
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

        // Start periodic check for chunk rebuilds
        SendCustomEventDelayedSeconds(nameof(ProcessChunkRebuildQueue), chunkRebuildInterval);
    }

    void InitializeWorldParameters()
    {
        worldDimensionX = Mathf.Max(1, worldDimensionX); 
        worldDimensionY = Mathf.Max(1, worldDimensionY); 
        worldDimensionZ = Mathf.Max(1, worldDimensionZ);
        chunkSizeXZ = Mathf.Max(1, chunkSizeXZ); 
        chunkSizeY = Mathf.Max(1, chunkSizeY);
        
        // Calculate offsets to center the world around (0,0,0) in terms of chunk array indices
        chunkOffsetX = worldDimensionX / 2;
        chunkOffsetY = worldDimensionY / 2;
        chunkOffsetZ = worldDimensionZ / 2;

        // Calculate total world size in voxels
        worldSizeX_voxels = worldDimensionX * chunkSizeXZ;
        worldSizeY_voxels = worldDimensionY * chunkSizeY;
        worldSizeZ_voxels = worldDimensionZ * chunkSizeXZ;
        
        // Calculate global voxel offsets to map global voxel coords (e.g., -50 to +50) to array indices (0 to 100)
        globalVoxelOffsetX = worldSizeX_voxels / 2;
        globalVoxelOffsetY = worldSizeY_voxels / 2;
        globalVoxelOffsetZ = worldSizeZ_voxels / 2;

        layerSize_voxels_for_1D_data = worldSizeX_voxels * worldSizeZ_voxels; // Voxels per Y-slice in the 1D data array
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
        chunkDataFinalized_1D = new bool[totalWorldChunks]; // Initialized to false by default
    }

    private int ChunkArrayCoordsTo1D(int arrayX, int arrayY, int arrayZ)
    {
        // Check if coordinates are within the bounds of the world's chunk dimensions
        if (arrayX < 0 || arrayX >= worldDimensionX ||
            arrayY < 0 || arrayY >= worldDimensionY ||
            arrayZ < 0 || arrayZ >= worldDimensionZ)
        {
            // LogError($"Chunk array coordinates out of bounds: ({arrayX},{arrayY},{arrayZ}) for dimensions ({worldDimensionX},{worldDimensionY},{worldDimensionZ})");
            return -1; // Indicates an out-of-bounds access
        }
        // Flatten 3D chunk array coordinates to a 1D index.
        // Order: Z (depth), then Y (height layers), then X (columns per row). This is one common way.
        // Alternative: Y * (dimX * dimZ) + Z * dimX + X (Y layers first)
        // Current: Z * (dimX * dimY) + Y * dimX + X
        return (arrayZ * worldDimensionX * worldDimensionY) + (arrayY * worldDimensionX) + arrayX;
    }

    void InitializeAndAllocateVoxelData()
    {
        if (totalWorldVoxels <= 0) { 
            Debug.LogError("[McWorld] Total world voxels is zero or negative. Cannot allocate data array."); 
            this.enabled = false; return; 
        }
        data = new byte[totalWorldVoxels]; 
        // Voxel data is initialized to 0 (air) by default.
    }

    public void StartWorldProcessing()
    {
        if (isProcessingWorld) { Debug.LogWarning("[McWorld] StartWorldProcessing called while already processing."); return; }
        if (totalWorldChunks == 0) { Debug.LogWarning("[McWorld] World has zero total chunks. Skipping world processing."); isProcessingWorld = false; return; }
        
#if UNITY_EDITOR
        if (enableVerboseLogging) Debug.Log($"[McWorld] Initializing Interleaved Radial World Processing. Target chunk (Rel 0,0,0). Voxels per data slice: {voxelsPerChunkDataProcessingSlice}. Step delay: {worldProcessingStepDelay}s.");
#endif
        
        isProcessingWorld = true;
        // proc_ variables define the *current chunk being actively processed* (data gen & instantiation)
        // Start at the center chunk (relative offset 0,0,0)
        proc_radius = 0; proc_dx = 0; proc_dy = 0; proc_dz = 0; 
        
        // inst_ variables are for the *radial iterator itself* to find the next chunk
        inst_radius_iterator = 0; inst_dx_iterator = 0; inst_dy_iterator = 0; inst_dz_iterator = 0;
        inst_shellIterationInitialized = true; // Start with the first point of the radius 0 shell

        // Reset data generation progress for the first chunk
        dataGen_vox_x_in_chunk = 0; dataGen_vox_y_in_chunk = 0; dataGen_vox_z_in_chunk = 0;
        currentChunkDataBeingPopulated = true; // Start by populating data for the first chunk
        currentChunkDataGen_StartTime_Total = Time.realtimeSinceStartup; 
        currentChunkDataGen_StartTime_Slice = Time.realtimeSinceStartup;
        currentChunkDataGen_SliceCount = 0;

        proc_lastLoggedPercent = -1; 
        debug_worldStepCallCount = 0; 
        chunksProcessedAndInstantiatedCount = 0;
        
        ProcessNextWorldStep(); // Start the first step
    }

    public void ProcessNextWorldStep()
    {
        if (!isProcessingWorld) return; // Stop if processing flag is false
        debug_worldStepCallCount++;

        // Current target chunk's array coordinates (non-centered)
        int currentArrayCX = proc_dx + chunkOffsetX;
        int currentArrayCY = proc_dy + chunkOffsetY;
        int currentArrayCZ = proc_dz + chunkOffsetZ;

        // Validate if current target chunk is within world bounds
        if (currentArrayCX < 0 || currentArrayCX >= worldDimensionX ||
            currentArrayCY < 0 || currentArrayCY >= worldDimensionY ||
            currentArrayCZ < 0 || currentArrayCZ >= worldDimensionZ)
        {
#if UNITY_EDITOR
            if (enableVerboseLogging) Debug.LogWarning($"[McWorld] ProcessNextWorldStep: Current target chunk Arr({currentArrayCX},{currentArrayCY},{currentArrayCZ}) is out of bounds. Rel({proc_dx},{proc_dy},{proc_dz}). Advancing iterator.");
#endif
            
            bool foundNext = AdvanceRadialIterator(); // This will set new proc_dx, dy, dz
            if (foundNext && isProcessingWorld) { 
                currentChunkDataBeingPopulated = true; // Start populating for the new chunk
                // Reset timing and counters for the new chunk's data generation
                currentChunkDataGen_StartTime_Total = Time.realtimeSinceStartup; 
                currentChunkDataGen_StartTime_Slice = Time.realtimeSinceStartup;
                currentChunkDataGen_SliceCount = 0;
                dataGen_vox_x_in_chunk = 0; dataGen_vox_y_in_chunk = 0; dataGen_vox_z_in_chunk = 0;
                ScheduleNextWorldStep(); 
            }
            else if (isProcessingWorld) { // No next chunk found, and still "processing"
#if UNITY_EDITOR
                if (enableVerboseLogging) Debug.Log($"[McWorld] World Processing seems complete (radial iterator exhausted). Processed {chunksProcessedAndInstantiatedCount}/{totalWorldChunks} chunks. Total steps: {debug_worldStepCallCount}.");
#endif
                isProcessingWorld = false; // Mark processing as complete
            }
            return;
        }
        
        // If currently populating data for the target chunk
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
                                                   currentArrayCX, currentArrayCY, currentArrayCZ, proc_dx, proc_dy, proc_dz, currentChunkDataGen_SliceCount +1, totalDataGenTime);
                    Debug.Log(logBuilder_McWorld.ToString());
                }
#endif

                // Place structures/features in this chunk now that its base data is generated
                terrainGenerator.PlaceFeaturesInChunk(currentArrayCX, currentArrayCY, currentArrayCZ);
                
                int chunk1DIndex_current = ChunkArrayCoordsTo1D(currentArrayCX, currentArrayCY, currentArrayCZ);
                if (chunk1DIndex_current != -1) {
                    chunkDataFinalized_1D[chunk1DIndex_current] = true; // Mark data as final

                    // Instantiate the chunk GameObject if it doesn't exist yet
                    if (chunks_1D[chunk1DIndex_current] == null)
                    {
                        InstantiateAndConfigureChunk(currentArrayCX, currentArrayCY, currentArrayCZ, proc_dx, proc_dy, proc_dz);
                        // The new chunk will request its own mesh update in its InitializeChunk method.
                    } else {
                        // If chunk already exists (e.g. from a previous partial load), ensure its mesh is updated if needed.
                        // This path might not be hit with current radial generation, but good for robustness.
                        RequestChunkMeshUpdate(chunks_1D[chunk1DIndex_current]);
                    }
                } else {
                    Debug.LogError($"[McWorld] ProcessNextWorldStep: Invalid chunk 1D index for Arr({currentArrayCX},{currentArrayCY},{currentArrayCZ}) after data gen.");
                }
                
                chunksProcessedAndInstantiatedCount++;
                currentChunkDataBeingPopulated = false; // Done populating this chunk

                // Log progress
                if (totalWorldChunks > 0) {
                    int currentPercent = (chunksProcessedAndInstantiatedCount * 100) / totalWorldChunks;
                    if (currentPercent / 10 > proc_lastLoggedPercent / 10 && currentPercent <= 100) { 
#if UNITY_EDITOR
                        if (enableVerboseLogging) Debug.Log($"[McWorld] World Processing: ~{currentPercent}% complete ({chunksProcessedAndInstantiatedCount}/{totalWorldChunks} chunks data finalized & instantiated).");
#endif
                        proc_lastLoggedPercent = currentPercent;
                    }
                }
                
                // Advance to find the next chunk to process
                bool foundNext = AdvanceRadialIterator(); 
                if (foundNext) {
                    currentChunkDataBeingPopulated = true; // Start populating data for the new chunk
                    currentChunkDataGen_StartTime_Total = Time.realtimeSinceStartup; 
                    currentChunkDataGen_StartTime_Slice = Time.realtimeSinceStartup;
                    currentChunkDataGen_SliceCount = 0;
                    dataGen_vox_x_in_chunk = 0; dataGen_vox_y_in_chunk = 0; dataGen_vox_z_in_chunk = 0;
                } else { // No more chunks to process
                    isProcessingWorld = false; 
#if UNITY_EDITOR
                    if (enableVerboseLogging) Debug.Log($"[McWorld] All chunks processed and instantiated. Total: {chunksProcessedAndInstantiatedCount}. World steps: {debug_worldStepCallCount}.");
#endif
                }
            }
        }
        else // This case implies data for current proc_dx,dy,dz was done, and we should have advanced.
        {
            // This state should ideally not be reached if logic is correct, as AdvanceRadialIterator
            // should be called right after data completion.
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
            else isProcessingWorld = false; // No next chunk, stop processing
        }
        
        if (isProcessingWorld) ScheduleNextWorldStep(); // If still processing, schedule the next step
    }
    
    private void ScheduleNextWorldStep() {
        if (worldProcessingStepDelay > 0.0001f) SendCustomEventDelayedSeconds(nameof(ProcessNextWorldStep), worldProcessingStepDelay);
        else SendCustomEventDelayedFrames(nameof(ProcessNextWorldStep), 1); // Process next frame if delay is ~0
    }

    // Populates a slice of voxel data for the current target chunk (defined by proc_dx,dy,dz)
    private bool PopulateDataSliceForCurrentTargetChunk()
    {
        int voxelsInDataSliceProcessed = 0;
        // Uses cached block IDs for minor efficiency
        byte grassID = cachedGrassBlockID; byte stoneID = cachedStoneBlockID;
        byte dirtID = cachedDirtBlockID; byte waterID = cachedWaterBlockID;
        int currentSeaLevel = cachedSeaLevel;
        
        // Loop until slice budget is met or chunk data is fully generated
        while (voxelsInDataSliceProcessed < voxelsPerChunkDataProcessingSlice)
        {
            // Check if data generation for the entire current chunk is complete
            // Iteration order: X -> Z -> Y (innermost Y)
            if (dataGen_vox_x_in_chunk >= chunkSizeXZ) // Finished all X columns for this chunk
            { 
                // Log if this "slice" did some work before returning true (chunk finished)
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
                return true; // Current chunk's data generation is complete
            }

            // Calculate terrain surface height ONCE per (X,Z) column (when Y is at the bottom of the column)
            if (dataGen_vox_y_in_chunk == 0)
            {
                // Global voxel coordinates for current column's top
                int currentGlobalX_forHeight = (proc_dx * chunkSizeXZ) + dataGen_vox_x_in_chunk;
                int currentGlobalZ_forHeight = (proc_dz * chunkSizeXZ) + dataGen_vox_z_in_chunk;
                currentColumnTerrainSurfaceY = terrainGenerator.GetBaseTerrainHeight(currentGlobalX_forHeight, currentGlobalZ_forHeight);
            }

            // Global Y coordinate of the current voxel being processed
            int currentGlobalY = (proc_dy * chunkSizeY) + dataGen_vox_y_in_chunk;
            byte blockType = 0; // Default to air

            // Determine block type based on height relative to surface and sea level
            if (currentGlobalY == currentColumnTerrainSurfaceY) blockType = grassID;
            else if (currentGlobalY < currentColumnTerrainSurfaceY) { // Below surface
                if (currentGlobalY >= currentColumnTerrainSurfaceY - 3) blockType = dirtID; // Dirt layer
                else blockType = stoneID; // Stone deeper down
            }
            else { // Above surface (currentGlobalY > currentColumnTerrainSurfaceY)
                if (currentGlobalY <= currentSeaLevel) blockType = waterID; // Water if below or at sea level
                // Else, it remains air (blockType = 0)
            }
            
            // Global X, Z for indexing into the 1D data array (Y is already global)
            int currentGlobalX_forIndex = (proc_dx * chunkSizeXZ) + dataGen_vox_x_in_chunk;
            int currentGlobalZ_forIndex = (proc_dz * chunkSizeXZ) + dataGen_vox_z_in_chunk;
            int dataIndex = GlobalPosToIndex(currentGlobalX_forIndex, currentGlobalY, currentGlobalZ_forIndex);
            if (dataIndex != -1) data[dataIndex] = blockType; // Set voxel data

            voxelsInDataSliceProcessed++;

            // Advance iterators for voxel position within the chunk (Y -> Z -> X)
            dataGen_vox_y_in_chunk++;
            if (dataGen_vox_y_in_chunk >= chunkSizeY) { // Finished current Y column
                dataGen_vox_y_in_chunk = 0; 
                dataGen_vox_z_in_chunk++;
                if (dataGen_vox_z_in_chunk >= chunkSizeXZ) { // Finished current Z row
                    dataGen_vox_z_in_chunk = 0; 
                    dataGen_vox_x_in_chunk++; // Move to next X column
                    // If dataGen_vox_x_in_chunk >= chunkSizeXZ, outer loop condition will catch it.
                }
            }
        }
        // If loop finishes, it means a full slice was processed (or chunk finished mid-slice)
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
        currentChunkDataGen_StartTime_Slice = Time.realtimeSinceStartup; // Reset timer for next slice
        return false; // Slice is done, but chunk data generation is not necessarily complete
    }
    
    // Advances the radial iterator to find the next chunk that needs data generation/instantiation
    private bool AdvanceRadialIterator() 
    {
        // Reset per-chunk data generation iterators when moving to a new target chunk
        dataGen_vox_x_in_chunk = 0; dataGen_vox_y_in_chunk = 0; dataGen_vox_z_in_chunk = 0;

        while(true) // Loop until a valid, unprocessed chunk is found or iterator is exhausted
        {
            // Advance iterator coordinates (inst_dx, dy, dz) for the current radius (inst_radius_iterator)
            if (!inst_shellIterationInitialized) // If starting a new radius shell
            {
                inst_dx_iterator = -inst_radius_iterator; 
                inst_dy_iterator = -inst_radius_iterator; 
                inst_dz_iterator = -inst_radius_iterator;
                inst_shellIterationInitialized = true;
            }
            else // Continue iterating on the current shell
            {
                inst_dz_iterator++;
                if (inst_dz_iterator > inst_radius_iterator) {
                    inst_dz_iterator = -inst_radius_iterator; inst_dy_iterator++;
                    if (inst_dy_iterator > inst_radius_iterator) {
                        inst_dy_iterator = -inst_radius_iterator; inst_dx_iterator++;
                        if (inst_dx_iterator > inst_radius_iterator) { // Finished current radius shell
                            inst_radius_iterator++; // Move to next larger radius
                            inst_shellIterationInitialized = false; // Mark to re-initialize shell iterators
                            continue; // Restart loop to initialize for new radius
                        }
                    }
                }
            }

            // Check if iterator has gone beyond maximum possible world dimensions
            int max_abs_coord_x = (worldDimensionX -1) / 2 + ((worldDimensionX-1)%2); // Max relative offset from center
            int max_abs_coord_y = (worldDimensionY -1) / 2 + ((worldDimensionY-1)%2);
            int max_abs_coord_z = (worldDimensionZ -1) / 2 + ((worldDimensionZ-1)%2);
            
            // If current radius exceeds the largest dimension's span from center, iterator is exhausted
            if (inst_radius_iterator > Mathf.Max(max_abs_coord_x, Mathf.Max(max_abs_coord_y, max_abs_coord_z)) +1) // +1 for safety margin
            {
                return false; // No more chunks to process
            }
            
            // Consider only chunks on the "surface" of the current radius shell
            if (inst_radius_iterator == 0 || Mathf.Abs(inst_dx_iterator) == inst_radius_iterator || Mathf.Abs(inst_dy_iterator) == inst_radius_iterator || Mathf.Abs(inst_dz_iterator) == inst_radius_iterator) 
            {
                // Current iterator position (inst_dx,dy,dz) is a candidate.
                // Set proc_dx,dy,dz to this candidate for processing.
                proc_dx = inst_dx_iterator;
                proc_dy = inst_dy_iterator;
                proc_dz = inst_dz_iterator;

                // Convert relative proc_dx,dy,dz to absolute array coordinates
                int array_cx = proc_dx + chunkOffsetX;
                int array_cy = proc_dy + chunkOffsetY;
                int array_cz = proc_dz + chunkOffsetZ;

                // Check if these array coordinates are valid (within world bounds)
                if (array_cx >= 0 && array_cx < worldDimensionX &&
                    array_cy >= 0 && array_cy < worldDimensionY &&
                    array_cz >= 0 && array_cz < worldDimensionZ)
                {
                    int chunk1DIndex_radial = ChunkArrayCoordsTo1D(array_cx, array_cy, array_cz);
                    // If valid index and data for this chunk hasn't been finalized yet
                    if (chunk1DIndex_radial != -1 && !chunkDataFinalized_1D[chunk1DIndex_radial])
                    {
                        return true; // Found a valid, unprocessed chunk
                    }
                }
            }
        } // End while(true)
    }

    void InstantiateAndConfigureChunk(int array_cx, int array_cy, int array_cz, int centered_dx, int centered_dy, int centered_dz)
    {
        int chunk1DIndex = ChunkArrayCoordsTo1D(array_cx, array_cy, array_cz);
        if (chunk1DIndex == -1) {
            Debug.LogError($"[McWorld] InstantiateAndConfigureChunk: Invalid chunk 1D index for Arr({array_cx},{array_cy},{array_cz})");
            return;
        }

        // Should not happen if AdvanceRadialIterator works correctly, but double check
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
        newChunkGO.transform.SetParent(this.transform, false); // Parent to McWorld, don't change world position
        newChunkGO.transform.localPosition = new Vector3(centered_dx * chunkSizeXZ, centered_dy * chunkSizeY, centered_dz * chunkSizeXZ);
        newChunkGO.transform.localRotation = Quaternion.identity;
        
        McChunk newChunkScript = newChunkGO.GetComponent<McChunk>();
        if (newChunkScript != null) {
            chunks_1D[chunk1DIndex] = newChunkScript; // Store reference
            // Configure chunk parameters
            newChunkScript.chunkSizeXZ = this.chunkSizeXZ; 
            newChunkScript.chunkSizeY = this.chunkSizeY;
            newChunkScript.voxelsPerSlice = this.voxelsPerSliceInChunks; // For its async full rebuilds
            // Set chunk's global position (origin of its local 0,0,0)
            newChunkScript.chunkX = centered_dx * this.chunkSizeXZ; 
            newChunkScript.chunkY = centered_dy * this.chunkSizeY; 
            newChunkScript.chunkZ = centered_dz * this.chunkSizeXZ;
            newChunkScript.template = false; 
            newChunkScript.SetWorld(this); // This will call InitializeChunk in McChunk
            newChunkGO.SetActive(true); // Ensure it's active
        } else { 
            Debug.LogError($"[McWorld] Failed to get McChunk script from instantiated prefab for C_array({array_cx},{array_cy},{array_cz}). GO: {newChunkGO.name}. Destroying GO."); 
            if(newChunkGO != null) Destroy(newChunkGO); 
        }
    }

    // Adds a chunk to the queue for a full asynchronous mesh rebuild.
    public void RequestChunkMeshUpdate(McChunk chunkToUpdate)
    {
        if (chunkToUpdate == null) return;
        // Avoid queuing if it's currently building (especially an immediate build, or if already in queue for async)
        // However, if an immediate partial just finished, it *needs* to be queued for full async.
        // The logic in SetBlock now handles this: it calls immediate partial, then calls this Request for full async.

        // Check for duplicates in the circular buffer to avoid redundant rebuilds
        for (int i = 0; i < chunkRebuildQueue_count; i++)
        {
            int index = (chunkRebuildQueue_head + i) % MAX_REBUILD_QUEUE_SIZE;
            if (chunkRebuildQueue[index] == chunkToUpdate) {
#if UNITY_EDITOR
                // if (enableVerboseLogging) Debug.Log($"[McWorld] Chunk {chunkToUpdate.gameObject.name} already in rebuild queue. Ignoring duplicate request.");
#endif
                return; // Already in queue
            }
        }

        if (chunkRebuildQueue_count < MAX_REBUILD_QUEUE_SIZE)
        {
            chunkRebuildQueue[chunkRebuildQueue_tail] = chunkToUpdate;
            chunkRebuildQueue_tail = (chunkRebuildQueue_tail + 1) % MAX_REBUILD_QUEUE_SIZE;
            chunkRebuildQueue_count++;
#if UNITY_EDITOR
            // if (enableVerboseLogging) Debug.Log($"[McWorld] Queued chunk {chunkToUpdate.gameObject.name} for mesh rebuild. Queue size: {chunkRebuildQueue_count}");
#endif
        }
        else {
#if UNITY_EDITOR
             if (enableVerboseLogging) Debug.LogWarning($"[McWorld] Chunk rebuild queue is full ({MAX_REBUILD_QUEUE_SIZE}). Dropping request for {chunkToUpdate.gameObject.name}.");
#endif
        }
    }

    // Periodically processes the chunk rebuild queue for asynchronous full rebuilds.
    public void ProcessChunkRebuildQueue()
    {
        int processedThisCall = 0;
        int itemsToPotentiallyCheck = chunkRebuildQueue_count; 

        for (int i = 0; i < itemsToPotentiallyCheck && processedThisCall < maxChunksToRebuildPerInterval && chunkRebuildQueue_count > 0; i++)
        {
            McChunk chunkToBuild = chunkRebuildQueue[chunkRebuildQueue_head]; // Peek at head

            if (chunkToBuild == null) { // Should not happen if queue is managed well
                chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE;
                chunkRebuildQueue_count--;
                continue;
            }
            // If chunk is already building (e.g. an immediate partial just happened, or it started its async build from a previous queue check)
            if (chunkToBuild.isBuildingMesh) {
                // If it's a very long async build, it might stay at head.
                // Consider moving to tail if not ready, to give others a chance.
                // For now, just skip if it's busy.
                // Rotate the queue: move this item to the tail if it's not ready and there are others.
                if (chunkRebuildQueue_count > 1) {
                    chunkRebuildQueue[chunkRebuildQueue_tail] = chunkToBuild; // Copy to tail
                    chunkRebuildQueue_tail = (chunkRebuildQueue_tail + 1) % MAX_REBUILD_QUEUE_SIZE;
                    chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE; // Advance head (count unchanged by rotate)
                }
#if UNITY_EDITOR
                // if (enableVerboseLogging) Debug.Log($"[McWorld.RebuildQueue] Chunk {chunkToBuild.gameObject.name} is already building. Skipping or rotating.");
#endif
                continue; // Try next item or wait for next interval
            }


            bool canBuild = false;
            switch (neighboringChunksProcessingMode)
            {
                case 0: // Off - always build if chunk data is ready (which it should be if queued by SetBlock or initial gen)
                    canBuild = true;
                    break;
                case 1: // Normal - wait for neighbors' data to be finalized
                    canBuild = AreAllNeighborsDataFinalized(chunkToBuild);
                    break;
                case 2: // Regenerate - build this chunk, then try to queue neighbors for their rebuilds
                    canBuild = true; 
                    break;
                default: 
                    canBuild = AreAllNeighborsDataFinalized(chunkToBuild); // Default to Normal mode
                    Debug.LogWarning($"[McWorld] Unknown neighboringChunksProcessingMode: {neighboringChunksProcessingMode}. Defaulting to mode 1.");
                    break;
            }

            if (canBuild)
            {
                // Dequeue
                chunkRebuildQueue[chunkRebuildQueue_head] = null; // Clear reference from queue
                chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE;
                chunkRebuildQueue_count--;

                // Start the full asynchronous mesh build (processImmediately = false, no POI)
#if UNITY_EDITOR
                // if (enableVerboseLogging) Debug.Log($"[McWorld.RebuildQueue] Starting async full rebuild for {chunkToBuild.gameObject.name}.");
#endif
                chunkToBuild.StartBuildMesh(false); 
                processedThisCall++;

                if (neighboringChunksProcessingMode == 2)
                {
                    TriggerNeighborMeshRebuilds(chunkToBuild); // If mode 2, queue neighbors
                }
            }
            else // Cannot build this chunk yet (e.g., waiting for neighbors)
            {
                // Move item at head to the tail to allow other items to be processed.
                if (chunkRebuildQueue_count > 1) 
                {
                    chunkRebuildQueue[chunkRebuildQueue_tail] = chunkToBuild; // Copy item to tail
                    chunkRebuildQueue_tail = (chunkRebuildQueue_tail + 1) % MAX_REBUILD_QUEUE_SIZE;
                    chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE; // Advance head
                }
                // If only one item and it's not ready, it stays at head. Loop will exit.
            }
        }
        // Schedule the next check of the rebuild queue
        SendCustomEventDelayedSeconds(nameof(ProcessChunkRebuildQueue), chunkRebuildInterval);
    }

    // Checks if data for all 6 direct neighbors of a given chunk has been finalized.
    private bool AreAllNeighborsDataFinalized(McChunk chunk)
    {
        if (chunk == null) return false; 

        // Centered chunk coordinates of the input chunk
        int centeredCX = chunk.chunkX / chunkSizeXZ;
        int centeredCY = chunk.chunkY / chunkSizeY;
        int centeredCZ = chunk.chunkZ / chunkSizeXZ;

        for (int i = 0; i < 6; i++) 
        {
            // Centered coordinates of the neighbor
            int neighborCenteredX = centeredCX + neighbor_dx_offsets[i];
            int neighborCenteredY = centeredCY + neighbor_dy_offsets[i];
            int neighborCenteredZ = centeredCZ + neighbor_dz_offsets[i];

            // Absolute array coordinates of the neighbor
            int neighborArrayX = neighborCenteredX + chunkOffsetX;
            int neighborArrayY = neighborCenteredY + chunkOffsetY;
            int neighborArrayZ = neighborCenteredZ + chunkOffsetZ;

            int neighbor1DIndex = ChunkArrayCoordsTo1D(neighborArrayX, neighborArrayY, neighborArrayZ);
            if (neighbor1DIndex != -1) // If neighbor is within world bounds
            {
                if (!chunkDataFinalized_1D[neighbor1DIndex]) // If neighbor's data is not yet final
                {
#if UNITY_EDITOR
                    // if (enableVerboseLogging) Debug.Log($"[McWorld] Chunk {chunk.gameObject.name} waiting for neighbor Arr({neighborArrayX},{neighborArrayY},{neighborArrayZ}) data.");
#endif
                    return false; // Not all neighbors ready
                }
            }
            // If neighbor is outside world bounds (neighbor1DIndex == -1), it's considered 'finalized' for culling purposes.
        }
        return true; // All in-bounds neighbors have finalized data
    }

    // If neighboringChunksProcessingMode is 2, this queues ready neighbors for mesh rebuild.
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
            if (neighbor1DIndex != -1) // If neighbor is within world bounds
            {
                // Check if neighbor's data is finalized AND it has an instantiated chunk script
                if (chunkDataFinalized_1D[neighbor1DIndex])
                {
                    McChunk neighborChunk = chunks_1D[neighbor1DIndex];
                    if (neighborChunk != null && !neighborChunk.isBuildingMesh) // Ensure not already building
                    {
                        RequestChunkMeshUpdate(neighborChunk); // Add to async rebuild queue
                    }
                }
            }
        }
    }

    // Converts global voxel coordinates to 1D array index for the 'data' array.
    private int GlobalPosToIndex(int globalX, int globalY, int globalZ)
    {
        // Convert global coords to 0-based array coords using offsets
        int arrayCoordX = globalX + globalVoxelOffsetX;
        int arrayCoordY = globalY + globalVoxelOffsetY;
        int arrayCoordZ = globalZ + globalVoxelOffsetZ;

        // Check bounds
        if (arrayCoordX < 0 || arrayCoordX >= worldSizeX_voxels || 
            arrayCoordY < 0 || arrayCoordY >= worldSizeY_voxels || 
            arrayCoordZ < 0 || arrayCoordZ >= worldSizeZ_voxels) return -1; // Out of bounds
        
        // Flatten 3D array coords to 1D index
        return arrayCoordY * layerSize_voxels_for_1D_data + arrayCoordZ * worldSizeX_voxels + arrayCoordX;
    }

    // Gets the block type at global voxel coordinates.
    public byte GetBlock(int globalX, int globalY, int globalZ)
    {
        int index = GlobalPosToIndex(globalX, globalY, globalZ);
        if (index == -1) return 0; // Air for out-of-bounds
        if (data == null || index < 0 || index >= data.Length) {
#if UNITY_EDITOR
            // Debug.LogError($"[McWorld.GetBlock] Index {index} out of bounds for data array (len { (data != null ? data.Length : -1) }) at G({globalX},{globalY},{globalZ})");
#endif
            return 0; // Should not happen if bounds checks are correct
        }
        return data[index];
    }

    // Moved local function UpdateAffectedNeighbor to be a private instance method
    private void _UpdateAffectedNeighbor(int neighborCenteredCX, int neighborCenteredCY, int neighborCenteredCZ, 
                                     int neighborLocalPoiX, int neighborLocalPoiY, int neighborLocalPoiZ, 
                                     bool rebuildImmediately) {
        int neighborArrX = neighborCenteredCX + chunkOffsetX;
        int neighborArrY = neighborCenteredCY + chunkOffsetY;
        int neighborArrZ = neighborCenteredCZ + chunkOffsetZ;
        
        int neighbor1DIdx = ChunkArrayCoordsTo1D(neighborArrX, neighborArrY, neighborArrZ);
        if (neighbor1DIdx != -1 && chunkDataFinalized_1D[neighbor1DIdx]) // Ensure neighbor is valid and its data is ready
        {
            McChunk neighborChunk = GetChunkScript(neighborArrX, neighborArrY, neighborArrZ);
            if (neighborChunk != null) {
                if (rebuildImmediately) {
#if UNITY_EDITOR
                    // if (enableVerboseLogging) Debug.Log($"[McWorld.SetBlock] Triggering IMMEDIATE PARTIAL update for NEIGHBOR chunk {neighborChunk.gameObject.name} at its local ({neighborLocalPoiX},{neighborLocalPoiY},{neighborLocalPoiZ})");
#endif
                    neighborChunk.StartBuildMesh(true);
                    RequestChunkMeshUpdate(neighborChunk); // Queue for full async rebuild
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
        if (data[dataIndex] == blockType) { 
#if UNITY_EDITOR
            // if (enableVerboseLogging) Debug.Log($"[McWorld.SetBlock] Block at G({globalX},{globalY},{globalZ}) is already type {blockType}. No change.");
#endif
            return; // No change needed
        }
        
        data[dataIndex] = blockType; // Update voxel data

        // Determine which chunk contains this block (centered coordinates)
        int centeredChunkX = Mathf.FloorToInt((float)globalX / chunkSizeXZ);
        int centeredChunkY = Mathf.FloorToInt((float)globalY / chunkSizeY);
        int centeredChunkZ = Mathf.FloorToInt((float)globalZ / chunkSizeXZ);
        
        // Convert to chunk array coordinates
        int array_cx = centeredChunkX + chunkOffsetX; 
        int array_cy = centeredChunkY + chunkOffsetY; 
        int array_cz = centeredChunkZ + chunkOffsetZ;
        
        McChunk targetChunk = GetChunkScript(array_cx, array_cy, array_cz);
        // Local coordinates of the block within its chunk
        int localPoiX = globalX - (centeredChunkX * chunkSizeXZ);
        int localPoiY = globalY - (centeredChunkY * chunkSizeY);
        int localPoiZ = globalZ - (centeredChunkZ * chunkSizeXZ);
        

        // Update the primary chunk
        if (targetChunk != null) { 
            if (rebuildImmediately) {
                // Perform immediate partial update. partialUpdateRadius is a world setting.
#if UNITY_EDITOR
                // if (enableVerboseLogging) Debug.Log($"[McWorld.SetBlock] Triggering IMMEDIATE PARTIAL update for chunk {targetChunk.gameObject.name} at local ({localPoiX},{localPoiY},{localPoiZ})");
#endif
                targetChunk.StartBuildMesh(true); 
                RequestChunkMeshUpdate(targetChunk); 
            } else { // Normal asynchronous full rebuild (no POI)
                RequestChunkMeshUpdate(targetChunk); 
            }
        } else {
#if UNITY_EDITOR
            if (enableVerboseLogging) Debug.LogWarning($"[McWorld.SetBlock] Target chunk at Arr({array_cx},{array_cy},{array_cz}) for G({globalX},{globalY},{globalZ}) is null. Cannot update mesh.");
#endif
        }
        
        // Update affected neighbors
        // Check X-axis neighbors
        if (localPoiX == 0) _UpdateAffectedNeighbor(centeredChunkX - 1, centeredChunkY, centeredChunkZ, chunkSizeXZ - 1, localPoiY, localPoiZ, rebuildImmediately);
        if (localPoiX == chunkSizeXZ - 1) _UpdateAffectedNeighbor(centeredChunkX + 1, centeredChunkY, centeredChunkZ, 0, localPoiY, localPoiZ, rebuildImmediately);
        // Check Y-axis neighbors
        if (localPoiY == 0) _UpdateAffectedNeighbor(centeredChunkX, centeredChunkY - 1, centeredChunkZ, localPoiX, chunkSizeY - 1, localPoiZ, rebuildImmediately);
        if (localPoiY == chunkSizeY - 1) _UpdateAffectedNeighbor(centeredChunkX, centeredChunkY + 1, centeredChunkZ, localPoiX, 0, localPoiZ, rebuildImmediately);
        // Check Z-axis neighbors
        if (localPoiZ == 0) _UpdateAffectedNeighbor(centeredChunkX, centeredChunkY, centeredChunkZ - 1, localPoiX, localPoiY, chunkSizeXZ - 1, rebuildImmediately);
        if (localPoiZ == chunkSizeXZ - 1) _UpdateAffectedNeighbor(centeredChunkX, centeredChunkY, centeredChunkZ + 1, localPoiX, localPoiY, 0, rebuildImmediately);
    }

    // Gets the McChunk script for given chunk array coordinates.
    public McChunk GetChunkScript(int array_cx, int array_cy, int array_cz)
    {
        int index = ChunkArrayCoordsTo1D(array_cx, array_cy, array_cz);
        if (index == -1) return null; // Out of bounds
        if (chunks_1D == null || index < 0 || index >= chunks_1D.Length) {
#if UNITY_EDITOR
             // Debug.LogError($"[McWorld.GetChunkScript] Calculated index {index} is out of bounds for chunks_1D (len { (chunks_1D != null ? chunks_1D.Length : -1) }). Input Arr({array_cx},{array_cy},{array_cz})");
#endif
             return null;
        }
        return chunks_1D[index];
    }
}
