using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using System.Text;

/// <summary>
/// This version of McChunk features a fully time-sliced generation and meshing pipeline.
///
/// 1.  GREEDY MESHING: A more advanced algorithm that iterates boundaries, not voxels.
/// 2.  MULTI-LAYER RLE: Advanced Run-Length Encoding compresses chunk data for
///     maximum memory efficiency.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McChunk : UdonSharpBehaviour
{
    [HideInInspector] public McWorld world;
    [HideInInspector] public int chunkSizeXZ = 16;
    [HideInInspector] public int chunkSizeY = 16;
    [HideInInspector] public int chunkX_world, chunkY_world, chunkZ_world;
    [HideInInspector] public bool isDataReady = false; 
    [HideInInspector] public bool isSingleOpaqueSolid = false;
    
    [Header("Component References")]
    public MeshFilter opaqueMeshFilter;
    public MeshFilter transparentMeshFilter;
    public MeshFilter cutoutMeshFilter;
    public MeshCollider meshCollider;

    // --- Private State ---
    private object _chunkData;
    private bool _isCompressed;
    private ushort[] _localBlockData; // Temporary, for meshing
    private int _chunkDataSize;
    [HideInInspector] public bool isBuildingMesh = false;
    private bool isGeneratingData = false;
    private int generationStep = 0;
    
    // --- Meshing Buffers ---
    private const int MAX_VERTS = 12288;
    private const int MAX_TRIS = (MAX_VERTS / 4) * 6;
    private Vector3[] _opaqueVertices; private int[] _opaqueTriangles; private Vector3[] _opaqueUVs; private Vector3[] _opaqueNormals;
    private int _opaqueVertexCount; private int _opaqueTriangleCount;
    private Vector3[] _transparentVertices; private int[] _transparentTriangles; private Vector3[] _transparentUVs; private Vector3[] _transparentNormals;
    private int _transparentVertexCount; private int _transparentTriangleCount;
    private Vector3[] _cutoutVertices; private int[] _cutoutTriangles; private Vector3[] _cutoutUVs; private Vector3[] _cutoutNormals;
    private int _cutoutVertexCount; private int _cutoutTriangleCount;
    private Vector3[] _collisionVertices; private int[] _collisionTriangles;
    private int _collisionVertexCount; private int _collisionTriangleCount;
    
    // --- Neighbor Caches ---
    private ushort[] neighborCache_PX; private ushort[] neighborCache_NX;
    private ushort[] neighborCache_PY; private ushort[] neighborCache_NY;
    private ushort[] neighborCache_PZ; private ushort[] neighborCache_NZ;
    
    // --- Time-slicing State & Config ---
    private int _voxelsPerMeshStep = 256; // Now represents columns/rows per step
    private int _greedyAxis;
    private int _greedyU, _greedyV;
    
    private McTerrainGenerator _terrainGenRef;
    
    // --- Constants ---
    private const int FACE_INDEX_SIDE = 0; private const int FACE_INDEX_TOP = 2; private const int FACE_INDEX_BOTTOM = 3;
    private readonly Vector3[] FaceVertices_North = { new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 1) };
    private readonly Vector3[] FaceVertices_East = { new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1) };
    private readonly Vector3[] FaceVertices_South = { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) };
    private readonly Vector3[] FaceVertices_West = { new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0), new Vector3(0, 0, 0) };
    private readonly Vector3[] FaceVertices_Up = { new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0) };
    private readonly Vector3[] FaceVertices_Down = { new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 0) };
    private readonly Vector3 Normal_North = Vector3.forward; private readonly Vector3 Normal_East = Vector3.right; private readonly Vector3 Normal_South = Vector3.back;
    private readonly Vector3 Normal_West = Vector3.left; private readonly Vector3 Normal_Up = Vector3.up; private readonly Vector3 Normal_Down = Vector3.down;
    private readonly int[] neighbor_dx_offsets = { 1, -1, 0,  0, 0,  0 };
    private readonly int[] neighbor_dy_offsets = { 0,  0, 1, -1, 0,  0 };
    private readonly int[] neighbor_dz_offsets = { 0,  0, 0,  0, 1, -1 };
    private const ushort RLE_TYPE_1D = 0;
    private const ushort RLE_TYPE_2D_XZ_PLANE = 1;
    private const ushort RLE_TYPE_3D_FULL_CHUNK = 2;

    private const int COLLIDER_DEFER_FRAMES = 2;
    private int _lastMeshStepFrame = 0;


    #if UNITY_EDITOR
    [Header("Debugging")]
    public bool enableVerboseLogging = false;
    private StringBuilder logBuilder;
    private float time_DataPrep, time_NeighborCache, time_MainLoop, time_ApplyOpaque, time_ApplyTransparent, time_ApplyCutout, time_ApplyCollision, time_TotalBuild;
    #endif
    
    void Start()
    {
        #if UNITY_EDITOR
        if (logBuilder == null) logBuilder = new StringBuilder(2048);
        #endif
    }
    
    void Update()
    {
        if (isBuildingMesh && Time.frameCount - _lastMeshStepFrame > 5)
        {
            BuildMeshStep();
        }
    }
    
    public void Initialize(McWorld worldRef, McTerrainGenerator terrainGen, int cX, int cY, int cZ, int noisePointsPerStep, int voxelsPerMeshStep, int voxelsPerTerrainStep)
    {
        this.world = worldRef;
        this.chunkSizeXZ = world.chunkSizeXZ;
        this.chunkSizeY = world.chunkSizeY;
        this.chunkX_world = cX;
        this.chunkY_world = cY;
        this.chunkZ_world = cZ;
        this._terrainGenRef = terrainGen;
        
        this._voxelsPerMeshStep = Mathf.Max(1, voxelsPerMeshStep / 16); // Adjust step size for greedy mesher
        
        _chunkDataSize = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
        _localBlockData = new ushort[_chunkDataSize];
        _opaqueVertices = new Vector3[MAX_VERTS]; _opaqueTriangles = new int[MAX_TRIS]; _opaqueUVs = new Vector3[MAX_VERTS]; _opaqueNormals = new Vector3[MAX_VERTS];
        _transparentVertices = new Vector3[MAX_VERTS]; _transparentTriangles = new int[MAX_TRIS]; _transparentUVs = new Vector3[MAX_VERTS]; _transparentNormals = new Vector3[MAX_VERTS];
        _cutoutVertices = new Vector3[MAX_VERTS]; _cutoutTriangles = new int[MAX_TRIS]; _cutoutUVs = new Vector3[MAX_VERTS]; _cutoutNormals = new Vector3[MAX_VERTS];
        _collisionVertices = new Vector3[MAX_VERTS * 3]; _collisionTriangles = new int[MAX_TRIS * 3];
        
        StartDataGeneration();
    }
    
    private void StartDataGeneration()
    {
        isGeneratingData = true;
        generationStep = 0;
        
        _terrainGenRef.StartChunkGeneration(chunkX_world, chunkY_world, chunkZ_world);
    }
    
    public bool StepDataGeneration()
    {
        if (!isGeneratingData) return true;
        
        ushort[] generatedData;
        bool isComplete = _terrainGenRef.StepChunkGeneration(out generatedData);
        
        if (isComplete)
        {
            ushort[] compressedRLEData = CompressChunkMultiLayerRLE(generatedData);
            float compressionRatio = (float)compressedRLEData.Length / generatedData.Length;
                
            #if UNITY_EDITOR
            if (enableVerboseLogging) {
                Debug.Log($"[McChunk ({chunkX_world},{chunkY_world},{chunkZ_world})] RLE Compression complete. Ratio: {compressionRatio:P2} (Original: {generatedData.Length*2} bytes, Compressed: {compressedRLEData.Length*2} bytes)");
            }
            #endif

            if (compressionRatio < 1.0f) {
                _chunkData = compressedRLEData;
                _isCompressed = true;
            } else {
                _chunkData = generatedData;
                _isCompressed = false;
            }
            
            if (_isCompressed) {
                ushort[] data = (ushort[])_chunkData;
                if(data.Length > 0 && data[0] == RLE_TYPE_3D_FULL_CHUNK)
                {
                    ushort blockValue = data[1];
                    byte blockID = (byte)(blockValue & 0xFF);
                    bool isSolid = world.blockTypeManager.GetBlockIsSolid(blockID);
                    var visibility = world.blockTypeManager.GetBlockVisibilityType(blockID);
                    if(isSolid && visibility == BlockVisibilityType.Opaque) {
                        isSingleOpaqueSolid = true;
                    }
                }
            }
                
            isDataReady = true;
            isGeneratingData = false;
            world.TriggerNeighborMeshRebuilds(this);
            
            return true;
        }
        
        return false;
    }

    public bool IsGeneratingData()
    {
        return isGeneratingData;
    }

    
    public void BuildMesh()
    {
        if (isBuildingMesh) return;
        if (world == null || _chunkData == null) return;
        isBuildingMesh = true;

        #if UNITY_EDITOR
        time_TotalBuild = 0; time_MainLoop = 0;
        float timer_start_total = 0f; float timer_start_stage = 0f;
        if (enableVerboseLogging && logBuilder != null)
        {
            logBuilder.Clear(); logBuilder.AppendLine($"--- BuildMesh for Chunk ({chunkX_world},{chunkY_world},{chunkZ_world}) ---");
            timer_start_total = Time.realtimeSinceStartup; timer_start_stage = Time.realtimeSinceStartup;
        }
        #endif

        ClearAllBuffers();

        if (isSingleOpaqueSolid)
        {
            bool isFullyOccluded = true;
            for (int i = 0; i < 6; i++)
            {
                if (!world.IsChunkSingleOpaqueSolid(chunkX_world + neighbor_dx_offsets[i], chunkY_world + neighbor_dy_offsets[i], chunkZ_world + neighbor_dz_offsets[i]))
                {
                    isFullyOccluded = false; break;
                }
            }
            if (isFullyOccluded) { ApplyEmptyMesh(); isBuildingMesh = false; return; }
        }

        if (_isCompressed)
        {
            ushort[] data = (ushort[])_chunkData;
            if (data.Length > 0 && data[0] == RLE_TYPE_3D_FULL_CHUNK && data[1] == 0)
            {
                ApplyEmptyMesh(); isBuildingMesh = false; return;
            }
        }

        CacheAllNeighbors();
        if (_isCompressed)
        {
            _localBlockData = DecompressChunkMultiLayerRLE((ushort[])_chunkData);
        }
        else
        {
            System.Array.Copy((ushort[])_chunkData, _localBlockData, _chunkDataSize);
        }

        _greedyAxis = 0;
        _greedyU = 0;
        _greedyV = 0;

        #if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            time_NeighborCache = (Time.realtimeSinceStartup - timer_start_stage) * 1000f;
            timer_start_stage = Time.realtimeSinceStartup;
            time_DataPrep = (Time.realtimeSinceStartup - timer_start_stage) * 1000f;
        }
        #endif

        SendCustomEventDelayedFrames(nameof(BuildMeshStep), 1);
    }
    
    public void BuildMeshStep()
    {
        if (!isBuildingMesh) return;
        _lastMeshStepFrame = Time.frameCount;

        #if UNITY_EDITOR
        float timer_start_stage = 0f;
        if (enableVerboseLogging) timer_start_stage = Time.realtimeSinceStartup;
        #endif

        int steps_taken = 0;
        while (steps_taken < _voxelsPerMeshStep && _greedyAxis <= 2)
        {
            int u = _greedyU;
            int v = _greedyV;

            // Process one column/row along the current axis
            if (_greedyAxis == 0) // Y Axis (column is along Y)
            {
                for (int j = 0; j < chunkSizeY + 1; j++)
                {
                    ushort d1 = GetBlockForGreedyMesher(u, j - 1, v);
                    ushort d2 = GetBlockForGreedyMesher(u, j, v);
                    ProcessBoundary(d1, d2, new Vector3(u, j - 1, v), new Vector3(u, j, v), 0);
                }
            }
            else if (_greedyAxis == 1) // Z Axis (row is along Z)
            {
                for (int k = 0; k < chunkSizeXZ + 1; k++)
                {
                    ushort d1 = GetBlockForGreedyMesher(u, v, k - 1);
                    ushort d2 = GetBlockForGreedyMesher(u, v, k);
                    ProcessBoundary(d1, d2, new Vector3(u, v, k - 1), new Vector3(u, v, k), 1);
                }
            }
            else // X Axis (row is along X)
            {
                for (int i = 0; i < chunkSizeXZ + 1; i++)
                {
                    ushort d1 = GetBlockForGreedyMesher(i - 1, u, v);
                    ushort d2 = GetBlockForGreedyMesher(i, u, v);
                    ProcessBoundary(d1, d2, new Vector3(i - 1, u, v), new Vector3(i, u, v), 2);
                }
            }

            steps_taken++;

            // Advance greedy mesher state
            _greedyU++;
            int limitU = (_greedyAxis == 1 || _greedyAxis == 2) ? chunkSizeY : chunkSizeXZ;
            int limitV = chunkSizeXZ;

            if (_greedyU >= limitU)
            {
                _greedyU = 0;
                _greedyV++;
                if (_greedyV >= limitV)
                {
                    _greedyV = 0;
                    _greedyAxis++;
                }
            }
        }

        #if UNITY_EDITOR
        if (enableVerboseLogging) time_MainLoop += (Time.realtimeSinceStartup - timer_start_stage) * 1000f;
        #endif

        if (_greedyAxis > 2)
        {
            ApplyAllMeshData();
            _localBlockData = null; // Free up memory
            isBuildingMesh = false;

            #if UNITY_EDITOR
            if (enableVerboseLogging)
            {
                logBuilder.AppendLine("--- Timings ---");
                logBuilder.AppendLine($"1. Neighbor Caching: {time_NeighborCache:F3} ms");
                logBuilder.AppendLine($"2. Data Prep: {time_DataPrep:F3} ms");
                logBuilder.AppendLine($"3. Main Loop (Total): {time_MainLoop:F3} ms");
                logBuilder.AppendLine($"4. Apply Opaque: {time_ApplyOpaque:F3} ms ({_opaqueVertexCount} verts)");
                logBuilder.AppendLine($"5. Apply Transparent: {time_ApplyTransparent:F3} ms ({_transparentVertexCount} verts)");
                logBuilder.AppendLine($"6. Apply Cutout: {time_ApplyCutout:F3} ms ({_cutoutVertexCount} verts)");
                logBuilder.AppendLine($"7. Apply Collision: {time_ApplyCollision:F3} ms ({_collisionVertexCount} verts)");
                Debug.Log(logBuilder.ToString());
            }
            #endif

            SendCustomEventDelayedFrames(nameof(ApplyColliderDeferred), COLLIDER_DEFER_FRAMES);
        }
        else
        {
            SendCustomEventDelayedFrames(nameof(BuildMeshStep), 1);
        }
    }
    
    private void ProcessBoundary(ushort data1, ushort data2, Vector3 pos1, Vector3 pos2, int axis)
    {
        byte id1 = (byte)(data1 & 0xFF);
        byte id2 = (byte)(data2 & 0xFF);

        if (id1 == id2) return;

        BlockVisibilityType vis1 = world.blockTypeManager.GetBlockVisibilityType(id1);
        BlockVisibilityType vis2 = world.blockTypeManager.GetBlockVisibilityType(id2);

        if (ShouldDrawFace(id1, vis1, data2))
        {
            if (axis == 0) AddFace(FaceVertices_Up, Normal_Up, pos1, id1, vis1, FACE_INDEX_TOP);
            else if (axis == 1) AddFace(FaceVertices_North, Normal_North, pos1, id1, vis1, FACE_INDEX_SIDE);
            else AddFace(FaceVertices_East, Normal_East, pos1, id1, vis1, FACE_INDEX_SIDE);
        }

        if (ShouldDrawFace(id2, vis2, data1))
        {
            if (axis == 0) AddFace(FaceVertices_Down, Normal_Down, pos2, id2, vis2, FACE_INDEX_BOTTOM);
            else if (axis == 1) AddFace(FaceVertices_South, Normal_South, pos2, id2, vis2, FACE_INDEX_SIDE);
            else AddFace(FaceVertices_West, Normal_West, pos2, id2, vis2, FACE_INDEX_SIDE);
        }
    }

    private ushort GetBlockForGreedyMesher(int x, int y, int z)
    {
        if (x < 0) return (neighborCache_NX != null) ? neighborCache_NX[y * chunkSizeXZ + z] : (ushort)0;
        if (x >= chunkSizeXZ) return (neighborCache_PX != null) ? neighborCache_PX[y * chunkSizeXZ + z] : (ushort)0;
        if (y < 0) return (neighborCache_NY != null) ? neighborCache_NY[z * chunkSizeXZ + x] : (ushort)0;
        if (y >= chunkSizeY) return (neighborCache_PY != null) ? neighborCache_PY[z * chunkSizeXZ + x] : (ushort)0;
        if (z < 0) return (neighborCache_NZ != null) ? neighborCache_NZ[y * chunkSizeXZ + x] : (ushort)0;
        if (z >= chunkSizeXZ) return (neighborCache_PZ != null) ? neighborCache_PZ[y * chunkSizeXZ + x] : (ushort)0;

        return _localBlockData[y * chunkSizeXZ * chunkSizeXZ + z * chunkSizeXZ + x];
    }
    
    private void AddFace(Vector3[] faceVertices, Vector3 faceNormal, Vector3 blockPos, byte blockID, BlockVisibilityType visibility, int faceIndex)
    {
        Vector3[] targetVertices; int[] targetTriangles; Vector3[] targetUVs; Vector3[] targetNormals;
        int currentVertexCount; int currentTriangleCount;

        if (visibility == BlockVisibilityType.Opaque) {
            if (_opaqueVertexCount + 4 > MAX_VERTS) return;
            targetVertices = _opaqueVertices; targetTriangles = _opaqueTriangles; targetUVs = _opaqueUVs; targetNormals = _opaqueNormals;
            currentVertexCount = _opaqueVertexCount; currentTriangleCount = _opaqueTriangleCount;
        } else if (visibility == BlockVisibilityType.Transparent) {
            if (_transparentVertexCount + 4 > MAX_VERTS) return;
            targetVertices = _transparentVertices; targetTriangles = _transparentTriangles; targetUVs = _transparentUVs; targetNormals = _transparentNormals;
            currentVertexCount = _transparentVertexCount; currentTriangleCount = _transparentTriangleCount;
        } else {
            if (_cutoutVertexCount + 4 > MAX_VERTS) return;
            targetVertices = _cutoutVertices; targetTriangles = _cutoutTriangles; targetUVs = _cutoutUVs; targetNormals = _cutoutNormals;
            currentVertexCount = _cutoutVertexCount; currentTriangleCount = _cutoutTriangleCount;
        }
        
        float bx = blockPos.x, by = blockPos.y, bz = blockPos.z;
        targetVertices[currentVertexCount + 0] = new Vector3(bx + faceVertices[0].x, by + faceVertices[0].y, bz + faceVertices[0].z);
        targetVertices[currentVertexCount + 1] = new Vector3(bx + faceVertices[1].x, by + faceVertices[1].y, bz + faceVertices[1].z);
        targetVertices[currentVertexCount + 2] = new Vector3(bx + faceVertices[2].x, by + faceVertices[2].y, bz + faceVertices[2].z);
        targetVertices[currentVertexCount + 3] = new Vector3(bx + faceVertices[3].x, by + faceVertices[3].y, bz + faceVertices[3].z);
        for (int i=0; i<4; i++) targetNormals[currentVertexCount + i] = faceNormal;
        float textureSlice = world.blockTypeManager.GetFinalBlockTextureSlice(blockID, faceIndex);
        targetUVs[currentVertexCount + 0] = new Vector3(0, 0, textureSlice); targetUVs[currentVertexCount + 1] = new Vector3(0, 1, textureSlice);
        targetUVs[currentVertexCount + 2] = new Vector3(1, 1, textureSlice); targetUVs[currentVertexCount + 3] = new Vector3(1, 0, textureSlice);
        targetTriangles[currentTriangleCount + 0] = currentVertexCount; targetTriangles[currentTriangleCount + 1] = currentVertexCount + 1;
        targetTriangles[currentTriangleCount + 2] = currentVertexCount + 2; targetTriangles[currentTriangleCount + 3] = currentVertexCount;
        targetTriangles[currentTriangleCount + 4] = currentVertexCount + 2; targetTriangles[currentTriangleCount + 5] = currentVertexCount + 3;
        if (visibility == BlockVisibilityType.Opaque) { _opaqueVertexCount += 4; _opaqueTriangleCount += 6; }
        else if (visibility == BlockVisibilityType.Transparent) { _transparentVertexCount += 4; _transparentTriangleCount += 6; }
        else { _cutoutVertexCount += 4; _cutoutTriangleCount += 6; }
        if (visibility != BlockVisibilityType.Transparent && _collisionVertexCount < MAX_VERTS * 3 - 4) {
            _collisionVertices[_collisionVertexCount + 0] = targetVertices[currentVertexCount + 0];
            _collisionVertices[_collisionVertexCount + 1] = targetVertices[currentVertexCount + 1];
            _collisionVertices[_collisionVertexCount + 2] = targetVertices[currentVertexCount + 2];
            _collisionVertices[_collisionVertexCount + 3] = targetVertices[currentVertexCount + 3];
            _collisionTriangles[_collisionTriangleCount++] = _collisionVertexCount; _collisionTriangles[_collisionTriangleCount++] = _collisionVertexCount + 1;
            _collisionTriangles[_collisionTriangleCount++] = _collisionVertexCount + 2; _collisionTriangles[_collisionTriangleCount++] = _collisionVertexCount;
            _collisionTriangles[_collisionTriangleCount++] = _collisionVertexCount + 2; _collisionTriangles[_collisionTriangleCount++] = _collisionVertexCount + 3;
            _collisionVertexCount += 4;
        }
    }
    
    private void ApplyEmptyMesh()
    {
        ApplyDataToMesh(opaqueMeshFilter, _opaqueVertices, _opaqueTriangles, _opaqueUVs, _opaqueNormals, 0, 0);
        ApplyDataToMesh(transparentMeshFilter, _transparentVertices, _transparentTriangles, _transparentUVs, _transparentNormals, 0, 0);
        ApplyDataToMesh(cutoutMeshFilter, _cutoutVertices, _cutoutTriangles, _cutoutUVs, _cutoutNormals, 0, 0);
        ApplyDataToCollider();
    }
    
    private void CacheAllNeighbors()
    {
        neighborCache_PX = GetNeighborEdgeCache(world.GetChunkAt(chunkX_world + 1, chunkY_world, chunkZ_world), 0);
        neighborCache_NX = GetNeighborEdgeCache(world.GetChunkAt(chunkX_world - 1, chunkY_world, chunkZ_world), 1);
        neighborCache_PY = GetNeighborEdgeCache(world.GetChunkAt(chunkX_world, chunkY_world + 1, chunkZ_world), 2);
        neighborCache_NY = GetNeighborEdgeCache(world.GetChunkAt(chunkX_world, chunkY_world - 1, chunkZ_world), 3);
        neighborCache_PZ = GetNeighborEdgeCache(world.GetChunkAt(chunkX_world, chunkY_world, chunkZ_world + 1), 4);
        neighborCache_NZ = GetNeighborEdgeCache(world.GetChunkAt(chunkX_world, chunkY_world, chunkZ_world - 1), 5);
    }

    private ushort[] GetNeighborEdgeCache(McChunk neighbor, int edgeType)
    {
        if (neighbor == null || !neighbor.isDataReady) return null;

        int n_chunkSizeXZ = neighbor.chunkSizeXZ;
        int n_chunkSizeY = neighbor.chunkSizeY;
        
        int edgeSize;
        if (edgeType <= 1) edgeSize = n_chunkSizeY * n_chunkSizeXZ;      // X edge: YZ plane
        else if (edgeType <= 3) edgeSize = n_chunkSizeXZ * n_chunkSizeXZ; // Y edge: XZ plane
        else edgeSize = n_chunkSizeY * n_chunkSizeXZ;      // Z edge: XY plane
            
        ushort[] edgeCache = new ushort[edgeSize];
        
        if (neighbor.isSingleOpaqueSolid)
        {
            ushort blockValue = neighbor.GetHomogeneousBlockValue();
            for (int i = 0; i < edgeSize; i++) edgeCache[i] = blockValue;
            return edgeCache;
        }
        
        if (!neighbor._isCompressed)
        {
            ushort[] fullData = (ushort[])neighbor._chunkData;
            int index = 0;
            switch (edgeType)
            {
                case 0: for (int y=0; y<n_chunkSizeY; y++) for (int z=0; z<n_chunkSizeXZ; z++) edgeCache[index++] = fullData[y*n_chunkSizeXZ*n_chunkSizeXZ + z*n_chunkSizeXZ + 0]; break;
                case 1: for (int y=0; y<n_chunkSizeY; y++) for (int z=0; z<n_chunkSizeXZ; z++) edgeCache[index++] = fullData[y*n_chunkSizeXZ*n_chunkSizeXZ + z*n_chunkSizeXZ + (n_chunkSizeXZ-1)]; break;
                case 2: for (int z=0; z<n_chunkSizeXZ; z++) for (int x=0; x<n_chunkSizeXZ; x++) edgeCache[index++] = fullData[0*n_chunkSizeXZ*n_chunkSizeXZ + z*n_chunkSizeXZ + x]; break;
                case 3: for (int z=0; z<n_chunkSizeXZ; z++) for (int x=0; x<n_chunkSizeXZ; x++) edgeCache[index++] = fullData[(n_chunkSizeY-1)*n_chunkSizeXZ*n_chunkSizeXZ + z*n_chunkSizeXZ + x]; break;
                case 4: for (int y=0; y<n_chunkSizeY; y++) for (int x=0; x<n_chunkSizeXZ; x++) edgeCache[index++] = fullData[y*n_chunkSizeXZ*n_chunkSizeXZ + 0*n_chunkSizeXZ + x]; break;
                case 5: for (int y=0; y<n_chunkSizeY; y++) for (int x=0; x<n_chunkSizeXZ; x++) edgeCache[index++] = fullData[y*n_chunkSizeXZ*n_chunkSizeXZ + (n_chunkSizeXZ-1)*n_chunkSizeXZ + x]; break;
            }
            return edgeCache;
        }

        ushort[] rleData = (ushort[])neighbor._chunkData;
        if (rleData == null || rleData.Length == 0) return edgeCache;

        if (rleData[0] == RLE_TYPE_3D_FULL_CHUNK)
        {
            ushort blockValue = rleData[1];
            for (int i = 0; i < edgeSize; i++) edgeCache[i] = blockValue;
            return edgeCache;
        }

        int planeSize = n_chunkSizeXZ * n_chunkSizeXZ;
        ushort[] planeBuffer = new ushort[planeSize];
        int rleIndex = 0;
        
        if (edgeType == 2 || edgeType == 3) // Y-edge (XZ plane), most efficient case
        {
            int targetY = (edgeType == 2) ? 0 : n_chunkSizeY - 1;
            for (int y = 0; y < targetY; y++) SkipNextPlaneFromRLE(neighbor, rleData, ref rleIndex);
            DecompressNextPlaneFromRLE(neighbor, rleData, ref rleIndex, planeBuffer);
            System.Array.Copy(planeBuffer, edgeCache, planeSize);
        }
        else // X or Z edge, requires iterating all Y planes
        {
            for (int y = 0; y < n_chunkSizeY; y++)
            {
                DecompressNextPlaneFromRLE(neighbor, rleData, ref rleIndex, planeBuffer);
                int edgePlaneIndex = (edgeType <=1) ? (y * n_chunkSizeXZ) : (y * n_chunkSizeY);
                switch (edgeType)
                {
                    case 0: // +X neighbor, copy Z-column at X=0
                        for (int z = 0; z < n_chunkSizeXZ; z++) edgeCache[edgePlaneIndex + z] = planeBuffer[z * n_chunkSizeXZ + 0];
                        break;
                    case 1: // -X neighbor, copy Z-column at X=max
                        for (int z = 0; z < n_chunkSizeXZ; z++) edgeCache[edgePlaneIndex + z] = planeBuffer[z * n_chunkSizeXZ + (n_chunkSizeXZ - 1)];
                        break;
                    case 4: // +Z neighbor, copy X-row at Z=0
                        for (int x = 0; x < n_chunkSizeXZ; x++) edgeCache[y * n_chunkSizeXZ + x] = planeBuffer[0 * n_chunkSizeXZ + x];
                        break;
                    case 5: // -Z neighbor, copy X-row at Z=max
                        for (int x = 0; x < n_chunkSizeXZ; x++) edgeCache[y * n_chunkSizeXZ + x] = planeBuffer[(n_chunkSizeXZ - 1) * n_chunkSizeXZ + x];
                        break;
                }
            }
        }
        return edgeCache;
    }

    private void DecompressNextPlaneFromRLE(McChunk chunk, ushort[] rleData, ref int rleIndex, ushort[] planeBuffer)
    {
        int planeSize = chunk.chunkSizeXZ * chunk.chunkSizeXZ;
        if (rleIndex >= rleData.Length) return;

        ushort runType = rleData[rleIndex];
        if (runType == RLE_TYPE_2D_XZ_PLANE)
        {
            rleIndex++;
            ushort blockValue = rleData[rleIndex++];
            for (int i = 0; i < planeSize; i++) planeBuffer[i] = blockValue;
        }
        else // RLE_TYPE_1D
        {
            int planeDecompressed = 0;
            while (planeDecompressed < planeSize)
            {
                rleIndex++;
                ushort runCount = rleData[rleIndex++];
                ushort blockValue = rleData[rleIndex++];
                for (int i = 0; i < runCount; i++)
                {
                    if (planeDecompressed + i < planeSize) planeBuffer[planeDecompressed + i] = blockValue;
                }
                planeDecompressed += runCount;
            }
        }
    }

    private void SkipNextPlaneFromRLE(McChunk chunk, ushort[] rleData, ref int rleIndex)
    {
        int planeSize = chunk.chunkSizeXZ * chunk.chunkSizeXZ;
        if (rleIndex >= rleData.Length) return;

        ushort runType = rleData[rleIndex];
        if (runType == RLE_TYPE_2D_XZ_PLANE)
        {
            rleIndex += 2;
        }
        else
        {
            int planeDecompressed = 0;
            while (planeDecompressed < planeSize)
            {
                rleIndex++;
                ushort runCount = rleData[rleIndex++];
                rleIndex++;
                planeDecompressed += runCount;
            }
        }
    }

    public ushort GetHomogeneousBlockValue()
    {
        if (isSingleOpaqueSolid) { return ((ushort[])_chunkData)[1]; }
        return 0;
    }
    
    public ushort[] GetDecompressedData()
    {
        if (_isCompressed) { return DecompressChunkMultiLayerRLE((ushort[])_chunkData); }
        return (ushort[])_chunkData;
    }
    
    private void ClearAllBuffers()
    {
        _opaqueVertexCount = 0; _opaqueTriangleCount = 0;
        _transparentVertexCount = 0; _transparentTriangleCount = 0;
        _cutoutVertexCount = 0; _cutoutTriangleCount = 0;
        _collisionVertexCount = 0; _collisionTriangleCount = 0;
    }
    
    private bool ShouldDrawFace(byte selfID, BlockVisibilityType selfVisibility, ushort neighborData)
    {
        byte neighborID = (byte)(neighborData & 0xFF);
        if (neighborID == 0) return true;
        BlockVisibilityType neighborVisibility = world.blockTypeManager.GetBlockVisibilityType(neighborID);
        if (selfVisibility == BlockVisibilityType.Invisible) return false;
        BlockCullingType selfCulling = world.blockTypeManager.GetBlockCullingType(selfID);
        
        switch (selfCulling)
        {
            case BlockCullingType.NoCull: return true;
            case BlockCullingType.CullSelf: return neighborID != selfID;
            case BlockCullingType.CullSelfAndOpaque: return !(neighborID == selfID || neighborVisibility == BlockVisibilityType.Opaque);
            case BlockCullingType.CullSelfAndCutout: return !(neighborID == selfID || neighborVisibility == BlockVisibilityType.Cutout);
            case BlockCullingType.CullSelfAndTransparent: return !(neighborID == selfID || neighborVisibility == BlockVisibilityType.Transparent);
            case BlockCullingType.CullAll: return false;
            default: return !(neighborVisibility == BlockVisibilityType.Opaque);
        }
    }
    
    private void ApplyAllMeshData()
    {
        #if UNITY_EDITOR
        if(enableVerboseLogging) 
        {
            float timer_start = Time.realtimeSinceStartup;
            ApplyDataToMesh(opaqueMeshFilter, _opaqueVertices, _opaqueTriangles, _opaqueUVs, _opaqueNormals, _opaqueVertexCount, _opaqueTriangleCount);
            time_ApplyOpaque = (Time.realtimeSinceStartup - timer_start) * 1000f;

            timer_start = Time.realtimeSinceStartup;
            ApplyDataToMesh(transparentMeshFilter, _transparentVertices, _transparentTriangles, _transparentUVs, _transparentNormals, _transparentVertexCount, _transparentTriangleCount);
            time_ApplyTransparent = (Time.realtimeSinceStartup - timer_start) * 1000f;

            timer_start = Time.realtimeSinceStartup;
            ApplyDataToMesh(cutoutMeshFilter, _cutoutVertices, _cutoutTriangles, _cutoutUVs, _cutoutNormals, _cutoutVertexCount, _cutoutTriangleCount);
            time_ApplyCutout = (Time.realtimeSinceStartup - timer_start) * 1000f;
        }
        else
        {
            ApplyDataToMesh(opaqueMeshFilter, _opaqueVertices, _opaqueTriangles, _opaqueUVs, _opaqueNormals, _opaqueVertexCount, _opaqueTriangleCount);
            ApplyDataToMesh(transparentMeshFilter, _transparentVertices, _transparentTriangles, _transparentUVs, _transparentNormals, _transparentVertexCount, _transparentTriangleCount);
            ApplyDataToMesh(cutoutMeshFilter, _cutoutVertices, _cutoutTriangles, _cutoutUVs, _cutoutNormals, _cutoutVertexCount, _cutoutTriangleCount);
        }
        #else
        ApplyDataToMesh(opaqueMeshFilter, _opaqueVertices, _opaqueTriangles, _opaqueUVs, _opaqueNormals, _opaqueVertexCount, _opaqueTriangleCount);
        ApplyDataToMesh(transparentMeshFilter, _transparentVertices, _transparentTriangles, _transparentUVs, _transparentNormals, _transparentVertexCount, _transparentTriangleCount);
        ApplyDataToMesh(cutoutMeshFilter, _cutoutVertices, _cutoutTriangles, _cutoutUVs, _cutoutNormals, _cutoutVertexCount, _cutoutTriangleCount);
        #endif
    }

    private void ApplyDataToMesh(MeshFilter mf, Vector3[] vertices, int[] triangles, Vector3[] uvs, Vector3[] normals, int vertexCount, int triangleCount)
    {
        if (mf == null) return;
        Mesh m = mf.sharedMesh;
        if (m == null) { m = new Mesh(); m.name = $"ChunkMesh_{mf.gameObject.name}"; mf.sharedMesh = m; }
        m.Clear();
        if (vertexCount == 0) { mf.gameObject.SetActive(false); return; }
        mf.gameObject.SetActive(true);
        Vector3[] finalVertices = new Vector3[vertexCount]; System.Array.Copy(vertices, finalVertices, vertexCount);
        int[] finalTriangles = new int[triangleCount]; System.Array.Copy(triangles, finalTriangles, triangleCount);
        Vector3[] finalNormals = new Vector3[vertexCount]; System.Array.Copy(normals, finalNormals, vertexCount);
        Vector3[] finalUVs = new Vector3[vertexCount]; System.Array.Copy(uvs, finalUVs, vertexCount);
        m.vertices = finalVertices; m.triangles = finalTriangles; m.normals = finalNormals;
        m.SetUVs(0, finalUVs);
        m.RecalculateBounds();
    }

    private void ApplyDataToCollider()
    {
        if (meshCollider == null) return;
        Mesh colMesh = meshCollider.sharedMesh;
        if (colMesh == null) { colMesh = new Mesh(); colMesh.name = $"ChunkCollisionMesh_{gameObject.name}"; }
        colMesh.Clear();
        if (_collisionVertexCount == 0) { meshCollider.sharedMesh = null; meshCollider.enabled = false; return; }
        meshCollider.enabled = true;
        Vector3[] finalVertices = new Vector3[_collisionVertexCount]; System.Array.Copy(_collisionVertices, finalVertices, _collisionVertexCount);
        int[] finalTriangles = new int[_collisionTriangleCount]; System.Array.Copy(_collisionTriangles, finalTriangles, _collisionTriangleCount);
        colMesh.vertices = finalVertices; colMesh.triangles = finalTriangles;
        meshCollider.sharedMesh = colMesh;
    }

    public ushort GetBlockLocal(int x, int y, int z)
    {
        if (x < 0 || x >= chunkSizeXZ || y < 0 || y >= chunkSizeY || z < 0 || z >= chunkSizeXZ) return 0;
        
        if (_chunkData == null) return 0;

        if (_isCompressed) {
            return DecompressBlockFromMultiLayerRLE((ushort[])_chunkData, x, y, z);
        }
        
        int targetIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        return ((ushort[])_chunkData)[targetIndex];
    }

    public void SetBlockLocal(int x, int y, int z, byte blockType, bool updateMesh = true)
    {
        if (x < 0 || x >= chunkSizeXZ || y < 0 || y >= chunkSizeY || z < 0 || z >= chunkSizeXZ) return;
        
        ushort[] fullData = GetDecompressedData();
        if (fullData == null) return; 

        int localIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        ushort newPackedData = world.PackBlockData(blockType);

        if (fullData[localIndex] == newPackedData) return;
        fullData[localIndex] = newPackedData;
        
        ushort[] recompressedData = CompressChunkMultiLayerRLE(fullData);
        if ((float)recompressedData.Length / fullData.Length < 1.0f) {
            _chunkData = recompressedData; _isCompressed = true;
        } else {
            _chunkData = fullData; _isCompressed = false;
        }
        
        isSingleOpaqueSolid = false;
        if (_isCompressed) {
            ushort[] data = (ushort[])_chunkData;
            if(data.Length > 0 && data[0] == RLE_TYPE_3D_FULL_CHUNK) {
                ushort blockValue = data[1];
                byte blockID = (byte)(blockValue & 0xFF);
                if(world.blockTypeManager.GetBlockIsSolid(blockID) && world.blockTypeManager.GetBlockVisibilityType(blockID) == BlockVisibilityType.Opaque) {
                    isSingleOpaqueSolid = true;
                }
            }
        }
        
        if (!updateMesh) return;
        world.RequestChunkMeshUpdate(this);
        if (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1)
        {
            world.TriggerNeighborMeshRebuilds(this);
        }
    }
    
    private ushort[] CompressChunkMultiLayerRLE(ushort[] fullChunkData)
    {
        ushort[] tempCompressed = new ushort[fullChunkData.Length * 2];
        int compressedIndex = 0;
        if (fullChunkData.Length == 0) return new ushort[0];

        ushort firstBlock = fullChunkData[0];
        bool isFullChunkRun = true;
        for (int i = 1; i < fullChunkData.Length; i++) {
            if (fullChunkData[i] != firstBlock) {
                isFullChunkRun = false;
                break;
            }
        }
        if (isFullChunkRun) {
            tempCompressed[0] = RLE_TYPE_3D_FULL_CHUNK;
            tempCompressed[1] = firstBlock;
            compressedIndex = 2;
        } else {
            int planeSize = chunkSizeXZ * chunkSizeXZ;
            for (int y = 0; y < chunkSizeY; y++) {
                int planeStartIndex = y * planeSize;
                ushort firstBlockInPlane = fullChunkData[planeStartIndex];
                bool isPlaneRun = true;
                for (int i = 1; i < planeSize; i++) {
                    if (fullChunkData[planeStartIndex + i] != firstBlockInPlane) {
                        isPlaneRun = false;
                        break;
                    }
                }
                
                if (isPlaneRun) {
                    tempCompressed[compressedIndex++] = RLE_TYPE_2D_XZ_PLANE;
                    tempCompressed[compressedIndex++] = firstBlockInPlane;
                } else {
                    for (int i = 0; i < planeSize; ) {
                        ushort currentBlock = fullChunkData[planeStartIndex + i];
                        ushort runCount = 1;
                        while (i + runCount < planeSize && fullChunkData[planeStartIndex + i + runCount] == currentBlock && runCount < ushort.MaxValue) {
                            runCount++;
                        }
                        tempCompressed[compressedIndex++] = RLE_TYPE_1D;
                        tempCompressed[compressedIndex++] = runCount;
                        tempCompressed[compressedIndex++] = currentBlock;
                        i += runCount;
                    }
                }
            }
        }

        ushort[] finalCompressedData = new ushort[compressedIndex];
        System.Array.Copy(tempCompressed, finalCompressedData, compressedIndex);
        return finalCompressedData;
    }
    
    private ushort[] DecompressChunkMultiLayerRLE(ushort[] rleData)
    {
        ushort[] fullData = new ushort[_chunkDataSize];
        if (rleData == null || rleData.Length == 0) return fullData;

        int rleIndex = 0;

        if (rleData[rleIndex] == RLE_TYPE_3D_FULL_CHUNK) {
            ushort blockValue = rleData[rleIndex + 1];
            for (int i = 0; i < _chunkDataSize; i++) {
                fullData[i] = blockValue;
            }
            return fullData;
        }

        int planeSize = chunkSizeXZ * chunkSizeXZ;
        for (int y = 0; y < chunkSizeY; y++) {
            int planeStartIndex = y * planeSize;
            int planeDecompressed = 0;
            
            while (planeDecompressed < planeSize) {
                if (rleIndex >= rleData.Length) return fullData;
                ushort runType = rleData[rleIndex++];
                if (runType == RLE_TYPE_2D_XZ_PLANE) {
                    ushort blockValue = rleData[rleIndex++];
                    for (int i = 0; i < planeSize; i++) {
                        fullData[planeStartIndex + i] = blockValue;
                    }
                    planeDecompressed = planeSize;
                } else {
                    ushort runCount = rleData[rleIndex++];
                    ushort blockValue = rleData[rleIndex++];
                    for (int j = 0; j < runCount; j++) {
                        if (planeDecompressed < planeSize) {
                           fullData[planeStartIndex + planeDecompressed] = blockValue;
                           planeDecompressed++;
                        }
                    }
                }
            }
        }

        return fullData;
    }
    
    private ushort DecompressBlockFromMultiLayerRLE(ushort[] rleData, int x, int y, int z)
    {
        if (rleData == null || rleData.Length == 0) return 0;
        
        int rleIndex = 0;

        if (rleData[rleIndex] == RLE_TYPE_3D_FULL_CHUNK) {
            return rleData[rleIndex + 1];
        }

        int targetLocalIndexInPlane = z * chunkSizeXZ + x;
        int planeSize = chunkSizeXZ * chunkSizeXZ;

        for (int currentY = 0; currentY < chunkSizeY; currentY++) {
            int planeDecompressed = 0;

            while(planeDecompressed < planeSize)
            {
                if (rleIndex >= rleData.Length) return 0;
                ushort runType = rleData[rleIndex++];

                if(runType == RLE_TYPE_2D_XZ_PLANE)
                {
                    ushort blockValue = rleData[rleIndex++];
                    if (currentY == y) return blockValue;
                    planeDecompressed = planeSize;
                }
                else
                {
                    ushort runCount = rleData[rleIndex++];
                    ushort blockValue = rleData[rleIndex++];
                    if(currentY == y && targetLocalIndexInPlane >= planeDecompressed && targetLocalIndexInPlane < planeDecompressed + runCount)
                    {
                        return blockValue;
                    }
                    planeDecompressed += runCount;
                }
            }
        }
        return 0; 
    }

    public void ApplyColliderDeferred()
    {
        #if UNITY_EDITOR
        float timer_start = Time.realtimeSinceStartup;
        ApplyDataToCollider();
        time_ApplyCollision = (Time.realtimeSinceStartup - timer_start) * 1000f;
        #else
        ApplyDataToCollider();
        #endif
    }
}
