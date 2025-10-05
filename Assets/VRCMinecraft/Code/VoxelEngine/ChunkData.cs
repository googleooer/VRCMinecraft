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
    public MeshCollider meshCollider;

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
    public float time_DataPrep, time_NeighborCache, time_MainLoop, time_ApplyOpaque, time_ApplyTransparent, time_ApplyCutout, time_ApplyCollision;
    public float timer_start_stage; // Helper to carry state between steps
    public int mesh_step_count;

    // --- Extra-detailed profiling ---
    public float time_SentinelEnsure, time_DecompressNeighbors, time_SentinelBuild;
    public float time_AxisY, time_AxisZ, time_AxisX;
    public int boundaryChecksY, boundaryChecksZ, boundaryChecksX;
    public int shouldDrawTests, shouldDrawTrue;
    public int facesOpaque, facesTransparent, facesCutout, facesTotal;
    public int sentinelInteriorCopied, sentinelBorderCopied;
#endif
} 