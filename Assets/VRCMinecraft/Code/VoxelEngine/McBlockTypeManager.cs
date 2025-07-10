using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRRefAssist;
using System.Text; 

/// <summary>
/// Defines how block textures are mapped to faces.
/// </summary>
public enum McBlockTextureMappingType
{
    AllFacesSame,
    TopBottomSides,
}

/// <summary>
/// Defines how a block is rendered (which material/queue it uses).
/// </summary>
public enum BlockVisibilityType
{
    Opaque,
    Transparent,
    Cutout,
    Invisible
}

/// <summary>
/// Defines how this block culls its faces when next to other blocks.
/// </summary>
public enum BlockCullingType
{
    NoCull, //Never culls side
    CullSelf, //Culls side if it's the same block type
    CullSelfAndOpaque, //Culls side if it's the same block type or an opaque block type
    CullSelfAndCutout, //Culls side if it's the same block type or a cutout block type
    CullSelfAndTransparent, //Culls side if it's the same block type or a transparent block type
    CullAll //Always culls side, unless it's air
}

/// <summary>
/// Defines the basic mesh shape of a block.
/// </summary>
public enum McBlockShapeType
{
    Cube,
    Cross
}

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McBlockTypeManager : UdonSharpBehaviour
{
    [Header("Texture Array for Editor Preview")]
    [Tooltip("Assign the Texture 2D Array used for block textures here to enable slice previews in the editor.")]
    public Texture2DArray previewTextureArray;

    [Header("--- Runtime Data ---")]
    [Tooltip("Baked, bit-packed data for runtime use. Generated from the editor.")]
    public ushort[] finalDataArray;
    
    // The following arrays are for editor configuration and are stripped in builds.
    [Header("Block Definitions (Editor-Only Source)")]
    public int numberOfBlockTypes = 1;
    public string[] blockNames;
    public bool[] isSolidData;
    public int[] blockVisibilityTypeData;
    public int[] blockCullingTypeData;  // NEW
    public int[] blockShapeTypeData; 
    public int[] textureMappingTypeData;

    [Header("UV Data (Runtime)")]
    public int[] uv_allFacesData;
    public int[] uv_topFaceData;
    public int[] uv_bottomFaceData;
    public int[] uv_sideFacesData;
    
    [Header("Audio (Runtime)")]
    public AudioClip[][] breakSounds; 
    public AudioClip[][] placeSounds; 
    public AudioClip[][] footstepSounds; 

    [Header("Fallback Audio (Runtime)")]
    public AudioClip[] fallbackBreakSounds; 
    public AudioClip[] fallbackPlaceSounds; 
    public AudioClip[] fallbackFootstepSounds; 

    [Header("Particles (Runtime)")]
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

    private byte[] _cache_isSolid; // NEW – 1 == solid, 0 == not
    private byte[] _cache_visibility; // NEW – BlockVisibilityType as byte
    private byte[] _cache_culling; // NEW – BlockCullingType as byte


    void Start()
    {
        float startTime = Time.realtimeSinceStartup;
        logBuilder = new StringBuilder(256); 

        #if UNITY_EDITOR
        // In the editor, validate the source arrays to help with configuration.
        bool arraysValid = true;
        if (numberOfBlockTypes < 0) numberOfBlockTypes = 0;

        string errorFormat = "[McBlockTypeManager.Start] '{0}' array size mismatch. Expected {1}, got {2}.";
        if (blockNames == null || blockNames.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "blockNames", numberOfBlockTypes, (blockNames != null ? blockNames.Length : -1))); arraysValid = false; }
        if (isSolidData == null || isSolidData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "isSolidData", numberOfBlockTypes, (isSolidData != null ? isSolidData.Length : -1))); arraysValid = false; }
        if (blockVisibilityTypeData == null || blockVisibilityTypeData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "blockVisibilityTypeData", numberOfBlockTypes, (blockVisibilityTypeData != null ? blockVisibilityTypeData.Length : -1))); arraysValid = false; }
        if (blockCullingTypeData == null || blockCullingTypeData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "blockCullingTypeData", numberOfBlockTypes, (blockCullingTypeData != null ? blockCullingTypeData.Length : -1))); arraysValid = false; }
        if (blockShapeTypeData == null || blockShapeTypeData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "blockShapeTypeData", numberOfBlockTypes, (blockShapeTypeData != null ? blockShapeTypeData.Length : -1))); arraysValid = false; }
        if (textureMappingTypeData == null || textureMappingTypeData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "textureMappingTypeData", numberOfBlockTypes, (textureMappingTypeData != null ? textureMappingTypeData.Length : -1))); arraysValid = false; }
        if (uv_allFacesData == null || uv_allFacesData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "uv_allFacesData", numberOfBlockTypes, (uv_allFacesData != null ? uv_allFacesData.Length : -1))); arraysValid = false; }
        if (uv_topFaceData == null || uv_topFaceData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "uv_topFaceData", numberOfBlockTypes, (uv_topFaceData != null ? uv_topFaceData.Length : -1))); arraysValid = false; }
        if (uv_bottomFaceData == null || uv_bottomFaceData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "uv_bottomFaceData", numberOfBlockTypes, (uv_bottomFaceData != null ? uv_bottomFaceData.Length : -1))); arraysValid = false; }
        if (uv_sideFacesData == null || uv_sideFacesData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "uv_sideFacesData", numberOfBlockTypes, (uv_sideFacesData != null ? uv_sideFacesData.Length : -1))); arraysValid = false; }
        
        if (breakSounds == null || breakSounds.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "breakSounds (AudioClip[][])", numberOfBlockTypes, (breakSounds != null ? breakSounds.Length : -1))); arraysValid = false; }
        if (placeSounds == null || placeSounds.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "placeSounds (AudioClip[][])", numberOfBlockTypes, (placeSounds != null ? placeSounds.Length : -1))); arraysValid = false; }
        if (footstepSounds == null || footstepSounds.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "footstepSounds (AudioClip[][])", numberOfBlockTypes, (footstepSounds != null ? footstepSounds.Length : -1))); arraysValid = false; }
        
        if (breakParticlesPrefabData == null || breakParticlesPrefabData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "breakParticlesPrefabData", numberOfBlockTypes, (breakParticlesPrefabData != null ? breakParticlesPrefabData.Length : -1))); arraysValid = false; }
        if (placeParticlesPrefabData == null || placeParticlesPrefabData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "placeParticlesPrefabData", numberOfBlockTypes, (placeParticlesPrefabData != null ? placeParticlesPrefabData.Length : -1))); arraysValid = false; }
        #endif

        // Pre-filtering sounds is always needed for runtime.
        PreFilterAllSounds();

        // --- NEW: Build fast lookup caches for runtime ---
        BuildMetadataCaches();

        #if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            logBuilder.Clear();
            logBuilder.AppendFormat("[McBlockTypeManager.Start] Initialized. Total Blocks: {0}. Time: {1:F2} ms.",
                (finalDataArray != null ? finalDataArray.Length : 0), (Time.realtimeSinceStartup - startTime) * 1000f);
            Debug.Log(logBuilder.ToString());
        }
        #endif
        
        #if !UNITY_EDITOR
        // In a build, clear the source data arrays to save memory.
        // The baked finalDataArray is used instead.
        isSolidData = null;
        blockVisibilityTypeData = null;
        blockCullingTypeData = null;
        blockShapeTypeData = null;
        textureMappingTypeData = null;
        blockNames = null;
        #endif
    }

    // NEW: Build the three lookup tables so hot paths are constant-time array reads.
    private void BuildMetadataCaches()
    {
        int total = (finalDataArray != null) ? finalDataArray.Length : 0;
        _cache_isSolid = new byte[total <= 0 ? 256 : total];
        _cache_visibility = new byte[_cache_isSolid.Length];
        _cache_culling = new byte[_cache_isSolid.Length];

        for (int i = 0; i < _cache_isSolid.Length; i++)
        {
            ushort packed = (i < total) ? finalDataArray[i] : (ushort)0;
            _cache_isSolid[i] = (byte)((packed & 1) != 0 ? 1 : 0);
            _cache_visibility[i] = (byte)((packed >> 1) & 0x3);
            _cache_culling[i] = (byte)((packed >> 3) & 0x7);
        }
    }
    
    /// <summary>
    /// Called from the editor script to bake source arrays into the final packed data array.
    /// </summary>
    public void EncodeDataForBuild()
    {
        if (numberOfBlockTypes < 0) numberOfBlockTypes = 0;
        finalDataArray = new ushort[numberOfBlockTypes];
        for (int i = 0; i < numberOfBlockTypes; i++)
        {
            ushort data = 0;
            // Bit 0:         IsSolid (1 bit)
            // Bits 1-2:      VisibilityType (2 bits) - now only needs 2 bits for 4 values
            // Bits 3-5:      CullingType (3 bits)
            // Bits 6-7:      ShapeType (2 bits)
            // Bits 8-9:      TextureMappingType (2 bits)
            // Bits 10-15:    Unused (6 bits)

            if (isSolidData[i]) data |= (1 << 0);
            data |= (ushort)(blockVisibilityTypeData[i] << 1);
            data |= (ushort)(blockCullingTypeData[i] << 3);
            data |= (ushort)(blockShapeTypeData[i] << 6);
            data |= (ushort)(textureMappingTypeData[i] << 8);
            
            finalDataArray[i] = data;
        }
    }

    private void PreFilterAllSounds()
    {
        int totalTypes = (finalDataArray != null) ? finalDataArray.Length : 0;
        
        _prefilteredBreakSounds = new AudioClip[totalTypes][];
        _prefilteredPlaceSounds = new AudioClip[totalTypes][];
        _prefilteredFootstepSounds = new AudioClip[totalTypes][];

        for (int i = 0; i < totalTypes; i++)
        {
            if (breakSounds != null && i < breakSounds.Length && breakSounds[i] != null)
                _prefilteredBreakSounds[i] = _prefilterClipArray(breakSounds[i]);
            else
                _prefilteredBreakSounds[i] = new AudioClip[0];

            if (placeSounds != null && i < placeSounds.Length && placeSounds[i] != null)
                _prefilteredPlaceSounds[i] = _prefilterClipArray(placeSounds[i]);
            else
                _prefilteredPlaceSounds[i] = new AudioClip[0];

            if (footstepSounds != null && i < footstepSounds.Length && footstepSounds[i] != null)
                _prefilteredFootstepSounds[i] = _prefilterClipArray(footstepSounds[i]);
            else
                _prefilteredFootstepSounds[i] = new AudioClip[0];
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
        // Block names are editor-only, so this will only work in the editor.
        #if UNITY_EDITOR
        if (blockNames != null && blockID >= 0 && blockID < blockNames.Length) return blockNames[blockID];
        if (enableVerboseLogging) Debug.LogWarning($"[McBlockTypeManager.GetBlockName] Invalid ID {blockID}.");
        return "Unknown Block";
        #else
        return "N/A in build";
        #endif
    }

    public bool GetBlockIsSolid(byte blockID)
    {
        if (_cache_isSolid != null && blockID < _cache_isSolid.Length) return _cache_isSolid[blockID] == 1;
        if (finalDataArray != null && blockID < finalDataArray.Length)
            return (finalDataArray[blockID] & 1) != 0;
        return false;
    }

    public BlockVisibilityType GetBlockVisibilityType(byte blockID)
    {
        if (_cache_visibility != null && blockID < _cache_visibility.Length) return (BlockVisibilityType)_cache_visibility[blockID];
        if (finalDataArray != null && blockID < finalDataArray.Length)
            return (BlockVisibilityType)((finalDataArray[blockID] >> 1) & 0x3);
        return BlockVisibilityType.Opaque;
    }

    public BlockCullingType GetBlockCullingType(byte blockID)
    {
        if (_cache_culling != null && blockID < _cache_culling.Length) return (BlockCullingType)_cache_culling[blockID];
        if (finalDataArray != null && blockID < finalDataArray.Length)
            return (BlockCullingType)((finalDataArray[blockID] >> 3) & 0x7);
        return BlockCullingType.CullAll;
    }

    public McBlockShapeType GetBlockShapeType(byte blockID)
    {
        if (finalDataArray != null && blockID >= 0 && blockID < finalDataArray.Length)
            return (McBlockShapeType)((finalDataArray[blockID] >> 6) & 0x3); // Bits 6-7
        return McBlockShapeType.Cube;
    }
    
    public int GetBlockTextureMappingTypeAsInt(byte blockID)
    {
        if (finalDataArray != null && blockID >= 0 && blockID < finalDataArray.Length)
            return (finalDataArray[blockID] >> 8) & 0x3; // Bits 8-9
        return (int)McBlockTextureMappingType.AllFacesSame;
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
    
    public int GetFinalBlockTextureSlice(byte blockID, int faceIndex)
    {
        if (finalDataArray != null && blockID >= 0 && blockID < finalDataArray.Length) 
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
                    return GetBlockTextureSlice_AllFaces(blockID);
            }
        }
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
        int totalTypes = (finalDataArray != null) ? finalDataArray.Length : 0;
        if (blockID >= 0 && blockID < totalTypes && _prefilteredBreakSounds[blockID] != null)
        {
             AudioClip clip = GetRandomClip(_prefilteredBreakSounds[blockID]);
             if (clip != null) return clip;
        }
        return GetRandomClip(_prefilteredFallbackBreakSounds);
    }

    public AudioClip GetPlaceSound(byte blockID)
    {
        int totalTypes = (finalDataArray != null) ? finalDataArray.Length : 0;
        if (blockID >= 0 && blockID < totalTypes && _prefilteredPlaceSounds[blockID] != null)
        {
            AudioClip clip = GetRandomClip(_prefilteredPlaceSounds[blockID]);
            if (clip != null) return clip;
        }
        return GetRandomClip(_prefilteredFallbackPlaceSounds);
    }

    public AudioClip GetFootstepSound(byte blockID)
    {
        int totalTypes = (finalDataArray != null) ? finalDataArray.Length : 0;
        if (blockID >= 0 && blockID < totalTypes && _prefilteredFootstepSounds[blockID] != null)
        {
            AudioClip clip = GetRandomClip(_prefilteredFootstepSounds[blockID]);
            if (clip != null) return clip;
        }
        return GetRandomClip(_prefilteredFallbackFootstepSounds);
    }

    public ParticleSystem GetBreakParticlesPrefab(byte blockID)
    {
        int totalTypes = (finalDataArray != null) ? finalDataArray.Length : 0;
        if (breakParticlesPrefabData != null && blockID >= 0 && blockID < totalTypes) return breakParticlesPrefabData[blockID];
        return null;
    }
    public ParticleSystem GetPlaceParticlesPrefab(byte blockID)
    {
        int totalTypes = (finalDataArray != null) ? finalDataArray.Length : 0;
        if (placeParticlesPrefabData != null && blockID >= 0 && blockID < totalTypes) return placeParticlesPrefabData[blockID];
        return null;
    }
}