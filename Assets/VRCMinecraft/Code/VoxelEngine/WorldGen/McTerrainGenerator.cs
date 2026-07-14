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
    Complete,
    GpuResidentComplete,
    // FINALIZE SLICING: decoration phase split out of Prepare_GpuFinalize (appended at the
    // end so existing state indices are untouched).
    Prepare_GpuDecorate
}

public enum GpuWorldgenReadbackPhase
{
    None,
    Climate,
    BaseColumn,
    DiagnosticNoise1,
    DiagnosticNoise2,
    DiagnosticNoise3,
    DiagnosticNoise6,
    DiagnosticNoise7,
    DiagnosticDensity
}

// [Singleton] removed: McWorld now supports a 2nd terrain generator instance
// (terrainGenerator2) for concurrent column generation. VRRefAssist auto-injects all
// fields whose type is a registered singleton, which would force terrainGenerator2 to
// point at the single instance. terrainGenerator / terrainGenerator2 are serialized refs
// on McWorld instead. The generator's own `world` field is still injected (McWorld remains [Singleton]).
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
    public bool generateCaves = true;

    [Header("GPU Worldgen")]
    public bool enableGpuWorldgen = true;
    [Tooltip("Read back finalized GPU columns as single-channel block IDs when supported. Disable this if a target platform rejects R8 readback.")]
    public bool useSingleChannelGpuColumnReadback = true;
    public Material gpuNoiseOctaveMaterial;
    public Material gpuNoiseCombineMaterial;
    public Material gpuColumnBaseFillMaterial;
    public Material gpuColumnSurfaceInfoMaterial;
    public Material gpuColumnSurfaceReplaceMaterial;
    public Material gpuColumnDecorationMaterial;
    public Material gpuColumnTreeDecorationMaterial;
    public Material gpuCaveCarveMaterial;

    [Header("GPU Noise Diagnostics")]
    public bool enableGpuNoiseDiagnostics = false;
    public bool gpuNoiseDiagnosticDumpAllCells = true;
    public int gpuNoiseDiagnosticChunkX = 0;
    public int gpuNoiseDiagnosticChunkZ = 0;
    public bool gpuNoiseDiagnosticRunOncePerChunk = true;

    [Header("Structure & Feature Templates")]
    public McStructureTemplate[] structureTemplates;

#if LOGGING
    [Header("Debugging")]
    public bool enableVerboseLogging = true;
    
    [Header("Performance Profiling")]
    public bool enableDetailedTimings = true;

    // CPU/GPU path tracing one-shot guard
    private bool dbg_loggedFirstGpuChunkCopy = false;
    
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

    // Centralized aggregate window for McWorld's profiler log.
    private int agg_chunksCompleted;
    private int agg_cachedColumnsUsed;
    private float agg_time_Preparation;
    private float agg_time_Prep_GetBiomes;
    private float agg_time_Prep_SandNoise;
    private float agg_time_Prep_GravelNoise;
    private float agg_time_Prep_StoneNoise;
    private float agg_time_Prep_AllocNoiseCache;
    private float agg_time_NoiseGen1;
    private float agg_time_NoiseGen2;
    private float agg_time_NoiseGen3;
    private float agg_time_Noise6;
    private float agg_time_Noise7;
    private float agg_time_NoiseCombine;
    private float agg_time_GeneratingTerrain;
    private float agg_time_ReplacingBiomes;
    private float agg_time_Decoration;
    private int agg_decorationColumns;
    private int agg_treesPlaced;
    private int agg_tallGrassPlaced;
    private int agg_flowersPlaced;
    private float agg_time_ActualChunkWork;
    private float agg_time_WallClock;
    private int agg_totalSteps;
    private float agg_stepTimeMax;
    private float agg_stepTimeMin = float.MaxValue;
    // Time spent INSIDE VRCAsyncGPUReadback.Request calls (climate + base column): if the
    // 20-45ms atomic gen steps live here, Request() is forcing a driver flush at submit.
    private int agg_readbackRequestCalls;
    private float agg_readbackRequestMs;
    private int agg_noiseGen1Cells;
    private int agg_noiseGen2Cells;
    private int agg_noiseGen3Cells;
    private int agg_noise6Cells;
    private int agg_noise7Cells;
    private int agg_noiseCombineCells;
    private int agg_terrainVoxelsVisited;
    private int agg_terrainAssignments;
    private int agg_terrainStoneAssignments;
    private int agg_terrainWaterAssignments;
    private int agg_terrainIceAssignments;
    private int agg_biomeTopAssignments;
    private int agg_biomeFillerAssignments;
    private int agg_biomeBedrockAssignments;
    private int agg_biomeWaterAssignments;
    private int agg_biomeGravelAssignments;
    private int agg_biomeSandAssignments;
    private int agg_biomeSandstoneAssignments;
    private int agg_gpuColumnsStarted;
    private int agg_gpuColumnsFinalized;
    private int agg_gpuFallbacks;
    private int agg_gpuFallbackPrepareNoise;
    private int agg_gpuFallbackFinalize;
    private int agg_gpuFallbackReadbackFailure;
    // GPU OFFLOAD #1: counts chunks whose CPU mirror copy was skipped (GPU-only chunks).
    private int agg_gpuChunkMirrorSkips;
    private int agg_gpuDiagnosticReadbackStalls;
    private int agg_gpuNoiseBlits;
    private float agg_gpuNoiseBlitTime;
    private int agg_gpuCombineBlits;
    private float agg_gpuCombineBlitTime;
    private int agg_gpuBaseFillBlits;
    private float agg_gpuBaseFillBlitTime;
    private int agg_gpuSurfaceInfoBlits;
    private float agg_gpuSurfaceInfoBlitTime;
    private int agg_gpuFinalizeBlits;
    private float agg_gpuFinalizeBlitTime;

    private int agg_gpuNoiseInputUploads;
    private float agg_gpuNoiseInputUploadTime;
    private int agg_gpuNoiseInputUploadBytes;
    private int agg_gpuSurfaceUploads;
    private float agg_gpuSurfaceUploadTime;
    private int agg_gpuSurfaceUploadBytes;
    private int agg_gpuFinalColumnUploads;
    private float agg_gpuFinalColumnUploadTime;
    private int agg_gpuFinalColumnUploadBytes;
    private int agg_gpuChunkSliceCopies;
    private float agg_gpuChunkSliceCopyTime;
    private int agg_gpuChunkSliceCopyBytes;
    private int agg_gpuWorkingSliceCopies;
    private float agg_gpuWorkingSliceCopyTime;
    private float agg_gpuHighestStoneScanTime;
    private int agg_gpuBaseReadbacksCompleted;
    private int agg_gpuBaseReadbackFailures;
    private float agg_gpuBaseReadbackLatencyTotal;
    private float agg_gpuBaseReadbackLatencyMin = float.MaxValue;
    private float agg_gpuBaseReadbackLatencyMax;
    private float agg_gpuBaseReadbackCallbackCopyTime;
    private int agg_gpuBaseReadbackBytes;
    private int agg_gpuDiagnosticReadbacksCompleted;
    private int agg_gpuDiagnosticReadbackFailures;
    private float agg_gpuDiagnosticReadbackLatencyTotal;
    private float agg_gpuDiagnosticReadbackLatencyMin = float.MaxValue;
    private float agg_gpuDiagnosticReadbackLatencyMax;
    private float agg_gpuDiagnosticReadbackCallbackCopyTime;
    private int agg_gpuDiagnosticReadbackBytes;
    private const int SLOWEST_TERRAIN_CHUNK_COUNT = 3;
    private float[] agg_slowestChunkTime = new float[SLOWEST_TERRAIN_CHUNK_COUNT];
    private int[] agg_slowestChunkX = new int[SLOWEST_TERRAIN_CHUNK_COUNT];
    private int[] agg_slowestChunkY = new int[SLOWEST_TERRAIN_CHUNK_COUNT];
    private int[] agg_slowestChunkZ = new int[SLOWEST_TERRAIN_CHUNK_COUNT];
    private float[] agg_slowestChunkPrep = new float[SLOWEST_TERRAIN_CHUNK_COUNT];
    private float[] agg_slowestChunkGenerate = new float[SLOWEST_TERRAIN_CHUNK_COUNT];
    private float[] agg_slowestChunkReplace = new float[SLOWEST_TERRAIN_CHUNK_COUNT];
#endif

    [SerializeField, FindObjectOfType(true)]
    private McWorld world;
    private WorldChunkManagerOld wcm;
    private WorldChunkManagerOld biomeQueryWcm;

    [SerializeField, FindObjectOfType(true)]
    private McBlockTypeManager blockTypeManager;

    // The random state for the generator, based on the seed
    private JavaRandom rand;
    // PARITY: 64-bit seed matches Java's Long. Truncating to int dropped the top 32 bits
    // and produced a different world even with a matching numeric seed.
    private long generatorSeed;

    private NoiseGeneratorOctaves3D noiseGen1;
    private NoiseGeneratorOctaves3D noiseGen2;
    private NoiseGeneratorOctaves3D noiseGen3;
    private NoiseGeneratorOctaves3D noiseGen4;
    private NoiseGeneratorOctaves3D noiseGen5;
    private NoiseGeneratorOctaves3D noiseGen6;
    private NoiseGeneratorOctaves3D noiseGen7;
    private NoiseGeneratorOctaves3D treeNoise;
    private NoiseGeneratorOctaves2D biomeTempNoiseGen;
    private NoiseGeneratorOctaves2D biomeRainNoiseGen;
    private NoiseGeneratorOctaves2D biomeModifierNoiseGen;

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

    // PERF: Pre-baked 1/2^n for n in [0..15]. Replaces 16 Math.Pow calls per chunk.
    // (Instance field — UdonSharp does not support static fields on UdonSharpBehaviours.)
    private double[] _octaveFrequencyLut;
    
    // RADICAL OPTIMIZATION: Simple caching for last biome query
    // Avoids re-computing biomes for same coordinates
    private int lastBiomeChunkX = int.MaxValue;
    private int lastBiomeChunkZ = int.MaxValue;
    private BetaBiomeEnum[] cachedBiomes;
    private double[] cachedTemperatures;
    private double[] cachedRainfall;
    private const int BIOME_COLUMN_CACHE_SIZE = 16;
    private int[] biomeCacheChunkX;
    private int[] biomeCacheChunkZ;
    private double[][] biomeCacheTemperatures;
    private double[][] biomeCacheRainfall;
    private int biomeCacheWriteIndex = 0;

    // GPU worldgen runtime state
    private bool gpuWorldgenReady = false;
    private bool gpuColumnReadbackPending = false;
    private bool gpuColumnReadbackReady = false;
    private bool gpuColumnReadbackFailed = false;
    // EVENT-DRIVEN GEN: set true by StepChunkGeneration when the current column can make no progress
    // this frame without an async GPU readback callback (base-column or climate readback in flight).
    // McWorld's gen loop reads this to stop spin-polling a 180-220ms readback ~32x/frame. Reset at
    // the top of every StepChunkGeneration call.
    public bool gpuStepBlockedOnReadback = false;
    private bool gpuReadbackContainsFinalColumn = false;
    private int gpuCachedColumnX = int.MaxValue;
    private int gpuCachedColumnZ = int.MaxValue;
    private int gpuPendingColumnX = int.MaxValue;
    private int gpuPendingColumnZ = int.MaxValue;
    private bool gpuCachedChunkSlicesReady = false;
    private bool gpuFinalColumnSliceCachePending = false;
    private bool gpuClimateReadbackPending = false;
    private bool gpuClimateReadbackReady = false;
    private bool gpuClimateReadbackFailed = false;
    private int gpuClimatePendingChunkX = int.MaxValue;
    private int gpuClimatePendingChunkZ = int.MaxValue;
    private byte[][] gpuCachedChunkSlices;
    private GpuWorldgenReadbackPhase gpuReadbackPhase = GpuWorldgenReadbackPhase.None;
    private int gpuWorldHeightBlocks = 0;
    private Texture2D gpuPermTexture;
    private Texture2D gpuPermTextureNoise1;
    private Texture2D gpuPermTextureNoise2;
    private Texture2D gpuPermTextureNoise3;
    private Texture2D gpuPermTextureNoise4;
    private Texture2D gpuPermTextureNoise5;
    private Texture2D gpuPermTextureNoise6;
    private Texture2D gpuPermTextureNoise7;
    private Texture2D gpuClimatePermTextureTemp;
    private Texture2D gpuClimatePermTextureRain;
    private Texture2D gpuClimatePermTextureModifier;
    private Texture2D gpuClimateOffsetTextureTemp;
    private Texture2D gpuClimateOffsetTextureRain;
    private Texture2D gpuClimateOffsetTextureModifier;
    private Texture2D gpuClimateBiomeLookupTexture;
    private Texture2D gpuCoordXZTexture; // X in RG, Z in BA
    private Texture2D gpuCoordYTexture;

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
    private Texture2D gpuSurfaceNoiseUpload;
    private Color[] gpuNoiseUpload3DPixels;
    private Color[] gpuNoiseUpload2DPixels;
    private Color[] gpuSurfaceNoiseUploadPixels;
    private RenderTexture gpuNoise1Texture;
    private RenderTexture gpuNoise2Texture;
    private RenderTexture gpuNoise3Texture;
    private RenderTexture gpuNoise6Texture;
    private RenderTexture gpuNoise7Texture;
    private RenderTexture gpuDensityTexture;
    private RenderTexture gpuColumnBaseTexture;
    private RenderTexture gpuColumnSurfaceInfoTexture;
    private RenderTexture gpuColumnFinalTexture;
    // The exact texture the last base-column readback sampled (= gpuColumnFinalTexture, or the
    // decoration output). Exposed so McWorld can GPU->GPU repack the column straight into the
    // atlas (no readback) for GPU-resident chunks. Valid for this column until the next column
    // overwrites it; a generator is single-column, so it's valid while this column's chunks finish.
    public RenderTexture gpuLastReadbackSource;

    // GPU-RESIDENT (#2 migration, step 2): when McWorld sets gpuSkipReadbackForColumn before
    // StartChunkGeneration, the generator finalizes the column on the GPU and then SKIPS the
    // GPU->CPU readback. lastChunkGpuResident signals the resident completion to McWorld;
    // gpuResidentColumnX/Z let sibling chunks repack their Y-slice straight from the still-valid
    // column final texture (gpuLastReadbackSource) without re-running the column gen.
    public bool gpuSkipReadbackForColumn = false;
    public bool lastChunkGpuResident = false;
    public int gpuResidentColumnX = int.MaxValue;
    public int gpuResidentColumnZ = int.MaxValue;
    // Diagnostic: counts _StartGpuColumnFinalize calls (= column gens incl. sibling re-gens).
    // Compare to the column count to detect redundant re-gen under the look-ahead.
    public int dbgColumnFinalizeCount = 0;
    // Diagnostic: StepChunkGeneration step count per GenerationState (index = (int)state).
    // The state with the most steps = where the gen spends its frames (the stall).
    public int[] dbgStateSteps = new int[20];
    // Per-state wall-clock accumulation (LOGGING): pinpoints WHICH state the 20-45ms atomic
    // step blocks live in — every individually-timed piece (blits, uploads, unpacks) reports
    // sub-ms, so the block is in an untimed section of specific states. Printed in the perf
    // summary; reset with the aggregate window.
    private float[] dbgStateTimeMs = new float[20];
    private float[] dbgStateTimeMaxMs = new float[20];
    private string[] dbgStateNames = new string[] {
        "Idle", "GetBiomes", "SandNoise", "GravelNoise", "StoneNoise", "GpuNoise",
        "GpuFinalize", "GpuReadback", "CopySlice", "AllocCache", "NoiseOctaves",
        "CombineNoise", "GenTerrain", "ReplaceBiomes", "Decorate", "Complete", "ResidentDone",
        "GpuDecorate" };

    // FINALIZE SLICING (Prepare_GpuDecorate): decoration work split across budgeted steps —
    // candidate collect (CPU JavaRandom streams), then ONE tree-anchor chunk render per step
    // (each anchor is a full mini column pipeline, ~20-50ms; rendering up to 4 in one call
    // was the 100-237ms GpuFinalize spike), then the decoration blits + readback request.
    private int gpuDecorStep = 0;
    private int gpuDecorTreeCount = 0;
    private int gpuDecorCandidateCount = 0;
    private int gpuDecorAnchorMask = 0;
    private RenderTexture gpuDecorCurrentTex;
    // TREE-ANCHOR CACHE: gpuTreeAnchorChunkTextures (minus index 4, the own-column snapshot)
    // act as a pool keyed by WORLD column coords — int.MaxValue = invalid entry. Anchor
    // content is deterministic (seed+coords), so entries never go stale; adjacent columns
    // reuse each other's anchors instead of re-running the ~20-50ms mini pipeline.
    private int[] gpuTreeAnchorPoolCx;
    private int[] gpuTreeAnchorPoolCz;
    private int[] gpuDecorSlotPool; // per decoration slot 0-8: bound pool index, -1 = unused
    private int gpuTreeAnchorPoolClock = 0;
#if LOGGING
    private int agg_anchorCacheHits;
    private int agg_anchorCacheMisses;
#endif
    private RenderTexture gpuSandNoiseTexture;
    private RenderTexture gpuGravelNoiseTexture;
    private RenderTexture gpuStoneNoiseTexture;
    private RenderTexture gpuClimateTexture;
    private Texture2D gpuColumnUploadTexture;
    private Texture2D gpuClearTexture;

    private Color[] gpuClimateReadbackPixels;
    private Color[] gpuClimatePermPixels;
    private Color[] gpuClimateOffsetPixels;
    private Color[] gpuClimateBiomeLookupPixels;
    private Color[] gpuPermutationPixels; // 256 * MAX_OCTAVES for batched upload
    private Color[] gpuCoordXZPixels; // X in RG, Z in BA
    private Color[] gpuCoordYPixels;
    private const int GPU_MAX_OCTAVES = 16;
    private const int GPU_DENSITY_GENERATORS = 5;
    private const int GPU_COORD_TEX_HEIGHT = GPU_MAX_OCTAVES * GPU_DENSITY_GENERATORS;
    private Color[] gpuSurfaceParamPixelsA;
    private Color[] gpuSurfaceParamPixelsB;
    private Color[] gpuBedrockMaskPixels;
    private Color[] gpuColumnUploadPixels;
    private Color32[] gpuColumnReadbackPixels;
    private byte[] gpuColumnReadbackBlocks;
    private Color32[] gpuSurfaceInfoReadbackPixels;
    private Color[] gpuNoiseDiagnosticReadbackPixels;
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
    private int gpuPropOctaveRowOffsetId;
    private int gpuPropCoordTexHeightId;
    private int gpuPropNoise1TexId;
    private int gpuPropNoise2TexId;
    private int gpuPropNoise3TexId;
    private int gpuPropNoise6TexId;
    private int gpuPropNoise7TexId;
    private int gpuPropTemperatureTexId;
    private int gpuPropRainfallTexId;
    private int gpuPropClimatePermTex0Id;
    private int gpuPropClimatePermTex1Id;
    private int gpuPropClimatePermTex2Id;
    private int gpuPropClimateOffsetTex0Id;
    private int gpuPropClimateOffsetTex1Id;
    private int gpuPropClimateOffsetTex2Id;
    private int gpuPropClimateBiomeLookupTexId;
    private int gpuPropClimateOctaveCount0Id;
    private int gpuPropClimateOctaveCount1Id;
    private int gpuPropClimateOctaveCount2Id;
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
    private int gpuPropSurfaceInfoTexId;
    private int gpuPropSurfaceParamsTexAId;
    private int gpuPropSurfaceParamsTexBId;
    private int gpuPropBedrockMaskTexId;
    private int gpuPropBedrockBlockId;
    private int gpuPropSandBlockId;
    private int gpuPropGravelBlockId;
    private int gpuPropSandstoneBlockId;
    private int gpuPropGravelNoiseTexId;
    private int gpuPropSandNoiseTexId;
    private int gpuPropStoneNoiseTexId;

    // GPU decoration property IDs
    private int gpuPropCandidateTexId;
    private int gpuPropCandidateCountId;
    private int gpuPropCandidateTexWidthId;
    private int gpuPropAirBlockIdDecor;
    private int gpuPropGrassBlockIdDecor;
    private int gpuPropDirtBlockIdDecor;
    private int gpuPropLeavesBlockIdDecor;

    // GPU decoration textures
    private const int GPU_DECORATION_TEX_WIDTH = 256;
    private const int GPU_DECORATION_TEX_HEIGHT = 32;
    private Texture2D gpuDecorationCandidateTexture;
    private Color[] gpuDecorationCandidatePixels;
    private RenderTexture gpuDecorationWorkTexture;
    private int gpuPropCandidateTexHeightId;

    // GPU tree decoration
    private const int GPU_TREE_TEX_WIDTH = 64;
    private Texture2D gpuTreeInfoTexture;
    private Color[] gpuTreeInfoPixels;
    private RenderTexture gpuTreeWorkTexture;
    private RenderTexture[] gpuTreeAnchorChunkTextures;
    private int[] gpuTreeAnchorChunkTexIds;
    private BetaBiomeEnum[] gpuTreeAnchorBiomeBuffer;
    private int gpuPropTreeInfoTexId;
    private int gpuPropTreeCountId;
    private int gpuPropLogBlockIdDecor;

    // GPU cave carving
    private RenderTexture gpuCaveWorkTexture;
    private int gpuPropCaveChunkXId, gpuPropCaveChunkZId;
    private int gpuPropCaveWorldSeedHiId, gpuPropCaveWorldSeedLoId;
    private int gpuPropCaveHashAHiId, gpuPropCaveHashALoId;
    private int gpuPropCaveHashBHiId, gpuPropCaveHashBLoId;
    private int gpuPropCaveGenerateId;
    // MapGenBase hashA/hashB precomputed once per world seed
    private bool gpuCaveHashesReady;
    private int gpuCaveHashAHi, gpuCaveHashALo;
    private int gpuCaveHashBHi, gpuCaveHashBLo;
#if LOGGING
    private int agg_gpuCaveBlits;
    private float agg_gpuCaveBlitTime;
