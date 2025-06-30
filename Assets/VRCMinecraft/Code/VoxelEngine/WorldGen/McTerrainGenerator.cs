using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;
using VRRefAssist;
using System.Text;
using VRC.SDK3.Rendering;
using VRC.Udon.Common.Interfaces;

public enum GenerationState
{
    Idle,
    GeneratingHeightMap,
    ReadingHeightMap,
    FillingBlocks,
    ApplyingSurface,
    GeneratingCaves,
    GeneratingBedrock,
    Complete
}

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McTerrainGenerator : UdonSharpBehaviour
{
    [Header("GPU Acceleration")]
    [SerializeField] private Material terrainNoiseMaterial;
    [SerializeField] private Material heightProcessorMaterial;
    
    // Render textures for GPU processing
    private RenderTexture heightMapRT;
    private RenderTexture biomeDataRT;
    private RenderTexture tempRT;
    
    // GPU readback state
    private bool isReadbackPending = false;
    private Color32[] heightMapData;
    private Color32[] biomeMapData;
    
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
    private float time_Total, time_GPU, time_BlockFill, time_Surface, time_Caves, time_Bedrock;
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

    // Time-slicing state
    private GenerationState currentState = GenerationState.Idle;
    private int currentStep = 0;
    private int currentChunkX, currentChunkY, currentChunkZ;
    private ushort[] workingChunkData;
    private float[] workingHeightMap;
    private float[] workingBiomeTemp;
    private float[] workingBiomeHumidity;

    private StringBuilder logBuilder;

    public void InitializeGenerator(int seed)
    {
        float startTime = Time.realtimeSinceStartup;
        if (isInitialized) return;

        logBuilder = new StringBuilder(256);
        _worldActualSeed = seed;

        _placementRandState = (uint)seed;
        if (_placementRandState == 0) _placementRandState = 1;

        // Initialize GPU resources
        InitializeGPUResources();

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

    private void InitializeGPUResources()
    {
        // Create render textures for height map and biome data
        // Use ARGB32 format for compatibility with Color32 readback
        int textureSize = world.chunkSizeXZ;
        
        heightMapRT = new RenderTexture(textureSize, textureSize, 0, RenderTextureFormat.ARGB32);
        heightMapRT.filterMode = FilterMode.Point;
        heightMapRT.wrapMode = TextureWrapMode.Clamp;
        heightMapRT.Create();

        biomeDataRT = new RenderTexture(textureSize, textureSize, 0, RenderTextureFormat.ARGB32);
        biomeDataRT.filterMode = FilterMode.Point;
        biomeDataRT.wrapMode = TextureWrapMode.Clamp;
        biomeDataRT.Create();

        tempRT = new RenderTexture(textureSize, textureSize, 0, RenderTextureFormat.ARGB32);
        tempRT.filterMode = FilterMode.Point;
        tempRT.wrapMode = TextureWrapMode.Clamp;
        tempRT.Create();

        // Pre-allocate readback buffers
        heightMapData = new Color32[textureSize * textureSize];
        biomeMapData = new Color32[textureSize * textureSize];
    }

    // Main entry point - now returns null to indicate processing needed
    public ushort[] GenerateChunkData(int chunkX, int chunkY, int chunkZ)
    {
        StartChunkGeneration(chunkX, chunkY, chunkZ);
        return null;
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
        currentState = GenerationState.GeneratingHeightMap;
        currentStep = 0;
        isReadbackPending = false;

        int chunkSize = world.chunkSizeXZ * world.chunkSizeY * world.chunkSizeXZ;
        workingChunkData = new ushort[chunkSize];
        workingHeightMap = new float[world.chunkSizeXZ * world.chunkSizeXZ];
        workingBiomeTemp = new float[world.chunkSizeXZ * world.chunkSizeXZ];
        workingBiomeHumidity = new float[world.chunkSizeXZ * world.chunkSizeXZ];

#if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            time_Total = Time.realtimeSinceStartup;
            time_GPU = 0f;
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
            case GenerationState.GeneratingHeightMap:
                GenerateHeightMapGPU();
#if UNITY_EDITOR
                if(enableVerboseLogging) time_GPU = (Time.realtimeSinceStartup - stepTimer);
#endif
                currentState = GenerationState.ReadingHeightMap;
                return false;

            case GenerationState.ReadingHeightMap:
                if (!isReadbackPending)
                {
                    // Start GPU readback from the final biome texture
                    VRCAsyncGPUReadback.Request(biomeDataRT, 0, (IUdonEventReceiver)this);
                    isReadbackPending = true;
                }
                // Wait for readback to complete
                return false;

            case GenerationState.FillingBlocks:
                bool fillDone = StepBlockFilling();
#if UNITY_EDITOR
                if(enableVerboseLogging) time_BlockFill += (Time.realtimeSinceStartup - stepTimer);
#endif
                if (fillDone)
                {
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
                    logBuilder.AppendFormat("--- GPU Terrain Gen Timings for Chunk ({0},{1},{2}) ---", currentChunkX, currentChunkY, currentChunkZ).AppendLine();
                    logBuilder.AppendFormat("1. GPU Height Map Gen:      {0:F3} ms", time_GPU * 1000f).AppendLine();
                    logBuilder.AppendFormat("2. Block Fill (total):      {0:F3} ms", time_BlockFill * 1000f).AppendLine();
                    logBuilder.AppendFormat("3. Surface Decor (total):   {0:F3} ms", time_Surface * 1000f).AppendLine();
                    logBuilder.AppendFormat("4. Cave Gen:                {0:F3} ms", time_Caves).AppendLine();
                    logBuilder.AppendFormat("5. Bedrock Gen:             {0:F3} ms", time_Bedrock).AppendLine();
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

    // VRCAsyncGPUReadback callback
    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        isReadbackPending = false;
        
        if (!request.hasError && currentState == GenerationState.ReadingHeightMap)
        {
            // Get the height map data
            request.TryGetData(heightMapData);
            
#if UNITY_EDITOR
            if (enableVerboseLogging)
            {
                Debug.Log($"[McTerrainGenerator] GPU readback complete for chunk ({currentChunkX},{currentChunkY},{currentChunkZ})");
            }
#endif
            
            // Process the GPU data
            ProcessGPUHeightMapData();
            
            // Move to next state
            currentState = GenerationState.FillingBlocks;
            currentStep = 0;
        }
        else if (request.hasError)
        {
            Debug.LogError($"[McTerrainGenerator] GPU readback error for chunk ({currentChunkX},{currentChunkY},{currentChunkZ})");
        }
    }

    private void GenerateHeightMapGPU()
    {
        // Set shader parameters
        terrainNoiseMaterial.SetInt("_ChunkX", currentChunkX);
        terrainNoiseMaterial.SetInt("_ChunkY", currentChunkY); 
        terrainNoiseMaterial.SetInt("_ChunkZ", currentChunkZ);
        terrainNoiseMaterial.SetInt("_ChunkSizeXZ", world.chunkSizeXZ);
        terrainNoiseMaterial.SetInt("_ChunkSizeY", world.chunkSizeY);
        terrainNoiseMaterial.SetInt("_Seed", _worldActualSeed);
        terrainNoiseMaterial.SetFloat("_SeaLevel", seaLevel);
        terrainNoiseMaterial.SetFloat("_TerrainMultiplier", terrainHeightMultiplier);
        
        // Generate height map on GPU
        VRCGraphics.Blit(null, heightMapRT, terrainNoiseMaterial, 0);
        
        // Generate biome data
        VRCGraphics.Blit(heightMapRT, biomeDataRT, terrainNoiseMaterial, 1);
    }

    private void ProcessGPUHeightMapData()
    {
        // Convert GPU data to usable format
        int chunkArea = world.chunkSizeXZ * world.chunkSizeXZ;
        int textureWidth = heightMapRT.width;
        
        for (int z = 0; z < world.chunkSizeXZ; z++)
        {
            for (int x = 0; x < world.chunkSizeXZ; x++)
            {
                // Get pixel from the texture data array
                int textureIndex = z * textureWidth + x;
                Color32 pixel = heightMapData[textureIndex];
                
                // Local chunk index
                int chunkIndex = z * world.chunkSizeXZ + x;
                
                // R channel: normalized height (0-1)
                // G channel: temperature
                // B channel: humidity  
                // A channel: surface material hint
                
                float normalizedHeight = pixel.r / 255.0f;
                workingHeightMap[chunkIndex] = normalizedHeight * 255.0f; // Height in blocks
                
                workingBiomeTemp[chunkIndex] = pixel.g / 255.0f;
                workingBiomeHumidity[chunkIndex] = pixel.b / 255.0f;
            }
        }
    }

    private bool StepBlockFilling()
    {
        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;
        int columnsPerStep = 4;

        int totalColumns = chunkSizeXZ * chunkSizeXZ;
        int startColumn = currentStep * columnsPerStep;
        int endColumn = Mathf.Min(startColumn + columnsPerStep, totalColumns);

        for (int col = startColumn; col < endColumn; col++)
        {
            int x = col % chunkSizeXZ;
            int z = col / chunkSizeXZ;
            
            float surfaceHeight = workingHeightMap[x + z * chunkSizeXZ];
            int surfaceWorldY = Mathf.FloorToInt(surfaceHeight);

            // Fill blocks based on height
            for (int y = 0; y < chunkSizeY; y++)
            {
                int worldY = currentChunkY * chunkSizeY + y;
                int index = x + y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ;

                if (worldY < surfaceWorldY)
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
        int columnsPerStep = 4;

        int totalColumns = chunkSizeXZ * chunkSizeXZ;
        int startColumn = currentStep * columnsPerStep;
        int endColumn = Mathf.Min(startColumn + columnsPerStep, totalColumns);

        for (int col = startColumn; col < endColumn; col++)
        {
            int x = col % chunkSizeXZ;
            int z = col / chunkSizeXZ;

            int biomeIndex = x + z * chunkSizeXZ;
            float temp = workingBiomeTemp[biomeIndex];
            float humidity = workingBiomeHumidity[biomeIndex];

            // Simple biome determination
            bool isDesert = temp > 0.95f && humidity < 0.2f;
            bool isTundra = temp < 0.2f;

            int worldX = currentChunkX * chunkSizeXZ + x;
            int worldZ = currentChunkZ * chunkSizeXZ + z;

            // Use cached heightmap
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
                surfaceWorldY = Mathf.FloorToInt(workingHeightMap[biomeIndex]);
                globalHeightMap[heightMapIndex] = surfaceWorldY;
                globalHeightMapInitialized[heightMapIndex] = true;
            }

            if (surfaceWorldY == -1) continue;

            // Apply surface decoration
            for (int y = chunkSizeY - 1; y >= 0; y--)
            {
                int worldY = currentChunkY * chunkSizeY + y;
                int index = x + y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ;
                ushort currentBlock = workingChunkData[index];

                if (currentBlock != stoneBlockID) continue;
                
                int depthFromWorldSurface = surfaceWorldY - worldY;

                if (depthFromWorldSurface == 0)
                {
                    // Surface block
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
                        workingChunkData[index] = stoneBlockID;
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

        currentStep++;
        return endColumn >= totalColumns;
    }

    private bool StepCaveGeneration()
    {
        GenerateCaves(workingChunkData, currentChunkX, currentChunkY, currentChunkZ);
        return true;
    }

    // Keep existing cave generation methods unchanged
    private void GenerateCaves(ushort[] chunkData, int chunkX, int chunkY, int chunkZ)
    {
        int chunkSizeXZ = world.chunkSizeXZ;
        int chunkSizeY = world.chunkSizeY;
        
        int caveRadius = Mathf.Max(8, chunkSizeXZ / 2);
        
        for (int cx = -caveRadius; cx <= caveRadius; cx++)
        {
            for (int cz = -caveRadius; cz <= caveRadius; cz++)
            {
                int otherChunkX = chunkX + cx;
                int otherChunkZ = chunkZ + cz;
                
                int caveSeed = HashChunkCoord(otherChunkX, otherChunkZ, _worldActualSeed);
                
                Random.InitState(caveSeed);
                
                if (Random.value > caveFrequency) continue;
                
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

    private int HashChunkCoord(int x, int z, int seed)
    {
        int hash = -2128831035;
        hash = (hash ^ seed) * 16777619;
        hash = (hash ^ x) * 16777619;
        hash = (hash ^ (x >> 16)) * 16777619;
        hash = (hash ^ z) * 16777619;
        hash = (hash ^ (z >> 16)) * 16777619;
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

            float relativeX = x - (chunkX * chunkSizeXZ);
            float relativeY = y - (chunkY * chunkSizeY);
            float relativeZ = z - (chunkZ * chunkSizeXZ);

            if (relativeX >= -currentRadius && relativeX <= chunkSizeXZ - 1 + currentRadius &&
                relativeY >= -currentRadius && relativeY <= chunkSizeY - 1 + currentRadius &&
                relativeZ >= -currentRadius && relativeZ <= chunkSizeXZ - 1 + currentRadius)
            {
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

            x += Mathf.Cos(yaw) * Mathf.Cos(pitch);
            y += Mathf.Sin(pitch);
            z += Mathf.Sin(yaw) * Mathf.Cos(pitch);

            yaw += Random.Range(-0.2f, 0.2f);
            pitch = pitch * 0.9f + Random.Range(-0.1f, 0.1f);
            pitch = Mathf.Clamp(pitch, -0.7f, 0.7f);

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
        
        Random.InitState(HashChunkCoord(currentChunkX, currentChunkZ, _worldActualSeed + 2000));
        
        for (int x = 0; x < chunkSizeXZ; x++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                chunkData[x + 0 * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ] = bedrockBlockID;
                
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

    void OnDestroy()
    {
        // Clean up GPU resources
        if (heightMapRT != null) 
        {
            heightMapRT.Release();
            heightMapRT = null;
        }
        if (biomeDataRT != null) 
        {
            biomeDataRT.Release();
            biomeDataRT = null;
        }
        if (tempRT != null) 
        {
            tempRT.Release();
            tempRT = null;
        }
    }
}