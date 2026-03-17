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
    private const byte BLOCK_TORCH = 50;
    private const byte BLOCK_REDSTONE_TORCH_OFF = 75;
    private const byte BLOCK_REDSTONE_TORCH_ON = 76;
    private const int BETA_SLICE_TORCH = 96;
    private const int BETA_SLICE_REDSTONE_TORCH_ON = 99;
    private const int BETA_SLICE_REDSTONE_TORCH_OFF = 115;

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
    public int[] lightOpacityData; // NEW: 0=air/tallgrass, 1=leaves, 3=water, 15=solid blocks
    public int[] lightEmissionData; // NEW: Light emission 0-15 (0=none, 7=redstone torch, 14=torch, 15=glowstone/lava)
    public bool[] canBlockGrassData; // Beta 1.7.3 Block.canBlockGrass semantics for AO diagonal sampling

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

    void Start()
    {
        float startTime = Time.realtimeSinceStartup;
        logBuilder = new StringBuilder(256); 

        #if UNITY_EDITOR
        // In the editor, validate the source arrays to help with configuration.
        bool arraysValid = true;
        if (numberOfBlockTypes < 0) numberOfBlockTypes = 0;
        _EnsureCanBlockGrassData();

        string errorFormat = "[McBlockTypeManager.Start] '{0}' array size mismatch. Expected {1}, got {2}.";
        if (blockNames == null || blockNames.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "blockNames", numberOfBlockTypes, (blockNames != null ? blockNames.Length : -1))); arraysValid = false; }
        if (isSolidData == null || isSolidData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "isSolidData", numberOfBlockTypes, (isSolidData != null ? isSolidData.Length : -1))); arraysValid = false; }
        if (blockVisibilityTypeData == null || blockVisibilityTypeData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "blockVisibilityTypeData", numberOfBlockTypes, (blockVisibilityTypeData != null ? blockVisibilityTypeData.Length : -1))); arraysValid = false; }
        if (blockCullingTypeData == null || blockCullingTypeData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "blockCullingTypeData", numberOfBlockTypes, (blockCullingTypeData != null ? blockCullingTypeData.Length : -1))); arraysValid = false; }
        if (blockShapeTypeData == null || blockShapeTypeData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "blockShapeTypeData", numberOfBlockTypes, (blockShapeTypeData != null ? blockShapeTypeData.Length : -1))); arraysValid = false; }
        if (textureMappingTypeData == null || textureMappingTypeData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "textureMappingTypeData", numberOfBlockTypes, (textureMappingTypeData != null ? textureMappingTypeData.Length : -1))); arraysValid = false; }
        if (lightOpacityData == null || lightOpacityData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "lightOpacityData", numberOfBlockTypes, (lightOpacityData != null ? lightOpacityData.Length : -1))); arraysValid = false; }
        if (canBlockGrassData == null || canBlockGrassData.Length != numberOfBlockTypes) { Debug.LogError(string.Format(errorFormat, "canBlockGrassData", numberOfBlockTypes, (canBlockGrassData != null ? canBlockGrassData.Length : -1))); arraysValid = false; }
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

        #if !UNITY_EDITOR
        _EnsureCanBlockGrassData();
        #endif

        // Pre-filtering sounds is always needed for runtime.
        PreFilterAllSounds();

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

    private void _EnsureCanBlockGrassData()
    {
        if (numberOfBlockTypes < 0) numberOfBlockTypes = 0;
        if (canBlockGrassData != null && canBlockGrassData.Length == numberOfBlockTypes) return;

        bool[] defaults = new bool[numberOfBlockTypes];
        for (int i = 0; i < numberOfBlockTypes; i++)
        {
            defaults[i] = _GetDefaultCanBlockGrass((byte)i);
        }
        canBlockGrassData = defaults;
    }

    private bool _GetDefaultCanBlockGrass(byte blockID)
    {
        if (blockID == 0) return true;

        switch (blockID)
        {
            case 6:  // sapling
            case 27: // powered rail
            case 28: // detector rail
            case 31: // tall grass
            case 32: // dead bush
            case 37: // dandelion
            case 38: // rose
            case 39: // brown mushroom
            case 40: // red mushroom
            case 50: // torch
            case 51: // fire
            case 55: // redstone wire
            case 59: // crops
            case 65: // ladder
            case 66: // rail
            case 69: // lever
            case 75: // redstone torch off
            case 76: // redstone torch on
            case 77: // stone button
            case 78: // snow layer
            case 83: // reeds
            case 90: // portal
            case 93: // repeater off
            case 94: // repeater on
                return true;
        }

        if (blockShapeTypeData != null && blockID < blockShapeTypeData.Length && blockShapeTypeData[blockID] == (int)McBlockShapeType.Cross)
        {
            return true;
        }

        if (isSolidData != null && blockID < isSolidData.Length && !isSolidData[blockID])
        {
            switch (blockID)
            {
                case 8:  // flowing water
                case 9:  // still water
                case 10: // flowing lava
                case 11: // still lava
                case 18: // leaves
                case 20: // glass
                case 79: // ice
                case 81: // cactus
                    return false;
                default:
                    return true;
            }
        }

        return false;
    }

    // NEW: Build the three lookup tables so hot paths are constant-time array reads.
    private void BuildMetadataCaches()
    {
        int total = (finalDataArray != null) ? finalDataArray.Length : 0;
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
            // Bits 10-13:    LightOpacity (4 bits) - 0-15
            // Bits 14-15:    LightEmission (2 bits) - compressed: 0=none, 1=weak(7), 2=medium(12), 3=full(15)

            if (isSolidData[i]) data |= (1 << 0);
            data |= (ushort)(blockVisibilityTypeData[i] << 1);
            data |= (ushort)(blockCullingTypeData[i] << 3);
            data |= (ushort)(blockShapeTypeData[i] << 6);
            data |= (ushort)(textureMappingTypeData[i] << 8);
            data |= (ushort)((lightOpacityData[i] & 0xF) << 10);
            
            // Compress light emission: 0-7 → 0 or 1, 8-13 → 2, 14-15 → 3
            int emission = lightEmissionData != null && i < lightEmissionData.Length ? lightEmissionData[i] : 0;
            int compressed = 0;
            if (emission >= 14) compressed = 3;      // 14-15 → full (15)
            else if (emission >= 10) compressed = 2; // 10-13 → medium (12)
            else if (emission >= 5) compressed = 1;  // 5-9 → weak (7)
            else compressed = 0;                     // 0-4 → none (0)
            data |= (ushort)(compressed << 14);
            
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
        if (blockNames != null && blockID >= 0 && blockID < blockNames.Length) return blockNames[blockID];

        string fallbackBlockName;
        if (_TryGetFallbackBlockName(blockID, out fallbackBlockName)) return fallbackBlockName;

#if UNITY_EDITOR
        if (enableVerboseLogging) Debug.LogWarning($"[McBlockTypeManager.GetBlockName] Invalid ID {blockID}.");
        return "Unknown Block";
#else
        return "N/A in build";
#endif
    }

    public bool GetBlockIsSolid(byte blockID)
    {
        if (_HasSerializedBlockDefinition(blockID))
            return (finalDataArray[blockID] & 1) != 0;

        bool isSolid;
        BlockVisibilityType visibilityType;
        BlockCullingType cullingType;
        McBlockShapeType shapeType;
        McBlockTextureMappingType mappingType;
        int lightOpacity;
        int lightEmission;
        int allFacesSlice;
        int topSlice;
        int bottomSlice;
        int sideSlice;
        if (_TryGetFallbackBlockDefinition(blockID, out isSolid, out visibilityType, out cullingType, out shapeType, out mappingType, out lightOpacity, out lightEmission, out allFacesSlice, out topSlice, out bottomSlice, out sideSlice))
            return isSolid;

        return false;
    }

    public BlockVisibilityType GetBlockVisibilityType(byte blockID)
    {
        if (_HasSerializedBlockDefinition(blockID))
            return (BlockVisibilityType)((finalDataArray[blockID] >> 1) & 0x3);

        bool isSolid;
        BlockVisibilityType visibilityType;
        BlockCullingType cullingType;
        McBlockShapeType shapeType;
        McBlockTextureMappingType mappingType;
        int lightOpacity;
        int lightEmission;
        int allFacesSlice;
        int topSlice;
        int bottomSlice;
        int sideSlice;
        if (_TryGetFallbackBlockDefinition(blockID, out isSolid, out visibilityType, out cullingType, out shapeType, out mappingType, out lightOpacity, out lightEmission, out allFacesSlice, out topSlice, out bottomSlice, out sideSlice))
            return visibilityType;

        return BlockVisibilityType.Opaque;
    }

    public BlockCullingType GetBlockCullingType(byte blockID)
    {
        if (_HasSerializedBlockDefinition(blockID))
            return (BlockCullingType)((finalDataArray[blockID] >> 3) & 0x7);

        bool isSolid;
        BlockVisibilityType visibilityType;
        BlockCullingType cullingType;
        McBlockShapeType shapeType;
        McBlockTextureMappingType mappingType;
        int lightOpacity;
        int lightEmission;
        int allFacesSlice;
        int topSlice;
        int bottomSlice;
        int sideSlice;
        if (_TryGetFallbackBlockDefinition(blockID, out isSolid, out visibilityType, out cullingType, out shapeType, out mappingType, out lightOpacity, out lightEmission, out allFacesSlice, out topSlice, out bottomSlice, out sideSlice))
            return cullingType;

        return BlockCullingType.CullAll;
    }

    public McBlockShapeType GetBlockShapeType(byte blockID)
    {
        if (_HasSerializedBlockDefinition(blockID))
            return (McBlockShapeType)((finalDataArray[blockID] >> 6) & 0x3); // Bits 6-7

        bool isSolid;
        BlockVisibilityType visibilityType;
        BlockCullingType cullingType;
        McBlockShapeType shapeType;
        McBlockTextureMappingType mappingType;
        int lightOpacity;
        int lightEmission;
        int allFacesSlice;
        int topSlice;
        int bottomSlice;
        int sideSlice;
        if (_TryGetFallbackBlockDefinition(blockID, out isSolid, out visibilityType, out cullingType, out shapeType, out mappingType, out lightOpacity, out lightEmission, out allFacesSlice, out topSlice, out bottomSlice, out sideSlice))
            return shapeType;

        return McBlockShapeType.Cube;
    }
    
    public int GetBlockTextureMappingTypeAsInt(byte blockID)
    {
        if (_HasSerializedBlockDefinition(blockID))
            return (finalDataArray[blockID] >> 8) & 0x3; // Bits 8-9

        bool isSolid;
        BlockVisibilityType visibilityType;
        BlockCullingType cullingType;
        McBlockShapeType shapeType;
        McBlockTextureMappingType mappingType;
        int lightOpacity;
        int lightEmission;
        int allFacesSlice;
        int topSlice;
        int bottomSlice;
        int sideSlice;
        if (_TryGetFallbackBlockDefinition(blockID, out isSolid, out visibilityType, out cullingType, out shapeType, out mappingType, out lightOpacity, out lightEmission, out allFacesSlice, out topSlice, out bottomSlice, out sideSlice))
            return (int)mappingType;

        return (int)McBlockTextureMappingType.AllFacesSame;
    }
    
    public int GetBlockLightOpacity(byte blockID)
    {
        if (_HasSerializedBlockDefinition(blockID))
            return (finalDataArray[blockID] >> 10) & 0xF; // Bits 10-13

        bool isSolid;
        BlockVisibilityType visibilityType;
        BlockCullingType cullingType;
        McBlockShapeType shapeType;
        McBlockTextureMappingType mappingType;
        int lightOpacity;
        int lightEmission;
        int allFacesSlice;
        int topSlice;
        int bottomSlice;
        int sideSlice;
        if (_TryGetFallbackBlockDefinition(blockID, out isSolid, out visibilityType, out cullingType, out shapeType, out mappingType, out lightOpacity, out lightEmission, out allFacesSlice, out topSlice, out bottomSlice, out sideSlice))
            return lightOpacity;

        return 0;
    }
    
    public int GetBlockLightEmission(byte blockID)
    {
        if (_HasSerializedBlockDefinition(blockID))
        {
            int compressed = (finalDataArray[blockID] >> 14) & 0x3; // Bits 14-15
            // Decompress: 0=none(0), 1=weak(7), 2=medium(12), 3=full(15)
            if (compressed == 3) return 15;
            if (compressed == 2) return 12;
            if (compressed == 1) return 7;
            return 0;
        }

        bool isSolid;
        BlockVisibilityType visibilityType;
        BlockCullingType cullingType;
        McBlockShapeType shapeType;
        McBlockTextureMappingType mappingType;
        int lightOpacity;
        int lightEmission;
        int allFacesSlice;
        int topSlice;
        int bottomSlice;
        int sideSlice;
        if (_TryGetFallbackBlockDefinition(blockID, out isSolid, out visibilityType, out cullingType, out shapeType, out mappingType, out lightOpacity, out lightEmission, out allFacesSlice, out topSlice, out bottomSlice, out sideSlice))
            return lightEmission;

        return 0;
    }

    public bool GetBlockCanBlockGrass(byte blockID)
    {
        if (canBlockGrassData == null || canBlockGrassData.Length == 0)
        {
            return _GetDefaultCanBlockGrass(blockID);
        }

        if (blockID < canBlockGrassData.Length)
        {
            return canBlockGrassData[blockID];
        }

        return _GetDefaultCanBlockGrass(blockID);
    }
    
    public int GetBlockTextureSlice_AllFaces(byte blockID)
    {
        if (uv_allFacesData != null && blockID >= 0 && blockID < uv_allFacesData.Length) return uv_allFacesData[blockID];

        bool isSolid;
        BlockVisibilityType visibilityType;
        BlockCullingType cullingType;
        McBlockShapeType shapeType;
        McBlockTextureMappingType mappingType;
        int lightOpacity;
        int lightEmission;
        int allFacesSlice;
        int topSlice;
        int bottomSlice;
        int sideSlice;
        if (_TryGetFallbackBlockDefinition(blockID, out isSolid, out visibilityType, out cullingType, out shapeType, out mappingType, out lightOpacity, out lightEmission, out allFacesSlice, out topSlice, out bottomSlice, out sideSlice))
            return allFacesSlice;

        return 0;
    }
    public int GetBlockTextureSlice_TopFace(byte blockID)
    {
        if (uv_topFaceData != null && blockID >= 0 && blockID < uv_topFaceData.Length) return uv_topFaceData[blockID];

        bool isSolid;
        BlockVisibilityType visibilityType;
        BlockCullingType cullingType;
        McBlockShapeType shapeType;
        McBlockTextureMappingType mappingType;
        int lightOpacity;
        int lightEmission;
        int allFacesSlice;
        int topSlice;
        int bottomSlice;
        int sideSlice;
        if (_TryGetFallbackBlockDefinition(blockID, out isSolid, out visibilityType, out cullingType, out shapeType, out mappingType, out lightOpacity, out lightEmission, out allFacesSlice, out topSlice, out bottomSlice, out sideSlice))
            return topSlice;

        return 0;
    }
    public int GetBlockTextureSlice_BottomFace(byte blockID)
    {
        if (uv_bottomFaceData != null && blockID >= 0 && blockID < uv_bottomFaceData.Length) return uv_bottomFaceData[blockID];

        bool isSolid;
        BlockVisibilityType visibilityType;
        BlockCullingType cullingType;
        McBlockShapeType shapeType;
        McBlockTextureMappingType mappingType;
        int lightOpacity;
        int lightEmission;
        int allFacesSlice;
        int topSlice;
        int bottomSlice;
        int sideSlice;
        if (_TryGetFallbackBlockDefinition(blockID, out isSolid, out visibilityType, out cullingType, out shapeType, out mappingType, out lightOpacity, out lightEmission, out allFacesSlice, out topSlice, out bottomSlice, out sideSlice))
            return bottomSlice;

        return 0;
    }
    public int GetBlockTextureSlice_SideFaces(byte blockID)
    {
        if (uv_sideFacesData != null && blockID >= 0 && blockID < uv_sideFacesData.Length) return uv_sideFacesData[blockID];

        bool isSolid;
        BlockVisibilityType visibilityType;
        BlockCullingType cullingType;
        McBlockShapeType shapeType;
        McBlockTextureMappingType mappingType;
        int lightOpacity;
        int lightEmission;
        int allFacesSlice;
        int topSlice;
        int bottomSlice;
        int sideSlice;
        if (_TryGetFallbackBlockDefinition(blockID, out isSolid, out visibilityType, out cullingType, out shapeType, out mappingType, out lightOpacity, out lightEmission, out allFacesSlice, out topSlice, out bottomSlice, out sideSlice))
            return sideSlice;

        return 0;
    }
    
    public int GetFinalBlockTextureSlice(byte blockID, int faceIndex)
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

    private bool _HasSerializedBlockDefinition(byte blockID)
    {
        return finalDataArray != null && blockID >= 0 && blockID < finalDataArray.Length;
    }

    private bool _TryGetFallbackBlockName(byte blockID, out string blockName)
    {
        switch (blockID)
        {
            case BLOCK_TORCH:
                blockName = "Torch";
                return true;
            case BLOCK_REDSTONE_TORCH_OFF:
                blockName = "Redstone_Torch_Off";
                return true;
            case BLOCK_REDSTONE_TORCH_ON:
                blockName = "Redstone_Torch_On";
                return true;
        }

        blockName = null;
        return false;
    }

    private bool _TryGetFallbackBlockDefinition(byte blockID, out bool isSolid, out BlockVisibilityType visibilityType, out BlockCullingType cullingType, out McBlockShapeType shapeType, out McBlockTextureMappingType mappingType, out int lightOpacity, out int lightEmission, out int allFacesSlice, out int topSlice, out int bottomSlice, out int sideSlice)
    {
        isSolid = false;
        visibilityType = BlockVisibilityType.Cutout;
        cullingType = BlockCullingType.NoCull;
        shapeType = McBlockShapeType.Cross;
        mappingType = McBlockTextureMappingType.AllFacesSame;
        lightOpacity = 0;
        lightEmission = 0;
        allFacesSlice = 0;
        topSlice = 0;
        bottomSlice = 0;
        sideSlice = 0;

        switch (blockID)
        {
            // Minecraft.unity currently authors block tables only up through glowstone.
            case BLOCK_TORCH:
                allFacesSlice = BETA_SLICE_TORCH;
                topSlice = BETA_SLICE_TORCH;
                bottomSlice = BETA_SLICE_TORCH;
                sideSlice = BETA_SLICE_TORCH;
                lightEmission = 14;
                return true;
            case BLOCK_REDSTONE_TORCH_OFF:
                allFacesSlice = BETA_SLICE_REDSTONE_TORCH_OFF;
                topSlice = BETA_SLICE_REDSTONE_TORCH_OFF;
                bottomSlice = BETA_SLICE_REDSTONE_TORCH_OFF;
                sideSlice = BETA_SLICE_REDSTONE_TORCH_OFF;
                return true;
            case BLOCK_REDSTONE_TORCH_ON:
                allFacesSlice = BETA_SLICE_REDSTONE_TORCH_ON;
                topSlice = BETA_SLICE_REDSTONE_TORCH_ON;
                bottomSlice = BETA_SLICE_REDSTONE_TORCH_ON;
                sideSlice = BETA_SLICE_REDSTONE_TORCH_ON;
                lightEmission = 7;
                return true;
        }

        return false;
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
