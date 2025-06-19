using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRRefAssist;
using System.Text; 

// Enums McBlockTextureMappingType and BlockVisibilityType should be defined globally or accessible.

/// <summary>
/// Defines how block textures are mapped to faces.
/// </summary>
public enum McBlockTextureMappingType
{
    AllFacesSame,
    TopBottomSides,
}

/// <summary>
/// Defines how a block is rendered and how it affects face culling of itself and neighbors.
/// </summary>
public enum BlockVisibilityType
{
    Opaque,
    Transparent,
    Transparent_NoCull, // DEPRECATED, replace with BlockCullingType
    Transparent_CullSelf, // DEPRECATED, replace with BlockCullingType
    Transparent_CullSelfAndOpaque, // DEPRECATED, replace with BlockCullingType
    Cutout,
    Cutout_CullOpaqueOnly, // DEPRECATED, replace with BlockCullingType
    Cutout_CullSelf, // DEPRECATED, replace with BlockCullingType
    Cutout_CullSelfAndOtherCutout, // DEPRECATED, replace with BlockCullingType
    Invisible
}

public enum BlockCullingType
{
    NoCull,
    CullSelf,
    CullSelfAndOpaque,
    CullSelfAndCutout,
    CullSelfAndTransparent,
    CullAll
}

/// <summary>
/// Defines the basic mesh shape of a block.
/// </summary>
public enum McBlockShapeType
{
    Cube,
    Cross
}

// REMOVED: AudioClipArrayWrapper class

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McBlockTypeManager : UdonSharpBehaviour
{
    [Header("Texture Array for Editor Preview")]
    [Tooltip("Assign the Texture 2D Array used for block textures here to enable slice previews in the editor.")]
    public Texture2DArray previewTextureArray;

    [Header("Block Definitions (Parallel Arrays)")]
    public int numberOfBlockTypes = 1;
    public string[] blockNames;
    public bool[] isSolidData;
    public int[] blockVisibilityTypeData;
    public int[] blockShapeTypeData; 

    public int[] uv_allFacesData;
    public int[] uv_topFaceData;
    public int[] uv_bottomFaceData;
    public int[] uv_sideFacesData;
    public int[] textureMappingTypeData;

    [Header("Audio")]
    // MODIFIED: Reverted to AudioClip[][]
    public AudioClip[][] breakSounds; 
    public AudioClip[][] placeSounds; 
    public AudioClip[][] footstepSounds; 

    [Header("Fallback Audio (if block-specific is not set)")]
    public AudioClip[] fallbackBreakSounds; 
    public AudioClip[] fallbackPlaceSounds; 
    public AudioClip[] fallbackFootstepSounds; 

    [Header("Particles")]
    public ParticleSystem[] breakParticlesPrefabData;
    public ParticleSystem[] placeParticlesPrefabData;

    [Header("Logging")]
    #if UNITY_EDITOR
    public bool enableVerboseLogging = true;
    #endif
    private StringBuilder logBuilder;

    // Pre-filtered audio clips (jagged array)
    private AudioClip[][] _prefilteredBreakSounds;
    private AudioClip[][] _prefilteredPlaceSounds;
    private AudioClip[][] _prefilteredFootstepSounds;
    private AudioClip[] _prefilteredFallbackBreakSounds;
    private AudioClip[] _prefilteredFallbackPlaceSounds;
    private AudioClip[] _prefilteredFallbackFootstepSounds;


    void Start()
    {
        float startTime = Time.realtimeSinceStartup;
        logBuilder = new StringBuilder(256); 

        bool arraysValid = true;
        if (numberOfBlockTypes < 0) numberOfBlockTypes = 0;

        // Validate array sizes against numberOfBlockTypes
        string errorFormat = "[McBlockTypeManager.Start] '{0}' array size mismatch. Expected {1}, got {2}.";
        if (blockNames == null || blockNames.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "blockNames", numberOfBlockTypes, (blockNames != null ? blockNames.Length : -1))); arraysValid = false; }
        if (isSolidData == null || isSolidData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "isSolidData", numberOfBlockTypes, (isSolidData != null ? isSolidData.Length : -1))); arraysValid = false; }
        if (blockVisibilityTypeData == null || blockVisibilityTypeData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "blockVisibilityTypeData", numberOfBlockTypes, (blockVisibilityTypeData != null ? blockVisibilityTypeData.Length : -1))); arraysValid = false; }
        if (blockShapeTypeData == null || blockShapeTypeData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "blockShapeTypeData", numberOfBlockTypes, (blockShapeTypeData != null ? blockShapeTypeData.Length : -1))); arraysValid = false; }
        if (uv_allFacesData == null || uv_allFacesData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "uv_allFacesData", numberOfBlockTypes, (uv_allFacesData != null ? uv_allFacesData.Length : -1))); arraysValid = false; }
        if (uv_topFaceData == null || uv_topFaceData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "uv_topFaceData", numberOfBlockTypes, (uv_topFaceData != null ? uv_topFaceData.Length : -1))); arraysValid = false; }
        if (uv_bottomFaceData == null || uv_bottomFaceData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "uv_bottomFaceData", numberOfBlockTypes, (uv_bottomFaceData != null ? uv_bottomFaceData.Length : -1))); arraysValid = false; }
        if (uv_sideFacesData == null || uv_sideFacesData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "uv_sideFacesData", numberOfBlockTypes, (uv_sideFacesData != null ? uv_sideFacesData.Length : -1))); arraysValid = false; }
        if (textureMappingTypeData == null || textureMappingTypeData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "textureMappingTypeData", numberOfBlockTypes, (textureMappingTypeData != null ? textureMappingTypeData.Length : -1))); arraysValid = false; }
        
        if (breakSounds == null || breakSounds.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "breakSounds (AudioClip[][])", numberOfBlockTypes, (breakSounds != null ? breakSounds.Length : -1))); arraysValid = false; }
        if (placeSounds == null || placeSounds.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "placeSounds (AudioClip[][])", numberOfBlockTypes, (placeSounds != null ? placeSounds.Length : -1))); arraysValid = false; }
        if (footstepSounds == null || footstepSounds.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "footstepSounds (AudioClip[][])", numberOfBlockTypes, (footstepSounds != null ? footstepSounds.Length : -1))); arraysValid = false; }
        
        if (breakParticlesPrefabData == null || breakParticlesPrefabData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "breakParticlesPrefabData", numberOfBlockTypes, (breakParticlesPrefabData != null ? breakParticlesPrefabData.Length : -1))); arraysValid = false; }
        if (placeParticlesPrefabData == null || placeParticlesPrefabData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "placeParticlesPrefabData", numberOfBlockTypes, (placeParticlesPrefabData != null ? placeParticlesPrefabData.Length : -1))); arraysValid = false; }

        PreFilterAllSounds();

