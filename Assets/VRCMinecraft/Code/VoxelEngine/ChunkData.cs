#define LOGGING

using UnityEngine;

// This is a plain C# class, not an UdonSharpBehaviour.
// It holds all the state for a single chunk that was previously in McChunk.cs.
public class ChunkData
{
    // --- Identifiers & World Reference ---
    public McWorld world;
    public int chunkX_world, chunkY_world, chunkZ_world;

    // --- Game Object & Component References ---
    public GameObject gameObject;
    public MeshFilter opaqueMeshFilter;
    public MeshFilter transparentMeshFilter;
    public MeshFilter cutoutMeshFilter;
    public MeshRenderer opaqueRenderer;     // (c) GPU chunk-mesh render path: cached opaque renderer
    public MeshRenderer transparentRenderer; // cutout/transparent renderers for the GPU passes
    public MeshRenderer cutoutRenderer;
    public Material opaqueOriginalMaterial; // sharedOpaque material to restore if a chunk leaves GPU render
    public bool _isGpuMeshRendered = false; // true while this chunk shows the shared GPU voxel mesh
    public MeshCollider meshCollider; // Player collision (solid blocks only)
    public MeshCollider selectionCollider; // Selection/focus collision (includes focusable non-solid blocks)

    // --- Data & State ---
    // PERF: Pair `_chunkData` with a discriminator byte so the hot _GetBlockLocal /
    // _SetBlockLocal paths can do `switch (_chunkDataKind)` (single byte compare)
    // instead of `_chunkData.GetType()` which is a virtual call + RTTI scan in Udon —
    // the single most expensive op on every block read on the slow path.
    // Kinds: 0 = unset/null, 1 = homogeneous (boxed byte), 2 = raw byte[],
    //        3 = column RLE (ushort[][]).
    public const byte CHUNK_KIND_NULL = 0;
    public const byte CHUNK_KIND_HOMOGENEOUS = 1;
    public const byte CHUNK_KIND_RAW = 2;
    public const byte CHUNK_KIND_RLE = 3;
    public byte _chunkDataKind;
    public object _chunkData;
    public int _chunkDataSize;
    public bool isDataReady = false;
    public bool isSingleOpaqueSolid = false;
    public bool isGeneratingData = false;
    public bool isBuildingMesh = false;
    public bool isMeshDeferred = false;
    public bool pendingColliderApply = false;
    public bool pendingColliderMeshRebuild = false;
    public bool pendingChunkMeshRebuild = false;
    public bool isQueuedForMeshRebuild = false;
    public bool interactionMeshPriority = false;
    public bool pendingNeighborMeshRebuild = false;
    public bool pendingLightingFinalize = false;
    public int _meshBuildVersion = 0;

    // --- Persistent Decompression Cache (OPTIMIZATION) ---
    public byte[] _cachedDecompressedData;
    public bool _decompCacheValid = false;
    public int _cachedDataVersion = 0;

