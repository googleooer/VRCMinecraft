
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using Varneon.VUdon.ArrayExtensions;
using System.Linq;
using System;


public enum BlockTextureType{
        CUBE_ALL,
        CUBE_COLUMN,
        CUBE_COLUMN_HORIZONTAL,
        BLOCK,
        TINTED_BLOCK,
        ORIENTABLE,
        DIRECTIONAL_TOP_BOTTOM,



        CROSS,
        TINTED_CROSS

    }

    public enum Direction{
        NORTH,
        SOUTH,
        EAST,
        WEST,
        UP,
        DOWN
    }

public class DebugVoxelCreator : UdonSharpBehaviour
{

    

    

    public GameObject cubeGameObjectTarget;
    Mesh cachedChunkMesh;
    GameObject theChunk;

    [Header("Material Assignment")]
    public int[] blockTypeMaterials;
    public BlockTextureType[] blockTextureTypes;

    [Header("Material Settings")]
    /// <summary>
    /// 
    /// The textures to be assigned to the block, count starts at 0, goes to 255. Match to texture atlas. The parsing structure for each BlockTextureType is as follows:
    /// CUBE_ALL: Single number used.
    /// CUBE_COLUMN: First number is top and bottom, Second is sides. E.g 21,20 for oak log.
    /// CUBE_COLUMN_HORIZONTAL: Same as CUBE_COLUM.
    /// BLOCK: First bottom, Second top, Third sides.
    /// TINTED_BLOCK: Same as BLOCK.
    /// ORIENTABLE: First front, Second side, Third Top
    /// 
    /// CROSS: Single number used.
    /// TINTED_CROSS: Same as CROSS.
    /// 
    /// 
    /// </summary>
    public int[] blockFaceAssignments;

    void Start()
    { 
        GenChunk(16,4);
    }

    Vector2[] rotateTriangleUvClockwise(Vector2[] uvs, int multiplier)
    {
        if(uvs.Length != 3)
        {
            Debug.LogWarning("Attempted to rotate triangle uvs clockwise, but uv count wasn't exactly 3");
            return uvs;
        }
        //We take in triangle UVs and rotate them clockwise.
        // Perform the 90-degree clockwise rotation
        Vector2 rotatedUV1 = new Vector2(uvs[0].y, -uvs[0].x);
        Vector2 rotatedUV2 = new Vector2(uvs[1].y, -uvs[1].x);
        Vector2 rotatedUV3 = new Vector2(uvs[2].y, -uvs[2].x);

        // Return the rotated UVs in the original order
        return new Vector2[] { rotatedUV1, rotatedUV2, rotatedUV3 };
    }

    void GenChunk(int chunkWidth, int chunkHeight)
    {
        for(int x = 0; x < chunkWidth; x++)
        {
            for(int z = 0; z < chunkWidth; z++)
            {
                for(int y = 0; y < chunkHeight; y++)
                {
                    if(PerlinAtPosition(x,y,z) > 0.5) CreateVoxelAtPos(x,y,z, 0);

                }
            }
        }
    }

    float PerlinAtPosition(int posX, int posY, int posZ)
    {
        return 1;
    }



    Vector2Int getBlockTextureOffset(int blockType)
    {
        return new Vector2Int(0,0);
    }