#endif

    // Precomputed coordinate data for double-precision noise on GPU.
    // CPU computes floor() & 0xFF and fractional parts in double,
    // then uploads as float arrays so the GPU never does precision-sensitive coord math.
    // GPU noise helpers are used both for 5x5 density grids and 16x16 surface-noise passes.
    private const int GPU_MAX_XZSIZE = 16;
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
    private int gpuPropCoordXZTexId;
    private int gpuPropCoordYTexId;
    private double[] gpuDiagCpuNoise1;
    private double[] gpuDiagCpuNoise2;
    private double[] gpuDiagCpuNoise3;
    private double[] gpuDiagCpuNoise6;
    private double[] gpuDiagCpuNoise7;
    private double[] gpuDiagCpuDensity;
    private int gpuDiagChunkX = int.MaxValue;
    private int gpuDiagChunkZ = int.MaxValue;
    private int gpuDiagLastLoggedChunkX = int.MaxValue;
    private int gpuDiagLastLoggedChunkZ = int.MaxValue;
    private StringBuilder gpuNoiseDiagnosticLog;
    private int gpuDiagnosticReadbackStallFrames = 0;
    private const int GPU_DIAGNOSTIC_READBACK_STALL_LIMIT = 180;
    private float gpuReadbackRequestStartTimeMs = -1f;
    private int gpuReadbackRequestBytes = 0;

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
    // Direct column buffer for decoration — avoids world.GetBlock/SetBlock overhead
    private byte[] decoration_columnBuffer; // flat 16×worldHeight×16
    private bool[] decoration_columnDirtySlice; // which chunk slices were modified
    private int decoration_columnOriginX; // world-space block X of column origin
    private int decoration_columnOriginZ; // world-space block Z of column origin
    private int decoration_columnHeight; // total Y blocks in column
    private int decoration_sizeXZ; // chunk XZ size (16) 

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

    public void init(long seed)
    {
        DateTime startTime = DateTime.UtcNow;
        if (isInitialized) return;

        wcm = new WorldChunkManagerOld(seed);
        biomeQueryWcm = new WorldChunkManagerOld(seed);
        generatorSeed = seed;
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

        biomeCacheChunkX = new int[BIOME_COLUMN_CACHE_SIZE];
        biomeCacheChunkZ = new int[BIOME_COLUMN_CACHE_SIZE];
        biomeCacheTemperatures = new double[BIOME_COLUMN_CACHE_SIZE][];
        biomeCacheRainfall = new double[BIOME_COLUMN_CACHE_SIZE][];
        for (int i = 0; i < BIOME_COLUMN_CACHE_SIZE; i++)
        {
            biomeCacheChunkX[i] = int.MaxValue;
            biomeCacheChunkZ[i] = int.MaxValue;
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
        biomeTempNoiseGen = new NoiseGeneratorOctaves2D(new JavaRandom(seed * 9871L), 4);
        biomeRainNoiseGen = new NoiseGeneratorOctaves2D(new JavaRandom(seed * 39811L), 4);
        biomeModifierNoiseGen = new NoiseGeneratorOctaves2D(new JavaRandom(seed * 543321L), 2);

        caves = new WorldGenCavesOld();
        _InitializeGpuWorldgen();
        ResetAggregatePerformanceStats();
        
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

#if LOGGING
    public void AppendAggregatePerformanceStats(StringBuilder sb)
    {
        if (sb == null) return;

        if (enableDetailedTimings && agg_chunksCompleted > 0)
        {
            float avgPrep = agg_time_Preparation / agg_chunksCompleted;
            float avgGenerate = agg_time_GeneratingTerrain / agg_chunksCompleted;
            float avgReplace = agg_time_ReplacingBiomes / agg_chunksCompleted;
            float avgActual = agg_time_ActualChunkWork / agg_chunksCompleted;
            float avgWallClock = agg_time_WallClock / agg_chunksCompleted;
            float avgSteps = agg_totalSteps / (float)agg_chunksCompleted;
            float avgStep = agg_totalSteps > 0 ? agg_time_WallClock / agg_totalSteps : 0f;

            sb.AppendLine("Terrain Generation:");
            sb.AppendFormat("  Chunks: {0} ({1} cached columns), prep {2:F3}ms, terrain {3:F3}ms, replace biomes {4:F3}ms\n",
                agg_chunksCompleted, agg_cachedColumnsUsed, avgPrep, avgGenerate, avgReplace);
            sb.AppendFormat("  Chunk work: actual {0:F3}ms, wall-clock {1:F3}ms, steps/chunk {2:F1}, avg step {3:F3}ms, max step {4:F3}ms, min step {5:F3}ms\n",
                avgActual, avgWallClock, avgSteps, avgStep, agg_stepTimeMax, agg_stepTimeMin == float.MaxValue ? 0f : agg_stepTimeMin);
            // Per-state step attribution: which generator state the wall-clock actually lives in.
            if (dbgStateTimeMs != null)
            {
                sb.Append("  Gen state time:");
                for (int si = 0; si < dbgStateTimeMs.Length; si++)
                {
                    if (dbgStateTimeMs[si] < 0.5f) continue;
                    string nm = (dbgStateNames != null && si < dbgStateNames.Length) ? dbgStateNames[si] : si.ToString();
                    sb.Append(' ').Append(nm).Append(' ')
                      .Append(dbgStateTimeMs[si].ToString("F1")).Append("ms(max ")
                      .Append(dbgStateTimeMaxMs[si].ToString("F1")).Append(")");
                }
                sb.AppendLine();
            }
            sb.AppendFormat("  Prep breakdown: biomes {0:F3}ms, sand {1:F3}ms, gravel {2:F3}ms, stone {3:F3}ms, alloc noise cache {4:F3}ms\n",
                agg_time_Prep_GetBiomes / agg_chunksCompleted,
                agg_time_Prep_SandNoise / agg_chunksCompleted,
                agg_time_Prep_GravelNoise / agg_chunksCompleted,
                agg_time_Prep_StoneNoise / agg_chunksCompleted,
                agg_time_Prep_AllocNoiseCache / agg_chunksCompleted);
            sb.AppendFormat("  Noise breakdown: n1 {0:F3}ms, n2 {1:F3}ms, n3 {2:F3}ms, n6 {3:F3}ms, n7 {4:F3}ms, combine {5:F3}ms\n",
                agg_time_NoiseGen1 / agg_chunksCompleted,
                agg_time_NoiseGen2 / agg_chunksCompleted,
                agg_time_NoiseGen3 / agg_chunksCompleted,
                agg_time_Noise6 / agg_chunksCompleted,
                agg_time_Noise7 / agg_chunksCompleted,
                agg_time_NoiseCombine / agg_chunksCompleted);
            sb.AppendFormat("  Noise cells: n1 {0}, n2 {1}, n3 {2}, n6 {3}, n7 {4}, combine {5}\n",
                agg_noiseGen1Cells, agg_noiseGen2Cells, agg_noiseGen3Cells, agg_noise6Cells, agg_noise7Cells, agg_noiseCombineCells);
            sb.AppendFormat("  Terrain voxels: visited {0}, assignments {1}, stone {2}, water {3}, ice {4}\n",
                agg_terrainVoxelsVisited, agg_terrainAssignments, agg_terrainStoneAssignments, agg_terrainWaterAssignments, agg_terrainIceAssignments);
            sb.AppendFormat("  Surface assignments: top {0}, filler {1}, bedrock {2}, water {3}, gravel {4}, sand {5}, sandstone {6}\n",
                agg_biomeTopAssignments, agg_biomeFillerAssignments, agg_biomeBedrockAssignments,
                agg_biomeWaterAssignments, agg_biomeGravelAssignments, agg_biomeSandAssignments, agg_biomeSandstoneAssignments);
            float msPerThousandVisited = agg_terrainVoxelsVisited > 0 ? agg_time_ActualChunkWork * 1000f / agg_terrainVoxelsVisited : 0f;
            float msPerThousandAssignments = agg_terrainAssignments > 0 ? agg_time_ActualChunkWork * 1000f / agg_terrainAssignments : 0f;
            sb.AppendFormat("  Throughput: {0:F3}ms / 1k visited voxels, {1:F3}ms / 1k assignments\n",
                msPerThousandVisited, msPerThousandAssignments);
            if (agg_decorationColumns > 0)
            {
                sb.AppendFormat("  Decoration: {0} columns, {1:F3}ms total, trees {2}, grass {3}, flowers {4}\n",
                    agg_decorationColumns, agg_time_Decoration, agg_treesPlaced, agg_tallGrassPlaced, agg_flowersPlaced);
            }
            for (int i = 0; i < SLOWEST_TERRAIN_CHUNK_COUNT; i++)
            {
                if (agg_slowestChunkTime[i] <= 0f) break;
                sb.AppendFormat("  Slowest #{0}: ({1},{2},{3}) {4:F3}ms, prep {5:F3}ms, terrain {6:F3}ms, replace {7:F3}ms\n",
                    i + 1, agg_slowestChunkX[i], agg_slowestChunkY[i], agg_slowestChunkZ[i],
                    agg_slowestChunkTime[i], agg_slowestChunkPrep[i], agg_slowestChunkGenerate[i], agg_slowestChunkReplace[i]);
            }
        }

        bool hasGpuStats =
            agg_gpuColumnsStarted > 0 ||
            agg_gpuFallbacks > 0 ||
            agg_gpuNoiseBlits > 0 ||
            agg_gpuBaseReadbacksCompleted > 0 ||
            agg_gpuDiagnosticReadbacksCompleted > 0 ||
            agg_gpuBaseReadbackFailures > 0 ||
            agg_gpuDiagnosticReadbackFailures > 0;

        if (hasGpuStats)
        {
            sb.AppendLine("GPU Worldgen:");
            sb.AppendFormat("  Columns: started {0}, finalized {1}, fallbacks {2}\n",
                agg_gpuColumnsStarted, agg_gpuColumnsFinalized, agg_gpuFallbacks);
            if (agg_gpuFallbacks > 0 || agg_gpuDiagnosticReadbackStalls > 0)
            {
                sb.AppendFormat("  Fallback reasons: prepare noise {0}, finalize {1}, readback failure {2}, diagnostic stalls {3}\n",
                    agg_gpuFallbackPrepareNoise, agg_gpuFallbackFinalize, agg_gpuFallbackReadbackFailure, agg_gpuDiagnosticReadbackStalls);
            }
            sb.AppendFormat("  Blit submit: noise {0} ({1:F3}ms), combine {2} ({3:F3}ms), base fill {4} ({5:F3}ms), surface info {6} ({7:F3}ms), finalize {8} ({9:F3}ms)\n",
                agg_gpuNoiseBlits, agg_gpuNoiseBlitTime,
                agg_gpuCombineBlits, agg_gpuCombineBlitTime,
                agg_gpuBaseFillBlits, agg_gpuBaseFillBlitTime,
                agg_gpuSurfaceInfoBlits, agg_gpuSurfaceInfoBlitTime,
                agg_gpuFinalizeBlits, agg_gpuFinalizeBlitTime);
            sb.AppendFormat("  Cave blits: {0} ({1:F3}ms)\n",
                agg_gpuCaveBlits, agg_gpuCaveBlitTime);
            sb.AppendFormat("  CPU->GPU uploads: noise inputs {0} ({1:F3}ms, {2:F1}KB), surface {3} ({4:F3}ms, {5:F1}KB), final column {6} ({7:F3}ms, {8:F1}KB)\n",
                agg_gpuNoiseInputUploads, agg_gpuNoiseInputUploadTime, agg_gpuNoiseInputUploadBytes / 1024f,
                agg_gpuSurfaceUploads, agg_gpuSurfaceUploadTime, agg_gpuSurfaceUploadBytes / 1024f,
                agg_gpuFinalColumnUploads, agg_gpuFinalColumnUploadTime, agg_gpuFinalColumnUploadBytes / 1024f);
            sb.AppendFormat("  GPU->CPU slice cache unpack: {0} columns, {1:F3}ms, {2:F1}KB\n",
                agg_gpuChunkSliceCopies, agg_gpuChunkSliceCopyTime, agg_gpuChunkSliceCopyBytes / 1024f);
            if (agg_gpuWorkingSliceCopies > 0)
            {
                sb.AppendFormat("  Slice copy split: working {0} slices ({1:F3}ms), highest-stone scan {2:F3}ms\n",
                    agg_gpuWorkingSliceCopies, agg_gpuWorkingSliceCopyTime, agg_gpuHighestStoneScanTime);
            }

            if (agg_gpuBaseReadbacksCompleted > 0 || agg_gpuBaseReadbackFailures > 0)
            {
                float avgLatency = agg_gpuBaseReadbacksCompleted > 0 ? agg_gpuBaseReadbackLatencyTotal / agg_gpuBaseReadbacksCompleted : 0f;
                sb.AppendFormat("  Base readback: ok {0}, fail {1}, latency avg {2:F3}ms min {3:F3}ms max {4:F3}ms, callback copy {5:F3}ms, {6:F1}KB\n",
                    agg_gpuBaseReadbacksCompleted, agg_gpuBaseReadbackFailures, avgLatency,
                    agg_gpuBaseReadbackLatencyMin == float.MaxValue ? 0f : agg_gpuBaseReadbackLatencyMin,
                    agg_gpuBaseReadbackLatencyMax, agg_gpuBaseReadbackCallbackCopyTime,
                    agg_gpuBaseReadbackBytes / 1024f);
            }
            if (agg_readbackRequestCalls > 0)
            {
                sb.AppendFormat("  Readback Request() submit-block: {0} calls, {1:F1}ms total, {2:F2}ms avg\n",
                    agg_readbackRequestCalls, agg_readbackRequestMs, agg_readbackRequestMs / agg_readbackRequestCalls);
            }
            if (agg_anchorCacheHits > 0 || agg_anchorCacheMisses > 0)
            {
                sb.AppendFormat("  Tree-anchor cache: {0} hits, {1} misses\n",
                    agg_anchorCacheHits, agg_anchorCacheMisses);
            }

            if (agg_gpuDiagnosticReadbacksCompleted > 0 || agg_gpuDiagnosticReadbackFailures > 0)
            {
                float avgLatency = agg_gpuDiagnosticReadbacksCompleted > 0 ? agg_gpuDiagnosticReadbackLatencyTotal / agg_gpuDiagnosticReadbacksCompleted : 0f;
                sb.AppendFormat("  Diagnostic readback: ok {0}, fail {1}, latency avg {2:F3}ms min {3:F3}ms max {4:F3}ms, callback copy {5:F3}ms, {6:F1}KB\n",
                    agg_gpuDiagnosticReadbacksCompleted, agg_gpuDiagnosticReadbackFailures, avgLatency,
                    agg_gpuDiagnosticReadbackLatencyMin == float.MaxValue ? 0f : agg_gpuDiagnosticReadbackLatencyMin,
                    agg_gpuDiagnosticReadbackLatencyMax, agg_gpuDiagnosticReadbackCallbackCopyTime,
                    agg_gpuDiagnosticReadbackBytes / 1024f);
            }
        }
    }

    public void ResetAggregatePerformanceStats()
    {
        agg_chunksCompleted = 0;
        agg_cachedColumnsUsed = 0;
        agg_time_Preparation = 0f;
        agg_time_Prep_GetBiomes = 0f;
        agg_time_Prep_SandNoise = 0f;
        agg_time_Prep_GravelNoise = 0f;
        agg_time_Prep_StoneNoise = 0f;
        agg_time_Prep_AllocNoiseCache = 0f;
        agg_time_NoiseGen1 = 0f;
        agg_time_NoiseGen2 = 0f;
        agg_time_NoiseGen3 = 0f;
        agg_time_Noise6 = 0f;
        agg_time_Noise7 = 0f;
        agg_time_NoiseCombine = 0f;
        agg_time_GeneratingTerrain = 0f;
        agg_time_ReplacingBiomes = 0f;
        agg_time_ActualChunkWork = 0f;
        agg_time_WallClock = 0f;
        agg_totalSteps = 0;
        agg_stepTimeMax = 0f;
        agg_stepTimeMin = float.MaxValue;
        agg_readbackRequestCalls = 0;
        agg_readbackRequestMs = 0f;
        agg_anchorCacheHits = 0;
        agg_anchorCacheMisses = 0;
        if (dbgStateTimeMs != null)
        {
            for (int si = 0; si < dbgStateTimeMs.Length; si++)
            {
                dbgStateTimeMs[si] = 0f;
                dbgStateTimeMaxMs[si] = 0f;
            }
        }
        agg_noiseGen1Cells = 0;
        agg_noiseGen2Cells = 0;
        agg_noiseGen3Cells = 0;
        agg_noise6Cells = 0;
        agg_noise7Cells = 0;
        agg_noiseCombineCells = 0;
        agg_terrainVoxelsVisited = 0;
        agg_terrainAssignments = 0;
        agg_terrainStoneAssignments = 0;
        agg_terrainWaterAssignments = 0;
        agg_terrainIceAssignments = 0;
        agg_biomeTopAssignments = 0;
        agg_biomeFillerAssignments = 0;
        agg_biomeBedrockAssignments = 0;
        agg_biomeWaterAssignments = 0;
        agg_biomeGravelAssignments = 0;
        agg_biomeSandAssignments = 0;
        agg_biomeSandstoneAssignments = 0;
        agg_time_Decoration = 0f;
        agg_decorationColumns = 0;
        agg_treesPlaced = 0;
        agg_tallGrassPlaced = 0;
        agg_flowersPlaced = 0;
        agg_gpuColumnsStarted = 0;
        agg_gpuColumnsFinalized = 0;
        agg_gpuFallbacks = 0;
        agg_gpuFallbackPrepareNoise = 0;
        agg_gpuFallbackFinalize = 0;
        agg_gpuFallbackReadbackFailure = 0;
        agg_gpuDiagnosticReadbackStalls = 0;
        agg_gpuNoiseBlits = 0;
        agg_gpuNoiseBlitTime = 0f;
        agg_gpuCombineBlits = 0;
        agg_gpuCombineBlitTime = 0f;
        agg_gpuBaseFillBlits = 0;
        agg_gpuBaseFillBlitTime = 0f;
        agg_gpuSurfaceInfoBlits = 0;
        agg_gpuSurfaceInfoBlitTime = 0f;
        agg_gpuFinalizeBlits = 0;
        agg_gpuFinalizeBlitTime = 0f;
        agg_gpuCaveBlits = 0;
        agg_gpuCaveBlitTime = 0f;

        agg_gpuNoiseInputUploads = 0;
        agg_gpuNoiseInputUploadTime = 0f;
        agg_gpuNoiseInputUploadBytes = 0;
        agg_gpuSurfaceUploads = 0;
        agg_gpuSurfaceUploadTime = 0f;
        agg_gpuSurfaceUploadBytes = 0;
        agg_gpuFinalColumnUploads = 0;
        agg_gpuFinalColumnUploadTime = 0f;
        agg_gpuFinalColumnUploadBytes = 0;
        agg_gpuChunkSliceCopies = 0;
        agg_gpuChunkSliceCopyTime = 0f;
        agg_gpuChunkSliceCopyBytes = 0;
        agg_gpuWorkingSliceCopies = 0;
        agg_gpuWorkingSliceCopyTime = 0f;
        agg_gpuHighestStoneScanTime = 0f;
        agg_gpuBaseReadbacksCompleted = 0;
        agg_gpuBaseReadbackFailures = 0;
        agg_gpuBaseReadbackLatencyTotal = 0f;
        agg_gpuBaseReadbackLatencyMin = float.MaxValue;
        agg_gpuBaseReadbackLatencyMax = 0f;
        agg_gpuBaseReadbackCallbackCopyTime = 0f;
        agg_gpuBaseReadbackBytes = 0;
        agg_gpuDiagnosticReadbacksCompleted = 0;
        agg_gpuDiagnosticReadbackFailures = 0;
        agg_gpuDiagnosticReadbackLatencyTotal = 0f;
        agg_gpuDiagnosticReadbackLatencyMin = float.MaxValue;
        agg_gpuDiagnosticReadbackLatencyMax = 0f;
        agg_gpuDiagnosticReadbackCallbackCopyTime = 0f;
        agg_gpuDiagnosticReadbackBytes = 0;
        for (int i = 0; i < SLOWEST_TERRAIN_CHUNK_COUNT; i++)
        {
            agg_slowestChunkTime[i] = 0f;
            agg_slowestChunkX[i] = 0;
            agg_slowestChunkY[i] = 0;
            agg_slowestChunkZ[i] = 0;
            agg_slowestChunkPrep[i] = 0f;
            agg_slowestChunkGenerate[i] = 0f;
            agg_slowestChunkReplace[i] = 0f;
        }
    }



    private void _RecordGpuNoiseInputUpload(float timeMs, int bytes)
    {
        agg_gpuNoiseInputUploads++;
        agg_gpuNoiseInputUploadTime += timeMs;
        agg_gpuNoiseInputUploadBytes += bytes;
    }

    private void _RecordGpuSurfaceUpload(float timeMs, int bytes)
    {
        agg_gpuSurfaceUploads++;
        agg_gpuSurfaceUploadTime += timeMs;
        agg_gpuSurfaceUploadBytes += bytes;
    }

    private void _RecordGpuFinalColumnUpload(float timeMs, int bytes)
    {
        agg_gpuFinalColumnUploads++;
        agg_gpuFinalColumnUploadTime += timeMs;
        agg_gpuFinalColumnUploadBytes += bytes;
    }

    private void _RecordGpuReadbackCompletion(bool diagnostic, bool success, float latencyMs, float callbackCopyMs, int bytes)
    {
        if (diagnostic)
        {
            if (success)
            {
                agg_gpuDiagnosticReadbacksCompleted++;
                agg_gpuDiagnosticReadbackLatencyTotal += latencyMs;
                if (latencyMs < agg_gpuDiagnosticReadbackLatencyMin) agg_gpuDiagnosticReadbackLatencyMin = latencyMs;
                if (latencyMs > agg_gpuDiagnosticReadbackLatencyMax) agg_gpuDiagnosticReadbackLatencyMax = latencyMs;
                agg_gpuDiagnosticReadbackCallbackCopyTime += callbackCopyMs;
                agg_gpuDiagnosticReadbackBytes += bytes;
            }
            else
            {
                agg_gpuDiagnosticReadbackFailures++;
            }
            return;
        }

        if (success)
        {
            agg_gpuBaseReadbacksCompleted++;
            agg_gpuBaseReadbackLatencyTotal += latencyMs;
            if (latencyMs < agg_gpuBaseReadbackLatencyMin) agg_gpuBaseReadbackLatencyMin = latencyMs;
            if (latencyMs > agg_gpuBaseReadbackLatencyMax) agg_gpuBaseReadbackLatencyMax = latencyMs;
            agg_gpuBaseReadbackCallbackCopyTime += callbackCopyMs;
            agg_gpuBaseReadbackBytes += bytes;
        }
        else
        {
            agg_gpuBaseReadbackFailures++;
        }
    }

    private void _AccumulateCompletedChunkProfile(
        int chunkX,
        int chunkY,
        int chunkZ,
        float preparation,
        float prepGetBiomes,
        float prepSandNoise,
        float prepGravelNoise,
        float prepStoneNoise,
        float prepAllocNoiseCache,
        float noiseGen1Time,
        float noiseGen2Time,
        float noiseGen3Time,
        float noise6Time,
        float noise7Time,
        float noiseCombineTime,
        int noiseGen1Count,
        int noiseGen2Count,
        int noiseGen3Count,
        int noise6Count,
        int noise7Count,
        int noiseCombineCount,
        float actualChunkTime,
        float totalTime)
    {
        agg_chunksCompleted++;
        if (timingsCached) agg_cachedColumnsUsed++;
        agg_time_Preparation += preparation;
        agg_time_Prep_GetBiomes += prepGetBiomes;
        agg_time_Prep_SandNoise += prepSandNoise;
        agg_time_Prep_GravelNoise += prepGravelNoise;
        agg_time_Prep_StoneNoise += prepStoneNoise;
        agg_time_Prep_AllocNoiseCache += prepAllocNoiseCache;
        agg_time_NoiseGen1 += noiseGen1Time;
        agg_time_NoiseGen2 += noiseGen2Time;
        agg_time_NoiseGen3 += noiseGen3Time;
        agg_time_Noise6 += noise6Time;
        agg_time_Noise7 += noise7Time;
        agg_time_NoiseCombine += noiseCombineTime;
        agg_time_GeneratingTerrain += time_GeneratingTerrain;
        agg_time_ReplacingBiomes += time_ReplacingBiomes;
        agg_time_ActualChunkWork += actualChunkTime;
        agg_time_WallClock += totalTime;
        agg_totalSteps += totalSteps;
        if (maxStepTime > agg_stepTimeMax) agg_stepTimeMax = maxStepTime;
        if (minStepTime < agg_stepTimeMin) agg_stepTimeMin = minStepTime;
        agg_noiseGen1Cells += noiseGen1Count;
        agg_noiseGen2Cells += noiseGen2Count;
        agg_noiseGen3Cells += noiseGen3Count;
        agg_noise6Cells += noise6Count;
        agg_noise7Cells += noise7Count;
        agg_noiseCombineCells += noiseCombineCount;
        agg_terrainVoxelsVisited += terrainVoxelsVisited;
        agg_terrainAssignments += terrainAssignments;
        agg_terrainStoneAssignments += terrainStoneAssignments;
        agg_terrainWaterAssignments += terrainWaterAssignments;
        agg_terrainIceAssignments += terrainIceAssignments;
        agg_biomeTopAssignments += biomeTopAssignments;
        agg_biomeFillerAssignments += biomeFillerAssignments;
        agg_biomeBedrockAssignments += biomeBedrockAssignments;
        agg_biomeWaterAssignments += biomeWaterAssignments;
        agg_biomeGravelAssignments += biomeGravelAssignments;
        agg_biomeSandAssignments += biomeSandAssignments;
        agg_biomeSandstoneAssignments += biomeSandstoneAssignments;
        _RecordSlowTerrainChunk(chunkX, chunkY, chunkZ, totalTime, preparation, time_GeneratingTerrain, time_ReplacingBiomes);
    }

    private void _RecordSlowTerrainChunk(int chunkX, int chunkY, int chunkZ, float totalTime, float preparation, float generateTime, float replaceTime)
    {
        int insertIndex = -1;
        for (int i = 0; i < SLOWEST_TERRAIN_CHUNK_COUNT; i++)
        {
            if (totalTime > agg_slowestChunkTime[i])
            {
                insertIndex = i;
                break;
            }
        }

        if (insertIndex == -1) return;

        for (int i = SLOWEST_TERRAIN_CHUNK_COUNT - 1; i > insertIndex; i--)
        {
            agg_slowestChunkTime[i] = agg_slowestChunkTime[i - 1];
            agg_slowestChunkX[i] = agg_slowestChunkX[i - 1];
            agg_slowestChunkY[i] = agg_slowestChunkY[i - 1];
            agg_slowestChunkZ[i] = agg_slowestChunkZ[i - 1];
            agg_slowestChunkPrep[i] = agg_slowestChunkPrep[i - 1];
            agg_slowestChunkGenerate[i] = agg_slowestChunkGenerate[i - 1];
            agg_slowestChunkReplace[i] = agg_slowestChunkReplace[i - 1];
        }

        agg_slowestChunkTime[insertIndex] = totalTime;
        agg_slowestChunkX[insertIndex] = chunkX;
        agg_slowestChunkY[insertIndex] = chunkY;
        agg_slowestChunkZ[insertIndex] = chunkZ;
        agg_slowestChunkPrep[insertIndex] = preparation;
        agg_slowestChunkGenerate[insertIndex] = generateTime;
        agg_slowestChunkReplace[insertIndex] = replaceTime;
    }
#endif

    private void _CacheBiomeColumnData(int chunkX, int chunkZ, double[] temperatures, double[] rainfall)
    {
        // Disabled: Udon runtime is unstable with the jagged biome ring-cache access pattern.
        // Keep the existing single-column cache (`lastBiomeChunk*`) and use `biomeQueryWcm`
        // for non-current chunk lookups in GetBiomeDataForChunk().
    }

    private bool _RestoreCachedBiomeStateForCurrentColumn()
    {
        if (currentChunkX != lastBiomeChunkX || currentChunkZ != lastBiomeChunkZ) return false;
        if (cachedBiomes == null || cachedTemperatures == null || cachedRainfall == null) return false;

        currentChunkBiomes = cachedBiomes;
        if (wcm != null)
        {
            wcm.temperatures = cachedTemperatures;
            wcm.rainfall = cachedRainfall;
        }
        return true;
    }

    private bool _HasBiomeInputsReady()
    {
        return currentChunkBiomes != null &&
               wcm != null &&
               wcm.temperatures != null &&
               wcm.rainfall != null;
    }

    private bool _HasCpuNoiseInputsReady()
    {
        return noiseCache != null &&
               noise1 != null &&
               noise2 != null &&
               noise3 != null &&
               noise6 != null &&
               noise7 != null;
    }

    private bool _HasSurfaceNoiseInputsReady()
    {
        return sandNoise != null &&
               gravelNoise != null &&
               stoneNoise != null;
    }

    private Texture2D _CreateGpuFloatTexture(int width, int height, TextureWrapMode wrapMode)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = wrapMode;
        return texture;
    }

    private Texture2D _CreateGpuPreciseFloatTexture(int width, int height, TextureWrapMode wrapMode)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
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

    private RenderTexture _CreateGpuBlockIdRenderTexture(int width, int height, string textureName)
    {
        RenderTextureFormat format = useSingleChannelGpuColumnReadback ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32;
        RenderTexture rt = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear);
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
        gpuCachedChunkSlicesReady = false;
        gpuFinalColumnSliceCachePending = false;
        gpuClimateReadbackPending = false;
        gpuClimateReadbackReady = false;
        gpuClimateReadbackFailed = false;
        gpuClimatePendingChunkX = int.MaxValue;
        gpuClimatePendingChunkZ = int.MaxValue;

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
        // Width is 512 (not 256) because the Perlin double-table uses indices 0-511.
        gpuPermTexture = _CreateGpuColorTexture(512, GPU_MAX_OCTAVES, TextureWrapMode.Clamp);
        gpuPermTextureNoise1 = _CreateGpuColorTexture(512, GPU_MAX_OCTAVES, TextureWrapMode.Clamp);
        gpuPermTextureNoise2 = _CreateGpuColorTexture(512, GPU_MAX_OCTAVES, TextureWrapMode.Clamp);
        gpuPermTextureNoise3 = _CreateGpuColorTexture(512, GPU_MAX_OCTAVES, TextureWrapMode.Clamp);
        gpuPermTextureNoise4 = _CreateGpuColorTexture(512, GPU_MAX_OCTAVES, TextureWrapMode.Clamp);
        gpuPermTextureNoise5 = _CreateGpuColorTexture(512, GPU_MAX_OCTAVES, TextureWrapMode.Clamp);
        gpuPermTextureNoise6 = _CreateGpuColorTexture(512, GPU_MAX_OCTAVES, TextureWrapMode.Clamp);
        gpuPermTextureNoise7 = _CreateGpuColorTexture(512, GPU_MAX_OCTAVES, TextureWrapMode.Clamp);
        gpuClimatePermTextureTemp = _CreateGpuColorTexture(512, 4, TextureWrapMode.Clamp);
        gpuClimatePermTextureRain = _CreateGpuColorTexture(512, 4, TextureWrapMode.Clamp);
        gpuClimatePermTextureModifier = _CreateGpuColorTexture(512, 4, TextureWrapMode.Clamp);
        gpuClimateOffsetTextureTemp = _CreateGpuPreciseFloatTexture(4, 1, TextureWrapMode.Clamp);
        gpuClimateOffsetTextureRain = _CreateGpuPreciseFloatTexture(4, 1, TextureWrapMode.Clamp);
        gpuClimateOffsetTextureModifier = _CreateGpuPreciseFloatTexture(4, 1, TextureWrapMode.Clamp);
        gpuClimateBiomeLookupTexture = _CreateGpuColorTexture(64, 64, TextureWrapMode.Clamp);
        // These tables carry the lattice indices and fractional coordinates that drive
        // the entire Perlin lookup. Keep them at full float precision to minimize
        // CPU-vs-GPU drift before considering software-emulated doubles.
        gpuCoordXZTexture = _CreateGpuPreciseFloatTexture(GPU_MAX_XZSIZE, GPU_COORD_TEX_HEIGHT, TextureWrapMode.Clamp);
        gpuCoordYTexture = _CreateGpuPreciseFloatTexture(GPU_MAX_YSIZE, GPU_COORD_TEX_HEIGHT, TextureWrapMode.Clamp);

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
        int surfNoiseSize = world.chunkSizeXZ;
        gpuSurfaceNoiseUpload = _CreateGpuPreciseFloatTexture(surfNoiseSize, surfNoiseSize, TextureWrapMode.Clamp);
        gpuSurfaceNoiseUploadPixels = new Color[surfNoiseSize * surfNoiseSize];
        gpuNoise1Texture = _CreateGpuFloatRenderTexture(densityPackedWidth, densityYSize, "GPU_Noise1");
        gpuNoise2Texture = _CreateGpuFloatRenderTexture(densityPackedWidth, densityYSize, "GPU_Noise2");
        gpuNoise3Texture = _CreateGpuFloatRenderTexture(densityPackedWidth, densityYSize, "GPU_Noise3");
        gpuNoise6Texture = _CreateGpuFloatRenderTexture(densityXSize, densityZSize, "GPU_Noise6");
        gpuNoise7Texture = _CreateGpuFloatRenderTexture(densityXSize, densityZSize, "GPU_Noise7");
        gpuDensityTexture = _CreateGpuFloatRenderTexture(densityPackedWidth, densityYSize, "GPU_Density");
        gpuColumnBaseTexture = _CreateGpuBlockIdRenderTexture(world.chunkSizeXZ, gpuWorldHeightBlocks * world.chunkSizeXZ, "GPU_ColumnBase");
        gpuColumnSurfaceInfoTexture = _CreateGpuColorRenderTexture(world.chunkSizeXZ, world.chunkSizeXZ, "GPU_ColumnSurfaceInfo");
        gpuColumnFinalTexture = _CreateGpuBlockIdRenderTexture(world.chunkSizeXZ, gpuWorldHeightBlocks * world.chunkSizeXZ, "GPU_ColumnFinal");
        gpuSandNoiseTexture = _CreateGpuFloatRenderTexture(world.chunkSizeXZ, world.chunkSizeXZ, "GPU_SandNoise");
        gpuGravelNoiseTexture = _CreateGpuFloatRenderTexture(world.chunkSizeXZ, world.chunkSizeXZ, "GPU_GravelNoise");
        gpuStoneNoiseTexture = _CreateGpuFloatRenderTexture(world.chunkSizeXZ, world.chunkSizeXZ, "GPU_StoneNoise");
        gpuClimateTexture = _CreateGpuFloatRenderTexture(world.chunkSizeXZ, world.chunkSizeXZ, "GPU_Climate");
        gpuColumnUploadTexture = _CreateGpuColorTexture(world.chunkSizeXZ, gpuWorldHeightBlocks * world.chunkSizeXZ, TextureWrapMode.Clamp);


        gpuClimateReadbackPixels = new Color[world.chunkSizeXZ * world.chunkSizeXZ];
        gpuClimatePermPixels = new Color[512 * 4];
        gpuClimateOffsetPixels = new Color[4];
        gpuClimateBiomeLookupPixels = new Color[64 * 64];
        gpuPermutationPixels = new Color[512 * GPU_MAX_OCTAVES]; // 512 entries per octave (Perlin double-table)
        gpuCoordXZPixels = new Color[GPU_MAX_XZSIZE * GPU_COORD_TEX_HEIGHT];
        gpuCoordYPixels = new Color[GPU_MAX_YSIZE * GPU_COORD_TEX_HEIGHT];
        // Pre-fill with (0,0,0,1) so per-upload zero-fill is unnecessary
        for (int i = 0; i < gpuCoordXZPixels.Length; i++)
            gpuCoordXZPixels[i] = new Color(0f, 0f, 0f, 1f);
        for (int i = 0; i < gpuCoordYPixels.Length; i++)
            gpuCoordYPixels[i] = new Color(0f, 0f, 0f, 1f);
        gpuSurfaceParamPixelsA = new Color[world.chunkSizeXZ * world.chunkSizeXZ];
        gpuSurfaceParamPixelsB = new Color[world.chunkSizeXZ * world.chunkSizeXZ];
        gpuBedrockMaskPixels = new Color[world.chunkSizeXZ * 5 * world.chunkSizeXZ];
        gpuColumnUploadPixels = new Color[world.chunkSizeXZ * gpuWorldHeightBlocks * world.chunkSizeXZ];
        int gpuColumnReadbackLength = world.chunkSizeXZ * gpuWorldHeightBlocks * world.chunkSizeXZ;
        gpuColumnReadbackPixels = useSingleChannelGpuColumnReadback ? null : new Color32[gpuColumnReadbackLength];
        gpuColumnReadbackBlocks = new byte[gpuColumnReadbackLength];
        gpuSurfaceInfoReadbackPixels = new Color32[world.chunkSizeXZ * world.chunkSizeXZ];
        gpuNoiseDiagnosticReadbackPixels = new Color[densityPackedWidth * densityYSize];
        gpuNoiseDiagnosticLog = new StringBuilder(4096);
        gpuCachedChunkSlices = new byte[world.worldDimensionY][];
        int chunkSize = world.chunkSizeXZ * world.chunkSizeY * world.chunkSizeXZ;
        for (int chunkY = 0; chunkY < world.worldDimensionY; chunkY++)
        {
            gpuCachedChunkSlices[chunkY] = new byte[chunkSize];
        }

        // GPU decoration texture (candidate positions uploaded from CPU)
        gpuDecorationCandidateTexture = new Texture2D(GPU_DECORATION_TEX_WIDTH, GPU_DECORATION_TEX_HEIGHT, TextureFormat.RGBA32, false, true);
        gpuDecorationCandidateTexture.filterMode = FilterMode.Point;
        gpuDecorationCandidateTexture.wrapMode = TextureWrapMode.Clamp;
        gpuDecorationCandidatePixels = new Color[GPU_DECORATION_TEX_WIDTH * GPU_DECORATION_TEX_HEIGHT];
        gpuDecorationWorkTexture = _CreateGpuBlockIdRenderTexture(world.chunkSizeXZ, gpuWorldHeightBlocks * world.chunkSizeXZ, "GPU_DecorationWork");

        // GPU tree decoration texture (one pixel per tree candidate)
        gpuTreeInfoTexture = new Texture2D(GPU_TREE_TEX_WIDTH, 1, TextureFormat.RGBA32, false, true);
        gpuTreeInfoTexture.filterMode = FilterMode.Point;
        gpuTreeInfoTexture.wrapMode = TextureWrapMode.Clamp;
        gpuTreeInfoPixels = new Color[GPU_TREE_TEX_WIDTH];
        gpuTreeWorkTexture = _CreateGpuBlockIdRenderTexture(world.chunkSizeXZ, gpuWorldHeightBlocks * world.chunkSizeXZ, "GPU_TreeWork");
        gpuTreeAnchorChunkTextures = new RenderTexture[9];
        for (int treeChunkSlot = 0; treeChunkSlot < gpuTreeAnchorChunkTextures.Length; treeChunkSlot++)
        {
            gpuTreeAnchorChunkTextures[treeChunkSlot] = _CreateGpuBlockIdRenderTexture(
                world.chunkSizeXZ,
                gpuWorldHeightBlocks * world.chunkSizeXZ,
                "GPU_TreeAnchorChunk_" + treeChunkSlot
            );
        }
        gpuTreeAnchorBiomeBuffer = new BetaBiomeEnum[world.chunkSizeXZ * world.chunkSizeXZ];

        // GPU cave carving RT
        gpuCaveWorkTexture = _CreateGpuBlockIdRenderTexture(world.chunkSizeXZ, gpuWorldHeightBlocks * world.chunkSizeXZ, "GPU_CaveWork");
        // Precompute MapGenBase hashA/hashB from world seed (same for all columns)
        gpuCaveHashesReady = false;

        _UploadGpuPermutationTable(gpuPermTextureNoise1, noiseGen1, 16);
        _UploadGpuPermutationTable(gpuPermTextureNoise2, noiseGen2, 16);
        _UploadGpuPermutationTable(gpuPermTextureNoise3, noiseGen3, 8);
        _UploadGpuPermutationTable(gpuPermTextureNoise4, noiseGen4, 16);
        _UploadGpuPermutationTable(gpuPermTextureNoise5, noiseGen5, 16);
        _UploadGpuPermutationTable(gpuPermTextureNoise6, noiseGen6, 10);
        _UploadGpuPermutationTable(gpuPermTextureNoise7, noiseGen7, 16);
        _UploadGpuClimatePermutationTable(gpuClimatePermTextureTemp, biomeTempNoiseGen);
        _UploadGpuClimatePermutationTable(gpuClimatePermTextureRain, biomeRainNoiseGen);
        _UploadGpuClimatePermutationTable(gpuClimatePermTextureModifier, biomeModifierNoiseGen);
        _UploadGpuClimateOffsetTexture(gpuClimateOffsetTextureTemp, biomeTempNoiseGen);
        _UploadGpuClimateOffsetTexture(gpuClimateOffsetTextureRain, biomeRainNoiseGen);
        _UploadGpuClimateOffsetTexture(gpuClimateOffsetTextureModifier, biomeModifierNoiseGen);
        _UploadGpuClimateBiomeLookupTexture();

        gpuPropPermTexId = VRCShader.PropertyToID("_PermTex");
        gpuPropAccumulationTexId = VRCShader.PropertyToID("_AccumulationTex");
        gpuPropOctaveId = VRCShader.PropertyToID("_OctaveCount");
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
        gpuPropOctaveRowOffsetId = VRCShader.PropertyToID("_OctaveRowOffset");
        gpuPropCoordTexHeightId = VRCShader.PropertyToID("_CoordTexHeight");
        gpuPropNoise1TexId = VRCShader.PropertyToID("_Noise1Tex");
        gpuPropNoise2TexId = VRCShader.PropertyToID("_Noise2Tex");
        gpuPropNoise3TexId = VRCShader.PropertyToID("_Noise3Tex");
        gpuPropNoise6TexId = VRCShader.PropertyToID("_Noise6Tex");
        gpuPropNoise7TexId = VRCShader.PropertyToID("_Noise7Tex");
        gpuPropTemperatureTexId = VRCShader.PropertyToID("_TemperatureTex");
        gpuPropRainfallTexId = VRCShader.PropertyToID("_RainfallTex");
        gpuPropClimatePermTex0Id = VRCShader.PropertyToID("_ClimatePermTex0");
        gpuPropClimatePermTex1Id = VRCShader.PropertyToID("_ClimatePermTex1");
        gpuPropClimatePermTex2Id = VRCShader.PropertyToID("_ClimatePermTex2");
        gpuPropClimateOffsetTex0Id = VRCShader.PropertyToID("_ClimateOffsetTex0");
        gpuPropClimateOffsetTex1Id = VRCShader.PropertyToID("_ClimateOffsetTex1");
        gpuPropClimateOffsetTex2Id = VRCShader.PropertyToID("_ClimateOffsetTex2");
        gpuPropClimateBiomeLookupTexId = VRCShader.PropertyToID("_ClimateBiomeLookupTex");
        gpuPropClimateOctaveCount0Id = VRCShader.PropertyToID("_ClimateOctaveCount0");
        gpuPropClimateOctaveCount1Id = VRCShader.PropertyToID("_ClimateOctaveCount1");
        gpuPropClimateOctaveCount2Id = VRCShader.PropertyToID("_ClimateOctaveCount2");
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
        gpuPropSurfaceInfoTexId = VRCShader.PropertyToID("_SurfaceInfoTex");
        gpuPropSurfaceParamsTexAId = VRCShader.PropertyToID("_SurfaceParamsTexA");
        gpuPropSurfaceParamsTexBId = VRCShader.PropertyToID("_SurfaceParamsTexB");
        gpuPropBedrockMaskTexId = VRCShader.PropertyToID("_BedrockMaskTex");
        gpuPropBedrockBlockId = VRCShader.PropertyToID("_BedrockBlockId");
        gpuPropSandBlockId = VRCShader.PropertyToID("_SandBlockId");
        gpuPropGravelBlockId = VRCShader.PropertyToID("_GravelBlockId");
        gpuPropSandstoneBlockId = VRCShader.PropertyToID("_SandstoneBlockId");
        gpuPropSandNoiseTexId = VRCShader.PropertyToID("_SandNoiseTex");
        gpuPropGravelNoiseTexId = VRCShader.PropertyToID("_GravelNoiseTex");
        gpuPropStoneNoiseTexId = VRCShader.PropertyToID("_StoneNoiseTex");

        // GPU decoration property IDs
        gpuPropCandidateTexId = VRCShader.PropertyToID("_CandidateTex");
        gpuPropCandidateCountId = VRCShader.PropertyToID("_CandidateCount");
        gpuPropCandidateTexWidthId = VRCShader.PropertyToID("_CandidateTexWidth");
        gpuPropCandidateTexHeightId = VRCShader.PropertyToID("_CandidateTexHeight");
        gpuPropAirBlockIdDecor = VRCShader.PropertyToID("_AirBlockId");
        gpuPropGrassBlockIdDecor = VRCShader.PropertyToID("_GrassBlockId");
        gpuPropDirtBlockIdDecor = VRCShader.PropertyToID("_DirtBlockId");
        gpuPropLeavesBlockIdDecor = VRCShader.PropertyToID("_LeavesBlockId");
        gpuPropTreeInfoTexId = VRCShader.PropertyToID("_TreeInfoTex");
        gpuPropTreeCountId = VRCShader.PropertyToID("_TreeCount");
        gpuPropLogBlockIdDecor = VRCShader.PropertyToID("_LogBlockId");
        gpuTreeAnchorChunkTexIds = new int[9];
        gpuTreeAnchorChunkTexIds[0] = VRCShader.PropertyToID("_TreeChunkTex0");
        gpuTreeAnchorChunkTexIds[1] = VRCShader.PropertyToID("_TreeChunkTex1");
        gpuTreeAnchorChunkTexIds[2] = VRCShader.PropertyToID("_TreeChunkTex2");
        gpuTreeAnchorChunkTexIds[3] = VRCShader.PropertyToID("_TreeChunkTex3");
        gpuTreeAnchorChunkTexIds[4] = VRCShader.PropertyToID("_TreeChunkTex4");
        gpuTreeAnchorChunkTexIds[5] = VRCShader.PropertyToID("_TreeChunkTex5");
        gpuTreeAnchorChunkTexIds[6] = VRCShader.PropertyToID("_TreeChunkTex6");
        gpuTreeAnchorChunkTexIds[7] = VRCShader.PropertyToID("_TreeChunkTex7");
        gpuTreeAnchorChunkTexIds[8] = VRCShader.PropertyToID("_TreeChunkTex8");

        // GPU cave property IDs
        gpuPropCaveChunkXId = VRCShader.PropertyToID("_ChunkX");
        gpuPropCaveChunkZId = VRCShader.PropertyToID("_ChunkZ");
        gpuPropCaveWorldSeedHiId = VRCShader.PropertyToID("_WorldSeedHi");
        gpuPropCaveWorldSeedLoId = VRCShader.PropertyToID("_WorldSeedLo");
        gpuPropCaveHashAHiId = VRCShader.PropertyToID("_HashAHi");
        gpuPropCaveHashALoId = VRCShader.PropertyToID("_HashALo");
        gpuPropCaveHashBHiId = VRCShader.PropertyToID("_HashBHi");
        gpuPropCaveHashBLoId = VRCShader.PropertyToID("_HashBLo");
        gpuPropCaveGenerateId = VRCShader.PropertyToID("_GenerateCaves");

        // Precomputed coordinate property IDs
        gpuPropPermIdxXId = VRCShader.PropertyToID("_PermIdxX");
        gpuPropFracXId = VRCShader.PropertyToID("_FracX");
        gpuPropPermIdxYId = VRCShader.PropertyToID("_PermIdxY");
        gpuPropFracYId = VRCShader.PropertyToID("_FracY");
        gpuPropGradFracYId = VRCShader.PropertyToID("_GradFracY");
        gpuPropPermIdxZId = VRCShader.PropertyToID("_PermIdxZ");
        gpuPropFracZId = VRCShader.PropertyToID("_FracZ");
        gpuPropCoordXZTexId = VRCShader.PropertyToID("_CoordXZTex");
        gpuPropCoordYTexId = VRCShader.PropertyToID("_CoordYTex");

        // Allocate precompute arrays
        gpuPrePermX = new float[GPU_MAX_XZSIZE];
        gpuPreFracX = new float[GPU_MAX_XZSIZE];
        gpuPrePermY = new float[GPU_MAX_YSIZE];
        gpuPreFracY = new float[GPU_MAX_YSIZE];
        gpuPreGradFracY = new float[GPU_MAX_YSIZE];
        gpuPrePermZ = new float[GPU_MAX_XZSIZE];
        gpuPreFracZ = new float[GPU_MAX_XZSIZE];

        gpuWorldgenReady = true;
#if LOGGING
        Debug.Log("[McTerrainGen][GPU] GPU worldgen resources initialized (gpuWorldgenReady=true) -> GPU terrain generation is available.");
#endif
    }

    private void _UploadGpuPermutationTable(Texture2D targetTexture, NoiseGeneratorOctaves3D source, int octaveCount)
    {
        if (targetTexture == null || source == null || gpuPermutationPixels == null) return;

        for (int i = 0; i < gpuPermutationPixels.Length; i++)
        {
            gpuPermutationPixels[i] = new Color(0f, 0f, 0f, 1f);
        }

        for (int octave = 0; octave < octaveCount; octave++)
        {
            NoiseGenerator3dPerlin generator = source.GetGenerator(octave);
            if (generator == null || generator.permutations == null) continue;

            int rowOffset = octave * 512;
            for (int i = 0; i < 512; i++)
            {
                gpuPermutationPixels[rowOffset + i] = new Color(generator.permutations[i] / 255.0f, 0f, 0f, 1f);
            }
        }

        targetTexture.SetPixels(gpuPermutationPixels);
        targetTexture.Apply(false, false);
    }

    private void _UploadGpuClimatePermutationTable(Texture2D targetTexture, NoiseGeneratorOctaves2D source)
    {
        if (targetTexture == null || source == null || gpuClimatePermPixels == null) return;
        const float inv255 = 1.0f / 255.0f;

        for (int i = 0; i < gpuClimatePermPixels.Length; i++)
        {
            gpuClimatePermPixels[i] = new Color(0f, 0f, 0f, 1f);
        }

        int octaveCount = source.GetOctaveCount();
        for (int octave = 0; octave < octaveCount; octave++)
        {
            NoiseGenerator2D generator = source.GetGenerator(octave);
            if (generator == null || generator.permutations == null) continue;

            int rowOffset = octave * 512;
            for (int i = 0; i < 512; i++)
            {
                gpuClimatePermPixels[rowOffset + i] = new Color((float)generator.permutations[i] * inv255, 0f, 0f, 1f);
            }
        }

        targetTexture.SetPixels(gpuClimatePermPixels);
        targetTexture.Apply(false, false);
    }

    private void _UploadGpuClimateOffsetTexture(Texture2D targetTexture, NoiseGeneratorOctaves2D source)
    {
        if (targetTexture == null || source == null || gpuClimateOffsetPixels == null) return;

        for (int i = 0; i < gpuClimateOffsetPixels.Length; i++)
        {
            gpuClimateOffsetPixels[i] = new Color(0f, 0f, 0f, 1f);
        }

        int octaveCount = source.GetOctaveCount();
        for (int octave = 0; octave < octaveCount && octave < gpuClimateOffsetPixels.Length; octave++)
        {
            NoiseGenerator2D generator = source.GetGenerator(octave);
            if (generator == null) continue;
            gpuClimateOffsetPixels[octave] = new Color((float)generator.randomDX, (float)generator.randomDY, 0f, 1f);
        }

        targetTexture.SetPixels(gpuClimateOffsetPixels);
        targetTexture.Apply(false, false);
    }

    private void _UploadGpuClimateBiomeLookupTexture()
    {
        if (gpuClimateBiomeLookupTexture == null || gpuClimateBiomeLookupPixels == null) return;
        const double inv63 = 1.0D / 63.0D;

        BiomeOld biomeLookup = new BiomeOld();
        for (int rainIndex = 0; rainIndex < 64; rainIndex++)
        {
            int rowBase = rainIndex * 64;
            double rainfall = rainIndex * inv63;
            for (int tempIndex = 0; tempIndex < 64; tempIndex++)
            {
                BetaBiomeEnum biome = biomeLookup.getBiomeFromLookup(tempIndex * inv63, rainfall);
                gpuClimateBiomeLookupPixels[rowBase + tempIndex] = new Color(_GetPackedBiomeIdFloat(biome), 0f, 0f, 1f);
            }
        }

        gpuClimateBiomeLookupTexture.SetPixels(gpuClimateBiomeLookupPixels);
        gpuClimateBiomeLookupTexture.Apply(false, false);
    }

    private float _GetPackedBiomeIdFloat(BetaBiomeEnum biome)
    {
        switch (biome)
        {
            case BetaBiomeEnum.RAINFOREST: return 0.0f;
            case BetaBiomeEnum.SWAMPLAND: return 0.003921569f;
            case BetaBiomeEnum.SEASONAL_FOREST: return 0.007843138f;
            case BetaBiomeEnum.FOREST: return 0.011764706f;
            case BetaBiomeEnum.SAVANNA: return 0.015686275f;
            case BetaBiomeEnum.SHRUBLAND: return 0.019607844f;
            case BetaBiomeEnum.TAIGA: return 0.023529412f;
            case BetaBiomeEnum.DESERT: return 0.02745098f;
            case BetaBiomeEnum.PLAINS: return 0.03137255f;
            case BetaBiomeEnum.ICE_DESERT: return 0.03529412f;
            case BetaBiomeEnum.TUNDRA: return 0.039215688f;
        }
        return 0.0f;
    }

    private Texture2D _ResolveGpuPermutationTexture(NoiseGeneratorOctaves3D source)
    {
        if (source == null) return null;
        if (noiseGen1 != null && source == noiseGen1) return gpuPermTextureNoise1;
        if (noiseGen2 != null && source == noiseGen2) return gpuPermTextureNoise2;
        if (noiseGen3 != null && source == noiseGen3) return gpuPermTextureNoise3;
        if (noiseGen4 != null && source == noiseGen4) return gpuPermTextureNoise4;
        if (noiseGen5 != null && source == noiseGen5) return gpuPermTextureNoise5;
        if (noiseGen6 != null && source == noiseGen6) return gpuPermTextureNoise6;
        if (noiseGen7 != null && source == noiseGen7) return gpuPermTextureNoise7;
        return null;
    }

    private bool _EnsureGpuChunkSliceCacheBuilt()
    {
        if (!gpuFinalColumnSliceCachePending) return gpuCachedChunkSlicesReady;
        if (!gpuColumnReadbackReady || (gpuColumnReadbackPixels == null && gpuColumnReadbackBlocks == null)) return false;

        _BuildGpuChunkSliceCache();
        gpuCachedColumnX = gpuPendingColumnX;
        gpuCachedColumnZ = gpuPendingColumnZ;
        gpuCachedChunkSlicesReady = true;
        gpuFinalColumnSliceCachePending = false;
        return true;
    }

    private bool _StartGpuClimateGeneration()
    {
        if (!gpuWorldgenReady || gpuNoiseOctaveMaterial == null || gpuClimateTexture == null ||
            biomeTempNoiseGen == null || biomeRainNoiseGen == null || biomeModifierNoiseGen == null)
        {
            return false;
        }

        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimatePermTex0Id, gpuClimatePermTextureTemp);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimatePermTex1Id, gpuClimatePermTextureRain);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimatePermTex2Id, gpuClimatePermTextureModifier);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimateOffsetTex0Id, gpuClimateOffsetTextureTemp);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimateOffsetTex1Id, gpuClimateOffsetTextureRain);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimateOffsetTex2Id, gpuClimateOffsetTextureModifier);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimateBiomeLookupTexId, gpuClimateBiomeLookupTexture);
        gpuNoiseOctaveMaterial.SetInt(gpuPropClimateOctaveCount0Id, biomeTempNoiseGen.GetOctaveCount());
        gpuNoiseOctaveMaterial.SetInt(gpuPropClimateOctaveCount1Id, biomeRainNoiseGen.GetOctaveCount());
        gpuNoiseOctaveMaterial.SetInt(gpuPropClimateOctaveCount2Id, biomeModifierNoiseGen.GetOctaveCount());
        gpuNoiseOctaveMaterial.SetInt(gpuPropChunkXId, _GetTerrainBlockStartX(currentChunkX));
        gpuNoiseOctaveMaterial.SetInt(gpuPropChunkZId, _GetTerrainBlockStartZ(currentChunkZ));
        gpuNoiseOctaveMaterial.SetInt(gpuPropXSizeId, world.chunkSizeXZ);
        gpuNoiseOctaveMaterial.SetInt(gpuPropZSizeId, world.chunkSizeXZ);

