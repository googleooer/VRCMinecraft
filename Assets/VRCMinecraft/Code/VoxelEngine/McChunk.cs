﻿using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRRefAssist; // Required for [FindObjectOfType] attribute

// Ensure BlockVisibilityType enum is accessible
// It MUST be defined outside the McBlockTypeManager class in its .cs file, or in its own .cs file.

// Enum to specify which mesh data set to target (moved outside class)
internal enum McChunk_MeshTarget { Opaque, Transparent, Cutout } // Renamed slightly to avoid global conflicts if any

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McChunk : UdonSharpBehaviour
{
    [HideInInspector] public McWorld world;
    [HideInInspector] public GameObject worldGO; 

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

    private Mesh chunkMesh; // Single mesh for this chunk
    [SerializeField, GetComponent] private MeshCollider meshCollider; 
    [SerializeField, GetComponent] private MeshRenderer meshRenderer;
    [SerializeField, GetComponent] private MeshFilter meshFilter;
    
    private const int MAX_VERTS_PER_SUB_DATASET = 65534; // Max for temporary collection before combining

    // Opaque Mesh Data (Temporary Collection)
    private Vector3[] opaque_vertexPool;
    private int[] opaque_trianglePool;
    private Vector2[] opaque_uvPool;
    private Vector3[] opaque_normalPool;
    private int opaque_vertexCount;
    private int opaque_triangleCount;
    private int opaque_uvCount;
    private int opaque_normalCount;

    // Transparent Mesh Data (Temporary Collection)
    private Vector3[] transparent_vertexPool;
    private int[] transparent_trianglePool;
    private Vector2[] transparent_uvPool;
    private Vector3[] transparent_normalPool;
    private int transparent_vertexCount;
    private int transparent_triangleCount;
    private int transparent_uvCount;
    private int transparent_normalCount;

    // Cutout Mesh Data (Temporary Collection)
    private Vector3[] cutout_vertexPool;
    private int[] cutout_trianglePool;
    private Vector2[] cutout_uvPool;
    private Vector3[] cutout_normalPool;
    private int cutout_vertexCount;
    private int cutout_triangleCount;
    private int cutout_uvCount;
    private int cutout_normalCount;

    private float tUnit;
    private float uvPadding;

    [HideInInspector] public bool isBuildingMesh = false;
    private bool isInitialized = false;

    [SerializeField, FindObjectOfType(true)] 
    private McBlockTypeManager blockTypeManager; 

    private int buildMesh_currentX = 0;
    private int buildMesh_currentY = 0;
    private int buildMesh_currentZ = 0;

    // Cached array for mesh processing directions
    private readonly Vector3[] processMeshDirections = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

    public void InitializeChunk()
    {
        if (isInitialized || template) return;

        
        // Got rid of GetComponent and AddComponent, not exposed to Udon and we already know the filter, renderer and collision will never be null.

        if (blockTypeManager == null) {
            Debug.LogError($"[{this.gameObject.name}] McBlockTypeManager instance NOT FOUND! UVs/block properties will be default.");
            tUnit = 0.0625f; uvPadding = 0.0001f;
        } else {
            tUnit = blockTypeManager.textureAtlasTUnit;
            uvPadding = blockTypeManager.textureAtlasUVPadding;
        }

        chunkMesh = new Mesh();
        chunkMesh.name = $"ChunkMesh_({chunkX},{chunkY},{chunkZ})";
        meshFilter.mesh = chunkMesh;

        Material[] materials = new Material[3];
        materials[0] = opaqueMaterial;
        materials[1] = transparentMaterial;
        materials[2] = cutoutMaterial;
        meshRenderer.sharedMaterials = materials;

        int poolVertexSize = MAX_VERTS_PER_SUB_DATASET;
        int poolTriangleIndexSize = Mathf.FloorToInt(poolVertexSize * 1.5f);

        opaque_vertexPool = new Vector3[poolVertexSize]; opaque_trianglePool = new int[poolTriangleIndexSize];
        opaque_uvPool = new Vector2[poolVertexSize]; opaque_normalPool = new Vector3[poolVertexSize];

        transparent_vertexPool = new Vector3[poolVertexSize]; transparent_trianglePool = new int[poolTriangleIndexSize];
        transparent_uvPool = new Vector2[poolVertexSize]; transparent_normalPool = new Vector3[poolVertexSize];

        cutout_vertexPool = new Vector3[poolVertexSize]; cutout_trianglePool = new int[poolTriangleIndexSize];
        cutout_uvPool = new Vector2[poolVertexSize]; cutout_normalPool = new Vector3[poolVertexSize];

        if (world == null && worldGO != null) world = worldGO.GetComponent<McWorld>();
        if (world == null) { Debug.LogError($"[{this.gameObject.name}] McWorld reference not set!"); return; }

        isInitialized = true;
        if (world != null) world.RequestChunkMeshUpdate(this); 
    }

    public void StartBuildMesh() 
    {
        if (isBuildingMesh) return;
        if (!isInitialized) { InitializeChunk(); if (!isInitialized) return; }
        if (world == null || blockTypeManager == null) { 
            Debug.LogError($"[{this.gameObject.name}] Critical reference missing in StartBuildMesh (World or BlockTypeManager).");
            if (blockTypeManager == null) { tUnit = 0.0625f; uvPadding = 0.0001f; }
            isBuildingMesh = false; return; 
        }
        tUnit = blockTypeManager.textureAtlasTUnit; uvPadding = blockTypeManager.textureAtlasUVPadding;

        isBuildingMesh = true; 
        opaque_vertexCount = 0; opaque_triangleCount = 0; opaque_uvCount = 0; opaque_normalCount = 0;
        transparent_vertexCount = 0; transparent_triangleCount = 0; transparent_uvCount = 0; transparent_normalCount = 0;
        cutout_vertexCount = 0; cutout_triangleCount = 0; cutout_uvCount = 0; cutout_normalCount = 0;
        buildMesh_currentX = 0; buildMesh_currentY = 0; buildMesh_currentZ = 0;
        ProcessMeshSlice();
    }

    public void ProcessMeshSlice()
    {
        if (!isBuildingMesh) return;
        int processedThisSlice = 0;

        for (int x = buildMesh_currentX; x < chunkSizeXZ; x++)
        {
            for (int y = buildMesh_currentY; y < chunkSizeY; y++)
            {
                for (int z = buildMesh_currentZ; z < chunkSizeXZ; z++)
                {
                    int currentGlobalX = chunkX + x; int currentGlobalY = chunkY + y; int currentGlobalZ = chunkZ + z;
                    byte currentBlockID = world.GetBlock(currentGlobalX, currentGlobalY, currentGlobalZ);

                    if (currentBlockID == 0) { processedThisSlice++; if (ShouldScheduleNextSlice(processedThisSlice,x,y,z)) return; continue; }

                    BlockVisibilityType currentVisibility = blockTypeManager.GetBlockVisibilityType(currentBlockID);
                    if (currentVisibility == BlockVisibilityType.Invisible) { processedThisSlice++; if (ShouldScheduleNextSlice(processedThisSlice,x,y,z)) return; continue; }

                    McChunk_MeshTarget targetMeshTypeForCurrentBlock; 
                    if (currentVisibility == BlockVisibilityType.Opaque) targetMeshTypeForCurrentBlock = McChunk_MeshTarget.Opaque;
                    else if (blockTypeManager.IsAnyCutoutType(currentVisibility)) targetMeshTypeForCurrentBlock = McChunk_MeshTarget.Cutout; // Updated to use helper
                    else targetMeshTypeForCurrentBlock = McChunk_MeshTarget.Transparent; // Covers all Transparent_* types

                    // Vector3[] directions = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back }; // Replaced by cached version
                    for (int i = 0; i < 6; i++)
                    {
                        Vector3 dir = processMeshDirections[i]; // Use cached array
                        byte neighborBlockID = world.GetBlock(currentGlobalX + (int)dir.x, currentGlobalY + (int)dir.y, currentGlobalZ + (int)dir.z);
                        bool drawFace = false;

                        if (neighborBlockID == 0) drawFace = true; // Neighbor is Air
                        else {
                            BlockVisibilityType neighborVisibility = blockTypeManager.GetBlockVisibilityType(neighborBlockID);
                            if (neighborVisibility == BlockVisibilityType.Invisible) drawFace = true; // Neighbor is Invisible
                            else {
                                // Common culling: All non-Opaque types are culled by Opaque neighbors
                                if (currentVisibility != BlockVisibilityType.Opaque && neighborVisibility == BlockVisibilityType.Opaque) {
                                    drawFace = false;
                                } else {
                                    // Specific culling rules
                                    switch (currentVisibility)
                                    {
                                        case BlockVisibilityType.Opaque:
                                            drawFace = (neighborVisibility != BlockVisibilityType.Opaque);
                                            break;
                                        // Transparent types (all already culled by Opaque above if current is not Opaque)
                                        case BlockVisibilityType.Transparent_NoCull:
                                            drawFace = true; // No further culling for this type beyond Opaque neighbor
                                            break;
                                        case BlockVisibilityType.Transparent_CullSelf:
                                        case BlockVisibilityType.Transparent_CullSelfAndOpaque: // Logic is same: cull self, Opaque already handled
                                            drawFace = (neighborBlockID != currentBlockID);
                                            break;
                                        
                                        // Cutout types (all already culled by Opaque above if current is not Opaque)
                                        case BlockVisibilityType.Cutout_CullOpaqueOnly:
                                            drawFace = true; // No further culling beyond Opaque neighbor
                                            break;
                                        case BlockVisibilityType.Cutout_CullSelf:
                                            drawFace = !(neighborBlockID == currentBlockID && blockTypeManager.IsSelfCullingCutout(neighborVisibility));
                                            break;
                                        case BlockVisibilityType.Cutout_CullSelfAndOtherCutout:
                                            if (blockTypeManager.IsAnyCutoutType(neighborVisibility)) drawFace = false; // Cull against any cutout (includes self)
                                            else drawFace = true;
                                            break;
                                    }
                                }
                            }
                        }
                        if (drawFace) AddFaceDataToPool(x, y, z, dir, currentBlockID, targetMeshTypeForCurrentBlock);
                    }
                    processedThisSlice++;
                    if (ShouldScheduleNextSlice(processedThisSlice, x, y, z)) return;
                }
                buildMesh_currentZ = 0;
            }
            buildMesh_currentY = 0;
        }
        FinalizeMeshBuild();
    }

    private bool ShouldScheduleNextSlice(int processedCount, int curX, int curY, int curZ) {
        if (voxelsPerSlice > 0 && processedCount >= voxelsPerSlice) {
            buildMesh_currentX = curX; buildMesh_currentY = curY; buildMesh_currentZ = curZ + 1;
            SendCustomEventDelayedFrames(nameof(ProcessMeshSlice), 1);
            return true;
        }
        return false;
    }

    void AddFaceDataToPool(int x, int y, int z, Vector3 direction, byte blockType, McChunk_MeshTarget targetMesh)
    {
        if (blockTypeManager == null) { 
            Debug.LogError($"[{gameObject.name}] AddFaceDataToPool: McBlockTypeManager is null. Aborting face.");
            isBuildingMesh = false; 
            return;
        }

        Vector3[] selectedVertexPool;
        int[] selectedTrianglePool;
        Vector2[] selectedUvPool;
        Vector3[] selectedNormalPool;
        
        int currentVertexPoolLength = 0;
        int currentTrianglePoolLength = 0;
        int vCount = 0, tCount = 0, uCount = 0, nC = 0; 

        switch (targetMesh) // Use renamed enum McChunk_MeshTarget
        {
            case McChunk_MeshTarget.Opaque:
                selectedVertexPool = opaque_vertexPool; selectedTrianglePool = opaque_trianglePool; selectedUvPool = opaque_uvPool; selectedNormalPool = opaque_normalPool;
                currentVertexPoolLength = opaque_vertexPool.Length; currentTrianglePoolLength = opaque_trianglePool.Length;
                vCount = opaque_vertexCount; tCount = opaque_triangleCount; uCount = opaque_uvCount; nC = opaque_normalCount;
                break;
            case McChunk_MeshTarget.Transparent:
                selectedVertexPool = transparent_vertexPool; selectedTrianglePool = transparent_trianglePool; selectedUvPool = transparent_uvPool; selectedNormalPool = transparent_normalPool;
                currentVertexPoolLength = transparent_vertexPool.Length; currentTrianglePoolLength = transparent_trianglePool.Length;
                vCount = transparent_vertexCount; tCount = transparent_triangleCount; uCount = transparent_uvCount; nC = transparent_normalCount;
                break;
            case McChunk_MeshTarget.Cutout:
                selectedVertexPool = cutout_vertexPool; selectedTrianglePool = cutout_trianglePool; selectedUvPool = cutout_uvPool; selectedNormalPool = cutout_normalPool;
                currentVertexPoolLength = cutout_vertexPool.Length; currentTrianglePoolLength = cutout_trianglePool.Length;
                vCount = cutout_vertexCount; tCount = cutout_triangleCount; uCount = cutout_uvCount; nC = cutout_normalCount;
                break;
            default: 
                Debug.LogError("Invalid McChunk_MeshTarget in AddFaceDataToPool"); return;
        }

        if (vCount + 4 > currentVertexPoolLength || tCount + 6 > currentTrianglePoolLength)
        { Debug.LogError($"[{gameObject.name}] Mesh data pool overflow for {targetMesh}! Aborting face add."); isBuildingMesh = false; return; }

        Vector3 p0, p1, p2, p3; Vector3 faceNormal = direction; float fx = (float)x, fy = (float)y, fz = (float)z;
        int faceIndex = 0; 
        if(direction == Vector3.right) faceIndex = 0;
        else if(direction == Vector3.left) faceIndex = 1;
        else if(direction == Vector3.up) faceIndex = 2;
        else if(direction == Vector3.down) faceIndex = 3;
        else if(direction == Vector3.forward) faceIndex = 4;
        else if(direction == Vector3.back) faceIndex = 5;
        Vector2 uv_atlas_origin = blockTypeManager.GetFinalBlockTextureUV(blockType, faceIndex);

        if (direction == Vector3.right) { p0=new Vector3(fx+1,fy,fz); p1=new Vector3(fx+1,fy,fz+1); p2=new Vector3(fx+1,fy+1,fz+1); p3=new Vector3(fx+1,fy+1,fz); }
        else if (direction == Vector3.left) { p0=new Vector3(fx,fy,fz+1); p1=new Vector3(fx,fy,fz); p2=new Vector3(fx,fy+1,fz); p3=new Vector3(fx,fy+1,fz+1); }
        else if (direction == Vector3.up) { p0=new Vector3(fx,fy+1,fz); p1=new Vector3(fx+1,fy+1,fz); p2=new Vector3(fx+1,fy+1,fz+1); p3=new Vector3(fx,fy+1,fz+1); }
        else if (direction == Vector3.down) { p0=new Vector3(fx,fy,fz+1); p1=new Vector3(fx+1,fy,fz+1); p2=new Vector3(fx+1,fy,fz); p3=new Vector3(fx,fy,fz); }
        else if (direction == Vector3.forward) { p0=new Vector3(fx+1,fy,fz+1); p1=new Vector3(fx,fy,fz+1); p2=new Vector3(fx,fy+1,fz+1); p3=new Vector3(fx+1,fy+1,fz+1); }
        else { p0=new Vector3(fx,fy,fz); p1=new Vector3(fx+1,fy,fz); p2=new Vector3(fx+1,fy+1,fz); p3=new Vector3(fx,fy+1,fz); } 

        selectedVertexPool[vCount + 0] = p0; selectedVertexPool[vCount + 1] = p1; selectedVertexPool[vCount + 2] = p2; selectedVertexPool[vCount + 3] = p3;
        selectedNormalPool[nC + 0] = faceNormal; selectedNormalPool[nC + 1] = faceNormal; selectedNormalPool[nC + 2] = faceNormal; selectedNormalPool[nC + 3] = faceNormal;
        
        // Standardized triangle winding for all faces (CCW when viewed from outside)
        selectedTrianglePool[tCount + 0] = vCount + 0; selectedTrianglePool[tCount + 1] = vCount + 2; selectedTrianglePool[tCount + 2] = vCount + 1;
        selectedTrianglePool[tCount + 3] = vCount + 0; selectedTrianglePool[tCount + 4] = vCount + 3; selectedTrianglePool[tCount + 5] = vCount + 2;
        
        float u_start = uv_atlas_origin.x * tUnit + uvPadding; float u_end = (uv_atlas_origin.x + 1) * tUnit - uvPadding;
        float v_start_flipped = 1.0f - (uv_atlas_origin.y + 1) * tUnit + uvPadding; float v_end_flipped = 1.0f - uv_atlas_origin.y * tUnit - uvPadding;
        selectedUvPool[uCount + 0] = new Vector2(u_start, v_start_flipped); selectedUvPool[uCount + 1] = new Vector2(u_end, v_start_flipped);
        selectedUvPool[uCount + 2] = new Vector2(u_end, v_end_flipped); selectedUvPool[uCount + 3] = new Vector2(u_start, v_end_flipped);
        
        switch (targetMesh) // Use renamed enum McChunk_MeshTarget
        {
            case McChunk_MeshTarget.Opaque:
                opaque_vertexCount += 4; opaque_triangleCount += 6; opaque_uvCount += 4; opaque_normalCount += 4;
                break;
            case McChunk_MeshTarget.Transparent:
                transparent_vertexCount += 4; transparent_triangleCount += 6; transparent_uvCount += 4; transparent_normalCount += 4;
                break;
            case McChunk_MeshTarget.Cutout:
                cutout_vertexCount += 4; cutout_triangleCount += 6; cutout_uvCount += 4; cutout_normalCount += 4;
                break;
        }
    }

    void FinalizeMeshBuild()
    {
        if (chunkMesh == null) { Debug.LogError("ChunkMesh is null in Finalize!"); isBuildingMesh = false; return; }
        chunkMesh.Clear();

        int totalVertices = opaque_vertexCount + transparent_vertexCount + cutout_vertexCount;
        if (totalVertices == 0)
        { // No visible blocks in this chunk
            if (meshCollider != null) meshCollider.sharedMesh = null;
            isBuildingMesh = false;
            return;
        }
        if (totalVertices > 65534) {
             Debug.LogError($"[{gameObject.name}] Combined mesh for chunk would exceed 65534 vertices ({totalVertices}). This is not supported by Unity for a single mesh. Consider smaller chunks or fewer details.");
             // Potentially clear pools and set collider to null to prevent further errors.
             if (meshCollider != null) meshCollider.sharedMesh = null; 
             isBuildingMesh = false;
             return;
        }

        Vector3[] finalVertices = new Vector3[totalVertices];
        Vector2[] finalUVs = new Vector2[totalVertices];
        Vector3[] finalNormals = new Vector3[totalVertices];

        int currentOffset = 0;
        if (opaque_vertexCount > 0) {
            System.Array.Copy(opaque_vertexPool, 0, finalVertices, currentOffset, opaque_vertexCount);
            System.Array.Copy(opaque_uvPool, 0, finalUVs, currentOffset, opaque_uvCount);
            System.Array.Copy(opaque_normalPool, 0, finalNormals, currentOffset, opaque_normalCount);
            currentOffset += opaque_vertexCount;
        }
        if (transparent_vertexCount > 0) {
            System.Array.Copy(transparent_vertexPool, 0, finalVertices, currentOffset, transparent_vertexCount);
            System.Array.Copy(transparent_uvPool, 0, finalUVs, currentOffset, transparent_uvCount);
            System.Array.Copy(transparent_normalPool, 0, finalNormals, currentOffset, transparent_normalCount);
            currentOffset += transparent_vertexCount;
        }
        if (cutout_vertexCount > 0) {
            System.Array.Copy(cutout_vertexPool, 0, finalVertices, currentOffset, cutout_vertexCount);
            System.Array.Copy(cutout_uvPool, 0, finalUVs, currentOffset, cutout_uvCount);
            System.Array.Copy(cutout_normalPool, 0, finalNormals, currentOffset, cutout_normalCount);
        }

        chunkMesh.vertices = finalVertices;
        chunkMesh.uv = finalUVs;
        chunkMesh.normals = finalNormals;

        chunkMesh.subMeshCount = 3; // Always 3 for Opaque, Transparent, Cutout order

        // Opaque triangles
        int[] finalOpaqueTriangles = new int[opaque_triangleCount];
        if (opaque_triangleCount > 0) System.Array.Copy(opaque_trianglePool, finalOpaqueTriangles, opaque_triangleCount);
        chunkMesh.SetTriangles(finalOpaqueTriangles, 0);

        // Transparent triangles (offset by opaque vertex count)
        int[] finalTransparentTriangles = new int[transparent_triangleCount];
        for (int i = 0; i < transparent_triangleCount; i++) finalTransparentTriangles[i] = transparent_trianglePool[i] + opaque_vertexCount;
        chunkMesh.SetTriangles(finalTransparentTriangles, 1);

        // Cutout triangles (offset by opaque + transparent vertex count)
        int[] finalCutoutTriangles = new int[cutout_triangleCount];
        int transparentOffset = opaque_vertexCount + transparent_vertexCount;
        for (int i = 0; i < cutout_triangleCount; i++) finalCutoutTriangles[i] = cutout_trianglePool[i] + transparentOffset;
        chunkMesh.SetTriangles(finalCutoutTriangles, 2);

        chunkMesh.RecalculateBounds(); // Important for culling and performance
        // Normals are set per-face, RecalculateNormals() might smooth them if that's desired, but usually not for blocky style.

        if (meshCollider != null) meshCollider.sharedMesh = chunkMesh; 
        // If only opaque should collide, a separate mesh would be needed for the collider, 
        // or the collider could be configured to ignore transparent/cutout layers if possible (not standard with MeshCollider).

        isBuildingMesh = false; 
    }

    public void SetWorld(McWorld newWorld)
    {
        world = newWorld;
        if (!isInitialized && !template) InitializeChunk();
    }
}