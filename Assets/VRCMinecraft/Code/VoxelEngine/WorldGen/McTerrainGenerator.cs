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
    GeneratingBlocksGPU,
    ReadingBlocksGPU,
    ApplyingGPUData,
    GeneratingCaves,
    GeneratingBedrock,
    Complete
}

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McTerrainGenerator : UdonSharpBehaviour
{
    [Header("GPU Acceleration")]
    [Tooltip("The material/shader that generates the final block ID data for a whole chunk.")]
    [SerializeField] private Material terrainComputeMaterial;
    
    // A single render texture to hold the entire chunk's block data.
    private RenderTexture chunkDataRT;
    
    // Permutation texture for noise generation
    private Texture2D permutationTexture;
    
    // GPU readback state
    private bool isReadbackPending = false;
    private byte[] gpuBlockData;
    
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
    private float time_Total, time_GPU, time_DataCopy, time_Caves, time_Bedrock;
#endif

    [SerializeField, FindObjectOfType(true)]
    private McWorld world;

    [SerializeField, FindObjectOfType(true)]
    private McBlockTypeManager blockTypeManager;

    private bool isInitialized = false;
    private int _worldActualSeed;
    private int _placementRandState;
    
    // Time-slicing state
    private GenerationState currentState = GenerationState.Idle;
    private int currentChunkX, currentChunkY, currentChunkZ;
    private ushort[] workingChunkData;
    
    private StringBuilder logBuilder;

    public void InitializeGenerator(int seed)
    {
        float startTime = Time.realtimeSinceStartup;
        if (isInitialized) return;

        logBuilder = new StringBuilder(256);
        _worldActualSeed = seed;

        _placementRandState = seed;
        if (_placementRandState == 0) _placementRandState = 1;
        
        InitializeGPUResources();
        CreatePermutationTexture(seed);
        
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
        // The texture width is unrolled to contain all XZ data for a layer.
        int textureWidth = world.chunkSizeXZ * world.chunkSizeXZ; // e.g., 16 * 16 = 256
        int textureHeight = world.chunkSizeY;                     // e.g., 16

        // Create a single render texture to hold all block IDs for the chunk.
        // Format is R8, a single 8-bit channel, perfect for storing a byte (0-255).
        chunkDataRT = new RenderTexture(textureWidth, textureHeight, 0, RenderTextureFormat.R8);
        chunkDataRT.filterMode = FilterMode.Point;
        chunkDataRT.wrapMode = TextureWrapMode.Clamp;
        chunkDataRT.Create();
        
        // Pre-allocate the readback buffer. Its size is simply width * height for an R8 texture.
        int bufferSize = textureWidth * textureHeight; // e.g., 256 * 16 = 4096
        gpuBlockData = new byte[bufferSize];
    }

    private void CreatePermutationTexture(int seed)
    {
        // Generate the permutation table using the seed
        byte[] permTable = McUtils.GetPermutationTable(seed);
        
        // Create a 256x2 R8 texture
        permutationTexture = new Texture2D(256, 2, TextureFormat.R8, false);
        permutationTexture.filterMode = FilterMode.Point;
        permutationTexture.wrapMode = TextureWrapMode.Clamp;
        
        // Fill the texture with the permutation data
        // First row (y=0): values 0-255
        // Second row (y=1): duplicate values 256-511
        Color32[] pixels = new Color32[512];
        for (int i = 0; i < 512; i++)
        {
            pixels[i] = new Color32(permTable[i], 0, 0, 255);
        }
        
        permutationTexture.SetPixels32(pixels);
        permutationTexture.Apply(false, true); // Apply changes and make read-only
        
#if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            Debug.Log($"[McTerrainGenerator] Created permutation texture for seed {seed}. First few values: {permTable[0]}, {permTable[1]}, {permTable[2]}, {permTable[3]}");
        }
#endif
    }

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
        currentState = GenerationState.GeneratingBlocksGPU;
        isReadbackPending = false;

        int chunkSize = world.chunkSizeXZ * world.chunkSizeY * world.chunkSizeXZ;
        workingChunkData = new ushort[chunkSize];

#if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            time_Total = Time.realtimeSinceStartup;
            time_GPU = 0f;
            time_DataCopy = 0f;
            time_Caves = 0f;
            time_Bedrock = 0f;
        }
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
            case GenerationState.GeneratingBlocksGPU:
                GenerateBlocksGPU();
#if UNITY_EDITOR
                if(enableVerboseLogging) time_GPU = (Time.realtimeSinceStartup - stepTimer);
#endif
                currentState = GenerationState.ReadingBlocksGPU;
                return false;

            case GenerationState.ReadingBlocksGPU:
                if (!isReadbackPending)
                {
                    VRCAsyncGPUReadback.Request(chunkDataRT, 0, TextureFormat.R8, (IUdonEventReceiver)this);
                    isReadbackPending = true;
                }
                // Wait for readback to complete via the OnAsyncGpuReadbackComplete callback
                return false;

            case GenerationState.ApplyingGPUData:
                ProcessGPUData();
#if UNITY_EDITOR
                if (enableVerboseLogging) time_DataCopy = (Time.realtimeSinceStartup - stepTimer);
#endif
                currentState = generateCaves ? GenerationState.GeneratingCaves : GenerationState.GeneratingBedrock;
                return false;

            case GenerationState.GeneratingCaves:
                // Cave generation is complex, so for now we'll do it in one step.
                // This could be time-sliced in the future if needed.
                if (generateCaves)
                {
                    GenerateCaves(workingChunkData, currentChunkX, currentChunkY, currentChunkZ);
                }
#if UNITY_EDITOR
                if (enableVerboseLogging) time_Caves = (Time.realtimeSinceStartup - stepTimer) * 1000f;
