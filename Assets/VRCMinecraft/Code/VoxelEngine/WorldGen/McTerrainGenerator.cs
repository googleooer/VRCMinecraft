using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRRefAssist;
using System.Text; // Added for StringBuilder

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McTerrainGenerator : UdonSharpBehaviour
{
    [Header("Global Terrain Noise Settings")]
    public float baseNoiseScale = 70.0f;
    public int baseTerrainHeight = 0;
    public float baseHeightVariationAmplitude = 15f;
    public float perlinHeightOffset = 0f;

    [Header("Terrain Composition")]
    public int seaLevel = -5;
    public byte grassBlockID = 2;
    public byte stoneBlockID = 1;
    public byte dirtBlockID = 3;
    public byte waterBlockID = 4;

    [Header("Structure & Feature Templates")]
    public McStructureTemplate[] structureTemplates;

    [SerializeField, FindObjectOfType(true)]
    private McWorld world;

    [SerializeField, FindObjectOfType(true)]
    private McBlockTypeManager blockTypeManager;

    private float perlinSeedOffsetX_terrain;
    private float perlinSeedOffsetZ_terrain;
    private bool isInitialized = false;
    private int _worldActualSeed;

    private uint _placementRandState;









    private float _inverseBaseNoiseScale;
    private bool _isBaseNoiseScaleEffectivelyZero;
    private int _cachedMinYVoxel;
    private int _cachedMaxYVoxel;
    private bool _worldParametersCached = false;



    // Logging
    #if UNITY_EDITOR
    [HideInInspector] public bool enableVerboseLogging = true; // Can be set by McWorld or manually
    #endif
    private StringBuilder logBuilder;


    private void InitPlacementRand(int seed)
    {
        _placementRandState = (uint)Mathf.Abs(seed);
        if (_placementRandState == 0) _placementRandState = 1;
        for (int i = 0; i < 5; i++) GetPlacementRand();
    }

    private float GetPlacementRand()
    {
        _placementRandState = _placementRandState * 1664525u + 1013904223u;
        return (float)(_placementRandState >> 8) / (float)0x00FFFFFF;
    }

    private int GetPlacementRandRange(int maxExclusive)
    {
        if (maxExclusive <= 0) return 0;
        return (int)(GetPlacementRand() * maxExclusive);
    }

    public void InitializeGenerator(int seed)
    {
        float startTime = Time.realtimeSinceStartup;
        if (isInitialized) return;

        logBuilder = new StringBuilder(256); // Initialize StringBuilder

        UpdateTerrainGenParameters();
        
        // Inherit logging state from world if possible
        // if (world != null) enableVerboseLogging = world.enableVerboseLogging;


        _worldActualSeed = seed;
        perlinSeedOffsetX_terrain = (seed % 1000) * 1.23f + 10000.0f;
        perlinSeedOffsetZ_terrain = ((seed / 1000) % 1000) * 1.45f + 20000.0f;
        isInitialized = true;

#if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            logBuilder.Clear();
            logBuilder.AppendFormat("[McTerrainGenerator.InitializeGenerator] Complete. Seed: {0}. Time: {1:F2} ms.", seed, (Time.realtimeSinceStartup - startTime) * 1000f);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }

    // Call this method in Start() or OnEnable(), and if baseNoiseScale or world.globalVoxelOffsetY can change at runtime,
    // call it again after they change.
    public void UpdateTerrainGenParameters()
    {
        if (Mathf.Approximately(baseNoiseScale, 0f) || baseNoiseScale == 0f) // More robust check for zero
        {
            _isBaseNoiseScaleEffectivelyZero = true;
            _inverseBaseNoiseScale = 0f; // Or some other safe default if you prefer not to branch
        }
        else
        {
            _isBaseNoiseScaleEffectivelyZero = false;
            _inverseBaseNoiseScale = 1.0f / baseNoiseScale;
        }

        if (world != null)
        {
            _cachedMinYVoxel = -world.globalVoxelOffsetY;
            _cachedMaxYVoxel = world.globalVoxelOffsetY - 1;
            _worldParametersCached = true;
        } else {
            _worldParametersCached = false; // Mark as not cached if world is null
        }
    }

    public int GetBaseTerrainHeight(int worldX_voxel, int worldZ_voxel)
    {
        // Ensure parameters are cached. This check might be skippable if UpdateTerrainGenParameters()
        // is guaranteed to be called before any GetBaseTerrainHeight() calls.
        // if (!_worldParametersCached && world != null) UpdateTerrainGenParameters();


        //if (world == null || !_worldParametersCached) return baseTerrainHeight; // Return base if world is null or params not cached
        if (_isBaseNoiseScaleEffectivelyZero) return baseTerrainHeight;

        // Use multiplication instead of division
        float inputX = ((float)worldX_voxel + perlinSeedOffsetX_terrain) * _inverseBaseNoiseScale;
        float inputZ = ((float)worldZ_voxel + perlinSeedOffsetZ_terrain) * _inverseBaseNoiseScale;

        float perlinValue = Mathf.PerlinNoise(inputX, inputZ);

        // Mathf.PerlinNoise in Unity (and likely UdonSharp) typically returns values in the [0,1] range.
        // If this is guaranteed, Mathf.Clamp01(perlinValue) is redundant and can be removed.
        // Verify this behavior in your VRChat/UdonSharp environment.
        // perlinValue = Mathf.Clamp01(perlinValue); // Potentially remove this line

        // Calculation remains similar, but uses pre-calculated min/max Y
        int calculatedHeight = baseTerrainHeight + Mathf.FloorToInt(perlinValue * baseHeightVariationAmplitude + perlinHeightOffset);

        return Mathf.Clamp(calculatedHeight, _cachedMinYVoxel, _cachedMaxYVoxel);
    }

    public void PlaceFeaturesInChunk(int chunkArrayX, int chunkArrayY, int chunkArrayZ)
    {
        float startTime = Time.realtimeSinceStartup;
        if (!isInitialized || world == null || structureTemplates == null || structureTemplates.Length == 0) return;

        int centeredChunkX = chunkArrayX - world.chunkOffsetX;
        int centeredChunkY = chunkArrayY - world.chunkOffsetY;
        int centeredChunkZ = chunkArrayZ - world.chunkOffsetZ;

        int chunkOriginGlobalX = centeredChunkX * world.chunkSizeXZ;
        int chunkOriginGlobalY = centeredChunkY * world.chunkSizeY;
        int chunkOriginGlobalZ = centeredChunkZ * world.chunkSizeXZ;
        
        int structuresPlacedThisChunk = 0;

        foreach (McStructureTemplate structureTemplate in structureTemplates)
        {
            if (structureTemplate == null) continue;

            int combinedSeed = _worldActualSeed + chunkArrayX * 7883 + chunkArrayY * 1471 + chunkArrayZ * 3463 + structureTemplate.placementSalt;
            InitPlacementRand(combinedSeed);

            if (GetPlacementRand() <= structureTemplate.spawnChance)
            {
                int placementAttempts = 3;
                for (int attempt = 0; attempt < placementAttempts; attempt++)
                {
                    int localX = GetPlacementRandRange(world.chunkSizeXZ);
                    int localZ = GetPlacementRandRange(world.chunkSizeXZ);
                    int spawnOriginGlobalX = chunkOriginGlobalX + localX;
                    int spawnOriginGlobalZ = chunkOriginGlobalZ + localZ;
                    int surfaceY = -world.globalVoxelOffsetY - 1;

                    for (int yInChunkScan = world.chunkSizeY - 1; yInChunkScan >= 0; --yInChunkScan)
                    {
                        int currentScanGlobalY = chunkOriginGlobalY + yInChunkScan;
                        if (world.GetBlock(spawnOriginGlobalX, currentScanGlobalY, spawnOriginGlobalZ) != 0)
                        {
                            surfaceY = currentScanGlobalY;
                            break;
                        }
                    }
                    if (surfaceY < -world.globalVoxelOffsetY) continue;

                    if (structureTemplate.requiredSpawnBlockID != -1)
                    {
                        byte blockAtSurface = (byte)(world.GetBlock(spawnOriginGlobalX, surfaceY, spawnOriginGlobalZ) & 0xFF);
                        if (blockAtSurface != (byte)structureTemplate.requiredSpawnBlockID) continue;
                    }

                    int spawnOriginGlobalY = surfaceY + 1;
                    if (spawnOriginGlobalY < structureTemplate.minYSpawnLevel || spawnOriginGlobalY > structureTemplate.maxYSpawnLevel) continue;
                    if (structureTemplate.requiresSolidGround)
                    {
                        bool groundSolidEnough = true;
                        for (int d = 0; d < structureTemplate.solidGroundDepth; d++)
                        {
                            if (world.GetBlock(spawnOriginGlobalX, surfaceY - d, spawnOriginGlobalZ) == 0)
                            {
                                groundSolidEnough = false; break;
                            }
                        }
                        if (!groundSolidEnough) continue;
                    }

                    float stampStartTime = Time.realtimeSinceStartup;
                    StampStructure(structureTemplate, spawnOriginGlobalX, spawnOriginGlobalY, spawnOriginGlobalZ);
                    float stampDuration = (Time.realtimeSinceStartup - stampStartTime) * 1000f;
                    structuresPlacedThisChunk++;
#if UNITY_EDITOR
                    if (enableVerboseLogging)
                    {
                        logBuilder.Clear();
                        logBuilder.AppendFormat("[McTerrainGenerator.PlaceFeaturesInChunk] Stamped '{0}' at G({1},{2},{3}) in chunk Arr({4},{5},{6}). Stamp took {7:F2} ms.",
                            structureTemplate.structureName, spawnOriginGlobalX, spawnOriginGlobalY, spawnOriginGlobalZ,
                            chunkArrayX, chunkArrayY, chunkArrayZ, stampDuration);
                        Debug.Log(logBuilder.ToString());
                    }
#endif
                    break; 
                }
            }
        }
#if UNITY_EDITOR
        if (enableVerboseLogging && structuresPlacedThisChunk > 0)
        {
            logBuilder.Clear();
            logBuilder.AppendFormat("[McTerrainGenerator.PlaceFeaturesInChunk] Finished for chunk Arr({0},{1},{2}). Placed {3} structures. Total time: {4:F2} ms.",
                chunkArrayX, chunkArrayY, chunkArrayZ, structuresPlacedThisChunk, (Time.realtimeSinceStartup - startTime) * 1000f);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }

    private void StampStructure(McStructureTemplate structureTemplate, int originGlobalX, int originGlobalY, int originGlobalZ)
    {
        // Profiling done by caller (PlaceFeaturesInChunk)
        if (structureTemplate == null || world == null) return;

        Vector3Int[] positions = structureTemplate.bakedVoxelPositions;
        byte[] blockIDs = structureTemplate.bakedVoxelBlockIDs;

        if (positions == null || blockIDs == null || positions.Length == 0 || positions.Length != blockIDs.Length)
        {
#if UNITY_EDITOR
            // if (enableVerboseLogging) Debug.LogWarning($"[McTerrainGenerator.StampStructure] Attempted to stamp structure '{structureTemplate.structureName}' but its baked data is missing, empty, or mismatched.");
#endif
            return;
        }

        for (int i = 0; i < positions.Length; i++)
        {
            Vector3Int relativePos = positions[i];
            byte blockID = blockIDs[i];
            int placeGlobalX = originGlobalX + relativePos.x;
            int placeGlobalY = originGlobalY + relativePos.y;
            int placeGlobalZ = originGlobalZ + relativePos.z;
            world.SetBlock(placeGlobalX, placeGlobalY, placeGlobalZ, blockID); // SetBlock in McWorld now has its own logging
        }
        // world.FinalizeBatchedBlockUpdates(); // This method was removed from McWorld
    }
}