#if LOGGING
        float blitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        VRCGraphics.Blit(gpuClearTexture, gpuClimateTexture, gpuNoiseOctaveMaterial, 2);
#if LOGGING
        if (enableDetailedTimings)
        {
            agg_gpuNoiseBlits++;
            agg_gpuNoiseBlitTime += (Time.realtimeSinceStartup - blitStart) * 1000f;
        }
#endif

        gpuClimateReadbackPending = true;
        gpuClimateReadbackReady = false;
        gpuClimateReadbackFailed = false;
        gpuClimatePendingChunkX = currentChunkX;
        gpuClimatePendingChunkZ = currentChunkZ;
        gpuReadbackPhase = GpuWorldgenReadbackPhase.Climate;
#if LOGGING
        if (enableDetailedTimings)
        {
            gpuReadbackRequestStartTimeMs = Time.realtimeSinceStartup * 1000f;
            gpuReadbackRequestBytes = gpuClimateReadbackPixels != null ? gpuClimateReadbackPixels.Length * 16 : world.chunkSizeXZ * world.chunkSizeXZ * 16;
        }
#endif
#if LOGGING
        float _rbReqT0 = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        VRCAsyncGPUReadback.Request(gpuClimateTexture, 0, TextureFormat.RGBAFloat, (IUdonEventReceiver)this);
#if LOGGING
        if (enableDetailedTimings) { agg_readbackRequestCalls++; agg_readbackRequestMs += (Time.realtimeSinceStartup - _rbReqT0) * 1000f; }
