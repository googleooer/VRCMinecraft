﻿using UdonSharp;
using UnityEngine;
using VRC.SDKBase; 
using VRRefAssist; 

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
// Uses McTerrainGeneratorEditor.cs for the inspector
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
    [Tooltip("List of all structure and feature template prefabs (GameObjects with McStructureTemplate script and baked data).")]
    public McStructureTemplate[] structureTemplates; 

    [SerializeField, FindObjectOfType(true)] 
    private McWorld world; 
    
    [SerializeField, FindObjectOfType(true)] 
    private McBlockTypeManager blockTypeManager; // Not directly used in V3.1 stamping, but good to have

    private float perlinSeedOffsetX_terrain;
    private float perlinSeedOffsetZ_terrain;
    private bool isInitialized = false;
    private int _worldActualSeed; // To store world seed for deterministic Random.InitState

    // Custom PRNG state for structure placement
    private uint _placementRandState;

    // Custom PRNG: Initialize state
    private void InitPlacementRand(int seed)
    {
        // Ensure the seed is positive before casting to uint to avoid System.Convert overflow
        // Using Abs ensures a non-negative value. The bit pattern is what matters for the PRNG start.
        _placementRandState = (uint)Mathf.Abs(seed);
        // It's also good to ensure _placementRandState is not zero if the LCG behaves poorly with a zero state.
        // A common LCG issue: if state becomes 0, it might stay 0 if c (the additive constant) is 0.
        // Our LCG has a non-zero c (1013904223u), so 0 state is fine but can be avoided for robustness.
        if (_placementRandState == 0) _placementRandState = 1; // Avoid zero state if Abs(seed) was 0

        for (int i = 0; i < 5; i++) GetPlacementRand(); 
    }

    // Custom PRNG: Get next float [0,1)
    private float GetPlacementRand()
    {
        _placementRandState = _placementRandState * 1664525u + 1013904223u; // LCG parameters (Numerical Recipes)
        return (float)(_placementRandState >> 8) / (float)0x00FFFFFF; // Use upper 24 bits for float, avoid issues with direct uint to float conversion maxing out.
    }
    
    // Custom PRNG: Get int [0, maxExclusive - 1]
    private int GetPlacementRandRange(int maxExclusive)
    {
        if (maxExclusive <= 0) return 0;
        return (int)(GetPlacementRand() * maxExclusive); // Basic range mapping
    }

    public void InitializeGenerator(int seed)
    {
        if(isInitialized) return;
        
        // VRRefAssist should populate world and blockTypeManager.
        // Adding checks here for robustness.
        if (world == null) {
            Debug.LogError("[McTerrainGenerator] McWorld instance NOT FOUND! Cannot initialize."); return;
        }
        if (blockTypeManager == null) {
            Debug.LogError("[McTerrainGenerator] McBlockTypeManager instance NOT FOUND! Cannot initialize."); return;
        }

        _worldActualSeed = seed; // Store the main world seed
        perlinSeedOffsetX_terrain = (seed % 1000) * 1.23f + 10000.0f;
        perlinSeedOffsetZ_terrain = ((seed / 1000) % 1000) * 1.45f + 20000.0f;
        isInitialized = true;
    }

    public int GetBaseTerrainHeight(int worldX_voxel, int worldZ_voxel) 
    {
        if (world == null) return baseTerrainHeight; // Fallback if world not initialized
        if (Mathf.Approximately(baseNoiseScale, 0f)) return baseTerrainHeight; 

        float inputX = ((float)worldX_voxel + perlinSeedOffsetX_terrain) / baseNoiseScale;
        float inputZ = ((float)worldZ_voxel + perlinSeedOffsetZ_terrain) / baseNoiseScale;
        float perlinValue = Mathf.PerlinNoise(inputX, inputZ); 
        perlinValue = Mathf.Clamp01(perlinValue);
        
        int calculatedHeight = baseTerrainHeight + Mathf.FloorToInt(perlinValue * baseHeightVariationAmplitude + perlinHeightOffset);
        
        int minYVoxel = -world.globalVoxelOffsetY;
        int maxYVoxel = world.globalVoxelOffsetY - 1;
        return Mathf.Clamp(calculatedHeight, minYVoxel, maxYVoxel);
    }

    public void PlaceFeaturesInChunk(int chunkArrayX, int chunkArrayY, int chunkArrayZ)
    {
        if (!isInitialized || world == null || structureTemplates == null || structureTemplates.Length == 0) return;

        int centeredChunkX = chunkArrayX - world.chunkOffsetX; 
        int centeredChunkY = chunkArrayY - world.chunkOffsetY;
        int centeredChunkZ = chunkArrayZ - world.chunkOffsetZ;

        int chunkOriginGlobalX = centeredChunkX * world.chunkSizeXZ;
        int chunkOriginGlobalY = centeredChunkY * world.chunkSizeY;
        int chunkOriginGlobalZ = centeredChunkZ * world.chunkSizeXZ;
        
        foreach (McStructureTemplate structureTemplate in structureTemplates)
        {
            if (structureTemplate == null) continue;

            int combinedSeed = _worldActualSeed + chunkArrayX * 7883 + chunkArrayY * 1471 + chunkArrayZ * 3463 + structureTemplate.placementSalt;
            InitPlacementRand(combinedSeed); // Initialize our custom PRNG

            if (GetPlacementRand() <= structureTemplate.spawnChance) // Use custom PRNG
            {
                int placementAttempts = 3; 
                for (int attempt = 0; attempt < placementAttempts; attempt++) {
                    int localX = GetPlacementRandRange(world.chunkSizeXZ); // Use custom PRNG
                    int localZ = GetPlacementRandRange(world.chunkSizeXZ); // Use custom PRNG
                    int spawnOriginGlobalX = chunkOriginGlobalX + localX;
                    int spawnOriginGlobalZ = chunkOriginGlobalZ + localZ;

                    int surfaceY = -world.globalVoxelOffsetY -1; 
                    // Scan from top of current chunk down to find surface (simplified from original, ensure it works)
                    for(int yInChunkScan = world.chunkSizeY -1; yInChunkScan >=0; --yInChunkScan) {
                        int currentScanGlobalY = chunkOriginGlobalY + yInChunkScan;
                         if(world.GetBlock(spawnOriginGlobalX, currentScanGlobalY, spawnOriginGlobalZ) != 0) { // Found non-air block
                            surfaceY = currentScanGlobalY;
                            break;
                        }
                    }

                    if (surfaceY < -world.globalVoxelOffsetY) continue; // No ground found in this column for this attempt

                    // Check requiredSpawnBlockID
                    if (structureTemplate.requiredSpawnBlockID != -1) {
                        byte blockAtSurface = world.GetBlock(spawnOriginGlobalX, surfaceY, spawnOriginGlobalZ);
                        if (blockAtSurface != (byte)structureTemplate.requiredSpawnBlockID) {
                            continue; // Did not match required block, try next attempt or template
                        }
                    }

                    int spawnOriginGlobalY = surfaceY + 1; 

                    if (spawnOriginGlobalY < structureTemplate.minYSpawnLevel || spawnOriginGlobalY > structureTemplate.maxYSpawnLevel) continue;
                    if (structureTemplate.requiresSolidGround) {
                        bool groundSolidEnough = true;
                        for (int d = 0; d < structureTemplate.solidGroundDepth; d++) {
                            if (world.GetBlock(spawnOriginGlobalX, surfaceY - d, spawnOriginGlobalZ) == 0) {
                                groundSolidEnough = false; break;
                            }
                        }
                        if (!groundSolidEnough) continue;
                    }
                    
                    StampStructure(structureTemplate, spawnOriginGlobalX, spawnOriginGlobalY, spawnOriginGlobalZ);
                    break; // Structure placed, move to next template
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
            // It's normal for some structures to have no baked data if not processed yet, or if empty by design.
            // Only log warning if it was expected to have data.
            // Debug.LogWarning($"[McTerrainGenerator] Attempted to stamp structure '{structureTemplate.structureName}' but its baked data is missing, empty, or mismatched. Ensure it's baked correctly via its Inspector.", structureTemplate.gameObject);
            return;
        }

        // Debug.Log($"[McTerrainGenerator] Stamping '{structureTemplate.structureName}' with {positions.Length} voxels at G({originGlobalX},{originGlobalY},{originGlobalZ})");

        for (int i = 0; i < positions.Length; i++)
        {
            Vector3Int relativePos = positions[i];
            byte blockID = blockIDs[i];

            int placeGlobalX = originGlobalX + relativePos.x;
            int placeGlobalY = originGlobalY + relativePos.y;
            int placeGlobalZ = originGlobalZ + relativePos.z;
            
            world.SetBlock(placeGlobalX, placeGlobalY, placeGlobalZ, blockID);
        }
    }
}