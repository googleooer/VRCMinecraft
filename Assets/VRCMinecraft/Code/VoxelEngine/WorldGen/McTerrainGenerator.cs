using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRRefAssist;
using System.Text;

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

        // Initialize biome arrays (16x16 for each chunk)
        biomeTemperature = new float[256];
        biomeHumidity = new float[256];

        isInitialized = true;

#if UNITY_EDITOR
        if (enableVerboseLogging) {
            logBuilder.Clear();
            logBuilder.AppendFormat("[McTerrainGenerator.InitializeGenerator] Complete. Seed: {0}. Time: {1:F2} ms.", seed, (Time.realtimeSinceStartup - startTime) * 1000f);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }

    public ushort[] GenerateChunkData(int chunkX, int chunkY, int chunkZ)
    {
        // Ensure we're initialized
        if (!isInitialized)
        {
            Debug.LogError("[McTerrainGenerator] Not initialized! Call InitializeGenerator first.");
            return new ushort[world.chunkSizeXZ * world.chunkSizeY * world.chunkSizeXZ];
        }

        int chunkSizeXZ = world.chunkSizeXZ; // 16
        int chunkSizeY = world.chunkSizeY;   // 64
        
        ushort[] chunkData = new ushort[chunkSizeXZ * chunkSizeY * chunkSizeXZ];
        
        // Generate height map and density field
        float[] heightMap = new float[chunkSizeXZ * chunkSizeXZ];
        float[] densityField = GenerateDensityField(chunkX, chunkY, chunkZ);
        
        // First pass: Generate base terrain
        for (int x = 0; x < chunkSizeXZ; x++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                int worldX = chunkX * chunkSizeXZ + x;
                int worldZ = chunkZ * chunkSizeXZ + z;
                
                // Calculate surface height from density field - look for the highest solid block
                int surfaceWorldY = -1;

                // We need to check the density field to find the actual surface
                // The surface is where we transition from solid (positive density) to air (negative density)
                for (int y = chunkSizeY - 1; y >= 0; y--)
                {
                    int index3D = x + y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ;
                    int worldY = chunkY * chunkSizeY + y;
                    
                    if (densityField[index3D] > 0)
                    {
                        // Check if the block above is air (or we're at the top of the chunk)
                        if (y == chunkSizeY - 1)
                        {
                            // We're at the top of the chunk, this might be the surface
                            // but the actual surface could be in the chunk above
                            surfaceWorldY = worldY;
                        }
                        else
                        {
                            int indexAbove = x + (y + 1) * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ;
                            if (densityField[indexAbove] <= 0)
                            {
                                // Found the surface - solid below, air above
                                surfaceWorldY = worldY;
                                break;
                            }
                        }
                    }
                }

                // Store the world Y coordinate of the surface
                heightMap[x + z * chunkSizeXZ] = surfaceWorldY;
                
                // Fill blocks based on density
                for (int y = 0; y < chunkSizeY; y++)
                {
                    int worldY = chunkY * chunkSizeY + y;
                    int index = x + y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ;
                    
                    if (densityField[index] > 0)
                    {
                        chunkData[index] = stoneBlockID;
                    }
                    else if (worldY <= seaLevel)
                    {
                        chunkData[index] = waterBlockID;
                    }
                    else
                    {
                        chunkData[index] = airBlockID;
                    }
                }
            }
        }
        
        // Second pass: Surface decoration
        ApplySurfaceDecoration(chunkData, chunkX, chunkY, chunkZ, heightMap);
        
        // Third pass: Cave generation
        if (generateCaves)
        {
            GenerateCaves(chunkData, chunkX, chunkY, chunkZ);
        }
        
        // Fourth pass: Bedrock layer
        if (generateBedrock && chunkY == 0)
        {
            GenerateBedrock(chunkData);
        }
        
        return chunkData;
    }

    private float[] GenerateDensityField(int chunkX, int chunkY, int chunkZ)
    {
        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;
        
        // Use sparse sampling and interpolation (5x33x5 -> 16x64x16)
        int sampleXZ = 5;
        int sampleY = 33;
        float[] samples = new float[sampleXZ * sampleY * sampleXZ];
        
        // Check if noise generators are available
        if (mainNoise == null || minLimitNoise == null || maxLimitNoise == null || 
            selectorNoise == null || depthNoise == null)
        {
            Debug.LogError("[McTerrainGenerator] Noise generators not assigned!");
            return new float[chunkSizeXZ * chunkSizeY * chunkSizeXZ];
        }
        
        // Generate sparse samples
        for (int sx = 0; sx < sampleXZ; sx++)
        {
            for (int sz = 0; sz < sampleXZ; sz++)
            {
                for (int sy = 0; sy < sampleY; sy++)
                {
                    float worldX = (chunkX * chunkSizeXZ) + (sx * 4);
                    float worldY = (chunkY * chunkSizeY) + (sy * 2);
                    float worldZ = (chunkZ * chunkSizeXZ) + (sz * 4);
                    
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
                    
                    samples[sx + sy * sampleXZ * sampleXZ + sz * sampleXZ] = density;
                }
            }
        }
        
        // Interpolate to full resolution
        float[] densityField = new float[chunkSizeXZ * chunkSizeY * chunkSizeXZ];
        
        for (int x = 0; x < chunkSizeXZ; x++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                for (int y = 0; y < chunkSizeY; y++)
                {
                    // Find sample positions
                    float sx = x / 4.0f;
                    float sy = y / 2.0f;
                    float sz = z / 4.0f;
                    
                    int sx0 = Mathf.FloorToInt(sx);
                    int sy0 = Mathf.FloorToInt(sy);
                    int sz0 = Mathf.FloorToInt(sz);
                    
                    int sx1 = Mathf.Min(sx0 + 1, sampleXZ - 1);
                    int sy1 = Mathf.Min(sy0 + 1, sampleY - 1);
                    int sz1 = Mathf.Min(sz0 + 1, sampleXZ - 1);
                    
                    float fx = sx - sx0;
                    float fy = sy - sy0;
                    float fz = sz - sz0;
                    
                    // Trilinear interpolation
                    float v000 = samples[sx0 + sy0 * sampleXZ * sampleXZ + sz0 * sampleXZ];
                    float v100 = samples[sx1 + sy0 * sampleXZ * sampleXZ + sz0 * sampleXZ];
                    float v010 = samples[sx0 + sy1 * sampleXZ * sampleXZ + sz0 * sampleXZ];
                    float v110 = samples[sx1 + sy1 * sampleXZ * sampleXZ + sz0 * sampleXZ];
                    float v001 = samples[sx0 + sy0 * sampleXZ * sampleXZ + sz1 * sampleXZ];
                    float v101 = samples[sx1 + sy0 * sampleXZ * sampleXZ + sz1 * sampleXZ];
                    float v011 = samples[sx0 + sy1 * sampleXZ * sampleXZ + sz1 * sampleXZ];
                    float v111 = samples[sx1 + sy1 * sampleXZ * sampleXZ + sz1 * sampleXZ];
                    
                    float v00 = Mathf.Lerp(v000, v100, fx);
                    float v10 = Mathf.Lerp(v010, v110, fx);
                    float v01 = Mathf.Lerp(v001, v101, fx);
                    float v11 = Mathf.Lerp(v011, v111, fx);
                    
                    float v0 = Mathf.Lerp(v00, v10, fy);
                    float v1 = Mathf.Lerp(v01, v11, fy);
                    
                    densityField[x + y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ] = Mathf.Lerp(v0, v1, fz);
                }
            }
        }
        
        return densityField;
    }

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
                
                // Find the highest solid block in this column across ALL chunks vertically
                int surfaceWorldY = -1;
                bool foundSurface = false;
                
                // First, check current chunk for surface
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
                
                // If no surface found in this chunk, it might be above or below
                // For chunks above the surface, we don't need to do anything
                // For chunks below the surface, all stone remains stone
                if (!foundSurface)
                {
                    // Check if this chunk is entirely below the terrain
                    // by checking if the top block is stone
                    int topIndex = x + (chunkSizeY - 1) * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ;
                    if (chunkData[topIndex] == stoneBlockID)
                    {
                        // This chunk is entirely underground, no surface decoration needed
                        continue;
                    }
                    else
                    {
                        // This chunk is entirely above ground, no surface decoration needed
                        continue;
                    }
                }
                
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
                            else if (surfaceWorldY < seaLevel - 1) // Near water
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
                        // Else it remains stone (deeper than surface depth)
                    }
                }
            }
        }
    }

    private void GenerateBiomeData(int chunkX, int chunkZ)
    {
        // Generate temperature and humidity maps
        for (int x = 0; x < 16; x++)
        {
            for (int z = 0; z < 16; z++)
            {
                float worldX = chunkX * 16 + x;
                float worldZ = chunkZ * 16 + z;
                
                // Temperature noise
                float temp = Mathf.PerlinNoise(worldX * 0.025f, worldZ * 0.025f);
                temp = Mathf.Clamp01(temp);
                
                // Humidity noise
                float humidity = Mathf.PerlinNoise(worldX * 0.05f + 1000, worldZ * 0.05f + 1000);
                humidity = Mathf.Clamp01(humidity * temp); // Humidity affected by temperature
                
                biomeTemperature[x + z * 16] = temp;
                biomeHumidity[x + z * 16] = humidity;
            }
        }
    }

    private void GenerateCaves(ushort[] chunkData, int chunkX, int chunkY, int chunkZ)
    {
        Random.InitState(_worldActualSeed + chunkX * 341873128 + chunkZ * 132897987);
        
        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;
        
        // Check 8x8 chunk area for cave systems that might intersect this chunk
        for (int cx = -8; cx <= 8; cx++)
        {
            for (int cz = -8; cz <= 8; cz++)
            {
                int otherChunkX = chunkX + cx;
                int otherChunkZ = chunkZ + cz;
                
                Random.InitState(_worldActualSeed + otherChunkX * 341873128 + otherChunkZ * 132897987);
                
                if (Random.value > caveFrequency) continue;
                
                // Generate 1-3 cave systems
                int caveCount = Random.Range(1, 4);
                
                for (int i = 0; i < caveCount; i++)
                {
                    float startX = otherChunkX * 16 + Random.Range(0, 16);
                    float startY = Random.Range(32, 96);
                    float startZ = otherChunkZ * 16 + Random.Range(0, 16);
                    
                    float yaw = Random.Range(0f, Mathf.PI * 2);
                    float pitch = Random.Range(-0.5f, 0.5f);
                    
                    CarveCaveSystem(chunkData, chunkX, chunkY, chunkZ, 
                                   startX, startY, startZ, yaw, pitch, 
                                   Random.Range(85, 112), 0);
                }
            }
        }
    }

    private void CarveCaveSystem(ushort[] chunkData, int chunkX, int chunkY, int chunkZ,
                                 float x, float y, float z, float yaw, float pitch, 
                                 int length, int branch)
    {
        if (branch > 3) return; // Max branch depth
        
        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;
        
        float radius = Random.Range(1.0f, 6.0f);
        
        for (int i = 0; i < length; i++)
        {
            // Cave expansion/contraction
            float scale = Mathf.Sin(i * Mathf.PI / length);
            float currentRadius = radius * scale;
            
            // Calculate chunk-relative coordinates
            float relativeX = x - (chunkX * 16);
            float relativeY = y - (chunkY * 64);
            float relativeZ = z - (chunkZ * 16);
            
            // Only carve if the cave center is within reasonable distance of this chunk
            if (relativeX >= -currentRadius && relativeX <= 15 + currentRadius &&
                relativeY >= -currentRadius && relativeY <= 63 + currentRadius &&
                relativeZ >= -currentRadius && relativeZ <= 15 + currentRadius)
            {
                // Carve sphere at current position
                int minX = Mathf.Max(0, Mathf.FloorToInt(relativeX - currentRadius));
                int maxX = Mathf.Min(15, Mathf.CeilToInt(relativeX + currentRadius));
                int minY = Mathf.Max(0, Mathf.FloorToInt(relativeY - currentRadius));
                int maxY = Mathf.Min(63, Mathf.CeilToInt(relativeY + currentRadius));
                int minZ = Mathf.Max(0, Mathf.FloorToInt(relativeZ - currentRadius));
                int maxZ = Mathf.Min(15, Mathf.CeilToInt(relativeZ + currentRadius));
                
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
                                
                                // Bounds check to prevent array out of bounds
                                if (index >= 0 && index < chunkData.Length)
                                {
                                    // Don't carve through water
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
            
            // Clamp pitch to prevent vertical caves
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
        
        Random.InitState(_worldActualSeed);
        
        for (int x = 0; x < chunkSizeXZ; x++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                // Layer 0: Always bedrock
                chunkData[x + 0 * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ] = bedrockBlockID;
                
                // Layers 1-4: Random bedrock
                for (int y = 1; y <= 4; y++)
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