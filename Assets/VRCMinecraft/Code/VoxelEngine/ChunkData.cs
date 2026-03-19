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
    public MeshCollider meshCollider; // Player collision (solid blocks only)
    public MeshCollider selectionCollider; // Selection/focus collision (includes focusable non-solid blocks)

    // --- Data & State ---
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
    public bool interactionMeshPriority = false;
    public bool pendingNeighborMeshRebuild = false;
    public bool pendingLightingFinalize = false;

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
    public bool _isAllAir = false;
    public bool _hasWaterBlocks = false;
    public bool _hasEmissiveBlocks = false;
    public bool _hasTorchBlocks = false;
    public byte _chunkGlobalMinY = 255, _chunkGlobalMaxY = 0;
    public byte _chunkGlobalMinX = 255, _chunkGlobalMaxX = 0;
    public byte _chunkGlobalMinZ = 255, _chunkGlobalMaxZ = 0;
    public byte[] torchMountData;

    // --- GPU Face Extraction State ---
    public int _gpuFaceSlotIndex = -1;
    public int _gpuFaceReadbackQueueSlot = -1;
    public bool _gpuMeshPending = false;
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

    // --- Time-slicing State (for meshing) ---
    public int _greedyAxis;
    public int _greedyU, _greedyV;
    public int _lastMeshStepFrame = 0;

    // --- Meshing Buffers ---
    // These could be pooled by McWorld to reduce allocations
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
#endif
}
