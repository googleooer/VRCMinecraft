using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using System.Text;

/// <summary>
/// This version of McChunk features a fully time-sliced generation and meshing pipeline.
///
/// 1.  TIME-SLICED MESHING: BuildMesh() is a state machine that processes a
///     configurable number of voxels per frame.
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
    private int _meshing_progress_index = 0;
    private int _voxelsPerMeshStep = 512;
    // --- REMOVED old generation state variables ---
    
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
    
    public void Initialize(McWorld worldRef, McTerrainGenerator terrainGen, int cX, int cY, int cZ, int noisePointsPerStep, int voxelsPerMeshStep, int voxelsPerTerrainStep)
    {
        this.world = worldRef;
        this.chunkSizeXZ = world.chunkSizeXZ;
        this.chunkSizeY = world.chunkSizeY;
        this.chunkX_world = cX;
        this.chunkY_world = cY;
        this.chunkZ_world = cZ;
        this._terrainGenRef = terrainGen;
        
        this._voxelsPerMeshStep = Mathf.Max(1, voxelsPerMeshStep);
        
        _chunkDataSize = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
        _localBlockData = new ushort[_chunkDataSize];
        _opaqueVertices = new Vector3[MAX_VERTS]; _opaqueTriangles = new int[MAX_TRIS]; _opaqueUVs = new Vector3[MAX_VERTS]; _opaqueNormals = new Vector3[MAX_VERTS];
        _transparentVertices = new Vector3[MAX_VERTS]; _transparentTriangles = new int[MAX_TRIS]; _transparentUVs = new Vector3[MAX_VERTS]; _transparentNormals = new Vector3[MAX_VERTS];
        _cutoutVertices = new Vector3[MAX_VERTS]; _cutoutTriangles = new int[MAX_TRIS]; _cutoutUVs = new Vector3[MAX_VERTS]; _cutoutNormals = new Vector3[MAX_VERTS];
        _collisionVertices = new Vector3[MAX_VERTS * 3]; _collisionTriangles = new int[MAX_TRIS * 3];
        
        StartDataGeneration();
    }
    
    // --- REWRITTEN --- The new, simplified data generation process.
    private void StartDataGeneration()
    {
        isGeneratingData = true;
        generationStep = 0;
        
        // Initialize terrain generator for this chunk
        _terrainGenRef.StartChunkGeneration(chunkX_world, chunkY_world, chunkZ_world);
    }
    
    public bool StepDataGeneration()
    {
        if (!isGeneratingData) return true;
        
        ushort[] generatedData;
        bool isComplete = _terrainGenRef.StepChunkGeneration(out generatedData);
        
        if (isComplete)
        {
            // Generation complete, compress the data
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
                    bool isSolid = (blockValue & 0x0100) != 0;
                    var visibility = (BlockVisibilityType)((blockValue >> 9) & 0x7);
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

    // Add a method to check if chunk is generating data:
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
            // Use the temporary _localBlockData for meshing to avoid repeated decompression.
            _localBlockData = DecompressChunkMultiLayerRLE((ushort[])_chunkData);
        }
        else
        {
            // If not compressed, we still need to copy it to the local buffer for meshing.
            System.Array.Copy((ushort[])_chunkData, _localBlockData, _chunkDataSize);
        }

        _meshing_progress_index = 0;

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

        #if UNITY_EDITOR
        float timer_start_stage = 0f;
        if(enableVerboseLogging) timer_start_stage = Time.realtimeSinceStartup;
        #endif

        int y_stride = chunkSizeXZ * chunkSizeXZ;
        int z_stride = chunkSizeXZ;
        
        int voxelsChecked = 0;
        
        while (voxelsChecked < _voxelsPerMeshStep && _meshing_progress_index < _chunkDataSize)
        {
            int index = _meshing_progress_index;
            ushort blockData = _localBlockData[index];

            if ((blockData & 0xFF) != 0) 
            {
                int y = index / y_stride;
                int rem = index % y_stride;
                int z = rem / z_stride;
                int x = rem % z_stride;
                
                byte blockID = (byte)(blockData & 0xFF);
                BlockVisibilityType visibility = (BlockVisibilityType)((blockData >> 9) & 0x3); // Only 2 bits needed now
                Vector3 blockPos = new Vector3(x, y, z);
                
                ushort neighborData;

                if (y + 1 >= chunkSizeY) { neighborData = (neighborCache_PY == null) ? (ushort)0 : neighborCache_PY[z * z_stride + x]; } 
                else { neighborData = _localBlockData[index + y_stride]; }
                if (ShouldDrawFace(blockID, visibility, neighborData)) AddFace(FaceVertices_Up, Normal_Up, blockPos, blockID, visibility, FACE_INDEX_TOP);

                if (y - 1 < 0) { neighborData = (neighborCache_NY == null) ? (ushort)0 : neighborCache_NY[((chunkSizeY - 1) * y_stride) + (z * z_stride) + x]; } 
                else { neighborData = _localBlockData[index - y_stride]; }
                if (ShouldDrawFace(blockID, visibility, neighborData)) AddFace(FaceVertices_Down, Normal_Down, blockPos, blockID, visibility, FACE_INDEX_BOTTOM);

                if (z + 1 >= chunkSizeXZ) { neighborData = (neighborCache_PZ == null) ? (ushort)0 : neighborCache_PZ[y * y_stride + x]; } 
                else { neighborData = _localBlockData[index + z_stride]; }
                if (ShouldDrawFace(blockID, visibility, neighborData)) AddFace(FaceVertices_North, Normal_North, blockPos, blockID, visibility, FACE_INDEX_SIDE);

                if (z - 1 < 0) { neighborData = (neighborCache_NZ == null) ? (ushort)0 : neighborCache_NZ[y * y_stride + (chunkSizeXZ - 1) * z_stride + x]; } 
                else { neighborData = _localBlockData[index - z_stride]; }
                if (ShouldDrawFace(blockID, visibility, neighborData)) AddFace(FaceVertices_South, Normal_South, blockPos, blockID, visibility, FACE_INDEX_SIDE);

                if (x + 1 >= chunkSizeXZ) { neighborData = (neighborCache_PX == null) ? (ushort)0 : neighborCache_PX[y * y_stride + z * z_stride]; } 
                else { neighborData = _localBlockData[index + 1]; }
                if (ShouldDrawFace(blockID, visibility, neighborData)) AddFace(FaceVertices_East, Normal_East, blockPos, blockID, visibility, FACE_INDEX_SIDE);

                if (x - 1 < 0) { neighborData = (neighborCache_NX == null) ? (ushort)0 : neighborCache_NX[y * y_stride + z * z_stride + (chunkSizeXZ - 1)]; } 
                else { neighborData = _localBlockData[index - 1]; }
                if (ShouldDrawFace(blockID, visibility, neighborData)) AddFace(FaceVertices_West, Normal_West, blockPos, blockID, visibility, FACE_INDEX_SIDE);
            }
            
            _meshing_progress_index++;
            voxelsChecked++;
        }

        #if UNITY_EDITOR
        if(enableVerboseLogging) time_MainLoop += (Time.realtimeSinceStartup - timer_start_stage) * 1000f;
        #endif

        if (_meshing_progress_index >= _chunkDataSize)
        {
            ApplyAllMeshData();
            _localBlockData = null; // Clear temporary mesh data
            isBuildingMesh = false;
            
            #if UNITY_EDITOR
            if (enableVerboseLogging) {
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
        }
        else
        {
            SendCustomEventDelayedFrames(nameof(BuildMeshStep), 1);
        }
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
        
        targetVertices[currentVertexCount + 0] = blockPos + faceVertices[0]; targetVertices[currentVertexCount + 1] = blockPos + faceVertices[1];
        targetVertices[currentVertexCount + 2] = blockPos + faceVertices[2]; targetVertices[currentVertexCount + 3] = blockPos + faceVertices[3];
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
            _collisionVertices[_collisionVertexCount + 0] = blockPos + faceVertices[0]; _collisionVertices[_collisionVertexCount + 1] = blockPos + faceVertices[1];
            _collisionVertices[_collisionVertexCount + 2] = blockPos + faceVertices[2]; _collisionVertices[_collisionVertexCount + 3] = blockPos + faceVertices[3];
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
        neighborCache_PX = GetNeighborCache(world.GetChunkAt(chunkX_world + 1, chunkY_world, chunkZ_world));
        neighborCache_NX = GetNeighborCache(world.GetChunkAt(chunkX_world - 1, chunkY_world, chunkZ_world));
        neighborCache_PY = GetNeighborCache(world.GetChunkAt(chunkX_world, chunkY_world + 1, chunkZ_world));
        neighborCache_NY = GetNeighborCache(world.GetChunkAt(chunkX_world, chunkY_world - 1, chunkZ_world));
        neighborCache_PZ = GetNeighborCache(world.GetChunkAt(chunkX_world, chunkY_world, chunkZ_world + 1));
        neighborCache_NZ = GetNeighborCache(world.GetChunkAt(chunkX_world, chunkY_world, chunkZ_world - 1));
    }

    private ushort[] GetNeighborCache(McChunk neighbor)
    {
        if (neighbor == null) return null;
        if (neighbor.isSingleOpaqueSolid)
        {
            ushort[] dummyCache = new ushort[_chunkDataSize];
            ushort blockValue = neighbor.GetHomogeneousBlockValue(); 
            for (int i = 0; i < _chunkDataSize; i++) { dummyCache[i] = blockValue; }
            return dummyCache;
        }
        return neighbor.GetDecompressedData();
    }

    public ushort GetHomogeneousBlockValue()
    {
        if (isSingleOpaqueSolid) { return ((ushort[])_chunkData)[1]; }
        return 0;
    }
    
    public ushort[] GetDecompressedData()
    {
        if (_isCompressed) { return DecompressChunkMultiLayerRLE((ushort[])_chunkData); }
        // If not compressed, _chunkData is already the full array.
        return (ushort[])_chunkData;
    }
    
    private void ClearAllBuffers()
    {
        _opaqueVertexCount = 0; _opaqueTriangleCount = 0;
        _transparentVertexCount = 0; _transparentTriangleCount = 0;
        _cutoutVertexCount = 0; _cutoutTriangleCount = 0;
        _collisionVertexCount = 0; _collisionTriangleCount = 0;
    }
    
    // Updated ShouldDrawFace method signature and implementation
    private bool ShouldDrawFace(byte selfID, BlockVisibilityType selfVisibility, ushort neighborData)
    {
        byte neighborID = (byte)(neighborData & 0xFF);
        
        // Always draw if neighbor is air
        if (neighborID == 0) return true;

        // Get neighbor visibility
        BlockVisibilityType neighborVisibility = (BlockVisibilityType)((neighborData >> 9) & 0x3); // Updated bit position
        
        // Invisible blocks never draw faces
        if (selfVisibility == BlockVisibilityType.Invisible) return false;
        
        // Get culling type for this block
        BlockCullingType selfCulling = world.blockTypeManager.GetBlockCullingType(selfID);
        
        // Apply culling rules
        switch (selfCulling)
        {
            case BlockCullingType.NoCull:
                // Never cull - always draw the face
                return true;
                
            case BlockCullingType.CullSelf:
                // Only cull if neighbor is the same block type
                return neighborID != selfID;
                
            case BlockCullingType.CullSelfAndOpaque:
                // Cull if neighbor is same block or opaque
                return !(neighborID == selfID || neighborVisibility == BlockVisibilityType.Opaque);
                
            case BlockCullingType.CullSelfAndCutout:
                // Cull if neighbor is same block or cutout
                return !(neighborID == selfID || neighborVisibility == BlockVisibilityType.Cutout);
                
            case BlockCullingType.CullSelfAndTransparent:
                // Cull if neighbor is same block or transparent
                return !(neighborID == selfID || neighborVisibility == BlockVisibilityType.Transparent);
                
            case BlockCullingType.CullAll:
                // Always cull (unless neighbor is air, which we already checked)
                return false;
                
            default:
                // Default to most conservative culling
                return !(neighborVisibility == BlockVisibilityType.Opaque);
        }
    }
    
    private void ApplyAllMeshData()
    {
        #if UNITY_EDITOR
        float timer_start = 0.0f;
        if(enableVerboseLogging) timer_start = Time.realtimeSinceStartup;
        #endif
        ApplyDataToMesh(opaqueMeshFilter, _opaqueVertices, _opaqueTriangles, _opaqueUVs, _opaqueNormals, _opaqueVertexCount, _opaqueTriangleCount);
        #if UNITY_EDITOR
        if(enableVerboseLogging) time_ApplyOpaque = (Time.realtimeSinceStartup - timer_start) * 1000f;
        #endif
        #if UNITY_EDITOR
        if(enableVerboseLogging) timer_start = Time.realtimeSinceStartup;
        #endif
        ApplyDataToMesh(transparentMeshFilter, _transparentVertices, _transparentTriangles, _transparentUVs, _transparentNormals, _transparentVertexCount, _transparentTriangleCount);
        #if UNITY_EDITOR
        if(enableVerboseLogging) time_ApplyTransparent = (Time.realtimeSinceStartup - timer_start) * 1000f;
        #endif
        #if UNITY_EDITOR
        if(enableVerboseLogging) timer_start = Time.realtimeSinceStartup;
        #endif
        ApplyDataToMesh(cutoutMeshFilter, _cutoutVertices, _cutoutTriangles, _cutoutUVs, _cutoutNormals, _cutoutVertexCount, _cutoutTriangleCount);
        #if UNITY_EDITOR
        if(enableVerboseLogging) time_ApplyCutout = (Time.realtimeSinceStartup - timer_start) * 1000f;
        #endif
        #if UNITY_EDITOR
        if(enableVerboseLogging) timer_start = Time.realtimeSinceStartup;
        #endif
        ApplyDataToCollider();
        #if UNITY_EDITOR
        if(enableVerboseLogging) time_ApplyCollision = (Time.realtimeSinceStartup - timer_start) * 1000f;
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

    public void SetBlockLocal(int x, int y, int z, byte blockType)
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
                if((blockValue & 0x0100) != 0 && ((BlockVisibilityType)((blockValue >> 9) & 0x7)) == BlockVisibilityType.Opaque) {
                    isSingleOpaqueSolid = true;
                }
            }
        }
        
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

        // 1. Check for 3D run (full chunk)
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
            // 2. Process layer by layer
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
                    // 3. Process as 1D runs within the layer
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
                if (rleIndex >= rleData.Length) return fullData; // Bounds check
                ushort runType = rleData[rleIndex++];
                if (runType == RLE_TYPE_2D_XZ_PLANE) {
                    ushort blockValue = rleData[rleIndex++];
                    for (int i = 0; i < planeSize; i++) {
                        fullData[planeStartIndex + i] = blockValue;
                    }
                    planeDecompressed = planeSize;
                } else { // RLE_TYPE_1D
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
                if (rleIndex >= rleData.Length) return 0; // Bounds check
                ushort runType = rleData[rleIndex++];

                if(runType == RLE_TYPE_2D_XZ_PLANE)
                {
                    ushort blockValue = rleData[rleIndex++];
                    if (currentY == y) return blockValue;
                    planeDecompressed = planeSize;
                }
                else // RLE_TYPE_1D
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
}