    // --- Derived per-chunk caches (updated from decompressed data) ---
    public ChunkData[] _cachedNeighbors;
    public bool _neighborCacheValid = false;
    public byte[] _columnMinY;
    public byte[] _columnMaxY;
    public int[] _crossBlockPackedPositions;
    public int _crossBlockCount = 0;
    public int[] _torchBlockPackedPositions;
    public int _torchBlockCount = 0;
    // AMBIENT PARTICLE EMITTERS: packed (blockId << 24) | (x << 16) | (y << 8) | z for blocks whose
    // randomDisplayTick emits ambient particles (torch, fire, lava-with-air-above, lit furnace,
    // redstone torch on, portal). Collected by the derived-data scan; McParticleManager walks these
    // instead of blind-sampling 1000 random positions through GetBlock every frame. Capacity-capped
    // (McWorld.AMBIENT_EMITTER_CAP) — overflow entries are dropped, which only thins particles on
    // pathological all-lava-surface chunks.
    public int[] _ambientEmitterPacked;
    public int _ambientEmitterCount = 0;
    public bool _isAllAir = false;
    public bool _hasWaterBlocks = false;
    public bool _hasEmissiveBlocks = false;
    public bool _hasTorchBlocks = false;
    // GPU shared-mesh render gates (derived scan):
    // _hasNonOpaqueContent — any voxel whose visibility class isn't Opaque (cutout/transparent/
    //   cross/torch/fluid). When false, the chunk's cutout renderer never needs the shared voxel
    //   mesh — skipping it halves that chunk's per-frame vertex-shader load.
    // _isAllOpaqueSolid — every voxel is an opaque solid. Combined with all 6 neighbors also being
    //   all-opaque-solid, the chunk has zero visible faces and its renderers can be skipped
    //   entirely (typical for buried underground chunks — a large share of the lit set).
    // _derivedSolidOpaqueCount — scan-internal accumulator (persists across sliced scan calls).
    public bool _hasNonOpaqueContent = false;
    public bool _isAllOpaqueSolid = false;
    public int _derivedSolidOpaqueCount = 0;
    // Data version the derived caches were last computed for. Lets prep stage 1 skip the
    // 4096-voxel rescan when a re-prep (neighbor-rebuild churn during load) runs on unchanged
    // data — the single biggest redundant cost in the load-phase meshing loop.
    public int _derivedForDataVersion = -1;
    public byte _chunkGlobalMinY = 255, _chunkGlobalMaxY = 0;
    public byte _chunkGlobalMinX = 255, _chunkGlobalMaxX = 0;
    public byte _chunkGlobalMinZ = 255, _chunkGlobalMaxZ = 0;
    // RENDER-PREP DEFERRAL: true when _RefreshChunkDerivedData was skipped at finalize because the
    // chunk generated outside the render distance (data-gen margin). The derived caches above are
    // stale/unallocated until materialised at mesh-build entry (_EnsureChunkDerivedData clears this).
    public bool _derivedDirty = false;
    public byte[] torchMountData;

    // --- Block Metadata (Beta 1.7.3 style) ---
    // Per-voxel metadata (water flow level, fire age, cactus/reed growth, leaf decay flags, etc.)
    // Index: y * 256 + z * 16 + x
    public byte[] blockMetadata;

    // --- GPU Face Extraction State ---
    public int _gpuFaceSlotIndex = -1;
    public int _gpuFaceReadbackQueueSlot = -1;
    public bool _gpuMeshPending = false;
    public byte _borderMissingMask = 0; // bits 0-5 = +Y,-Y,+Z,-Z,+X,-X neighbors missing at mesh time
    public bool _gpuFaceBuildActive = false;
    public int _gpuFaceBuildStage = 0; // -1=summary prep, 0=greedy faces, 1=cross+torch, 2=water, 3=apply
    public int _gpuFaceDirection = 0;
    public int _gpuFaceSlice = 0;
    public int _gpuFaceCrossIndex = 0;
    public int _gpuFaceWaterColumnIndex = 0;
    public int _gpuFaceSummaryIndex = 0;
    public bool _gpuFaceBuildUsesSummary = false;
    public Color32[] _gpuFacePixels;
    public byte[] _gpuFaceSliceActive;
    public byte[] _gpuFaceSliceMinU;
    public byte[] _gpuFaceSliceMaxU;
    public byte[] _gpuFaceSliceMinV;
    public byte[] _gpuFaceSliceMaxV;

    // --- GPU render-prep time-slicing (shared-mesh render path) ---
    // The GPU render gate spreads a chunk's render-prep (atlas sync, derived-data scan, mesh+fluid
    // assign) across frames instead of doing ~28ms atomically. _gpuPrepActive marks a chunk in the
    // stepped prep; _gpuPrepStage is the stage; _derivedScanCursor is the Y cursor for the sliced
    // derived-data scan; _gpuPrepData holds the decompressed blocks the prep operates on.
    public bool _gpuPrepActive = false;
    public int _gpuPrepStage = 0;
    public int _derivedScanCursor = 0;
    public byte[] _gpuPrepData;
    // Stage-3 fluid build z-row cursor: -1 = setup pending, 0..15 = next z-row, 16 = apply.
    public int _gpuFluidZCursor = -1;

