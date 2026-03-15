#define LOGGING


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
/// Terrain generator with an optional GPU-accelerated density/base-terrain path.
/// Biome surface replacement and bedrock can also run on GPU; decoration and gameplay-facing block mutation remain CPU-side.
/// </summary>
public enum GenerationState
{
    Idle,
    Prepare_GetBiomes,
    Prepare_SandNoise,
    Prepare_GravelNoise,
    Prepare_StoneNoise,
    Prepare_GpuNoise,
    Prepare_GpuFinalize,
    Prepare_GpuReadback,
    Copy_GpuChunkSlice,
    Prepare_AllocCache,
    Prepare_NoiseOctaves,
    Prepare_CombineNoise,
    GeneratingTerrain,
    ReplacingBiomeBlocks,
    DecoratingTerrain,
    Complete
}

public enum GpuWorldgenReadbackPhase
{
    None,
    BaseColumn
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
    public byte logBlockID = 17;
    public byte leavesBlockID = 18;
    public byte tallGrassBlockID = 31;
    public byte flowerYellowBlockID = 37;
    public byte flowerRedBlockID = 38;

    [Header("Beta 1.7.3 Terrain Parameters")]
    // Debugging offsets for chunk generation.
    public int chunkOffsetX = 0;
    public int chunkOffsetZ = 0;
    [Tooltip("When enabled, terrain sampling matches the legacy 1to1 branch exactly: no X flip, no built-in offsets, and legacy surface-biome indexing.")]
    public bool match1to1TerrainBaseline = false;
    [Tooltip("Flip X-axis to match Minecraft's right-handed coordinate system (Unity is left-handed)")]
    public bool flipXAxis = true;
    [Tooltip("Built-in block offset on X-axis to align with Minecraft coordinate system")]
    private const int BUILTIN_OFFSET_X = -16;
    private const int BUILTIN_OFFSET_Z = 0;
    
    [Header("Debug Features")]
    [Tooltip("Generate a debug pillar at world origin (0,0) spanning full height")]
    public bool generateDebugPillar = true;
    [Range(0.1f, 2.0f)] public float terrainHeightMultiplier = 1.0f;
    [Range(0.0f, 1.0f)] public float caveFrequency = 0.14f;
    public bool generateCaves = true;
    public bool generateBedrock = true;
    
    [Header("Performance (VRChat)")]
    [Tooltip("WARN: Noise generation takes 400-500ms in VRChat (3-4x slower than editor). This is a known Udon limitation. First chunk per column will stutter.")]
    public bool acknowledgeVRChatPerformance = false;

    [Header("GPU Worldgen")]
    public bool enableGpuWorldgen = true;
    public Material gpuNoiseOctaveMaterial;
    public Material gpuNoiseCombineMaterial;
    public Material gpuColumnBaseFillMaterial;
    public Material gpuColumnSurfaceInfoMaterial;
    public Material gpuColumnSurfaceReplaceMaterial;

    [Header("Structure & Feature Templates")]
    public McStructureTemplate[] structureTemplates;

#if LOGGING
    [Header("Debugging")]
    public bool enableVerboseLogging = true;
    
    [Header("Performance Profiling")]
    public bool enableDetailedTimings = true;
    public bool enableCounters = true;
    public bool enableMemoryTracking = true;
    
    private DateTime time_Total_Start;
    private float time_Preparation, time_GeneratingTerrain, time_ReplacingBiomes;

    // Extra-detailed timings and counters
    private float time_Prep_GetBiomes, time_Prep_SandNoise, time_Prep_GravelNoise, time_Prep_StoneNoise, time_Prep_AllocNoiseCache;
    private float time_NoiseGen1, time_NoiseGen2, time_NoiseGen3, time_Noise6, time_Noise7, time_NoiseCombine;
    private int noiseGen1Cells, noiseGen2Cells, noiseGen3Cells, noise6Cells, noise7Cells, noiseCombineCells;
    private int terrainVoxelsVisited, terrainAssignments, terrainStoneAssignments, terrainWaterAssignments, terrainIceAssignments;
    private int biomeColumnsProcessed, biomeTopAssignments, biomeFillerAssignments, biomeBedrockAssignments, biomeWaterAssignments, biomeGravelAssignments, biomeSandAssignments, biomeSandstoneAssignments;
    
    // Per-step timing tracking
    private float lastStepTime;
    private float maxStepTime;
    private float minStepTime;
    private int totalSteps;
    
    // Cached timings from first chunk in column (for display purposes)
    private float cached_time_Preparation;
    private float cached_time_Prep_GetBiomes, cached_time_Prep_SandNoise, cached_time_Prep_GravelNoise, cached_time_Prep_StoneNoise, cached_time_Prep_AllocNoiseCache;
    private float cached_time_NoiseGen1, cached_time_NoiseGen2, cached_time_NoiseGen3, cached_time_Noise6, cached_time_Noise7, cached_time_NoiseCombine;
    private int cached_noiseGen1Cells, cached_noiseGen2Cells, cached_noiseGen3Cells, cached_noise6Cells, cached_noise7Cells, cached_noiseCombineCells;
    private bool timingsCached = false;
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

    // Cache of highest Y within this chunk where STONE appears per column (x,z)
    private int[] highestStoneYColumn;

    private bool isInitialized = false;
    
    // Time-slicing state
    private GenerationState currentState = GenerationState.Idle;
    private int currentChunkX, currentChunkY, currentChunkZ;
    private byte[] workingChunkData;
    private BetaBiomeEnum[] currentChunkBiomes;
    
    // Cache for preserving surface generation state across vertical chunks
    private int[] columnDepthCache;
    private byte[] columnFillerCache;  // Cache filler material per column (for sand→sandstone)
    private int cacheCoordX = int.MaxValue;
    private int cacheCoordZ = int.MaxValue;
    
    // Cache for the main 3D noise field for a column
    private double[] noiseCache;
    
    // RADICAL OPTIMIZATION: Simple caching for last biome query
    // Avoids re-computing biomes for same coordinates
    private int lastBiomeChunkX = int.MaxValue;
    private int lastBiomeChunkZ = int.MaxValue;
    private BetaBiomeEnum[] cachedBiomes;
    private double[] cachedTemperatures;
    private double[] cachedRainfall;

    // GPU worldgen runtime state
    private bool gpuWorldgenReady = false;
    private bool gpuColumnReadbackPending = false;
    private bool gpuColumnReadbackReady = false;
    private bool gpuColumnReadbackFailed = false;
    private int gpuCachedColumnX = int.MaxValue;
    private int gpuCachedColumnZ = int.MaxValue;
    private int gpuPendingColumnX = int.MaxValue;
    private int gpuPendingColumnZ = int.MaxValue;
    private GpuWorldgenReadbackPhase gpuReadbackPhase = GpuWorldgenReadbackPhase.None;
    private int gpuWorldHeightBlocks = 0;
    private Texture2D gpuPermTexture;
    private Texture2D gpuTemperatureTexture;
    private Texture2D gpuRainfallTexture;
    private Texture2D gpuSurfaceParamsTextureA;
    private Texture2D gpuSurfaceParamsTextureB;
    private Texture2D gpuBedrockMaskTexture;
    private RenderTexture gpuNoiseWork3D_A;
    private RenderTexture gpuNoiseWork3D_B;
    private RenderTexture gpuNoiseWork2D_A;
    private RenderTexture gpuNoiseWork2D_B;
    // CPU noise upload textures (reusable, pre-allocated)
    private Texture2D gpuNoiseUpload3D;
    private Texture2D gpuNoiseUpload2D;
    private Color[] gpuNoiseUpload3DPixels;
    private Color[] gpuNoiseUpload2DPixels;
    private RenderTexture gpuNoise1Texture;
    private RenderTexture gpuNoise2Texture;
    private RenderTexture gpuNoise3Texture;
    private RenderTexture gpuNoise6Texture;
    private RenderTexture gpuNoise7Texture;
    private RenderTexture gpuDensityTexture;
    private RenderTexture gpuColumnBaseTexture;
    private RenderTexture gpuColumnSurfaceInfoTexture;
    private RenderTexture gpuColumnFinalTexture;
    private Texture2D gpuColumnUploadTexture;
    private Texture2D gpuClearTexture;
    private Color[] gpuClimatePixels;
    private Color[] gpuPermutationPixels; // 256 * MAX_OCTAVES for batched upload
    private const int GPU_MAX_OCTAVES = 16;
    private Color[] gpuSurfaceParamPixelsA;
    private Color[] gpuSurfaceParamPixelsB;
    private Color[] gpuBedrockMaskPixels;
    private Color[] gpuColumnUploadPixels;
    private Color32[] gpuColumnReadbackPixels;
    private Color32[] gpuSurfaceInfoReadbackPixels;
    private int gpuPropPermTexId;
    private int gpuPropAccumulationTexId;
    private int gpuPropOctaveId;
    private int gpuPropFrequencyId;
    private int gpuPropAmplitudeId;
    private int gpuPropXCoordId;
    private int gpuPropYCoordId;
    private int gpuPropZCoordId;
    private int gpuPropXPosId;
    private int gpuPropYPosId;
    private int gpuPropZPosId;
    private int gpuPropGridXId;
    private int gpuPropGridYId;
    private int gpuPropGridZId;
    private int gpuPropXSizeId;
    private int gpuPropYSizeId;
    private int gpuPropZSizeId;
    private int gpuPropIs2DId;
    private int gpuPropNoise1TexId;
    private int gpuPropNoise2TexId;
    private int gpuPropNoise3TexId;
    private int gpuPropNoise6TexId;
    private int gpuPropNoise7TexId;
    private int gpuPropTemperatureTexId;
    private int gpuPropRainfallTexId;
    private int gpuPropChunkXId;
    private int gpuPropChunkZId;
    private int gpuPropFlipXAxisId;
    private int gpuPropBuiltinOffsetXId;
    private int gpuPropBuiltinOffsetZId;
    private int gpuPropDensityTexId;
    private int gpuPropWorldHeightId;
    private int gpuPropChunkSizeXZId;
    private int gpuPropOceanHeightId;
    private int gpuPropStoneBlockId;
    private int gpuPropWaterBlockId;
    private int gpuPropIceBlockId;
    private int gpuPropBaseColumnTexId;
    private int gpuPropSurfaceParamsTexAId;
    private int gpuPropSurfaceParamsTexBId;
    private int gpuPropBedrockMaskTexId;
    private int gpuPropBedrockBlockId;
    private int gpuPropSandBlockId;
    private int gpuPropSandstoneBlockId;

    // Precomputed coordinate data for double-precision noise on GPU.
    // CPU computes floor() & 0xFF and fractional parts in double,
    // then uploads as float arrays so the GPU never does precision-sensitive coord math.
    private const int GPU_MAX_XZSIZE = 5;
    private const int GPU_MAX_YSIZE = 65; // supports up to height=512
    private float[] gpuPrePermX, gpuPreFracX;
    private float[] gpuPrePermY, gpuPreFracY;
    private float[] gpuPrePermZ, gpuPreFracZ;
    // Gradient-cache fractional Y: stores the relY of the first sample in each
    // Perlin cell, replicating Minecraft Beta 1.7.3's Y-level caching behaviour
    // where gradient dot products reuse stale relY within the same cell.
    private float[] gpuPreGradFracY;
    private int gpuPropPermIdxXId, gpuPropFracXId;
    private int gpuPropPermIdxYId, gpuPropFracYId;
    private int gpuPropPermIdxZId, gpuPropFracZId;
    private int gpuPropGradFracYId;

    // Time-slicing progress trackers for the preparation state
    private int noiseCombine_x;
    private int noiseCombine_k1;
    private int noiseCombine_l1;
    
    // Time-slicing for noise octave generation
    private int currentNoiseGenerator; // 0=noise1, 1=noise2, 2=noise3, 3=noise6, 4=noise7
    private int currentOctave;
    private double[] currentNoiseOutput;

    // Time-slicing progress trackers
    private int terrain_xPiece, terrain_zPiece;
    private int biome_x;
    private int terrain_gen_step_count; 
    
    // Decoration state variables
    private int decoration_step;
    private int decoration_feature_count;
    private int decoration_feature_index; 

#if LOGGING
    private StringBuilder logBuilder;
#endif

    public Texture GetGpuDensityDebugTexture()
    {
        return gpuDensityTexture;
    }

    public Texture GetGpuColumnBaseDebugTexture()
    {
        return gpuColumnBaseTexture;
    }

    public Texture GetGpuColumnSurfaceInfoDebugTexture()
    {
        return gpuColumnSurfaceInfoTexture;
    }

    public Texture GetGpuColumnFinalDebugTexture()
    {
        return gpuColumnFinalTexture;
    }

    private int _GetTerrainBlockStartX(int chunkX)
    {
        if (match1to1TerrainBaseline) return chunkX * world.chunkSizeXZ;
        return (flipXAxis ? -chunkX * world.chunkSizeXZ : chunkX * world.chunkSizeXZ) + BUILTIN_OFFSET_X;
    }

    private int _GetTerrainBlockStartZ(int chunkZ)
    {
        if (match1to1TerrainBaseline) return chunkZ * world.chunkSizeXZ;
        return chunkZ * world.chunkSizeXZ + BUILTIN_OFFSET_Z;
    }

    private int _GetTerrainNoiseStartX(int chunkX, int pieceSize)
    {
        if (match1to1TerrainBaseline) return chunkX * pieceSize;
        return (flipXAxis ? -chunkX : chunkX) * pieceSize + (BUILTIN_OFFSET_X / pieceSize);
    }

    private int _GetTerrainNoiseStartZ(int chunkZ, int pieceSize)
    {
        if (match1to1TerrainBaseline) return chunkZ * pieceSize;
        return chunkZ * pieceSize + (BUILTIN_OFFSET_Z / pieceSize);
    }

    private int _MapTerrainLocalX(int localX, int sizeXZ)
    {
        if (match1to1TerrainBaseline) return localX;
        return flipXAxis ? (sizeXZ - 1 - localX) : localX;
    }

    private int _GetSurfaceBiomeIndex(int x, int z, int sizeXZ)
    {
        if (match1to1TerrainBaseline) return x + z * sizeXZ;
        return x * sizeXZ + z;
    }

    public void init(int seed)
    {
        DateTime startTime = DateTime.UtcNow;
        if (isInitialized) return;

        wcm = new WorldChunkManagerOld(seed);
#if LOGGING
        logBuilder = new StringBuilder(256);
#endif
        
        // Initialize depth cache
        if (world != null) {
            columnDepthCache = new int[world.chunkSizeXZ * world.chunkSizeXZ];
            columnFillerCache = new byte[world.chunkSizeXZ * world.chunkSizeXZ];
        } else {
            // Fallback, though world should always be available.
            columnDepthCache = new int[16 * 16];
            columnFillerCache = new byte[16 * 16];
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
        _InitializeGpuWorldgen();
        
        isInitialized = true;

#if LOGGING
        if (enableVerboseLogging) {
            logBuilder.Clear();
            double elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
            logBuilder.AppendFormat("[McTerrainGenerator.InitializeGenerator] Complete. Seed: {0}. Time: {1:F3} ms.", seed, elapsedMs);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }

    private Texture2D _CreateGpuFloatTexture(int width, int height, TextureWrapMode wrapMode)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = wrapMode;
        return texture;
    }

    private Texture2D _CreateGpuColorTexture(int width, int height, TextureWrapMode wrapMode)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = wrapMode;
        return texture;
    }

    private RenderTexture _CreateGpuFloatRenderTexture(int width, int height, string textureName)
    {
        // Use ARGBFloat (32-bit per channel) instead of ARGBHalf (16-bit) for noise accumulation.
        // Half-float only has ~3 decimal digits of mantissa precision which destroys
        // early-octave detail when accumulated against later-octave large amplitude values.
        RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        rt.name = textureName;
        rt.filterMode = FilterMode.Point;
        rt.wrapMode = TextureWrapMode.Clamp;
        rt.useMipMap = false;
        rt.autoGenerateMips = false;
        rt.Create();
        return rt;
    }

    private RenderTexture _CreateGpuColorRenderTexture(int width, int height, string textureName)
    {
        RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        rt.name = textureName;
        rt.filterMode = FilterMode.Point;
        rt.wrapMode = TextureWrapMode.Clamp;
        rt.useMipMap = false;
        rt.autoGenerateMips = false;
        rt.Create();
        return rt;
    }

