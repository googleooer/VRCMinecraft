using UdonSharp;
using UnityEngine;
using VRC.SDKBase; 
using VRRefAssist; 

// Enum for texture mapping, should be accessible by McChunk and the Editor script
public enum McBlockTextureMappingType 
{
    AllFacesSame, 
    TopBottomSides, 
    // UniquePerFace // Future consideration
}

// Enum for Block Visibility
public enum BlockVisibilityType
{
    Opaque,                             // Standard solid block, culls neighbors.
    Transparent_NoCull,                 // Transparent, never culls itself or neighbors (except Opaque).
    Transparent_CullSelf,               // Transparent, culls faces against same block type AND Opaque blocks (e.g., water).
    Transparent_CullSelfAndOpaque,      // Transparent, culls against same block type AND Opaque blocks (e.g., glass).
    
    Cutout_CullOpaqueOnly,              // Default Cutout: Only culls against Opaque blocks.
    Cutout_CullSelf,                    // Cutout: Culls against Opaque AND itself (if neighbor is same type).
    Cutout_CullSelfAndOtherCutout,      // Cutout: Culls against Opaque, itself, AND any other Cutout type.

    Invisible                           // No mesh generated, but still occupies space.
}

[Singleton] // VRRefAssist attribute
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
// Uses McBlockTypeManagerEditor.cs for the inspector
public class McBlockTypeManager : UdonSharpBehaviour
{
    [Header("Global Atlas Settings")]
    [Tooltip("Texture unit size for the atlas (e.g., 1/16 = 0.0625 for a 16x16 grid of textures in the atlas).")]
    public float textureAtlasTUnit = 0.0625f;
    [Tooltip("Small padding to prevent texture bleeding from adjacent textures in the atlas.")]
    public float textureAtlasUVPadding = 0.0001f;

    [Header("Block Definitions (Parallel Arrays)")]
    [Tooltip("Set the total number of block types. All arrays below should match this size.")]
    public int numberOfBlockTypes = 1; 

    [Tooltip("Names for each block type.")]
    public string[] blockNames; 
    
    [Tooltip("Is the block solid? (Affects physics, interaction, light passage if not generating mesh)")]
    public bool[] isSolidData;
    [Tooltip("Defines how the block is rendered and culled. See BlockVisibilityType enum.")]
    public int[] blockVisibilityTypeData; // Stores BlockVisibilityType as int

    // UV Atlas Coordinates (Vector2: X=column, Y=row from top-left of atlas image)
    [Tooltip("UV coordinates if all faces use the same texture (Column, Row).")]
    public Vector2[] uv_allFacesData;
    [Tooltip("UV coordinates for the top face (Column, Row).")]
    public Vector2[] uv_topFaceData;
    [Tooltip("UV coordinates for the bottom face (Column, Row).")]
    public Vector2[] uv_bottomFaceData;
    [Tooltip("UV coordinates for the side faces (Column, Row).")]
    public Vector2[] uv_sideFacesData;
    
    [Tooltip("Texture mapping strategy for each block type. 0=AllFacesSame, 1=TopBottomSides.")]
    public int[] textureMappingTypeData; // Stores McBlockTextureMappingType as int

    [Header("Audio (Assign one sound or leave empty)")]
    public AudioClip[] breakSounds; 
    public AudioClip[] placeSounds;
    public AudioClip[] footstepSounds;

    [Header("Particles (Assign Prefabs or leave empty)")]
    public ParticleSystem[] breakParticlesPrefabData;
    public ParticleSystem[] placeParticlesPrefabData;

    void Start()
    {
        // Rely on VRRefAssist for singleton access by other scripts.
        // Basic validation for array lengths can be done here if needed,
        // but the custom editor is the primary place for configuration.
        if (blockNames == null || blockNames.Length != numberOfBlockTypes) {
            Debug.LogError($"[McBlockTypeManager] Configuration error: 'blockNames' array size does not match 'numberOfBlockTypes'. Expected {numberOfBlockTypes}, got {(blockNames != null ? blockNames.Length : -1)}. Please use the 'Apply Number of Types' button in the Inspector.");
        }
        // Similar checks can be added for other arrays.
        
        Debug.Log($"[McBlockTypeManager] Initialized. Expecting {numberOfBlockTypes} block types. Atlas tUnit: {textureAtlasTUnit}, uvPadding: {textureAtlasUVPadding}.");
    }

    // --- Getter Methods for Block Properties ---

    public string GetBlockName(byte blockID) {
        if (blockNames != null && blockID >= 0 && blockID < blockNames.Length) {
            return blockNames[blockID];
        }
        Debug.LogWarning($"[McBlockTypeManager] GetBlockName: Invalid ID {blockID}.");
        return "Unknown Block";
    }

    public bool GetBlockIsSolid(byte blockID)
    {
        if (isSolidData != null && blockID >= 0 && blockID < isSolidData.Length)
        {
            return isSolidData[blockID];
        }
        Debug.LogWarning($"[McBlockTypeManager] GetBlockIsSolid: Invalid ID {blockID}. Defaulting to false.");
        return false; 
    }