    // NO-HOLES guarantee: set true the moment a render mesh is APPLIED to this chunk (GPU prep finish,
    // CPU mesh apply, or an intentional empty/occluded apply). Cleared on recycle. The deferred-mesh
    // sweep re-arms any in-render, data-ready, non-air chunk that lacks this — so a chunk can never be
    // a permanent hole even if its build fell out of the pipeline.
    public bool _renderMeshApplied = false;

    // --- Time-slicing State (for meshing) ---
    public int _greedyAxis;
    public int _greedyU, _greedyV;
    public int _lastMeshStepFrame = 0;

    // --- Meshing Buffers (pooled by McWorld — only valid while isBuildingMesh) ---
    public int _meshPoolSlot = -1;
    public Vector3[] _opaqueVertices; public int[] _opaqueTriangles; public Vector3[] _opaqueUVs; public Vector3[] _opaqueNormals;
    public int _opaqueVertexCount; public int _opaqueTriangleCount;
    public Vector3[] _transparentVertices; public int[] _transparentTriangles; public Vector3[] _transparentUVs; public Vector3[] _transparentNormals;
    public int _transparentVertexCount; public int _transparentTriangleCount;
    public Vector3[] _cutoutVertices; public int[] _cutoutTriangles; public Vector3[] _cutoutUVs; public Vector3[] _cutoutNormals;
    public int _cutoutVertexCount; public int _cutoutTriangleCount;
    public Vector3[] _collisionVertices; public int[] _collisionTriangles;
    public int _collisionVertexCount; public int _collisionTriangleCount;
    public Vector3[] _selectionVertices; public int[] _selectionTriangles;
    public int _selectionVertexCount; public int _selectionTriangleCount;

    // --- Vertex Colors (for biome tinting and AO) ---
    public Color[] _opaqueColors;
    public Color[] _transparentColors;
    public Color[] _cutoutColors;

    // --- Biome Data (for tinting during mesh generation) ---
    // Arrays of temperature and rainfall values per XZ column (16x16)
    // Populated during terrain generation and used during meshing
    public double[] _biomeTemperatures;
    public double[] _biomeRainfall;

    // --- Lighting Data (Minecraft Beta 1.7.3 style) ---
    // One byte per voxel: high 4 bits = sky light (0-15), low 4 bits = block light (0-15)
    // Array size: 16x16x16 = 4096 bytes
    // Index: y * 256 + z * 16 + x
    public byte[] lightData;

    // Flag to prevent recursive BFS during cross-chunk propagation
    public bool isPropagatingLight = false;

    // --- FIXED: Incremental Lighting State (for coordinator-managed lighting) ---
    public bool isProcessingLighting = false;
    public int lightingPhase = 0; // 0=sky, 1=block, 2=complete
    public int lightingQueueStart = 0;
    public int lightingQueueEnd = 0;
    public int lightingIteration = 0;
    public int[] lightingQueue; // Persistent queue for this chunk

    // --- Sentinel Occupancy Buffer (for boundary-mask meshing) ---
    // Stores block IDs for the current chunk plus a 1-voxel border copied from neighbors.
    public byte[] _sentinel; // length = (chunkSizeXZ+2) * (chunkSizeY+2) * (chunkSizeXZ+2)
    public int _sentinelSX, _sentinelSY, _sentinelSZ;
    public int _sentinelStrideY, _sentinelStrideZ;
    public bool _sentinelReady;

    // --- Decompressed data caches (per-mesh-build) ---
    // Flattened as: y * (sizeXZ*sizeXZ) + (z * sizeXZ + x)
    public byte[] _decompSelf;
    public byte[] _decompPX, _decompNX, _decompPY, _decompNY, _decompPZ, _decompNZ;

    // --- Neighbor Caches (for meshing) ---
    // These are direct references to neighbor data, fetched once at the start of meshing.
    public ChunkData neighborPX, neighborNX, neighborPY, neighborNY, neighborPZ, neighborNZ;

