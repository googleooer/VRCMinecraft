using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRRefAssist;
using System.Text;

public enum GenerationState
{
    GeneratingDensity,
    FillingBlocks,
    GeneratingBedrock,
    ApplyingSurface,
    GeneratingCaves,
    Complete
}

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McTerrainGenerator : UdonSharpBehaviour
{
    [Header("Biome & Surface Settings")]
    [Tooltip("The Y-level at or below which water will be placed in open areas.")]
    public int seaLevel = 62;
    [Tooltip("The depth, in blocks, that grass/dirt will form on surfaces.")]
    public int surfaceDepth = 4;

    [Header("Terrain Composition")]
    public byte airBlockID = 0;
    public byte grassBlockID = 2;
    public byte stoneBlockID = 1;
    public byte dirtBlockID = 3;
    public byte waterBlockID = 9;
    public byte bedrockBlockID = 7;
    public byte sandBlockID = 12;
    public byte sandStoneBlockID = 24;

    [Header("Noise Generators")]
    [SerializeField] private PerlinNoiseGenerator mainNoise;
    [SerializeField] private PerlinNoiseGenerator minLimitNoise;
    [SerializeField] private PerlinNoiseGenerator maxLimitNoise;
    [SerializeField] private PerlinNoiseGenerator depthNoise;
    [SerializeField] private PerlinNoiseGenerator selectorNoise;

    [Header("Beta 1.7.3 Terrain Parameters")]
    [Range(0.1f, 2.0f)] public float terrainHeightMultiplier = 1.0f;
    [Range(0.0f, 1.0f)] public float caveFrequency = 0.14f;
    public bool generateCaves = true;
    public bool generateBedrock = true;

    [Header("Structure & Feature Templates")]
    public McStructureTemplate[] structureTemplates;

#if UNITY_EDITOR
    [Header("Debugging")]
    public bool enableVerboseLogging = true;
    private float time_Total, time_DensitySamples, time_DensityInterp, time_BlockFill, time_Surface, time_Caves, time_Bedrock;
#endif

    [SerializeField, FindObjectOfType(true)]
    private McWorld world;

    [SerializeField, FindObjectOfType(true)]
    private McBlockTypeManager blockTypeManager;

    private bool isInitialized = false;
    private int _worldActualSeed;
    private uint _placementRandState;
    private uint _biomeRandState;
    private uint _caveRandState;
    private uint _bedrockRandState;

    // Global heightmap cache to avoid re-calculating surface Y
    private int[] globalHeightMap;
    private bool[] globalHeightMapInitialized;

    // Biome temperature/humidity for simple biome selection
    private float[] biomeTemperature;
    private float[] biomeHumidity;

    // Time-slicing state constants
    private const int STATE_IDLE = 0;
    private const int STATE_GENERATING_DENSITY = 1;
    private const int STATE_FILLING_BLOCKS = 2;
    private const int STATE_APPLYING_SURFACE = 3;
    private const int STATE_GENERATING_CAVES = 4;
    private const int STATE_GENERATING_BEDROCK = 5;
    private const int STATE_COMPLETE = 6;

    private GenerationState currentState = STATE_IDLE;
    private int currentStep = 0;
    private int currentChunkX, currentChunkY, currentChunkZ;
    private ushort[] workingChunkData;
    private float[] workingDensityField;
    private float[] workingHeightMap;
    private float[] workingSamples;
    private int workingSampleXZ, workingSampleY;

    // FIXED: Use consistent sampling interval regardless of chunk size
    private const int DENSITY_SAMPLE_INTERVAL = 4;

    private StringBuilder logBuilder;

    public void InitializeGenerator(int seed)
    {
        float startTime = Time.realtimeSinceStartup;
        if (isInitialized) return;

        logBuilder = new StringBuilder(256);
        _worldActualSeed = seed;

        _placementRandState = (uint)seed;
        if (_placementRandState == 0) _placementRandState = 1; // Ensure non-zero

        // Initialize all noise generators with different offsets
        if (mainNoise != null) mainNoise.Initialize(seed, 0);
        if (minLimitNoise != null) minLimitNoise.Initialize(seed, 100);
        if (maxLimitNoise != null) maxLimitNoise.Initialize(seed, 200);
        if (depthNoise != null) depthNoise.Initialize(seed, 300);
        if (selectorNoise != null) selectorNoise.Initialize(seed, 400);

        // Initialize biome arrays (flexible size based on chunk dimensions)
        int biomeArraySize = world.chunkSizeXZ * world.chunkSizeXZ;
        biomeTemperature = new float[biomeArraySize];
        biomeHumidity = new float[biomeArraySize];

        // Initialize the global heightmap cache
        if (globalHeightMap == null)
        {
            int worldSizeX = world.worldDimensionX * world.chunkSizeXZ;
            int worldSizeZ = world.worldDimensionZ * world.chunkSizeXZ;
            globalHeightMap = new int[worldSizeX * worldSizeZ];
            globalHeightMapInitialized = new bool[worldSizeX * worldSizeZ];
        }

        isInitialized = true;

#if UNITY_EDITOR
        if (enableVerboseLogging) {
            logBuilder.Clear();
            logBuilder.AppendFormat("[McTerrainGenerator.InitializeGenerator] Complete. Seed: {0}. Time: {1:F2} ms.", seed, (Time.realtimeSinceStartup - startTime) * 1000f);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }

    // Main entry point - now returns null to indicate processing needed
    public ushort[] GenerateChunkData(int chunkX, int chunkY, int chunkZ)
    {
        // Start the time-sliced generation process
        StartChunkGeneration(chunkX, chunkY, chunkZ);
        return null; // Indicates time-sliced processing needed
    }

    // Initialize time-sliced generation
    public void StartChunkGeneration(int chunkX, int chunkY, int chunkZ)
    {
        if (!isInitialized)
        {
            Debug.LogError("[McTerrainGenerator] Not initialized! Call InitializeGenerator first.");
            return;
        }

        currentChunkX = chunkX;
        currentChunkY = chunkY;
        currentChunkZ = chunkZ;
        currentState = GenerationState.GeneratingDensity;
        currentStep = 0;

        int chunkSize = world.chunkSizeXZ * world.chunkSizeY * world.chunkSizeXZ;
        workingChunkData = new ushort[chunkSize];
        workingHeightMap = new float[world.chunkSizeXZ * world.chunkSizeXZ];

#if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            time_Total = Time.realtimeSinceStartup;
            time_DensitySamples = 0f;
            time_DensityInterp = 0f;
            time_BlockFill = 0f;
            time_Surface = 0f;
            time_Caves = 0f;
            time_Bedrock = 0f;
        }
#endif
    }

    // Process one step of generation - returns true when complete
    public bool StepChunkGeneration(out ushort[] completedData)
    {
        completedData = null;

#if UNITY_EDITOR
        float stepTimer = 0f;
        if (enableVerboseLogging) stepTimer = Time.realtimeSinceStartup;
#endif

        switch (currentState)
        {
            case GenerationState.GeneratingDensity:
                bool densityDone = StepDensityGeneration();
#if UNITY_EDITOR
                if(enableVerboseLogging) time_DensitySamples += (Time.realtimeSinceStartup - stepTimer);
#endif
                if (densityDone)
                {
                    currentState = GenerationState.FillingBlocks;
                    currentStep = 0;
                }
                return false;

            case GenerationState.FillingBlocks:
                bool fillDone = StepBlockFilling();
#if UNITY_EDITOR
                if(enableVerboseLogging) time_BlockFill += (Time.realtimeSinceStartup - stepTimer);
#endif
                if (fillDone)
                {
                    // Setup for surface decoration
                    GenerateBiomeData(currentChunkX, currentChunkZ);
                    currentState = GenerationState.ApplyingSurface;
                    currentStep = 0;
                }
                return false;

            case GenerationState.ApplyingSurface:
                bool surfaceDone = StepSurfaceDecoration();
#if UNITY_EDITOR
                if (enableVerboseLogging) time_Surface += (Time.realtimeSinceStartup - stepTimer);
#endif
                if(surfaceDone)
                {
                    currentState = generateCaves ? GenerationState.GeneratingCaves : GenerationState.GeneratingBedrock;
                    currentStep = 0;
                }
                return false;

            case GenerationState.GeneratingCaves:
                if (StepCaveGeneration())
                {
#if UNITY_EDITOR
                    if (enableVerboseLogging) time_Caves = (Time.realtimeSinceStartup - stepTimer) * 1000f;
#endif
                    currentState = GenerationState.GeneratingBedrock;
                    currentStep = 0;
                }
                return false;

            case GenerationState.GeneratingBedrock:
                if (generateBedrock && currentChunkY == 0)
                {
                    GenerateBedrock(workingChunkData);
                }
#if UNITY_EDITOR
                if (enableVerboseLogging) time_Bedrock = (Time.realtimeSinceStartup - stepTimer) * 1000f;
#endif
                currentState = GenerationState.Complete;
                completedData = workingChunkData;

#if UNITY_EDITOR
                if (enableVerboseLogging)
                {
                    float totalTime = (Time.realtimeSinceStartup - time_Total) * 1000f;
                    logBuilder.Clear();
                    logBuilder.AppendFormat("--- Terrain Gen Timings for Chunk ({0},{1},{2}) ---", currentChunkX, currentChunkY, currentChunkZ).AppendLine();
                    logBuilder.AppendFormat("1. Density Samples (total): {0:F3} ms", time_DensitySamples * 1000f).AppendLine();
                    logBuilder.AppendFormat("2. Density Interp:          {0:F3} ms", time_DensityInterp).AppendLine();
                    logBuilder.AppendFormat("3. Block Fill (total):      {0:F3} ms", time_BlockFill * 1000f).AppendLine();
                    logBuilder.AppendFormat("4. Surface Decor (total):   {0:F3} ms", time_Surface * 1000f).AppendLine();
                    logBuilder.AppendFormat("5. Cave Gen:                {0:F3} ms", time_Caves).AppendLine();
                    logBuilder.AppendFormat("6. Bedrock Gen:             {0:F3} ms", time_Bedrock).AppendLine();
                    logBuilder.AppendFormat("-> Total Measured:          {0:F3} ms", (time_DensitySamples*1000f + time_DensityInterp + time_BlockFill*1000f + time_Surface*1000f + time_Caves + time_Bedrock)).AppendLine();
                    logBuilder.AppendFormat("=> Total Actual:            {0:F3} ms", totalTime).AppendLine();
                    Debug.Log(logBuilder.ToString());
                }
#endif
                return true;

            case GenerationState.Complete:
                completedData = workingChunkData;
                return true;

            default:
                return true;
        }
    }

    private bool StepDensityGeneration()
    {
        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;
        
        int minWorldX;
        int maxWorldX;
        int minWorldY;
        int maxWorldY;
        int minWorldZ;
        int maxWorldZ;

        int alignedMinX;
        int alignedMinY;
        int alignedMinZ;

        if (currentStep == 0)
        {
            // FIXED: Calculate sample dimensions based on world-space sampling
            // Always sample at DENSITY_SAMPLE_INTERVAL boundaries, with extra samples for overlap
            minWorldX = currentChunkX * chunkSizeXZ;
            maxWorldX = minWorldX + chunkSizeXZ;
            minWorldY = currentChunkY * chunkSizeY;
            maxWorldY = minWorldY + chunkSizeY;
            minWorldZ = currentChunkZ * chunkSizeXZ;
            maxWorldZ = minWorldZ + chunkSizeXZ;

            // Align to sample grid and calculate required samples
            alignedMinX = (minWorldX / DENSITY_SAMPLE_INTERVAL) * DENSITY_SAMPLE_INTERVAL;
            alignedMinY = (minWorldY / DENSITY_SAMPLE_INTERVAL) * DENSITY_SAMPLE_INTERVAL;
            alignedMinZ = (minWorldZ / DENSITY_SAMPLE_INTERVAL) * DENSITY_SAMPLE_INTERVAL;

            // Need samples up to and including the boundaries
            workingSampleXZ = ((maxWorldX - alignedMinX + DENSITY_SAMPLE_INTERVAL - 1) / DENSITY_SAMPLE_INTERVAL) + 1;
            workingSampleY = ((maxWorldY - alignedMinY + DENSITY_SAMPLE_INTERVAL - 1) / DENSITY_SAMPLE_INTERVAL) + 1;

            workingSamples = new float[workingSampleXZ * workingSampleY * workingSampleXZ];
            workingDensityField = new float[chunkSizeXZ * chunkSizeY * chunkSizeXZ];
            currentStep++;
            return false;
        }

        // Generate samples in steps
        int samplesPerStep = 64;
        int totalSamples = workingSampleXZ * workingSampleY * workingSampleXZ;
        int startIndex = (currentStep - 1) * samplesPerStep;
        int endIndex = Mathf.Min(startIndex + samplesPerStep, totalSamples);

        // FIXED: Calculate aligned world coordinates for sampling
        minWorldX = currentChunkX * chunkSizeXZ;
        minWorldY = currentChunkY * chunkSizeY;
        minWorldZ = currentChunkZ * chunkSizeXZ;
        alignedMinX = (minWorldX / DENSITY_SAMPLE_INTERVAL) * DENSITY_SAMPLE_INTERVAL;
        alignedMinY = (minWorldY / DENSITY_SAMPLE_INTERVAL) * DENSITY_SAMPLE_INTERVAL;
        alignedMinZ = (minWorldZ / DENSITY_SAMPLE_INTERVAL) * DENSITY_SAMPLE_INTERVAL;

        for (int i = startIndex; i < endIndex; i++)
        {
            int sx = i % workingSampleXZ;
            int sy = (i / workingSampleXZ) % workingSampleY;
            int sz = i / (workingSampleXZ * workingSampleY);

            // FIXED: Use aligned world coordinates
            float worldX = alignedMinX + (sx * DENSITY_SAMPLE_INTERVAL);
            float worldY = alignedMinY + (sy * DENSITY_SAMPLE_INTERVAL);
            float worldZ = alignedMinZ + (sz * DENSITY_SAMPLE_INTERVAL);

            // Generate noise value
            float density = GenerateDensityAtPoint(worldX, worldY, worldZ);
            workingSamples[sx + sy * workingSampleXZ * workingSampleXZ + sz * workingSampleXZ] = density;
        }

        currentStep++;

        if (endIndex >= totalSamples)
        {
#if UNITY_EDITOR
            if (enableVerboseLogging)
            {
                float timer = Time.realtimeSinceStartup;
                InterpolateDensityField();
                time_DensityInterp = (Time.realtimeSinceStartup - timer) * 1000f;
            }
            else
            {
                InterpolateDensityField();
            }
#endif
            return true;
        }

        return false;
    }

    private float GenerateDensityAtPoint(float worldX, float worldY, float worldZ)
    {
        if (!isInitialized)
        {
            Debug.LogError("[McTerrainGenerator] GenerateDensityAtPoint called before initialization!");
            return 0;
        }
        
        if (mainNoise == null || minLimitNoise == null || maxLimitNoise == null || 
            selectorNoise == null || depthNoise == null)
        {
            return 0;
        }

        float warpX = Mathf.Sin(_worldActualSeed * 0.1f) * 100.0f;
        float warpZ = Mathf.Cos(_worldActualSeed * 0.1f) * 100.0f;
        float warpedX = worldX + warpX;
        float warpedZ = worldZ + warpZ;

        // Beta 1.7.3 noise combination with proper scaling
        float mainVal = mainNoise.GenerateNoise(
            warpedX / 684.412f, 
            worldY / 684.412f, 
            warpedZ / 684.412f, 
            8, 0.5f
        );
        
        float minVal = minLimitNoise.GenerateNoise(
            warpedX / 512f, 
            worldY / 512f, 
            warpedZ / 512f, 
            8, 0.5f
        );
        
        float maxVal = maxLimitNoise.GenerateNoise(
            warpedX / 512f, 
            worldY / 512f, 
            warpedZ / 512f, 
            8, 0.5f
        );
        
        float selectorVal = selectorNoise.GenerateNoise(
            warpedX / 512f, 
            worldY / 512f, 
            warpedZ / 512f, 
            4, 0.5f
        );
        
        // Depth-based density reduction
        float depth = depthNoise.GenerateNoise(
            warpedX / 200f, 
            0, 
            warpedZ / 200f, 
            4, 0.5f
        );
        
        // Height gradient
        float heightGradient = (worldY - seaLevel) / 64.0f;
        
        // Combine noises Beta 1.7.3 style
        float density = Mathf.Lerp(minVal, maxVal, selectorVal) + mainVal;
        density -= heightGradient * (1.0f + depth * 0.5f);
        density *= terrainHeightMultiplier;
        
        return density;
    }

    private void InterpolateDensityField()
    {
        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;

        // FIXED: Calculate aligned offsets for proper interpolation
        int minWorldX = currentChunkX * chunkSizeXZ;
        int minWorldY = currentChunkY * chunkSizeY;
        int minWorldZ = currentChunkZ * chunkSizeXZ;
        int alignedMinX = (minWorldX / DENSITY_SAMPLE_INTERVAL) * DENSITY_SAMPLE_INTERVAL;
        int alignedMinY = (minWorldY / DENSITY_SAMPLE_INTERVAL) * DENSITY_SAMPLE_INTERVAL;
        int alignedMinZ = (minWorldZ / DENSITY_SAMPLE_INTERVAL) * DENSITY_SAMPLE_INTERVAL;
        
        float offsetX = (minWorldX - alignedMinX) / (float)DENSITY_SAMPLE_INTERVAL;
        float offsetY = (minWorldY - alignedMinY) / (float)DENSITY_SAMPLE_INTERVAL;
        float offsetZ = (minWorldZ - alignedMinZ) / (float)DENSITY_SAMPLE_INTERVAL;

        for (int x = 0; x < chunkSizeXZ; x++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                for (int y = 0; y < chunkSizeY; y++)
                {
                    // FIXED: Calculate sample positions with proper offset
                    float sx = offsetX + (x / (float)DENSITY_SAMPLE_INTERVAL);
                    float sy = offsetY + (y / (float)DENSITY_SAMPLE_INTERVAL);
                    float sz = offsetZ + (z / (float)DENSITY_SAMPLE_INTERVAL);
                    
                    int sx0 = Mathf.FloorToInt(sx);
                    int sy0 = Mathf.FloorToInt(sy);
                    int sz0 = Mathf.FloorToInt(sz);
                    
                    // Ensure we stay within sample bounds
                    sx0 = Mathf.Clamp(sx0, 0, workingSampleXZ - 2);
                    sy0 = Mathf.Clamp(sy0, 0, workingSampleY - 2);
                    sz0 = Mathf.Clamp(sz0, 0, workingSampleXZ - 2);
                    
                    int sx1 = sx0 + 1;
                    int sy1 = sy0 + 1;
                    int sz1 = sz0 + 1;
                    
                    // Calculate interpolation factors with smoothstep for smoother transitions
                    float fx = SmoothStep(sx - sx0);
                    float fy = SmoothStep(sy - sy0);
                    float fz = SmoothStep(sz - sz0);
                    
                    // Get sample indices
                    int idx000 = sx0 + sy0 * workingSampleXZ * workingSampleXZ + sz0 * workingSampleXZ;
                    int idx100 = sx1 + sy0 * workingSampleXZ * workingSampleXZ + sz0 * workingSampleXZ;
                    int idx010 = sx0 + sy1 * workingSampleXZ * workingSampleXZ + sz0 * workingSampleXZ;
                    int idx110 = sx1 + sy1 * workingSampleXZ * workingSampleXZ + sz0 * workingSampleXZ;
                    int idx001 = sx0 + sy0 * workingSampleXZ * workingSampleXZ + sz1 * workingSampleXZ;
                    int idx101 = sx1 + sy0 * workingSampleXZ * workingSampleXZ + sz1 * workingSampleXZ;
                    int idx011 = sx0 + sy1 * workingSampleXZ * workingSampleXZ + sz1 * workingSampleXZ;
                    int idx111 = sx1 + sy1 * workingSampleXZ * workingSampleXZ + sz1 * workingSampleXZ;
                    
                    // Trilinear interpolation
                    float v000 = workingSamples[idx000];
                    float v100 = workingSamples[idx100];
                    float v010 = workingSamples[idx010];
                    float v110 = workingSamples[idx110];
                    float v001 = workingSamples[idx001];
                    float v101 = workingSamples[idx101];
                    float v011 = workingSamples[idx011];
                    float v111 = workingSamples[idx111];
                    
                    float v00 = Mathf.Lerp(v000, v100, fx);
                    float v10 = Mathf.Lerp(v010, v110, fx);
                    float v01 = Mathf.Lerp(v001, v101, fx);
                    float v11 = Mathf.Lerp(v011, v111, fx);
                    
                    float v0 = Mathf.Lerp(v00, v10, fy);
                    float v1 = Mathf.Lerp(v01, v11, fy);
                    
                    workingDensityField[x + y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ] = Mathf.Lerp(v0, v1, fz);
                }
            }
        }
    }

    private float SmoothStep(float t)
    {
        return t * t * (3.0f - 2.0f * t);
    }

    private bool StepBlockFilling()
    {
        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;
        int columnsPerStep = 4; // Process 4 columns per step

        int totalColumns = chunkSizeXZ * chunkSizeXZ;
        int startColumn = currentStep * columnsPerStep;
        int endColumn = Mathf.Min(startColumn + columnsPerStep, totalColumns);

        for (int col = startColumn; col < endColumn; col++)
        {
            int x = col % chunkSizeXZ;
            int z = col / chunkSizeXZ;

            // Calculate surface height from density field
            int surfaceWorldY = -1;
            for (int y = chunkSizeY - 1; y >= 0; y--)
            {
                int index3D = x + y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ;
                int worldY = currentChunkY * chunkSizeY + y;

                if (workingDensityField[index3D] > 0)
                {
                    if (y == chunkSizeY - 1 || workingDensityField[x + (y + 1) * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ] <= 0)
                    {
                        surfaceWorldY = worldY;
                        break;
                    }
                }
            }

            workingHeightMap[x + z * chunkSizeXZ] = surfaceWorldY;

            // Fill blocks based on density
            for (int y = 0; y < chunkSizeY; y++)
            {
                int worldY = currentChunkY * chunkSizeY + y;
                int index = x + y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ;

                if (workingDensityField[index] > 0)
                {
                    workingChunkData[index] = stoneBlockID;
                }
                else if (worldY <= seaLevel)
                {
                    workingChunkData[index] = waterBlockID;
                }
                else
                {
                    workingChunkData[index] = airBlockID;
                }
            }
        }

        currentStep++;
        return endColumn >= totalColumns;
    }

    private bool StepSurfaceDecoration()
    {
        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;
        int columnsPerStep = 4; // Process 4 columns per step

        int totalColumns = chunkSizeXZ * chunkSizeXZ;
        int startColumn = currentStep * columnsPerStep;
        int endColumn = Mathf.Min(startColumn + columnsPerStep, totalColumns);

        for (int col = startColumn; col < endColumn; col++)
        {
            int x = col % chunkSizeXZ;
            int z = col / chunkSizeXZ;

            int biomeIndex = x + z * chunkSizeXZ;
            float temp = biomeTemperature[biomeIndex];
            float humidity = biomeHumidity[biomeIndex];

            // Simple biome determination
            bool isDesert = temp > 0.95f && humidity < 0.2f;
            bool isTundra = temp < 0.2f;

            int worldX = currentChunkX * chunkSizeXZ + x;
            int worldZ = currentChunkZ * chunkSizeXZ + z;

            // --- OPTIMIZATION: Use a global, cached heightmap ---
            int heightMapX = worldX + world.globalVoxelOffsetX;
            int heightMapZ = worldZ + world.globalVoxelOffsetZ;
            int heightMapIndex = heightMapZ * (world.worldDimensionX * world.chunkSizeXZ) + heightMapX;
            int surfaceWorldY;

            if (globalHeightMapInitialized[heightMapIndex])
            {
                surfaceWorldY = globalHeightMap[heightMapIndex];
            }
            else
            {
                // This is the first time we've seen this column.
                // OPTIMIZED: Use a coarse-to-fine search to find the surface quickly.
                surfaceWorldY = -1;
                int worldTopY = world.worldDimensionY * world.chunkSizeY;
                int coarseStep = 8; // Scan in 8-block intervals

                // 1. Coarse search downwards to find an area with land
                int yCoarseStart = -1;
                for (int yScan = worldTopY - 1; yScan >= 0; yScan -= coarseStep)
                {
                    if (GenerateDensityAtPoint(worldX, yScan, worldZ) > 0)
                    {
                        yCoarseStart = yScan;
                        break;
                    }
                }

                // 2. Fine search in the identified vicinity if a potential surface was found
                if (yCoarseStart != -1)
                {
                    int fineSearchTop = yCoarseStart + coarseStep;
                    if (fineSearchTop >= worldTopY) fineSearchTop = worldTopY - 1;

                    for (int yScan = fineSearchTop; yScan >= yCoarseStart; yScan--)
                    {
                        if (GenerateDensityAtPoint(worldX, yScan, worldZ) > 0)
                        {
                            surfaceWorldY = yScan;
                            break;
                        }
                    }
                }

                // Store the result in the cache for next time
                globalHeightMap[heightMapIndex] = surfaceWorldY;
                globalHeightMapInitialized[heightMapIndex] = true;
            }

            if (surfaceWorldY == -1) continue; // No ground in this column.

            // Apply surface decoration based on the true WORLD surface height
            for (int y = chunkSizeY - 1; y >= 0; y--)
            {
                int worldY = currentChunkY * chunkSizeY + y;
                int index = x + y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ;

                if (workingChunkData[index] == stoneBlockID)
                {
                    int depthFromWorldSurface = surfaceWorldY - worldY;

                    if (depthFromWorldSurface == 0)
                    {
                        // This is the actual world surface block
                        if (surfaceWorldY < seaLevel - 1)
                        {
                            workingChunkData[index] = sandBlockID;
                        }
                        else if (isDesert)
                        {
                            workingChunkData[index] = sandBlockID;
                        }
                        else if (isTundra && surfaceWorldY > seaLevel + 20)
                        {
                            workingChunkData[index] = stoneBlockID; // Snow would go here
                        }
                        else
                        {
                            workingChunkData[index] = grassBlockID;
                        }
                    }
                    else if (depthFromWorldSurface > 0 && depthFromWorldSurface < surfaceDepth)
                    {
                        // Subsurface blocks
                        if (isDesert && depthFromWorldSurface < 4)
                        {
                            workingChunkData[index] = sandBlockID;
                        }
                        else if (surfaceWorldY < seaLevel - 1)
                        {
                            if (depthFromWorldSurface < 3)
                            {
                                workingChunkData[index] = sandBlockID;
                            }
                            else
                            {
                                workingChunkData[index] = sandStoneBlockID;
                            }
                        }
                        else
                        {
                            workingChunkData[index] = dirtBlockID;
                        }
                    }
                }
            }
        }

        currentStep++;
        return endColumn >= totalColumns;
    }

    private bool StepCaveGeneration()
    {
        // For simplicity, do all cave generation in one step
        GenerateCaves(workingChunkData, currentChunkX, currentChunkY, currentChunkZ);
        return true;
    }

    // Keep existing methods below unchanged...
    private void GenerateBiomeData(int chunkX, int chunkZ)
    {
        int chunkSizeXZ = world.chunkSizeXZ;
        
        // Initialize biome random state
        _biomeRandState = (uint)HashChunkCoord(chunkX, chunkZ, _worldActualSeed + 1000);
        if (_biomeRandState == 0) _biomeRandState = 1;
        
        // FIXED: Add world seed offset to prevent repetition
        float biomeOffset = _worldActualSeed * 0.1f;
        
        // Generate temperature and humidity maps
        for (int x = 0; x < chunkSizeXZ; x++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                float worldX = chunkX * chunkSizeXZ + x;
                float worldZ = chunkZ * chunkSizeXZ + z;
                
                // Temperature noise with offset
                float temp = Mathf.PerlinNoise(
                    worldX * 0.025f + 0.5f + biomeOffset, 
                    worldZ * 0.025f + 0.5f + biomeOffset
                );
                temp = Mathf.Clamp01(temp);
                
                // Humidity noise with different offset
                float humidity = Mathf.PerlinNoise(
                    worldX * 0.05f + 1000.5f + biomeOffset * 2f, 
                    worldZ * 0.05f + 1000.5f + biomeOffset * 2f
                );
                humidity = Mathf.Clamp01(humidity * temp);
                
                biomeTemperature[x + z * chunkSizeXZ] = temp;
                biomeHumidity[x + z * chunkSizeXZ] = humidity;
            }
        }
    }

    private void GenerateCaves(ushort[] chunkData, int chunkX, int chunkY, int chunkZ)
    {
        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;
        
        // Scale cave generation radius based on chunk size
        int caveRadius = Mathf.Max(8, chunkSizeXZ / 2);
        
        // Check surrounding chunks for cave systems
        for (int cx = -caveRadius; cx <= caveRadius; cx++)
        {
            for (int cz = -caveRadius; cz <= caveRadius; cz++)
            {
                int otherChunkX = chunkX + cx;
                int otherChunkZ = chunkZ + cz;
                
                // Use a better hash function for cave seeds
                // This uses the FNV-1a hash algorithm which distributes better
                int caveSeed = HashChunkCoord(otherChunkX, otherChunkZ, _worldActualSeed);
                
                Random.InitState(caveSeed);
                
                if (Random.value > caveFrequency) continue;
                
                // Generate 1-3 cave systems
                int caveCount = Random.Range(1, 4);
                
                for (int i = 0; i < caveCount; i++)
                {
                    float startX = otherChunkX * chunkSizeXZ + Random.Range(0, chunkSizeXZ);
                    float startY = Random.Range(32, 96);
                    float startZ = otherChunkZ * chunkSizeXZ + Random.Range(0, chunkSizeXZ);
                    
                    float yaw = Random.Range(0f, Mathf.PI * 2);
                    float pitch = Random.Range(-0.5f, 0.5f);
                    
                    CarveCaveSystem(chunkData, chunkX, chunkY, chunkZ, 
                                startX, startY, startZ, yaw, pitch, 
                                Random.Range(85, 112), 0);
                }
            }
        }
    }

    // Add this helper method for better hash distribution
    private int HashChunkCoord(int x, int z, int seed)
    {
        // FNV-1a hash algorithm adapted for chunk coordinates
        int hash = -2128831035; // Same bit pattern as 2166136261u when cast to int
            
        // Mix in the seed
        hash = (hash ^ seed) * 16777619;
        
        // Mix in x coordinate
        hash = (hash ^ x) * 16777619;
        hash = (hash ^ (x >> 16)) * 16777619;
        
        // Mix in z coordinate  
        hash = (hash ^ z) * 16777619;
        hash = (hash ^ (z >> 16)) * 16777619;
        
        // Additional mixing
        hash ^= hash >> 13;
        hash *= 16777619;
        hash ^= hash >> 15;
        
        return hash;
        
    }

    private int SimpleHash(int x, int y, int seed)
    {
        int h = seed + x * 374761393 + y * 668265261;
        h = (h ^ (h >> 13)) * 1274126177;
        return h ^ (h >> 16);
    }

    private int SimpleHash3D(int x, int y, int z, int seed)
    {
        int h = seed + x * 374761393 + y * 668265261 + z * 805306457;
        h = (h ^ (h >> 13)) * 1274126177;
        return h ^ (h >> 16);
    }
    
    private uint XorShift32(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private float GetRandomFloat(ref uint state)
    {
        state = XorShift32(state);
        return (state & 0x7FFFFFFF) / (float)0x7FFFFFFF;
    }

    private int GetRandomInt(ref uint state, int min, int max)
    {
        state = XorShift32(state);
        uint range = (uint)(max - min);
        uint randomValue = state & 0x7FFFFFFF;
        uint result = randomValue - ((randomValue / range) * range); // This is equivalent to % 
        return min + (int)result;
    }

    // Simple value noise (not true Perlin, but works for terrain)
private float ValueNoise2D(float x, float y, int seed)
{
    int xi = Mathf.FloorToInt(x);
    int yi = Mathf.FloorToInt(y);
    
    float fx = x - xi;
    float fy = y - yi;
    
    // Smooth the fractional parts
    fx = fx * fx * (3.0f - 2.0f * fx);
    fy = fy * fy * (3.0f - 2.0f * fy);
    
    // Get corner values
    float v00 = HashToFloat(SimpleHash(xi, yi, seed));
    float v10 = HashToFloat(SimpleHash(xi + 1, yi, seed));
    float v01 = HashToFloat(SimpleHash(xi, yi + 1, seed));
    float v11 = HashToFloat(SimpleHash(xi + 1, yi + 1, seed));
    
    // Bilinear interpolation
    float v0 = Mathf.Lerp(v00, v10, fx);
    float v1 = Mathf.Lerp(v01, v11, fx);
    return Mathf.Lerp(v0, v1, fy);
}

    private float HashToFloat(int hash)
    {
        return (hash & 0x7FFFFFFF) / (float)0x7FFFFFFF;
    }

    // Octaved noise for terrain
    private float OctavedNoise2D(float x, float y, int seed, int octaves, float persistence, float scale)
    {
        float total = 0;
        float frequency = scale;
        float amplitude = 1;
        float maxValue = 0;
        
        for (int i = 0; i < octaves; i++)
        {
            total += ValueNoise2D(x * frequency, y * frequency, seed + i) * amplitude;
            maxValue += amplitude;
            amplitude *= persistence;
            frequency *= 2;
        }
        
        return total / maxValue;
    }

    private void CarveCaveSystem(ushort[] chunkData, int chunkX, int chunkY, int chunkZ,
                                 float x, float y, float z, float yaw, float pitch,
                                 int length, int branch)
    {
        if (branch > 3) return;

        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;

        float radius = Random.Range(1.0f, 6.0f);

        for (int i = 0; i < length; i++)
        {
            float scale = Mathf.Sin(i * Mathf.PI / length);
            float currentRadius = radius * scale;

            // Calculate chunk-relative coordinates
            float relativeX = x - (chunkX * chunkSizeXZ);
            float relativeY = y - (chunkY * chunkSizeY);
            float relativeZ = z - (chunkZ * chunkSizeXZ);

            // Only carve if the cave center is within reasonable distance of this chunk
            if (relativeX >= -currentRadius && relativeX <= chunkSizeXZ - 1 + currentRadius &&
                relativeY >= -currentRadius && relativeY <= chunkSizeY - 1 + currentRadius &&
                relativeZ >= -currentRadius && relativeZ <= chunkSizeXZ - 1 + currentRadius)
            {
                // Carve sphere at current position
                int minX = Mathf.Max(0, Mathf.FloorToInt(relativeX - currentRadius));
                int maxX = Mathf.Min(chunkSizeXZ - 1, Mathf.CeilToInt(relativeX + currentRadius));
                int minY = Mathf.Max(0, Mathf.FloorToInt(relativeY - currentRadius));
                int maxY = Mathf.Min(chunkSizeY - 1, Mathf.CeilToInt(relativeY + currentRadius));
                int minZ = Mathf.Max(0, Mathf.FloorToInt(relativeZ - currentRadius));
                int maxZ = Mathf.Min(chunkSizeXZ - 1, Mathf.CeilToInt(relativeZ + currentRadius));

                for (int cx = minX; cx <= maxX; cx++)
                {
                    for (int cy = minY; cy <= maxY; cy++)
                    {
                        for (int cz = minZ; cz <= maxZ; cz++)
                        {
                            float dx = cx - relativeX;
                            float dy = cy - relativeY;
                            float dz = cz - relativeZ;

                            if (dx * dx + dy * dy + dz * dz < currentRadius * currentRadius)
                            {
                                int index = cx + cy * chunkSizeXZ * chunkSizeXZ + cz * chunkSizeXZ;

                                // Bounds check
                                if (index >= 0 && index < chunkData.Length)
                                {
                                    if (chunkData[index] != waterBlockID)
                                    {
                                        chunkData[index] = airBlockID;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Move position
            x += Mathf.Cos(yaw) * Mathf.Cos(pitch);
            y += Mathf.Sin(pitch);
            z += Mathf.Sin(yaw) * Mathf.Cos(pitch);

            // Random direction changes
            yaw += Random.Range(-0.2f, 0.2f);
            pitch = pitch * 0.9f + Random.Range(-0.1f, 0.1f);
            pitch = Mathf.Clamp(pitch, -0.7f, 0.7f);

            // Branching
            if (Random.value < 0.25f && branch < 3)
            {
                CarveCaveSystem(chunkData, chunkX, chunkY, chunkZ,
                               x, y, z,
                               Random.Range(0f, Mathf.PI * 2),
                               Random.Range(-0.5f, 0.5f),
                               length - i, branch + 1);
            }
        }
    }

    private void GenerateBedrock(ushort[] chunkData)
    {
        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;
        
        // Use chunk-specific seed for bedrock
        Random.InitState(HashChunkCoord(currentChunkX, currentChunkZ, _worldActualSeed + 2000));
        
        for (int x = 0; x < chunkSizeXZ; x++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                // Layer 0: Always bedrock
                chunkData[x + 0 * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ] = bedrockBlockID;
                
                // Layers 1-4: Random bedrock (only if chunk is tall enough)
                for (int y = 1; y <= 4 && y < chunkSizeY; y++)
                {
                    if (Random.value < (5 - y) / 5.0f)
                    {
                        chunkData[x + y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ] = bedrockBlockID;
                    }
                }
            }
        }
    }
}