#if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            logBuilder.Clear();
            logBuilder.AppendFormat("[McBlockTypeManager.Start] Initialized. Expecting {0} block types. Arrays valid: {1}. Audio pre-filtered. Time: {2:F2} ms.",
                numberOfBlockTypes, arraysValid, (Time.realtimeSinceStartup - startTime) * 1000f);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }

    private void PreFilterAllSounds()
    {
        _prefilteredBreakSounds = new AudioClip[numberOfBlockTypes][];
        _prefilteredPlaceSounds = new AudioClip[numberOfBlockTypes][];
        _prefilteredFootstepSounds = new AudioClip[numberOfBlockTypes][];

        for (int i = 0; i < numberOfBlockTypes; i++)
        {
            // Pre-filter breakSounds
            // Ensure breakSounds itself is not null and index i is valid for breakSounds outer array
            if (breakSounds != null && i < breakSounds.Length && breakSounds[i] != null)
            {
                _prefilteredBreakSounds[i] = _prefilterClipArray(breakSounds[i]);
            }
            else
            {
                _prefilteredBreakSounds[i] = new AudioClip[0]; // Default to empty if source is problematic
            }

            // Pre-filter placeSounds
            if (placeSounds != null && i < placeSounds.Length && placeSounds[i] != null)
            {
                _prefilteredPlaceSounds[i] = _prefilterClipArray(placeSounds[i]);
            }
            else
            {
                _prefilteredPlaceSounds[i] = new AudioClip[0];
            }

            // Pre-filter footstepSounds
            if (footstepSounds != null && i < footstepSounds.Length && footstepSounds[i] != null)
            {
                _prefilteredFootstepSounds[i] = _prefilterClipArray(footstepSounds[i]);
            }
            else
            {
                _prefilteredFootstepSounds[i] = new AudioClip[0];
            }
        }
        
        _prefilteredFallbackBreakSounds = _prefilterClipArray(fallbackBreakSounds);
        _prefilteredFallbackPlaceSounds = _prefilterClipArray(fallbackPlaceSounds);
        _prefilteredFallbackFootstepSounds = _prefilterClipArray(fallbackFootstepSounds);
    }
    
    private AudioClip[] _prefilterClipArray(AudioClip[] source)
    {
        if (source == null || source.Length == 0) return new AudioClip[0];

        int validCount = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != null) validCount++;
        }

        if (validCount == 0) return new AudioClip[0];
        
        AudioClip[] filtered = new AudioClip[validCount];
        int currentIndex = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != null)
            {
                filtered[currentIndex] = source[i];
                currentIndex++;
            }
        }
        return filtered;
    }


    // --- Getter Methods ---

    public string GetBlockName(byte blockID)
    {
        if (blockNames != null && blockID >= 0 && blockID < blockNames.Length) return blockNames[blockID];
#if UNITY_EDITOR
        if (enableVerboseLogging) Debug.LogWarning($"[McBlockTypeManager.GetBlockName] Invalid ID {blockID}.");
#endif
        return "Unknown Block";
    }

    public bool GetBlockIsSolid(byte blockID)
    {
        if (isSolidData != null && blockID >= 0 && blockID < isSolidData.Length) return isSolidData[blockID];
#if UNITY_EDITOR
        if (enableVerboseLogging) Debug.LogWarning($"[McBlockTypeManager.GetBlockIsSolid] Invalid ID {blockID}. Defaulting to false.");
#endif
        return false;
    }

    public BlockVisibilityType GetBlockVisibilityType(byte blockID)
    {
        if (blockVisibilityTypeData != null && blockID >= 0 && blockID < blockVisibilityTypeData.Length) return (BlockVisibilityType)blockVisibilityTypeData[blockID];
#if UNITY_EDITOR
        if (enableVerboseLogging) Debug.LogWarning($"[McBlockTypeManager.GetBlockVisibilityType] Invalid ID {blockID}. Defaulting to Opaque.");
#endif
        return BlockVisibilityType.Opaque;
    }

    public McBlockShapeType GetBlockShapeType(byte blockID)
    {
        if (blockShapeTypeData != null && blockID >= 0 && blockID < blockShapeTypeData.Length) return (McBlockShapeType)blockShapeTypeData[blockID];
#if UNITY_EDITOR
        if (enableVerboseLogging) Debug.LogWarning($"[McBlockTypeManager.GetBlockShapeType] Invalid ID {blockID}. Defaulting to Cube.");
#endif
        return McBlockShapeType.Cube;
    }


    public int GetBlockTextureSlice_AllFaces(byte blockID)
    {
        if (uv_allFacesData != null && blockID >= 0 && blockID < uv_allFacesData.Length) return uv_allFacesData[blockID];
        return 0;
    }
    public int GetBlockTextureSlice_TopFace(byte blockID)
    {
        if (uv_topFaceData != null && blockID >= 0 && blockID < uv_topFaceData.Length) return uv_topFaceData[blockID];
        return 0;
    }
    public int GetBlockTextureSlice_BottomFace(byte blockID)
    {
        if (uv_bottomFaceData != null && blockID >= 0 && blockID < uv_bottomFaceData.Length) return uv_bottomFaceData[blockID];
        return 0;
    }
    public int GetBlockTextureSlice_SideFaces(byte blockID)
    {
        if (uv_sideFacesData != null && blockID >= 0 && blockID < uv_sideFacesData.Length) return uv_sideFacesData[blockID];
        return 0;
    }

    public int GetBlockTextureMappingTypeAsInt(byte blockID)
    {
        if (textureMappingTypeData != null && blockID >= 0 && blockID < textureMappingTypeData.Length) return textureMappingTypeData[blockID];
        return (int)McBlockTextureMappingType.AllFacesSame;
    }

    public int GetFinalBlockTextureSlice(byte blockID, int faceIndex)
    {
        if (blockID >= 0 && blockID < numberOfBlockTypes) 
        {
            McBlockTextureMappingType mappingType = (McBlockTextureMappingType)GetBlockTextureMappingTypeAsInt(blockID);
            switch (mappingType)
            {
                case McBlockTextureMappingType.AllFacesSame: return GetBlockTextureSlice_AllFaces(blockID);
                case McBlockTextureMappingType.TopBottomSides:
                    if (faceIndex == 2) return GetBlockTextureSlice_TopFace(blockID); 
                    if (faceIndex == 3) return GetBlockTextureSlice_BottomFace(blockID); 
                    return GetBlockTextureSlice_SideFaces(blockID); 
                default:
#if UNITY_EDITOR
                    if(enableVerboseLogging) Debug.LogWarning($"[McBlockTypeManager.GetFinalBlockTextureSlice] Unknown mapping type for block ID {blockID}. Defaulting to AllFaces.");
#endif
                    return GetBlockTextureSlice_AllFaces(blockID);
            }
        }
#if UNITY_EDITOR
        if(enableVerboseLogging) Debug.LogWarning($"[McBlockTypeManager.GetFinalBlockTextureSlice] Invalid block ID {blockID} or configuration error. Defaulting to slice 0.");
#endif
        return 0;
    }
    
    private AudioClip GetRandomClip(AudioClip[] prefilteredClips)
    {
        if (prefilteredClips != null && prefilteredClips.Length > 0)
        {
            return prefilteredClips[Random.Range(0, prefilteredClips.Length)];
        }
        return null;
    }

    public AudioClip GetBreakSound(byte blockID)
    {
        // Use prefiltered arrays. _prefilteredBreakSounds is guaranteed to have numberOfBlockTypes elements.
        if (blockID >= 0 && blockID < numberOfBlockTypes && _prefilteredBreakSounds[blockID] != null)
        {
             AudioClip clip = GetRandomClip(_prefilteredBreakSounds[blockID]);
             if (clip != null) return clip;
        }
        return GetRandomClip(_prefilteredFallbackBreakSounds);
    }

    public AudioClip GetPlaceSound(byte blockID)
    {
        if (blockID >= 0 && blockID < numberOfBlockTypes && _prefilteredPlaceSounds[blockID] != null)
        {
            AudioClip clip = GetRandomClip(_prefilteredPlaceSounds[blockID]);
            if (clip != null) return clip;
        }
        return GetRandomClip(_prefilteredFallbackPlaceSounds);
    }

    public AudioClip GetFootstepSound(byte blockID)
    {
        if (blockID >= 0 && blockID < numberOfBlockTypes && _prefilteredFootstepSounds[blockID] != null)
        {
            AudioClip clip = GetRandomClip(_prefilteredFootstepSounds[blockID]);
            if (clip != null) return clip;
        }
        return GetRandomClip(_prefilteredFallbackFootstepSounds);
    }

    public ParticleSystem GetBreakParticlesPrefab(byte blockID)
    {
        if (breakParticlesPrefabData != null && blockID >= 0 && blockID < breakParticlesPrefabData.Length) return breakParticlesPrefabData[blockID];
        return null;
    }
    public ParticleSystem GetPlaceParticlesPrefab(byte blockID)
    {
        if (placeParticlesPrefabData != null && blockID >= 0 && blockID < placeParticlesPrefabData.Length) return placeParticlesPrefabData[blockID];
        return null;
    }

    public bool IsAnyCutoutType(BlockVisibilityType visibilityType)
    {
        return visibilityType == BlockVisibilityType.Cutout_CullOpaqueOnly ||
               visibilityType == BlockVisibilityType.Cutout_CullSelf ||
               visibilityType == BlockVisibilityType.Cutout_CullSelfAndOtherCutout;
    }

    public bool IsSelfCullingCutout(BlockVisibilityType visibilityType)
    {
        return visibilityType == BlockVisibilityType.Cutout_CullSelf ||
               visibilityType == BlockVisibilityType.Cutout_CullSelfAndOtherCutout;
    }
}