    // --- OPTIMIZATION: Pre-computed brightness cache (Phase 3) ---
    // Cache brightness values (0-15) for all blocks in chunk to avoid repeated lighting calculations
    // Size: 16x16x16 = 4096 bytes, indexed by: y * 256 + z * 16 + x
    public byte[] _cachedBrightness;

    // --- OPTIMIZATION: Pre-computed biome color cache (Phase 6) ---
    // Cache biome colors per XZ column (256 values for 16x16 grid)
    // Indexed by: z * 16 + x. Reduces biome texture lookups from ~10,000 to 256 per chunk
    public Color[] _cachedBiomeColors;
    public int[] _cachedPackedGrassBiomeColors;
    public bool _cachedBiomeColorsValid = false;
    // Biome data (_biomeTemperatures/_biomeRainfall) is a pure function of world seed + column
    // position — once fetched it can never change. Without this flag every mesh prep re-ran the
    // generator's full 10-octave biome simplex for 256 columns (~60-300ms — the residual one-frame
    // spikes) and re-invalidated the colour/texture caches below.
    public bool _biomeDataValid = false;
    // The 16x16 biome colour texture matches _cachedBiomeColors; skip SetPixels+Apply while valid.
    public bool _gpuBiomeTexValid = false;

    // GPU OFFLOAD #3: Per-chunk biome tint RTs (16x16) baked by GpuBiomeColorBake shader.
    // Sampled directly by mesh shader (no CPU readback). Only allocated when GPU bake is enabled.
    public UnityEngine.RenderTexture _gpuBiomeGrassRT;
    // (c) GPU chunk-mesh render: per-column biome tint as a LINEAR texture holding the EXACT CPU
    // _cachedBiomeColors (so the shader samples the same raw values MCTerrain uses as vertex colours).
    public UnityEngine.Texture2D _gpuBiomeCpuTex;
    public UnityEngine.RenderTexture _gpuBiomeFoliageRT;
    public UnityEngine.RenderTexture _gpuBiomeWaterRT;
    public bool _gpuBiomeColorsBaked = false;

    // GPU OFFLOAD #3: Per-chunk 16x16 climate Texture2D (temperature.r + rainfall.g) used as
    // input to the biome color baker. Populated from the terrain generator's
    // wcm.temperatures/rainfall via SetPixels32 + Apply.
    public UnityEngine.Texture2D _gpuClimateTex;
    public UnityEngine.Color32[] _gpuClimateUploadScratch;

    // GPU OFFLOAD #9: AO output RT — packed AO values per voxel face vertex for the mesh shader.
    public UnityEngine.RenderTexture _gpuAORT;
    public bool _gpuAOBaked = false;

    // GPU OFFLOAD #4: Sentinel border RT — same chunk-block format but with 1-voxel border
    // filled from 6 neighbors via Blit (not CPU). Mesh shader samples this for face culling.
    public UnityEngine.RenderTexture _gpuSentinelRT;
    public bool _gpuSentinelBuilt = false;

    // GPU OFFLOAD #7: per-chunk fluid level RT (8-bit) for the GPU water/lava CA pass.
    public UnityEngine.RenderTexture _gpuFluidLevelRT;
    public UnityEngine.RenderTexture _gpuFluidLevelRTNext;

    // GPU OFFLOAD #8: per-chunk scheduled-tick bitmask RT (1 bit per voxel).
    public UnityEngine.RenderTexture _gpuTickMaskRT;
    public bool _gpuTickMaskDirty = false;

    // GPU OFFLOAD #5: Per-chunk face buffer RT — packed (x,y,z,face,blockId,ao,light)
    // for every visible face. Sized by face count from GpuVoxelFaceExtract's summary pass.
    // Sampled by GpuVoxelQuadDraw vertex shader at unity_InstanceID time.
    public UnityEngine.RenderTexture _gpuQuadFaceBufferRT;
    public int _gpuQuadFaceCount = 0;
    // Per-chunk bounds for instanced draw (used as Bounds for culling).
    public UnityEngine.Bounds _gpuQuadDrawBounds;