    void CreateVoxelAtPos(int posX, int posY, int posZ, byte blockType)
    {
        // Define the vertices of a cube
        Vector3[] vertices = new Vector3[]
        {
            // Front face
            new Vector3( posX-0.5f, posY-0.5f,  posZ+0.5f), // 0
            new Vector3( posX+0.5f, posY-0.5f,  posZ+0.5f), // 1
            new Vector3( posX+0.5f, posY+0.5f,  posZ+0.5f), // 2
            new Vector3( posX-0.5f, posY+0.5f,  posZ+0.5f), // 3

            // Back face
            new Vector3( posX-0.5f, posY-0.5f, posZ-0.5f), // 4
            new Vector3( posX+0.5f, posY-0.5f, posZ-0.5f), // 5
            new Vector3( posX+0.5f, posY+0.5f, posZ-0.5f), // 6
            new Vector3( posX-0.5f, posY+0.5f, posZ-0.5f), // 7

            // Top face
            new Vector3( posX-0.5f, posY+0.5f, posZ-0.5f), // 8
            new Vector3( posX+0.5f, posY+0.5f, posZ-0.5f), // 9
            new Vector3( posX+0.5f, posY+0.5f, posZ+0.5f), // 10
            new Vector3( posX-0.5f, posY+0.5f, posZ+0.5f), // 11

            // Bottom face
            new Vector3( posX-0.5f, posY-0.5f, posZ-0.5f), // 12
            new Vector3( posX+0.5f, posY-0.5f, posZ-0.5f), // 13
            new Vector3( posX+0.5f, posY-0.5f, posZ+0.5f), // 14
            new Vector3( posX-0.5f, posY-0.5f, posZ+0.5f), // 15

            // Left face
            new Vector3( posX-0.5f, posY-0.5f, posZ-0.5f), // 16
            new Vector3( posX-0.5f, posY+0.5f, posZ-0.5f), // 17
            new Vector3( posX-0.5f, posY+0.5f, posZ+0.5f), // 18
            new Vector3( posX-0.5f, posY-0.5f, posZ+0.5f), // 19

            // Right face
            new Vector3( posX+0.5f, posY-0.5f, posZ-0.5f), // 20
            new Vector3( posX+0.5f, posY+0.5f, posZ-0.5f), // 21
            new Vector3( posX+0.5f, posY+0.5f, posZ+0.5f), // 22
            new Vector3( posX+0.5f, posY-0.5f, posZ+0.5f), // 23
        };

        // Define the triangles (two for each face)
        int[] triangles = new int[]
        {
            // Front face
            0, 1, 2,
            0, 2, 3,
            
            // Back face
            4, 6, 5,
            4, 7, 6,
            
            // Top face
            8, 10, 9,
            8, 11, 10,
            
            // Bottom face
            12, 13, 14,
            12, 14, 15,
            
            // Left face
            16, 18, 17,
            16, 19, 18,
            
            // Right face
            20, 21, 22,
            20, 22, 23
        };

        // Define UVs for the vertices
        float uvYOff = 1;
        float uvXOff = 0.0625f*4;
        Vector2[] uvs = new Vector2[]
        {
            // Front face
            new Vector2(uvXOff+0.0f, -uvYOff-0.0f), // 0
            new Vector2(uvXOff+0.0625f, -uvYOff-0.0f), // 1
            new Vector2(uvXOff+0.0625f, -uvYOff-0.0625f), // 2
            new Vector2(uvXOff+0.0f, -uvYOff-0.0625f), // 3

            // Back face
            new Vector2(uvXOff+0.0f, -uvYOff-0.0f), // 4
            new Vector2(uvXOff+0.0625f, -uvYOff-0.0f), // 5
            new Vector2(uvXOff+0.0625f, -uvYOff-0.0625f), // 6
            new Vector2(uvXOff+0.0f, -uvYOff-0.0625f), // 7

            // Top face
            new Vector2(uvXOff+0.0f, uvYOff-0.0f), // 8
            new Vector2(uvXOff+0.0625f, uvYOff-0.0f), // 9
            new Vector2(uvXOff+0.0625f, uvYOff-0.0625f), // 10
            new Vector2(uvXOff+0.0f, uvYOff-0.0625f), // 11

            // Bottom face
            new Vector2(uvXOff+0.0f, uvYOff-0.0f), // 12
            new Vector2(uvXOff+0.0625f, uvYOff-0.0f), // 13
            new Vector2(uvXOff+0.0625f, uvYOff-0.0625f), // 14
            new Vector2(uvXOff+0.0f, uvYOff-0.0625f), // 15

            // Left face
            new Vector2(uvXOff+0.0f, uvYOff-0.0f), // 16
            new Vector2(uvXOff+0.0625f, uvYOff-0.0f), // 17
            new Vector2(uvXOff+0.0625f, uvYOff-0.0625f), // 18
            new Vector2(uvXOff+0.0f, uvYOff-0.0625f), // 19

            // Right face
            new Vector2(uvXOff+0.0f, uvYOff-0.0f), // 20
            new Vector2(uvXOff+0.0625f, uvYOff-0.0f), // 21
            new Vector2(uvXOff+0.0625f, uvYOff-0.0625f), // 22
            new Vector2(uvXOff+0.0f, uvYOff-0.0625f), // 23
        };

        // Create a new mesh and assign vertices, triangles, and uvs
        if(cachedChunkMesh == null)
        {
            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.uv = uvs;
            mesh.Optimize();
            mesh.RecalculateNormals();
            cachedChunkMesh = mesh;


            // Create a new GameObject and add MeshFilter and MeshRenderer components
            theChunk = Instantiate(cubeGameObjectTarget);
        }
        else
        {
            // Since we already have a cached chunk mesh, we need to append the new vertices one by one, and we also need to offset the triangle references by the number of
            // triangles in the already cached chunk mesh.
            int triangleOffset = cachedChunkMesh.vertices.Length;
            Vector3[] newVertices = cachedChunkMesh.vertices;
            int[] newTriangles = cachedChunkMesh.triangles;
            Vector2[] newUVs = cachedChunkMesh.uv;
            for(int i = 0; i < vertices.Length; i++)
            {
                newVertices = newVertices.Add(vertices[i]);
            }

            for(int i = 0; i < triangles.Length; i++)
            {
                newTriangles = newTriangles.Add(triangles[i] + triangleOffset);
            }

            for(int i = 0; i < uvs.Length; i++)
            {
                newUVs = newUVs.Add(uvs[i]);
            }
            cachedChunkMesh.vertices = newVertices;
            cachedChunkMesh.triangles = newTriangles;
            cachedChunkMesh.uv = newUVs;
            cachedChunkMesh.Optimize();
            cachedChunkMesh.RecalculateNormals();
        }
        
        MeshFilter meshFilter = theChunk.GetComponent<MeshFilter>();
        MeshRenderer meshRenderer = theChunk.GetComponent<MeshRenderer>();
        MeshCollider meshCollider = theChunk.GetComponent<MeshCollider>();

        

        // Assign the mesh to the MeshFilter and MeshCollider;
        meshFilter.mesh = cachedChunkMesh;
        meshCollider.sharedMesh = cachedChunkMesh;
    }
}