#endif
        return true;
    }

    private void _ApplyGpuClimateReadbackToCurrentChunk()
    {
        if (!gpuClimateReadbackReady || gpuClimateReadbackPixels == null || world == null) return;

        int sizeXZ = world.chunkSizeXZ;
        int total = sizeXZ * sizeXZ;
        if (currentChunkBiomes == null || currentChunkBiomes.Length != total)
        {
            currentChunkBiomes = new BetaBiomeEnum[total];
        }
        if (wcm.temperatures == null || wcm.temperatures.Length != total) wcm.temperatures = new double[total];
        if (wcm.rainfall == null || wcm.rainfall.Length != total) wcm.rainfall = new double[total];
        if (cachedTemperatures == null || cachedTemperatures.Length != total)
        {
            cachedTemperatures = new double[total];
            cachedRainfall = new double[total];
        }

        for (int z = 0; z < sizeXZ; z++)
        {
            int packedRowBase = z * sizeXZ;
            for (int x = 0; x < sizeXZ; x++)
            {
                int packedIndex = packedRowBase + x;
                int sourceIndex = x * sizeXZ + z;
                Color climate = gpuClimateReadbackPixels[packedIndex];
                double temp = climate.r;
                double rain = climate.g;
                int biomeId = Mathf.RoundToInt(climate.b * 255.0f);

                wcm.temperatures[sourceIndex] = temp;
                wcm.rainfall[sourceIndex] = rain;
                cachedTemperatures[sourceIndex] = temp;
                cachedRainfall[sourceIndex] = rain;
                currentChunkBiomes[sourceIndex] = (BetaBiomeEnum)Mathf.Clamp(biomeId, 0, 10);
            }
        }

        lastBiomeChunkX = currentChunkX;
        lastBiomeChunkZ = currentChunkZ;
        cachedBiomes = currentChunkBiomes;
        _CacheBiomeColumnData(currentChunkX, currentChunkZ, wcm.temperatures, wcm.rainfall);
        gpuClimateReadbackReady = false;
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

    /// <summary>
    /// Write one generator's precomputed coordinates into a 16-row block of the batched
    /// coord pixel arrays. Does NOT call SetPixels/Apply — call _FlushBatchedNoiseCoords after
    /// all generators have been written.
    /// </summary>
    // Y-COORD INVARIANCE: the Y coordinate block for a generator is a pure function of the seed
    // (every call site passes yPos=0, gridY is a per-generator constant, generator.yCoord is fixed
    // at seed init). It was rebuilt (2,600 double-precision iterations + 5,200 Color writes) and
    // re-uploaded (~83KB of the ~104KB coord upload) for EVERY column. Write each generator's Y
    // rows exactly once; the flush uploads the Y texture only while new rows appear.
    private bool[] gpuCoordYRowDone = new bool[16];
    private bool gpuCoordYDirty = false;

    private void _WriteNoiseCoordBlock(NoiseGeneratorOctaves3D source, int octaveCount,
        int xSize, int ySize, int zSize, int xPos, int yPos, int zPos,
        double gridX, double gridY, double gridZ, bool is2D, int generatorIndex)
    {
        int baseRow = generatorIndex * GPU_MAX_OCTAVES;
        bool writeY = generatorIndex >= gpuCoordYRowDone.Length || !gpuCoordYRowDone[generatorIndex];
        for (int octave = 0; octave < octaveCount; octave++)
        {
            NoiseGenerator3dPerlin generator = source.GetGenerator(octave);
            if (generator == null) continue;

            double octaveFrequency = System.Math.Pow(0.5, octave);
            double scaledGridX = gridX * octaveFrequency;
            double scaledGridY = gridY * octaveFrequency;
            double scaledGridZ = gridZ * octaveFrequency;

            _PrecomputeNoiseCoords(gpuPrePermX, gpuPreFracX, xSize, (double)xPos, scaledGridX, generator.xCoord);
            if (!is2D && writeY)
            {
                _PrecomputeNoiseCoordsY(gpuPrePermY, gpuPreFracY, gpuPreGradFracY, ySize, (double)yPos, scaledGridY, generator.yCoord);
            }
            _PrecomputeNoiseCoords(gpuPrePermZ, gpuPreFracZ, zSize, (double)zPos, scaledGridZ, generator.zCoord);

            int xzRowOffset = (baseRow + octave) * GPU_MAX_XZSIZE;
            for (int i = 0; i < GPU_MAX_XZSIZE; i++)
            {
                float xPerm = i < xSize ? gpuPrePermX[i] / 255.0f : 0f;
                float xFrac = i < xSize ? gpuPreFracX[i] : 0f;
                float zPerm = i < zSize ? gpuPrePermZ[i] / 255.0f : 0f;
                float zFrac = i < zSize ? gpuPreFracZ[i] : 0f;
                gpuCoordXZPixels[xzRowOffset + i] = new Color(xPerm, xFrac, zPerm, zFrac);
            }

            if (writeY)
            {
                int yRowOff = (baseRow + octave) * GPU_MAX_YSIZE;
                for (int i = 0; i < GPU_MAX_YSIZE; i++)
                {
                    if (!is2D && i < ySize)
                    {
                        gpuCoordYPixels[yRowOff + i] = new Color(gpuPrePermY[i] / 255.0f, gpuPreFracY[i], gpuPreGradFracY[i], 1f);
                    }
                    else
                    {
                        gpuCoordYPixels[yRowOff + i] = new Color(0f, 0f, 0f, 1f);
                    }
                }
            }
        }
        // Zero any unused octave rows for this generator
        for (int octave = octaveCount; octave < GPU_MAX_OCTAVES; octave++)
        {
            int xzRowOffset = (baseRow + octave) * GPU_MAX_XZSIZE;
            for (int i = 0; i < GPU_MAX_XZSIZE; i++)
                gpuCoordXZPixels[xzRowOffset + i] = new Color(0f, 0f, 0f, 0f);
            if (writeY)
            {
                int yRowOff = (baseRow + octave) * GPU_MAX_YSIZE;
                for (int i = 0; i < GPU_MAX_YSIZE; i++)
                    gpuCoordYPixels[yRowOff + i] = new Color(0f, 0f, 0f, 1f);
            }
        }
        if (writeY && generatorIndex >= 0 && generatorIndex < gpuCoordYRowDone.Length)
        {
            gpuCoordYRowDone[generatorIndex] = true;
            gpuCoordYDirty = true;
        }
    }

    /// <summary>
    /// Flush the batched coord pixel arrays to the GPU. Call once after all
    /// _WriteNoiseCoordBlock calls are done.
    /// </summary>
    private void _FlushBatchedNoiseCoords()
    {
#if LOGGING
        float uploadStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
        int bytes = gpuCoordXZPixels.Length * 16;
#endif
        gpuCoordXZTexture.SetPixels(gpuCoordXZPixels);
        gpuCoordXZTexture.Apply(false, false);
        // Y-COORD INVARIANCE: upload the Y texture only when new generator rows were written
        // this batch (first column or two); after that it never changes.
        if (gpuCoordYDirty)
        {
            gpuCoordYTexture.SetPixels(gpuCoordYPixels);
            gpuCoordYTexture.Apply(false, false);
            gpuCoordYDirty = false;
#if LOGGING
            bytes += gpuCoordYPixels.Length * 16;
#endif
        }
#if LOGGING
        if (enableDetailedTimings)
        {
            _RecordGpuNoiseInputUpload((Time.realtimeSinceStartup - uploadStart) * 1000f, bytes);
        }
#endif
    }

    private void _RunGpuNoiseOctaves(NoiseGeneratorOctaves3D source, int octaveCount, RenderTexture resultTexture, int xPos, int yPos, int zPos, int xSize, int ySize, int zSize, double gridX, double gridY, double gridZ, bool is2D, int octaveRowOffset)
    {
        if (!gpuWorldgenReady || source == null || resultTexture == null) return;
        Texture2D permutationTexture = _ResolveGpuPermutationTexture(source);
#if LOGGING
        float uploadStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        if (permutationTexture == null)
        {
            _UploadGpuPermutationTable(gpuPermTexture, source, octaveCount);
            permutationTexture = gpuPermTexture;
#if LOGGING
            if (enableDetailedTimings)
            {
                int permBytes = gpuPermutationPixels.Length * 4;
                _RecordGpuNoiseInputUpload((Time.realtimeSinceStartup - uploadStart) * 1000f, permBytes);
            }
#endif
        }

        gpuNoiseOctaveMaterial.SetTexture(gpuPropPermTexId, permutationTexture);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropCoordXZTexId, gpuCoordXZTexture);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropCoordYTexId, gpuCoordYTexture);
        gpuNoiseOctaveMaterial.SetInt(gpuPropOctaveId, octaveCount);
        gpuNoiseOctaveMaterial.SetInt(gpuPropXSizeId, xSize);
        gpuNoiseOctaveMaterial.SetInt(gpuPropYSizeId, ySize);
        gpuNoiseOctaveMaterial.SetInt(gpuPropZSizeId, zSize);
        gpuNoiseOctaveMaterial.SetInt(gpuPropIs2DId, is2D ? 1 : 0);
        gpuNoiseOctaveMaterial.SetInt(gpuPropOctaveRowOffsetId, octaveRowOffset);
        gpuNoiseOctaveMaterial.SetInt(gpuPropCoordTexHeightId, GPU_COORD_TEX_HEIGHT);

#if LOGGING
        float blitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        VRCGraphics.Blit(gpuClearTexture, resultTexture, gpuNoiseOctaveMaterial, 1);
#if LOGGING
        if (enableDetailedTimings)
        {
            agg_gpuNoiseBlits++;
            agg_gpuNoiseBlitTime += (Time.realtimeSinceStartup - blitStart) * 1000f;
        }
#endif
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

    /// <summary>
    /// Upload CPU-computed surface noise (sand/gravel/stone) to a GPU RenderTexture.
    /// CPU array order: [x][z] = array[x * size + z]
    /// Texture layout: pixel(x, z) = pixels[z * size + x]
    /// Uses the dedicated 16x16 gpuSurfaceNoiseUpload texture.
    /// </summary>
    private void _UploadCpuSurfaceNoise(double[] cpuNoise, int size, RenderTexture target)
    {
        for (int nx = 0; nx < size; nx++)
            for (int nz = 0; nz < size; nz++)
            {
                int cpuIdx = nx * size + nz;
                int texIdx = nz * size + nx;
                gpuSurfaceNoiseUploadPixels[texIdx] = new Color((float)cpuNoise[cpuIdx], 0f, 0f, 1f);
            }
        gpuSurfaceNoiseUpload.SetPixels(gpuSurfaceNoiseUploadPixels);
        gpuSurfaceNoiseUpload.Apply(false, false);
        VRCGraphics.Blit(gpuSurfaceNoiseUpload, target);
    }

    private bool _ShouldRunGpuNoiseDiagnosticsForCurrentChunk()
    {
        if (!enableGpuNoiseDiagnostics) return false;
        if (currentChunkX != gpuNoiseDiagnosticChunkX || currentChunkZ != gpuNoiseDiagnosticChunkZ) return false;
        if (gpuNoiseDiagnosticRunOncePerChunk && gpuDiagLastLoggedChunkX == currentChunkX && gpuDiagLastLoggedChunkZ == currentChunkZ) return false;
        return true;
    }

    private void _BuildGpuNoiseDiagnosticCpuReference()
    {
        int xSize = 5;
        int ySize = gpuWorldHeightBlocks / 8 + 1;
        int zSize = 5;
        double d0 = 684.412D;
        double d1 = 684.412D;
        int noiseStartX = _GetTerrainNoiseStartX(currentChunkX, 4);
        int noiseStartZ = _GetTerrainNoiseStartZ(currentChunkZ, 4);

        gpuDiagCpuNoise1 = noiseGen1.generateNoiseOctaves(gpuDiagCpuNoise1, noiseStartX, 0, noiseStartZ, xSize, ySize, zSize, d0, d1, d0);
        gpuDiagCpuNoise2 = noiseGen2.generateNoiseOctaves(gpuDiagCpuNoise2, noiseStartX, 0, noiseStartZ, xSize, ySize, zSize, d0, d1, d0);
        gpuDiagCpuNoise3 = noiseGen3.generateNoiseOctaves(gpuDiagCpuNoise3, noiseStartX, 0, noiseStartZ, xSize, ySize, zSize, d0 / 80.0D, d1 / 160.0D, d0 / 80.0D);
        gpuDiagCpuNoise6 = noiseGen6.generateNoiseArray(gpuDiagCpuNoise6, noiseStartX, noiseStartZ, xSize, zSize, 1.121D, 1.121D, 0.5D);
        gpuDiagCpuNoise7 = noiseGen7.generateNoiseArray(gpuDiagCpuNoise7, noiseStartX, noiseStartZ, xSize, zSize, 200.0D, 200.0D, 0.5D);

        int densityCount = xSize * ySize * zSize;
        if (gpuDiagCpuDensity == null || gpuDiagCpuDensity.Length != densityCount)
        {
            gpuDiagCpuDensity = new double[densityCount];
        }
        else
        {
            System.Array.Clear(gpuDiagCpuDensity, 0, gpuDiagCpuDensity.Length);
        }

        int i2 = 16 / xSize;
        double[] temp = wcm.temperatures;
        double[] rain = wcm.rainfall;
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
                d4 *= d4;
                d4 *= d4;
                d4 = 1.0D - d4;
                double d5 = (gpuDiagCpuNoise6[l1] + 256.0D) / 512.0D;
                d5 *= d4;
                if (d5 > 1.0D) d5 = 1.0D;
                double d6 = gpuDiagCpuNoise7[l1] / 8000.0D;
                if (d6 < 0.0D) d6 = -d6 * 0.3D;
                d6 = d6 * 3.0D - 2.0D;
                if (d6 < 0.0D)
                {
                    d6 *= 0.5D;
                    if (d6 < -1D) d6 = -1D;
                    d6 *= 0.3571428571428571D;
                    d5 = 0.0D;
                }
                else
                {
                    if (d6 > 1.0D) d6 = 1.0D;
                    d6 *= 0.125D;
                }
                if (d5 < 0.0D) d5 = 0.0D;
                d5 += 0.5D;
                d6 = (d6 * (double)ySize) / 16.0D;
                double d7 = (double)ySize / 2.0D + d6 * 4.0D;

                for (int y = 0; y < ySize; y++)
                {
                    double d9 = (((double)y - d7) * 12D) / d5;
                    if (d9 < 0.0D) d9 *= 4.0D;
                    double d10 = gpuDiagCpuNoise1[k1] / 512.0D;
                    double d11 = gpuDiagCpuNoise2[k1] / 512.0D;
                    double d12 = (gpuDiagCpuNoise3[k1] * 0.1D + 1.0D) * 0.5D;
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
                    gpuDiagCpuDensity[k1] = d8;
                    k1++;
                }
                l1++;
            }
        }

        gpuDiagChunkX = currentChunkX;
        gpuDiagChunkZ = currentChunkZ;
    }

    private bool _BeginGpuBaseColumnReadback(RenderTexture source = null, bool containsFinalColumn = false)
    {
        if (source == null) source = gpuColumnBaseTexture;
        if (containsFinalColumn) gpuLastReadbackSource = source; // remember the final column texture for GPU->GPU repack
        gpuDiagnosticReadbackStallFrames = 0;
        gpuReadbackPhase = GpuWorldgenReadbackPhase.BaseColumn;
        gpuColumnReadbackPending = true;
        gpuReadbackContainsFinalColumn = containsFinalColumn;
#if LOGGING
        if (enableDetailedTimings)
        {
            gpuReadbackRequestStartTimeMs = Time.realtimeSinceStartup * 1000f;
            int pixelCount = gpuColumnReadbackBlocks != null ? gpuColumnReadbackBlocks.Length : world.chunkSizeXZ * gpuWorldHeightBlocks * world.chunkSizeXZ;
            gpuReadbackRequestBytes = useSingleChannelGpuColumnReadback ? pixelCount : pixelCount * 4;
        }
#endif
#if LOGGING
        float _rbReqT1 = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        VRCAsyncGPUReadback.Request(source, 0, useSingleChannelGpuColumnReadback ? TextureFormat.R8 : TextureFormat.RGBA32, (IUdonEventReceiver)this);
#if LOGGING
        if (enableDetailedTimings) { agg_readbackRequestCalls++; agg_readbackRequestMs += (Time.realtimeSinceStartup - _rbReqT1) * 1000f; }
#endif
        return true;
    }

    private bool _RequestGpuNoiseDiagnosticReadback(RenderTexture source, GpuWorldgenReadbackPhase phase)
    {
        if (source == null) return false;
        gpuDiagnosticReadbackStallFrames = 0;
        gpuReadbackPhase = phase;
        gpuColumnReadbackPending = true;
        gpuReadbackContainsFinalColumn = false;
#if LOGGING
        if (enableDetailedTimings)
        {
            gpuReadbackRequestStartTimeMs = Time.realtimeSinceStartup * 1000f;
            gpuReadbackRequestBytes = source.width * source.height * 16;
        }
#endif
        VRCAsyncGPUReadback.Request(source, 0, TextureFormat.RGBAFloat, (IUdonEventReceiver)this);
        return true;
    }

    private void _AppendGpuNoiseDiagnosticComparison(string label, double[] cpuValues, bool is2D)
    {
        if (gpuNoiseDiagnosticLog == null || cpuValues == null || gpuNoiseDiagnosticReadbackPixels == null) return;

        int xSize = 5;
        int ySize = gpuWorldHeightBlocks / 8 + 1;
        int zSize = 5;
        int packedWidth = xSize * zSize;
        double maxAbsDiff = 0.0D;
        double sumAbsDiff = 0.0D;
        int signMismatchCount = 0;
        int over1e3Count = 0;
        int over1e2Count = 0;
        int worstX = 0;
        int worstY = 0;
        int worstZ = 0;
        double worstCpu = 0.0D;
        double worstGpu = 0.0D;
        int total = is2D ? xSize * zSize : xSize * ySize * zSize;

        if (gpuNoiseDiagnosticDumpAllCells)
        {
            gpuNoiseDiagnosticLog.Append(label).Append(" values").AppendLine();
        }

        if (is2D)
        {
            for (int z = 0; z < zSize; z++)
            {
                for (int x = 0; x < xSize; x++)
                {
                    int cpuIndex = x * zSize + z;
                    int texIndex = z * xSize + x;
                    double cpuValue = cpuValues[cpuIndex];
                    double gpuValue = gpuNoiseDiagnosticReadbackPixels[texIndex].r;
                    double absDiff = System.Math.Abs(cpuValue - gpuValue);
                    sumAbsDiff += absDiff;
                    if (absDiff > maxAbsDiff)
                    {
                        maxAbsDiff = absDiff;
                        worstX = x;
                        worstY = 0;
                        worstZ = z;
                        worstCpu = cpuValue;
                        worstGpu = gpuValue;
                    }
                    if ((cpuValue > 0.0D) != (gpuValue > 0.0D)) signMismatchCount++;
                    if (absDiff > 0.001D) over1e3Count++;
                    if (absDiff > 0.01D) over1e2Count++;
                    if (gpuNoiseDiagnosticDumpAllCells)
                    {
                        gpuNoiseDiagnosticLog.Append("  [x=").Append(x).Append(",z=").Append(z)
                            .Append("] cpu=").Append(cpuValue.ToString("F6"))
                            .Append(" gpu=").Append(gpuValue.ToString("F6"))
                            .Append(" diff=").Append((cpuValue - gpuValue).ToString("F6"))
                            .AppendLine();
                    }
                }
            }
        }
        else
        {
            for (int y = 0; y < ySize; y++)
            {
                int rowBase = y * packedWidth;
                for (int z = 0; z < zSize; z++)
                {
                    int packedXZ = z * xSize;
                    for (int x = 0; x < xSize; x++)
                    {
                        int cpuIndex = x * zSize * ySize + z * ySize + y;
                        int texIndex = rowBase + packedXZ + x;
                        double cpuValue = cpuValues[cpuIndex];
                        double gpuValue = gpuNoiseDiagnosticReadbackPixels[texIndex].r;
                        double absDiff = System.Math.Abs(cpuValue - gpuValue);
                        sumAbsDiff += absDiff;
                        if (absDiff > maxAbsDiff)
                        {
                            maxAbsDiff = absDiff;
                            worstX = x;
                            worstY = y;
                            worstZ = z;
                            worstCpu = cpuValue;
                            worstGpu = gpuValue;
                        }
                        if ((cpuValue > 0.0D) != (gpuValue > 0.0D)) signMismatchCount++;
                        if (absDiff > 0.001D) over1e3Count++;
                        if (absDiff > 0.01D) over1e2Count++;
                        if (gpuNoiseDiagnosticDumpAllCells)
                        {
                            gpuNoiseDiagnosticLog.Append("  [x=").Append(x).Append(",y=").Append(y).Append(",z=").Append(z)
                                .Append("] cpu=").Append(cpuValue.ToString("F6"))
                                .Append(" gpu=").Append(gpuValue.ToString("F6"))
                                .Append(" diff=").Append((cpuValue - gpuValue).ToString("F6"))
                                .AppendLine();
                        }
                    }
                }
            }
        }

        gpuNoiseDiagnosticLog.Append(label)
            .Append(" summary: avgAbs=").Append((sumAbsDiff / (double)total).ToString("F6"))
            .Append(" maxAbs=").Append(maxAbsDiff.ToString("F6"))
            .Append(" signMismatches=").Append(signMismatchCount).Append("/").Append(total)
            .Append(" gt1e-3=").Append(over1e3Count)
            .Append(" gt1e-2=").Append(over1e2Count)
            .Append(" worst=(").Append(worstX).Append(",").Append(worstY).Append(",").Append(worstZ).Append(")")
            .Append(" cpu=").Append(worstCpu.ToString("F6"))
            .Append(" gpu=").Append(worstGpu.ToString("F6"))
            .AppendLine();
    }

    private bool _RequestNextGpuNoiseDiagnosticPhase(GpuWorldgenReadbackPhase completedPhase)
    {
        switch (completedPhase)
        {
            case GpuWorldgenReadbackPhase.DiagnosticNoise1:
                return _RequestGpuNoiseDiagnosticReadback(gpuNoise2Texture, GpuWorldgenReadbackPhase.DiagnosticNoise2);
            case GpuWorldgenReadbackPhase.DiagnosticNoise2:
                return _RequestGpuNoiseDiagnosticReadback(gpuNoise3Texture, GpuWorldgenReadbackPhase.DiagnosticNoise3);
            case GpuWorldgenReadbackPhase.DiagnosticNoise3:
                return _RequestGpuNoiseDiagnosticReadback(gpuNoise6Texture, GpuWorldgenReadbackPhase.DiagnosticNoise6);
            case GpuWorldgenReadbackPhase.DiagnosticNoise6:
                return _RequestGpuNoiseDiagnosticReadback(gpuNoise7Texture, GpuWorldgenReadbackPhase.DiagnosticNoise7);
            case GpuWorldgenReadbackPhase.DiagnosticNoise7:
                return _RequestGpuNoiseDiagnosticReadback(gpuDensityTexture, GpuWorldgenReadbackPhase.DiagnosticDensity);
            default:
                return false;
        }
    }

    private bool _HandleGpuNoiseDiagnosticReadback(VRCAsyncGPUReadbackRequest request, float latencyMs, float callbackStartMs)
    {
        GpuWorldgenReadbackPhase phase = gpuReadbackPhase;
        gpuColumnReadbackPending = false;
        gpuReadbackPhase = GpuWorldgenReadbackPhase.None;

#if LOGGING
        bool success = false;
        float callbackCopyMs = 0f;
#endif

        if (request.hasError)
        {
            gpuNoiseDiagnosticLog.Append("[GPU Noise Diagnostic] Readback error in phase ").Append(phase).AppendLine();
        }
        else if (!request.TryGetData(gpuNoiseDiagnosticReadbackPixels, 0))
        {
            gpuNoiseDiagnosticLog.Append("[GPU Noise Diagnostic] TryGetData failed in phase ").Append(phase).AppendLine();
        }
        else
        {
#if LOGGING
            success = true;
            callbackCopyMs = Time.realtimeSinceStartup * 1000f - callbackStartMs;
#endif
            if (phase == GpuWorldgenReadbackPhase.DiagnosticNoise1) _AppendGpuNoiseDiagnosticComparison("noise1", gpuDiagCpuNoise1, false);
            else if (phase == GpuWorldgenReadbackPhase.DiagnosticNoise2) _AppendGpuNoiseDiagnosticComparison("noise2", gpuDiagCpuNoise2, false);
            else if (phase == GpuWorldgenReadbackPhase.DiagnosticNoise3) _AppendGpuNoiseDiagnosticComparison("noise3", gpuDiagCpuNoise3, false);
            else if (phase == GpuWorldgenReadbackPhase.DiagnosticNoise6) _AppendGpuNoiseDiagnosticComparison("noise6", gpuDiagCpuNoise6, true);
            else if (phase == GpuWorldgenReadbackPhase.DiagnosticNoise7) _AppendGpuNoiseDiagnosticComparison("noise7", gpuDiagCpuNoise7, true);
            else if (phase == GpuWorldgenReadbackPhase.DiagnosticDensity) _AppendGpuNoiseDiagnosticComparison("density", gpuDiagCpuDensity, false);
        }

#if LOGGING
        if (enableDetailedTimings)
        {
            _RecordGpuReadbackCompletion(true, success, latencyMs, callbackCopyMs, gpuReadbackRequestBytes);
        }
#endif

        if (phase == GpuWorldgenReadbackPhase.DiagnosticDensity)
        {
            gpuNoiseDiagnosticLog.Append("[GPU Noise Diagnostic] chunk=(").Append(gpuDiagChunkX).Append(",").Append(gpuDiagChunkZ).Append(")").AppendLine();
            Debug.Log(gpuNoiseDiagnosticLog.ToString());
            gpuDiagLastLoggedChunkX = gpuDiagChunkX;
            gpuDiagLastLoggedChunkZ = gpuDiagChunkZ;
            return _BeginGpuBaseColumnReadback();
        }

        return _RequestNextGpuNoiseDiagnosticPhase(phase);
    }

    private bool _StartGpuColumnGeneration()
    {
        if (!gpuWorldgenReady) return false;

#if LOGGING
        if (enableDetailedTimings) agg_gpuColumnsStarted++;
#endif
        // Climate data already lives in gpuClimateTexture from _StartGpuClimateGeneration.
        // No need to round-trip through CPU and re-upload.

        int densityXSize = 5;
        int densityYSize = gpuWorldHeightBlocks / 8 + 1;
        int densityZSize = 5;
        int densityXPos = _GetTerrainNoiseStartX(currentChunkX, 4);
        int densityZPos = _GetTerrainNoiseStartZ(currentChunkZ, 4);
        double d0 = 684.412D;
        double d1 = 684.412D;

        // Batch all 5 density generators' coords into a single texture upload (2 Apply calls
        // instead of 10), then run each noise blit reading from its own row block.
        _WriteNoiseCoordBlock(noiseGen1, 16, densityXSize, densityYSize, densityZSize,
            densityXPos, 0, densityZPos, d0, d1, d0, false, 0);
        _WriteNoiseCoordBlock(noiseGen2, 16, densityXSize, densityYSize, densityZSize,
            densityXPos, 0, densityZPos, d0, d1, d0, false, 1);
        _WriteNoiseCoordBlock(noiseGen3, 8, densityXSize, densityYSize, densityZSize,
            densityXPos, 0, densityZPos, d0 / 80.0D, d1 / 160.0D, d0 / 80.0D, false, 2);
        _WriteNoiseCoordBlock(noiseGen6, 10, densityXSize, 1, densityZSize,
            densityXPos, 10, densityZPos, 1.121D, 1.0D, 1.121D, true, 3);
        _WriteNoiseCoordBlock(noiseGen7, 16, densityXSize, 1, densityZSize,
            densityXPos, 10, densityZPos, 200.0D, 1.0D, 200.0D, true, 4);
        _FlushBatchedNoiseCoords();

        _RunGpuNoiseOctaves(noiseGen1, 16, gpuNoise1Texture,
            densityXPos, 0, densityZPos, densityXSize, densityYSize, densityZSize,
            d0, d1, d0, false, 0 * GPU_MAX_OCTAVES);
        _RunGpuNoiseOctaves(noiseGen2, 16, gpuNoise2Texture,
            densityXPos, 0, densityZPos, densityXSize, densityYSize, densityZSize,
            d0, d1, d0, false, 1 * GPU_MAX_OCTAVES);
        _RunGpuNoiseOctaves(noiseGen3, 8, gpuNoise3Texture,
            densityXPos, 0, densityZPos, densityXSize, densityYSize, densityZSize,
            d0 / 80.0D, d1 / 160.0D, d0 / 80.0D, false, 2 * GPU_MAX_OCTAVES);
        _RunGpuNoiseOctaves(noiseGen6, 10, gpuNoise6Texture,
            densityXPos, 10, densityZPos, densityXSize, 1, densityZSize,
            1.121D, 1.0D, 1.121D, true, 3 * GPU_MAX_OCTAVES);
        _RunGpuNoiseOctaves(noiseGen7, 16, gpuNoise7Texture,
            densityXPos, 10, densityZPos, densityXSize, 1, densityZSize,
            200.0D, 1.0D, 200.0D, true, 4 * GPU_MAX_OCTAVES);


        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise1TexId, gpuNoise1Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise2TexId, gpuNoise2Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise3TexId, gpuNoise3Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise6TexId, gpuNoise6Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise7TexId, gpuNoise7Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropTemperatureTexId, gpuClimateTexture);
        gpuNoiseCombineMaterial.SetInt(gpuPropXSizeId, densityXSize);
        gpuNoiseCombineMaterial.SetInt(gpuPropYSizeId, densityYSize);
        gpuNoiseCombineMaterial.SetInt(gpuPropZSizeId, densityZSize);
        gpuNoiseCombineMaterial.SetInt(gpuPropChunkXId, currentChunkX);
        gpuNoiseCombineMaterial.SetInt(gpuPropChunkZId, currentChunkZ);
        gpuNoiseCombineMaterial.SetInt(gpuPropFlipXAxisId, match1to1TerrainBaseline ? 0 : (flipXAxis ? 1 : 0));
        gpuNoiseCombineMaterial.SetInt(gpuPropBuiltinOffsetXId, BUILTIN_OFFSET_X);
        gpuNoiseCombineMaterial.SetInt(gpuPropBuiltinOffsetZId, BUILTIN_OFFSET_Z);