    // (c) GPU instanced voxel render: when true, this chunk renders via per-voxel instancing
    // straight from the atlas (no CPU mesh, no readback) and is NOT meshed on the CPU.
    public bool _isInstancedRendered;

    // GPU OFFLOAD #2: GPU-resident chunk flag. True for chunks whose authoritative block
    // data lives in the GPU atlas slot — `_chunkData` may be null and `_cachedDecompressedData`
    // may be stale. Mesh + lighting passes sample from the GPU atlas directly.
    //
    // When _isGpuResident is true and CPU code needs blocks (collision/raycast/tick),
    // McWorld.GetBlock triggers a sync readback into `_cachedDecompressedData` via
    // `_GpuRehydrateCpuMirror`. The flag is cleared once the player walks close enough
    // to bring the chunk into the cpuMirrorRadius.
    public bool _isGpuResident = false;

    // Pending hydration request — set when CPU code calls GetBlock on a GPU-resident
    // chunk; cleared by the readback completion handler.
    public bool _gpuMirrorRehydratePending = false;

#if LOGGING
    // --- Debugging & Timings ---
    public float time_DataPrep, time_NeighborCache, time_MainLoop, time_ApplyOpaque, time_ApplyTransparent, time_ApplyCutout, time_ApplyCollision, time_ApplySelection;
    public float timer_start_stage; // Helper to carry state between steps
    public int mesh_step_count;

    // --- Extra-detailed profiling ---
    public float time_SentinelEnsure, time_DecompressNeighbors, time_SentinelBuild;
    public float time_AxisY, time_AxisZ, time_AxisX;
    public int boundaryChecksY, boundaryChecksZ, boundaryChecksX;
    public int shouldDrawTests, shouldDrawTrue;
    public int facesOpaque, facesTransparent, facesCutout, facesTotal;
    public int sentinelInteriorCopied, sentinelBorderCopied;

    // --- Lighting Performance Profiling ---
    public float time_LightingInit, time_LightingImport, time_LightingBFS_Sky, time_LightingBFS_Block, time_LightingReconcile;
    public int lightingBlocksProcessed_Sky, lightingBlocksProcessed_Block;
    public int lightingQueueOps_Sky, lightingQueueOps_Block;
    public int lightingNeighborQueries_Sky, lightingNeighborQueries_Block;
    public int lightingCrossChunkOps_Sky, lightingCrossChunkOps_Block;
    public int lightingUpdatesApplied_Sky, lightingUpdatesApplied_Block;
    public int lightingSkylightReachedBlocks; // Blocks that got skylight=15 (early optimization)
    public int lightingSkylightZeroBlocks; // Blocks that got skylight=0 (need BFS)

    // --- RLE Compression Stats ---
    public float rle_compressionTime, rle_decompressionTime;
    public int rle_compressionCount, rle_decompressionCount;
    public int rle_bytesIn, rle_bytesOut;
    public float rle_compressionRatio;
    public int rle_homogeneousDetections;

    // --- Block Access Stats ---
    public int block_getLocalCalls, block_setLocalCalls;
    public int block_rleTraversalDepthTotal, block_rleTraversalDepthCount;
    public int block_decompressionTriggers;

    // --- Cache Stats ---
    public int cache_decompHits, cache_decompMisses;
    public int cache_neighborHits, cache_neighborMisses;
    public int cache_sentinelReuses;

    // --- Memory Tracking ---
    public int memory_meshBufferBytes;
    public int memory_lightDataBytes;
    public int memory_sentinelBytes;
    public int memory_totalAllocated;

    // --- Mesh Build Tracking ---
    public float meshBuildStartTime;
    public float profile_dataReadyTime;
    public float profile_deferredColliderQueuedTime;
    public bool profile_waitingForFirstMesh;
    public bool profile_firstMeshWasDeferred;
#endif
}