#endif
                currentState = GenerationState.GeneratingBedrock;
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
                return false;

            case GenerationState.Complete:
                completedData = workingChunkData;
#if UNITY_EDITOR
                if (enableVerboseLogging)
                {
                    float totalTime = (Time.realtimeSinceStartup - time_Total) * 1000f;
                    logBuilder.Clear();
                    logBuilder.AppendFormat("--- GPU Terrain Gen Timings for Chunk ({0},{1},{2}) ---", currentChunkX, currentChunkY, currentChunkZ).AppendLine();
                    logBuilder.AppendFormat("1. GPU Block Gen:           {0:F3} ms", time_GPU * 1000f).AppendLine();
                    logBuilder.AppendFormat("2. Data Copy:               {0:F3} ms", time_DataCopy * 1000f).AppendLine();
                    logBuilder.AppendFormat("3. Cave Gen:                {0:F3} ms", time_Caves).AppendLine();
                    logBuilder.AppendFormat("4. Bedrock Gen:             {0:F3} ms", time_Bedrock).AppendLine();
                    logBuilder.AppendFormat("=> Total Actual:            {0:F3} ms", totalTime).AppendLine();
                    Debug.Log(logBuilder.ToString());
                }
#endif
                return true;

            default:
                return true;
        }
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        isReadbackPending = false;
        
        if (!request.hasError && currentState == GenerationState.ReadingBlocksGPU)
        {
            request.TryGetData(gpuBlockData);
            
#if UNITY_EDITOR
            if (enableVerboseLogging)
            {
                Debug.Log($"[McTerrainGenerator] GPU readback complete for chunk ({currentChunkX},{currentChunkY},{currentChunkZ})");
                Debug.Log($"GPU Block Data at index 0: {gpuBlockData[0]}, total array length: {gpuBlockData.Length}");
            }
#endif
            
            // Move to the next state to process the data in the main time-sliced loop
            currentState = GenerationState.ApplyingGPUData;
        }
        else if (request.hasError)
        {
            Debug.LogError($"[McTerrainGenerator] GPU readback error for chunk ({currentChunkX},{currentChunkY},{currentChunkZ})");
            currentState = GenerationState.Complete; // Fail gracefully
        }
    }

    private void GenerateBlocksGPU()
    {
        // Set shader parameters based on the properties defined in the shader
        // Assuming the terrainComputeMaterial will need these to generate world-aligned terrain
        terrainComputeMaterial.SetInt("_Udon_ChunkX", currentChunkX);
        terrainComputeMaterial.SetInt("_Udon_ChunkY", currentChunkY);
        terrainComputeMaterial.SetInt("_Udon_ChunkZ", currentChunkZ);
        
        // These properties are already in your shader
        terrainComputeMaterial.SetInt("_Udon_WorldSeed", _worldActualSeed);
        terrainComputeMaterial.SetFloat("_Udon_SeaLevel", seaLevel);
        terrainComputeMaterial.SetFloat("_Udon_TerrainMultiplier", terrainHeightMultiplier);
        terrainComputeMaterial.SetInt("_Udon_ChunkSizeXZ", world.chunkSizeXZ);
        terrainComputeMaterial.SetInt("_Udon_ChunkSizeY", world.chunkSizeY);
        
        // Set the hardcoded parameters from Table 2.1 in the document.
        // These could be exposed as public fields if you want to tweak them in the inspector.
        terrainComputeMaterial.SetInt("_Udon_MinMaxLimitNoise_Octaves", 16);
        terrainComputeMaterial.SetVector("_Udon_MinMaxLimitNoise_Scale", new Vector4(684.412f, 684.412f, 0, 0));
        terrainComputeMaterial.SetInt("_Udon_MainNoise_Octaves", 8);
        terrainComputeMaterial.SetVector("_Udon_MainNoise_Scale", new Vector4(80.0f, 160.0f, 0, 0));
        terrainComputeMaterial.SetInt("_Udon_ScaleNoise_Octaves", 10);
        terrainComputeMaterial.SetVector("_Udon_ScaleNoise_Scale", new Vector4(80.0f, 160.0f, 0, 0));
        terrainComputeMaterial.SetInt("_Udon_SurfaceNoise_Octaves", 4);
        terrainComputeMaterial.SetVector("_Udon_SurfaceNoise_Scale", new Vector4(200.0f, 200.0f, 0, 0));

        // Set the permutation texture for noise generation
        terrainComputeMaterial.SetTexture("_Udon_PermutationTex", permutationTexture);
        
        // Generate all block data for the chunk in a single GPU pass
        VRCGraphics.Blit(null, chunkDataRT, terrainComputeMaterial, 0);
    }

    /// <summary>
    /// Processes the raw byte data from the GPU into the final ushort array for the chunk.
    /// Since the GPU now generates final block IDs, this is a direct 1-to-1 copy.
    /// </summary>
    private void ProcessGPUData()
    {
        Array.Copy(gpuBlockData, workingChunkData, gpuBlockData.Length);
    }

    // This method is now obsolete as the GPU handles all block filling.
    // private bool StepBlockFilling() { ... }

    // This method is now obsolete as the GPU handles all surface decoration.
    // private bool StepSurfaceDecoration() { ... }

    private void GenerateCaves(ushort[] chunkData, int chunkX, int chunkY, int chunkZ)
    {
        /*
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
        */
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
        if (chunkDataRT != null) 
        {
            chunkDataRT.Release();
            chunkDataRT = null;
        }
        
        if (permutationTexture != null)
        {
        #if UNITY_EDITOR && !COMPILER_UDONSHARP
            if (Application.isPlaying)
                Destroy(permutationTexture);
            else
                DestroyImmediate(permutationTexture);
            permutationTexture = null;
        #endif
        }
    }
}