    public BlockVisibilityType GetBlockVisibilityType(byte blockID)
    {
        if (blockVisibilityTypeData != null && blockID >= 0 && blockID < blockVisibilityTypeData.Length)
        {
            return (BlockVisibilityType)blockVisibilityTypeData[blockID];
        }
        Debug.LogWarning($"[McBlockTypeManager] GetBlockVisibilityType: Invalid ID {blockID}. Defaulting to Opaque.");
        return BlockVisibilityType.Opaque;
    }

    // Specific UV getters (atlas column, row)
    public Vector2 GetBlockTextureUV_AllFaces(byte blockID) {
        if (uv_allFacesData != null && blockID >= 0 && blockID < uv_allFacesData.Length) {
            return uv_allFacesData[blockID];
        }
        return Vector2.zero;
    }
    public Vector2 GetBlockTextureUV_TopFace(byte blockID) {
        if (uv_topFaceData != null && blockID >= 0 && blockID < uv_topFaceData.Length) {
            return uv_topFaceData[blockID];
        }
        return Vector2.zero;
    }
    public Vector2 GetBlockTextureUV_BottomFace(byte blockID) {
        if (uv_bottomFaceData != null && blockID >= 0 && blockID < uv_bottomFaceData.Length) {
            return uv_bottomFaceData[blockID];
        }
        return Vector2.zero;
    }
    public Vector2 GetBlockTextureUV_SideFaces(byte blockID) {
        if (uv_sideFacesData != null && blockID >= 0 && blockID < uv_sideFacesData.Length) {
            return uv_sideFacesData[blockID];
        }
        return Vector2.zero;
    }
    
    public int GetBlockTextureMappingTypeAsInt(byte blockID) { 
        if (textureMappingTypeData != null && blockID >= 0 && blockID < textureMappingTypeData.Length) {
            return textureMappingTypeData[blockID];
        }
        return (int)McBlockTextureMappingType.AllFacesSame; 
    }

    // Helper to get the correct base UV atlas coordinates for a given face
    public Vector2 GetFinalBlockTextureUV(byte blockID, int faceIndex) 
    {
        // faceIndex: 0=right(+X), 1=left(-X), 2=up(+Y), 3=down(-Y), 4=forward(+Z), 5=back(-Z)
        if (blockID >= 0 && blockID < numberOfBlockTypes) 
        {
             McBlockTextureMappingType mappingType = (McBlockTextureMappingType)GetBlockTextureMappingTypeAsInt(blockID);
             switch (mappingType)
             {
                 case McBlockTextureMappingType.AllFacesSame:
                     return GetBlockTextureUV_AllFaces(blockID);
                 case McBlockTextureMappingType.TopBottomSides:
                     if (faceIndex == 2) return GetBlockTextureUV_TopFace(blockID);    
                     if (faceIndex == 3) return GetBlockTextureUV_BottomFace(blockID); 
                     return GetBlockTextureUV_SideFaces(blockID);                      
                 default:
                     Debug.LogWarning($"[McBlockTypeManager] GetFinalBlockTextureUV: Unknown textureMapping type for block ID {blockID}. Defaulting to AllFaces.");
                     return GetBlockTextureUV_AllFaces(blockID); 
             }
        }
        Debug.LogWarning($"[McBlockTypeManager] GetFinalBlockTextureUV: Invalid block ID {blockID}. Defaulting to UV (0,0).");
        return Vector2.zero;
    }

    public AudioClip GetBreakSound(byte blockID) 
    {
        if (breakSounds != null && blockID >= 0 && blockID < breakSounds.Length) {
            return breakSounds[blockID]; 
        }
        return null;
    }

    public AudioClip GetPlaceSound(byte blockID) 
    {
        if (placeSounds != null && blockID >= 0 && blockID < placeSounds.Length) {
            return placeSounds[blockID];
        }
        return null;
    }

     public AudioClip GetFootstepSound(byte blockID) 
    {
        if (footstepSounds != null && blockID >= 0 && blockID < footstepSounds.Length) {
            return footstepSounds[blockID];
        }
        return null;
    }

    public ParticleSystem GetBreakParticlesPrefab(byte blockID)
    {
        if (breakParticlesPrefabData != null && blockID >= 0 && blockID < breakParticlesPrefabData.Length) {
            return breakParticlesPrefabData[blockID];
        }
        return null;
    }
    
    public ParticleSystem GetPlaceParticlesPrefab(byte blockID)
    {
        if (placeParticlesPrefabData != null && blockID >= 0 && blockID < placeParticlesPrefabData.Length) {
            return placeParticlesPrefabData[blockID];
        }
        return null;
    }

    // Helper methods for culling logic in McChunk
    public bool IsAnyCutoutType(BlockVisibilityType visibilityType)
    {
        return visibilityType == BlockVisibilityType.Cutout_CullOpaqueOnly || 
               visibilityType == BlockVisibilityType.Cutout_CullSelf || 
               visibilityType == BlockVisibilityType.Cutout_CullSelfAndOtherCutout;
    }

    // Helper to check if a cutout type is one that culls against itself
    public bool IsSelfCullingCutout(BlockVisibilityType visibilityType)
    {
        return visibilityType == BlockVisibilityType.Cutout_CullSelf ||
               visibilityType == BlockVisibilityType.Cutout_CullSelfAndOtherCutout;
    }
}