#if LOGGING
        float blitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        VRCGraphics.Blit(gpuNoise1Texture, gpuDensityTexture, gpuNoiseCombineMaterial, 1);
#if LOGGING
        if (enableDetailedTimings)
        {
            agg_gpuCombineBlits++;
            agg_gpuCombineBlitTime += (Time.realtimeSinceStartup - blitStart) * 1000f;
        }
#endif

        gpuColumnBaseFillMaterial.SetTexture(gpuPropDensityTexId, gpuDensityTexture);
        gpuColumnBaseFillMaterial.SetTexture(gpuPropTemperatureTexId, gpuClimateTexture);
        gpuColumnBaseFillMaterial.SetInt(gpuPropWorldHeightId, gpuWorldHeightBlocks);
        gpuColumnBaseFillMaterial.SetInt(gpuPropChunkSizeXZId, world.chunkSizeXZ);
        gpuColumnBaseFillMaterial.SetInt(gpuPropOceanHeightId, 64);
        gpuColumnBaseFillMaterial.SetInt(gpuPropFlipXAxisId, match1to1TerrainBaseline ? 0 : (flipXAxis ? 1 : 0));
        gpuColumnBaseFillMaterial.SetInt(gpuPropStoneBlockId, stoneBlockID);
        gpuColumnBaseFillMaterial.SetInt(gpuPropWaterBlockId, waterBlockID);
        gpuColumnBaseFillMaterial.SetInt(gpuPropIceBlockId, (int)BlockMaterial.ICE);
#if LOGGING
        blitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        VRCGraphics.Blit(gpuDensityTexture, gpuColumnBaseTexture, gpuColumnBaseFillMaterial);
#if LOGGING
        if (enableDetailedTimings)
        {
            agg_gpuBaseFillBlits++;
            agg_gpuBaseFillBlitTime += (Time.realtimeSinceStartup - blitStart) * 1000f;
        }
#endif

        gpuColumnReadbackPending = false;
        gpuColumnReadbackReady = false;
        gpuColumnReadbackFailed = false;
        gpuPendingColumnX = currentChunkX;
        gpuPendingColumnZ = currentChunkZ;
        gpuCachedChunkSlicesReady = false;
        gpuFinalColumnSliceCachePending = false;
        gpuColumnSurfaceInfoMaterial.SetTexture(gpuPropBaseColumnTexId, gpuColumnBaseTexture);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropWorldHeightId, gpuWorldHeightBlocks);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropChunkSizeXZId, world.chunkSizeXZ);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropStoneBlockId, stoneBlockID);
#if LOGGING
        blitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        VRCGraphics.Blit(gpuColumnBaseTexture, gpuColumnSurfaceInfoTexture, gpuColumnSurfaceInfoMaterial);
#if LOGGING
        if (enableDetailedTimings)
        {
            agg_gpuSurfaceInfoBlits++;
            agg_gpuSurfaceInfoBlitTime += (Time.realtimeSinceStartup - blitStart) * 1000f;
        }
#endif

        if (_ShouldRunGpuNoiseDiagnosticsForCurrentChunk())
        {
            _BuildGpuNoiseDiagnosticCpuReference();
            gpuNoiseDiagnosticLog.Length = 0;
            gpuNoiseDiagnosticLog.Append("[GPU Noise Diagnostic] Begin chunk=(").Append(currentChunkX).Append(",").Append(currentChunkZ).Append(")").AppendLine();
            return _RequestGpuNoiseDiagnosticReadback(gpuNoise1Texture, GpuWorldgenReadbackPhase.DiagnosticNoise1);
        }

        return true;
    }

    private void _UploadGpuSurfaceTextures()
    {
        if (gpuSurfaceParamsTextureA == null || gpuSurfaceParamsTextureB == null || gpuBedrockMaskTexture == null) return;

#if LOGGING
        float uploadStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        gpuSurfaceParamsTextureA.SetPixels(gpuSurfaceParamPixelsA);
        gpuSurfaceParamsTextureA.Apply(false, false);
        gpuSurfaceParamsTextureB.SetPixels(gpuSurfaceParamPixelsB);
        gpuSurfaceParamsTextureB.Apply(false, false);
        gpuBedrockMaskTexture.SetPixels(gpuBedrockMaskPixels);
        gpuBedrockMaskTexture.Apply(false, false);
#if LOGGING
        if (enableDetailedTimings)
        {
            int bytes = (gpuSurfaceParamPixelsA.Length + gpuSurfaceParamPixelsB.Length + gpuBedrockMaskPixels.Length) * 4;
            _RecordGpuSurfaceUpload((Time.realtimeSinceStartup - uploadStart) * 1000f, bytes);
        }
#endif
    }

    private void _BuildGpuSurfaceParamsFromSurfaceInfo()
    {
        if (currentChunkBiomes == null) return;

        int sizeXZ = world.chunkSizeXZ;
        int count = sizeXZ * sizeXZ;
        int bedrockCount = sizeXZ * 5 * sizeXZ;
        for (int i = 0; i < count; i++)
        {
            gpuSurfaceParamPixelsA[i] = new Color(0f, 0f, 0f, 1f);
            gpuSurfaceParamPixelsB[i] = new Color(0f, 0f, 0f, 0f);
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
                BetaBiomeEnum biome = currentChunkBiomes[biomeIndex];
                byte topBlock = BiomeOld.top(biome);
                byte fillerBlock = BiomeOld.filler(biome);
                float sandRand = (float)random.NextDouble();
                float gravelRand = (float)random.NextDouble();
                float depthRand = (float)random.NextDouble();
                int sandstoneDepth = random.NextInt(4);

                gpuSurfaceParamPixelsA[packedIndex] = new Color(topBlock / 255.0f, fillerBlock / 255.0f, 0f, 1f);
                gpuSurfaceParamPixelsB[packedIndex] = new Color(sandRand, gravelRand, depthRand, sandstoneDepth / 255.0f);
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
        if (!gpuWorldgenReady || gpuColumnSurfaceReplaceMaterial == null || currentChunkBiomes == null) return false;
        dbgColumnFinalizeCount++;

        // FINALIZE SLICING guard: the resident-repack contract (CanRepackGpuResidentColumn)
        // relied on texture-overwrite and resident-marker-advance being ONE atomic call. Now
        // that decoration/latching moved to Prepare_GpuDecorate, the surface-replace blit
        // below stomps gpuColumnFinalTexture one or more FRAMES before step 2 advances the
        // markers — a stale resident sibling stepping in that window would repack this NEW
        // column's half-finished terrain into the OLD column's atlas slot (silent wrong
        // terrain). Invalidate the previous column's latch before the first overwrite; its
        // stale siblings fall back to a normal re-generation (correct, just slower).
        if (gpuResidentColumnX != gpuPendingColumnX || gpuResidentColumnZ != gpuPendingColumnZ)
        {
            gpuLastReadbackSource = null;
            gpuResidentColumnX = int.MaxValue;
            gpuResidentColumnZ = int.MaxValue;
        }

        int sizeXZ = world.chunkSizeXZ;

        // Sand/gravel/stone noise: computed on CPU because the GPU noise shader produces
        // incorrect values for these 16x16 surface noise textures (confirmed against vanilla MC).
        // FINALIZE SLICING: the values come from the Prepare_Sand/Gravel/StoneNoise states
        // (identical generator + arguments — see those cases), each its own budgeted step.
        // Computing all three inline here was most of the GpuFinalize atomic block. Null
        // buffers (never possible on the normal path) fall back to the CPU pipeline.
        if (this.sandNoise == null || this.gravelNoise == null || this.stoneNoise == null) return false;
        _UploadCpuSurfaceNoise(this.sandNoise, sizeXZ, gpuSandNoiseTexture);
        _UploadCpuSurfaceNoise(this.gravelNoise, sizeXZ, gpuGravelNoiseTexture);
        _UploadCpuSurfaceNoise(this.stoneNoise, sizeXZ, gpuStoneNoiseTexture);

        _BuildGpuSurfaceParamsFromSurfaceInfo();

        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropBaseColumnTexId, gpuColumnBaseTexture);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropSurfaceInfoTexId, gpuColumnSurfaceInfoTexture);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropSurfaceParamsTexAId, gpuSurfaceParamsTextureA);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropSurfaceParamsTexBId, gpuSurfaceParamsTextureB);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropBedrockMaskTexId, gpuBedrockMaskTexture);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropSandNoiseTexId, gpuSandNoiseTexture);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropGravelNoiseTexId, gpuGravelNoiseTexture);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropStoneNoiseTexId, gpuStoneNoiseTexture);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropWorldHeightId, gpuWorldHeightBlocks);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropChunkSizeXZId, sizeXZ);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropStoneBlockId, stoneBlockID);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropBedrockBlockId, bedrockBlockID);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropSandBlockId, sandBlockID);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropGravelBlockId, (int)BlockMaterial.GRAVEL);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropWaterBlockId, waterBlockID);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropSandstoneBlockId, sandStoneBlockID);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropFlipXAxisId, match1to1TerrainBaseline ? 0 : (flipXAxis ? 1 : 0));

#if LOGGING
        float blitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        VRCGraphics.Blit(gpuColumnBaseTexture, gpuColumnFinalTexture, gpuColumnSurfaceReplaceMaterial);
#if LOGGING
        if (enableDetailedTimings)
        {
            agg_gpuFinalizeBlits++;
            agg_gpuFinalizeBlitTime += (Time.realtimeSinceStartup - blitStart) * 1000f;
        }
        blitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        // GPU cave carve pass
        if (generateCaves && gpuCaveCarveMaterial != null)
        {
            _EnsureGpuCaveHashesReady();
#if LOGGING
            float caveBlitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
            long caveSeedLong = McUtils.GetMinecraftSeed(world.worldSeedString);
            gpuCaveCarveMaterial.SetInt(gpuPropCaveChunkXId, currentChunkX);
            gpuCaveCarveMaterial.SetInt(gpuPropCaveChunkZId, currentChunkZ);
            gpuCaveCarveMaterial.SetInt(gpuPropCaveWorldSeedHiId, (int)(caveSeedLong >> 32));
            // UDON-CHECKED-CAST: `(int)(long & 0xFFFFFFFFL)` overflows the Udon runtime's
            // checked Int32 conversion when the value exceeds int.MaxValue. XOR-then-subtract
            // maps unsigned 32-bit [0, 2^32) into signed int [-2^31, 2^31) without overflow.
            long _caveSeedLo = caveSeedLong & 0xFFFFFFFFL;
            gpuCaveCarveMaterial.SetInt(gpuPropCaveWorldSeedLoId, (int)((_caveSeedLo ^ 0x80000000L) - 0x80000000L));
            gpuCaveCarveMaterial.SetInt(gpuPropCaveHashAHiId, gpuCaveHashAHi);
            gpuCaveCarveMaterial.SetInt(gpuPropCaveHashALoId, gpuCaveHashALo);
            gpuCaveCarveMaterial.SetInt(gpuPropCaveHashBHiId, gpuCaveHashBHi);
            gpuCaveCarveMaterial.SetInt(gpuPropCaveHashBLoId, gpuCaveHashBLo);
            gpuCaveCarveMaterial.SetInt(gpuPropWorldHeightId, gpuWorldHeightBlocks);
            gpuCaveCarveMaterial.SetInt(gpuPropChunkSizeXZId, sizeXZ);
            gpuCaveCarveMaterial.SetInt(gpuPropFlipXAxisId, match1to1TerrainBaseline ? 0 : (flipXAxis ? 1 : 0));
            gpuCaveCarveMaterial.SetInt(gpuPropStoneBlockId, stoneBlockID);
            gpuCaveCarveMaterial.SetInt(VRCShader.PropertyToID("_DirtBlockId"), dirtBlockID);
            gpuCaveCarveMaterial.SetInt(VRCShader.PropertyToID("_GrassBlockId"), grassBlockID);
            gpuCaveCarveMaterial.SetInt(gpuPropWaterBlockId, waterBlockID);
            gpuCaveCarveMaterial.SetInt(VRCShader.PropertyToID("_StationaryWaterBlockId"), (int)BlockMaterial.STATIONARY_WATER);
            gpuCaveCarveMaterial.SetInt(VRCShader.PropertyToID("_LavaBlockId"), (int)BlockMaterial.STATIONARY_LAVA);
            gpuCaveCarveMaterial.SetInt(gpuPropCaveGenerateId, 1);
            VRCGraphics.Blit(gpuColumnFinalTexture, gpuCaveWorkTexture, gpuCaveCarveMaterial);
            VRCGraphics.Blit(gpuCaveWorkTexture, gpuColumnFinalTexture);
#if LOGGING
            if (enableDetailedTimings)
            {
                agg_gpuCaveBlits++;
                agg_gpuCaveBlitTime += (Time.realtimeSinceStartup - caveBlitStart) * 1000f;
            }
#endif
        }
        gpuColumnSurfaceInfoMaterial.SetTexture(gpuPropBaseColumnTexId, gpuColumnFinalTexture);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropWorldHeightId, gpuWorldHeightBlocks);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropChunkSizeXZId, sizeXZ);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropStoneBlockId, stoneBlockID);
        VRCGraphics.Blit(gpuColumnFinalTexture, gpuColumnSurfaceInfoTexture, gpuColumnSurfaceInfoMaterial);
#if LOGGING
        if (enableDetailedTimings)
        {
            agg_gpuSurfaceInfoBlits++;
            agg_gpuSurfaceInfoBlitTime += (Time.realtimeSinceStartup - blitStart) * 1000f;
            agg_gpuColumnsFinalized++;
        }
