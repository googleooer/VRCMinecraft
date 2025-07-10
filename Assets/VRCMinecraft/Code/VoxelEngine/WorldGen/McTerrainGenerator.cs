using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;
using VRRefAssist;
using System.Text;
using VRC.SDK3.Rendering;
using VRC.Udon.Common.Interfaces;
using UnityEngine.Rendering;
using Unity.Collections;
using System;
using Random = UnityEngine.Random;

/// <summary>
/// Updated generation states for a fully GPU-driven pipeline.
/// The CPU no longer fills blocks or applies surfaces; it just orchestrates
/// the GPU and applies post-processing like caves.
/// </summary>
public enum GenerationState
{
    Idle,
    Preparing,
    Prepare_NoiseGen3D_1,
    Prepare_NoiseGen3D_2,
    Prepare_NoiseGen3D_3,
    Prepare_CombineNoise,
    GeneratingTerrain,
    ReplacingBiomeBlocks,
    Complete
}

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McTerrainGenerator : UdonSharpBehaviour
{

    [Header("Terrain Composition")]
    public byte airBlockID = 0;
    public byte grassBlockID = 2;
    public byte stoneBlockID = 1;
    public byte dirtBlockID = 3;
    public byte waterBlockID = 9;
    public byte bedrockBlockID = 7;
    public byte sandBlockID = 12;
    public byte sandStoneBlockID = 24;

    [Header("Beta 1.7.3 Terrain Parameters")]
    // Debugging offsets for chunk generation.
    public int chunkOffsetX = 0;
    public int chunkOffsetZ = 0;
    [Range(0.1f, 2.0f)] public float terrainHeightMultiplier = 1.0f;
    [Range(0.0f, 1.0f)] public float caveFrequency = 0.14f;
    public bool generateCaves = true;
    public bool generateBedrock = true;

    [Header("Structure & Feature Templates")]
    public McStructureTemplate[] structureTemplates;

#if UNITY_EDITOR
    [Header("Debugging")]
    public bool enableVerboseLogging = true;
    private float time_Total, time_Preparation, time_GeneratingTerrain, time_ReplacingBiomes;
#endif

    [SerializeField, FindObjectOfType(true)]
    private McWorld world;
    private WorldChunkManagerOld wcm;

    [SerializeField, FindObjectOfType(true)]
    private McCoordinator coordinator;

    [SerializeField, FindObjectOfType(true)]
    private McBlockTypeManager blockTypeManager;

    // The random state for the generator, based on the seed
    private JavaRandom rand;

    private NoiseGeneratorOctaves3D noiseGen1;
    private NoiseGeneratorOctaves3D noiseGen2;
    private NoiseGeneratorOctaves3D noiseGen3;
    private NoiseGeneratorOctaves3D noiseGen4;
    private NoiseGeneratorOctaves3D noiseGen5;
    private NoiseGeneratorOctaves3D noiseGen6;
    private NoiseGeneratorOctaves3D noiseGen7;
    private NoiseGeneratorOctaves3D treeNoise;

    private double[] sandNoise;
    private double[] gravelNoise;
    private double[] stoneNoise;
    private WorldGenCavesOld caves;
    private double[] noise3;
    private double[] noise1;
    private double[] noise2;
    private double[] noise6;
    private double[] noise7;


    private bool isInitialized = false;
    
    // Time-slicing state
    private GenerationState currentState = GenerationState.Idle;
    private int currentChunkX, currentChunkY, currentChunkZ;
    private ushort[] workingChunkData;
    private BetaBiomeEnum[] currentChunkBiomes;
    
    // Cache for preserving surface generation state across vertical chunks
    private int[] columnDepthCache;
    private int cacheCoordX = int.MaxValue;
    private int cacheCoordZ = int.MaxValue;
    
    // Cache for the main 3D noise field for a column
    private double[] noiseCache;

    // Time-slicing progress trackers for the preparation state
    private int noiseCombine_x;
    private int noiseCombine_k1;
    private int noiseCombine_l1;

    // Time-slicing progress trackers
    private int terrain_xPiece, terrain_zPiece;
    private int biome_x;

#if UNITY_EDITOR
    private StringBuilder logBuilder;
#endif

    public void init(int seed)
    {
        float startTime = Time.realtimeSinceStartup;
        if (isInitialized) return;

        wcm = new WorldChunkManagerOld(seed);
#if UNITY_EDITOR
        logBuilder = new StringBuilder(256);
#endif
        
        // Initialize depth cache
        if (world != null) {
            columnDepthCache = new int[world.chunkSizeXZ * world.chunkSizeXZ];
        } else {
            // Fallback, though world should always be available.
            columnDepthCache = new int[16 * 16];
        }

        rand = new JavaRandom(seed);
        noiseGen1 = new NoiseGeneratorOctaves3D(rand, 16);
        noiseGen2 = new NoiseGeneratorOctaves3D(rand, 16);
        noiseGen3 = new NoiseGeneratorOctaves3D(rand, 8);
        noiseGen4 = new NoiseGeneratorOctaves3D(rand, 4);
        noiseGen5 = new NoiseGeneratorOctaves3D(rand, 4);
        noiseGen6 = new NoiseGeneratorOctaves3D(rand, 10);
        noiseGen7 = new NoiseGeneratorOctaves3D(rand, 16);
        treeNoise = new NoiseGeneratorOctaves3D(rand, 8);

        caves = new WorldGenCavesOld();
        
        isInitialized = true;

#if UNITY_EDITOR
        if (enableVerboseLogging) {
            logBuilder.Clear();
            logBuilder.AppendFormat("[McTerrainGenerator.InitializeGenerator] Complete. Seed: {0}. Time: {1:F2} ms.", seed, (Time.realtimeSinceStartup - startTime) * 1000f);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }

    public void StartChunkGeneration(int chunkX, int chunkY, int chunkZ)
    {
        if (!isInitialized)
        {
            Debug.LogError("[McTerrainGenerator] Not initialized! Call init() first.");
            return;
        }

        currentChunkX = chunkX + (chunkOffsetX / world.chunkSizeXZ);
        currentChunkY = chunkY;
        currentChunkZ = chunkZ + (chunkOffsetZ / world.chunkSizeXZ);
        currentState = GenerationState.Preparing;

        // Reset the depth cache for each column if we are at the top of the world
        if (currentChunkY == world.worldDimensionY - 1)
        {
            for (int i = 0; i < columnDepthCache.Length; i++)
            {
                columnDepthCache[i] = -1;
            }
        }

        int chunkSize = world.chunkSizeXZ * world.chunkSizeY * world.chunkSizeXZ;
        if (workingChunkData == null || workingChunkData.Length != chunkSize)
        {
            workingChunkData = new ushort[chunkSize];
        }
        else
        {
            System.Array.Clear(workingChunkData, 0, workingChunkData.Length);
        }

        terrain_xPiece = 0;
        terrain_zPiece = 0;
        biome_x = 0;

#if UNITY_EDITOR
        time_Total = Time.realtimeSinceStartup;
        time_Preparation = 0f;
        time_GeneratingTerrain = 0f;
        time_ReplacingBiomes = 0f;
#endif
    }
    
    public bool StepChunkGeneration(out ushort[] completedData)
    {
        completedData = null;

#if UNITY_EDITOR
        float stepTimer = 0f;
        if (enableVerboseLogging) stepTimer = Time.realtimeSinceStartup;
#endif

        switch (currentState)
        {
            case GenerationState.Preparing:
#if UNITY_EDITOR
                if (enableVerboseLogging)
                {
                    logBuilder.Clear();
                    logBuilder.Append("[McTerrainGenerator.Step] State: Preparing for chunk (").Append(currentChunkX).Append(",").Append(currentChunkY).Append(",").Append(currentChunkZ).Append(")");
                    Debug.Log(logBuilder.ToString());
                }
#endif
                if (currentChunkX != cacheCoordX || currentChunkZ != cacheCoordZ)
                {
                    // New column, start the multi-step preparation
#if UNITY_EDITOR
                    if (enableVerboseLogging) Debug.Log($"[McTerrainGenerator] New column ({currentChunkX}, {currentChunkZ}), starting sliced preparation.");
#endif

                    cacheCoordX = currentChunkX;
                    cacheCoordZ = currentChunkZ;

                    currentChunkBiomes = wcm.getBiomeBlock(currentChunkBiomes, currentChunkX * 16, currentChunkZ * 16, 16, 16);
                    this.sandNoise = this.noiseGen4.generateNoiseOctaves(this.sandNoise, currentChunkX * 16, currentChunkZ * 16, 0.0D, 16, 16, 1, 0.03125D, 0.03125D, 1.0D);
                    this.gravelNoise = this.noiseGen4.generateNoiseOctaves(this.gravelNoise, currentChunkX * 16, 109.0134D, currentChunkZ * 16, 16, 1, 16, 0.03125D, 1.0D, 0.03125D);
                    this.stoneNoise = this.noiseGen5.generateNoiseOctaves(this.stoneNoise, currentChunkX * 16, currentChunkZ * 16, 0.0D, 16, 16, 1, 0.0625D, 0.0625D, 0.0625D);

                    byte byte0 = 4;
                    int xSize = byte0 + 1;
                    byte ySize = (byte)(world.worldDimensionY * world.chunkSizeY / 8 + 1);
                    int zSize = byte0 + 1;
                    if (noiseCache == null || noiseCache.Length != xSize * ySize * zSize)
                    {
                        noiseCache = new double[xSize * ySize * zSize];
                    }

                    currentState = GenerationState.Prepare_NoiseGen3D_1;
                }
                else
                {
                    // Cached column, proceed as before
#if UNITY_EDITOR
                    if (enableVerboseLogging) Debug.Log($"[McTerrainGenerator] Cached column ({currentChunkX}, {currentChunkZ}), skipping preparation.");
#endif
                    initRand(currentChunkX, currentChunkZ);
                    currentState = GenerationState.GeneratingTerrain;
                }
#if UNITY_EDITOR
                if (enableVerboseLogging) time_Preparation += (Time.realtimeSinceStartup - stepTimer) * 1000f;
#endif
                return false;

            case GenerationState.Prepare_NoiseGen3D_1:
                {
                    byte byte0 = 4;
                    int xSize = byte0 + 1;
                    byte ySize = (byte)(world.worldDimensionY * world.chunkSizeY / 8 + 1);
                    int zSize = byte0 + 1;
                    double d0 = 684.412D;
                    double d1 = 684.412D;
                    noise1 = noiseGen1.generateNoiseOctaves(noise1, currentChunkX * byte0, 0, currentChunkZ * byte0, xSize, ySize, zSize, d0, d1, d0);
                    currentState = GenerationState.Prepare_NoiseGen3D_2;
                    return false;
                }

            case GenerationState.Prepare_NoiseGen3D_2:
                {
                    byte byte0 = 4;
                    int xSize = byte0 + 1;
                    byte ySize = (byte)(world.worldDimensionY * world.chunkSizeY / 8 + 1);
                    int zSize = byte0 + 1;
                    double d0 = 684.412D;
                    double d1 = 684.412D;
                    noise2 = noiseGen2.generateNoiseOctaves(noise2, currentChunkX * byte0, 0, currentChunkZ * byte0, xSize, ySize, zSize, d0, d1, d0);
                    currentState = GenerationState.Prepare_NoiseGen3D_3;
                    return false;
                }

            case GenerationState.Prepare_NoiseGen3D_3:
                {
                    byte byte0 = 4;
                    int xSize = byte0 + 1;
                    byte ySize = (byte)(world.worldDimensionY * world.chunkSizeY / 8 + 1);
                    int zSize = byte0 + 1;
                    double d0 = 684.412D;
                    double d1 = 684.412D;
                    noise3 = noiseGen3.generateNoiseOctaves(noise3, currentChunkX * byte0, 0, currentChunkZ * byte0, xSize, ySize, zSize, d0 / 80.0D, d1 / 160.0D, d0 / 80.0D);
                    noise6 = noiseGen6.generateNoiseArray(noise6, currentChunkX * byte0, currentChunkZ * byte0, xSize, zSize, 1.121D, 1.121D, 0.5D);
                    noise7 = noiseGen7.generateNoiseArray(noise7, currentChunkX * byte0, currentChunkZ * byte0, xSize, zSize, 200.0D, 200.0D, 0.5D);

                    noiseCombine_x = 0;
                    noiseCombine_k1 = 0;
                    noiseCombine_l1 = 0;
                    currentState = GenerationState.Prepare_CombineNoise;
                    return false;
                }

            case GenerationState.Prepare_CombineNoise:
                {
                    byte byte0 = 4;
                    int xSize = byte0 + 1;
                    byte ySize = (byte)(world.worldDimensionY * world.chunkSizeY / 8 + 1);
                    int zSize = byte0 + 1;
                    int i2 = 16 / xSize;
                    double[] temp = this.wcm.temperatures;
                    double[] rain = this.wcm.rainfall;

                    int x = noiseCombine_x;
                    int k2 = x * i2 + i2 / 2;

                    for (int z = 0; z < zSize; z++)
                    {
                        int i3 = z * i2 + i2 / 2;
                        double d2 = temp[k2 * 16 + i3];
                        double d3 = rain[k2 * 16 + i3] * d2;
                        double d4 = 1.0D - d3;
                        d4 *= d4;
                        d4 *= d4;
                        d4 = 1.0D - d4;
                        double d5 = (noise6[noiseCombine_l1] + 256.0D) / 512.0D;
                        d5 *= d4;
                        if (d5 > 1.0D) d5 = 1.0D;
                        
                        double d6 = noise7[noiseCombine_l1] / 8000.0D;
                        if (d6 < 0.0D) d6 = -d6 * 0.3D;
                        
                        d6 = d6 * 3.0D - 2.0D;
                        if (d6 < 0.0D)
                        {
                            d6 /= 2D;
                            if (d6 < -1D) d6 = -1D;
                            d6 /= 1.4D;
                            d6 /= 2.0D;
                            d5 = 0.0D;
                        }
                        else
                        {
                            if (d6 > 1.0D) d6 = 1.0D;
                            d6 /= 8.0D;
                        }

                        if (d5 < 0.0D) d5 = 0.0D;
                        
                        d5 += 0.5D;
                        d6 = (d6 * (double)ySize) / 16.0D;
                        double d7 = (double)ySize / 2.0D + d6 * 4.0D;

                        for (int y = 0; y < ySize; y++)
                        {
                            double d8 = 0.0D;
                            double d9 = (((double)y - d7) * 12D) / d5;
                            if (d9 < 0.0D) d9 *= 4.0D;
                            
                            double d10 = noise1[noiseCombine_k1] / 512.0D;
                            double d11 = noise2[noiseCombine_k1] / 512.0D;
                            double d12 = (this.noise3[noiseCombine_k1] / 10.0D + 1.0D) / 2.0D;

                            if (d12 < 0.0D) d8 = d10;
                            else if (d12 > 1.0D) d8 = d11;
                            else d8 = d10 + (d11 - d10) * d12;
                            
                            d8 -= d9;
                            if (y > ySize - 4)
                            {
                                double d13 = (double)((float)(y - (ySize - 4)) / 3.0F);
                                d8 = d8 * (1.0D - d13) + -10.0D * d13;
                            }
                            noiseCache[noiseCombine_k1] = d8;
                            noiseCombine_k1++;
                        }
                        noiseCombine_l1++;
                    }

                    noiseCombine_x++;
                    if (noiseCombine_x >= xSize)
                    {
                        noise1 = noise2 = noise3 = noise6 = noise7 = null;
                        initRand(currentChunkX, currentChunkZ);
                        currentState = GenerationState.GeneratingTerrain;
                    }

                    return false;
                }

            case GenerationState.GeneratingTerrain:
#if UNITY_EDITOR
                if (enableVerboseLogging && terrain_xPiece == 0 && terrain_zPiece == 0)
                {
                    logBuilder.Clear();
                    logBuilder.Append("[McTerrainGenerator.Step] State: GeneratingTerrain for chunk (").Append(currentChunkX).Append(",").Append(currentChunkY).Append(",").Append(currentChunkZ).Append(")");
                    Debug.Log(logBuilder.ToString());
                }
#endif
                byte byte0_gt = 4;
                byte oceanHeight_gt = 64;
                int l_gt = byte0_gt + 1;
                byte b2_gt = (byte)(world.worldDimensionY * world.chunkSizeY / 8 + 1);
                int var10 = byte0_gt + 1;
                
                int yPiecesPerChunk = world.chunkSizeY / 8;
                int startYPiece = currentChunkY * yPiecesPerChunk;
                int endYPiece = startYPiece + yPiecesPerChunk;

                for (int i = 0; i < 4; i++) 
                {
                    if (terrain_xPiece >= byte0_gt) break;

                    for (int yPiece = startYPiece; yPiece < endYPiece; yPiece++)
                    {
                        double d_gt = 0.125D;
                        double d1 = noiseCache[((terrain_xPiece + 0) * l_gt + (terrain_zPiece + 0)) * b2_gt + (yPiece + 0)];
                        double d2 = noiseCache[((terrain_xPiece + 0) * l_gt + (terrain_zPiece + 1)) * b2_gt + (yPiece + 0)];
                        double d3 = noiseCache[((terrain_xPiece + 1) * l_gt + (terrain_zPiece + 0)) * b2_gt + (yPiece + 0)];
                        double d4 = noiseCache[((terrain_xPiece + 1) * l_gt + (terrain_zPiece + 1)) * b2_gt + (yPiece + 0)];
                        double d5 = (noiseCache[((terrain_xPiece + 0) * l_gt + (terrain_zPiece + 0)) * b2_gt + (yPiece + 1)] - d1) * d_gt;
                        double d6 = (noiseCache[((terrain_xPiece + 0) * l_gt + (terrain_zPiece + 1)) * b2_gt + (yPiece + 1)] - d2) * d_gt;
                        double d7 = (noiseCache[((terrain_xPiece + 1) * l_gt + (terrain_zPiece + 0)) * b2_gt + (yPiece + 1)] - d3) * d_gt;
                        double d8 = (noiseCache[((terrain_xPiece + 1) * l_gt + (terrain_zPiece + 1)) * b2_gt + (yPiece + 1)] - d4) * d_gt;
                        for (int l1 = 0; l1 < 8; l1++)
                        {
                            double d9 = 0.25D;
                            double d10 = d1;
                            double d11 = d2;
                            double d12 = (d3 - d1) * d9;
                            double d13 = (d4 - d2) * d9;
                            for (int i2 = 0; i2 < 4; i2++)
                            {
                                int xLoc = i2 + terrain_xPiece * 4;
                                int yLoc = yPiece * 8 + l1;
                                double d14 = 0.25D;
                                double d15 = d10;
                                double d16 = (d11 - d10) * d14;
                                for (int k2 = 0; k2 < 4; k2++)
                                {
                                    int currentZ = k2 + terrain_zPiece * 4;
                                    double d17 = wcm.temperatures[(terrain_xPiece * 4 + i2) * 16 + (terrain_zPiece * 4 + k2)];
                                    BlockMaterial block = BlockMaterial.AIR;
                                    if (yPiece * 8 + l1 < oceanHeight_gt)
                                    {
                                        if (d17 < 0.5D && yPiece * 8 + l1 >= oceanHeight_gt - 1) block = BlockMaterial.ICE;
                                        else block = BlockMaterial.STATIONARY_WATER;
                                    }
                                    if (d15 > 0.0D) block = BlockMaterial.STONE;

                                    int localY = yLoc - (currentChunkY * world.chunkSizeY);
                                    if (localY >= 0 && localY < world.chunkSizeY)
                                    {
                                        int index = localY * world.chunkSizeXZ * world.chunkSizeXZ + currentZ * world.chunkSizeXZ + xLoc;
                                        if (index >= 0 && index < workingChunkData.Length)
                                        {
                                            workingChunkData[index] = world.PackBlockData((byte)block);
                                        }
                                    }
                                    d15 += d16;
                                }
                                d10 += d12;
                                d11 += d13;
                            }
                            d1 += d5; d2 += d6; d3 += d7; d4 += d8;
                        }
                    }

                    terrain_zPiece++;
                    if (terrain_zPiece >= byte0_gt)
                    {
                        terrain_zPiece = 0;
                        terrain_xPiece++;
                    }
                }

#if UNITY_EDITOR
                if (enableVerboseLogging) time_GeneratingTerrain += (Time.realtimeSinceStartup - stepTimer) * 1000f;
#endif

                if (terrain_xPiece >= byte0_gt)
                {
                    currentState = GenerationState.ReplacingBiomeBlocks;
                }
                return false;
            
            case GenerationState.ReplacingBiomeBlocks:
#if UNITY_EDITOR
                if (enableVerboseLogging)
                {
                    logBuilder.Clear();
                    logBuilder.Append("[McTerrainGenerator.Step] State: ReplacingBiomeBlocks for chunk (").Append(currentChunkX).Append(",").Append(currentChunkY).Append(",").Append(currentChunkZ).Append(")");
                    Debug.Log(logBuilder.ToString());
                }
#endif
                int columnsToProcess = (coordinator != null) ? coordinator.columnsPerDataGenStep : 4;
                byte oceanHeight_rb = 64;

                for (int x = 0; x < 16; x++)
                {
                    for (int z = 0; z < 16; z++)
                    {
                        BetaBiomeEnum biome = currentChunkBiomes[x + z * 16];
                        bool sand = sandNoise[x + z * 16] + rand.NextDouble() * 0.2D > 0.0D;
                        bool gravel = gravelNoise[x + z * 16] + rand.NextDouble() * 0.2D > 3D;
                        int depth = (int)(stoneNoise[x + z * 16] / 3D + 3D + rand.NextDouble() * 0.25D);
                        
                        // Load the previous depth from the cache for this column
                        int prevDepth = columnDepthCache[z * world.chunkSizeXZ + x];

                        BlockMaterial topBlock = (BlockMaterial)BiomeOld.top(biome);
                        BlockMaterial fillerBlock = (BlockMaterial)BiomeOld.filler(biome);

                        for (int y = (currentChunkY * world.chunkSizeY) + world.chunkSizeY - 1; y >= (currentChunkY * world.chunkSizeY); y--)
                        {
                            int localY = y - (currentChunkY * world.chunkSizeY);
                            if (localY < 0 || localY >= world.chunkSizeY) continue;

                            int index = localY * world.chunkSizeXZ * world.chunkSizeXZ + z * world.chunkSizeXZ + x;

                            if (y <= rand.NextInt(5))
                            {
                                workingChunkData[index] = world.PackBlockData(bedrockBlockID);
                                continue;
                            }
                            
                            ushort blockData = workingChunkData[index];
                            BlockMaterial block = (BlockMaterial)(blockData & 0xFF);

                            if (block == BlockMaterial.AIR) { prevDepth = -1; continue; }
                            if (block != BlockMaterial.STONE) continue;

                            if (prevDepth == -1)
                            {
                                if (depth <= 0) { topBlock = BlockMaterial.AIR; fillerBlock = BlockMaterial.STONE; }
                                else if (y >= oceanHeight_rb - 4 && y <= oceanHeight_rb + 1)
                                {
                                    topBlock = (BlockMaterial)BiomeOld.top(biome);
                                    fillerBlock = (BlockMaterial)BiomeOld.filler(biome);
                                    if (gravel) { topBlock = BlockMaterial.AIR; fillerBlock = BlockMaterial.GRAVEL; }
                                    if (sand) { topBlock = BlockMaterial.SAND; fillerBlock = BlockMaterial.SAND; }
                                }
                                if (y < oceanHeight_rb && topBlock == BlockMaterial.AIR) topBlock = BlockMaterial.STATIONARY_WATER;
                                
                                prevDepth = depth;
                                if (y >= oceanHeight_rb - 1) workingChunkData[index] = world.PackBlockData((byte)topBlock);
                                else workingChunkData[index] = world.PackBlockData((byte)fillerBlock);
                                continue;
                            }
                            if (prevDepth > 0)
                            {
                                prevDepth--;
                                workingChunkData[index] = world.PackBlockData((byte)fillerBlock);
                                if (prevDepth == 0 && fillerBlock == BlockMaterial.SAND)
                                {
                                    prevDepth = rand.NextInt(4);
                                    fillerBlock = BlockMaterial.SANDSTONE;
                                }
                            }
                        }
                        
                        // Save the final depth back to the cache for the next chunk below
                        columnDepthCache[z * world.chunkSizeXZ + x] = prevDepth;
                    }
                }
                
#if UNITY_EDITOR
                if (enableVerboseLogging) time_ReplacingBiomes += (Time.realtimeSinceStartup - stepTimer) * 1000f;
#endif
                currentState = GenerationState.Complete;
                
                return false;

            case GenerationState.Complete:
                completedData = workingChunkData;
#if UNITY_EDITOR
                if (enableVerboseLogging)
                {
                    float totalTime = (Time.realtimeSinceStartup - time_Total) * 1000f;
                    logBuilder.Clear();
                    logBuilder.Append("--- Terrain Gen Timings for Chunk (").Append(currentChunkX).Append(",").Append(currentChunkY).Append(",").Append(currentChunkZ).Append(") ---").AppendLine();
                    logBuilder.Append("1. Preparation:         ").Append(time_Preparation.ToString("F3")).Append(" ms").AppendLine();
                    logBuilder.Append("2. Terrain Generation:  ").Append(time_GeneratingTerrain.ToString("F3")).Append(" ms").AppendLine();
                    logBuilder.Append("3. Biome Block Replace: ").Append(time_ReplacingBiomes.ToString("F3")).Append(" ms").AppendLine();
                    logBuilder.Append("Total Time-Sliced Gen:  ").Append(totalTime.ToString("F3")).Append(" ms").AppendLine();
                    Debug.Log(logBuilder.ToString());
                }
#endif
                currentState = GenerationState.Idle;
                return true;

            default:
                currentState = GenerationState.Idle;
                return true;
        }
    }

    public void initRand(int chunkX, int chunkZ)
    {
        this.rand.SetSeed((long)chunkX * 341873128712L + (long)chunkZ * 132897987541L);
    }

    // This function is now fully time-sliced and integrated into the state machine.
    // private double[] initNoiseField(...)

    // This method is now obsolete as the GPU handles all block filling.
    // private bool StepBlockFilling() { ... }

    // This method is now obsolete as the GPU handles all surface decoration.
    // private bool StepSurfaceDecoration() { ... }

    private void GenerateCaves(ushort[] chunkData, int chunkX, int chunkY, int chunkZ)
    {
    }

    private void CarveCaveSystem(ushort[] chunkData, int chunkX, int chunkY, int chunkZ,
                                 float x, float y, float z, float yaw, float pitch,
                                 int length, int branch)
    {
    }

    private void GenerateBedrock(ushort[] chunkData)
    {
    }
}