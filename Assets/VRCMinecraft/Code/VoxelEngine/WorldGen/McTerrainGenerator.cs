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

    [SerializeField, FindObjectOfType(true)]
    private McWorld world;

    [SerializeField, FindObjectOfType(true)]
    private McBlockTypeManager blockTypeManager;

    private bool isInitialized = false;
    private int _worldActualSeed;
    private uint _placementRandState;

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

#if UNITY_EDITOR
    [HideInInspector] public bool enableVerboseLogging = true;
#endif
    private StringBuilder logBuilder;

    public void InitializeGenerator(int seed)
    {
        float startTime = Time.realtimeSinceStartup;
        if (isInitialized) return;

        logBuilder = new StringBuilder(256);
        _worldActualSeed = seed;

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
    }

    // Process one step of generation - returns true when complete
    public bool StepChunkGeneration(out ushort[] completedData)
    {
        completedData = null;

        switch (currentState)
        {
            case GenerationState.GeneratingDensity:
                if (StepDensityGeneration())
                {
                    currentState = GenerationState.FillingBlocks;
                    currentStep = 0;
                }
                return false;

            case GenerationState.FillingBlocks:
                if (StepBlockFilling())
                {
                    currentState = GenerationState.ApplyingSurface;
                    currentStep = 0;
                }
                return false;

            case GenerationState.ApplyingSurface:
                ApplySurfaceDecoration(workingChunkData, currentChunkX, currentChunkY, currentChunkZ, workingHeightMap);
                currentState = generateCaves ? GenerationState.GeneratingCaves : GenerationState.GeneratingBedrock;
                currentStep = 0;
                return false;

            case GenerationState.GeneratingCaves:
                if (StepCaveGeneration())
                {
                    currentState = GenerationState.GeneratingBedrock;
                    currentStep = 0;
                }
                return false;

            case GenerationState.GeneratingBedrock:
                if (generateBedrock && currentChunkY == 0)
                {
                    GenerateBedrock(workingChunkData);
                }
                currentState = GenerationState.Complete;
                completedData = workingChunkData;
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

        if (currentStep == 0)
        {
            // Initialize density field generation
            // Calculate sample dimensions based on chunk size
            // Use fixed sample spacing to ensure consistent alignment across chunks
            workingSampleXZ = 5; // Fixed for consistent chunk boundaries
            workingSampleY = Mathf.Max(5, (chunkSizeY / 8) + 1); // Scale with chunk height
            
            workingSamples = new float[workingSampleXZ * workingSampleY * workingSampleXZ];
            workingDensityField = new float[chunkSizeXZ * chunkSizeY * chunkSizeXZ];
            currentStep++;
            return false;
        }

        // Generate samples in steps
        int samplesPerStep = 64; // Process 64 sample points per step
        int totalSamples = workingSampleXZ * workingSampleY * workingSampleXZ;
        int startIndex = (currentStep - 1) * samplesPerStep;
        int endIndex = Mathf.Min(startIndex + samplesPerStep, totalSamples);

        for (int i = startIndex; i < endIndex; i++)
        {
            // Convert linear index to 3D coordinates
            int sx = i % workingSampleXZ;
            int sy = (i / workingSampleXZ) % workingSampleY;
            int sz = i / (workingSampleXZ * workingSampleY);

            // FIXED: Use consistent 4-unit spacing for all chunks
            // This ensures samples at chunk boundaries align perfectly
            float worldX = (currentChunkX * chunkSizeXZ) + (sx * 4.0f);
            float worldY = (currentChunkY * chunkSizeY) + (sy * (chunkSizeY / 4.0f));
            float worldZ = (currentChunkZ * chunkSizeXZ) + (sz * 4.0f);

            // Generate noise value
            float density = GenerateDensityAtPoint(worldX, worldY, worldZ);
            workingSamples[sx + sy * workingSampleXZ * workingSampleXZ + sz * workingSampleXZ] = density;
        }

        currentStep++;

        // Check if we're done with samples
        if (endIndex >= totalSamples)
        {
            // Interpolate samples to full resolution
            InterpolateDensityField();
            return true;
        }

        return false;
    }

    private float GenerateDensityAtPoint(float worldX, float worldY, float worldZ)
    {
        if (mainNoise == null || minLimitNoise == null || maxLimitNoise == null || 
            selectorNoise == null || depthNoise == null)
        {
            return 0;
        }

        // Beta 1.7.3 noise combination
        float mainVal = mainNoise.GenerateNoise(
            worldX / 684.412f, 
            worldY / 684.412f, 
            worldZ / 684.412f, 
            16, 0.5f
        );
        
        float minVal = minLimitNoise.GenerateNoise(
            worldX / 512f, 
            worldY / 512f, 
            worldZ / 512f, 
            16, 0.5f
        );
        
        float maxVal = maxLimitNoise.GenerateNoise(
            worldX / 512f, 
            worldY / 512f, 
            worldZ / 512f, 
            16, 0.5f
        );
        
        float selectorVal = selectorNoise.GenerateNoise(
            worldX / 512f, 
            worldY / 512f, 
            worldZ / 512f, 
            8, 0.5f
        );
        
        // Depth-based density reduction
        float depth = depthNoise.GenerateNoise(
            worldX / 200f, 
            0, 
            worldZ / 200f, 
            16, 0.5f
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

        // FIXED: Use exact spacing that matches our sample generation
        float sampleSpacingX = 4.0f;
        float sampleSpacingY = chunkSizeY / 4.0f;
        float sampleSpacingZ = 4.0f;

        for (int x = 0; x < chunkSizeXZ; x++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                for (int y = 0; y < chunkSizeY; y++)
                {
                    // Find sample positions
                    float sx = x / sampleSpacingX;
                    float sy = y / sampleSpacingY;
                    float sz = z / sampleSpacingZ;
                    
                    int sx0 = Mathf.FloorToInt(sx);
                    int sy0 = Mathf.FloorToInt(sy);
                    int sz0 = Mathf.FloorToInt(sz);
                    
                    // FIXED: Clamp to valid sample indices
                    sx0 = Mathf.Clamp(sx0, 0, workingSampleXZ - 2);
                    sy0 = Mathf.Clamp(sy0, 0, workingSampleY - 2);
                    sz0 = Mathf.Clamp(sz0, 0, workingSampleXZ - 2);
                    
                    int sx1 = sx0 + 1;
                    int sy1 = sy0 + 1;
                    int sz1 = sz0 + 1;
                    
                    // Calculate interpolation factors
                    float fx = Mathf.Clamp01(sx - sx0);
                    float fy = Mathf.Clamp01(sy - sy0);
                    float fz = Mathf.Clamp01(sz - sz0);
                    
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

    private bool StepCaveGeneration()
    {
        // For simplicity, do all cave generation in one step
        // Could be further subdivided if needed
        GenerateCaves(workingChunkData, currentChunkX, currentChunkY, currentChunkZ);
        return true;
    }

    // Keep existing methods below unchanged...
    private void ApplySurfaceDecoration(ushort[] chunkData, int chunkX, int chunkY, int chunkZ, float[] heightMap)
    {
        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;
        
        // Generate biome data for this chunk
        GenerateBiomeData(chunkX, chunkZ);
        
        for (int x = 0; x < chunkSizeXZ; x++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                int biomeIndex = x + z * chunkSizeXZ;
                float temp = biomeTemperature[biomeIndex];
                float humidity = biomeHumidity[biomeIndex];
                
                // Simple biome determination
                bool isDesert = temp > 0.95f && humidity < 0.2f;
                bool isTundra = temp < 0.2f;
                
                // Calculate WORLD position for this column
                int worldX = chunkX * chunkSizeXZ + x;
                int worldZ = chunkZ * chunkSizeXZ + z;
                
                // Find the highest solid block in this column
                int surfaceWorldY = -1;
                bool foundSurface = false;
                
                for (int y = chunkSizeY - 1; y >= 0; y--)
                {
                    int index = x + y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ;
                    if (chunkData[index] == stoneBlockID)
                    {
                        surfaceWorldY = chunkY * chunkSizeY + y;
                        foundSurface = true;
                        break;
                    }
                }
                
                if (!foundSurface) continue;
                
                // Apply surface decoration based on WORLD surface height
                for (int y = chunkSizeY - 1; y >= 0; y--)
                {
                    int worldY = chunkY * chunkSizeY + y;
                    int index = x + y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ;
                    
                    if (chunkData[index] == stoneBlockID)
                    {
                        int depthFromWorldSurface = surfaceWorldY - worldY;
                        
                        if (depthFromWorldSurface == 0)
                        {
                            // This is the actual world surface block
                            if (surfaceWorldY < seaLevel - 1)
                            {
                                chunkData[index] = sandBlockID;
                            }
                            else if (isDesert)
                            {
                                chunkData[index] = sandBlockID;
                            }
                            else if (isTundra && surfaceWorldY > seaLevel + 20)
                            {
                                chunkData[index] = stoneBlockID; // Snow would go here
                            }
                            else
                            {
                                chunkData[index] = grassBlockID;
                            }
                        }
                        else if (depthFromWorldSurface > 0 && depthFromWorldSurface < surfaceDepth)
                        {
                            // Subsurface blocks
                            if (isDesert && depthFromWorldSurface < 4)
                            {
                                chunkData[index] = sandBlockID;
                            }
                            else if (surfaceWorldY < seaLevel - 1)
                            {
                                if (depthFromWorldSurface < 3)
                                {
                                    chunkData[index] = sandBlockID;
                                }
                                else
                                {
                                    chunkData[index] = sandStoneBlockID;
                                }
                            }
                            else
                            {
                                chunkData[index] = dirtBlockID;
                            }
                        }
                    }
                }
            }
        }
    }

    private void GenerateBiomeData(int chunkX, int chunkZ)
    {
        int chunkSizeXZ = world.chunkSizeXZ;
        
        // Save current random state
        Random.State savedState = Random.state;
        
        // Use deterministic seed for biome generation
        Random.InitState(HashChunkCoord(chunkX, chunkZ, _worldActualSeed + 1000));
        
        // Generate temperature and humidity maps
        for (int x = 0; x < chunkSizeXZ; x++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                float worldX = chunkX * chunkSizeXZ + x;
                float worldZ = chunkZ * chunkSizeXZ + z;
                
                // Temperature noise
                float temp = Mathf.PerlinNoise(worldX * 0.025f + 0.5f, worldZ * 0.025f + 0.5f);
                temp = Mathf.Clamp01(temp);
                
                // Humidity noise  
                float humidity = Mathf.PerlinNoise(worldX * 0.05f + 1000.5f, worldZ * 0.05f + 1000.5f);
                humidity = Mathf.Clamp01(humidity * temp);
                
                biomeTemperature[x + z * chunkSizeXZ] = temp;
                biomeHumidity[x + z * chunkSizeXZ] = humidity;
            }
        }
        
        // Restore random state
        Random.state = savedState;
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
        int hash = 2166136261; // FNV offset basis
            
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
        
        // Save current random state
        Random.State savedState = Random.state;
        
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
        
        // Restore random state
        Random.state = savedState;
    }
}