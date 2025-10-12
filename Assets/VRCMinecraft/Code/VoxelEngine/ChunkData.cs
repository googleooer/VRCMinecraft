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
    
    // --- Persistent Decompression Cache (OPTIMIZATION) ---
    public byte[] _cachedDecompressedData;
    public bool _decompCacheValid = false;
    
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
#endif
} 