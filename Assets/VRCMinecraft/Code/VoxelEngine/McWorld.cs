using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRRefAssist;
using System.Text;

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

    [Header("Voxel Data Storage (RLE)")]
    // World voxel data is now stored per-chunk in a generic object array.
    // Each object can be either:
    // 1. A ushort[] for uncompressed, detailed chunks.
    // 2. A ushort[chunkSizeY] for chunks compressed into horizontal layers (RLE).
    private object[] chunkDataArray;
    // A parallel array of flags for fast checking of a chunk's compression state,
    // avoiding slower type checks with 'is' or 'GetType()'.
    private bool[] isChunkCompressedAsLayers;

    private int worldSizeX_voxels;
    private int worldSizeY_voxels;
    private int worldSizeZ_voxels;
    private int totalWorldVoxels;
    private int totalWorldChunks;

    [HideInInspector] public int chunkOffsetX;
    [HideInInspector] public int chunkOffsetY;
    [HideInInspector] public int chunkOffsetZ;

    [HideInInspector] public int globalVoxelOffsetX;
    [HideInInspector] public int globalVoxelOffsetY;
    [HideInInspector] public int globalVoxelOffsetZ;

    [Header("Generators")]
    [Tooltip("Reference to the McTerrainGenerator for base terrain and structure placement.")]
    [SerializeField, FindObjectOfType(true)]
    private McTerrainGenerator terrainGenerator;
    [Tooltip("Reference to the McBlockTypeManager for getting block properties.")]
    [SerializeField, FindObjectOfType(true)]
    public McBlockTypeManager blockTypeManager;
    
    [Header("Performance Parameters (McWorld)")]
    [Tooltip("Delay (seconds) between processing steps for world generation. 0 for frame-by-frame.")]
    public float worldProcessingStepDelay = 0f;
    [Tooltip("How often (seconds) McWorld checks its queue to start rebuilding chunk meshes.")]
    public float chunkRebuildInterval = 0.03f;
    [Tooltip("Max number of chunks McWorld will start rebuilding in a single interval.")]
    public int maxChunksToRebuildPerInterval = 1;
    [Tooltip("Neighboring chunks processing mode for async rebuilds. 0 = Off, 1 = Normal (wait for neighbors), 2 = Regenerate (build this, queue neighbors).")]
    public int neighboringChunksProcessingMode = 1;
    
    [Header("Debug")]
    #if UNITY_EDITOR
    public bool enableVerboseLogging = true;
    #endif
    private StringBuilder logBuilder_McWorld;

    // Chunk Rebuild Queue (Circular Buffer)
    private McChunk[] chunkRebuildQueue;
    private int chunkRebuildQueue_head = 0;
    private int chunkRebuildQueue_tail = 0;
    private int chunkRebuildQueue_count = 0;
    private const int MAX_REBUILD_QUEUE_SIZE = 256;

    // World Processing State
    private bool isProcessingWorld = false;
    private int proc_lastLoggedPercent = -1;
    private int chunksProcessedCount = 0;
    private int radialIterator_chunkIndex = 0;
    private int[] radialChunkOrder;

    // Cached values from TerrainGenerator & BlockTypeManager
    private ushort cachedPackedGrassData;
    private ushort cachedPackedStoneData;
    private ushort cachedPackedDirtData;
    private ushort cachedPackedWaterData;
    private int cachedSeaLevel;
    
    private byte _lastPackedBlockId = 255; 
    private ushort _lastPackedUshort;

    private readonly int[] neighbor_dx_offsets = { 1, -1, 0,  0, 0,  0 };
    private readonly int[] neighbor_dy_offsets = { 0,  0, 1, -1, 0,  0 };
    private readonly int[] neighbor_dz_offsets = { 0,  0, 0,  0, 1, -1 };


    void Start()
    {
        if (chunkPrefab == null || terrainGenerator == null || blockTypeManager == null) {
            Debug.LogError("[McWorld] A critical component (Chunk Prefab, Terrain Generator, or Block Type Manager) is not assigned! Aborting.");
            this.enabled = false;
            return;
        }

        logBuilder_McWorld = new StringBuilder(512);

        InitializeWorldParameters();
        InitializeChunkStorage();
        InitializeVoxelDataStorage();
        
        terrainGenerator.InitializeGenerator(worldSeedString.GetHashCode());

        CacheCommonBlockData();
        
        chunkRebuildQueue = new McChunk[MAX_REBUILD_QUEUE_SIZE];
        
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

        totalWorldVoxels = worldSizeX_voxels * worldSizeY_voxels * worldSizeZ_voxels;
        totalWorldChunks = worldDimensionX * worldDimensionY * worldDimensionZ;
    }

    void InitializeChunkStorage()
    {
        if (totalWorldChunks <= 0) {
            Debug.LogError("[McWorld] totalWorldChunks is zero. Cannot initialize chunk storage.");
            this.enabled = false;
            return;
        }
        chunks_1D = new McChunk[totalWorldChunks];
        chunkDataFinalized_1D = new bool[totalWorldChunks];
    }

    void InitializeVoxelDataStorage()
    {
        if (totalWorldChunks <= 0) {
            this.enabled = false;
            return;
        }
        // Initialize the new data storage arrays.
        chunkDataArray = new object[totalWorldChunks];
        isChunkCompressedAsLayers = new bool[totalWorldChunks];
    }

    void CacheCommonBlockData()
    {
        cachedPackedGrassData = PackBlockData(terrainGenerator.grassBlockID);
        cachedPackedStoneData = PackBlockData(terrainGenerator.stoneBlockID);
        cachedPackedDirtData = PackBlockData(terrainGenerator.dirtBlockID);
        cachedPackedWaterData = PackBlockData(terrainGenerator.waterBlockID);
        cachedSeaLevel = terrainGenerator.seaLevel;
    }
    
    public void StartWorldProcessing()
    {
        if (isProcessingWorld) return;
        if (totalWorldChunks == 0) return;

        isProcessingWorld = true;
        proc_lastLoggedPercent = -1;
        chunksProcessedCount = 0;
        
        // Pre-calculate the processing order to be a radial expansion from the center.
        GenerateRadialChunkOrder();
        radialIterator_chunkIndex = 0;

        ProcessNextWorldStep();
    }

    public void ProcessNextWorldStep()
    {
        if (!isProcessingWorld) return;

        // Get the next chunk to process from the pre-calculated radial order.
        int chunk1DIndex = radialChunkOrder[radialIterator_chunkIndex];
        
        int array_cx, array_cy, array_cz;
        Chunk1DToArrrayCoords(chunk1DIndex, out array_cx, out array_cy, out array_cz);

        // --- 1. Generate Voxel Data for this Chunk ---
        GenerateDataForChunk(array_cx, array_cy, array_cz);

        // --- 2. Place Features (trees, etc.) ---
        terrainGenerator.PlaceFeaturesInChunk(array_cx, array_cy, array_cz);

        // --- 3. Finalize and Instantiate ---
        chunkDataFinalized_1D[chunk1DIndex] = true;
        InstantiateAndConfigureChunk(array_cx, array_cy, array_cz);
        chunksProcessedCount++;
        
        // --- Progress Reporting ---
        if (totalWorldChunks > 0) {
            int currentPercent = (chunksProcessedCount * 100) / totalWorldChunks;
            if (currentPercent / 10 > proc_lastLoggedPercent / 10) {
                #if UNITY_EDITOR
                if (enableVerboseLogging) Debug.Log($"[McWorld] World Processing: ~{currentPercent}% complete ({chunksProcessedCount}/{totalWorldChunks} chunks processed).");
                #endif
                proc_lastLoggedPercent = currentPercent;
            }
        }
        
        // --- Advance Iterator and Schedule Next Step ---
        radialIterator_chunkIndex++;
        if (radialIterator_chunkIndex >= totalWorldChunks)
        {
            isProcessingWorld = false;
            #if UNITY_EDITOR
            if (enableVerboseLogging) Debug.Log($"[McWorld] All {totalWorldChunks} chunks processed. World generation complete.");
            #endif
        }
        else
        {
            ScheduleNextWorldStep();
        }
    }
    
    private void GenerateDataForChunk(int array_cx, int array_cy, int array_cz)
    {
        int chunk1DIndex = ChunkArrayCoordsTo1D(array_cx, array_cy, array_cz);
        if (chunk1DIndex == -1) return;

        int chunkVoxels = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
        ushort[] tempChunkData = new ushort[chunkVoxels];

        int chunkWorldStartX = (array_cx - chunkOffsetX) * chunkSizeXZ;
        int chunkWorldStartY = (array_cy - chunkOffsetY) * chunkSizeY;
        int chunkWorldStartZ = (array_cz - chunkOffsetZ) * chunkSizeXZ;

        // Generate the full, uncompressed data first
        for (int y = 0; y < chunkSizeY; y++)
        {
            int globalY = chunkWorldStartY + y;
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                int globalZ = chunkWorldStartZ + z;
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    int globalX = chunkWorldStartX + x;

                    int surfaceHeight = terrainGenerator.GetBaseTerrainHeight(globalX, globalZ);
                    ushort blockData = 0; // Air

                    if (globalY < surfaceHeight)
                    {
                        if (globalY >= surfaceHeight - 3) blockData = cachedPackedDirtData;
                        else blockData = cachedPackedStoneData;
                    }
                    else if (globalY == surfaceHeight)
                    {
                        blockData = cachedPackedGrassData;
                    }
                    else if (globalY <= cachedSeaLevel)
                    {
                        blockData = cachedPackedWaterData;
                    }
                    
                    int localIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                    tempChunkData[localIndex] = blockData;
                }
            }
        }
        
        // Attempt to compress the data
        ushort[] compressedLayerData = TryCompressAsLayers(tempChunkData, chunkSizeXZ, chunkSizeY);

        if (compressedLayerData != null)
        {
            chunkDataArray[chunk1DIndex] = compressedLayerData;
            isChunkCompressedAsLayers[chunk1DIndex] = true;
        }
        else
        {
            chunkDataArray[chunk1DIndex] = tempChunkData;
            isChunkCompressedAsLayers[chunk1DIndex] = false;
        }
    }

    private ushort[] TryCompressAsLayers(ushort[] fullChunkData, int sizeXZ, int sizeY)
    {
        ushort[] layerData = new ushort[sizeY];
        int layerVoxelCount = sizeXZ * sizeXZ;

        for (int y = 0; y < sizeY; y++)
        {
            int layerStartIndex = y * layerVoxelCount;
            ushort firstBlockOfLayer = fullChunkData[layerStartIndex];

            for (int i = 1; i < layerVoxelCount; i++)
            {
                if (fullChunkData[layerStartIndex + i] != firstBlockOfLayer)
                {
                    return null; // Layer is not uniform, compression fails for this chunk.
                }
            }
            layerData[y] = firstBlockOfLayer;
        }
        return layerData; // All layers in the chunk were uniform.
    }
    
    // Decompresses layer data into a full voxel array.
    private ushort[] DecompressLayerData(ushort[] layerData)
    {
        int chunkVoxelCount = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
        int layerVoxelCount = chunkSizeXZ * chunkSizeXZ;
        ushort[] fullData = new ushort[chunkVoxelCount];

        for (int y = 0; y < chunkSizeY; y++)
        {
            int layerStartIndex = y * layerVoxelCount;
            ushort blockForLayer = layerData[y];
            for (int i = 0; i < layerVoxelCount; i++)
            {
                fullData[layerStartIndex + i] = blockForLayer;
            }
        }
        return fullData;
    }

    private void ScheduleNextWorldStep()
    {
        if (worldProcessingStepDelay > 0.0001f) SendCustomEventDelayedSeconds(nameof(ProcessNextWorldStep), worldProcessingStepDelay);
        else SendCustomEventDelayedFrames(nameof(ProcessNextWorldStep), 1);
    }

    void InstantiateAndConfigureChunk(int array_cx, int array_cy, int array_cz)
    {
        int chunk1DIndex = ChunkArrayCoordsTo1D(array_cx, array_cy, array_cz);
        if (chunk1DIndex == -1) return;

        if (chunks_1D[chunk1DIndex] != null) return;

        int centered_dx = array_cx - chunkOffsetX;
        int centered_dy = array_cy - chunkOffsetY;
        int centered_dz = array_cz - chunkOffsetZ;

        GameObject newChunkGO = Instantiate(chunkPrefab);
        newChunkGO.name = $"Chunk_arr({array_cx},{array_cy},{array_cz})";
        newChunkGO.transform.SetParent(this.transform, false);
        newChunkGO.transform.localPosition = new Vector3(centered_dx * chunkSizeXZ, centered_dy * chunkSizeY, centered_dz * chunkSizeXZ);

        McChunk newChunkScript = newChunkGO.GetComponent<McChunk>();
        chunks_1D[chunk1DIndex] = newChunkScript;

        newChunkScript.world = this;
        newChunkScript.chunkSizeXZ = this.chunkSizeXZ;
        newChunkScript.chunkSizeY = this.chunkSizeY;
        newChunkScript.chunkX_world = centered_dx;
        newChunkScript.chunkY_world = centered_dy;
        newChunkScript.chunkZ_world = centered_dz;

        newChunkGO.SetActive(true);
        RequestChunkMeshUpdate(newChunkScript);
    }
    
    public void RequestChunkMeshUpdate(McChunk chunkToUpdate)
    {
        if (chunkToUpdate == null || chunkRebuildQueue_count >= MAX_REBUILD_QUEUE_SIZE) return;
        
        // Prevent duplicates
        for (int i = 0; i < chunkRebuildQueue_count; i++)
        {
            int index = (chunkRebuildQueue_head + i) % MAX_REBUILD_QUEUE_SIZE;
            if (chunkRebuildQueue[index] == chunkToUpdate) return;
        }

        chunkRebuildQueue[chunkRebuildQueue_tail] = chunkToUpdate;
        chunkRebuildQueue_tail = (chunkRebuildQueue_tail + 1) % MAX_REBUILD_QUEUE_SIZE;
        chunkRebuildQueue_count++;
    }

    public void ProcessChunkRebuildQueue()
    {
        int processedThisCall = 0;
        int itemsToPotentiallyCheck = chunkRebuildQueue_count;

        for (int i = 0; i < itemsToPotentiallyCheck && processedThisCall < maxChunksToRebuildPerInterval && chunkRebuildQueue_count > 0; i++)
        {
            McChunk chunkToBuild = chunkRebuildQueue[chunkRebuildQueue_head];

            if (chunkToBuild == null) {
                // Dequeue null chunk
                chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE;
                chunkRebuildQueue_count--;
                continue;
            }
            
            if (chunkToBuild.isBuildingMesh) {
                // Still building, move to back of queue to try later
                if (chunkRebuildQueue_count > 1) {
                    chunkRebuildQueue[chunkRebuildQueue_tail] = chunkToBuild;
                    chunkRebuildQueue_tail = (chunkRebuildQueue_tail + 1) % MAX_REBUILD_QUEUE_SIZE;
                    chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE;
                }
                continue;
            }

            bool canBuild = (neighboringChunksProcessingMode == 0) || AreAllNeighborsDataFinalized(chunkToBuild);

            if (canBuild)
            {
                // Dequeue and build
                chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE;
                chunkRebuildQueue_count--;
                
                chunkToBuild.BuildMesh();
                processedThisCall++;

                if (neighboringChunksProcessingMode == 2)
                {
                    TriggerNeighborMeshRebuilds(chunkToBuild);
                }
            }
            else
            {
                // Cannot build yet (waiting for neighbors), move to back of queue
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

    // Gets the packed ushort data at global voxel coordinates.
    public ushort GetBlock(int globalX, int globalY, int globalZ)
    {
        int centeredChunkX = Mathf.FloorToInt((float)globalX / chunkSizeXZ);
        int centeredChunkY = Mathf.FloorToInt((float)globalY / chunkSizeY);
        int centeredChunkZ = Mathf.FloorToInt((float)globalZ / chunkSizeXZ);
        
        int chunkIndex = ChunkCenteredCoordsTo1D(centeredChunkX, centeredChunkY, centeredChunkZ);
        if (chunkIndex == -1) return 0; // Air for out-of-bounds

        object data = chunkDataArray[chunkIndex];
        if (data == null) return 0; // Not generated yet

        if (isChunkCompressedAsLayers[chunkIndex])
        {
            ushort[] layerData = (ushort[])data;
            int localY = globalY - centeredChunkY * chunkSizeY;
            return layerData[localY];
        }
        else
        {
            ushort[] chunkVoxelData = (ushort[])data;
            int localX = globalX - centeredChunkX * chunkSizeXZ;
            int localY = globalY - centeredChunkY * chunkSizeY;
            int localZ = globalZ - centeredChunkZ * chunkSizeXZ;
            int localIndex = localY * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
            return chunkVoxelData[localIndex];
        }
    }
    
    // Sets the block type at global voxel coordinates and triggers mesh updates.
    public void SetBlock(int globalX, int globalY, int globalZ, byte blockType)
    {
        int centeredChunkX = Mathf.FloorToInt((float)globalX / chunkSizeXZ);
        int centeredChunkY = Mathf.FloorToInt((float)globalY / chunkSizeY);
        int centeredChunkZ = Mathf.FloorToInt((float)globalZ / chunkSizeXZ);
        
        int chunkIndex = ChunkCenteredCoordsTo1D(centeredChunkX, centeredChunkY, centeredChunkZ);
        if (chunkIndex == -1) return;

        object data = chunkDataArray[chunkIndex];
        if (data == null) return;
        
        ushort[] chunkVoxelData;

        // If the chunk is compressed, we must decompress it before modifying it.
        if (isChunkCompressedAsLayers[chunkIndex])
        {
            chunkVoxelData = DecompressLayerData((ushort[])data);
            chunkDataArray[chunkIndex] = chunkVoxelData;
            isChunkCompressedAsLayers[chunkIndex] = false;
        }
        else
        {
            chunkVoxelData = (ushort[])data;
        }

        int localX = globalX - centeredChunkX * chunkSizeXZ;
        int localY = globalY - centeredChunkY * chunkSizeY;
        int localZ = globalZ - centeredChunkZ * chunkSizeXZ;
        int localIndex = localY * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
        
        ushort newPackedData = PackBlockData(blockType);
        if (chunkVoxelData[localIndex] == newPackedData) return; // No change needed

        chunkVoxelData[localIndex] = newPackedData;

        // --- Trigger mesh updates for this chunk and any affected neighbors ---
        McChunk targetChunk = GetChunkScriptFromCentered(centeredChunkX, centeredChunkY, centeredChunkZ);
        if (targetChunk != null) RequestChunkMeshUpdate(targetChunk);

        if (localX == 0) TriggerNeighborUpdate(centeredChunkX - 1, centeredChunkY, centeredChunkZ);
        if (localX == chunkSizeXZ - 1) TriggerNeighborUpdate(centeredChunkX + 1, centeredChunkY, centeredChunkZ);
        if (localY == 0) TriggerNeighborUpdate(centeredChunkX, centeredChunkY - 1, centeredChunkZ);
        if (localY == chunkSizeY - 1) TriggerNeighborUpdate(centeredChunkX, centeredChunkY + 1, centeredChunkZ);
        if (localZ == 0) TriggerNeighborUpdate(centeredChunkX, centeredChunkY, centeredChunkZ - 1);
        if (localZ == chunkSizeXZ - 1) TriggerNeighborUpdate(centeredChunkX, centeredChunkY, centeredChunkZ + 1);
    }
    
    private void TriggerNeighborUpdate(int centeredCX, int centeredCY, int centeredCZ)
    {
        McChunk neighborChunk = GetChunkScriptFromCentered(centeredCX, centeredCY, centeredCZ);
        if (neighborChunk != null) RequestChunkMeshUpdate(neighborChunk);
    }

    #region Helper and Utility Methods

    private ushort PackBlockData(byte blockID)
    {
        if (blockID == _lastPackedBlockId) return _lastPackedUshort;
        if (blockID == 0) { _lastPackedBlockId = 0; _lastPackedUshort = 0; return 0; }
        ushort packedData = blockID;
        if (blockTypeManager.GetBlockIsSolid(blockID)) packedData |= (1 << 8);
        packedData |= (ushort)((int)blockTypeManager.GetBlockVisibilityType(blockID) << 9);
        packedData |= (ushort)((int)blockTypeManager.GetBlockShapeType(blockID) << 12);
        _lastPackedBlockId = blockID;
        _lastPackedUshort = packedData;
        return packedData;
    }

    private void TriggerNeighborMeshRebuilds(McChunk chunk)
    {
        for (int i = 0; i < 6; i++)
        {
            TriggerNeighborUpdate(
                chunk.chunkX_world + neighbor_dx_offsets[i],
                chunk.chunkY_world + neighbor_dy_offsets[i],
                chunk.chunkZ_world + neighbor_dz_offsets[i]
            );
        }
    }
    
    private bool AreAllNeighborsDataFinalized(McChunk chunk)
    {
        if (chunk == null) return false;
        for (int i = 0; i < 6; i++)
        {
            int n_arrayX = (chunk.chunkX_world + neighbor_dx_offsets[i]) + chunkOffsetX;
            int n_arrayY = (chunk.chunkY_world + neighbor_dy_offsets[i]) + chunkOffsetY;
            int n_arrayZ = (chunk.chunkZ_world + neighbor_dz_offsets[i]) + chunkOffsetZ;
            int n_1DIndex = ChunkArrayCoordsTo1D(n_arrayX, n_arrayY, n_arrayZ);
            if (n_1DIndex != -1 && !chunkDataFinalized_1D[n_1DIndex]) return false;
        }
        return true;
    }
    
    public object GetChunkDataObject(int centered_cx, int centered_cy, int centered_cz)
    {
        int index = ChunkCenteredCoordsTo1D(centered_cx, centered_cy, centered_cz);
        if (index == -1) return null;
        return chunkDataArray[index];
    }

    public bool IsChunkDataLayerCompressed(int centered_cx, int centered_cy, int centered_cz)
    {
        int index = ChunkCenteredCoordsTo1D(centered_cx, centered_cy, centered_cz);
        if (index == -1) return false;
        return isChunkCompressedAsLayers[index];
    }
    
    private int ChunkArrayCoordsTo1D(int arrayX, int arrayY, int arrayZ)
    {
        if (arrayX < 0 || arrayX >= worldDimensionX || arrayY < 0 || arrayY >= worldDimensionY || arrayZ < 0 || arrayZ >= worldDimensionZ) return -1;
        return (arrayZ * worldDimensionX * worldDimensionY) + (arrayY * worldDimensionX) + arrayX;
    }
    
    private void Chunk1DToArrrayCoords(int index, out int x, out int y, out int z)
    {
        z = index / (worldDimensionX * worldDimensionY);
        y = (index / worldDimensionX) % worldDimensionY;
        x = index % worldDimensionX;
    }
    
    private int ChunkCenteredCoordsTo1D(int centeredX, int centeredY, int centeredZ)
    {
        return ChunkArrayCoordsTo1D(centeredX + chunkOffsetX, centeredY + chunkOffsetY, centeredZ + chunkOffsetZ);
    }
    
    public McChunk GetChunkScriptFromCentered(int centered_cx, int centered_cy, int centered_cz)
    {
        int index = ChunkCenteredCoordsTo1D(centered_cx, centered_cy, centered_cz);
        if (index == -1 || chunks_1D == null || index >= chunks_1D.Length) return null;
        return chunks_1D[index];
    }

    private void GenerateRadialChunkOrder()
    {
        radialChunkOrder = new int[totalWorldChunks];
        int count = 0;
        int maxRadius = Mathf.Max(worldDimensionX, Mathf.Max(worldDimensionY, worldDimensionZ));
        
        for (int r = 0; r < maxRadius; r++)
        {
            for (int y = -r; y <= r; y++)
            {
                for (int z = -r; z <= r; z++)
                {
                    for (int x = -r; x <= r; x++)
                    {
                        if (Mathf.Abs(x) == r || Mathf.Abs(y) == r || Mathf.Abs(z) == r)
                        {
                            int chunkIndex = ChunkCenteredCoordsTo1D(x, y, z);
                            if (chunkIndex != -1)
                            {
                                bool alreadyAdded = false;
                                for (int i = 0; i < count; i++) {
                                    if (radialChunkOrder[i] == chunkIndex) {
                                        alreadyAdded = true;
                                        break;
                                    }
                                }
                                if (!alreadyAdded) {
                                    radialChunkOrder[count++] = chunkIndex;
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    #endregion
}
