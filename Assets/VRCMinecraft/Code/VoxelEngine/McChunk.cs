using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRRefAssist; 
using System.Text; 

internal enum McChunk_MeshTarget { Opaque, Transparent, Cutout }

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McChunk : UdonSharpBehaviour
{
    [HideInInspector] public McWorld world;

    public int chunkSizeXZ = 16;
    public int chunkSizeY = 16;
    [HideInInspector] public int voxelsPerSlice = 256;

    public int chunkX; 
    public int chunkY;
    public int chunkZ;
    public bool template = false;

    [Header("Materials (Order: Opaque, Transparent, Cutout)")]
    public Material opaqueMaterial;
    public Material transparentMaterial;
    public Material cutoutMaterial;

    [Header("Debug")]
    public bool enableVerboseLogging = true; 
    private float meshBuildStartTime_total;
    private float meshBuildSliceStartTime_current;
    private int meshBuildSliceCounter; 
    private StringBuilder logBuilder_McChunk;

    private Mesh chunkMesh; 
    [SerializeField, GetComponent] private MeshCollider meshCollider; 
    [SerializeField, GetComponent] private MeshRenderer meshRenderer;
    [SerializeField, GetComponent] private MeshFilter meshFilter;
    
    private const int MAX_VERTS_PER_SUB_DATASET = 65534; 

    private Vector3[] opaque_vertexPool;
    private int[] opaque_trianglePool;
    private Vector3[] opaque_uvPool;
    private Vector3[] opaque_normalPool;
    private int opaque_vertexCount;
    private int opaque_triangleCount;
    private int opaque_uvCount;
    private int opaque_normalCount;

    private Vector3[] transparent_vertexPool;
    private int[] transparent_trianglePool;
    private Vector3[] transparent_uvPool;
    private Vector3[] transparent_normalPool;
    private int transparent_vertexCount;
    private int transparent_triangleCount;
    private int transparent_uvCount;
    private int transparent_normalCount;

    private Vector3[] cutout_vertexPool;
    private int[] cutout_trianglePool;
    private Vector3[] cutout_uvPool;
    private Vector3[] cutout_normalPool;
    private int cutout_vertexCount;
    private int cutout_triangleCount;
    private int cutout_uvCount;
    private int cutout_normalCount;

    private Vector3[] collision_vertexPool;
    private int[] collision_trianglePool;
    private int collision_vertexCount;
    private int collision_triangleCount;

    [HideInInspector] public bool isBuildingMesh = false;
    private bool isInitialized = false;
    private bool _currentBuildIsImmediate = false; 
    
    [SerializeField, FindObjectOfType(true)] 
    private McBlockTypeManager blockTypeManager; 

    private int buildMesh_currentX = 0;
    private int buildMesh_currentY = 0;
    private int buildMesh_currentZ = 0;

    private bool _currentBuildHasPoi;
    private int _currentPoiX, _currentPoiY, _currentPoiZ;
    private int _currentUpdateRadius = 0; 

    private int _currentBuildMinX, _currentBuildMinY, _currentBuildMinZ;
    private int _currentBuildMaxX, _currentBuildMaxY, _currentBuildMaxZ;

    // Dont use static fields because theyre not supported by UdonSharp
    private readonly Vector3[] processMeshDirections = new Vector3[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

    private readonly Vector3[][] FaceVertexCoordinates = new Vector3[][] {
        new Vector3[] { new Vector3(1,0,0), new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0) }, // Right
        new Vector3[] { new Vector3(0,0,1), new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1) }, // Left
        new Vector3[] { new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1) }, // Up
        new Vector3[] { new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0), new Vector3(0,0,0) }, // Down
        new Vector3[] { new Vector3(1,0,1), new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1) }, // Forward
        new Vector3[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0) }  // Back
    };

    // Vertices for the two quads of a Cross shape (centered at 0.5,0.5 in XZ plane, from Y=0 to Y=1)
    // These are defined with their own normals for two-sided rendering (assuming material has Cull Off)
    private readonly Vector3[] CrossShapeQuad1Vertices = new Vector3[] {
        new Vector3(0,0,0), new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(0,1,0) 
    };
     private readonly Vector3[] CrossShapeQuad1Normals = new Vector3[] {
        new Vector3(-1,0,1).normalized, new Vector3(-1,0,1).normalized, new Vector3(-1,0,1).normalized, new Vector3(-1,0,1).normalized
    };

    private readonly Vector3[] CrossShapeQuad2Vertices = new Vector3[] {
        new Vector3(0,0,1), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,1)
    };
    private readonly Vector3[] CrossShapeQuad2Normals = new Vector3[] {
        new Vector3(1,0,1).normalized, new Vector3(1,0,1).normalized, new Vector3(1,0,1).normalized, new Vector3(1,0,1).normalized
    };


    public void InitializeChunk()
    {
        if (isInitialized || template) return;
        logBuilder_McChunk = new StringBuilder(256);
        
        if (meshFilter == null) meshFilter = gameObject.GetComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = gameObject.GetComponent<MeshRenderer>();
        if (meshCollider == null) meshCollider = gameObject.GetComponent<MeshCollider>();
        
        if (meshFilter == null || meshRenderer == null || meshCollider == null) { 
            Debug.LogError($"[{gameObject.name}] Critical component missing! MF:{meshFilter}, MR:{meshRenderer}, MC:{meshCollider}"); 
            this.enabled = false; return; 
        }
        if (blockTypeManager == null) {
            Debug.LogError($"[{gameObject.name}] McBlockTypeManager not found! Aborting InitializeChunk.");
            this.enabled = false; return;
        }


        chunkMesh = new Mesh();
        chunkMesh.name = $"ChunkMesh_({chunkX},{chunkY},{chunkZ})";
        meshFilter.mesh = chunkMesh;

        Material[] materials = new Material[3];
        materials[0] = opaqueMaterial; materials[1] = transparentMaterial; materials[2] = cutoutMaterial;
        meshRenderer.sharedMaterials = materials; 

        int poolVertexSize = MAX_VERTS_PER_SUB_DATASET;
        int poolTriangleIndexSize = Mathf.CeilToInt(poolVertexSize * 1.5f); 
        if (poolTriangleIndexSize % 3 != 0) poolTriangleIndexSize += (3 - (poolTriangleIndexSize % 3));

        opaque_vertexPool = new Vector3[poolVertexSize]; opaque_trianglePool = new int[poolTriangleIndexSize];
        opaque_uvPool = new Vector3[poolVertexSize]; opaque_normalPool = new Vector3[poolVertexSize];
        transparent_vertexPool = new Vector3[poolVertexSize]; transparent_trianglePool = new int[poolTriangleIndexSize];
        transparent_uvPool = new Vector3[poolVertexSize]; transparent_normalPool = new Vector3[poolVertexSize];
        cutout_vertexPool = new Vector3[poolVertexSize]; cutout_trianglePool = new int[poolTriangleIndexSize];
        cutout_uvPool = new Vector3[poolVertexSize]; cutout_normalPool = new Vector3[poolVertexSize];
        collision_vertexPool = new Vector3[poolVertexSize]; collision_trianglePool = new int[poolTriangleIndexSize];

        isInitialized = true;
#if UNITY_EDITOR
        if (enableVerboseLogging) Debug.Log($"[McChunk:{gameObject.name}] Initialized.");
#endif
        if (world != null) world.RequestChunkMeshUpdate(this); 
    }

    public void StartBuildMesh(bool processImmediately) 
    {
        if (isBuildingMesh && processImmediately && _currentBuildIsImmediate) return;
        if (!isInitialized) { InitializeChunk(); if (!isInitialized) return; } 
        if (blockTypeManager == null) { Debug.LogError($"[{gameObject.name}] McBlockTypeManager is null in StartBuildMesh. Aborting."); isBuildingMesh = false; return; }

        
        isBuildingMesh = true; 
        _currentBuildIsImmediate = processImmediately;
        meshBuildStartTime_total = Time.realtimeSinceStartup; meshBuildSliceCounter = 0;
        
        _currentBuildMinX = 0; _currentBuildMaxX = chunkSizeXZ;
        _currentBuildMinY = 0; _currentBuildMaxY = chunkSizeY;
        _currentBuildMinZ = 0; _currentBuildMaxZ = chunkSizeXZ;
        

        buildMesh_currentX = _currentBuildMinX; buildMesh_currentY = _currentBuildMinY; buildMesh_currentZ = _currentBuildMinZ;
        
        opaque_vertexCount = 0; opaque_triangleCount = 0; opaque_uvCount = 0; opaque_normalCount = 0;
        transparent_vertexCount = 0; transparent_triangleCount = 0; transparent_uvCount = 0; transparent_normalCount = 0;
        cutout_vertexCount = 0; cutout_triangleCount = 0; cutout_uvCount = 0; cutout_normalCount = 0;
        collision_vertexCount = 0; collision_triangleCount = 0;

        meshBuildSliceStartTime_current = Time.realtimeSinceStartup; 
        ProcessMeshSlice();
    }

    public void ProcessMeshSlice()
    {
        if (!isBuildingMesh) return; 
        if (blockTypeManager == null) { Debug.LogError($"[{gameObject.name}] McBlockTypeManager is null in ProcessMeshSlice. Aborting build."); isBuildingMesh = false; return; }

        int processedThisSlice = 0;

        for (int x = buildMesh_currentX; x < _currentBuildMaxX; x++) {
            for (int y = buildMesh_currentY; y < _currentBuildMaxY; y++) {
                for (int z = buildMesh_currentZ; z < _currentBuildMaxZ; z++) {
                    int currentGlobalX = chunkX + x; int currentGlobalY = chunkY + y; int currentGlobalZ = chunkZ + z;
                    byte currentBlockID = world.GetBlock(currentGlobalX, currentGlobalY, currentGlobalZ);

                    if (currentBlockID == 0) { processedThisSlice++; if (ShouldScheduleNextSlice(processedThisSlice, x, y, z)) return; continue; }

                    BlockVisibilityType currentVisibility = blockTypeManager.GetBlockVisibilityType(currentBlockID);
                    if (currentVisibility == BlockVisibilityType.Invisible) { processedThisSlice++; if (ShouldScheduleNextSlice(processedThisSlice, x, y, z)) return; continue; }

                    McChunk_MeshTarget targetMeshTypeForCurrentBlock; 
                    if (currentVisibility == BlockVisibilityType.Opaque) targetMeshTypeForCurrentBlock = McChunk_MeshTarget.Opaque;
                    else if (blockTypeManager.IsAnyCutoutType(currentVisibility)) targetMeshTypeForCurrentBlock = McChunk_MeshTarget.Cutout;
                    else targetMeshTypeForCurrentBlock = McChunk_MeshTarget.Transparent;

                    McBlockShapeType shapeType = blockTypeManager.GetBlockShapeType(currentBlockID);

                    if (shapeType == McBlockShapeType.Cube)
                    {
                        for (int i = 0; i < 6; i++) { 
                            Vector3 dir = processMeshDirections[i]; 
                            int neighborGlobalX = currentGlobalX + (int)dir.x; int neighborGlobalY = currentGlobalY + (int)dir.y; int neighborGlobalZ = chunkZ + z + (int)dir.z; // Corrected neighbor Z
                            byte neighborBlockID = world.GetBlock(neighborGlobalX, neighborGlobalY, neighborGlobalZ);
                            
                            bool drawFace = false;
                            // For Cube shapes, only draw face if neighbor is not solid or is a non-opaque type that current block should draw against
                            if (neighborBlockID == 0) { // Neighbor is Air
                                drawFace = true;
                            } else {
                                BlockVisibilityType neighborVisibility = blockTypeManager.GetBlockVisibilityType(neighborBlockID);
                                McBlockShapeType neighborShape = blockTypeManager.GetBlockShapeType(neighborBlockID);

                                if (neighborVisibility == BlockVisibilityType.Invisible) {
                                    drawFace = true;
                                } else if (neighborShape == McBlockShapeType.Cross) { 
                                    // Cubes always draw faces against Cross shapes (Cross shapes don't cull)
                                    drawFace = true;
                                } else { // Neighbor is a Cube
                                    if (currentVisibility == BlockVisibilityType.Opaque) {
                                        drawFace = (neighborVisibility != BlockVisibilityType.Opaque);
                                    } else if (currentVisibility == BlockVisibilityType.Transparent_NoCull) {
                                        drawFace = (neighborVisibility != BlockVisibilityType.Opaque);
                                    } else if (currentVisibility == BlockVisibilityType.Transparent_CullSelf || currentVisibility == BlockVisibilityType.Transparent_CullSelfAndOpaque) {
                                        drawFace = !(neighborVisibility == BlockVisibilityType.Opaque || (neighborBlockID == currentBlockID && neighborVisibility == currentVisibility));
                                    } else if (blockTypeManager.IsAnyCutoutType(currentVisibility)) {
                                        if (neighborVisibility == BlockVisibilityType.Opaque) drawFace = false;
                                        else if (currentVisibility == BlockVisibilityType.Cutout_CullOpaqueOnly) drawFace = true;
                                        else if (currentVisibility == BlockVisibilityType.Cutout_CullSelf) drawFace = !(neighborBlockID == currentBlockID && blockTypeManager.IsSelfCullingCutout(neighborVisibility));
                                        else if (currentVisibility == BlockVisibilityType.Cutout_CullSelfAndOtherCutout) drawFace = !blockTypeManager.IsAnyCutoutType(neighborVisibility);
                                        else drawFace = true;
                                    } else { // Default for other transparent types
                                        drawFace = (neighborVisibility != BlockVisibilityType.Opaque);
                                    }
                                }
                            }
                            if (drawFace) AddCubeFaceToPool(x, y, z, i, currentBlockID, targetMeshTypeForCurrentBlock); 
                        }
                    }
                    else if (shapeType == McBlockShapeType.Cross)
                    {
                        // Cross shapes are never culled by neighbors and do not cull neighbors.
                        // They are simply added if the block itself is visible.
                        AddCrossShapeToPool(x, y, z, currentBlockID, targetMeshTypeForCurrentBlock);
                    }

                    processedThisSlice++;
                    if (ShouldScheduleNextSlice(processedThisSlice, x, y, z)) return;
                }
                buildMesh_currentZ = _currentBuildMinZ; 
            }
            buildMesh_currentY = _currentBuildMinY; 
        }
        FinalizeMeshBuild();
    }

    private bool ShouldScheduleNextSlice(int processedCount, int curX, int curY, int curZ) {
        if (_currentBuildIsImmediate) return false; 
        if (voxelsPerSlice > 0 && processedCount >= voxelsPerSlice) {
            buildMesh_currentX = curX; buildMesh_currentY = curY; buildMesh_currentZ = curZ + 1; 
            if (buildMesh_currentZ >= _currentBuildMaxZ) { 
                buildMesh_currentZ = _currentBuildMinZ; buildMesh_currentY = curY + 1;          
                if (buildMesh_currentY >= _currentBuildMaxY) { buildMesh_currentY = _currentBuildMinY; buildMesh_currentX = curX + 1; }
            }
#if UNITY_EDITOR
            if (enableVerboseLogging) Debug.Log($"[McChunk:{gameObject.name}] Slice {meshBuildSliceCounter} done. Next:({buildMesh_currentX},{buildMesh_currentY},{buildMesh_currentZ})");
#endif
            meshBuildSliceCounter++; meshBuildSliceStartTime_current = Time.realtimeSinceStartup; 
            SendCustomEventDelayedFrames(nameof(ProcessMeshSlice), 1); 
            return true; 
        }
        return false; 
    }

    void AddCubeFaceToPool(int x, int y, int z, int faceIndex, byte blockType, McChunk_MeshTarget targetMesh)
    {
        int vCount = 0, tCount = 0;
        Vector3[] targetVertexPool = null, targetUvPool = null, targetNormalPool = null;
        int[] targetTrianglePool = null;

        switch (targetMesh) {
            case McChunk_MeshTarget.Opaque: vCount = opaque_vertexCount; tCount = opaque_triangleCount; targetVertexPool = opaque_vertexPool; targetTrianglePool = opaque_trianglePool; targetUvPool = opaque_uvPool; targetNormalPool = opaque_normalPool; break;
            case McChunk_MeshTarget.Transparent: vCount = transparent_vertexCount; tCount = transparent_triangleCount; targetVertexPool = transparent_vertexPool; targetTrianglePool = transparent_trianglePool; targetUvPool = transparent_uvPool; targetNormalPool = transparent_normalPool; break;
            case McChunk_MeshTarget.Cutout: vCount = cutout_vertexCount; tCount = cutout_triangleCount; targetVertexPool = cutout_vertexPool; targetTrianglePool = cutout_trianglePool; targetUvPool = cutout_uvPool; targetNormalPool = cutout_normalPool; break;
            default: Debug.LogError($"[{gameObject.name}] Invalid MeshTarget in AddCubeFaceToPool"); isBuildingMesh = false; return;
        }

        if (vCount + 4 > targetVertexPool.Length || tCount + 6 > targetTrianglePool.Length) { Debug.LogError($"[{gameObject.name}] Pool overflow for {targetMesh} cube face."); isBuildingMesh = false; return; }

        Vector3 blockOrigin = new Vector3(x, y, z); 
        Vector3[] verticesForFace = FaceVertexCoordinates[faceIndex];
        Vector3 faceNormal = processMeshDirections[faceIndex];
        int sliceIndex = blockTypeManager.GetFinalBlockTextureSlice(blockType, faceIndex);

        targetVertexPool[vCount + 0] = blockOrigin + verticesForFace[0]; targetVertexPool[vCount + 1] = blockOrigin + verticesForFace[1];
        targetVertexPool[vCount + 2] = blockOrigin + verticesForFace[2]; targetVertexPool[vCount + 3] = blockOrigin + verticesForFace[3];
        
        targetNormalPool[vCount + 0] = faceNormal; targetNormalPool[vCount + 1] = faceNormal;
        targetNormalPool[vCount + 2] = faceNormal; targetNormalPool[vCount + 3] = faceNormal;
        
        targetTrianglePool[tCount + 0] = vCount + 0; targetTrianglePool[tCount + 1] = vCount + 2; targetTrianglePool[tCount + 2] = vCount + 1; 
        targetTrianglePool[tCount + 3] = vCount + 0; targetTrianglePool[tCount + 4] = vCount + 3; targetTrianglePool[tCount + 5] = vCount + 2; 
        
        targetUvPool[vCount + 0] = new Vector3(0,0,sliceIndex); targetUvPool[vCount + 1] = new Vector3(1,0,sliceIndex);
        targetUvPool[vCount + 2] = new Vector3(1,1,sliceIndex); targetUvPool[vCount + 3] = new Vector3(0,1,sliceIndex);
        
        switch (targetMesh) { 
            case McChunk_MeshTarget.Opaque: opaque_vertexCount += 4; opaque_triangleCount += 6; opaque_uvCount += 4; opaque_normalCount += 4; break;
            case McChunk_MeshTarget.Transparent: transparent_vertexCount += 4; transparent_triangleCount += 6; transparent_uvCount += 4; transparent_normalCount += 4; break;
            case McChunk_MeshTarget.Cutout: cutout_vertexCount += 4; cutout_triangleCount += 6; cutout_uvCount += 4; cutout_normalCount += 4; break;
        }
    }

    void AddCrossShapeToPool(int x, int y, int z, byte blockType, McChunk_MeshTarget targetMesh)
    {
        int vCount = 0, tCount = 0;
        Vector3[] targetVertexPool = null, targetUvPool = null, targetNormalPool = null;
        int[] targetTrianglePool = null;

        switch (targetMesh) { 
            case McChunk_MeshTarget.Opaque: vCount = opaque_vertexCount; tCount = opaque_triangleCount; targetVertexPool = opaque_vertexPool; targetTrianglePool = opaque_trianglePool; targetUvPool = opaque_uvPool; targetNormalPool = opaque_normalPool; break;
            case McChunk_MeshTarget.Transparent: vCount = transparent_vertexCount; tCount = transparent_triangleCount; targetVertexPool = transparent_vertexPool; targetTrianglePool = transparent_trianglePool; targetUvPool = transparent_uvPool; targetNormalPool = transparent_normalPool; break;
            case McChunk_MeshTarget.Cutout: vCount = cutout_vertexCount; tCount = cutout_triangleCount; targetVertexPool = cutout_vertexPool; targetTrianglePool = cutout_trianglePool; targetUvPool = cutout_uvPool; targetNormalPool = cutout_normalPool; break;
            default: Debug.LogError($"[{gameObject.name}] Invalid MeshTarget in AddCrossShapeToPool"); isBuildingMesh = false; return;
        }

        if (vCount + 8 > targetVertexPool.Length || tCount + 12 > targetTrianglePool.Length) { Debug.LogError($"[{gameObject.name}] Pool overflow for {targetMesh} cross shape."); isBuildingMesh = false; return; }

        Vector3 blockOrigin = new Vector3(x, y, z);
        int sliceIndex = blockTypeManager.GetBlockTextureSlice_AllFaces(blockType); 

        // Quad 1
        targetVertexPool[vCount + 0] = blockOrigin + CrossShapeQuad1Vertices[0]; targetVertexPool[vCount + 1] = blockOrigin + CrossShapeQuad1Vertices[1];
        targetVertexPool[vCount + 2] = blockOrigin + CrossShapeQuad1Vertices[2]; targetVertexPool[vCount + 3] = blockOrigin + CrossShapeQuad1Vertices[3];
        targetUvPool[vCount + 0] = new Vector3(0,0,sliceIndex); targetUvPool[vCount + 1] = new Vector3(1,0,sliceIndex);
        targetUvPool[vCount + 2] = new Vector3(1,1,sliceIndex); targetUvPool[vCount + 3] = new Vector3(0,1,sliceIndex);
        targetNormalPool[vCount+0] = CrossShapeQuad1Normals[0]; targetNormalPool[vCount+1] = CrossShapeQuad1Normals[1]; 
        targetNormalPool[vCount+2] = CrossShapeQuad1Normals[2]; targetNormalPool[vCount+3] = CrossShapeQuad1Normals[3];
        targetTrianglePool[tCount + 0] = vCount + 0; targetTrianglePool[tCount + 1] = vCount + 2; targetTrianglePool[tCount + 2] = vCount + 1;
        targetTrianglePool[tCount + 3] = vCount + 0; targetTrianglePool[tCount + 4] = vCount + 3; targetTrianglePool[tCount + 5] = vCount + 2;
        vCount += 4; tCount += 6;

        // Quad 2
        targetVertexPool[vCount + 0] = blockOrigin + CrossShapeQuad2Vertices[0]; targetVertexPool[vCount + 1] = blockOrigin + CrossShapeQuad2Vertices[1];
        targetVertexPool[vCount + 2] = blockOrigin + CrossShapeQuad2Vertices[2]; targetVertexPool[vCount + 3] = blockOrigin + CrossShapeQuad2Vertices[3];
        targetUvPool[vCount + 0] = new Vector3(0,0,sliceIndex); targetUvPool[vCount + 1] = new Vector3(1,0,sliceIndex);
        targetUvPool[vCount + 2] = new Vector3(1,1,sliceIndex); targetUvPool[vCount + 3] = new Vector3(0,1,sliceIndex);
        targetNormalPool[vCount+0] = CrossShapeQuad2Normals[0]; targetNormalPool[vCount+1] = CrossShapeQuad2Normals[1];
        targetNormalPool[vCount+2] = CrossShapeQuad2Normals[2]; targetNormalPool[vCount+3] = CrossShapeQuad2Normals[3];
        targetTrianglePool[tCount + 0] = vCount + 0; targetTrianglePool[tCount + 1] = vCount + 2; targetTrianglePool[tCount + 2] = vCount + 1;
        targetTrianglePool[tCount + 3] = vCount + 0; targetTrianglePool[tCount + 4] = vCount + 3; targetTrianglePool[tCount + 5] = vCount + 2;
        
        switch (targetMesh) { 
            case McChunk_MeshTarget.Opaque: opaque_vertexCount += 8; opaque_triangleCount += 12; opaque_uvCount += 8; opaque_normalCount += 8; break;
            case McChunk_MeshTarget.Transparent: transparent_vertexCount += 8; transparent_triangleCount += 12; transparent_uvCount += 8; transparent_normalCount += 8; break;
            case McChunk_MeshTarget.Cutout: cutout_vertexCount += 8; cutout_triangleCount += 12; cutout_uvCount += 8; cutout_normalCount += 8; break;
        }
    }


    void AddFaceToCollisionPool(int x, int y, int z, int faceIndex)
    {
        if (collision_vertexCount + 4 > collision_vertexPool.Length || collision_triangleCount + 6 > collision_trianglePool.Length) { 
            Debug.LogError($"[{gameObject.name}] Collision pool overflow."); return; 
        }
        Vector3 blockOrigin = new Vector3(x,y,z); Vector3[] verts = FaceVertexCoordinates[faceIndex];
        collision_vertexPool[collision_vertexCount+0] = blockOrigin + verts[0]; collision_vertexPool[collision_vertexCount+1] = blockOrigin + verts[1];
        collision_vertexPool[collision_vertexCount+2] = blockOrigin + verts[2]; collision_vertexPool[collision_vertexCount+3] = blockOrigin + verts[3];
        collision_trianglePool[collision_triangleCount+0] = collision_vertexCount+0; collision_trianglePool[collision_triangleCount+1] = collision_vertexCount+2; collision_trianglePool[collision_triangleCount+2] = collision_vertexCount+1;
        collision_trianglePool[collision_triangleCount+3] = collision_vertexCount+0; collision_trianglePool[collision_triangleCount+4] = collision_vertexCount+3; collision_trianglePool[collision_triangleCount+5] = collision_vertexCount+2;
        collision_vertexCount+=4; collision_triangleCount+=6;
    }

    void FinalizeMeshBuild()
    {
        float finalizeStartTime = Time.realtimeSinceStartup;
        if (chunkMesh == null) { Debug.LogError($"[{gameObject.name}] ChunkMesh null in Finalize!"); isBuildingMesh = false; return; }
        if (blockTypeManager == null) { Debug.LogError($"[{gameObject.name}] McBlockTypeManager is null in FinalizeMeshBuild. Aborting."); isBuildingMesh = false; return; }

        chunkMesh.Clear(); 

        int totalVisualVertices = opaque_vertexCount + transparent_vertexCount + cutout_vertexCount;
        if (totalVisualVertices > 0) {
            if (totalVisualVertices > 65534) Debug.LogWarning($"[{gameObject.name}] Visual mesh verts {totalVisualVertices} > 65534 limit.");
            Vector3[] finalVertices = new Vector3[totalVisualVertices]; Vector3[] finalUVs = new Vector3[totalVisualVertices]; Vector3[] finalNormals = new Vector3[totalVisualVertices];
            int currentVOffset = 0;
            if(opaque_vertexCount>0){ System.Array.Copy(opaque_vertexPool,0,finalVertices,currentVOffset,opaque_vertexCount); System.Array.Copy(opaque_uvPool,0,finalUVs,currentVOffset,opaque_uvCount); System.Array.Copy(opaque_normalPool,0,finalNormals,currentVOffset,opaque_normalCount); currentVOffset+=opaque_vertexCount; }
            if(transparent_vertexCount>0){ System.Array.Copy(transparent_vertexPool,0,finalVertices,currentVOffset,transparent_vertexCount); System.Array.Copy(transparent_uvPool,0,finalUVs,currentVOffset,transparent_uvCount); System.Array.Copy(transparent_normalPool,0,finalNormals,currentVOffset,transparent_normalCount); currentVOffset+=transparent_vertexCount; }
            if(cutout_vertexCount>0){ System.Array.Copy(cutout_vertexPool,0,finalVertices,currentVOffset,cutout_vertexCount); System.Array.Copy(cutout_uvPool,0,finalUVs,currentVOffset,cutout_uvCount); System.Array.Copy(cutout_normalPool,0,finalNormals,currentVOffset,cutout_normalCount); }
            chunkMesh.vertices = finalVertices; chunkMesh.SetUVs(0, finalUVs); chunkMesh.normals = finalNormals;
            chunkMesh.subMeshCount = 3;
            int[] finalOTris = new int[opaque_triangleCount]; if(opaque_triangleCount>0) System.Array.Copy(opaque_trianglePool,finalOTris,opaque_triangleCount); chunkMesh.SetTriangles(finalOTris,0,true);
            int[] finalTTris = new int[transparent_triangleCount]; int tVStart = opaque_vertexCount; for(int i=0;i<transparent_triangleCount;i++)finalTTris[i]=transparent_trianglePool[i]+tVStart; chunkMesh.SetTriangles(finalTTris,1,true);
            int[] finalCTris = new int[cutout_triangleCount]; int cVStart = opaque_vertexCount+transparent_vertexCount; for(int i=0;i<cutout_triangleCount;i++)finalCTris[i]=cutout_trianglePool[i]+cVStart; chunkMesh.SetTriangles(finalCTris,2,true);
        }
        
        collision_vertexCount = 0; collision_triangleCount = 0; 
        for (int x = _currentBuildMinX; x < _currentBuildMaxX; x++) {
            for (int y = _currentBuildMinY; y < _currentBuildMaxY; y++) {
                for (int z = _currentBuildMinZ; z < _currentBuildMaxZ; z++) {
                    byte cID = world.GetBlock(chunkX+x, chunkY+y, chunkZ+z);
                    if (cID > 0 && blockTypeManager.GetBlockIsSolid(cID) && 
                        blockTypeManager.GetBlockShapeType(cID) == McBlockShapeType.Cube) // OPTIMIZATION: Only Cube shapes contribute to collision
                    {
                         for (int i=0; i<6; i++) {
                            Vector3 dir = processMeshDirections[i];
                            byte nID = world.GetBlock(chunkX+x+(int)dir.x, chunkY+y+(int)dir.y, chunkZ+z+(int)dir.z);
                            bool nSolidAndCube = false; 
                            if(nID > 0) {
                                nSolidAndCube = blockTypeManager.GetBlockIsSolid(nID) && (blockTypeManager.GetBlockShapeType(nID) == McBlockShapeType.Cube);
                            }
                            if (!nSolidAndCube) AddFaceToCollisionPool(x,y,z,i); // Only draw collision face if neighbor is not a solid cube
                        }
                    }
                }
            }
        }

        if (meshCollider != null) {
            if (collision_vertexCount > 0 && collision_vertexCount <= MAX_VERTS_PER_SUB_DATASET) {
                Mesh newColMesh = new Mesh(); newColMesh.name = $"ChunkColMesh_({chunkX},{chunkY},{chunkZ})";
                Vector3[] colVerts = new Vector3[collision_vertexCount]; int[] colTris = new int[collision_triangleCount];
                System.Array.Copy(collision_vertexPool,0,colVerts,0,collision_vertexCount); System.Array.Copy(collision_trianglePool,0,colTris,0,collision_triangleCount);
                newColMesh.vertices = colVerts; newColMesh.triangles = colTris;
                meshCollider.sharedMesh = newColMesh;
            } else if (collision_vertexCount == 0) meshCollider.sharedMesh = null;
            else { Debug.LogWarning($"[{gameObject.name}] Collision verts {collision_vertexCount} > limit. Collider disabled."); meshCollider.sharedMesh = null; }
        }

#if UNITY_EDITOR
        if(enableVerboseLogging) Debug.Log($"[McChunk:{gameObject.name}] Finalize. VisualV:{totalVisualVertices}, CollisionV:{collision_vertexCount}. TotalT:{(Time.realtimeSinceStartup-meshBuildStartTime_total)*1000f:F2}ms");
#endif
        isBuildingMesh = false; 
    }

    public void SetWorld(McWorld newWorld) { world = newWorld; if (!isInitialized && !template) InitializeChunk(); }
}
