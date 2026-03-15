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

    [Header("Debugging")]
    public bool enableGenerationTimings = false;

    private int totalWorldChunks;
    [HideInInspector] public int chunkOffsetX;
    [HideInInspector] public int chunkOffsetY;
    [HideInInspector] public int chunkOffsetZ;

    [HideInInspector] public int globalVoxelOffsetX;
    [HideInInspector] public int globalVoxelOffsetY;
    [HideInInspector] public int globalVoxelOffsetZ;

    [Header("System References")]
    [SerializeField, FindObjectOfType(true)]
    public McTerrainGenerator terrainGenerator;
    [SerializeField, FindObjectOfType(true)]
    public McBlockTypeManager blockTypeManager;
    [SerializeField, FindObjectOfType(true)]
    private McCoordinator coordinator;
    
    private readonly int[] neighbor_dx_offsets = { 1, -1, 0,  0, 0,  0 };
    private readonly int[] neighbor_dy_offsets = { 0,  0, 1, -1, 0,  0 };
    private readonly int[] neighbor_dz_offsets = { 0,  0, 0,  0, 1, -1 };


    void Start()
    {
        if (chunkPrefab == null || terrainGenerator == null || blockTypeManager == null || coordinator == null) {
            Debug.LogError("[McWorld] A critical component is not assigned! Aborting.");
            this.enabled = false;
            return;
        }
        
        InitializeWorldParameters();
        InitializeChunkStorage();
        terrainGenerator.init(McUtils.GetMinecraftSeed(worldSeedString));
        
        int[] radialChunkOrder = GenerateRadialChunkOrder();
        coordinator.InitializeAndStartProcessing(this, radialChunkOrder, totalWorldChunks);
    }

    void InitializeWorldParameters()
    {
        worldDimensionX = Mathf.Max(1, worldDimensionX);
        worldDimensionY = Mathf.Max(1, worldDimensionY);
        worldDimensionZ = Mathf.Max(1, worldDimensionZ);
        chunkSizeXZ = Mathf.Max(1, chunkSizeXZ);
        chunkSizeY = Mathf.Max(1, chunkSizeY);
        totalWorldChunks = worldDimensionX * worldDimensionY * worldDimensionZ;

        #if UNITY_EDITOR
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
        chunks_1D = new McChunk[totalWorldChunks];
    }
    
    public McChunk InstantiateAndConfigureChunk(int array_cx, int array_cy, int array_cz, int columnsPerDataGenStep, int voxelsPerMeshStep, int voxelsPerTerrainStep)
    {
        // With chunkOffsetY = 0, centered_dy is now the same as the array_cy.
        int centered_dx = array_cx - chunkOffsetX;
        int centered_dy = array_cy - chunkOffsetY; 
        int centered_dz = array_cz - chunkOffsetZ;

        int chunk1DIndex = ChunkCenteredCoordsTo1D(centered_dx, centered_dy, centered_dz);
        if (chunk1DIndex == -1 || chunks_1D[chunk1DIndex] != null) return null;
        
        GameObject newChunkGO = Instantiate(chunkPrefab);
        newChunkGO.name = $"Chunk_({centered_dx},{centered_dy},{centered_dz})";
        newChunkGO.transform.SetParent(this.transform, false);
        newChunkGO.transform.localPosition = new Vector3(centered_dx * chunkSizeXZ, centered_dy * chunkSizeY, centered_dz * chunkSizeXZ);

        McChunk newChunkScript = newChunkGO.GetComponent<McChunk>();
        
        chunks_1D[chunk1DIndex] = newChunkScript;

        newChunkScript.Initialize(this, terrainGenerator, centered_dx, centered_dy, centered_dz, columnsPerDataGenStep, voxelsPerMeshStep, voxelsPerTerrainStep);
        
        newChunkGO.SetActive(true);

        return newChunkScript;
    }
    
    public void RequestChunkMeshUpdate(McChunk chunkToUpdate)
    {
        if (coordinator != null) coordinator.RequestChunkMeshUpdate(chunkToUpdate);
    }
    
    public ushort GetBlock(int globalX, int globalY, int globalZ)
    {
        int centeredChunkX = Mathf.FloorToInt((float)globalX / chunkSizeXZ);
        int chunkY = Mathf.FloorToInt((float)globalY / chunkSizeY); // Y is no longer centered
        int centeredChunkZ = Mathf.FloorToInt((float)globalZ / chunkSizeXZ);
        
        McChunk chunk = GetChunkAt(centeredChunkX, chunkY, centeredChunkZ);
        if (chunk == null) return 0;

        int localX = globalX - chunk.chunkX_world * chunkSizeXZ;
        int localY = globalY - chunk.chunkY_world * chunkSizeY;
        int localZ = globalZ - chunk.chunkZ_world * chunkSizeXZ;
        
        return chunk.GetBlockLocal(localX, localY, localZ);
    }

    public ushort GetBlockInChunk(McChunk chunk, int localX, int localY, int localZ, out byte blockType)
    {
        ushort blockData = chunk.GetBlockLocal(localX, localY, localZ);
        blockType = (byte)(blockData & 0xFF);
        return blockData;
    }
    
    public void SetBlock(int globalX, int globalY, int globalZ, byte blockType)
    {
        int centeredChunkX = Mathf.FloorToInt((float)globalX / chunkSizeXZ);
        int chunkY = Mathf.FloorToInt((float)globalY / chunkSizeY); // Y is no longer centered
        int centeredChunkZ = Mathf.FloorToInt((float)globalZ / chunkSizeXZ);
        
        McChunk chunk = GetChunkAt(centeredChunkX, chunkY, centeredChunkZ);
        if (chunk == null) return;

        int localX = globalX - chunk.chunkX_world * chunkSizeXZ;
        int localY = globalY - chunk.chunkY_world * chunkSizeY;
        int localZ = globalZ - chunk.chunkZ_world * chunkSizeXZ;

        chunk.SetBlockLocal(localX, localY, localZ, blockType);
    }

    public void SetBlockInChunk(McChunk chunk, int localX, int localY, int localZ, byte blockType, bool updateMesh = true)
    {
        chunk.SetBlockLocal(localX, localY, localZ, blockType, updateMesh);
    }
    
    private void TriggerNeighborUpdate(int centeredCX, int cY, int centeredCZ)
    {
        McChunk neighborChunk = GetChunkAt(centeredCX, cY, centeredCZ);
        if (neighborChunk != null) RequestChunkMeshUpdate(neighborChunk);
    }
    
    public ushort PackBlockData(byte blockType)
    {
        if (blockTypeManager == null) return blockType;
        
        ushort packedData = blockType;
        bool isSolid = blockTypeManager.GetBlockIsSolid(blockType);
        BlockVisibilityType visibility = blockTypeManager.GetBlockVisibilityType(blockType);
        
        // Bits 0-7: Block ID (8 bits)
        // Bit 8: IsSolid (1 bit)
        // Bits 9-10: VisibilityType (2 bits)
        // Bits 11-15: Reserved
        
        if (isSolid) packedData |= (1 << 8);
        packedData |= (ushort)((int)visibility << 9);
        
        return packedData;
    }

    public void TriggerNeighborMeshRebuilds(McChunk chunk)
    {
        for (int i = 0; i < 6; i++) {
            TriggerNeighborUpdate(
                chunk.chunkX_world + neighbor_dx_offsets[i],
                chunk.chunkY_world + neighbor_dy_offsets[i],
                chunk.chunkZ_world + neighbor_dz_offsets[i]
            );
        }
    }
    
    public McChunk GetChunkAt(int centered_cx, int cy, int centered_cz)
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

        #if UNITY_EDITOR
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

    public bool AreAllNeighborsReady(McChunk chunk)
    {
        if (chunk == null) return false;
        
        for (int i = 0; i < 6; i++)
        {
            int n_cx = chunk.chunkX_world + neighbor_dx_offsets[i];
            int n_cy = chunk.chunkY_world + neighbor_dy_offsets[i];
            int n_cz = chunk.chunkZ_world + neighbor_dz_offsets[i];

            int neighbor_1D_index = ChunkCenteredCoordsTo1D(n_cx, n_cy, n_cz);
            if (neighbor_1D_index == -1) continue; 

            McChunk neighborChunk = GetChunkAt(n_cx, n_cy, n_cz);

            if (neighborChunk != null && !neighborChunk.isDataReady) return false;
        }
        return true;
    }

    public bool IsChunkSingleOpaqueSolid(int centered_cx, int cy, int centered_cz)
    {
        McChunk chunk = GetChunkAt(centered_cx, cy, centered_cz);
        if (chunk == null) return false; 
        return chunk.isSingleOpaqueSolid;
    }
}