#endif

        // FINALIZE SLICING: decoration + readback moved to the Prepare_GpuDecorate state —
        // this method now ends after the surface-info blit, so the finalize step is just
        // params + a handful of blits.
        return true;
    }

    // FINALIZE SLICING step 1 of Prepare_GpuDecorate: run the CPU candidate collection
    // (JavaRandom decoration streams of the 4 contributing chunks) and, when trees are
    // present, snapshot this column's final content into anchor slot 4 BEFORE any anchor
    // render — anchor renders reuse/overwrite gpuColumnBase/SurfaceInfo/FinalTexture.
    private void _GpuDecorationCollect(int sizeXZ)
    {
        gpuDecorTreeCount = 0;
        gpuDecorCandidateCount = 0;
        gpuDecorAnchorMask = 0;
        gpuDecorCurrentTex = gpuColumnFinalTexture;
        if (match1to1TerrainBaseline || gpuColumnDecorationMaterial == null || currentChunkBiomes == null) return;

        int worldHeight = gpuWorldHeightBlocks;
        int colOriginX = currentChunkX * sizeXZ;
        int colOriginZ = currentChunkZ * sizeXZ;

        int candidateCount = 0;
        int maxCandidates = GPU_DECORATION_TEX_WIDTH * GPU_DECORATION_TEX_HEIGHT;
        int treeCount = 0;
        int maxTrees = GPU_TREE_TEX_WIDTH;
        int requiredTreeAnchorMask = 1 << 4;

        // Collect candidates from 4 contributing chunks.
        // With BUILTIN_OFFSET_X=-16, trees from chunk cx land at world X = (cx-1)*16+8..23
        //   cx=currentChunkX   -> localX -8..+7,  cx=currentChunkX+1 -> localX +8..+23
        // With BUILTIN_OFFSET_Z=0,  trees from chunk cz land at world Z = cz*16+8..23
        //   cz=currentChunkZ-1 -> localZ -8..+7,  cz=currentChunkZ   -> localZ +8..+23
        int[] neighborOffsetX = new int[] { 0, 1, 0, 1 };
        int[] neighborOffsetZ = new int[] { -1, -1, 0, 0 };

        for (int ni = 0; ni < 4; ni++)
        {
            int ncx = currentChunkX + neighborOffsetX[ni];
            int ncz = currentChunkZ + neighborOffsetZ[ni];

            _GpuDecorationCollectFromChunk(ncx, ncz, colOriginX, colOriginZ,
                sizeXZ, worldHeight,
                ref treeCount, maxTrees,
                ref candidateCount, maxCandidates,
                ref requiredTreeAnchorMask);
        }

        gpuDecorTreeCount = treeCount;
        gpuDecorCandidateCount = candidateCount;
        if (treeCount > 0 && gpuColumnTreeDecorationMaterial != null && gpuTreeAnchorChunkTextures != null)
        {
            VRCGraphics.Blit(gpuColumnFinalTexture, gpuTreeAnchorChunkTextures[4]);
            gpuDecorCurrentTex = gpuTreeAnchorChunkTextures[4];
            gpuDecorAnchorMask = requiredTreeAnchorMask & ~(1 << 4);

            // TREE-ANCHOR CACHE: reset this column's slot->pool bindings. Pool arrays are
            // allocated lazily; entries persist across columns (see _GpuDecorationRenderNextAnchor).
            if (gpuTreeAnchorPoolCx == null)
            {
                gpuTreeAnchorPoolCx = new int[9];
                gpuTreeAnchorPoolCz = new int[9];
                gpuDecorSlotPool = new int[9];
                for (int pi = 0; pi < 9; pi++) { gpuTreeAnchorPoolCx[pi] = int.MaxValue; gpuTreeAnchorPoolCz[pi] = int.MaxValue; }
            }
            for (int si = 0; si < 9; si++) gpuDecorSlotPool[si] = -1;
            gpuDecorSlotPool[4] = 4; // own-column snapshot always lives in texture 4
        }
    }

    // FINALIZE SLICING step 2 of Prepare_GpuDecorate, with the TREE-ANCHOR CACHE: anchor
    // renders are keyed by WORLD column coords — the content is purely deterministic
    // (seed + coords; no caves, no decoration), so a cached texture never goes stale, and
    // adjacent columns need the same anchor up to 4x. Cache HITS just bind the pooled
    // texture (free — they don't consume a step); only a true MISS renders the full mini
    // column pipeline (~20-50ms), ONE per call. Returns true while pending anchors remain.
    private bool _GpuDecorationRenderNextAnchor(int sizeXZ)
    {
        if (gpuDecorAnchorMask == 0) return false;
        if (gpuTreeAnchorChunkTextures == null || gpuTreeAnchorBiomeBuffer == null || gpuDecorSlotPool == null)
        {
            gpuDecorAnchorMask = 0;
            return false;
        }

        // PHASE 1 (free, every call): resolve ALL pending cache hits before ANY victim is
        // chosen. Otherwise a miss on a low slot could round-robin-evict the exact pooled
        // entry a higher pending slot of THIS column would have hit — turning a free hit into
        // a redundant ~20-50ms render. Binding all hits first makes _GpuAnchorPoolIndexBound
        // protect every entry this column still needs.
        for (int treeChunkSlot = 0; treeChunkSlot < 9; treeChunkSlot++)
        {
            if (treeChunkSlot == 4) continue;
            if ((gpuDecorAnchorMask & (1 << treeChunkSlot)) == 0) continue;

            int hcx = currentChunkX + ((treeChunkSlot % 3) - 1);
            int hcz = currentChunkZ + ((treeChunkSlot / 3) - 1);
            for (int p = 0; p < 9; p++)
            {
                if (p == 4) continue;
                if (gpuTreeAnchorPoolCx[p] != hcx || gpuTreeAnchorPoolCz[p] != hcz) continue;
                if (gpuTreeAnchorChunkTextures[p] == null || !gpuTreeAnchorChunkTextures[p].IsCreated())
                {
                    // Content lost (device reset/alt-tab) — invalidate; re-render below.
                    gpuTreeAnchorPoolCx[p] = int.MaxValue;
                    gpuTreeAnchorPoolCz[p] = int.MaxValue;
                    continue;
                }
                gpuDecorSlotPool[treeChunkSlot] = p;
                gpuDecorAnchorMask &= ~(1 << treeChunkSlot);
#if LOGGING
                if (enableDetailedTimings) agg_anchorCacheHits++;
#endif
                break;
            }
        }

        // PHASE 2: render at most ONE remaining (miss) anchor per call.
        for (int treeChunkSlot = 0; treeChunkSlot < 9; treeChunkSlot++)
        {
            if (treeChunkSlot == 4) continue;
            if ((gpuDecorAnchorMask & (1 << treeChunkSlot)) == 0) continue;

            int chunkOffsetX = (treeChunkSlot % 3) - 1;
            int chunkOffsetZ = (treeChunkSlot / 3) - 1;
            int chunkX = currentChunkX + chunkOffsetX;
            int chunkZ = currentChunkZ + chunkOffsetZ;

            // MISS: pick a victim pool texture — an invalid entry if any, else round-robin
            // among entries NOT bound to this column's other slots (and never 4).
            int victim = -1;
            for (int p = 0; p < 9; p++)
            {
                if (p == 4) continue;
                if (gpuTreeAnchorPoolCx[p] == int.MaxValue && !_GpuAnchorPoolIndexBound(p)) { victim = p; break; }
            }
            if (victim < 0)
            {
                for (int tries = 0; tries < 9; tries++)
                {
                    gpuTreeAnchorPoolClock = gpuTreeAnchorPoolClock + 1;
                    if (gpuTreeAnchorPoolClock >= 9) gpuTreeAnchorPoolClock = 0;
                    int p = gpuTreeAnchorPoolClock;
                    if (p == 4 || _GpuAnchorPoolIndexBound(p)) continue;
                    victim = p;
                    break;
                }
            }
            if (victim < 0) victim = treeChunkSlot; // unreachable (>=4 free entries); stay safe

            gpuDecorAnchorMask &= ~(1 << treeChunkSlot);
            gpuDecorSlotPool[treeChunkSlot] = victim;
#if LOGGING
            if (enableDetailedTimings) agg_anchorCacheMisses++;
#endif
            if (!_FillGpuTreeAnchorBiomes(chunkX, chunkZ))
            {
                VRCGraphics.Blit(gpuClearTexture, gpuTreeAnchorChunkTextures[victim]);
                gpuTreeAnchorPoolCx[victim] = int.MaxValue;
                gpuTreeAnchorPoolCz[victim] = int.MaxValue;
            }
            else if (!_RenderGpuTreeAnchorChunk(chunkX, chunkZ, gpuTreeAnchorBiomeBuffer, gpuTreeAnchorChunkTextures[victim], sizeXZ))
            {
                VRCGraphics.Blit(gpuClearTexture, gpuTreeAnchorChunkTextures[victim]);
                gpuTreeAnchorPoolCx[victim] = int.MaxValue;
                gpuTreeAnchorPoolCz[victim] = int.MaxValue;
            }
            else
            {
                gpuTreeAnchorPoolCx[victim] = chunkX;
                gpuTreeAnchorPoolCz[victim] = chunkZ;
            }
            break; // one RENDER per step
        }
        return gpuDecorAnchorMask != 0;
    }

    // TREE-ANCHOR CACHE: is this pool index already bound to one of the current column's
    // decoration slots? (Bound entries must not be evicted mid-column.)
    private bool _GpuAnchorPoolIndexBound(int poolIndex)
    {
        for (int s = 0; s < 9; s++)
        {
            if (gpuDecorSlotPool[s] == poolIndex) return true;
        }
        return false;
    }

    // FINALIZE SLICING step 3 of Prepare_GpuDecorate: the tree/flower decoration blits.
    // Returns the texture the base-column readback should read. Mirrors the tail of the old
    // _BlitGpuDecoration exactly (same guards, same blit order).
    private RenderTexture _GpuDecorationFinishBlits(int sizeXZ)
    {
        int worldHeight = gpuWorldHeightBlocks;
        RenderTexture currentTex = gpuDecorCurrentTex != null ? gpuDecorCurrentTex : gpuColumnFinalTexture;

        // Pass 1: Tree decoration
        if (gpuDecorTreeCount > 0 && gpuColumnTreeDecorationMaterial != null)
        {
            int treeCount = gpuDecorTreeCount;
            gpuTreeInfoTexture.SetPixels(gpuTreeInfoPixels);
            gpuTreeInfoTexture.Apply(false, false);

            gpuColumnTreeDecorationMaterial.SetTexture(gpuPropBaseColumnTexId, currentTex);
            gpuColumnTreeDecorationMaterial.SetTexture(gpuPropTreeInfoTexId, gpuTreeInfoTexture);
            gpuColumnTreeDecorationMaterial.SetInt(gpuPropTreeCountId, treeCount);
            gpuColumnTreeDecorationMaterial.SetInt(gpuPropWorldHeightId, worldHeight);
            gpuColumnTreeDecorationMaterial.SetInt(gpuPropChunkSizeXZId, sizeXZ);
            gpuColumnTreeDecorationMaterial.SetInt(gpuPropAirBlockIdDecor, airBlockID);
            gpuColumnTreeDecorationMaterial.SetInt(gpuPropGrassBlockIdDecor, grassBlockID);
            gpuColumnTreeDecorationMaterial.SetInt(gpuPropDirtBlockIdDecor, dirtBlockID);
            gpuColumnTreeDecorationMaterial.SetInt(gpuPropLogBlockIdDecor, logBlockID);
            gpuColumnTreeDecorationMaterial.SetInt(gpuPropLeavesBlockIdDecor, leavesBlockID);
            for (int treeChunkSlot = 0; treeChunkSlot < gpuTreeAnchorChunkTextures.Length; treeChunkSlot++)
            {
                // TREE-ANCHOR CACHE: bind the pooled texture this slot resolved to (cache hit
                // or freshly rendered victim). Unbound slots (-1) get their own-index texture,
                // matching the old behavior for slots the shader never samples.
                int poolIdx = (gpuDecorSlotPool != null && gpuDecorSlotPool[treeChunkSlot] >= 0)
                    ? gpuDecorSlotPool[treeChunkSlot] : treeChunkSlot;
                gpuColumnTreeDecorationMaterial.SetTexture(gpuTreeAnchorChunkTexIds[treeChunkSlot], gpuTreeAnchorChunkTextures[poolIdx]);
            }

            VRCGraphics.Blit(currentTex, gpuTreeWorkTexture, gpuColumnTreeDecorationMaterial);
            currentTex = gpuTreeWorkTexture;
        }

        // Pass 2: Flower/grass decoration
        if (gpuDecorCandidateCount > 0)
        {
            gpuDecorationCandidateTexture.SetPixels(gpuDecorationCandidatePixels);
            gpuDecorationCandidateTexture.Apply(false, false);

            gpuColumnDecorationMaterial.SetTexture(gpuPropBaseColumnTexId, currentTex);
            gpuColumnDecorationMaterial.SetTexture(gpuPropCandidateTexId, gpuDecorationCandidateTexture);
            gpuColumnDecorationMaterial.SetInt(gpuPropCandidateCountId, gpuDecorCandidateCount);
            gpuColumnDecorationMaterial.SetInt(gpuPropCandidateTexWidthId, GPU_DECORATION_TEX_WIDTH);
            gpuColumnDecorationMaterial.SetInt(gpuPropCandidateTexHeightId, GPU_DECORATION_TEX_HEIGHT);
            gpuColumnDecorationMaterial.SetInt(gpuPropWorldHeightId, worldHeight);
            gpuColumnDecorationMaterial.SetInt(gpuPropChunkSizeXZId, sizeXZ);
            gpuColumnDecorationMaterial.SetInt(gpuPropAirBlockIdDecor, airBlockID);
            gpuColumnDecorationMaterial.SetInt(gpuPropGrassBlockIdDecor, grassBlockID);
            gpuColumnDecorationMaterial.SetInt(gpuPropDirtBlockIdDecor, dirtBlockID);
            gpuColumnDecorationMaterial.SetInt(gpuPropLeavesBlockIdDecor, leavesBlockID);

            VRCGraphics.Blit(currentTex, gpuDecorationWorkTexture, gpuColumnDecorationMaterial);
            currentTex = gpuDecorationWorkTexture;
        }

        return currentTex;
    }

    private int _GetTreeAnchorChunkSlot(int localX, int localZ, int sizeXZ)
    {
        int chunkOffsetX = localX < 0 ? -1 : (localX >= sizeXZ ? 1 : 0);
        int chunkOffsetZ = localZ < 0 ? -1 : (localZ >= sizeXZ ? 1 : 0);
        return (chunkOffsetZ + 1) * 3 + (chunkOffsetX + 1);
    }

    // (The old _PrepareGpuTreeAnchorChunks — all pending anchors in one atomic call — was
    // replaced by the per-step _GpuDecorationRenderNextAnchor above.)

    private bool _FillGpuTreeAnchorBiomes(int chunkX, int chunkZ)
    {
        if (gpuTreeAnchorBiomeBuffer == null) return false;

        if (chunkX == currentChunkX && chunkZ == currentChunkZ && currentChunkBiomes != null &&
            currentChunkBiomes.Length == gpuTreeAnchorBiomeBuffer.Length)
        {
            System.Array.Copy(currentChunkBiomes, gpuTreeAnchorBiomeBuffer, gpuTreeAnchorBiomeBuffer.Length);
            return true;
        }

        int blockX = _GetTerrainBlockStartX(chunkX);
        int blockZ = _GetTerrainBlockStartZ(chunkZ);
        if (biomeQueryWcm == null)
        {
            biomeQueryWcm = new WorldChunkManagerOld(generatorSeed);
        }

        BetaBiomeEnum[] result = biomeQueryWcm.getBiomeBlock(null, blockX, blockZ, world.chunkSizeXZ, world.chunkSizeXZ);
        if (result == null || result.Length < gpuTreeAnchorBiomeBuffer.Length) return false;

        System.Array.Copy(result, gpuTreeAnchorBiomeBuffer, gpuTreeAnchorBiomeBuffer.Length);
        return true;
    }

    private bool _RenderGpuTreeAnchorChunk(int chunkX, int chunkZ, BetaBiomeEnum[] chunkBiomes, RenderTexture targetTexture, int sizeXZ)
    {
        if (!gpuWorldgenReady || targetTexture == null || chunkBiomes == null || chunkBiomes.Length != sizeXZ * sizeXZ) return false;

        int savedChunkX = currentChunkX;
        int savedChunkZ = currentChunkZ;
        BetaBiomeEnum[] savedChunkBiomes = currentChunkBiomes;

        currentChunkX = chunkX;
        currentChunkZ = chunkZ;
        currentChunkBiomes = chunkBiomes;

        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimatePermTex0Id, gpuClimatePermTextureTemp);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimatePermTex1Id, gpuClimatePermTextureRain);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimatePermTex2Id, gpuClimatePermTextureModifier);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimateOffsetTex0Id, gpuClimateOffsetTextureTemp);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimateOffsetTex1Id, gpuClimateOffsetTextureRain);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimateOffsetTex2Id, gpuClimateOffsetTextureModifier);
        gpuNoiseOctaveMaterial.SetTexture(gpuPropClimateBiomeLookupTexId, gpuClimateBiomeLookupTexture);
        gpuNoiseOctaveMaterial.SetInt(gpuPropClimateOctaveCount0Id, biomeTempNoiseGen.GetOctaveCount());
        gpuNoiseOctaveMaterial.SetInt(gpuPropClimateOctaveCount1Id, biomeRainNoiseGen.GetOctaveCount());
        gpuNoiseOctaveMaterial.SetInt(gpuPropClimateOctaveCount2Id, biomeModifierNoiseGen.GetOctaveCount());
        gpuNoiseOctaveMaterial.SetInt(gpuPropChunkXId, _GetTerrainBlockStartX(currentChunkX));
        gpuNoiseOctaveMaterial.SetInt(gpuPropChunkZId, _GetTerrainBlockStartZ(currentChunkZ));
        gpuNoiseOctaveMaterial.SetInt(gpuPropXSizeId, sizeXZ);
        gpuNoiseOctaveMaterial.SetInt(gpuPropZSizeId, sizeXZ);
        VRCGraphics.Blit(gpuClearTexture, gpuClimateTexture, gpuNoiseOctaveMaterial, 2);

        int densityXSize = 5;
        int densityYSize = gpuWorldHeightBlocks / 8 + 1;
        int densityZSize = 5;
        int densityXPos = _GetTerrainNoiseStartX(currentChunkX, 4);
        int densityZPos = _GetTerrainNoiseStartZ(currentChunkZ, 4);
        double d0 = 684.412D;
        double d1 = 684.412D;

        _WriteNoiseCoordBlock(noiseGen1, 16, densityXSize, densityYSize, densityZSize, densityXPos, 0, densityZPos, d0, d1, d0, false, 0);
        _WriteNoiseCoordBlock(noiseGen2, 16, densityXSize, densityYSize, densityZSize, densityXPos, 0, densityZPos, d0, d1, d0, false, 1);
        _WriteNoiseCoordBlock(noiseGen3, 8, densityXSize, densityYSize, densityZSize, densityXPos, 0, densityZPos, d0 / 80.0D, d1 / 160.0D, d0 / 80.0D, false, 2);
        _WriteNoiseCoordBlock(noiseGen6, 10, densityXSize, 1, densityZSize, densityXPos, 10, densityZPos, 1.121D, 1.0D, 1.121D, true, 3);
        _WriteNoiseCoordBlock(noiseGen7, 16, densityXSize, 1, densityZSize, densityXPos, 10, densityZPos, 200.0D, 1.0D, 200.0D, true, 4);
        _FlushBatchedNoiseCoords();

        _RunGpuNoiseOctaves(noiseGen1, 16, gpuNoise1Texture, densityXPos, 0, densityZPos, densityXSize, densityYSize, densityZSize, d0, d1, d0, false, 0 * GPU_MAX_OCTAVES);
        _RunGpuNoiseOctaves(noiseGen2, 16, gpuNoise2Texture, densityXPos, 0, densityZPos, densityXSize, densityYSize, densityZSize, d0, d1, d0, false, 1 * GPU_MAX_OCTAVES);
        _RunGpuNoiseOctaves(noiseGen3, 8, gpuNoise3Texture, densityXPos, 0, densityZPos, densityXSize, densityYSize, densityZSize, d0 / 80.0D, d1 / 160.0D, d0 / 80.0D, false, 2 * GPU_MAX_OCTAVES);
        _RunGpuNoiseOctaves(noiseGen6, 10, gpuNoise6Texture, densityXPos, 10, densityZPos, densityXSize, 1, densityZSize, 1.121D, 1.0D, 1.121D, true, 3 * GPU_MAX_OCTAVES);
        _RunGpuNoiseOctaves(noiseGen7, 16, gpuNoise7Texture, densityXPos, 10, densityZPos, densityXSize, 1, densityZSize, 200.0D, 1.0D, 200.0D, true, 4 * GPU_MAX_OCTAVES);

        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise1TexId, gpuNoise1Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise2TexId, gpuNoise2Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise3TexId, gpuNoise3Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise6TexId, gpuNoise6Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropNoise7TexId, gpuNoise7Texture);
        gpuNoiseCombineMaterial.SetTexture(gpuPropTemperatureTexId, gpuClimateTexture);
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
        gpuColumnBaseFillMaterial.SetTexture(gpuPropTemperatureTexId, gpuClimateTexture);
        gpuColumnBaseFillMaterial.SetInt(gpuPropWorldHeightId, gpuWorldHeightBlocks);
        gpuColumnBaseFillMaterial.SetInt(gpuPropChunkSizeXZId, sizeXZ);
        gpuColumnBaseFillMaterial.SetInt(gpuPropOceanHeightId, 64);
        gpuColumnBaseFillMaterial.SetInt(gpuPropFlipXAxisId, match1to1TerrainBaseline ? 0 : (flipXAxis ? 1 : 0));
        gpuColumnBaseFillMaterial.SetInt(gpuPropStoneBlockId, stoneBlockID);
        gpuColumnBaseFillMaterial.SetInt(gpuPropWaterBlockId, waterBlockID);
        gpuColumnBaseFillMaterial.SetInt(gpuPropIceBlockId, (int)BlockMaterial.ICE);
        VRCGraphics.Blit(gpuDensityTexture, gpuColumnBaseTexture, gpuColumnBaseFillMaterial);

        gpuColumnSurfaceInfoMaterial.SetTexture(gpuPropBaseColumnTexId, gpuColumnBaseTexture);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropWorldHeightId, gpuWorldHeightBlocks);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropChunkSizeXZId, sizeXZ);
        gpuColumnSurfaceInfoMaterial.SetInt(gpuPropStoneBlockId, stoneBlockID);
        VRCGraphics.Blit(gpuColumnBaseTexture, gpuColumnSurfaceInfoTexture, gpuColumnSurfaceInfoMaterial);

        int noiseX = _GetTerrainBlockStartX(currentChunkX);
        int noiseZ = _GetTerrainBlockStartZ(currentChunkZ);
        double[] cpuSandN = noiseGen4.generateNoiseOctaves(null, noiseX, noiseZ, 0.0D, sizeXZ, sizeXZ, 1, 0.03125D, 0.03125D, 1.0D);
        _UploadCpuSurfaceNoise(cpuSandN, sizeXZ, gpuSandNoiseTexture);
        double[] cpuGravelN = noiseGen4.generateNoiseOctaves(null, noiseX, 109.0134D, noiseZ, sizeXZ, 1, sizeXZ, 0.03125D, 1.0D, 0.03125D);
        _UploadCpuSurfaceNoise(cpuGravelN, sizeXZ, gpuGravelNoiseTexture);
        double[] cpuStoneN = noiseGen5.generateNoiseOctaves(null, noiseX, noiseZ, 0.0D, sizeXZ, sizeXZ, 1, 0.0625D, 0.0625D, 0.0625D);
        _UploadCpuSurfaceNoise(cpuStoneN, sizeXZ, gpuStoneNoiseTexture);

        _BuildGpuSurfaceParamsFromSurfaceInfo();

        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropBaseColumnTexId, gpuColumnBaseTexture);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropSurfaceInfoTexId, gpuColumnSurfaceInfoTexture);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropSurfaceParamsTexAId, gpuSurfaceParamsTextureA);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropSurfaceParamsTexBId, gpuSurfaceParamsTextureB);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropBedrockMaskTexId, gpuBedrockMaskTexture);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropSandNoiseTexId, gpuSandNoiseTexture);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropGravelNoiseTexId, gpuGravelNoiseTexture);
        gpuColumnSurfaceReplaceMaterial.SetTexture(gpuPropStoneNoiseTexId, gpuStoneNoiseTexture);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropWorldHeightId, gpuWorldHeightBlocks);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropChunkSizeXZId, sizeXZ);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropStoneBlockId, stoneBlockID);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropBedrockBlockId, bedrockBlockID);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropSandBlockId, sandBlockID);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropGravelBlockId, (int)BlockMaterial.GRAVEL);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropWaterBlockId, waterBlockID);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropSandstoneBlockId, sandStoneBlockID);
        gpuColumnSurfaceReplaceMaterial.SetInt(gpuPropFlipXAxisId, match1to1TerrainBaseline ? 0 : (flipXAxis ? 1 : 0));
        VRCGraphics.Blit(gpuColumnBaseTexture, gpuColumnFinalTexture, gpuColumnSurfaceReplaceMaterial);

        VRCGraphics.Blit(gpuColumnFinalTexture, targetTexture);

        currentChunkX = savedChunkX;
        currentChunkZ = savedChunkZ;
        currentChunkBiomes = savedChunkBiomes;
        return true;
    }

    // Collect tree + flower/grass candidates from one chunk into the current column
    private void _GpuDecorationCollectFromChunk(int chunkX, int chunkZ,
        int colOriginX, int colOriginZ, int sizeXZ, int worldHeight,
        ref int treeCount, int maxTrees,
        ref int candidateCount, int maxCandidates,
        ref int requiredTreeAnchorMask)
    {
        long worldSeed = McUtils.GetMinecraftSeed(world.worldSeedString);
        long decorSeed = (long)chunkX * 1364927L + (long)chunkZ * 7420851L ^ worldSeed;
        JavaRandom dRand = new JavaRandom(decorSeed);

        BetaBiomeEnum centerBiome;
        if (chunkX == currentChunkX && chunkZ == currentChunkZ && currentChunkBiomes != null)
        {
            centerBiome = currentChunkBiomes[8 * 16 + 8];
        }
        else
        {
            centerBiome = _QueryBiomeAtChunkCenter(chunkX, chunkZ);
        }

        // Trees: compute count, then generate tree info
        int numTrees = BetaBiome.getTreesPerChunk(dRand, treeNoise, chunkX, chunkZ, centerBiome);
        for (int ti = 0; ti < numTrees && treeCount < maxTrees; ti++)
        {
            int treeX = chunkX * 16 + dRand.NextInt(16) + 8 + BUILTIN_OFFSET_X;
            int treeZ = chunkZ * 16 + dRand.NextInt(16) + 8 + BUILTIN_OFFSET_Z;

            int treeHeight = dRand.NextInt(3) + 4;

            // Pre-compute leaf corner skip decisions (always consume PRNG, even if tree fails validation)
            int cornerBits = 0;
            int cornerIndex = 0;
            for (int leafY = -3 + treeHeight; leafY <= treeHeight; leafY++)
            {
                int yOff = leafY - treeHeight;
                int leafR = 1 - yOff / 2;
                for (int lx = -leafR; lx <= leafR; lx++)
                {
                    for (int lz = -leafR; lz <= leafR; lz++)
                    {
                        if (System.Math.Abs(lx) == leafR && System.Math.Abs(lz) == leafR)
                        {
                            if (dRand.NextInt(2) == 0)
                            {
                                cornerBits |= (1 << (cornerIndex & 7));
                            }
                            cornerIndex++;
                        }
                    }
                }
            }

            // Accept trees within ±2 of column (max leaf radius) so cross-boundary leaves work
            int localX = treeX - colOriginX;
            int localZ = treeZ - colOriginZ;
            if (localX >= -2 && localX < sizeXZ + 2 && localZ >= -2 && localZ < sizeXZ + 2)
            {
                requiredTreeAnchorMask |= 1 << _GetTreeAnchorChunkSlot(localX, localZ, sizeXZ);
                gpuTreeInfoPixels[treeCount] = new Color(
                    (localX + 128) / 255f,
                    (localZ + 128) / 255f,
                    treeHeight / 255f,
                    cornerBits / 255f
                );
                treeCount++;
            }
        }

        // Yellow flowers
        int yellowFlowerCount = BetaBiome.getFlowersPerChunk(centerBiome);
        for (int fi = 0; fi < yellowFlowerCount; fi++)
        {
            _GpuDecorationAddFlowerCandidates(dRand, chunkX, chunkZ, colOriginX, colOriginZ,
                sizeXZ, worldHeight, flowerYellowBlockID, ref candidateCount, maxCandidates);
        }

        // Tall grass
        int grassCount = BetaBiome.getGrassPerChunk(centerBiome);
        for (int gi = 0; gi < grassCount; gi++)
        {
            _GpuDecorationAddGrassCandidates(dRand, chunkX, chunkZ, colOriginX, colOriginZ,
                sizeXZ, worldHeight, tallGrassBlockID, ref candidateCount, maxCandidates);
        }

        // Red flower (50% chance)
        if (dRand.NextInt(2) == 0)
        {
            _GpuDecorationAddFlowerCandidates(dRand, chunkX, chunkZ, colOriginX, colOriginZ,
                sizeXZ, worldHeight, flowerRedBlockID, ref candidateCount, maxCandidates);
        }
    }

    private BetaBiomeEnum _QueryBiomeAtChunkCenter(int chunkX, int chunkZ)
    {
        int blockX = _GetTerrainBlockStartX(chunkX) + 8;
        int blockZ = _GetTerrainBlockStartZ(chunkZ) + 8;
        if (biomeQueryWcm == null)
        {
            biomeQueryWcm = new WorldChunkManagerOld(generatorSeed);
        }
        BetaBiomeEnum[] result = biomeQueryWcm.getBiomeBlock(null, blockX, blockZ, 1, 1);
        if (result != null && result.Length > 0) return result[0];
        return BetaBiomeEnum.PLAINS;
    }

    // Flower: 64 scatter attempts from a source chunk, filtered to current column
    private void _GpuDecorationAddFlowerCandidates(JavaRandom dRand, int srcChunkX, int srcChunkZ,
        int colOriginX, int colOriginZ,
        int sizeXZ, int worldHeight, byte blockType, ref int candidateCount, int maxCandidates)
    {
        int baseX = srcChunkX * 16 + dRand.NextInt(16) + 8 + BUILTIN_OFFSET_X;
        int baseY = dRand.NextInt(worldHeight);
        int baseZ = srcChunkZ * 16 + dRand.NextInt(16) + 8 + BUILTIN_OFFSET_Z;

        for (int attempt = 0; attempt < 64; attempt++)
        {
            int fx = baseX + dRand.NextInt(8) - dRand.NextInt(8);
            int fy = baseY + dRand.NextInt(4) - dRand.NextInt(4);
            int fz = baseZ + dRand.NextInt(8) - dRand.NextInt(8);

            int localX = fx - colOriginX;
            int localZ = fz - colOriginZ;
            if (localX >= 0 && localX < sizeXZ && localZ >= 0 && localZ < sizeXZ
                && fy >= 0 && fy < worldHeight && candidateCount < maxCandidates)
            {
                gpuDecorationCandidatePixels[candidateCount] = new Color(
                    ((localX << 4) | localZ) / 255f, fy / 255f, blockType / 255f, 1f / 255f
                );
                candidateCount++;
            }
        }
    }

    // Tall grass: 128 scatter attempts from a source chunk, filtered to current column
    private void _GpuDecorationAddGrassCandidates(JavaRandom dRand, int srcChunkX, int srcChunkZ,
        int colOriginX, int colOriginZ,
        int sizeXZ, int worldHeight, byte blockType, ref int candidateCount, int maxCandidates)
    {
        int baseX = srcChunkX * 16 + dRand.NextInt(16) + 8 + BUILTIN_OFFSET_X;
        dRand.NextInt(worldHeight); // consume baseY from PRNG (GPU finds surface instead)
        int baseZ = srcChunkZ * 16 + dRand.NextInt(16) + 8 + BUILTIN_OFFSET_Z;

        for (int attempt = 0; attempt < 128; attempt++)
        {
            int gx = baseX + dRand.NextInt(8) - dRand.NextInt(8);
            int dY = dRand.NextInt(4) - dRand.NextInt(4);
            int gz = baseZ + dRand.NextInt(8) - dRand.NextInt(8);

            int localX = gx - colOriginX;
            int localZ = gz - colOriginZ;
            if (localX >= 0 && localX < sizeXZ && localZ >= 0 && localZ < sizeXZ
                && candidateCount < maxCandidates)
            {
                gpuDecorationCandidatePixels[candidateCount] = new Color(
                    ((localX << 4) | localZ) / 255f, (dY + 128) / 255f, blockType / 255f, 0f
                );
                candidateCount++;
            }
        }
    }

    private void _BuildGpuChunkSliceCache()
    {
        if ((gpuColumnReadbackPixels == null && gpuColumnReadbackBlocks == null) || gpuCachedChunkSlices == null || world == null) return;

#if LOGGING
        float copyStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        int sizeXZ = world.chunkSizeXZ;
        int sizeY = world.chunkSizeY;
        int chunkStride = sizeXZ * sizeXZ;
        int chunkSize = sizeXZ * sizeY * sizeXZ;
        int worldChunkCountY = world.worldDimensionY;
        byte[] sourceBlocks = gpuColumnReadbackBlocks;

        for (int chunkY = 0; chunkY < worldChunkCountY; chunkY++)
        {
            byte[] slice = gpuCachedChunkSlices[chunkY];
            if (slice == null || slice.Length != chunkSize)
            {
                slice = new byte[chunkSize];
                gpuCachedChunkSlices[chunkY] = slice;
            }
        }

        if (sourceBlocks != null && sourceBlocks.Length >= worldChunkCountY * chunkSize)
        {
            for (int chunkY = 0; chunkY < worldChunkCountY; chunkY++)
            {
                System.Array.Copy(sourceBlocks, chunkY * chunkSize, gpuCachedChunkSlices[chunkY], 0, chunkSize);
            }
        }
        else
        {
            int worldHeightBlocks = worldChunkCountY * sizeY;
            Color32[] sourcePixels = gpuColumnReadbackPixels;
            for (int globalY = 0; globalY < worldHeightBlocks; globalY++)
            {
                int chunkY = globalY / sizeY;
                int localY = globalY - chunkY * sizeY;
                byte[] slice = gpuCachedChunkSlices[chunkY];
                int sourceIndex = globalY * chunkStride;
                int targetIndex = localY * chunkStride;

                for (int i = 0; i < chunkStride; i++)
                {
                    slice[targetIndex + i] = sourcePixels[sourceIndex + i].r;
                }
            }
        }

#if LOGGING
        if (enableDetailedTimings)
        {
            agg_gpuChunkSliceCopies++;
            agg_gpuChunkSliceCopyTime += (Time.realtimeSinceStartup - copyStart) * 1000f;
            agg_gpuChunkSliceCopyBytes += sourceBlocks != null ? sourceBlocks.Length : gpuColumnReadbackPixels.Length * 4;
        }
#endif
    }

    private void _CopyGpuChunkSliceToWorkingData()
    {
        if (!gpuColumnReadbackReady || !gpuCachedChunkSlicesReady || gpuCachedChunkSlices == null || workingChunkData == null) return;

        byte[] slice = currentChunkY >= 0 && currentChunkY < gpuCachedChunkSlices.Length ? gpuCachedChunkSlices[currentChunkY] : null;
        if (slice == null) return;

        // GPU OFFLOAD #1 NOTE: A previous attempt skipped this copy for distant chunks,
        // but that broke the GPU atlas upload path (which depends on workingChunkData being
        // populated). The atlas-skip optimization now lives in chunk completion (`_chunkData`
        // assignment), not here. This copy is a cheap Array.Copy and always runs.

#if LOGGING
        float workingCopyStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        System.Array.Clear(workingChunkData, 0, workingChunkData.Length);
        System.Array.Copy(slice, workingChunkData, slice.Length);
#if LOGGING
        if (enableDetailedTimings)
        {
            agg_gpuWorkingSliceCopies++;
            agg_gpuWorkingSliceCopyTime += (Time.realtimeSinceStartup - workingCopyStart) * 1000f;
        }
#endif

        // DIAGNOSTIC: Compare GPU readback base blocks against CPU-computed terrain
        // for this chunk slice (only runs once per column, on the topmost chunk)
        if (enableGpuNoiseDiagnostics && currentChunkY == world.worldDimensionY - 1)
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
                byte gpuBlock = gpuColumnReadbackBlocks != null ? gpuColumnReadbackBlocks[gpuIdx] : gpuColumnReadbackPixels[gpuIdx].r;

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
                byte tGpuBlock = gpuColumnReadbackBlocks != null ? gpuColumnReadbackBlocks[tGpuIdx] : gpuColumnReadbackPixels[tGpuIdx].r;
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
        if (gpuReadbackPhase == GpuWorldgenReadbackPhase.None) return;

#if LOGGING
        float callbackStartMs = enableDetailedTimings ? Time.realtimeSinceStartup * 1000f : 0f;
        float latencyMs = enableDetailedTimings && gpuReadbackRequestStartTimeMs >= 0f
            ? callbackStartMs - gpuReadbackRequestStartTimeMs
            : 0f;
#endif
        if (gpuReadbackPhase == GpuWorldgenReadbackPhase.Climate)
        {
            gpuReadbackPhase = GpuWorldgenReadbackPhase.None;
            gpuClimateReadbackPending = false;
            gpuClimateReadbackReady = false;

            if (request.hasError || !request.TryGetData(gpuClimateReadbackPixels, 0))
            {
                gpuClimateReadbackFailed = true;
                return;
            }

            gpuClimateReadbackFailed = false;
            gpuClimateReadbackReady = true;
            return;
        }

        if (gpuReadbackPhase != GpuWorldgenReadbackPhase.BaseColumn)
        {
            _HandleGpuNoiseDiagnosticReadback(request, latencyMs, callbackStartMs);
            return;
        }

        gpuReadbackPhase = GpuWorldgenReadbackPhase.None;
        gpuColumnReadbackPending = false;
        gpuColumnReadbackReady = false;

        if (request.hasError)
        {
            gpuColumnReadbackFailed = true;
            gpuReadbackContainsFinalColumn = false;
            gpuFinalColumnSliceCachePending = false;
#if LOGGING
            if (enableDetailedTimings) _RecordGpuReadbackCompletion(false, false, latencyMs, 0f, gpuReadbackRequestBytes);
#endif
            return;
        }

        if (useSingleChannelGpuColumnReadback)
        {
            if (gpuColumnReadbackBlocks == null || !request.TryGetData(gpuColumnReadbackBlocks, 0))
            {
                gpuColumnReadbackFailed = true;
                gpuReadbackContainsFinalColumn = false;
                gpuFinalColumnSliceCachePending = false;
#if LOGGING
                if (enableDetailedTimings) _RecordGpuReadbackCompletion(false, false, latencyMs, 0f, gpuReadbackRequestBytes);
#endif
                return;
            }
        }
        else
        {
            if (!request.TryGetData(gpuColumnReadbackPixels, 0))
            {
                gpuColumnReadbackFailed = true;
                gpuReadbackContainsFinalColumn = false;
                gpuFinalColumnSliceCachePending = false;
#if LOGGING
                if (enableDetailedTimings) _RecordGpuReadbackCompletion(false, false, latencyMs, 0f, gpuReadbackRequestBytes);
#endif
                return;
            }

            Color32[] readbackPixels = gpuColumnReadbackPixels;
            if (gpuColumnReadbackBlocks == null || gpuColumnReadbackBlocks.Length != readbackPixels.Length)
            {
                gpuColumnReadbackBlocks = new byte[readbackPixels.Length];
            }
            byte[] readbackBlocks = gpuColumnReadbackBlocks;
            int readbackLength = readbackPixels.Length;
            int unrolledLimit = readbackLength - 7;
            int readbackIndex = 0;
            for (; readbackIndex < unrolledLimit; readbackIndex += 8)
            {
                readbackBlocks[readbackIndex] = readbackPixels[readbackIndex].r;
                readbackBlocks[readbackIndex + 1] = readbackPixels[readbackIndex + 1].r;
                readbackBlocks[readbackIndex + 2] = readbackPixels[readbackIndex + 2].r;
                readbackBlocks[readbackIndex + 3] = readbackPixels[readbackIndex + 3].r;
                readbackBlocks[readbackIndex + 4] = readbackPixels[readbackIndex + 4].r;
                readbackBlocks[readbackIndex + 5] = readbackPixels[readbackIndex + 5].r;
                readbackBlocks[readbackIndex + 6] = readbackPixels[readbackIndex + 6].r;
                readbackBlocks[readbackIndex + 7] = readbackPixels[readbackIndex + 7].r;
            }
            for (; readbackIndex < readbackLength; readbackIndex++)
            {
                readbackBlocks[readbackIndex] = readbackPixels[readbackIndex].r;
            }
        }

        gpuColumnReadbackFailed = false;
        gpuColumnReadbackReady = true;
        if (gpuReadbackContainsFinalColumn)
        {
            gpuCachedChunkSlicesReady = false;
            gpuFinalColumnSliceCachePending = true;
        }
        gpuReadbackContainsFinalColumn = false;
#if LOGGING
        if (enableDetailedTimings)
        {
            float callbackCopyMs = Time.realtimeSinceStartup * 1000f - callbackStartMs;
            _RecordGpuReadbackCompletion(false, true, latencyMs, callbackCopyMs, gpuReadbackRequestBytes);
        }
#endif
    }

    // One-line generation breakdown (the multi-line Performance Summary gets truncated by the
    // MCP console). Call via SendCustomEvent("DebugLogGenBreakdown") with timing flags on.
    public void DebugLogGenBreakdown()
    {
#if LOGGING
        Debug.Log("[GENBRK] colsStarted=" + agg_gpuColumnsStarted + " finalized=" + agg_gpuColumnsFinalized
            + " fallbacks=" + agg_gpuFallbacks
            + " | wallClock=" + agg_time_WallClock.ToString("F0") + "ms actualWork=" + agg_time_ActualChunkWork.ToString("F0") + "ms"
            + " | prep=" + agg_time_Preparation.ToString("F0") + " genTerrain=" + agg_time_GeneratingTerrain.ToString("F0")
            + " replaceBiome=" + agg_time_ReplacingBiomes.ToString("F0") + " decoration=" + agg_time_Decoration.ToString("F0")
            + " | gpuBlit noise=" + agg_gpuNoiseBlitTime.ToString("F0") + " base=" + agg_gpuBaseFillBlitTime.ToString("F0")
            + " surf=" + agg_gpuSurfaceInfoBlitTime.ToString("F0") + " final=" + agg_gpuFinalizeBlitTime.ToString("F0")
            + " upload=" + agg_gpuNoiseInputUploadTime.ToString("F0"));
#endif
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
        lastChunkGpuResident = false;

        if (!_HasBiomeInputsReady())
        {
            _RestoreCachedBiomeStateForCurrentColumn();
        }
        
        // Check if column is cached and set initial state appropriately
        if (currentChunkX == cacheCoordX && currentChunkZ == cacheCoordZ)
        {
            // Cached column, skip directly to terrain generation or GPU copy
            bool hasBiomeInputs = _HasBiomeInputsReady();
            bool canUseGpuCachedColumn = hasBiomeInputs &&
                gpuWorldgenReady &&
                gpuColumnReadbackReady &&
                gpuCachedChunkSlicesReady &&
                currentChunkX == gpuCachedColumnX &&
                currentChunkZ == gpuCachedColumnZ;
            bool canWaitForGpuColumn = hasBiomeInputs &&
                gpuWorldgenReady &&
                ((gpuColumnReadbackPending && currentChunkX == gpuPendingColumnX && currentChunkZ == gpuPendingColumnZ) ||
                 (gpuColumnReadbackReady && currentChunkX == gpuPendingColumnX && currentChunkZ == gpuPendingColumnZ));
            bool canUseCpuCachedColumn = hasBiomeInputs &&
                _HasCpuNoiseInputsReady() &&
                _HasSurfaceNoiseInputsReady();

            if (canUseGpuCachedColumn)
            {
                currentState = GenerationState.Copy_GpuChunkSlice;
            }
            else if (canWaitForGpuColumn)
            {
                currentState = GenerationState.Prepare_GpuReadback;
            }
            else if (canUseCpuCachedColumn)
            {
                initRand(currentChunkX, currentChunkZ);
                currentState = GenerationState.GeneratingTerrain;
            }
            else
            {
                currentState = GenerationState.Prepare_GetBiomes;
#if LOGGING
                timingsCached = false;
#endif
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

    // GPU-RESIDENT (#2): true when this generator just finalized the given column as a
    // skip-readback (resident) column and its final texture (gpuLastReadbackSource) is still
    // valid. Lets sibling Y-chunks repack their slice straight from the texture without
    // re-running the column gen. If a newer column has overwritten the texture, the X/Z no
    // longer match and this returns false (caller falls back to a normal gen — correct, just
    // slower), so this is safe even under the data-gen look-ahead.
    public bool CanRepackGpuResidentColumn(int chunkX, int chunkZ)
    {
        if (gpuLastReadbackSource == null) return false;
        int gx = chunkX + (chunkOffsetX / world.chunkSizeXZ);
        int gz = chunkZ + (chunkOffsetZ / world.chunkSizeXZ);
        return gpuResidentColumnX == gx && gpuResidentColumnZ == gz;
    }

    // True while this generator is mid-gen on exactly this chunk (the column's "trigger"/holder).
    // McWorld uses it so the holder drives its state machine to Idle (and resident completion)
    // instead of being short-circuited by the sibling repack fast-path on its final step.
    public bool IsGeneratingChunk(int chunkX, int chunkY, int chunkZ)
    {
        if (currentState == GenerationState.Idle) return false;
        int gx = chunkX + (chunkOffsetX / world.chunkSizeXZ);
        int gz = chunkZ + (chunkOffsetZ / world.chunkSizeXZ);
        return currentChunkX == gx && currentChunkY == chunkY && currentChunkZ == gz;
    }

    // MULTI-GEN CONTRACT: the state machine has no active column (safe to StartChunkGeneration).
    public bool IsIdle()
    {
        return currentState == GenerationState.Idle;
    }

    // DYNAMIC GENERATOR ASSIGNMENT: is the state machine mid-generation on this COLUMN (any
    // Y)? Pre-readback a column has no copyable cache, so without this check a mid-gen
    // column's sibling Y-chunks are indistinguishable from fresh new-column candidates.
    // Same coordinate adjustment as IsGeneratingChunk.
    public bool IsGeneratingColumn(int chunkX, int chunkZ)
    {
        if (currentState == GenerationState.Idle) return false;
        int gx = chunkX + (chunkOffsetX / world.chunkSizeXZ);
        int gz = chunkZ + (chunkOffsetZ / world.chunkSizeXZ);
        return currentChunkX == gx && currentChunkZ == gz;
    }

    // MULTI-GEN CONTRACT: does the chunk the state machine last worked on match these coords?
    // Unlike IsGeneratingChunk this stays true right AFTER completion (state back at Idle), so
    // McWorld can verify a step-loop completion actually belongs to the consuming chunk before
    // writing the data into it. Same coordinate space as IsGeneratingChunk's inputs.
    public bool IsLastWorkedChunk(int chunkX, int chunkY, int chunkZ)
    {
        int gx = chunkX + (chunkOffsetX / world.chunkSizeXZ);
        int gz = chunkZ + (chunkOffsetZ / world.chunkSizeXZ);
        return currentChunkX == gx && currentChunkY == chunkY && currentChunkZ == gz;
    }

    public bool CanCopyCachedGpuChunkSlice(int chunkX, int chunkY, int chunkZ)
    {
        if (!isInitialized || !enableGpuWorldgen || !gpuWorldgenReady || !gpuColumnReadbackReady || !gpuCachedChunkSlicesReady || gpuCachedChunkSlices == null || world == null)
        {
            return false;
        }

        if (chunkY < 0 || chunkY >= world.worldDimensionY) return false;
        if (chunkY >= world.worldDimensionY - 1) return false; // top chunk still needs decoration

        int adjustedChunkX = chunkX + (chunkOffsetX / world.chunkSizeXZ);
        int adjustedChunkZ = chunkZ + (chunkOffsetZ / world.chunkSizeXZ);
        if (adjustedChunkX != gpuCachedColumnX || adjustedChunkZ != gpuCachedColumnZ) return false;

        return gpuCachedChunkSlices[chunkY] != null;
    }

    // MULTI-GEN CONTRACT: true while this generator holds a complete, copyable column slice
    // cache. McWorld uses this (with CachedColumnChunkX/Z) to keep a FOREIGN column from
    // grabbing an idle generator while the cached column still has pending sibling chunks —
    // a new column's base-fill blit sets gpuCachedChunkSlicesReady=false, so starting one
    // forces every remaining cached sibling into a full ~300ms column re-generation.
    public bool HasCopyableColumnCache()
    {
        return isInitialized && enableGpuWorldgen && gpuWorldgenReady && gpuColumnReadbackReady
            && gpuCachedChunkSlicesReady && gpuCachedChunkSlices != null;
    }

    // World-space (centered) chunk coords of the cached column. Only meaningful while
    // HasCopyableColumnCache() is true.
    public int CachedColumnChunkX() { return gpuCachedColumnX - (chunkOffsetX / world.chunkSizeXZ); }
    public int CachedColumnChunkZ() { return gpuCachedColumnZ - (chunkOffsetZ / world.chunkSizeXZ); }

    // Dedicated output buffer for sibling slice copies. NEVER workingChunkData: sibling copies
    // are legal while the state machine is BUSY (the whole point of the cache — e.g. during the
    // top chunk's multi-frame DecoratingTerrain), and workingChunkData is the machine's live
    // buffer. Routing copies through workingChunkData let a draining sibling overwrite the top
    // chunk's in-flight blocks, so the top completed with a duplicate of that sibling's slice
    // (terrain copies floating at the top of columns). McWorld clones completedData on consume,
    // so one reusable buffer is safe.
    private byte[] gpuSliceCopyBuffer;

    public bool TryCopyCachedGpuChunkSlice(int chunkX, int chunkY, int chunkZ, out byte[] completedData)
    {
        completedData = null;
        if (!CanCopyCachedGpuChunkSlice(chunkX, chunkY, chunkZ)) return false;

        int chunkSize = world.chunkSizeXZ * world.chunkSizeY * world.chunkSizeXZ;
        if (gpuSliceCopyBuffer == null || gpuSliceCopyBuffer.Length != chunkSize)
        {
            gpuSliceCopyBuffer = new byte[chunkSize];
        }

#if LOGGING
        float copyStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        System.Array.Copy(gpuCachedChunkSlices[chunkY], gpuSliceCopyBuffer, chunkSize);
        completedData = gpuSliceCopyBuffer;

#if LOGGING
        if (enableVerboseLogging && !dbg_loggedFirstGpuChunkCopy)
        {
            dbg_loggedFirstGpuChunkCopy = true;
            Debug.Log("[McTerrainGen][GPU] First GPU-generated chunk slice copied to CPU -> GPU worldgen path is producing chunks (CPU noise-gen bypassed for these).");
        }
        if (enableDetailedTimings)
        {
            float copyMs = (Time.realtimeSinceStartup - copyStart) * 1000f;
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

            time_GeneratingTerrain = copyMs;
            time_ReplacingBiomes = 0f;
            totalSteps = 1;
            maxStepTime = copyMs;
            minStepTime = copyMs;
            _AccumulateCompletedChunkProfile(
                currentChunkX,
                currentChunkY,
                currentChunkZ,
                display_time_Preparation,
                display_time_Prep_GetBiomes,
                display_time_Prep_SandNoise,
                display_time_Prep_GravelNoise,
                display_time_Prep_StoneNoise,
                display_time_Prep_AllocNoiseCache,
                display_time_NoiseGen1,
                display_time_NoiseGen2,
                display_time_NoiseGen3,
                display_time_Noise6,
                display_time_Noise7,
                display_time_NoiseCombine,
                display_noiseGen1Cells,
                display_noiseGen2Cells,
                display_noiseGen3Cells,
                display_noise6Cells,
                display_noise7Cells,
                display_noiseCombineCells,
                copyMs,
                copyMs);
        }
#endif
        return true;
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
        // EVENT-DRIVEN GEN: cleared each step; set true below only if we end this call parked on an
        // async GPU readback wait. McWorld's gen loop breaks its step loop when it sees this.
        gpuStepBlockedOnReadback = false;

        // Declare coordinate variables once for all case statements
        int noiseX = 0;
        int noiseZ = 0;
        int noiseChunkX = 0;

        int dbgEntryState = (int)currentState;
        if (dbgStateSteps != null && dbgEntryState < dbgStateSteps.Length) dbgStateSteps[dbgEntryState]++;

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
                        if (wcm != null)
                        {
                            wcm.temperatures = cachedTemperatures;
                            wcm.rainfall = cachedRainfall;
                        }
                    }
                    else
                    {
                        if (gpuWorldgenReady)
                        {
                            if (gpuClimateReadbackFailed && currentChunkX == gpuClimatePendingChunkX && currentChunkZ == gpuClimatePendingChunkZ)
                            {
                                gpuClimateReadbackFailed = false;
                                noiseX = _GetTerrainBlockStartX(currentChunkX);
                                noiseZ = _GetTerrainBlockStartZ(currentChunkZ);
                                currentChunkBiomes = wcm.getBiomeBlock(currentChunkBiomes, noiseX, noiseZ, 16, 16);
                                lastBiomeChunkX = currentChunkX;
                                lastBiomeChunkZ = currentChunkZ;
                                cachedBiomes = currentChunkBiomes;
                                if (cachedTemperatures == null || cachedTemperatures.Length != wcm.temperatures.Length)
                                {
                                    cachedTemperatures = new double[wcm.temperatures.Length];
                                    cachedRainfall = new double[wcm.rainfall.Length];
                                }
                                System.Array.Copy(wcm.temperatures, cachedTemperatures, wcm.temperatures.Length);
                                System.Array.Copy(wcm.rainfall, cachedRainfall, wcm.rainfall.Length);
                            }
                            else if (gpuClimateReadbackReady && currentChunkX == gpuClimatePendingChunkX && currentChunkZ == gpuClimatePendingChunkZ)
                            {
                                _ApplyGpuClimateReadbackToCurrentChunk();
                            }
                            else
                            {
                                if (!gpuClimateReadbackPending)
                                {
                                    if (!_StartGpuClimateGeneration())
                                    {
                                        noiseX = _GetTerrainBlockStartX(currentChunkX);
                                        noiseZ = _GetTerrainBlockStartZ(currentChunkZ);
                                        currentChunkBiomes = wcm.getBiomeBlock(currentChunkBiomes, noiseX, noiseZ, 16, 16);
                                        lastBiomeChunkX = currentChunkX;
                                        lastBiomeChunkZ = currentChunkZ;
                                        cachedBiomes = currentChunkBiomes;
                                        if (cachedTemperatures == null || cachedTemperatures.Length != wcm.temperatures.Length)
                                        {
                                            cachedTemperatures = new double[wcm.temperatures.Length];
                                            cachedRainfall = new double[wcm.rainfall.Length];
                                        }
                                        System.Array.Copy(wcm.temperatures, cachedTemperatures, wcm.temperatures.Length);
                                        System.Array.Copy(wcm.rainfall, cachedRainfall, wcm.rainfall.Length);
                                    }
                                    else
                                    {
                                        gpuStepBlockedOnReadback = true; // climate readback just kicked off — wait for callback, don't spin
                                        break;
                                    }
                                }
                                else
                                {
                                    gpuStepBlockedOnReadback = true; // climate readback in flight — wait for callback, don't spin
                                    break;
                                }
                            }
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
                    }

                    if (wcm != null && wcm.temperatures != null && wcm.rainfall != null)
                    {
                        _CacheBiomeColumnData(currentChunkX, currentChunkZ, wcm.temperatures, wcm.rainfall);
                    }
                    
#if LOGGING
                    if (enableDetailedTimings) { time_Prep_GetBiomes = (float)(DateTime.UtcNow - t0).TotalMilliseconds; }
#endif
                    // FINALIZE SLICING: the GPU path now ALSO runs the Sand/Gravel/Stone noise
                    // states (each its own budgeted step) — _StartGpuColumnFinalize consumes
                    // their buffers instead of computing all three inline in one atomic step
                    // (they were the bulk of the 33-77ms GpuFinalize block on non-tree columns).
                    // The Prepare_StoneNoise exit already routes back to Prepare_GpuNoise when
                    // gpuWorldgenReady.
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
                if (enableDetailedTimings) { time_Prep_SandNoise = (float)(DateTime.UtcNow - t0).TotalMilliseconds; }
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
                // PARITY: Beta uses 109.0134D as the Y offset for gravel noise. Matches both
                // CPU and GPU paths. (Previous GPU shader path used 109.0 which shifted gravel
                // banks; this CPU call already had the correct value.)
                this.gravelNoise = this.noiseGen4.generateNoiseOctaves(this.gravelNoise, noiseX, 109.0134D, noiseZ, 16, 1, 16, 0.03125D, 1.0D, 0.03125D);
#if LOGGING
                if (enableDetailedTimings) { time_Prep_GravelNoise = (float)(DateTime.UtcNow - t0).TotalMilliseconds; }
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
                if (enableDetailedTimings) { time_Prep_StoneNoise = (float)(DateTime.UtcNow - t0).TotalMilliseconds; }
                if (enableDetailedTimings && gpuWorldgenReady)
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
#if LOGGING
                        if (enableDetailedTimings) { agg_gpuFallbacks++; agg_gpuFallbackPrepareNoise++; }
#endif
                        currentState = GenerationState.Prepare_AllocCache;
                        break;
                    }

                    currentState = enableGpuNoiseDiagnostics ? GenerationState.Prepare_GpuReadback : GenerationState.Prepare_GpuFinalize;
                }
                break;

            case GenerationState.Prepare_GpuFinalize:
                {
                    if (!_StartGpuColumnFinalize())
                    {
#if LOGGING
                        if (enableDetailedTimings) { agg_gpuFallbacks++; agg_gpuFallbackFinalize++; }
#endif
                        currentState = GenerationState.Prepare_AllocCache;
                        break;
                    }

                    // FINALIZE SLICING: decoration + readback moved to their own state.
                    gpuDecorStep = 0;
                    currentState = GenerationState.Prepare_GpuDecorate;
                }
                break;

            case GenerationState.Prepare_GpuDecorate:
                {
                    // FINALIZE SLICING: 0 = CPU candidate collect (+ column snapshot into
                    // anchor slot 4), 1 = ONE tree-anchor chunk render per step, 2 = the
                    // decoration blits + resident latch / base-column readback request.
                    if (gpuDecorStep == 0)
                    {
                        _GpuDecorationCollect(world.chunkSizeXZ);
                        gpuDecorStep = 1;
                        break;
                    }
                    if (gpuDecorStep == 1 && _GpuDecorationRenderNextAnchor(world.chunkSizeXZ))
                    {
                        break; // more anchors pending — one per step
                    }
                    gpuDecorStep = 2;

                    RenderTexture readbackSource = _GpuDecorationFinishBlits(world.chunkSizeXZ);
                    gpuCachedColumnX = gpuPendingColumnX;
                    gpuCachedColumnZ = gpuPendingColumnZ;
                    gpuCachedChunkSlicesReady = false;
                    gpuFinalColumnSliceCachePending = false;

                    if (gpuSkipReadbackForColumn)
                    {
                        // GPU-RESIDENT (#2): the decorated column texture is GPU-ready now —
                        // skip the GPU->CPU readback entirely. McWorld repacks each Y-slice
                        // from this texture into the chunk's atlas slot; the X/Z markers let
                        // sibling chunks repack without re-running the column gen.
                        gpuLastReadbackSource = readbackSource;
                        gpuResidentColumnX = gpuPendingColumnX;
                        gpuResidentColumnZ = gpuPendingColumnZ;
                        currentState = GenerationState.GpuResidentComplete;
                    }
                    else if (_BeginGpuBaseColumnReadback(readbackSource, true))
                    {
                        currentState = GenerationState.Prepare_GpuReadback;
                    }
                    else
                    {
#if LOGGING
                        if (enableDetailedTimings) { agg_gpuFallbacks++; agg_gpuFallbackFinalize++; }
#endif
                        currentState = GenerationState.Prepare_AllocCache;
                    }
                }
                break;

            case GenerationState.Prepare_GpuReadback:
                {
                    if (gpuColumnReadbackPending && gpuReadbackPhase != GpuWorldgenReadbackPhase.None && gpuReadbackPhase != GpuWorldgenReadbackPhase.BaseColumn)
                    {
                        gpuDiagnosticReadbackStallFrames++;
                        if (gpuDiagnosticReadbackStallFrames > GPU_DIAGNOSTIC_READBACK_STALL_LIMIT)
                        {
                            Debug.LogWarning("[GPU Noise Diagnostic] Readback stalled; aborting diagnostic and continuing normal GPU worldgen.");
#if LOGGING
                            if (enableDetailedTimings) agg_gpuDiagnosticReadbackStalls++;
#endif
                            gpuColumnReadbackPending = false;
                            gpuReadbackPhase = GpuWorldgenReadbackPhase.None;
                            _BeginGpuBaseColumnReadback();
                        }
                    }

                    if (gpuColumnReadbackFailed)
                    {
#if LOGGING
                        if (enableDetailedTimings) { agg_gpuFallbacks++; agg_gpuFallbackReadbackFailure++; }
#endif
                        currentState = GenerationState.Prepare_AllocCache;
                        break;
                    }

                    if (gpuColumnReadbackReady && currentChunkX == gpuPendingColumnX && currentChunkZ == gpuPendingColumnZ)
                    {
                        bool finalColumnReadbackReady = gpuFinalColumnSliceCachePending;
                        if (gpuFinalColumnSliceCachePending)
                        {
                            _EnsureGpuChunkSliceCacheBuilt();
                        }

                        if (enableGpuNoiseDiagnostics && !finalColumnReadbackReady)
                        {
                            currentState = GenerationState.Prepare_GpuFinalize;
                        }
                        else if (gpuCachedChunkSlicesReady)
                        {
                            currentState = GenerationState.Copy_GpuChunkSlice;
                        }
                    }

                    // EVENT-DRIVEN GEN: still parked in the base-column readback wait (callback not in
                    // yet) — tell McWorld to stop re-stepping us this frame; only OnAsyncGpuReadbackComplete
                    // can advance us. (Diagnostic phases keep stepping so their stall watchdog still runs.)
                    gpuStepBlockedOnReadback = (currentState == GenerationState.Prepare_GpuReadback)
                        && !gpuColumnReadbackFailed
                        && gpuReadbackPhase == GpuWorldgenReadbackPhase.BaseColumn;
                }
                break;

            case GenerationState.Copy_GpuChunkSlice:
                {
#if LOGGING
                    DateTime tGpuCopy = DateTime.MinValue;
                    if (enableDetailedTimings) tGpuCopy = DateTime.UtcNow;
#endif
                    _CopyGpuChunkSliceToWorkingData();
                    terrain_gen_step_count = 1;
#if LOGGING
                    if (enableDetailedTimings) time_GeneratingTerrain += (float)(DateTime.UtcNow - tGpuCopy).TotalMilliseconds;
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

            case GenerationState.GpuResidentComplete:
                // GPU-RESIDENT (#2): the column was finalized on the GPU and the readback was
                // skipped. There is no CPU block array for this chunk — its data lives only in
                // the column final texture (gpuLastReadbackSource). Signal completion with no
                // data; McWorld repacks the Y-slice into the atlas and marks the chunk resident.
                completedData = null;
                lastChunkGpuResident = true;
                currentState = GenerationState.Idle;
                isComplete = true;
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
                    if (enableDetailedTimings) { 
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

                    // PERF: Octave frequencies are powers of 1/2 — pre-baked LUT eliminates
                    // 16 `System.Math.Pow` calls (each a marshaled C++ trip) per chunk worldgen.
                    if (_octaveFrequencyLut == null)
                    {
                        _octaveFrequencyLut = new double[16];
                        double freq = 1.0;
                        for (int o = 0; o < 16; o++)
                        {
                            _octaveFrequencyLut[o] = freq;
                            freq *= 0.5;
                        }
                    }

#if LOGGING
                    DateTime tNoiseStart = DateTime.UtcNow;
#endif

                    // Process current octave of current generator
                    if (currentNoiseGenerator == 0) // noise1 (16 octaves)
                    {
                        if (currentOctave == 0) currentNoiseOutput = noise1;
                        double frequency = _octaveFrequencyLut[currentOctave];
                        // CRITICAL: Handle coordinate flip for Minecraft right-handed system
                        // Apply built-in offset (converted to noise grid scale: blocks/4)
                        int noiseStartX = _GetTerrainNoiseStartX(currentChunkX, byte0);
                        int noiseStartZ = _GetTerrainNoiseStartZ(currentChunkZ, byte0);
                        noiseGen1.GetGenerator(currentOctave).generateNoiseArray(currentNoiseOutput, noiseStartX, 0, noiseStartZ, xSize, ySize, zSize, d0 * frequency, d1 * frequency, d0 * frequency, frequency);
                        currentOctave++;
                        
                        if (currentOctave >= 16) {
                            noise1 = currentNoiseOutput;
#if LOGGING
                            if (enableDetailedTimings) { noiseGen1Cells = xSize * ySize * zSize; }
#endif
                            currentNoiseGenerator = 1;
                            currentOctave = 0;
                        }
                    }
                    else if (currentNoiseGenerator == 1) // noise2 (16 octaves)
                    {
                        if (currentOctave == 0) currentNoiseOutput = noise2;
                        double frequency = _octaveFrequencyLut[currentOctave];
                        // CRITICAL: Handle coordinate flip for Minecraft right-handed system
                        // Apply built-in offset (converted to noise grid scale: blocks/4)
                        int noiseStartX = _GetTerrainNoiseStartX(currentChunkX, byte0);
                        int noiseStartZ = _GetTerrainNoiseStartZ(currentChunkZ, byte0);
                        noiseGen2.GetGenerator(currentOctave).generateNoiseArray(currentNoiseOutput, noiseStartX, 0, noiseStartZ, xSize, ySize, zSize, d0 * frequency, d1 * frequency, d0 * frequency, frequency);
                        currentOctave++;
                        
                        if (currentOctave >= 16) {
                            noise2 = currentNoiseOutput;
#if LOGGING
                            if (enableDetailedTimings) { noiseGen2Cells = xSize * ySize * zSize; }
#endif
                            currentNoiseGenerator = 2;
                            currentOctave = 0;
                        }
                    }
                    else if (currentNoiseGenerator == 2) // noise3 (8 octaves)
                    {
                        if (currentOctave == 0) currentNoiseOutput = noise3;
                        double frequency = _octaveFrequencyLut[currentOctave];
                        // CRITICAL: Handle coordinate flip for Minecraft right-handed system
                        // Apply built-in offset (converted to noise grid scale: blocks/4)
                        int noiseStartX = _GetTerrainNoiseStartX(currentChunkX, byte0);
                        int noiseStartZ = _GetTerrainNoiseStartZ(currentChunkZ, byte0);
                        noiseGen3.GetGenerator(currentOctave).generateNoiseArray(currentNoiseOutput, noiseStartX, 0, noiseStartZ, xSize, ySize, zSize, (d0 / 80.0D) * frequency, (d1 / 160.0D) * frequency, (d0 / 80.0D) * frequency, frequency);
                        currentOctave++;
                        
                        if (currentOctave >= 8) {
                            noise3 = currentNoiseOutput;
#if LOGGING
                            if (enableDetailedTimings) { noiseGen3Cells = xSize * ySize * zSize; }
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
                        if (enableDetailedTimings) { noise6Cells = xSize * zSize; }
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
                        if (enableDetailedTimings) { noise7Cells = xSize * zSize; }
#endif
                        
                        // All noise generation complete
                        noiseCombine_x = 0;
                        noiseCombine_k1 = 0;
                        noiseCombine_l1 = 0;
                        currentState = GenerationState.Prepare_CombineNoise;
                    }

#if LOGGING
                    // Accumulate timing for noise generation (across all octaves)
                    if (enableDetailedTimings)
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
                    if (!_HasBiomeInputsReady())
                    {
                        currentState = GenerationState.Prepare_GetBiomes;
                        break;
                    }
                    if (!_HasCpuNoiseInputsReady())
                    {
                        currentState = GenerationState.Prepare_AllocCache;
                        break;
                    }

                    byte byte0 = 4;
                    int xSize = byte0 + 1;
                    byte ySize = (byte)(world.worldDimensionY * world.chunkSizeY / 8 + 1);
                    int zSize = byte0 + 1;
                    int i2 = 16 / xSize;
                    double[] temp = wcm.temperatures;
                    double[] rain = wcm.rainfall;

                    int x = noiseCombine_x;
                    int k2 = x * i2 + i2 / 2;

#if LOGGING
                    DateTime tCombine = DateTime.MinValue; if (enableDetailedTimings) tCombine = DateTime.UtcNow;
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
                        if (enableDetailedTimings) noiseCombineCells += ySize;
#endif
                    }
#if LOGGING
                    if (enableDetailedTimings) time_NoiseCombine += (float)(DateTime.UtcNow - tCombine).TotalMilliseconds;
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
                        if (enableDetailedTimings && !timingsCached)
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
                if (!_HasBiomeInputsReady())
                {
                    currentState = GenerationState.Prepare_GetBiomes;
                    break;
                }
                if (!_HasCpuNoiseInputsReady())
                {
                    currentState = GenerationState.Prepare_AllocCache;
                    break;
                }

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

                    // PERF: Hoist the 4 noise-corner indices out of the yPiece loop — they only
                    // depend on (terrain_xPiece, terrain_zPiece, l_gt, b2_gt), none of which change
                    // inside the yPiece loop. Saves 4 multiplies + 4 adds per yPiece iteration.
                    int idx00 = ((terrain_xPiece + 0) * l_gt + (terrain_zPiece + 0)) * b2_gt;
                    int idx01 = ((terrain_xPiece + 0) * l_gt + (terrain_zPiece + 1)) * b2_gt;
                    int idx10 = ((terrain_xPiece + 1) * l_gt + (terrain_zPiece + 0)) * b2_gt;
                    int idx11 = ((terrain_xPiece + 1) * l_gt + (terrain_zPiece + 1)) * b2_gt;

                    for (int yPiece = startYPiece; yPiece < endYPiece; yPiece++)
                    {
                        
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
                                    // PERF: Hoist `(xPieceOffset+i2)*16` out of the k2 loop — it's invariant.
                                    int tempsRowBase = xLoc * 16 + zPieceOffset;

                                    for (int k2 = 0; k2 < 4; k2++)
                                    {
                                        int currentZ = k2 + zPieceOffset;
                                        double d17 = temps[tempsRowBase + k2];
                                        
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
                                        if (enableDetailedTimings)
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
                if (enableDetailedTimings) time_GeneratingTerrain += (float)(DateTime.UtcNow - stepStartTime).TotalMilliseconds;
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
                    if (!_HasBiomeInputsReady())
                    {
                        currentState = GenerationState.Prepare_GetBiomes;
                        break;
                    }
                    if (!_HasSurfaceNoiseInputsReady())
                    {
                        currentState = GenerationState.Prepare_SandNoise;
                        break;
                    }

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
                                            if (enableDetailedTimings)
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
                                            if (enableDetailedTimings)
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
                                        if (enableDetailedTimings)
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

                    // PARITY: Java's ChunkProviderGenerate.replaceBlocksForBiome loops Y from 127
                    // down to 0 for EVERY column, calling `rand.nextInt(5)` on every iteration —
                    // the `if (var17 <= 0 + rand.nextInt(5))` check fires for every y, not just
                    // y in [0..4]. We must consume the same RNG state so downstream decoration
                    // RNG order matches. Per column: 128 NextInt(5) calls. Loop order matches
                    // Java: (x outer, z middle, y inner).
                    int worldHeightTotal = world.worldDimensionY * world.chunkSizeY;
                    int bedrockYTop = (worldHeightTotal < 128) ? worldHeightTotal : 128;
                    byte bedrockID = bedrockBlockID;
                    bool applyHere = (currentChunkY == 0);

                    for (int x = 0; x < sizeXZ; x++)
                    {
                        for (int z = 0; z < sizeXZ; z++)
                        {
                            // Walk Y from top of column down to 0, consuming one NextInt(5) per cell.
                            for (int globalY = bedrockYTop - 1; globalY >= 0; globalY--)
                            {
                                int r = random.NextInt(5);
                                if (!applyHere) continue;          // RNG-consumed but no write outside bottom chunk
                                if (globalY > 4) continue;         // Beta only writes bedrock at Y<=4 (NextInt(5) is 0..4)
                                if (globalY > r) continue;         // Java: `if(var17 <= rand.nextInt(5))` -> globalY <= r
                                int yLocal = globalY - chunkYBase;
                                if (yLocal < 0 || yLocal >= sizeY) continue;
                                int idx = yLocal * xyStride + z * sizeXZ + x;
                                data[idx] = bedrockID;
#if LOGGING
                                if (enableDetailedTimings) biomeBedrockAssignments++;
#endif
                            }
                        }
                    }
                }
                
#if LOGGING
                if (enableDetailedTimings) time_ReplacingBiomes += (float)(DateTime.UtcNow - stepStartTime).TotalMilliseconds;
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
#if LOGGING
                    float decorStepStart = Time.realtimeSinceStartup;
#endif
                    
                    if (decoration_step == 0)
                    {
                        // If GPU decoration handles everything, skip straight to completion
                        if (gpuColumnDecorationMaterial != null && gpuColumnTreeDecorationMaterial != null)
                        {
                            decoration_step = 8;
                        }
                        else
                        {
                            // CPU fallback: initialize decoration random seed + build column buffer
                            long worldSeed = McUtils.GetMinecraftSeed(world.worldSeedString);
                            JavaRandom seedRand = new JavaRandom(worldSeed);
                            long var7 = seedRand.NextLong() / 2L * 2L + 1L;
                            long var9 = seedRand.NextLong() / 2L * 2L + 1L;
                            rand.SetSeed((long)currentChunkX * var7 + (long)currentChunkZ * var9 ^ worldSeed);
                            _DecorationBuildColumnBuffer();
                            decoration_step++;
                        }
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
#if LOGGING
                                if (enableDetailedTimings) agg_treesPlaced++;
#endif
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
                        // CPU fallback: generate yellow flowers
                        int centerBiomeIndex = 8 * 16 + 8;
                        BetaBiomeEnum centerBiome = currentChunkBiomes[centerBiomeIndex];
                        decoration_feature_count = BetaBiome.getFlowersPerChunk(centerBiome);
                        decoration_feature_index = 0;
                        decoration_step++;
                    }
                    else if (decoration_step == 4)
                    {
                        // Step 4: Place yellow flowers (CPU fallback only)
                        if (decoration_feature_index < decoration_feature_count)
                        {
                            int worldX = currentChunkX * 16;
                            int worldZ = currentChunkZ * 16;
                            int flowerX = worldX + rand.NextInt(16) + 8 + BUILTIN_OFFSET_X;
                            int flowerZ = worldZ + rand.NextInt(16) + 8 + BUILTIN_OFFSET_Z;
                            int flowerY = rand.NextInt(world.worldDimensionY * world.chunkSizeY);
                            
                            GenerateFlower(flowerX, flowerY, flowerZ, flowerYellowBlockID);
#if LOGGING
                            if (enableDetailedTimings) agg_flowersPlaced++;
#endif
                            decoration_feature_index++;
                        }
                        else
                        {
                            decoration_step++;
                        }
                    }
                    else if (decoration_step == 5)
                    {
                        // Step 5: Generate tall grass count (CPU fallback only)
                        int centerBiomeIndex = 8 * 16 + 8;
                        BetaBiomeEnum centerBiome = currentChunkBiomes[centerBiomeIndex];
                        decoration_feature_count = BetaBiome.getGrassPerChunk(centerBiome);
                        decoration_feature_index = 0;
                        decoration_step++;
                    }
                    else if (decoration_step == 6)
                    {
                        // Step 6: Place tall grass (CPU fallback only)
                        if (decoration_feature_index < decoration_feature_count)
                        {
                            int worldX = currentChunkX * 16;
                            int worldZ = currentChunkZ * 16;
                            int grassX = worldX + rand.NextInt(16) + 8 + BUILTIN_OFFSET_X;
                            int grassZ = worldZ + rand.NextInt(16) + 8 + BUILTIN_OFFSET_Z;
                            int grassY = rand.NextInt(world.worldDimensionY * world.chunkSizeY);
                            
                            GenerateTallGrass(grassX, grassY, grassZ);
#if LOGGING
                            if (enableDetailedTimings) agg_tallGrassPlaced++;
#endif
                            decoration_feature_index++;
                        }
                        else
                        {
                            decoration_step++;
                        }
                    }
                    else if (decoration_step == 7)
                    {
                        // Step 7: Red flower (CPU fallback only)
                        if (rand.NextInt(2) == 0)
                        {
                            int worldX = currentChunkX * 16;
                            int worldZ = currentChunkZ * 16;
                            int flowerX = worldX + rand.NextInt(16) + 8 + BUILTIN_OFFSET_X;
                            int flowerZ = worldZ + rand.NextInt(16) + 8 + BUILTIN_OFFSET_Z;
                            int flowerY = rand.NextInt(world.worldDimensionY * world.chunkSizeY);
                            
                            GenerateFlower(flowerX, flowerY, flowerZ, flowerRedBlockID);
#if LOGGING
                            if (enableDetailedTimings) agg_flowersPlaced++;
#endif
                        }
                        decoration_step++;
                    }
                    else
                    {
                        // Decoration complete — flush modified slices back
                        _DecorationFlushColumnBuffer();
#if LOGGING
                        if (enableDetailedTimings) agg_decorationColumns++;
#endif
                        currentState = GenerationState.Complete;
                    }
#if LOGGING
                    if (enableDetailedTimings)
                    {
                        agg_time_Decoration += (Time.realtimeSinceStartup - decorStepStart) * 1000f;
                    }
#endif
                }
                break;

            case GenerationState.Complete:
                completedData = workingChunkData;

                // DIAGNOSTIC: Per-chunk block-count dump used to fire here unconditionally.
                // With 64 chunks in flight, the Debug.Log + StringBuilder churn was adding
                // ~5-10ms/chunk of overhead. Gated behind `enableVerboseLogging` so it can
                // be turned on for one-off debugging without dominating frame time.
                if (enableVerboseLogging)
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
                    int sXZ = world.chunkSizeXZ;
                    diagBuf.Append("\n  Y=0 row z=0: ");
                    for (int dx = 0; dx < sXZ; dx++)
                    {
                        diagBuf.Append(workingChunkData[0 * sXZ * sXZ + 0 * sXZ + dx]).Append(" ");
                    }
                    Debug.Log(diagBuf.ToString());
                }
#if LOGGING
                if (enableDetailedTimings)
                {
                    float actualChunkTime = time_GeneratingTerrain + time_ReplacingBiomes;
                    float totalTime = (float)(DateTime.UtcNow - time_Total_Start).TotalMilliseconds;
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
                    _AccumulateCompletedChunkProfile(
                        currentChunkX,
                        currentChunkY,
                        currentChunkZ,
                        display_time_Preparation,
                        display_time_Prep_GetBiomes,
                        display_time_Prep_SandNoise,
                        display_time_Prep_GravelNoise,
                        display_time_Prep_StoneNoise,
                        display_time_Prep_AllocNoiseCache,
                        display_time_NoiseGen1,
                        display_time_NoiseGen2,
                        display_time_NoiseGen3,
                        display_time_Noise6,
                        display_time_Noise7,
                        display_time_NoiseCombine,
                        display_noiseGen1Cells,
                        display_noiseGen2Cells,
                        display_noiseGen3Cells,
                        display_noise6Cells,
                        display_noise7Cells,
                        display_noiseCombineCells,
                        actualChunkTime,
                        totalTime);
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
        if (enableDetailedTimings)
        {
            // Track per-step timing
            lastStepTime = (Time.realtimeSinceStartup - frameStepStart) * 1000f;
            if (lastStepTime > maxStepTime) maxStepTime = lastStepTime;
            if (lastStepTime < minStepTime) minStepTime = lastStepTime;
            totalSteps++;
            // Attribute the step's wall-clock to the state it ENTERED with (state may have
            // transitioned inside the switch) — see dbgStateTimeMs declaration.
            if (dbgStateTimeMs != null && dbgEntryState < dbgStateTimeMs.Length)
            {
                dbgStateTimeMs[dbgEntryState] += lastStepTime;
                if (lastStepTime > dbgStateTimeMaxMs[dbgEntryState]) dbgStateTimeMaxMs[dbgEntryState] = lastStepTime;
            }
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
            return;
        }

        int noiseX = _GetTerrainBlockStartX(chunkX);
        int noiseZ = _GetTerrainBlockStartZ(chunkZ);
        if (biomeQueryWcm == null)
        {
            biomeQueryWcm = new WorldChunkManagerOld(generatorSeed);
        }
        biomeQueryWcm.getBiomeBlock(null, noiseX, noiseZ, 16, 16);

        if (biomeQueryWcm.temperatures != null && biomeQueryWcm.rainfall != null)
        {
            int queryCopySize = System.Math.Min(outTemperatures.Length, biomeQueryWcm.temperatures.Length);
            System.Array.Copy(biomeQueryWcm.temperatures, outTemperatures, queryCopySize);
            System.Array.Copy(biomeQueryWcm.rainfall, outRainfall, queryCopySize);
            return;
        }

        for (int i = 0; i < outTemperatures.Length; i++)
        {
            outTemperatures[i] = 0.5;
            outRainfall[i] = 0.5;
        }
    }

    // This function is now fully time-sliced and integrated into the state machine.
    // private double[] initNoiseField(...)

    // This method is now obsolete as the GPU handles all block filling.
    // private bool StepBlockFilling() { ... }

    // This method is now obsolete as the GPU handles all surface decoration.
    // private bool StepSurfaceDecoration() { ... }

    private void _EnsureGpuCaveHashesReady()
    {
        if (gpuCaveHashesReady) return;
        long worldSeed = McUtils.GetMinecraftSeed(world.worldSeedString);
        JavaRandom seedRand = new JavaRandom(worldSeed);
        long hashALong = seedRand.NextLong() / 2L * 2L + 1L;
        long hashBLong = seedRand.NextLong() / 2L * 2L + 1L;
        gpuCaveHashAHi = (int)(hashALong >> 32);
        // UDON-CHECKED-CAST: see comment above. XOR-then-subtract bit-pattern cast.
        long _hashALo = hashALong & 0xFFFFFFFFL;
        gpuCaveHashALo = (int)((_hashALo ^ 0x80000000L) - 0x80000000L);
        gpuCaveHashBHi = (int)(hashBLong >> 32);
        long _hashBLo = hashBLong & 0xFFFFFFFFL;
        gpuCaveHashBLo = (int)((_hashBLo ^ 0x80000000L) - 0x80000000L);
        gpuCaveHashesReady = true;
    }

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

    // ===== DECORATION COLUMN BUFFER =====
    // During decoration, we merge all gpuCachedChunkSlices into a single flat byte[]
    // so we can do direct array indexing instead of world.GetBlock/SetBlock.
    // Layout: buffer[y * sizeXZ * sizeXZ + z * sizeXZ + x] — same as chunk data but full column height.

    private void _DecorationBuildColumnBuffer()
    {
        int sXZ = world.chunkSizeXZ;
        int sY = world.chunkSizeY;
        int dimY = world.worldDimensionY;
        int totalHeight = dimY * sY;
        int chunkBlockCount = sXZ * sY * sXZ;
        int columnBlockCount = sXZ * totalHeight * sXZ;

        if (decoration_columnBuffer == null || decoration_columnBuffer.Length != columnBlockCount)
        {
            decoration_columnBuffer = new byte[columnBlockCount];
            decoration_columnDirtySlice = new bool[dimY];
        }

        // Copy each slice into the merged buffer
        for (int cy = 0; cy < dimY; cy++)
        {
            decoration_columnDirtySlice[cy] = false;
            byte[] slice = gpuCachedChunkSlices != null && cy < gpuCachedChunkSlices.Length ? gpuCachedChunkSlices[cy] : null;
            if (slice != null && slice.Length == chunkBlockCount)
            {
                System.Array.Copy(slice, 0, decoration_columnBuffer, cy * chunkBlockCount, chunkBlockCount);
            }
            else
            {
                System.Array.Clear(decoration_columnBuffer, cy * chunkBlockCount, chunkBlockCount);
            }
        }

        // The column's world-space origin: chunk coords * chunkSize gives the block origin.
        // currentChunkX/Z are centered chunk coordinates used by GetBlock/SetBlock.
        // GetBlock expects global block coordinates, where chunkX << 4 = block X.
        decoration_columnOriginX = currentChunkX * sXZ;
        decoration_columnOriginZ = currentChunkZ * sXZ;
        decoration_columnHeight = totalHeight;
        decoration_sizeXZ = sXZ;
    }

    private void _DecorationFlushColumnBuffer()
    {
        if (decoration_columnBuffer == null || gpuCachedChunkSlices == null) return;

        int sXZ = decoration_sizeXZ;
        int sY = world.chunkSizeY;
        int chunkBlockCount = sXZ * sY * sXZ;
        int dimY = world.worldDimensionY;

        for (int cy = 0; cy < dimY; cy++)
        {
            if (!decoration_columnDirtySlice[cy]) continue;

            byte[] slice = cy < gpuCachedChunkSlices.Length ? gpuCachedChunkSlices[cy] : null;
            if (slice != null)
            {
                System.Array.Copy(decoration_columnBuffer, cy * chunkBlockCount, slice, 0, chunkBlockCount);
            }

            // The top chunk's workingChunkData also needs updating
            if (cy == currentChunkY)
            {
                System.Array.Copy(decoration_columnBuffer, cy * chunkBlockCount, workingChunkData, 0, chunkBlockCount);
            }
        }
    }

    private byte _DecorationGetBlock(int globalX, int globalY, int globalZ)
    {
        // In-column fast path
        int localX = globalX - decoration_columnOriginX;
        int localZ = globalZ - decoration_columnOriginZ;
        if (localX >= 0 && localX < decoration_sizeXZ &&
            localZ >= 0 && localZ < decoration_sizeXZ &&
            globalY >= 0 && globalY < decoration_columnHeight)
        {
            return decoration_columnBuffer[globalY * decoration_sizeXZ * decoration_sizeXZ + localZ * decoration_sizeXZ + localX];
        }
        // Out-of-column fallback (tree canopy extending into neighbor columns)
        return world.GetBlock(globalX, globalY, globalZ);
    }

    private void _DecorationSetBlock(int globalX, int globalY, int globalZ, byte blockType)
    {
        // In-column fast path
        int localX = globalX - decoration_columnOriginX;
        int localZ = globalZ - decoration_columnOriginZ;
        if (localX >= 0 && localX < decoration_sizeXZ &&
            localZ >= 0 && localZ < decoration_sizeXZ &&
            globalY >= 0 && globalY < decoration_columnHeight)
        {
            decoration_columnBuffer[globalY * decoration_sizeXZ * decoration_sizeXZ + localZ * decoration_sizeXZ + localX] = blockType;
            // Mark which chunk slice was modified
            int chunkY = globalY / world.chunkSizeY;
            if (chunkY < decoration_columnDirtySlice.Length)
                decoration_columnDirtySlice[chunkY] = true;
            return;
        }
        // Out-of-column fallback
        world.SetBlock(globalX, globalY, globalZ, blockType);
    }

    // ===== DECORATION METHODS (Beta 1.7.3 WorldGenTrees, WorldGenTallGrass, WorldGenFlowers) =====
    
    private int GetHighestSolidBlock(int globalX, int globalZ)
    {
        for (int y = decoration_columnHeight - 1; y >= 0; y--)
        {
            byte blockID = _DecorationGetBlock(globalX, y, globalZ);
            if (blockID != airBlockID)
            {
                return y + 1;
            }
        }
        return 0;
    }
    
    private void GenerateTree(int x, int y, int z)
    {
        // EXACT port of Beta 1.7.3 WorldGenTrees.java
        int treeHeight = rand.NextInt(3) + 4;
        
        if (y < 1 || y + treeHeight + 1 > decoration_columnHeight) return;
        
        byte blockBelow = _DecorationGetBlock(x, y - 1, z);
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
                    if (checkY >= 0 && checkY < decoration_columnHeight)
                    {
                        byte checkBlock = _DecorationGetBlock(checkX, checkY, checkZ);
                        if (checkBlock != airBlockID && checkBlock != leavesBlockID)
                        {
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }
            }
        }
        
        _DecorationSetBlock(x, y - 1, z, dirtBlockID);
        
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
                    
                    if ((System.Math.Abs(xOffset) != leafRadius || System.Math.Abs(zOffset) != leafRadius || rand.NextInt(2) != 0 && yOffset != 0))
                    {
                        byte blockAtPos = _DecorationGetBlock(leafX, leafY, leafZ);
                        if (blockAtPos == airBlockID || blockAtPos == leavesBlockID)
                        {
                            _DecorationSetBlock(leafX, leafY, leafZ, leavesBlockID);
                        }
                    }
                }
            }
        }
        
        // Generate trunk
        for (int trunkY = 0; trunkY < treeHeight; trunkY++)
        {
            byte blockAtPos = _DecorationGetBlock(x, y + trunkY, z);
            if (blockAtPos == airBlockID || blockAtPos == leavesBlockID)
            {
                _DecorationSetBlock(x, y + trunkY, z, logBlockID);
            }
        }
    }
    
    private void GenerateTallGrass(int x, int y, int z)
    {
        // EXACT port of Beta 1.7.3 WorldGenTallGrass.java
        while (true)
        {
            byte blockAtPos = _DecorationGetBlock(x, y, z);
            if ((blockAtPos != airBlockID && blockAtPos != leavesBlockID) || y <= 0)
            {
                for (int attempt = 0; attempt < 128; attempt++)
                {
                    int grassX = x + rand.NextInt(8) - rand.NextInt(8);
                    int grassY = y + rand.NextInt(4) - rand.NextInt(4);
                    int grassZ = z + rand.NextInt(8) - rand.NextInt(8);
                    
                    if (grassY >= 0 && grassY < decoration_columnHeight)
                    {
                        byte blockAbove = _DecorationGetBlock(grassX, grassY, grassZ);
                        if (blockAbove == airBlockID)
                        {
                            if (grassY > 0)
                            {
                                byte blockBelowGrass = _DecorationGetBlock(grassX, grassY - 1, grassZ);
                                if (blockBelowGrass == grassBlockID || blockBelowGrass == dirtBlockID)
                                {
                                    _DecorationSetBlock(grassX, grassY, grassZ, tallGrassBlockID);
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
        if (y >= 0 && y < decoration_columnHeight)
        {
            byte blockAtPos = _DecorationGetBlock(x, y, z);
            if (blockAtPos == airBlockID)
            {
                if (y > 0)
                {
                    byte blockBelow = _DecorationGetBlock(x, y - 1, z);
                    if (blockBelow == grassBlockID || blockBelow == dirtBlockID)
                    {
                        _DecorationSetBlock(x, y, z, flowerBlockID);
                    }
                }
            }
        }
    }
    
    private void GenerateDebugPillar()
    {
        int pillarX = 0;
        int pillarZ = 0;
        int worldHeight = world.worldDimensionY * world.chunkSizeY;
        
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
}
