﻿﻿using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRRefAssist;

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McTerrainGenerator : UdonSharpBehaviour
{
    [Header("Global Terrain Noise Settings")]
    public float baseNoiseScale = 70.0f;
    public int baseTerrainHeight = 0;
    public float baseHeightVariationAmplitude = 15f;
    public float perlinHeightOffset = 0f; // Offset added to Perlin result before scaling by amplitude

    [Header("Terrain Composition")]
    public int seaLevel = -5; // Global Y coordinate for sea level
    public byte grassBlockID = 2;
    public byte stoneBlockID = 1;
    public byte dirtBlockID = 3;
    public byte waterBlockID = 4;

    [Header("Structure & Feature Templates")]
    [Tooltip("List of all structure and feature template prefabs (GameObjects with McStructureTemplate script and baked data).")]
    public McStructureTemplate[] structureTemplates;

    [SerializeField, FindObjectOfType(true)]
    private McWorld world;

    [SerializeField, FindObjectOfType(true)]
    private McBlockTypeManager blockTypeManager; // Good to have, though not directly used by this script's core logic now

    // These are now primarily used by McWorld to set parameters for VoxelDataInitializer.shader
    [HideInInspector] public float perlinSeedOffsetX_terrain;
    [HideInInspector] public float perlinSeedOffsetZ_terrain;

    private bool isInitialized = false;
    private int _worldActualSeed;

    // Custom PRNG state for structure placement
    private uint _placementRandState;

    private void InitPlacementRand(int seed)
    {
        _placementRandState = (uint)Mathf.Abs(seed);
        if (_placementRandState == 0) _placementRandState = 1;
        for (int i = 0; i < 5; i++) GetPlacementRand(); // Stir the pot
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
        if(isInitialized) return;

        if (world == null) {
            Debug.LogError("[McTerrainGenerator] McWorld instance NOT FOUND! Cannot initialize."); return;
        }
        // blockTypeManager check can be here if needed for future features

        _worldActualSeed = seed;
        // Calculate Perlin offsets for McWorld to use with the shader
        perlinSeedOffsetX_terrain = (seed % 1000) * 1.23f + 10000.0f;
        perlinSeedOffsetZ_terrain = ((seed / 1000) % 1000) * 1.45f + 20000.0f;
        isInitialized = true;
        Debug.Log($"[McTerrainGenerator] Initialized with seed {seed}. Perlin offsets: X={perlinSeedOffsetX_terrain}, Z={perlinSeedOffsetZ_terrain}");
    }

    // This CPU version is no longer the primary source for terrain height.
    // The VoxelDataInitializer.shader handles initial terrain generation.
    // This can be kept for CPU-side logic or reference if needed.
    public int GetBaseTerrainHeight(int worldX_voxel, int worldZ_voxel)
    {
        if (world == null || !isInitialized) return baseTerrainHeight;
        if (Mathf.Approximately(baseNoiseScale, 0f)) return baseTerrainHeight;

        float inputX = ((float)worldX_voxel + perlinSeedOffsetX_terrain) / baseNoiseScale;
        float inputZ = ((float)worldZ_voxel + perlinSeedOffsetZ_terrain) / baseNoiseScale;
        float perlinValue = Mathf.PerlinNoise(inputX, inputZ); // Unity's Perlin, 0 to 1
        perlinValue = Mathf.Clamp01(perlinValue);

        int calculatedHeight = baseTerrainHeight + Mathf.FloorToInt(perlinValue * baseHeightVariationAmplitude + perlinHeightOffset);

        int minYVoxel = -world.globalVoxelOffsetY;
        int maxYVoxel = world.globalVoxelOffsetY - 1; // Assuming globalVoxelOffsetY is half of total Y voxels
        return Mathf.Clamp(calculatedHeight, minYVoxel, maxYVoxel);
    }

    public void PlaceFeaturesInChunk(int chunkArrayX, int chunkArrayY, int chunkArrayZ)
    {
        if (!isInitialized || world == null || structureTemplates == null || structureTemplates.Length == 0) return;

        int centeredChunkX = chunkArrayX - world.chunkOffsetX;
        int centeredChunkY = chunkArrayY - world.chunkOffsetY;
        int centeredChunkZ = chunkArrayZ - world.chunkOffsetZ;

        int chunkOriginGlobalX = centeredChunkX * world.chunkSizeXZ;
        int chunkOriginGlobalY = centeredChunkY * world.chunkSizeY; // Base Y of the current chunk layer
        int chunkOriginGlobalZ = centeredChunkZ * world.chunkSizeXZ;

        foreach (McStructureTemplate structureTemplate in structureTemplates)
        {
            if (structureTemplate == null) continue;

            // Use a seed unique to this chunk and structure template for consistent placement attempts
            int combinedSeed = _worldActualSeed + chunkArrayX * 7883 + chunkArrayY * 1471 + chunkArrayZ * 3463 + structureTemplate.placementSalt;
            InitPlacementRand(combinedSeed);

            if (GetPlacementRand() <= structureTemplate.spawnChance)
            {
                int placementAttempts = 1;
                for (int attempt = 0; attempt < placementAttempts; attempt++) {
                    int localX = GetPlacementRandRange(world.chunkSizeXZ);
                    int localZ = GetPlacementRandRange(world.chunkSizeXZ);
                    int spawnCandidateGlobalX = chunkOriginGlobalX + localX;
                    int spawnCandidateGlobalZ = chunkOriginGlobalZ + localZ;

                    // Determine surface Y. This now relies on McWorld.GetBlock which is a placeholder.
                    // For accurate structure placement, GetBlock would need to work (e.g., AsyncGPUReadback)
                    // or placement logic needs to be aware of the GPU data.
                    // A simplified approach: use the CPU GetBaseTerrainHeight as an estimate.
                    int estimatedSurfaceY = GetBaseTerrainHeight(spawnCandidateGlobalX, spawnCandidateGlobalZ);

                    // If structure needs to be placed relative to a specific Y layer (e.g. caves)
                    // this logic would need to change. For surface structures:
                    int spawnOriginGlobalY = estimatedSurfaceY + 1;


                    if (spawnOriginGlobalY < structureTemplate.minYSpawnLevel || spawnOriginGlobalY > structureTemplate.maxYSpawnLevel) continue;

                    // RequiredSpawnBlockID check is difficult without reliable GetBlock.
                    // For now, we might have to skip this check or use the estimated surface type.
                    // byte blockAtSurface = world.GetBlock(spawnCandidateGlobalX, estimatedSurfaceY, spawnCandidateGlobalZ);
                    // if (structureTemplate.requiredSpawnBlockID != -1 && blockAtSurface != (byte)structureTemplate.requiredSpawnBlockID) continue;


                    // Solid ground check is also difficult.
                    // if (structureTemplate.requiresSolidGround) { ... }

                    // If all checks pass (many are currently difficult with GPU data without readback):
                    StampStructure(structureTemplate, spawnCandidateGlobalX, spawnOriginGlobalY, spawnCandidateGlobalZ);
                    break; // Structure placed, move to next template if only one per chunk
                }
            }
        }
    }

    private void StampStructure(McStructureTemplate structureTemplate, int originGlobalX, int originGlobalY, int originGlobalZ)
    {
        if (structureTemplate == null || world == null) return;

        Vector3Int[] positions = structureTemplate.bakedVoxelPositions;
        byte[] blockIDs = structureTemplate.bakedVoxelBlockIDs;

        if (positions == null || blockIDs == null || positions.Length == 0 || positions.Length != blockIDs.Length) {
            // Debug.LogWarning($"[McTerrainGenerator] Structure '{structureTemplate.structureName}' has no baked data.");
            return;
        }
        
        // Debug.Log($"[McTerrainGenerator] Stamping '{structureTemplate.structureName}' at G({originGlobalX},{originGlobalY},{originGlobalZ})");

        for (int i = 0; i < positions.Length; i++)
        {
            Vector3Int relativePos = positions[i];
            byte blockID = blockIDs[i];

            // Apply rotation if any (not implemented here, baked data is pre-rotated or axis-aligned)
            // Vector3Int rotatedRelativePos = ApplyRotation(relativePos, structureTemplate.rotation);

            int placeGlobalX = originGlobalX + relativePos.x;
            int placeGlobalY = originGlobalY + relativePos.y;
            int placeGlobalZ = originGlobalZ + relativePos.z;

            // world.SetBlock will call world.SetBlockGPU
            world.SetBlock(placeGlobalX, placeGlobalY, placeGlobalZ, blockID);
        }
    }
}