    private void _InitializeGpuWorldgen()
    {
        gpuWorldgenReady = false;
        gpuColumnReadbackPending = false;
        gpuColumnReadbackReady = false;
        gpuColumnReadbackFailed = false;
        gpuReadbackPhase = GpuWorldgenReadbackPhase.None;
        if (!enableGpuWorldgen) return;
        if (world == null || gpuNoiseOctaveMaterial == null || gpuNoiseCombineMaterial == null || gpuColumnBaseFillMaterial == null || gpuColumnSurfaceInfoMaterial == null || gpuColumnSurfaceReplaceMaterial == null) return;

        gpuWorldHeightBlocks = world.worldDimensionY * world.chunkSizeY;
        int densityXSize = 5;
        int densityYSize = gpuWorldHeightBlocks / 8 + 1;
        int densityZSize = 5;
        int densityPackedWidth = densityXSize * densityZSize;

        // CRITICAL: Use RGBA32 (8-bit unorm) NOT RGBAHalf for permutation data.
        // Values 0-255 divided by 255 are NOT exactly representable in half-float,
        // causing permutation indices to be off-by-one when read back in the shader.
        // RGBA32 preserves exact integers via the round-trip: store(v/255)→8bit(v)→read(v/255)→×255=v
        gpuPermTexture = _CreateGpuColorTexture(256, GPU_MAX_OCTAVES, TextureWrapMode.Clamp);
        gpuTemperatureTexture = _CreateGpuFloatTexture(world.chunkSizeXZ, world.chunkSizeXZ, TextureWrapMode.Clamp);
        gpuRainfallTexture = _CreateGpuFloatTexture(world.chunkSizeXZ, world.chunkSizeXZ, TextureWrapMode.Clamp);
        gpuSurfaceParamsTextureA = _CreateGpuColorTexture(world.chunkSizeXZ, world.chunkSizeXZ, TextureWrapMode.Clamp);
        gpuSurfaceParamsTextureB = _CreateGpuColorTexture(world.chunkSizeXZ, world.chunkSizeXZ, TextureWrapMode.Clamp);
        gpuBedrockMaskTexture = _CreateGpuColorTexture(world.chunkSizeXZ, 5 * world.chunkSizeXZ, TextureWrapMode.Clamp);
        gpuClearTexture = _CreateGpuColorTexture(1, 1, TextureWrapMode.Clamp);
        gpuClearTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 1f));
        gpuClearTexture.Apply(false, false);

        gpuNoiseWork3D_A = _CreateGpuFloatRenderTexture(densityPackedWidth, densityYSize, "GPU_NoiseWork3D_A");
        gpuNoiseWork3D_B = _CreateGpuFloatRenderTexture(densityPackedWidth, densityYSize, "GPU_NoiseWork3D_B");
        gpuNoiseWork2D_A = _CreateGpuFloatRenderTexture(densityXSize, densityZSize, "GPU_NoiseWork2D_A");
        gpuNoiseWork2D_B = _CreateGpuFloatRenderTexture(densityXSize, densityZSize, "GPU_NoiseWork2D_B");
        // Pre-allocate reusable textures for CPU noise upload
        gpuNoiseUpload3D = _CreateGpuFloatTexture(densityPackedWidth, densityYSize, TextureWrapMode.Clamp);
        gpuNoiseUpload2D = _CreateGpuFloatTexture(densityXSize, densityZSize, TextureWrapMode.Clamp);
        gpuNoiseUpload3DPixels = new Color[densityPackedWidth * densityYSize];
        gpuNoiseUpload2DPixels = new Color[densityXSize * densityZSize];
        gpuNoise1Texture = _CreateGpuFloatRenderTexture(densityPackedWidth, densityYSize, "GPU_Noise1");
        gpuNoise2Texture = _CreateGpuFloatRenderTexture(densityPackedWidth, densityYSize, "GPU_Noise2");
        gpuNoise3Texture = _CreateGpuFloatRenderTexture(densityPackedWidth, densityYSize, "GPU_Noise3");
        gpuNoise6Texture = _CreateGpuFloatRenderTexture(densityXSize, densityZSize, "GPU_Noise6");
        gpuNoise7Texture = _CreateGpuFloatRenderTexture(densityXSize, densityZSize, "GPU_Noise7");
        gpuDensityTexture = _CreateGpuFloatRenderTexture(densityPackedWidth, densityYSize, "GPU_Density");
        gpuColumnBaseTexture = _CreateGpuColorRenderTexture(world.chunkSizeXZ, gpuWorldHeightBlocks * world.chunkSizeXZ, "GPU_ColumnBase");
        gpuColumnSurfaceInfoTexture = _CreateGpuColorRenderTexture(world.chunkSizeXZ, world.chunkSizeXZ, "GPU_ColumnSurfaceInfo");
        gpuColumnFinalTexture = _CreateGpuColorRenderTexture(world.chunkSizeXZ, gpuWorldHeightBlocks * world.chunkSizeXZ, "GPU_ColumnFinal");
        gpuColumnUploadTexture = _CreateGpuColorTexture(world.chunkSizeXZ, gpuWorldHeightBlocks * world.chunkSizeXZ, TextureWrapMode.Clamp);

        gpuClimatePixels = new Color[world.chunkSizeXZ * world.chunkSizeXZ];
        gpuPermutationPixels = new Color[256 * GPU_MAX_OCTAVES];
        gpuSurfaceParamPixelsA = new Color[world.chunkSizeXZ * world.chunkSizeXZ];
        gpuSurfaceParamPixelsB = new Color[world.chunkSizeXZ * world.chunkSizeXZ];
        gpuBedrockMaskPixels = new Color[world.chunkSizeXZ * 5 * world.chunkSizeXZ];
        gpuColumnUploadPixels = new Color[world.chunkSizeXZ * gpuWorldHeightBlocks * world.chunkSizeXZ];
        gpuColumnReadbackPixels = new Color32[world.chunkSizeXZ * gpuWorldHeightBlocks * world.chunkSizeXZ];
        gpuSurfaceInfoReadbackPixels = new Color32[world.chunkSizeXZ * world.chunkSizeXZ];

        gpuPropPermTexId = VRCShader.PropertyToID("_PermTex");
        gpuPropAccumulationTexId = VRCShader.PropertyToID("_AccumulationTex");
        gpuPropOctaveId = VRCShader.PropertyToID("_Octave");
        gpuPropFrequencyId = VRCShader.PropertyToID("_Frequency");
        gpuPropAmplitudeId = VRCShader.PropertyToID("_Amplitude");
        gpuPropXCoordId = VRCShader.PropertyToID("_XCoord");
        gpuPropYCoordId = VRCShader.PropertyToID("_YCoord");
        gpuPropZCoordId = VRCShader.PropertyToID("_ZCoord");
        gpuPropXPosId = VRCShader.PropertyToID("_XPos");
        gpuPropYPosId = VRCShader.PropertyToID("_YPos");
        gpuPropZPosId = VRCShader.PropertyToID("_ZPos");
        gpuPropGridXId = VRCShader.PropertyToID("_GridX");
        gpuPropGridYId = VRCShader.PropertyToID("_GridY");
        gpuPropGridZId = VRCShader.PropertyToID("_GridZ");
        gpuPropXSizeId = VRCShader.PropertyToID("_XSize");
        gpuPropYSizeId = VRCShader.PropertyToID("_YSize");
        gpuPropZSizeId = VRCShader.PropertyToID("_ZSize");
        gpuPropIs2DId = VRCShader.PropertyToID("_Is2D");
        gpuPropNoise1TexId = VRCShader.PropertyToID("_Noise1Tex");
        gpuPropNoise2TexId = VRCShader.PropertyToID("_Noise2Tex");
        gpuPropNoise3TexId = VRCShader.PropertyToID("_Noise3Tex");
        gpuPropNoise6TexId = VRCShader.PropertyToID("_Noise6Tex");
        gpuPropNoise7TexId = VRCShader.PropertyToID("_Noise7Tex");
        gpuPropTemperatureTexId = VRCShader.PropertyToID("_TemperatureTex");
        gpuPropRainfallTexId = VRCShader.PropertyToID("_RainfallTex");
        gpuPropChunkXId = VRCShader.PropertyToID("_ChunkX");
        gpuPropChunkZId = VRCShader.PropertyToID("_ChunkZ");
        gpuPropFlipXAxisId = VRCShader.PropertyToID("_FlipXAxis");
        gpuPropBuiltinOffsetXId = VRCShader.PropertyToID("_BuiltinOffsetX");
        gpuPropBuiltinOffsetZId = VRCShader.PropertyToID("_BuiltinOffsetZ");
        gpuPropDensityTexId = VRCShader.PropertyToID("_DensityTex");
        gpuPropWorldHeightId = VRCShader.PropertyToID("_WorldHeight");
        gpuPropChunkSizeXZId = VRCShader.PropertyToID("_ChunkSizeXZ");
        gpuPropOceanHeightId = VRCShader.PropertyToID("_OceanHeight");
        gpuPropStoneBlockId = VRCShader.PropertyToID("_StoneBlockId");
        gpuPropWaterBlockId = VRCShader.PropertyToID("_WaterBlockId");
        gpuPropIceBlockId = VRCShader.PropertyToID("_IceBlockId");
        gpuPropBaseColumnTexId = VRCShader.PropertyToID("_BaseColumnTex");
        gpuPropSurfaceParamsTexAId = VRCShader.PropertyToID("_SurfaceParamsTexA");
        gpuPropSurfaceParamsTexBId = VRCShader.PropertyToID("_SurfaceParamsTexB");
        gpuPropBedrockMaskTexId = VRCShader.PropertyToID("_BedrockMaskTex");
        gpuPropBedrockBlockId = VRCShader.PropertyToID("_BedrockBlockId");
        gpuPropSandBlockId = VRCShader.PropertyToID("_SandBlockId");
        gpuPropSandstoneBlockId = VRCShader.PropertyToID("_SandstoneBlockId");

        // Precomputed coordinate property IDs
        gpuPropPermIdxXId = VRCShader.PropertyToID("_PermIdxX");
        gpuPropFracXId = VRCShader.PropertyToID("_FracX");
        gpuPropPermIdxYId = VRCShader.PropertyToID("_PermIdxY");
        gpuPropFracYId = VRCShader.PropertyToID("_FracY");
        gpuPropGradFracYId = VRCShader.PropertyToID("_GradFracY");
        gpuPropPermIdxZId = VRCShader.PropertyToID("_PermIdxZ");
        gpuPropFracZId = VRCShader.PropertyToID("_FracZ");

        // Allocate precompute arrays
        gpuPrePermX = new float[GPU_MAX_XZSIZE];
        gpuPreFracX = new float[GPU_MAX_XZSIZE];
        gpuPrePermY = new float[GPU_MAX_YSIZE];
        gpuPreFracY = new float[GPU_MAX_YSIZE];
        gpuPreGradFracY = new float[GPU_MAX_YSIZE];
        gpuPrePermZ = new float[GPU_MAX_XZSIZE];
        gpuPreFracZ = new float[GPU_MAX_XZSIZE];

        gpuWorldgenReady = true;
    }



    private void _UploadGpuPermutationTexture(NoiseGenerator3dPerlin perlin)
    {
        if (gpuPermTexture == null || perlin == null || perlin.permutations == null) return;

        for (int i = 0; i < 256; i++)
        {
            gpuPermutationPixels[i] = new Color(perlin.permutations[i] / 255.0f, 0f, 0f, 1f);
        }

        gpuPermTexture.SetPixels(gpuPermutationPixels);
        gpuPermTexture.Apply(false, false);
    }

    private void _UploadGpuClimateTextures()
    {
        if (gpuTemperatureTexture == null || gpuRainfallTexture == null || wcm == null || wcm.temperatures == null || wcm.rainfall == null) return;

        int sizeXZ = world.chunkSizeXZ;
        for (int x = 0; x < sizeXZ; x++)
        {
            int xBase = x * sizeXZ;
            for (int z = 0; z < sizeXZ; z++)
            {
                int sourceIndex = xBase + z;
                int packedIndex = z * sizeXZ + x;
                gpuClimatePixels[packedIndex] = new Color((float)wcm.temperatures[sourceIndex], 0f, 0f, 1f);
            }
        }
        gpuTemperatureTexture.SetPixels(gpuClimatePixels);
        gpuTemperatureTexture.Apply(false, false);

        for (int x = 0; x < sizeXZ; x++)
        {
            int xBase = x * sizeXZ;
            for (int z = 0; z < sizeXZ; z++)
            {
                int sourceIndex = xBase + z;
                int packedIndex = z * sizeXZ + x;
                gpuClimatePixels[packedIndex] = new Color((float)wcm.rainfall[sourceIndex], 0f, 0f, 1f);
            }
        }
        gpuRainfallTexture.SetPixels(gpuClimatePixels);
        gpuRainfallTexture.Apply(false, false);
    }

    /// <summary>
    /// Precompute the Perlin noise coordinate integer/fractional split for one axis.
    /// This is the precision-critical part: done in double on CPU, results passed as float arrays.
    /// permIdx[i] = floor(world) & 0xFF  (exact in float: 0-255)
    /// frac[i]    = world - floor(world)  (in [0,1), full float precision)
    /// </summary>
    private void _PrecomputeNoiseCoords(float[] permIdx, float[] frac, int count,
                                        double pos, double grid, double coord)
    {
        for (int i = 0; i < count; i++)
        {
            double world = (pos + (double)i) * grid + coord;
            double floorVal = System.Math.Floor(world);
            int intPart;
            if (floorVal < int.MinValue) intPart = int.MinValue;
            else if (floorVal > int.MaxValue) intPart = int.MaxValue;
            else intPart = (int)floorVal;
            permIdx[i] = (float)(intPart & 255);
            frac[i] = (float)(world - floorVal);
        }
        // Zero remaining slots (shader arrays have fixed size)
        for (int i = count; i < permIdx.Length; i++)
        {
            permIdx[i] = 0f;
            frac[i] = 0f;
        }
    }

    /// <summary>
    /// Precompute Y-axis coordinates with gradient-cache emulation.
    /// Minecraft Beta 1.7.3 caches gradient dot products when the Y permutation
    /// index hasn't changed (consecutive Y samples in the same Perlin cell).
    /// The cached gradients use relY from the FIRST sample in each cell.
    /// gradFrac[i] stores that "base" relY for gradient computation;
    /// frac[i] stores the actual relY for fade() interpolation only.
    /// </summary>
    private void _PrecomputeNoiseCoordsY(float[] permIdx, float[] frac, float[] gradFrac,
                                          int count, double pos, double grid, double coord)
    {
        int lastPerm = -1;
        double baseFrac = 0.0;
        for (int i = 0; i < count; i++)
        {
            double world = (pos + (double)i) * grid + coord;
            double floorVal = System.Math.Floor(world);
            int intPart;
            if (floorVal < int.MinValue) intPart = int.MinValue;
            else if (floorVal > int.MaxValue) intPart = int.MaxValue;
            else intPart = (int)floorVal;
            int perm = intPart & 255;
            double fracVal = world - floorVal;

            permIdx[i] = (float)perm;
            frac[i] = (float)fracVal;

            // Replicate CPU caching: reset baseFrac on first sample or when
            // the permutation index changes (new Perlin cell).
            if (i == 0 || perm != lastPerm)
            {
                lastPerm = perm;
                baseFrac = fracVal;
            }
            gradFrac[i] = (float)baseFrac;
        }
        // Zero remaining slots
        for (int i = count; i < permIdx.Length; i++)
        {
            permIdx[i] = 0f;
            frac[i] = 0f;
            gradFrac[i] = 0f;
        }
    }

    private void _RunGpuNoiseOctaves(NoiseGenerator3dPerlin[] generators, int octaveCount, RenderTexture resultTexture, int xPos, int yPos, int zPos, int xSize, int ySize, int zSize, double gridX, double gridY, double gridZ, bool is2D)
    {
        if (!gpuWorldgenReady || generators == null || resultTexture == null) return;

        RenderTexture current = is2D ? gpuNoiseWork2D_A : gpuNoiseWork3D_A;
        RenderTexture next = is2D ? gpuNoiseWork2D_B : gpuNoiseWork3D_B;
        VRCGraphics.Blit(gpuClearTexture, current);

        // BATCH UPLOAD: Write ALL octaves' permutation data into a single 256×N texture
        // so we only call Apply() ONCE before the blit loop. This eliminates the race
        // condition where Apply() for octave N+1 could overwrite texture data before
        // the GPU finishes reading octave N's Blit.
        for (int octave = 0; octave < octaveCount; octave++)
        {
            NoiseGenerator3dPerlin generator = generators[octave];
            if (generator == null) continue;
            int rowOffset = octave * 256;
            for (int i = 0; i < 256; i++)
            {
                gpuPermutationPixels[rowOffset + i] = new Color(generator.permutations[i] / 255.0f, 0f, 0f, 1f);
            }
        }
        gpuPermTexture.SetPixels(gpuPermutationPixels);
        gpuPermTexture.Apply(false, false);

        for (int octave = 0; octave < octaveCount; octave++)
        {
            NoiseGenerator3dPerlin generator = generators[octave];
            if (generator == null) continue;

            double octaveFrequency = System.Math.Pow(0.5, octave);
            float octaveAmplitude = 1.0f / Mathf.Max(0.0001f, (float)octaveFrequency);

            double scaledGridX = gridX * octaveFrequency;
            double scaledGridY = gridY * octaveFrequency;
            double scaledGridZ = gridZ * octaveFrequency;

            _PrecomputeNoiseCoords(gpuPrePermX, gpuPreFracX, xSize,
                                   (double)xPos, scaledGridX, generator.xCoord);
            if (!is2D)
            {
                _PrecomputeNoiseCoordsY(gpuPrePermY, gpuPreFracY, gpuPreGradFracY, ySize,
                                        (double)yPos, scaledGridY, generator.yCoord);
            }
            _PrecomputeNoiseCoords(gpuPrePermZ, gpuPreFracZ, zSize,
                                   (double)zPos, scaledGridZ, generator.zCoord);

            gpuNoiseOctaveMaterial.SetTexture(gpuPropAccumulationTexId, current);
            gpuNoiseOctaveMaterial.SetTexture(gpuPropPermTexId, gpuPermTexture);
            gpuNoiseOctaveMaterial.SetFloat(gpuPropAmplitudeId, octaveAmplitude);
            gpuNoiseOctaveMaterial.SetInt(gpuPropXSizeId, xSize);
            gpuNoiseOctaveMaterial.SetInt(gpuPropYSizeId, ySize);
            gpuNoiseOctaveMaterial.SetInt(gpuPropZSizeId, zSize);
            gpuNoiseOctaveMaterial.SetInt(gpuPropIs2DId, is2D ? 1 : 0);
            gpuNoiseOctaveMaterial.SetInt("_OctaveRow", octave);

            gpuNoiseOctaveMaterial.SetFloatArray(gpuPropPermIdxXId, gpuPrePermX);
            gpuNoiseOctaveMaterial.SetFloatArray(gpuPropFracXId, gpuPreFracX);
            if (!is2D)
            {
                gpuNoiseOctaveMaterial.SetFloatArray(gpuPropPermIdxYId, gpuPrePermY);
                gpuNoiseOctaveMaterial.SetFloatArray(gpuPropFracYId, gpuPreFracY);
                gpuNoiseOctaveMaterial.SetFloatArray(gpuPropGradFracYId, gpuPreGradFracY);
            }
            gpuNoiseOctaveMaterial.SetFloatArray(gpuPropPermIdxZId, gpuPrePermZ);
            gpuNoiseOctaveMaterial.SetFloatArray(gpuPropFracZId, gpuPreFracZ);

            VRCGraphics.Blit(current, next, gpuNoiseOctaveMaterial, 1);

            RenderTexture temp = current;
            current = next;
            next = temp;
        }

        VRCGraphics.Blit(current, resultTexture);
    }

    /// <summary>
    /// Upload CPU-computed 3D noise to a GPU RenderTexture, packed as (x + z*xSize, y).
    /// CPU array order: [x][z][y] = array[x * zSize * ySize + z * ySize + y]
    /// </summary>
    private void _UploadCpuNoise3D(double[] cpuNoise, int xSize, int ySize, int zSize, RenderTexture target)
    {
        int packedWidth = xSize * zSize;
        for (int nx = 0; nx < xSize; nx++)
            for (int nz = 0; nz < zSize; nz++)
                for (int ny = 0; ny < ySize; ny++)
                {
                    int cpuIdx = nx * zSize * ySize + nz * ySize + ny;
                    int xzIdx = nx + nz * xSize;
                    int texIdx = ny * packedWidth + xzIdx;
                    gpuNoiseUpload3DPixels[texIdx] = new Color((float)cpuNoise[cpuIdx], 0f, 0f, 1f);
                }
        gpuNoiseUpload3D.SetPixels(gpuNoiseUpload3DPixels);
        gpuNoiseUpload3D.Apply(false, false);
        VRCGraphics.Blit(gpuNoiseUpload3D, target);
    }

    /// <summary>
    /// Upload CPU-computed 2D noise to a GPU RenderTexture.
    /// CPU array order: [x][z] = array[x * zSize + z]
    /// Texture layout: pixel(x, z) = pixels[z * xSize + x]
    /// </summary>
    private void _UploadCpuNoise2D(double[] cpuNoise, int xSize, int zSize, RenderTexture target)
    {
        for (int nx = 0; nx < xSize; nx++)
            for (int nz = 0; nz < zSize; nz++)
            {
                int cpuIdx = nx * zSize + nz;
                int texIdx = nz * xSize + nx;
                gpuNoiseUpload2DPixels[texIdx] = new Color((float)cpuNoise[cpuIdx], 0f, 0f, 1f);
            }
        gpuNoiseUpload2D.SetPixels(gpuNoiseUpload2DPixels);
        gpuNoiseUpload2D.Apply(false, false);
        VRCGraphics.Blit(gpuNoiseUpload2D, target);
    }

    private bool _StartGpuColumnGeneration()
    {
        if (!gpuWorldgenReady) return false;

        _UploadGpuClimateTextures();

        int densityXSize = 5;
        int densityYSize = gpuWorldHeightBlocks / 8 + 1;
        int densityZSize = 5;
        int densityXPos = _GetTerrainNoiseStartX(currentChunkX, 4);
        int densityZPos = _GetTerrainNoiseStartZ(currentChunkZ, 4);
        double d0 = 684.412D;
        double d1 = 684.412D;

        // GPU noise: precompute coordinates on CPU (double precision), run shader per octave.
        // This eliminates the ~600ms CPU noise bottleneck while maintaining 1:1 accuracy
        // via CPU-precomputed permutation indices and fractional parts.
        _RunGpuNoiseOctaves(noiseGen1.generatorCollection, 16, gpuNoise1Texture,
            densityXPos, 0, densityZPos, densityXSize, densityYSize, densityZSize,
            d0, d1, d0, false);
        _RunGpuNoiseOctaves(noiseGen2.generatorCollection, 16, gpuNoise2Texture,
            densityXPos, 0, densityZPos, densityXSize, densityYSize, densityZSize,
            d0, d1, d0, false);
        _RunGpuNoiseOctaves(noiseGen3.generatorCollection, 8, gpuNoise3Texture,
            densityXPos, 0, densityZPos, densityXSize, densityYSize, densityZSize,
            d0 / 80.0D, d1 / 160.0D, d0 / 80.0D, false);
        // 2D noise: generateNoiseArray internally calls generateNoiseOctaves(ad, x, 10.0, z, xSize, 1, zSize, gridX, 1.0, gridZ)
        _RunGpuNoiseOctaves(noiseGen6.generatorCollection, 10, gpuNoise6Texture,
            densityXPos, 10, densityZPos, densityXSize, 1, densityZSize,
            1.121D, 1.0D, 1.121D, true);
        _RunGpuNoiseOctaves(noiseGen7.generatorCollection, 16, gpuNoise7Texture,
            densityXPos, 10, densityZPos, densityXSize, 1, densityZSize,
            200.0D, 1.0D, 200.0D, true);

        // Let's print the CPU's internal variables for the first octave at x=0, z=0
        if (currentChunkX == 0 && currentChunkZ == 0)
        {
            double c_gridX = d0 * 1.0D;
            double c_gridY = d1 * 1.0D;
            double c_gridZ = d0 * 1.0D;
            double cx = (double)densityXPos * c_gridX + noiseGen1.generatorCollection[0].xCoord;
            double cy = (double)0 * c_gridY + noiseGen1.generatorCollection[0].yCoord; // Will change in loop
            double cz = (double)densityZPos * c_gridZ + noiseGen1.generatorCollection[0].zCoord;
            
            int intX = NoiseGenerator3dPerlin.floor_and_clamp_int(cx);
            int p1 = intX & 255;
            double relX = cx - intX;
            double fx = relX * relX * relX * (relX * (relX * 6D - 15D) + 10D);
            
            int intZ = NoiseGenerator3dPerlin.floor_and_clamp_int(cz);
            int p3 = intZ & 255;
            double relZ = cz - intZ;
            
            int[] perm3d = noiseGen1.generatorCollection[0].permutations;
            double[] GRAD_X = NoiseGenerator3dPerlin.GRAD_X;
            double[] GRAD_Y = NoiseGenerator3dPerlin.GRAD_Y;
            double[] GRAD_Z = NoiseGenerator3dPerlin.GRAD_Z;
            
            StringBuilder sb = new StringBuilder();
            sb.Append("[NOISE MATH DEBUG] CPU Octave 0 at x=0,z=0\n");
            for (int gy = 7; gy <= 8 && gy < densityYSize; gy++)
            {
                double cuy = (double)gy * c_gridY + noiseGen1.generatorCollection[0].yCoord;
                int intY = NoiseGenerator3dPerlin.floor_and_clamp_int(cuy);
                int p2 = intY & 255;
                double relY = cuy - intY;
                
                int a1 = perm3d[p1] + p2;
                int a2 = perm3d[a1] + p3;
                int h0 = perm3d[a2] & 15;
                double g0 = GRAD_X[h0] * relX + GRAD_Y[h0] * relY + GRAD_Z[h0] * relZ;
                
                sb.Append("  y=").Append(gy).Append(": hash0=").Append(h0)
                  .Append(" g0=").Append(g0.ToString("F4"))
                  .Append(" fx=").Append(fx.ToString("F4")).Append("\n");
            }
            Debug.Log(sb.ToString());
        }

        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise1TexId, gpuNoise1Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise2TexId, gpuNoise2Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise3TexId, gpuNoise3Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise6TexId, gpuNoise6Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise7TexId, gpuNoise7Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropTemperatureTexId, gpuTemperatureTexture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropRainfallTexId, gpuRainfallTexture);
        gpuNoiseCombineMaterial.SetInt(gpuPropXSizeId, densityXSize);
        gpuNoiseCombineMaterial.SetInt(gpuPropYSizeId, densityYSize);
        gpuNoiseCombineMaterial.SetInt(gpuPropZSizeId, densityZSize);
        gpuNoiseCombineMaterial.SetInt(gpuPropChunkXId, currentChunkX);
        gpuNoiseCombineMaterial.SetInt(gpuPropChunkZId, currentChunkZ);
        gpuNoiseCombineMaterial.SetInt(gpuPropFlipXAxisId, match1to1TerrainBaseline ? 0 : (flipXAxis ? 1 : 0));
        gpuNoiseCombineMaterial.SetInt(gpuPropBuiltinOffsetXId, BUILTIN_OFFSET_X);
        gpuNoiseCombineMaterial.SetInt(gpuPropBuiltinOffsetZId, BUILTIN_OFFSET_Z);
        VRCGraphics.Blit(gpuNoise1Texture, gpuDensityTexture, gpuNoiseCombineMaterial, 1);

        gpuColumnBaseFillMaterial.SetTexture(gpuPropDensityTexId, gpuDensityTexture);
        gpuColumnBaseFillMaterial.SetTexture(gpuPropTemperatureTexId, gpuTemperatureTexture);
        gpuColumnBaseFillMaterial.SetInt(gpuPropWorldHeightId, gpuWorldHeightBlocks);
        gpuColumnBaseFillMaterial.SetInt(gpuPropChunkSizeXZId, world.chunkSizeXZ);
        gpuColumnBaseFillMaterial.SetInt(gpuPropOceanHeightId, 64);
        gpuColumnBaseFillMaterial.SetInt(gpuPropFlipXAxisId, match1to1TerrainBaseline ? 0 : (flipXAxis ? 1 : 0));
        gpuColumnBaseFillMaterial.SetInt(gpuPropStoneBlockId, stoneBlockID);
        gpuColumnBaseFillMaterial.SetInt(gpuPropWaterBlockId, waterBlockID);
        gpuColumnBaseFillMaterial.SetInt(gpuPropIceBlockId, (int)BlockMaterial.ICE);
        VRCGraphics.Blit(gpuDensityTexture, gpuColumnBaseTexture, gpuColumnBaseFillMaterial);

        gpuColumnReadbackPending = false;
        gpuColumnReadbackReady = false;
        gpuColumnReadbackFailed = false;
        gpuPendingColumnX = currentChunkX;
        gpuPendingColumnZ = currentChunkZ;
        gpuColumnSurfaceInfoMaterial.SetTexture(gpuPropBaseColumnTexId, gpuColumnBaseTexture);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropWorldHeightId, gpuWorldHeightBlocks);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropChunkSizeXZId, world.chunkSizeXZ);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropStoneBlockId, stoneBlockID);
        VRCGraphics.Blit(gpuColumnBaseTexture, gpuColumnSurfaceInfoTexture, gpuColumnSurfaceInfoMaterial);

        gpuReadbackPhase = GpuWorldgenReadbackPhase.BaseColumn;
        gpuColumnReadbackPending = true;
        VRCAsyncGPUReadback.Request(gpuColumnBaseTexture, 0, TextureFormat.RGBA32, (IUdonEventReceiver)this);
        return true;
    }

    private void _UploadGpuSurfaceTextures()
    {
        if (gpuSurfaceParamsTextureA == null || gpuSurfaceParamsTextureB == null || gpuBedrockMaskTexture == null) return;

        gpuSurfaceParamsTextureA.SetPixels(gpuSurfaceParamPixelsA);
        gpuSurfaceParamsTextureA.Apply(false, false);
        gpuSurfaceParamsTextureB.SetPixels(gpuSurfaceParamPixelsB);
        gpuSurfaceParamsTextureB.Apply(false, false);
        gpuBedrockMaskTexture.SetPixels(gpuBedrockMaskPixels);
        gpuBedrockMaskTexture.Apply(false, false);
    }

    private void _BuildGpuSurfaceParamsFromSurfaceInfo()
    {
        if (gpuSurfaceInfoReadbackPixels == null || currentChunkBiomes == null || sandNoise == null || gravelNoise == null || stoneNoise == null) return;

        int sizeXZ = world.chunkSizeXZ;
        int count = sizeXZ * sizeXZ;
        int bedrockCount = sizeXZ * 5 * sizeXZ;
        for (int i = 0; i < count; i++)
        {
            gpuSurfaceParamPixelsA[i] = new Color(0f, 0f, 0f, 0f);
            gpuSurfaceParamPixelsB[i] = new Color(128f / 255f, 0f, 0f, 1f);
        }
        for (int i = 0; i < bedrockCount; i++)
        {
            gpuBedrockMaskPixels[i] = new Color(0f, 0f, 0f, 1f);
        }

        initRand(currentChunkX, currentChunkZ);
        JavaRandom random = rand;

        for (int x = 0; x < sizeXZ; x++)
        {
            for (int z = 0; z < sizeXZ; z++)
            {
                int finalX = _MapTerrainLocalX(x, sizeXZ);
                int packedIndex = z * sizeXZ + finalX;
                int biomeIndex = _GetSurfaceBiomeIndex(x, z, sizeXZ);

                bool sand = sandNoise[biomeIndex] + random.NextDouble() * 0.2D > 0.0D;
                bool gravel = gravelNoise[biomeIndex] + random.NextDouble() * 0.2D > 3D;
                int depth = (int)(stoneNoise[biomeIndex] * 0.33333333D + 3D + random.NextDouble() * 0.25D);

                bool hasStone = gpuSurfaceInfoReadbackPixels[packedIndex].a > 127;
                int surfaceY = hasStone ? gpuSurfaceInfoReadbackPixels[packedIndex].r : 0;
                BetaBiomeEnum biome = currentChunkBiomes[biomeIndex];

                byte topBlock = BiomeOld.top(biome);
                byte fillerBlock = BiomeOld.filler(biome);
                if (depth <= 0)
                {
                    topBlock = (byte)BlockMaterial.AIR;
                    fillerBlock = (byte)BlockMaterial.STONE;
                }
                else if (surfaceY >= 60 && surfaceY <= 65)
                {
                    topBlock = BiomeOld.top(biome);
                    fillerBlock = BiomeOld.filler(biome);
                    if (gravel)
                    {
                        topBlock = (byte)BlockMaterial.AIR;
                        fillerBlock = (byte)BlockMaterial.GRAVEL;
                    }
                    if (sand)
                    {
                        topBlock = (byte)BlockMaterial.SAND;
                        fillerBlock = (byte)BlockMaterial.SAND;
                    }
                }

                if (surfaceY < 64 && topBlock == (byte)BlockMaterial.AIR)
                {
                    topBlock = (byte)BlockMaterial.STATIONARY_WATER;
                }

                int sandstoneDepth = 0;
                if (hasStone && fillerBlock == (byte)BlockMaterial.SAND && depth > 0 && surfaceY >= depth)
                {
                    sandstoneDepth = random.NextInt(4);
                }

                int depthEncoded = Mathf.Clamp(depth + 128, 0, 255);
                gpuSurfaceParamPixelsA[packedIndex] = new Color(surfaceY / 255.0f, topBlock / 255.0f, fillerBlock / 255.0f, hasStone ? 1f : 0f);
                gpuSurfaceParamPixelsB[packedIndex] = new Color(depthEncoded / 255.0f, sandstoneDepth / 255.0f, 0f, 1f);
            }
        }

        for (int y = 0; y < 5; y++)
        {
            int packedYBase = y * sizeXZ;
            for (int z = 0; z < sizeXZ; z++)
            {
                int rowBase = (packedYBase + z) * sizeXZ;
                for (int x = 0; x < sizeXZ; x++)
                {
                    if (y <= random.NextInt(5))
                    {
                        gpuBedrockMaskPixels[rowBase + x] = new Color(1f, 0f, 0f, 1f);
                    }
                }
            }
        }

        _UploadGpuSurfaceTextures();
    }

    private bool _StartGpuColumnFinalize()
    {
        if (!gpuColumnReadbackReady || gpuColumnReadbackPixels == null || gpuColumnUploadTexture == null || gpuColumnUploadPixels == null || currentChunkBiomes == null) return false;

        int sizeXZ = world.chunkSizeXZ;
        int worldHeight = gpuWorldHeightBlocks;
        byte stoneId = stoneBlockID;
        byte bedrockId = bedrockBlockID;
        byte waterId = (byte)BlockMaterial.STATIONARY_WATER;
        byte sandId = sandBlockID;
        byte sandstoneId = sandStoneBlockID;

        initRand(currentChunkX, currentChunkZ);
        JavaRandom random = rand;

        for (int i = 0; i < gpuColumnReadbackPixels.Length; i++)
        {
            byte blockId = gpuColumnReadbackPixels[i].r;
            gpuColumnUploadPixels[i] = new Color(blockId / 255.0f, 0f, 0f, 1f);
        }

        for (int x = 0; x < sizeXZ; x++)
        {
            for (int z = 0; z < sizeXZ; z++)
            {
                int finalX = _MapTerrainLocalX(x, sizeXZ);
                int biomeIndex = _GetSurfaceBiomeIndex(x, z, sizeXZ);
                BetaBiomeEnum biome = currentChunkBiomes[biomeIndex];
                bool sand = sandNoise[biomeIndex] + random.NextDouble() * 0.2D > 0.0D;
                bool gravel = gravelNoise[biomeIndex] + random.NextDouble() * 0.2D > 3D;
                int depth = (int)(stoneNoise[biomeIndex] * 0.33333333D + 3D + random.NextDouble() * 0.25D);

                int prevDepth = -1;
                byte fillerBlock = 0;

                for (int globalY = worldHeight - 1; globalY >= 0; globalY--)
                {
                    int packedIndex = ((globalY * sizeXZ) + z) * sizeXZ + finalX;
                    byte blockId = gpuColumnReadbackPixels[packedIndex].r;

                    if (blockId == airBlockID)
                    {
                        prevDepth = -1;
                        continue;
                    }

                    if (blockId != stoneId)
                    {
                        continue;
                    }

                    if (prevDepth == -1)
                    {
                        byte topBlock = BiomeOld.top(biome);
                        fillerBlock = BiomeOld.filler(biome);

                        if (depth <= 0)
                        {
                            topBlock = airBlockID;
                            fillerBlock = stoneId;
                        }
                        else if (globalY >= 60 && globalY <= 65)
                        {
                            topBlock = BiomeOld.top(biome);
                            fillerBlock = BiomeOld.filler(biome);
                            if (gravel)
                            {
                                topBlock = airBlockID;
                                fillerBlock = (byte)BlockMaterial.GRAVEL;
                            }
                            if (sand)
                            {
                                topBlock = sandId;
                                fillerBlock = sandId;
                            }
                        }

                        if (globalY < 64 && topBlock == airBlockID)
                        {
                            topBlock = waterId;
                        }

                        prevDepth = depth;
                        byte finalBlock = globalY >= 63 ? topBlock : fillerBlock;
                        gpuColumnReadbackPixels[packedIndex].r = finalBlock;
                        gpuColumnUploadPixels[packedIndex] = new Color(finalBlock / 255.0f, 0f, 0f, 1f);
                    }
                    else if (prevDepth > 0)
                    {
                        prevDepth--;
                        gpuColumnReadbackPixels[packedIndex].r = fillerBlock;
                        gpuColumnUploadPixels[packedIndex] = new Color(fillerBlock / 255.0f, 0f, 0f, 1f);

                        if (prevDepth == 0 && fillerBlock == sandId)
                        {
                            prevDepth = random.NextInt(4);
                            fillerBlock = sandstoneId;
                        }
                    }
                }
            }
        }

        for (int globalY = 0; globalY < 5; globalY++)
        {
            int rowBase = globalY * sizeXZ;
            for (int z = 0; z < sizeXZ; z++)
            {
                int sliceBase = (rowBase + z) * sizeXZ;
                for (int x = 0; x < sizeXZ; x++)
                {
                    if (globalY <= random.NextInt(5))
                    {
                        int packedIndex = sliceBase + x;
                        gpuColumnReadbackPixels[packedIndex].r = bedrockId;
                        gpuColumnUploadPixels[packedIndex] = new Color(bedrockId / 255.0f, 0f, 0f, 1f);
                    }
                }
            }
        }

        gpuColumnUploadTexture.SetPixels(gpuColumnUploadPixels);
        gpuColumnUploadTexture.Apply(false, false);
        VRCGraphics.Blit(gpuColumnUploadTexture, gpuColumnFinalTexture);
        gpuColumnSurfaceInfoMaterial.SetTexture(gpuPropBaseColumnTexId, gpuColumnFinalTexture);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropWorldHeightId, gpuWorldHeightBlocks);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropChunkSizeXZId, world.chunkSizeXZ);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropStoneBlockId, stoneBlockID);
        VRCGraphics.Blit(gpuColumnFinalTexture, gpuColumnSurfaceInfoTexture, gpuColumnSurfaceInfoMaterial);

        gpuCachedColumnX = gpuPendingColumnX;
        gpuCachedColumnZ = gpuPendingColumnZ;
        return true;
    }

    private void _CopyGpuChunkSliceToWorkingData()
    {
        if (!gpuColumnReadbackReady || gpuColumnReadbackPixels == null || workingChunkData == null) return;

        int sizeXZ = world.chunkSizeXZ;
        int sizeY = world.chunkSizeY;
        int xyStride = sizeXZ * sizeXZ;
        int chunkYBase = currentChunkY * sizeY;
        System.Array.Clear(workingChunkData, 0, workingChunkData.Length);
        for (int i = 0; i < highestStoneYColumn.Length; i++) highestStoneYColumn[i] = -1;

        for (int localY = 0; localY < sizeY; localY++)
        {
            int globalY = chunkYBase + localY;
            int sourceRowBase = globalY * sizeXZ;
            int targetYBase = localY * xyStride;
            for (int z = 0; z < sizeXZ; z++)
            {
                int sourceIndex = (sourceRowBase + z) * sizeXZ;
                int targetIndex = targetYBase + z * sizeXZ;
                for (int x = 0; x < sizeXZ; x++)
                {
                    byte blockID = gpuColumnReadbackPixels[sourceIndex + x].r;
                    workingChunkData[targetIndex + x] = blockID;
                    if (blockID == stoneBlockID && localY > highestStoneYColumn[z * sizeXZ + x])
                    {
                        highestStoneYColumn[z * sizeXZ + x] = localY;
                    }
                }
            }
        }

        // DIAGNOSTIC: Compare GPU readback base blocks against CPU-computed terrain
        // for this chunk slice (only runs once per column, on the topmost chunk)
        if (currentChunkY == world.worldDimensionY - 1)
        {
            _DiagnosticCompareGpuVsCpuBaseColumn();
        }
    }

    private void _DiagnosticCompareGpuVsCpuBaseColumn()
    {
        // Compute the FULL CPU noise field for this column, then compare block IDs.
        byte byte0 = 4;
        int xSize = byte0 + 1;
        byte ySize = (byte)(world.worldDimensionY * world.chunkSizeY / 8 + 1);
        int zSize = byte0 + 1;
        double d0 = 684.412D;
        double d1 = 684.412D;

        int noiseStartX = match1to1TerrainBaseline ? currentChunkX * byte0 : _GetTerrainNoiseStartX(currentChunkX, byte0);
        int noiseStartZ = match1to1TerrainBaseline ? currentChunkZ * byte0 : _GetTerrainNoiseStartZ(currentChunkZ, byte0);

        double[] cpuNoise1 = noiseGen1.generateNoiseOctaves(null, noiseStartX, 0, noiseStartZ, xSize, ySize, zSize, d0, d1, d0);
        double[] cpuNoise2 = noiseGen2.generateNoiseOctaves(null, noiseStartX, 0, noiseStartZ, xSize, ySize, zSize, d0, d1, d0);
        double[] cpuNoise3 = noiseGen3.generateNoiseOctaves(null, noiseStartX, 0, noiseStartZ, xSize, ySize, zSize, d0 / 80.0D, d1 / 160.0D, d0 / 80.0D);
        double[] cpuNoise6 = noiseGen6.generateNoiseArray(null, noiseStartX, noiseStartZ, xSize, zSize, 1.121D, 1.121D, 0.5D);
        double[] cpuNoise7 = noiseGen7.generateNoiseArray(null, noiseStartX, noiseStartZ, xSize, zSize, 200.0D, 200.0D, 0.5D);

        // Run the combine step (same as Prepare_CombineNoise)
        int i2 = 16 / xSize;
        double[] temp = wcm.temperatures;
        double[] rain = wcm.rainfall;
        double[] cpuDensity = new double[xSize * ySize * zSize];
        int k1 = 0;
        int l1 = 0;
        for (int x = 0; x < xSize; x++)
        {
            int k2 = x * i2 + i2 / 2;
            for (int z = 0; z < zSize; z++)
            {
                int i3 = z * i2 + i2 / 2;
                int tempIndex = k2 * 16 + i3;
                double d2 = temp[tempIndex];
                double d3 = rain[tempIndex] * d2;
                double d4 = 1.0D - d3;
                d4 *= d4; d4 *= d4; d4 = 1.0D - d4;
                double d5 = (cpuNoise6[l1] + 256.0D) / 512.0D;
                d5 *= d4;
                if (d5 > 1.0D) d5 = 1.0D;
                double d6 = cpuNoise7[l1] / 8000.0D;
                if (d6 < 0.0D) d6 = -d6 * 0.3D;
                d6 = d6 * 3.0D - 2.0D;
                if (d6 < 0.0D) { d6 *= 0.5D; if (d6 < -1D) d6 = -1D; d6 *= 0.3571428571428571D; d5 = 0.0D; }
                else { if (d6 > 1.0D) d6 = 1.0D; d6 *= 0.125D; }
                if (d5 < 0.0D) d5 = 0.0D;
                d5 += 0.5D;
                d6 = (d6 * (double)ySize) / 16.0D;
                double d7 = (double)ySize / 2.0D + d6 * 4.0D;

                for (int y = 0; y < ySize; y++)
                {
                    double d9 = (((double)y - d7) * 12D) / d5;
                    if (d9 < 0.0D) d9 *= 4.0D;
                    double d10 = cpuNoise1[k1] / 512.0D;
                    double d11 = cpuNoise2[k1] / 512.0D;
                    double d12 = (cpuNoise3[k1] * 0.1D + 1.0D) * 0.5D;
                    double d8;
                    if (d12 < 0.0D) d8 = d10;
                    else if (d12 > 1.0D) d8 = d11;
                    else d8 = d10 + (d11 - d10) * d12;
                    d8 -= d9;
                    if (y > ySize - 4)
                    {
                        double d13 = (double)((float)(y - (ySize - 4)) * 0.33333334F);
                        d8 = d8 * (1.0D - d13) - 10.0D * d13;
                    }
                    cpuDensity[k1] = d8;
                    k1++;
                }
                l1++;
            }
        }

        // Now compare: generate base column blocks from CPU density and compare with GPU readback.
        int diagSizeXZ = world.chunkSizeXZ;
        int worldHeight = gpuWorldHeightBlocks;
        int mismatches = 0;
        int totalCompared = 0;
        StringBuilder diagLog = new StringBuilder();
        diagLog.Append("[GPU vs CPU DIAGNOSTIC] Chunk (").Append(currentChunkX).Append(",").Append(currentChunkZ).Append(")\n");

        // Sample 5 columns at specific (x,z) positions
        int[] sampleX = new int[] { 0, 4, 8, 12, 7 };
        int[] sampleZ = new int[] { 0, 4, 8, 12, 7 };

        for (int s = 0; s < 5; s++)
        {
            int bx = sampleX[s];
            int bz = sampleZ[s];
            int xPiece = bx / 4;
            int zPiece = bz / 4;
            int xSub = bx - xPiece * 4;
            int zSub = bz - zPiece * 4;

            int colMismatches = 0;
            for (int by = 0; by < worldHeight; by++)
            {
                // CPU: trilinear interpolation (density indexed as (x * zSize + z) * ySize + y)
                int yPiece = by / 8;
                int ySub = by - yPiece * 8;
                double yLerp = ySub * 0.125D;
                double xLerp = xSub * 0.25D;
                double zLerp = zSub * 0.25D;

                // Inline density indexing: idx = (dx * zSize + dz) * ySize + dy
                double c00y0 = cpuDensity[(xPiece * zSize + zPiece) * ySize + yPiece];
                double c00y1 = cpuDensity[(xPiece * zSize + zPiece) * ySize + yPiece + 1];
                double c01y0 = cpuDensity[(xPiece * zSize + (zPiece + 1)) * ySize + yPiece];
                double c01y1 = cpuDensity[(xPiece * zSize + (zPiece + 1)) * ySize + yPiece + 1];
                double c10y0 = cpuDensity[((xPiece + 1) * zSize + zPiece) * ySize + yPiece];
                double c10y1 = cpuDensity[((xPiece + 1) * zSize + zPiece) * ySize + yPiece + 1];
                double c11y0 = cpuDensity[((xPiece + 1) * zSize + (zPiece + 1)) * ySize + yPiece];
                double c11y1 = cpuDensity[((xPiece + 1) * zSize + (zPiece + 1)) * ySize + yPiece + 1];

                double c00 = c00y0 + (c00y1 - c00y0) * yLerp;
                double c01 = c01y0 + (c01y1 - c01y0) * yLerp;
                double c10 = c10y0 + (c10y1 - c10y0) * yLerp;
                double c11 = c11y0 + (c11y1 - c11y0) * yLerp;
                double dx0 = c00 + (c10 - c00) * xLerp;
                double dx1 = c01 + (c11 - c01) * xLerp;
                double density = dx0 + (dx1 - dx0) * zLerp;

                // CPU block assignment (base column: stone/water/air)
                byte cpuBlock = 0; // air
                if (by < 64) cpuBlock = (byte)BlockMaterial.STATIONARY_WATER;
                if (density > 0.0D) cpuBlock = stoneBlockID;

                // GPU readback block (FINALIZED: has surface replacement applied)
                int gpuX = match1to1TerrainBaseline ? bx : (flipXAxis ? (diagSizeXZ - 1 - bx) : bx);
                int gpuIdx = ((by * diagSizeXZ) + bz) * diagSizeXZ + gpuX;
                byte gpuBlock = gpuColumnReadbackPixels[gpuIdx].r;

                // Compare terrain SHAPE (solid vs non-solid), not exact block IDs.
                // GPU blocks have been surface-replaced (stone → grass/dirt/sand/bedrock),
                // so exact block comparison is invalid at the surface.
                bool cpuSolid = density > 0.0D;
                bool gpuSolid = gpuBlock != 0 && gpuBlock != (byte)BlockMaterial.STATIONARY_WATER && gpuBlock != (byte)BlockMaterial.WATER;
                
                totalCompared++;
                if (cpuSolid != gpuSolid)
                {
                    colMismatches++;
                    mismatches++;
                    if (colMismatches <= 3)
                    {
                        diagLog.Append("  MISMATCH at (").Append(bx).Append(",").Append(by).Append(",").Append(bz).Append("): CPU=").Append(cpuSolid ? "solid" : "air").Append(" GPU=").Append(gpuBlock).Append(" density=").Append(density.ToString("F6")).Append("\n");
                    }
                }
            }
            diagLog.Append("  Column (").Append(bx).Append(",").Append(bz).Append("): ").Append(colMismatches).Append(" mismatches / ").Append(worldHeight).Append("\n");
        }
        diagLog.Append("TOTAL: ").Append(mismatches).Append(" / ").Append(totalCompared).Append(" blocks differ\n");

        // Targeted diagnostic at Y=56-72 for column (0,0) — the terrain surface region
        diagLog.Append("--- Y=56..72 detail for column (0,0) ---\n");
        {
            int bx = 0; int bz = 0;
            int txPiece = bx / 4; int tzPiece = bz / 4;
            int txSub = bx - txPiece * 4; int tzSub = bz - tzPiece * 4;
            int diagSXZ = world.chunkSizeXZ;
            for (int by = 56; by <= 72 && by < worldHeight; by++)
            {
                int tyPiece = by / 8;
                int tySub = by - tyPiece * 8;
                double tyLerp = tySub * 0.125D;
                double txLerp = txSub * 0.25D;
                double tzLerp = tzSub * 0.25D;

                double tc00y0 = cpuDensity[(txPiece * zSize + tzPiece) * ySize + tyPiece];
                double tc00y1 = cpuDensity[(txPiece * zSize + tzPiece) * ySize + tyPiece + 1];
                double tc00 = tc00y0 + (tc00y1 - tc00y0) * tyLerp;
                double tc10y0 = cpuDensity[((txPiece + 1) * zSize + tzPiece) * ySize + tyPiece];
                double tc10y1 = cpuDensity[((txPiece + 1) * zSize + tzPiece) * ySize + tyPiece + 1];
                double tc10 = tc10y0 + (tc10y1 - tc10y0) * tyLerp;
                double tc01y0 = cpuDensity[(txPiece * zSize + (tzPiece + 1)) * ySize + tyPiece];
                double tc01y1 = cpuDensity[(txPiece * zSize + (tzPiece + 1)) * ySize + tyPiece + 1];
                double tc01 = tc01y0 + (tc01y1 - tc01y0) * tyLerp;
                double tc11y0 = cpuDensity[((txPiece + 1) * zSize + (tzPiece + 1)) * ySize + tyPiece];
                double tc11y1 = cpuDensity[((txPiece + 1) * zSize + (tzPiece + 1)) * ySize + tyPiece + 1];
                double tc11 = tc11y0 + (tc11y1 - tc11y0) * tyLerp;
                double tdx0 = tc00 + (tc10 - tc00) * txLerp;
                double tdx1 = tc01 + (tc11 - tc01) * txLerp;
                double tDensity = tdx0 + (tdx1 - tdx0) * tzLerp;

                int tGpuX = match1to1TerrainBaseline ? bx : (flipXAxis ? (diagSXZ - 1 - bx) : bx);
                int tGpuIdx = ((by * diagSXZ) + bz) * diagSXZ + tGpuX;
                byte tGpuBlock = gpuColumnReadbackPixels[tGpuIdx].r;
                byte tCpuBlock = 0;
                if (by < 64) tCpuBlock = (byte)BlockMaterial.STATIONARY_WATER;
                if (tDensity > 0.0D) tCpuBlock = stoneBlockID;

                diagLog.Append("  Y=").Append(by).Append(": cpuDensity=").Append(tDensity.ToString("F4"));
                diagLog.Append(" cpuBlock=").Append(tCpuBlock);
                diagLog.Append(" gpuBlock=").Append(tGpuBlock);
                if (tCpuBlock != tGpuBlock) diagLog.Append(" *** MISMATCH");
                diagLog.Append("\n");
            }
        }
        // Grid-level noise comparison at (x=0, z=0, y=7..9) - the surface grid zone
        diagLog.Append("--- Grid noise at x=0,z=0 ---\n");
        {
            for (int gy = 7; gy <= 9 && gy < ySize; gy++)
            {
                int gIdx = 0 * zSize * ySize + 0 * ySize + gy;
                diagLog.Append("  grid y=").Append(gy);
                diagLog.Append(": n1=").Append(cpuNoise1[gIdx].ToString("F2"));
                diagLog.Append(" n2=").Append(cpuNoise2[gIdx].ToString("F2"));
                diagLog.Append(" n3=").Append(cpuNoise3[gIdx].ToString("F2"));
                diagLog.Append(" density=").Append(cpuDensity[gIdx].ToString("F2"));
                diagLog.Append("\n");
            }
            int gIdx6 = 0 * zSize + 0;
            diagLog.Append("  noise6=").Append(cpuNoise6[gIdx6].ToString("F2"));
            diagLog.Append(" noise7=").Append(cpuNoise7[gIdx6].ToString("F2")).Append("\n");
        }

        Debug.Log(diagLog.ToString());
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        if (gpuReadbackPhase != GpuWorldgenReadbackPhase.BaseColumn) return;

        gpuReadbackPhase = GpuWorldgenReadbackPhase.None;
        gpuColumnReadbackPending = false;
        gpuColumnReadbackReady = false;

        if (request.hasError)
        {
            gpuColumnReadbackFailed = true;
            return;
        }

        if (!request.TryGetData(gpuColumnReadbackPixels, 0))
        {
            gpuColumnReadbackFailed = true;
            return;
        }

        gpuColumnReadbackFailed = false;
        gpuColumnReadbackReady = true;
    }

    public void StartChunkGeneration(int chunkX, int chunkY, int chunkZ)
    {
        if (!isInitialized)
        {
            Debug.LogError("[McTerrainGenerator] Not initialized! Call init() first.");
            return;
        }

        // CRITICAL: Receive absolute Minecraft chunk coordinates from McWorld
        // The chunkOffsetX/Z parameters are for manual debugging only and should be 0
        // DO NOT add world.chunkOffsetX here - that conversion happens in McWorld
        currentChunkX = chunkX + (chunkOffsetX / world.chunkSizeXZ);
        currentChunkY = chunkY;
        currentChunkZ = chunkZ + (chunkOffsetZ / world.chunkSizeXZ);
        
        // Check if column is cached and set initial state appropriately
        if (currentChunkX == cacheCoordX && currentChunkZ == cacheCoordZ)
        {
            // Cached column, skip directly to terrain generation or GPU copy
            if (gpuWorldgenReady && gpuColumnReadbackReady && currentChunkX == gpuCachedColumnX && currentChunkZ == gpuCachedColumnZ)
            {
                currentState = GenerationState.Copy_GpuChunkSlice;
            }
            else if (gpuWorldgenReady && gpuColumnReadbackPending && currentChunkX == gpuPendingColumnX && currentChunkZ == gpuPendingColumnZ)
            {
                currentState = GenerationState.Prepare_GpuReadback;
            }
            else if (gpuWorldgenReady && gpuColumnReadbackReady && currentChunkX == gpuPendingColumnX && currentChunkZ == gpuPendingColumnZ)
            {
                currentState = GenerationState.Prepare_GpuFinalize;
            }
            else
            {
                initRand(currentChunkX, currentChunkZ);
                currentState = GenerationState.GeneratingTerrain;
            }
        }
        else
        {
            // New column, start with GetBiomes
            cacheCoordX = currentChunkX;
            cacheCoordZ = currentChunkZ;
            currentState = GenerationState.Prepare_GetBiomes;
#if LOGGING
            timingsCached = false;
#endif
        }

        // Reset the depth cache for each column if we are at the top of the world
        if (currentChunkY == world.worldDimensionY - 1)
        {
            for (int i = 0; i < columnDepthCache.Length; i++)
            {
                columnDepthCache[i] = -1;
                 columnFillerCache[i] = 0;
            }
        }

        int chunkSize = world.chunkSizeXZ * world.chunkSizeY * world.chunkSizeXZ;
        if (workingChunkData == null || workingChunkData.Length != chunkSize)
        {
            workingChunkData = new byte[chunkSize];
        }
        else
        {
            System.Array.Clear(workingChunkData, 0, workingChunkData.Length);
        }

        // Reset per-chunk highest stone cache
        int columnCount = world.chunkSizeXZ * world.chunkSizeXZ;
        if (highestStoneYColumn == null || highestStoneYColumn.Length != columnCount)
        {
            highestStoneYColumn = new int[columnCount];
        }
        for (int i = 0; i < columnCount; i++) highestStoneYColumn[i] = -1;

        terrain_xPiece = 0;
        terrain_zPiece = 0;
        biome_x = 0;
        terrain_gen_step_count = 0;

#if LOGGING
        time_Total_Start = DateTime.UtcNow;
        time_Preparation = 0f;
        time_GeneratingTerrain = 0f;
        time_ReplacingBiomes = 0f;
        // Reset detailed timers and counters
        time_Prep_GetBiomes = 0f; time_Prep_SandNoise = 0f; time_Prep_GravelNoise = 0f; time_Prep_StoneNoise = 0f; time_Prep_AllocNoiseCache = 0f;
        time_NoiseGen1 = 0f; time_NoiseGen2 = 0f; time_NoiseGen3 = 0f; time_Noise6 = 0f; time_Noise7 = 0f; time_NoiseCombine = 0f;
        noiseGen1Cells = 0; noiseGen2Cells = 0; noiseGen3Cells = 0; noise6Cells = 0; noise7Cells = 0; noiseCombineCells = 0;
        terrainVoxelsVisited = 0; terrainAssignments = 0; terrainStoneAssignments = 0; terrainWaterAssignments = 0; terrainIceAssignments = 0;
        biomeColumnsProcessed = 0; biomeTopAssignments = 0; biomeFillerAssignments = 0; biomeBedrockAssignments = 0; biomeWaterAssignments = 0; biomeGravelAssignments = 0; biomeSandAssignments = 0; biomeSandstoneAssignments = 0;
        // Reset per-step timing
        lastStepTime = 0f;
        maxStepTime = 0f;
        minStepTime = 999f;
        totalSteps = 0;
#endif
    }
    
    public bool StepChunkGeneration(out byte[] completedData)
    {
        completedData = null;

#if LOGGING
        DateTime stepStartTime = DateTime.UtcNow;
        float frameStepStart = Time.realtimeSinceStartup;
        DateTime t0 = DateTime.MinValue; // Declare once for all case statements
#endif
        
        bool isComplete = false;
        
        // Declare coordinate variables once for all case statements
        int noiseX = 0;
        int noiseZ = 0;
        int noiseChunkX = 0;
        
        switch (currentState)
        {
            case GenerationState.Prepare_GetBiomes:
                {
                    // OPTIMIZATION: Check if we've already computed biomes for this chunk coordinate
#if LOGGING
                    if (enableVerboseLogging) { logBuilder.Clear(); logBuilder.Append("[McTerrainGenerator] GetBiomes"); Debug.Log(logBuilder.ToString()); timingsCached = false; }
                    t0 = DateTime.UtcNow;
#endif
                    // Check cache first
                    if (currentChunkX == lastBiomeChunkX && currentChunkZ == lastBiomeChunkZ && cachedBiomes != null)
                    {
                        // Use cached data - instant!
                        currentChunkBiomes = cachedBiomes;
                        wcm.temperatures = cachedTemperatures;
                        wcm.rainfall = cachedRainfall;
                    }
                    else
                    {
                        // Compute biomes (expensive but necessary)
                        // CRITICAL: Handle coordinate flip for Minecraft right-handed system
                        // Apply built-in -15 block offset to align with Minecraft
                        noiseX = _GetTerrainBlockStartX(currentChunkX);
                        noiseZ = _GetTerrainBlockStartZ(currentChunkZ);
                        currentChunkBiomes = wcm.getBiomeBlock(currentChunkBiomes, noiseX, noiseZ, 16, 16);
                        
                        // Cache for potential reuse
                        lastBiomeChunkX = currentChunkX;
                        lastBiomeChunkZ = currentChunkZ;
                        cachedBiomes = currentChunkBiomes;
                        
                        // Cache temperature/rainfall arrays (needed for noise combination)
                        if (cachedTemperatures == null || cachedTemperatures.Length != wcm.temperatures.Length)
                        {
                            cachedTemperatures = new double[wcm.temperatures.Length];
                            cachedRainfall = new double[wcm.rainfall.Length];
                        }
                        System.Array.Copy(wcm.temperatures, cachedTemperatures, wcm.temperatures.Length);
                        System.Array.Copy(wcm.rainfall, cachedRainfall, wcm.rainfall.Length);
                    }
                    
#if LOGGING
                    if (enableVerboseLogging) { time_Prep_GetBiomes = (float)(DateTime.UtcNow - t0).TotalMilliseconds; }
#endif
                    currentState = GenerationState.Prepare_SandNoise;
                }
                break;
                
            case GenerationState.Prepare_SandNoise:
#if LOGGING
                t0 = DateTime.UtcNow;
#endif
                // CRITICAL: Handle coordinate flip for Minecraft right-handed system
                // Apply built-in -15 block offset to align with Minecraft
                noiseX = _GetTerrainBlockStartX(currentChunkX);
                noiseZ = _GetTerrainBlockStartZ(currentChunkZ);
                this.sandNoise = this.noiseGen4.generateNoiseOctaves(this.sandNoise, noiseX, noiseZ, 0.0D, 16, 16, 1, 0.03125D, 0.03125D, 1.0D);
#if LOGGING
                if (enableVerboseLogging) { time_Prep_SandNoise = (float)(DateTime.UtcNow - t0).TotalMilliseconds; }
#endif
                currentState = GenerationState.Prepare_GravelNoise;
                break;
                
            case GenerationState.Prepare_GravelNoise:
#if LOGGING
                t0 = DateTime.UtcNow;
#endif
                // Apply built-in -15 block offset to align with Minecraft
                noiseX = _GetTerrainBlockStartX(currentChunkX);
                noiseZ = _GetTerrainBlockStartZ(currentChunkZ);
                this.gravelNoise = this.noiseGen4.generateNoiseOctaves(this.gravelNoise, noiseX, 109.0134D, noiseZ, 16, 1, 16, 0.03125D, 1.0D, 0.03125D);
#if LOGGING
                if (enableVerboseLogging) { time_Prep_GravelNoise = (float)(DateTime.UtcNow - t0).TotalMilliseconds; }
#endif
                currentState = GenerationState.Prepare_StoneNoise;
                break;
                
            case GenerationState.Prepare_StoneNoise:
#if LOGGING
                t0 = DateTime.UtcNow;
#endif
                // Apply built-in -15 block offset to align with Minecraft
                noiseX = _GetTerrainBlockStartX(currentChunkX);
                noiseZ = _GetTerrainBlockStartZ(currentChunkZ);
                this.stoneNoise = this.noiseGen5.generateNoiseOctaves(this.stoneNoise, noiseX, noiseZ, 0.0D, 16, 16, 1, 0.0625D, 0.0625D, 0.0625D);
#if LOGGING
                if (enableVerboseLogging) { time_Prep_StoneNoise = (float)(DateTime.UtcNow - t0).TotalMilliseconds; }
                if (enableVerboseLogging && gpuWorldgenReady)
                {
                    time_Preparation = time_Prep_GetBiomes + time_Prep_SandNoise + time_Prep_GravelNoise + time_Prep_StoneNoise;
                }
#endif
                currentState = gpuWorldgenReady ? GenerationState.Prepare_GpuNoise : GenerationState.Prepare_AllocCache;
                break;

            case GenerationState.Prepare_GpuNoise:
                {
                    if (!_StartGpuColumnGeneration())
                    {
                        currentState = GenerationState.Prepare_AllocCache;
                        break;
                    }

                    currentState = GenerationState.Prepare_GpuReadback;
                }
                break;

            case GenerationState.Prepare_GpuFinalize:
                {
                    if (!_StartGpuColumnFinalize())
                    {
                        currentState = GenerationState.Prepare_AllocCache;
                        break;
                    }

                    currentState = GenerationState.Copy_GpuChunkSlice;
                }
                break;

            case GenerationState.Prepare_GpuReadback:
                {
                    if (gpuColumnReadbackFailed)
                    {
                        currentState = GenerationState.Prepare_AllocCache;
                        break;
                    }

                    if (gpuColumnReadbackReady && currentChunkX == gpuPendingColumnX && currentChunkZ == gpuPendingColumnZ)
                    {
                        currentState = GenerationState.Prepare_GpuFinalize;
                    }
                }
                break;

            case GenerationState.Copy_GpuChunkSlice:
                {
#if LOGGING
                    DateTime tGpuCopy = DateTime.MinValue;
                    if (enableVerboseLogging) tGpuCopy = DateTime.UtcNow;
#endif
                    _CopyGpuChunkSliceToWorkingData();
                    terrain_gen_step_count = 1;
#if LOGGING
                    if (enableVerboseLogging) time_GeneratingTerrain += (float)(DateTime.UtcNow - tGpuCopy).TotalMilliseconds;
#endif
                    if (!match1to1TerrainBaseline && currentChunkY == world.worldDimensionY - 1)
                    {
                        decoration_step = 0;
                        decoration_feature_index = 0;
                        decoration_feature_count = 0;
                        currentState = GenerationState.DecoratingTerrain;
                    }
                    else
                    {
                        currentState = GenerationState.Complete;
                    }
                }
                break;

            case GenerationState.Prepare_AllocCache:
                {
                    byte byte0 = 4;
                    int xSize = byte0 + 1;
                    byte ySize = (byte)(world.worldDimensionY * world.chunkSizeY / 8 + 1);
                    int zSize = byte0 + 1;
#if LOGGING
                    t0 = DateTime.UtcNow;
#endif
                    if (noiseCache == null || noiseCache.Length != xSize * ySize * zSize)
                    {
                        noiseCache = new double[xSize * ySize * zSize];
                    }
#if LOGGING
                    if (enableVerboseLogging) { 
                        time_Prep_AllocNoiseCache = (float)(DateTime.UtcNow - t0).TotalMilliseconds;
                        time_Preparation = time_Prep_GetBiomes + time_Prep_SandNoise + time_Prep_GravelNoise + time_Prep_StoneNoise + time_Prep_AllocNoiseCache;
                    }
#endif

                    // Initialize octave-by-octave noise generation
                    byte byte0_1 = 4;
                    int xSizeNoise = byte0_1 + 1;
                    byte ySizeNoise = (byte)(world.worldDimensionY * world.chunkSizeY / 8 + 1);
                    int zSizeNoise = byte0_1 + 1;
                    int totalNoiseCells = xSizeNoise * ySizeNoise * zSizeNoise;
                    
                    // Allocate noise arrays
                    if (noise1 == null || noise1.Length != totalNoiseCells) noise1 = new double[totalNoiseCells];
                    else System.Array.Clear(noise1, 0, noise1.Length);
                    
                    if (noise2 == null || noise2.Length != totalNoiseCells) noise2 = new double[totalNoiseCells];
                    else System.Array.Clear(noise2, 0, noise2.Length);
                    
                    if (noise3 == null || noise3.Length != totalNoiseCells) noise3 = new double[totalNoiseCells];
                    else System.Array.Clear(noise3, 0, noise3.Length);
                    
                    int noise6Size = xSizeNoise * zSizeNoise;
                    if (noise6 == null || noise6.Length != noise6Size) noise6 = new double[noise6Size];
                    else System.Array.Clear(noise6, 0, noise6.Length);
                    
                    if (noise7 == null || noise7.Length != noise6Size) noise7 = new double[noise6Size];
                    else System.Array.Clear(noise7, 0, noise7.Length);
                    
                    currentNoiseGenerator = 0;
                    currentOctave = 0;
                    currentState = GenerationState.Prepare_NoiseOctaves;
                }
                break;

            case GenerationState.Prepare_NoiseOctaves:
                {
                    // OPTIMIZATION: Process noise generators octave-by-octave to prevent huge frame spikes
                    // Each octave takes ~7-20ms, spreading 277ms across ~40 frames
                    byte byte0 = 4;
                    int xSize = byte0 + 1;
                    byte ySize = (byte)(world.worldDimensionY * world.chunkSizeY / 8 + 1);
                    int zSize = byte0 + 1;
                    double d0 = 684.412D;
                    double d1 = 684.412D;

#if LOGGING
                    DateTime tNoiseStart = DateTime.UtcNow;
#endif

                    // Process current octave of current generator
                    if (currentNoiseGenerator == 0) // noise1 (16 octaves)
                    {
                        if (currentOctave == 0) currentNoiseOutput = noise1;
                        double frequency = System.Math.Pow(0.5, currentOctave);
                        // CRITICAL: Handle coordinate flip for Minecraft right-handed system
                        // Apply built-in offset (converted to noise grid scale: blocks/4)
                        int noiseStartX = _GetTerrainNoiseStartX(currentChunkX, byte0);
                        int noiseStartZ = _GetTerrainNoiseStartZ(currentChunkZ, byte0);
                        noiseGen1.generatorCollection[currentOctave].generateNoiseArray(currentNoiseOutput, noiseStartX, 0, noiseStartZ, xSize, ySize, zSize, d0 * frequency, d1 * frequency, d0 * frequency, frequency);
                        currentOctave++;
                        
                        if (currentOctave >= 16) {
                            noise1 = currentNoiseOutput;
#if LOGGING
                            if (enableVerboseLogging) { noiseGen1Cells = xSize * ySize * zSize; }
#endif
                            currentNoiseGenerator = 1;
                            currentOctave = 0;
                        }
                    }
                    else if (currentNoiseGenerator == 1) // noise2 (16 octaves)
                    {
                        if (currentOctave == 0) currentNoiseOutput = noise2;
                        double frequency = System.Math.Pow(0.5, currentOctave);
                        // CRITICAL: Handle coordinate flip for Minecraft right-handed system
                        // Apply built-in offset (converted to noise grid scale: blocks/4)
                        int noiseStartX = _GetTerrainNoiseStartX(currentChunkX, byte0);
                        int noiseStartZ = _GetTerrainNoiseStartZ(currentChunkZ, byte0);
                        noiseGen2.generatorCollection[currentOctave].generateNoiseArray(currentNoiseOutput, noiseStartX, 0, noiseStartZ, xSize, ySize, zSize, d0 * frequency, d1 * frequency, d0 * frequency, frequency);
                        currentOctave++;
                        
                        if (currentOctave >= 16) {
                            noise2 = currentNoiseOutput;
#if LOGGING
                            if (enableVerboseLogging) { noiseGen2Cells = xSize * ySize * zSize; }
#endif
                            currentNoiseGenerator = 2;
                            currentOctave = 0;
                        }
                    }
                    else if (currentNoiseGenerator == 2) // noise3 (8 octaves)
                    {
                        if (currentOctave == 0) currentNoiseOutput = noise3;
                        double frequency = System.Math.Pow(0.5, currentOctave);
                        // CRITICAL: Handle coordinate flip for Minecraft right-handed system
                        // Apply built-in offset (converted to noise grid scale: blocks/4)
                        int noiseStartX = _GetTerrainNoiseStartX(currentChunkX, byte0);
                        int noiseStartZ = _GetTerrainNoiseStartZ(currentChunkZ, byte0);
                        noiseGen3.generatorCollection[currentOctave].generateNoiseArray(currentNoiseOutput, noiseStartX, 0, noiseStartZ, xSize, ySize, zSize, (d0 / 80.0D) * frequency, (d1 / 160.0D) * frequency, (d0 / 80.0D) * frequency, frequency);
                        currentOctave++;
                        
                        if (currentOctave >= 8) {
                            noise3 = currentNoiseOutput;
#if LOGGING
                            if (enableVerboseLogging) { noiseGen3Cells = xSize * ySize * zSize; }
#endif
                            currentNoiseGenerator = 3;
                        }
                    }
                    else if (currentNoiseGenerator == 3) // noise6 (10 octaves, 2D)
                    {
                        // CRITICAL: Handle coordinate flip for Minecraft right-handed system
                        // Apply built-in offset (converted to noise grid scale: blocks/4)
                        int noiseStartX = _GetTerrainNoiseStartX(currentChunkX, byte0);
                        int noiseStartZ = _GetTerrainNoiseStartZ(currentChunkZ, byte0);
                        noise6 = noiseGen6.generateNoiseArray(noise6, noiseStartX, noiseStartZ, xSize, zSize, 1.121D, 1.121D, 0.5D);
#if LOGGING
                        if (enableVerboseLogging) { noise6Cells = xSize * zSize; }
#endif
                        currentNoiseGenerator = 4;
                    }
                    else if (currentNoiseGenerator == 4) // noise7 (16 octaves, 2D)
                    {
                        // CRITICAL: Handle coordinate flip for Minecraft right-handed system
                        // Apply built-in offset (converted to noise grid scale: blocks/4)
                        int noiseStartX = _GetTerrainNoiseStartX(currentChunkX, byte0);
                        int noiseStartZ = _GetTerrainNoiseStartZ(currentChunkZ, byte0);
                        noise7 = noiseGen7.generateNoiseArray(noise7, noiseStartX, noiseStartZ, xSize, zSize, 200.0D, 200.0D, 0.5D);
#if LOGGING
                        if (enableVerboseLogging) { noise7Cells = xSize * zSize; }
#endif
                        
                        // All noise generation complete
                        noiseCombine_x = 0;
                        noiseCombine_k1 = 0;
                        noiseCombine_l1 = 0;
                        currentState = GenerationState.Prepare_CombineNoise;
                    }

#if LOGGING
                    // Accumulate timing for noise generation (across all octaves)
                    if (enableVerboseLogging)
                    {
                        float elapsed = (float)(DateTime.UtcNow - tNoiseStart).TotalMilliseconds;
                        if (currentNoiseGenerator == 0) time_NoiseGen1 += elapsed;
                        else if (currentNoiseGenerator == 1) time_NoiseGen2 += elapsed;
                        else if (currentNoiseGenerator == 2) time_NoiseGen3 += elapsed;
                        else if (currentNoiseGenerator == 3) time_Noise6 += elapsed;
                        else if (currentNoiseGenerator == 4) time_Noise7 += elapsed;
                    }
#endif

                    break;
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

#if LOGGING
                    DateTime tCombine = DateTime.MinValue; if (enableVerboseLogging) tCombine = DateTime.UtcNow;
#endif
                    // Cache noise arrays locally to reduce field access overhead
                    double[] n1 = noise1;
                    double[] n2 = noise2;
                    double[] n3 = noise3;
                    double[] n6 = noise6;
                    double[] n7 = noise7;
                    double[] nCache = noiseCache;
                    int k1 = noiseCombine_k1;
                    int l1 = noiseCombine_l1;
                    
                    // Precompute constants
                    double ySizeD = (double)ySize;
                    double ySizeHalf = ySizeD * 0.5D;
                    double ySize_m4 = ySize - 4;
                    double inv512 = 1.0D / 512.0D;
                    double inv8000 = 1.0D / 8000.0D;
                    double inv16 = 1.0D / 16.0D;
                    
                    for (int z = 0; z < zSize; z++)
                    {
                        int i3 = z * i2 + i2 / 2;
                        int tempIndex = k2 * 16 + i3;
                        double d2 = temp[tempIndex];
                        double d3 = rain[tempIndex] * d2;
                        double d4 = 1.0D - d3;
                        d4 *= d4;
                        d4 *= d4;
                        d4 = 1.0D - d4;
                        double d5 = (n6[l1] + 256.0D) * inv512;
                        d5 *= d4;
                        if (d5 > 1.0D) d5 = 1.0D;
                        
                        double d6 = n7[l1] * inv8000;
                        if (d6 < 0.0D) d6 = -d6 * 0.3D;
                        
                        d6 = d6 * 3.0D - 2.0D;
                        if (d6 < 0.0D)
                        {
                            d6 *= 0.5D;
                            if (d6 < -1D) d6 = -1D;
                            d6 *= 0.3571428571428571D; // 1/2.8 pre-calculated
                            d5 = 0.0D;
                        }
                        else
                        {
                            if (d6 > 1.0D) d6 = 1.0D;
                            d6 *= 0.125D;
                        }

                        if (d5 < 0.0D) d5 = 0.0D;
                        
                        d5 += 0.5D;
                        d6 = (d6 * ySizeD) * inv16;
                        double d7 = ySizeHalf + d6 * 4.0D;

                        for (int y = 0; y < ySize; y++)
                        {
                            double d9 = (((double)y - d7) * 12D) / d5;
                            if (d9 < 0.0D) d9 *= 4.0D;
                            
                            double d10 = n1[k1] * inv512;
                            double d11 = n2[k1] * inv512;
                            double d12 = (n3[k1] * 0.1D + 1.0D) * 0.5D;

                            double d8;
                            if (d12 < 0.0D) d8 = d10;
                            else if (d12 > 1.0D) d8 = d11;
                            else d8 = d10 + (d11 - d10) * d12;
                            
                            d8 -= d9;
                            if (y > ySize_m4)
                            {
                                double d13 = (double)((float)(y - ySize_m4) * 0.33333334F);
                                d8 = d8 * (1.0D - d13) - 10.0D * d13;
                            }
                            nCache[k1] = d8;
                            k1++;
                        }
                        l1++;
#if LOGGING
                        if (enableVerboseLogging) noiseCombineCells += ySize;
#endif
                    }
#if LOGGING
                    if (enableVerboseLogging) time_NoiseCombine += (float)(DateTime.UtcNow - tCombine).TotalMilliseconds;
#endif
                    
                    noiseCombine_k1 = k1;
                    noiseCombine_l1 = l1;

                    noiseCombine_x++;
                    if (noiseCombine_x >= xSize)
                    {
                        noise1 = noise2 = noise3 = noise6 = noise7 = null;
                        initRand(currentChunkX, currentChunkZ);
                        currentState = GenerationState.GeneratingTerrain;
#if LOGGING
                        // Cache timings from first chunk in this column
                        if (enableVerboseLogging && !timingsCached)
                        {
                            cached_time_Preparation = time_Preparation;
                            cached_time_Prep_GetBiomes = time_Prep_GetBiomes;
                            cached_time_Prep_SandNoise = time_Prep_SandNoise;
                            cached_time_Prep_GravelNoise = time_Prep_GravelNoise;
                            cached_time_Prep_StoneNoise = time_Prep_StoneNoise;
                            cached_time_Prep_AllocNoiseCache = time_Prep_AllocNoiseCache;
                            cached_time_NoiseGen1 = time_NoiseGen1;
                            cached_time_NoiseGen2 = time_NoiseGen2;
                            cached_time_NoiseGen3 = time_NoiseGen3;
                            cached_time_Noise6 = time_Noise6;
                            cached_time_Noise7 = time_Noise7;
                            cached_time_NoiseCombine = time_NoiseCombine;
                            cached_noiseGen1Cells = noiseGen1Cells;
                            cached_noiseGen2Cells = noiseGen2Cells;
                            cached_noiseGen3Cells = noiseGen3Cells;
                            cached_noise6Cells = noise6Cells;
                            cached_noise7Cells = noise7Cells;
                            cached_noiseCombineCells = noiseCombineCells;
                            timingsCached = true;
                        }
#endif
                    }

                    break;
                }

            case GenerationState.GeneratingTerrain:
{
#if LOGGING
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

                // Precompute common strides and cache locals
                int sizeXZ = world.chunkSizeXZ;
                int sizeY = world.chunkSizeY;
                int xyStride = sizeXZ * sizeXZ;
                int chunkYBase = currentChunkY * sizeY;
                byte[] chunkData = workingChunkData;
                int[] highestStone = highestStoneYColumn;
                double[] nCache = noiseCache;
                double[] temps = wcm.temperatures;

                for (int i = 0; i < 4; i++) 
                {
                    if (terrain_xPiece >= byte0_gt) break;

                    int xPieceOffset = terrain_xPiece * 4;
                    int zPieceOffset = terrain_zPiece * 4;
                    
                    for (int yPiece = startYPiece; yPiece < endYPiece; yPiece++)
                    {
                        // Cache noise indices
                        int idx00 = ((terrain_xPiece + 0) * l_gt + (terrain_zPiece + 0)) * b2_gt;
                        int idx01 = ((terrain_xPiece + 0) * l_gt + (terrain_zPiece + 1)) * b2_gt;
                        int idx10 = ((terrain_xPiece + 1) * l_gt + (terrain_zPiece + 0)) * b2_gt;
                        int idx11 = ((terrain_xPiece + 1) * l_gt + (terrain_zPiece + 1)) * b2_gt;
                        
                        double d1 = nCache[idx00 + yPiece];
                        double d2 = nCache[idx01 + yPiece];
                        double d3 = nCache[idx10 + yPiece];
                        double d4 = nCache[idx11 + yPiece];
                        double d5 = (nCache[idx00 + yPiece + 1] - d1) * 0.125D;
                        double d6 = (nCache[idx01 + yPiece + 1] - d2) * 0.125D;
                        double d7 = (nCache[idx10 + yPiece + 1] - d3) * 0.125D;
                        double d8 = (nCache[idx11 + yPiece + 1] - d4) * 0.125D;
                        
                        int yBase = yPiece * 8;
                        
                        for (int l1 = 0; l1 < 8; l1++)
                        {
                            double d10 = d1;
                            double d11 = d2;
                            double d12 = (d3 - d1) * 0.25D;
                            double d13 = (d4 - d2) * 0.25D;
                            
                            int yLoc = yBase + l1;
                            int localY = yLoc - chunkYBase;
                            bool yInRange = (localY >= 0 && localY < sizeY);
                            
                            if (yInRange)
                            {
                                int yOffset = localY * xyStride;
                                
                                for (int i2 = 0; i2 < 4; i2++)
                                {
                                    int xLoc = i2 + xPieceOffset;
                                    double d15 = d10;
                                    double d16 = (d11 - d10) * 0.25D;
                                    
                                    for (int k2 = 0; k2 < 4; k2++)
                                    {
                                        int currentZ = k2 + zPieceOffset;
                                        double d17 = temps[(xPieceOffset + i2) * 16 + (zPieceOffset + k2)];
                                        
                                        BlockMaterial block = BlockMaterial.AIR;
                                        if (yLoc < oceanHeight_gt)
                                        {
                                            if (d17 < 0.5D && yLoc >= 63) block = BlockMaterial.ICE;
                                            else block = BlockMaterial.STATIONARY_WATER;
                                        }
                                        if (d15 > 0.0D) block = BlockMaterial.STONE;

                                        // CRITICAL: If X-axis is flipped, flip the local X coordinate too
                                        int finalX = _MapTerrainLocalX(xLoc, sizeXZ);
                                        int index = yOffset + currentZ * sizeXZ + finalX;
                                        chunkData[index] = (byte)block;
#if LOGGING
                                        if (enableVerboseLogging)
                                        {
                                            terrainVoxelsVisited++;
                                            terrainAssignments++;
                                            if (block == BlockMaterial.STONE) terrainStoneAssignments++;
                                            else if (block == BlockMaterial.STATIONARY_WATER) terrainWaterAssignments++;
                                            else if (block == BlockMaterial.ICE) terrainIceAssignments++;
                                        }
#endif
                                        // Track highest STONE Y for this column
                                        if (block == BlockMaterial.STONE)
                                        {
                                            int colIndex = currentZ * sizeXZ + finalX;
                                            if (localY > highestStone[colIndex]) highestStone[colIndex] = localY;
                                        }
                                        d15 += d16;
                                    }
                                    d10 += d12;
                                    d11 += d13;
                                }
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
                    terrain_gen_step_count++;
                }

#if LOGGING
                if (enableVerboseLogging) time_GeneratingTerrain += (float)(DateTime.UtcNow - stepStartTime).TotalMilliseconds;
#endif

                if (terrain_xPiece >= byte0_gt)
                {
                    // Generate debug pillar if enabled
                    if (!match1to1TerrainBaseline && generateDebugPillar)
                    {
                        GenerateDebugPillar();
                    }
                    
                    currentState = GenerationState.ReplacingBiomeBlocks;
                }
                break;
}
            
            case GenerationState.ReplacingBiomeBlocks:
#if LOGGING
                if (enableVerboseLogging)
                {
                    logBuilder.Clear();
                    logBuilder.Append("[McTerrainGenerator.Step] State: ReplacingBiomeBlocks for chunk (").Append(currentChunkX).Append(",").Append(currentChunkY).Append(",").Append(currentChunkZ).Append(")");
                    Debug.Log(logBuilder.ToString());
                }
#endif
                {
                    int sizeXZ = world.chunkSizeXZ;
                    int sizeY = world.chunkSizeY;
                    int xyStride = sizeXZ * sizeXZ;
                    int chunkYBase = currentChunkY * sizeY;
                    byte[] data = workingChunkData;
                    
                    // Cache locals
                    BetaBiomeEnum[] biomes = currentChunkBiomes;
                    double[] sandN = sandNoise;
                    double[] gravelN = gravelNoise;
                    double[] stoneN = stoneNoise;
                    int[] depthCache = columnDepthCache;
                    byte[] fillerCache = columnFillerCache;
                    int[] highestStone = highestStoneYColumn;
                    JavaRandom random = rand;

                    // CRITICAL: Process from TOP to BOTTOM of chunk to match Beta 1.7.3's top-down replacement
                    for (int x = 0; x < sizeXZ; x++)
                    {
                        for (int z = 0; z < sizeXZ; z++)
                        {
                            // CRITICAL: If X-axis is flipped, flip local X coordinate
                            int finalX = _MapTerrainLocalX(x, sizeXZ);
                            int colIndex = z * sizeXZ + finalX;

                            int biomeIndex = _GetSurfaceBiomeIndex(x, z, sizeXZ);
                            BetaBiomeEnum biome = biomes[biomeIndex];
                            bool sand = sandN[biomeIndex] + random.NextDouble() * 0.2D > 0.0D;
                            bool gravel = gravelN[biomeIndex] + random.NextDouble() * 0.2D > 3D;
                            int depth = (int)(stoneN[biomeIndex] * 0.33333333D + 3D + random.NextDouble() * 0.25D);

                            int prevDepth = depthCache[colIndex];
                            
                            // Beta 1.7.3 processes top-to-bottom, so we need to process from highest Y downward
                            for (int yLocal = sizeY - 1; yLocal >= 0; yLocal--)
                            {
                                int idx = yLocal * xyStride + colIndex;
                                byte blockID = data[idx];
                                int globalY = chunkYBase + yLocal;
                                
                                // Skip non-stone blocks
                                if (blockID == 0)
                                {
                                    // Air - reset depth counter
                                    prevDepth = -1;
                                }
                                else if (blockID == stoneBlockID)
                                {
                                    // Found stone - apply surface materials
                                    if (prevDepth == -1)
                                    {
                                        // First stone from top - determine materials
                                        BlockMaterial topBlock = (BlockMaterial)BiomeOld.top(biome);
                                        BlockMaterial fillerBlock = (BlockMaterial)BiomeOld.filler(biome);
                                        
                                        if (depth <= 0)
                                        {
                                            topBlock = BlockMaterial.AIR;
                                            fillerBlock = BlockMaterial.STONE;
                                        }
                                        else if (globalY >= 60 && globalY <= 65)
                                        {
                                            topBlock = (BlockMaterial)BiomeOld.top(biome);
                                            fillerBlock = (BlockMaterial)BiomeOld.filler(biome);
                                            if (gravel) { topBlock = BlockMaterial.AIR; fillerBlock = BlockMaterial.GRAVEL; }
                                            if (sand) { topBlock = BlockMaterial.SAND; fillerBlock = BlockMaterial.SAND; }
                                        }
                                        
                                        if (globalY < 64 && topBlock == BlockMaterial.AIR)
                                        {
                                            topBlock = BlockMaterial.STATIONARY_WATER;
                                        }
                                        
                                        prevDepth = depth;
                                        
                                        // Cache the filler material for this column
                                        fillerCache[colIndex] = (byte)fillerBlock;
                                        
                                        if (globalY >= 63)
                                        {
                                            data[idx] = (byte)topBlock;
#if LOGGING
                                            if (enableVerboseLogging)
                                            {
                                                biomeColumnsProcessed++;
                                                biomeTopAssignments++;
                                            }
#endif
                                        }
                                        else
                                        {
                                            data[idx] = (byte)fillerBlock;
#if LOGGING
                                            if (enableVerboseLogging)
                                            {
                                                if (fillerBlock == BlockMaterial.SAND) biomeSandAssignments++;
                                                else if (fillerBlock == BlockMaterial.SANDSTONE) biomeSandstoneAssignments++;
                                                else if (fillerBlock == BlockMaterial.GRAVEL) biomeGravelAssignments++;
                                                else biomeFillerAssignments++;
                                            }
#endif
                                        }
                                    }
                                    else if (prevDepth > 0)
                                    {
                                        // CRITICAL: Match Beta 1.7.3 logic exactly
                                        // Decrement depth FIRST
                                        prevDepth--;
                                        
                                        // Get cached filler material (don't reset it from biome!)
                                        BlockMaterial fillerBlock = (BlockMaterial)fillerCache[colIndex];
                                        
                                        // Place the filler block
                                        data[idx] = (byte)fillerBlock;
                                        
                                        // AFTER placing, check if we hit 0 depth with sand
                                        if (prevDepth == 0 && fillerBlock == BlockMaterial.SAND)
                                        {
                                            // Add random 0-3 more layers of sandstone
                                            prevDepth = random.NextInt(4);
                                            fillerBlock = BlockMaterial.SANDSTONE;
                                            fillerCache[colIndex] = (byte)fillerBlock;  // Update cache!
                                        }
                                        
#if LOGGING
                                        if (enableVerboseLogging)
                                        {
                                            if (fillerBlock == BlockMaterial.SAND) biomeSandAssignments++;
                                            else if (fillerBlock == BlockMaterial.SANDSTONE) biomeSandstoneAssignments++;
                                            else if (fillerBlock == BlockMaterial.GRAVEL) biomeGravelAssignments++;
                                            else biomeFillerAssignments++;
                                        }
#endif
                                    }
                                }
                            }
                            
                            // Save depth AND filler material for next chunk
                            depthCache[colIndex] = prevDepth;
                            // Note: fillerCache is updated during processing
                        }
                    }

                    // Apply bedrock only in bottom world chunk (global Y 0..4)
                    if (currentChunkY == 0)
                    {
                        int maxLocal = sizeY - 1;
                        if (maxLocal > 4) maxLocal = 4;
                        byte bedrockID = bedrockBlockID;
                        for (int yLocal = 0; yLocal <= maxLocal; yLocal++)
                        {
                            int globalY = chunkYBase + yLocal;
                            int yBase = yLocal * xyStride;
                            for (int z = 0; z < sizeXZ; z++)
                            {
                                int zBase = z * sizeXZ;
                                for (int x = 0; x < sizeXZ; x++)
                                {
                                    if (globalY <= random.NextInt(5))
                                    {
                                        int idx = yBase + zBase + x;
                                        data[idx] = bedrockID;
#if LOGGING
                                        if (enableVerboseLogging) biomeBedrockAssignments++;
#endif
                                    }
                                }
                            }
                        }
                    }
                }
                
#if LOGGING
                if (enableVerboseLogging) time_ReplacingBiomes += (float)(DateTime.UtcNow - stepStartTime).TotalMilliseconds;
#endif
                
                // CRITICAL: Only decorate the TOP chunk in a column (highest Y chunk)
                // Beta 1.7.3 decorates per-column, not per-chunk
                if (!match1to1TerrainBaseline && currentChunkY == world.worldDimensionY - 1)
                {
                    decoration_step = 0;
                    decoration_feature_index = 0;
                    decoration_feature_count = 0;
                    currentState = GenerationState.DecoratingTerrain;
                }
                else
                {
                    currentState = GenerationState.Complete;
                }
                
                break;

            case GenerationState.DecoratingTerrain:
                {
                    // BETA 1.7.3 DECORATION: Trees, tall grass, and flowers
                    // This runs ONLY for the top chunk in each column
                    
                    if (decoration_step == 0)
                    {
                        // Step 0: Initialize decoration random seed (EXACTLY like Beta 1.7.3)
                        // From ChunkProviderGenerate.java line 315-318
                        // Use the world seed to initialize a temporary random for decoration
                        int worldSeed = McUtils.GetMinecraftSeed(world.worldSeedString);
                        JavaRandom seedRand = new JavaRandom(worldSeed);
                        long var7 = seedRand.NextLong() / 2L * 2L + 1L;
                        long var9 = seedRand.NextLong() / 2L * 2L + 1L;
                        rand.SetSeed((long)currentChunkX * var7 + (long)currentChunkZ * var9 ^ (long)worldSeed);
                        decoration_step++;
                    }
                    else if (decoration_step == 1)
                    {
                        // Step 1: Generate trees
                        // Get biome at center of chunk (EXACTLY like Beta 1.7.3 line 314)
                        // Use the center biome from the already-computed biome grid
                        // Beta 1.7.3 gets biome at chunk center (8,8)
                        int centerBiomeIndex = 8 * 16 + 8; // Center of 16x16 grid
                        BetaBiomeEnum centerBiome = currentChunkBiomes[centerBiomeIndex];
                        
                        // Calculate tree count EXACTLY like Beta 1.7.3 (lines 410-443)
                        decoration_feature_count = BetaBiome.getTreesPerChunk(rand, treeNoise, currentChunkX, currentChunkZ, centerBiome);
                        decoration_feature_index = 0;
                        decoration_step++;
                    }
                    else if (decoration_step == 2)
                    {
                        // Step 2: Place trees one at a time (time-sliced)
                        if (decoration_feature_index < decoration_feature_count)
                        {
                            // currentChunkX is centered chunk coordinate (same as Minecraft chunk coord)
                            int worldX = currentChunkX * 16;
                            int worldZ = currentChunkZ * 16;
                            int treeX = worldX + rand.NextInt(16) + 8 + BUILTIN_OFFSET_X;
                            int treeZ = worldZ + rand.NextInt(16) + 8 + BUILTIN_OFFSET_Z;
                            
                            // Apply built-in offset to match terrain coordinate system
                            
                            // Find the highest solid block at this X,Z
                            int treeY = GetHighestSolidBlock(treeX, treeZ);
                            if (treeY > 0 && treeY < world.worldDimensionY * world.chunkSizeY - 7)
                            {
                                GenerateTree(treeX, treeY, treeZ);
                            }
                            
                            decoration_feature_index++;
                        }
                        else
                        {
                            decoration_step++;
                        }
                    }
                    else if (decoration_step == 3)
                    {
                        // Step 3: Generate yellow flowers
                        int centerBiomeIndex = 8 * 16 + 8; // Center of 16x16 grid
                        BetaBiomeEnum centerBiome = currentChunkBiomes[centerBiomeIndex];
                        
                        decoration_feature_count = BetaBiome.getFlowersPerChunk(centerBiome);
                        decoration_feature_index = 0;
                        decoration_step++;
                    }
                    else if (decoration_step == 4)
                    {
                        // Step 4: Place yellow flowers (time-sliced)
                        if (decoration_feature_index < decoration_feature_count)
                        {
                            int worldX = currentChunkX * 16;
                            int worldZ = currentChunkZ * 16;
                            int flowerX = worldX + rand.NextInt(16) + 8 + BUILTIN_OFFSET_X;
                            int flowerZ = worldZ + rand.NextInt(16) + 8 + BUILTIN_OFFSET_Z;
                            int flowerY = rand.NextInt(world.worldDimensionY * world.chunkSizeY);
                            
                            GenerateFlower(flowerX, flowerY, flowerZ, flowerYellowBlockID);
                            decoration_feature_index++;
                        }
                        else
                        {
                            decoration_step++;
                        }
                    }
                    else if (decoration_step == 5)
                    {
                        // Step 5: Generate tall grass
                        int centerBiomeIndex = 8 * 16 + 8; // Center of 16x16 grid
                        BetaBiomeEnum centerBiome = currentChunkBiomes[centerBiomeIndex];
                        
                        decoration_feature_count = BetaBiome.getGrassPerChunk(centerBiome);
                        decoration_feature_index = 0;
                        decoration_step++;
                    }
                    else if (decoration_step == 6)
                    {
                        // Step 6: Place tall grass (time-sliced, 128 attempts per placement like Beta 1.7.3)
                        if (decoration_feature_index < decoration_feature_count)
                        {
                            int worldX = currentChunkX * 16;
                            int worldZ = currentChunkZ * 16;
                            int grassX = worldX + rand.NextInt(16) + 8 + BUILTIN_OFFSET_X;
                            int grassZ = worldZ + rand.NextInt(16) + 8 + BUILTIN_OFFSET_Z;
                            int grassY = rand.NextInt(world.worldDimensionY * world.chunkSizeY);
                            
                            GenerateTallGrass(grassX, grassY, grassZ);
                            decoration_feature_index++;
                        }
                        else
                        {
                            decoration_step++;
                        }
                    }
                    else if (decoration_step == 7)
                    {
                        // Step 7: Generate red flowers (Beta 1.7.3 lines 527-532: 50% chance)
                        if (rand.NextInt(2) == 0)
                        {
                            int worldX = currentChunkX * 16;
                            int worldZ = currentChunkZ * 16;
                            int flowerX = worldX + rand.NextInt(16) + 8 + BUILTIN_OFFSET_X;
                            int flowerZ = worldZ + rand.NextInt(16) + 8 + BUILTIN_OFFSET_Z;
                            int flowerY = rand.NextInt(world.worldDimensionY * world.chunkSizeY);
                            
                            GenerateFlower(flowerX, flowerY, flowerZ, flowerRedBlockID);
                        }
                        decoration_step++;
                    }
                    else
                    {
                        // Decoration complete
                currentState = GenerationState.Complete;
                    }
                }
                break;

            case GenerationState.Complete:
                completedData = workingChunkData;

                // DIAGNOSTIC: Dump block counts in the completed chunk
                {
                    int diagStone = 0, diagAir = 0, diagWater = 0, diagBedrock = 0, diagOther = 0;
                    for (int di = 0; di < workingChunkData.Length; di++)
                    {
                        byte b = workingChunkData[di];
                        if (b == 0) diagAir++;
                        else if (b == stoneBlockID) diagStone++;
                        else if (b == waterBlockID) diagWater++;
                        else if (b == 7) diagBedrock++;
                        else diagOther++;
                    }
                    StringBuilder diagBuf = new StringBuilder();
                    diagBuf.Append("[CHUNK BLOCKS] (").Append(currentChunkX).Append(",").Append(currentChunkY).Append(",").Append(currentChunkZ).Append(") ");
                    diagBuf.Append("stone=").Append(diagStone).Append(" air=").Append(diagAir).Append(" water=").Append(diagWater);
                    diagBuf.Append(" bedrock=").Append(diagBedrock).Append(" other=").Append(diagOther);
                    diagBuf.Append(" gpu=").Append(gpuWorldgenReady ? "Y" : "N");
                    
                    // Dump a cross-section at localY=0 (first layer of this chunk)
                    int sXZ = world.chunkSizeXZ;
                    diagBuf.Append("\n  Y=0 row z=0: ");
                    for (int dx = 0; dx < sXZ; dx++)
                    {
                        diagBuf.Append(workingChunkData[0 * sXZ * sXZ + 0 * sXZ + dx]).Append(" ");
                    }
                    Debug.Log(diagBuf.ToString());
                }
#if LOGGING
                if (enableVerboseLogging)
                {
                    // Calculate actual per-chunk work time (excluding cached column preparation)
                    float actualChunkTime = time_GeneratingTerrain + time_ReplacingBiomes;
                    float totalTime = (float)(DateTime.UtcNow - time_Total_Start).TotalMilliseconds;
                    
                    // Use cached timings for display (they're computed once per column)
                    float display_time_Preparation = timingsCached ? cached_time_Preparation : time_Preparation;
                    float display_time_Prep_GetBiomes = timingsCached ? cached_time_Prep_GetBiomes : time_Prep_GetBiomes;
                    float display_time_Prep_SandNoise = timingsCached ? cached_time_Prep_SandNoise : time_Prep_SandNoise;
                    float display_time_Prep_GravelNoise = timingsCached ? cached_time_Prep_GravelNoise : time_Prep_GravelNoise;
                    float display_time_Prep_StoneNoise = timingsCached ? cached_time_Prep_StoneNoise : time_Prep_StoneNoise;
                    float display_time_Prep_AllocNoiseCache = timingsCached ? cached_time_Prep_AllocNoiseCache : time_Prep_AllocNoiseCache;
                    float display_time_NoiseGen1 = timingsCached ? cached_time_NoiseGen1 : time_NoiseGen1;
                    float display_time_NoiseGen2 = timingsCached ? cached_time_NoiseGen2 : time_NoiseGen2;
                    float display_time_NoiseGen3 = timingsCached ? cached_time_NoiseGen3 : time_NoiseGen3;
                    float display_time_Noise6 = timingsCached ? cached_time_Noise6 : time_Noise6;
                    float display_time_Noise7 = timingsCached ? cached_time_Noise7 : time_Noise7;
                    float display_time_NoiseCombine = timingsCached ? cached_time_NoiseCombine : time_NoiseCombine;
                    int display_noiseGen1Cells = timingsCached ? cached_noiseGen1Cells : noiseGen1Cells;
                    int display_noiseGen2Cells = timingsCached ? cached_noiseGen2Cells : noiseGen2Cells;
                    int display_noiseGen3Cells = timingsCached ? cached_noiseGen3Cells : noiseGen3Cells;
                    int display_noise6Cells = timingsCached ? cached_noise6Cells : noise6Cells;
                    int display_noise7Cells = timingsCached ? cached_noise7Cells : noise7Cells;
                    int display_noiseCombineCells = timingsCached ? cached_noiseCombineCells : noiseCombineCells;
                    
                    logBuilder.Clear();
                    logBuilder.Append("--- Terrain Gen Timings for Chunk (").Append(currentChunkX).Append(",").Append(currentChunkY).Append(",").Append(currentChunkZ).Append(") ---").AppendLine();
                    logBuilder.Append("1. Preparation (Total): ").Append(display_time_Preparation.ToString("F3")).Append(" ms");
                    if (timingsCached) logBuilder.Append(" [Cached per column]");
                    logBuilder.AppendLine();
                    logBuilder.Append("   1a. GetBiomes:       ").Append(display_time_Prep_GetBiomes.ToString("F3")).Append(" ms").AppendLine();
                    logBuilder.Append("   1b. SandNoise:       ").Append(display_time_Prep_SandNoise.ToString("F3")).Append(" ms").AppendLine();
                    logBuilder.Append("   1c. GravelNoise:     ").Append(display_time_Prep_GravelNoise.ToString("F3")).Append(" ms").AppendLine();
                    logBuilder.Append("   1d. StoneNoise:      ").Append(display_time_Prep_StoneNoise.ToString("F3")).Append(" ms").AppendLine();
                    logBuilder.Append("   1e. AllocNoiseCache: ").Append(display_time_Prep_AllocNoiseCache.ToString("F3")).Append(" ms").AppendLine();
                    logBuilder.Append("2. Noise3D Stages:");
                    if (timingsCached) logBuilder.Append(" [Cached per column]");
                    logBuilder.AppendLine();
                    logBuilder.Append("   2a. NoiseGen1:       ").Append(display_time_NoiseGen1.ToString("F3")).Append(" ms").Append(" (cells: ").Append(display_noiseGen1Cells).Append(")").AppendLine();
                    logBuilder.Append("   2b. NoiseGen2:       ").Append(display_time_NoiseGen2.ToString("F3")).Append(" ms").Append(" (cells: ").Append(display_noiseGen2Cells).Append(")").AppendLine();
                    logBuilder.Append("   2c. NoiseGen3:       ").Append(display_time_NoiseGen3.ToString("F3")).Append(" ms").Append(" (cells: ").Append(display_noiseGen3Cells).Append(")").AppendLine();
                    logBuilder.Append("   2d. Noise6:          ").Append(display_time_Noise6.ToString("F3")).Append(" ms").Append(" (cells: ").Append(display_noise6Cells).Append(")").AppendLine();
                    logBuilder.Append("   2e. Noise7:          ").Append(display_time_Noise7.ToString("F3")).Append(" ms").Append(" (cells: ").Append(display_noise7Cells).Append(")").AppendLine();
                    logBuilder.Append("   2f. CombineNoise:    ").Append(display_time_NoiseCombine.ToString("F3")).Append(" ms").Append(" (cells: ").Append(display_noiseCombineCells).Append(")").AppendLine();
                    logBuilder.Append("3. Terrain Generation:  ").Append(time_GeneratingTerrain.ToString("F3")).Append(" ms").Append(" (steps: ").Append(terrain_gen_step_count).Append(")").AppendLine();
                    logBuilder.Append("   3a. Voxels visited:  ").Append(terrainVoxelsVisited).Append(", Assignments: ").Append(terrainAssignments).AppendLine();
                    logBuilder.Append("   3b. Stone: ").Append(terrainStoneAssignments).Append(", Water: ").Append(terrainWaterAssignments).Append(", Ice: ").Append(terrainIceAssignments).AppendLine();
                    logBuilder.Append("4. Replace Biomes:      ").Append(time_ReplacingBiomes.ToString("F3")).Append(" ms").AppendLine();
                    logBuilder.Append("   4a. Columns visited: ").Append(256).AppendLine();
                    logBuilder.Append("   4b. Top: ").Append(biomeTopAssignments).Append(", Filler: ").Append(biomeFillerAssignments).Append(", Bedrock: ").Append(biomeBedrockAssignments).AppendLine();
                    logBuilder.Append("   4c. Water: ").Append(biomeWaterAssignments).Append(", Gravel: ").Append(biomeGravelAssignments).Append(", Sand: ").Append(biomeSandAssignments).Append(", Sandstone: ").Append(biomeSandstoneAssignments).AppendLine();
                    logBuilder.Append("--- Performance Summary ---").AppendLine();
                    logBuilder.Append("Actual Per-Chunk Work:  ").Append(actualChunkTime.ToString("F3")).Append(" ms (3+4 only)").AppendLine();
                    logBuilder.Append("Total Time-Sliced Gen:  ").Append(totalTime.ToString("F3")).Append(" ms (wall-clock time across all frames)").AppendLine();
                    logBuilder.Append("--- Per-Step Performance ---").AppendLine();
                    logBuilder.Append("Total Steps: ").Append(totalSteps).AppendLine();
                    logBuilder.Append("Last Step: ").Append(lastStepTime.ToString("F3")).Append(" ms").AppendLine();
                    logBuilder.Append("Max Step: ").Append(maxStepTime.ToString("F3")).Append(" ms").AppendLine();
                    logBuilder.Append("Min Step: ").Append(minStepTime.ToString("F3")).Append(" ms").AppendLine();
                    float avgStepTime = totalSteps > 0 ? totalTime / totalSteps : 0f;
                    logBuilder.Append("Avg Step: ").Append(avgStepTime.ToString("F3")).Append(" ms").AppendLine();
                    Debug.Log(logBuilder.ToString());
                }
#endif
                currentState = GenerationState.Idle;
                isComplete = true;
                break;

            default:
                currentState = GenerationState.Idle;
                isComplete = true;
                break;
        }
        
#if LOGGING
        if (enableVerboseLogging)
        {
            // Track per-step timing
            lastStepTime = (Time.realtimeSinceStartup - frameStepStart) * 1000f;
            if (lastStepTime > maxStepTime) maxStepTime = lastStepTime;
            if (lastStepTime < minStepTime) minStepTime = lastStepTime;
            totalSteps++;
        }
#endif
        
        return isComplete;
    }

    public void initRand(int chunkX, int chunkZ)
    {
        this.rand.SetSeed((long)chunkX * 341873128712L + (long)chunkZ * 132897987541L);
    }

    /// <summary>
    /// Get biome temperature and rainfall data for a chunk column.
    /// This data is cached during terrain generation and can be retrieved here.
    /// </summary>
    public void GetBiomeDataForChunk(int chunkX, int chunkZ, double[] outTemperatures, double[] outRainfall)
    {
        // Check if we have cached data for this chunk
        if (chunkX == lastBiomeChunkX && chunkZ == lastBiomeChunkZ && cachedTemperatures != null && cachedRainfall != null)
        {
            // Copy cached data to output arrays
            int copySize = System.Math.Min(outTemperatures.Length, cachedTemperatures.Length);
            System.Array.Copy(cachedTemperatures, outTemperatures, copySize);
            System.Array.Copy(cachedRainfall, outRainfall, copySize);
        }
        else
        {
            // Data not cached, fill with default values
            // This shouldn't happen if called right after generation
            for (int i = 0; i < outTemperatures.Length; i++)
            {
                outTemperatures[i] = 0.5;
                outRainfall[i] = 0.5;
            }
        }
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

    // ===== DECORATION METHODS (Beta 1.7.3 WorldGenTrees, WorldGenTallGrass, WorldGenFlowers) =====
    
    private int GetHighestSolidBlock(int globalX, int globalZ)
    {
        // Find the highest non-air block at this X,Z coordinate
        for (int y = world.worldDimensionY * world.chunkSizeY - 1; y >= 0; y--)
        {
            byte blockID = world.GetBlock(globalX, y, globalZ);
            if (blockID != airBlockID)
            {
                return y + 1; // Return the air block above the solid block
            }
        }
        return 0;
    }
    
    private void GenerateTree(int x, int y, int z)
    {
        // EXACT port of Beta 1.7.3 WorldGenTrees.java
        int treeHeight = rand.NextInt(3) + 4; // Random height 4-6
        
        // Check if there's space for the tree
        if (y < 1 || y + treeHeight + 1 > world.worldDimensionY * world.chunkSizeY) return;
        
        // Check if trunk can be placed (must be on grass or dirt)
        byte blockBelow = world.GetBlock(x, y - 1, z);
        if (blockBelow != grassBlockID && blockBelow != dirtBlockID) return;
        
        // Check if there's enough space for the canopy
        for (int checkY = y; checkY <= y + 1 + treeHeight; checkY++)
        {
            int radius = 1;
            if (checkY == y) radius = 0;
            if (checkY >= y + 1 + treeHeight - 2) radius = 2;
            
            for (int checkX = x - radius; checkX <= x + radius; checkX++)
            {
                for (int checkZ = z - radius; checkZ <= z + radius; checkZ++)
                {
                    if (checkY >= 0 && checkY < world.worldDimensionY * world.chunkSizeY)
                    {
                        byte checkBlock = world.GetBlock(checkX, checkY, checkZ);
                        if (checkBlock != airBlockID && checkBlock != leavesBlockID)
                        {
                            return; // Not enough space
                        }
                    }
                    else
                    {
                        return; // Out of bounds
                    }
                }
            }
        }
        
        // Place dirt under the tree
        world.SetBlock(x, y - 1, z, dirtBlockID);
        
        // Generate leaves (canopy)
        for (int leafY = y - 3 + treeHeight; leafY <= y + treeHeight; leafY++)
        {
            int yOffset = leafY - (y + treeHeight);
            int leafRadius = 1 - yOffset / 2;
            
            for (int leafX = x - leafRadius; leafX <= x + leafRadius; leafX++)
            {
                int xOffset = leafX - x;
                for (int leafZ = z - leafRadius; leafZ <= z + leafRadius; leafZ++)
                {
                    int zOffset = leafZ - z;
                    
                    // Beta 1.7.3 logic: skip corners randomly, except at top
                    if ((System.Math.Abs(xOffset) != leafRadius || System.Math.Abs(zOffset) != leafRadius || rand.NextInt(2) != 0 && yOffset != 0))
                    {
                        byte blockAtPos = world.GetBlock(leafX, leafY, leafZ);
                        // Only place leaves if air or existing leaves
                        if (blockAtPos == airBlockID || blockAtPos == leavesBlockID)
                        {
                            world.SetBlock(leafX, leafY, leafZ, leavesBlockID);
                        }
                    }
                }
            }
        }
        
        // Generate trunk (wood blocks)
        for (int trunkY = 0; trunkY < treeHeight; trunkY++)
        {
            byte blockAtPos = world.GetBlock(x, y + trunkY, z);
            if (blockAtPos == airBlockID || blockAtPos == leavesBlockID)
            {
                world.SetBlock(x, y + trunkY, z, logBlockID);
            }
        }
    }
    
    private void GenerateTallGrass(int x, int y, int z)
    {
        // EXACT port of Beta 1.7.3 WorldGenTallGrass.java
        // Find the surface by moving down
        while (true)
        {
            byte blockAtPos = world.GetBlock(x, y, z);
            if ((blockAtPos != airBlockID && blockAtPos != leavesBlockID) || y <= 0)
            {
                // Found surface or bedrock, now try 128 random placements
                for (int attempt = 0; attempt < 128; attempt++)
                {
                    int grassX = x + rand.NextInt(8) - rand.NextInt(8);
                    int grassY = y + rand.NextInt(4) - rand.NextInt(4);
                    int grassZ = z + rand.NextInt(8) - rand.NextInt(8);
                    
                    // Check if air block
                    if (grassY >= 0 && grassY < world.worldDimensionY * world.chunkSizeY)
                    {
                        byte blockAbove = world.GetBlock(grassX, grassY, grassZ);
                        if (blockAbove == airBlockID)
                        {
                            // Check if can stay (grass/dirt below, enough light)
                            if (grassY > 0)
                            {
                                byte blockBelow = world.GetBlock(grassX, grassY - 1, grassZ);
                                if (blockBelow == grassBlockID || blockBelow == dirtBlockID)
                                {
                                    // METADATA: Beta 1.7.3 uses metadata 1 for standard tall grass
                                    // We don't have metadata support yet, so just place the block
                                    world.SetBlock(grassX, grassY, grassZ, tallGrassBlockID);
                                }
                            }
                        }
                    }
                }
                return;
            }
            y--;
        }
    }
    
    private void GenerateFlower(int x, int y, int z, byte flowerBlockID)
    {
        // EXACT port of Beta 1.7.3 WorldGenFlowers.java
        // Try to place a single flower at this location
        if (y >= 0 && y < world.worldDimensionY * world.chunkSizeY)
        {
            byte blockAtPos = world.GetBlock(x, y, z);
            if (blockAtPos == airBlockID)
            {
                // Check if can stay (grass/dirt below, enough light)
                if (y > 0)
                {
                    byte blockBelow = world.GetBlock(x, y - 1, z);
                    if (blockBelow == grassBlockID || blockBelow == dirtBlockID)
                    {
                        world.SetBlock(x, y, z, flowerBlockID);
                    }
                }
            }
        }
    }
    
    private void GenerateDebugPillar()
    {
        // Generate a debug pillar at world origin (0,0) spanning full height
        // This helps visualize lighting and chunk boundaries
        int pillarX = 0;
        int pillarZ = 0;
        int worldHeight = world.worldDimensionY * world.chunkSizeY;
        
        // Use stone blocks for the pillar
        for (int y = 0; y < worldHeight; y++)
        {
            world.SetBlock(pillarX, y, pillarZ, stoneBlockID);
        }
        
#if LOGGING
        if (enableVerboseLogging)
        {
            Debug.Log($"[McTerrainGenerator] Generated debug pillar at ({pillarX}, {pillarZ}) spanning height 0-{worldHeight-1}");
        }
#endif

}
