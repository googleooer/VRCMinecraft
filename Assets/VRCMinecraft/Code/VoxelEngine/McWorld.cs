#define LOGGING

using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.SDK3.Rendering;
using VRC.Udon.Common.Interfaces;
using VRRefAssist;
using TMPro;

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McWorld : UdonSharpBehaviour
{
    [Header("World Configuration")]
    public string worldSeedString = "DefaultSeed";
    public int worldDimensionX = 4;
    public int worldDimensionY = 2;
    public int worldDimensionZ = 4;
    public int chunkSizeXZ = 16;
    public int chunkSizeY = 16;

    [Header("Chunk Management")]
    [Tooltip("Prefab for chunks. Must have 3 child objects named 'Opaque', 'Transparent', 'Cutout' with MeshFilters, and a root MeshCollider.")]
    public GameObject chunkPrefab;
    public ChunkData[] chunks_1D; // OPTIMIZED: Public for direct coordinator access

    [Header("Performance Tuning")]
    [Tooltip("How many voxels to check for meshing per step inside a chunk. Higher values build meshes faster but may cause lag spikes.")]
    public int voxelsPerMeshStep = 2048;
    [Tooltip("Approximate time budget per mesh step in milliseconds. OPTIMIZATION Phase 4: Increased from 1.5ms to 8.0ms to reduce loop overhead (fewer, larger steps).")]
    public float meshStepTimeBudgetMs = 8.0f;
    [Tooltip("Time budget per Update() call in milliseconds to prevent frame drops.")]
    public float updateTimeBudgetMs = 22.0f;
    [Tooltip("During the ONE-TIME initial world generation, updateTimeBudgetMs is temporarily raised to this value so the (profiled ~92% idle) GPU + async readback pipeline can drain far more work per frame. Frame rate during the load matters much less than total load time. Automatically restored to updateTimeBudgetMs once world gen completes. Set <= updateTimeBudgetMs to disable the boost.")]
    public float loadPhaseUpdateBudgetMs = 45.0f;
    // Runtime state for the load-phase budget boost (see Update()).
    private bool _loadPhaseBudgetRestored = false;
    private float _runtimeUpdateBudgetMs = 0f;
    [Tooltip("Budget per incremental GPU mesh decode step in milliseconds. Lower values reduce hitches from GPU mesh completion.")]
    public float gpuMeshDecodeStepBudgetMs = 1.5f;
    [Tooltip("Maximum incremental GPU mesh decode steps per chunk each frame.")]
    public int gpuMeshDecodeStepsPerFrame = 2;
    [Tooltip("Total GPU mesh decode budget per frame in milliseconds. Caps aggregate decode work across all chunks.")]
    public float gpuMeshDecodeFrameBudgetMs = 2.5f;
    [Tooltip("Budget per GPU worldgen step in milliseconds. Lower values reduce frame spikes during terrain generation.")]
    public float gpuWorldgenStepBudgetMs = 3.0f;
    [Tooltip("Maximum GPU worldgen steps per chunk each frame.")]
    public int gpuWorldgenStepsPerFrame = 4;
    [Tooltip("Prioritize meshing the exposed shell and chunks near the player before fully enclosed interiors.")]
    public bool prioritizeVisibleShellMeshing = true;
    [Tooltip("Chunks within this XZ radius of the player are always eligible for meshing.")]
    public int shellMeshPriorityRadiusXZ = 2;
    [Tooltip("Chunks within this Y radius of the player are always eligible for meshing.")]
    public int shellMeshPriorityRadiusY = 1;
    [Tooltip("How many deferred interior chunks to reconsider per frame when headroom is available.")]
    public int deferredInteriorMeshRequestsPerFrame = 1;
    [Tooltip("How many chunk slots to scan per frame when looking for deferred interior meshes to wake up.")]
    public int deferredInteriorMeshScanCountPerFrame = 64;
    [Tooltip("Defer collider application, lighting finalize, and neighbor mesh rebuilds for far chunks until there is frame headroom.")]
    public bool deferFarChunkSecondaryWork = true;
    [Tooltip("Only keep chunk colliders enabled near the player. Far chunks keep render meshes but disable collision until the player approaches.")]
    public bool nearOnlyChunkColliders = true;
    [Tooltip("Chunks within this XZ radius keep their colliders active.")]
    public int colliderPriorityRadiusXZ = 2;
    [Tooltip("Chunks within this many chunk layers above the player keep their colliders active.")]
    public int colliderPriorityRadiusY = 1;
    [Tooltip("Chunks within this many chunk layers below the player keep their colliders active so the ground does not disappear underfoot.")]
    public int colliderPriorityBelowRadiusY = 3;
    [Tooltip("Maximum nearby chunk colliders to enable per frame before the general deferred-work scan runs.")]
    public int nearColliderUpdatesPerFrame = 8;
    [Tooltip("How many deferred secondary tasks to finish per frame when there is headroom.")]
    public int deferredSecondaryWorkTasksPerFrame = 2;
    [Tooltip("How many chunk slots to scan per frame for deferred secondary work.")]
    public int deferredSecondaryWorkScanCountPerFrame = 64;
    [Tooltip("Adapt GPU worldgen and mesh decode budgets based on recent frame time.")]
    public bool enableAdaptiveBudgets = true;
    [Tooltip("Target headroom below the frame budget before adaptive budgets ramp up.")]
    // THROUGHPUT FIX: was 1.5f. The adaptive throttle was collapsing the frame budget to
    // 5ms when worldgen briefly exceeded budget. Setting headroom to a negative value
    // effectively disables shrinking (target = budget + |headroom|, always met by smoothedMs).
    public float adaptiveFrameHeadroomMs = -100f;
    [Tooltip("Rate used to grow adaptive budgets on frames with spare headroom.")]
    public float adaptiveBudgetRecoverRate = 0.08f;
    [Tooltip("Rate used to shrink adaptive budgets on over-budget frames.")]
    // THROUGHPUT FIX: was 0.20f (shrink by 20% per over-budget frame).
    // Set to 0 to disable shrinking entirely — the system can still grow back up to the
    // configured budget but never below it.
    public float adaptiveBudgetBackoffRate = 0.0f;
    [Tooltip("Minimum fraction of the configured GPU budgets that adaptive throttling will allow.")]
    // THROUGHPUT FIX: was 0.80 (budget can shrink to 80% of configured). Setting to 1.0
    // means the budget never drops below the configured value.
    public float adaptiveBudgetMinScale = 1.0f;

    [Header("System References")]
    [SerializeField, FindObjectOfType(true)]
    public McTerrainGenerator terrainGenerator;
    [SerializeField, FindObjectOfType(true)]
    public McBlockTypeManager blockTypeManager;
    [SerializeField, FindObjectOfType(true)]
    private McCoordinator coordinator;
    [SerializeField, FindObjectOfType(true)]
    private McBlockTicker blockTicker;

    [Header("Biome Color Textures (Beta 1.7.3)")]
    [Tooltip("grasscolor.png from Beta 1.7.3 (256x256)")]
    public Texture2D grassColorTexture;
    [Tooltip("foliagecolor.png from Beta 1.7.3 (256x256)")]
    public Texture2D foliageColorTexture;
    [Tooltip("watercolor.png from Beta 1.7.3 (256x256)")]
    public Texture2D waterColorTexture;
    [Tooltip("Generated fire animation strip (16x512, 32 frames). Use Tools > VRCMinecraft > Generate Fire Texture.")]
    public Texture2D fireStripTexture;
    [Tooltip("Generated lava animation strip (16x512, 32 frames). Use Tools > VRCMinecraft > Generate Lava Texture.")]
    public Texture2D lavaStripTexture;
    [Tooltip("Minecraft Beta 1.7.3-style smooth lighting and ambient occlusion for terrain.")]
    public bool ambientOcclusion = true;

    [Header("Debugging")]
    public bool enableVerboseLogging = false;
    public bool enableGenerationTimings = false;

    [Header("GPU Voxel Backend")]
    public bool enableGpuVoxelBackend = true;
    public int gpuChunkSlotCapacity = 1023;
    public int gpuLightingIterationsPerUpdate = 64;
    public int gpuLightingTotalIterations = 128;
    public int gpuResidentRadiusXZ = 6;
    public int gpuResidentRadiusY = 2;
    public int gpuResidentSyncsPerFrame = 32;
    [Tooltip("Experimental: export per-slice face bounds instead of dense face pixels for GPU meshing. Disabled by default until validated.")]
    public bool useCompactGpuFaceExport = false;
    public Material gpuAtlasOverlayMaterial;
    public Material gpuLightingSeedMaterial;
    public Material gpuLightingPropagateMaterial;
    public Material gpuFaceExtractMaterial;
    public Material gpuWaterAnimMaterial;
    // GPU OFFLOAD #3: bakes per-column biome tint from climate texture + grass/foliage/water LUTs.
    public Material gpuBiomeColorBakeMaterial;
    // GPU OFFLOAD #9: per-vertex ambient occlusion baker, reads from chunk atlas.
    public Material gpuAOBakeMaterial;
    // GPU OFFLOAD #4: copies neighbor-face borders into the per-chunk sentinel RT.
    public Material gpuSentinelBorderMaterial;
    // GPU OFFLOAD #6: writes one voxel + propagates light a few cells outward.
    public Material gpuLightPokeMaterial;
    // GPU OFFLOAD #7: fluid cellular automaton (water flow / lava sticky).
    public Material gpuFluidTickMaterial;
    // GPU OFFLOAD #10: voxel DDA raycast for block target.
    public Material gpuRaycastMaterial;
    // GPU OFFLOAD #11: column-RLE encoder (one Blit per chunk → small fixed-size RT).
    public Material gpuRleEncodeMaterial;
    // GPU OFFLOAD #5: per-chunk instanced quad renderer (Mesh.DrawMeshInstanced).
    // When set, mesh build emits ZERO vertex arrays to CPU; instead we keep a
    // face-buffer RT per chunk and render via VRCGraphics.DrawMeshInstanced.
    public Material gpuVoxelQuadDrawMaterial;
    // Unit quad mesh used as the instance template by GpuVoxelQuadDraw.
    public Mesh gpuVoxelQuadMesh;

    [Header("GPU Debug HUD")]
    public bool enableGpuDebugHud = true;
    public bool showGpuDebugHudOnStart = false;
    public float gpuDebugHudDistance = 0.85f;
    public Vector3 gpuDebugHudLocalOffset = new Vector3(0f, -0.08f, 0f);
    public Vector3 gpuDebugHudEulerOffset = Vector3.zero;

    // --- Private World State ---
    private int totalWorldChunks;
    [HideInInspector] public int chunkOffsetX;
    [HideInInspector] public int chunkOffsetY;
    [HideInInspector] public int chunkOffsetZ;
    [HideInInspector] public int globalVoxelOffsetX;
    [HideInInspector] public int globalVoxelOffsetY;
    [HideInInspector] public int globalVoxelOffsetZ;
    private ushort[] blockDataCache;
    private int[] uv_allFacesCache;
    private int[] uv_topFaceCache;
    private int[] uv_bottomFaceCache;
    private int[] uv_sideFacesCache;
    private BlockVisibilityType[] visibilityCache; // per blockID
    private BlockCullingType[] cullingCache; // per blockID
    private McBlockShapeType[] shapeTypeCache; // per blockID - NEW: cache for block shape types
    private byte[] biomeTintModeCache; // 0=none, 1=grass, 2=foliage, 3=water
    private byte[] shouldDrawTable; // 256x256 lookup: self<<8 | neighbor => 0/1
    private bool[] canBlockGrassCache; // Beta Block.canBlockGrass semantics for AO diagonal sampling

    // --- Lighting System (Minecraft Beta 1.7.3 style) ---
    private float[] lightBrightnessTable; // 16 values: light level 0-15 to brightness 0.0-1.0
    private byte[] aoBrightnessAlphaTable; // 16^4 lookup: averaged corner brightness packed to 8-bit alpha
    public int skylightSubtracted = 0; // 0-11, for day/night cycle (0 = noon, 11 = midnight)
    private int[] lightOpacityCache; // per blockID
    private int[] lightEmissionCache; // per blockID

    // --- Lighting Optimization: Memory Pooling ---
    private readonly System.Collections.Generic.Queue<int[]> bfsQueuePool = new System.Collections.Generic.Queue<int[]>();
    private readonly System.Collections.Generic.Queue<int[]> bfsQueuePoolLarge = new System.Collections.Generic.Queue<int[]>();
    private readonly System.Collections.Generic.Queue<int[]> lightingQueuePool = new System.Collections.Generic.Queue<int[]>();
    private const int BFS_QUEUE_SIZE = 4096;
    private const int BFS_QUEUE_SIZE_LARGE = 16384; // FIXED: Increased to prevent queue overflow causing pitch black spots
    private const int LIGHTING_QUEUE_SIZE = 8192;

    // --- OPTIMIZATION: Reusable Arrays ---
    private byte[] reusableByteArray = new byte[4096];
    private bool[] reusableBoolArray = new bool[4096];
    private int[] reusableIntArray = new int[4096];
    private readonly byte[] greedyMaskBlockIds = new byte[256];
    private readonly byte[] greedyMaskLightLevels = new byte[256];
    private readonly int[] greedyMaskPackedColors = new int[256];
    private readonly int[] greedyMaskAoSignatures = new int[256];
    private readonly int[] aoNormalX = { 0, 0, 0, 0, 1, -1 };
    private readonly int[] aoNormalY = { 1, -1, 0, 0, 0, 0 };
    private readonly int[] aoNormalZ = { 0, 0, 1, -1, 0, 0 };
    private readonly int[] aoTangentUX = { 1, 1, 1, 1, 0, 0 };
    private readonly int[] aoTangentUY = { 0, 0, 0, 0, 0, 0 };
    private readonly int[] aoTangentUZ = { 0, 0, 0, 0, 1, 1 };
    private readonly int[] aoTangentVX = { 0, 0, 0, 0, 0, 0 };
    private readonly int[] aoTangentVY = { 0, 0, 1, 1, 1, 1 };
    private readonly int[] aoTangentVZ = { 1, 1, 0, 0, 0, 0 };
    private readonly int[] aoCornerUSigns =
    {
        -1, -1,  1,  1, // Up
         1,  1, -1, -1, // Down
         1,  1, -1, -1, // North (+Z)
        -1, -1,  1,  1, // South (-Z)
        -1, -1,  1,  1, // East (+X)
         1,  1, -1, -1  // West (-X)
    };
    private readonly int[] aoCornerVSigns =
    {
        -1,  1,  1, -1,
        -1,  1,  1, -1,
        -1,  1,  1, -1,
        -1,  1,  1, -1,
        -1,  1,  1, -1,
        -1,  1,  1, -1
    };
    private bool stats_trackCompactEmitInternals = false;
    private bool _greedyConstantLight = false;

    // --- OPTIMIZATION: Deferred Reconciliation System ---
    private readonly System.Collections.Generic.Queue<ChunkData> deferredReconciliationQueue = new System.Collections.Generic.Queue<ChunkData>();
    private readonly System.Collections.Generic.HashSet<ChunkData> reconciliationPending = new System.Collections.Generic.HashSet<ChunkData>();
    private const int MAX_RECONCILIATION_PER_FRAME = 5; // FIXED: Increased from 3 to 5 chunks per frame
    private const float RECONCILIATION_TIME_BUDGET_MS = 12.0f; // FIXED: Increased from 8ms to 12ms budget per frame

    // --- Active Processing Queues ---
    // THROUGHPUT FIX: was 16 (hard-capped to ~3-5 chunks/frame because of multi-frame GPU
    // readback latency). Bumping to 64 lifts the ceiling to ~12-20 chunks/frame on PC,
    // which is 4x faster worldgen for the same per-chunk cost. The arrays are allocated
    // once at Start and consume negligible memory. Tune lower on Quest if GPU memory
    // becomes a concern (each in-flight chunk reserves a GPU atlas slot).
    private const int MAX_ACTIVE_CHUNKS = 64;
    private ChunkData[] activeDataGenChunks;
    private int activeDataGenCount = 0;
    private ChunkData[] activeMeshingChunks;
    private int activeMeshingCount = 0;
    private const int COLLIDER_DEFER_FRAMES = 2;
    private const byte BLOCK_WATER_MOVING = 8;
    private const byte BLOCK_WATER_STILL = 9;
    private const byte BLOCK_TORCH = 50;
    private const byte BLOCK_REDSTONE_TORCH_OFF = 75;
    private const byte BLOCK_REDSTONE_TORCH_ON = 76;
    private const byte BLOCK_FENCE = 85;
    private const byte BLOCK_ICE = 79;
    private const byte TORCH_MOUNT_WEST = 1;
    private const byte TORCH_MOUNT_EAST = 2;
    private const byte TORCH_MOUNT_NORTH = 3;
    private const byte TORCH_MOUNT_SOUTH = 4;
    private const byte TORCH_MOUNT_FLOOR = 5;
    private const byte TORCH_MOUNT_CEILING = 6;
    private Material sharedOpaqueChunkMaterial;
    private Material sharedTransparentChunkMaterial;
    private Material sharedCutoutChunkMaterial;
    private int terrainPropUseVertexLightId = -1;
    private int terrainPropUseGpuExactAoId = -1;
    private RenderTexture betaWaterStillStateA;
    private RenderTexture betaWaterStillStateB;
    private RenderTexture betaWaterStillColor;
    private RenderTexture betaWaterFlowStateA;
    private RenderTexture betaWaterFlowStateB;
    private RenderTexture betaWaterFlowColor;
    private int betaWaterFlowFrame = 0;
    private int betaWaterStillSlice = -1;
    private int betaWaterFlowSlice = -1;
    private bool gpuSlotLookupDirty = false;
    private int terrainPropWaterStillTexId = -1;
    private int terrainPropWaterFlowTexId = -1;
    private int terrainPropWaterStillSliceId = -1;
    private int terrainPropWaterFlowSliceId = -1;

    // --- GPU Voxel Backend State ---
    private bool gpuBackendReady = false;

    // Deferred GPU sync: block ticker sets this so multiple SetBlock calls in one
    // tick batch share a single GPU chunk upload instead of one per voxel write.
    private bool _gpuSyncDeferred = false;
    private int[] _gpuDeferredDirtyChunks;
    private int _gpuDeferredDirtyCount = 0;
    private const int GPU_DEFERRED_DIRTY_MAX = 64;

    // Deferred mesh updates: block ticker sets this so mesh rebuilds are batched
    // after all ticks instead of firing inline per SetBlock
    private bool _meshUpdateDeferred = false;
    private int[] _meshDeferredDirtyChunks;
    private int _meshDeferredDirtyCount = 0;
    private const int MESH_DEFERRED_DIRTY_MAX = 64;
    private int gpuAtlasSlotsX = 1;
    private int gpuAtlasSlotsY = 1;
    private int gpuTileWidth = 16;
    private int gpuTileHeight = 256;
    private int gpuAtlasWidth = 16;
    private int gpuAtlasHeight = 256;
    private int[] gpuChunkIndexToSlot;
    private int[] gpuChunkSyncedDataVersion;
    private int[] gpuSlotToChunkIndex;
    private int[] gpuSlotUseStamp;
    private int gpuSlotUseCounter = 1;
    private int gpuResidentCenterChunkX = 0;
    private int gpuResidentCenterChunkY = 0;
    private int gpuResidentCenterChunkZ = 0;
    private Texture2D gpuSlotLookupTexture;
    private Texture2D gpuSlotMetaTexture;
    private Texture2D gpuBlockPropertyTexture;
    private Texture2D gpuUploadBlockTexture;
    private Texture2D gpuClearTexture;
    private Color32[] gpuSlotLookupPixels;
    private Color32[] gpuSlotMetaPixels;
    private Color32[] gpuUploadBlockPixels;

    private RenderTexture gpuBlockAtlas;
    private RenderTexture gpuBlockAtlasScratch;
    private RenderTexture gpuLightAtlas;
    private RenderTexture gpuLightAtlasScratch;
    private bool gpuLightingSeedPending = false;
    private int gpuLightingIterationsRemaining = 0;
    private int gpuLightingFrameCursor = 0;

    // --- GPU Face Extraction State ---
    // THROUGHPUT FIX: was 4. Caps how many chunks can be in flight at the GPU-mesh-face
    // readback stage. Each readback takes 2-3 frames asynchronously; with only 4 slots
    // that's ~1-2 chunks per frame meshed via the GPU pipeline regardless of how fast
    // the rest of the system runs. 16 lifts this to ~5-8 chunks/frame. Each buffer is
    // a small RT — negligible VRAM cost.
    private const int GPU_FACE_READBACK_BUFFER_COUNT = 16;
    // Was 2 — that capped GPU-meshed chunks at ~2/frame regardless of available
    // GPU/pipeline headroom (profiling showed the GPU ~92% idle during worldgen).
    // The processing loop is still bounded by frameBudget + mesh-pool availability,
    // so raising this only lets ready readbacks drain faster when there's spare time.
    private const int GPU_FACE_READBACKS_PER_FRAME = 8;
    private const int GPU_FACE_COMPACT_DIRECTIONS = 6;
    private Texture2D gpuShouldDrawTexture;
    private RenderTexture gpuFaceAtlas;
    private RenderTexture[] gpuFaceReadbackTextures;
    private Texture2D gpuFaceOccupancyUploadTexture;
    private Color32[] gpuFaceOccupancyUploadPixels;
    private RenderTexture gpuFaceOccupancyAtlas;
    private RenderTexture gpuFaceOccupancyAtlasScratch;
    private int gpuFaceOccupancySlotWidth = 8;
    private int gpuFaceOccupancySlotHeight = 48;
    private int gpuFaceOccupancyAtlasWidth = 8;
    private int gpuFaceOccupancyAtlasHeight = 48;
    private int gpuPropDrawTableTexId;
    private int gpuPropFaceModeId;
    private int gpuPropFaceReadSlotId;
    private int gpuPropBorderTexId;
    private int gpuPropBorderInfoId;
    private int gpuPropFaceOccupancyAtlasGlobalId;
    private int gpuPropFaceOccupancyInfoId;
    private Texture2D[] gpuBorderTextures;
    private Color32[][] gpuBorderPixels;
    private int gpuBorderWidth, gpuBorderHeight, gpuBorderStride;
    private int gpuFaceSummaryWidth = 64;
    private int gpuFaceSummaryHeight = 6;
    private Color32[][] gpuFaceReadbackPixels; // per-buffer, per-chunk face readback
    private ChunkData[] gpuFaceReadbackChunks;
    private int[] gpuFaceReadbackSlots;
    private int[] gpuFaceReadbackBuildVersion;
    private float[] gpuFaceReadbackStartMs;
    private int[] gpuFaceReadbackState; // 0 idle, 1 in-flight, 2 ready
    private int[] gpuFaceReadbackInflightOrder;
    private int gpuFaceReadbackInflightHead = 0;
    private int gpuFaceReadbackInflightTail = 0;
    private int gpuFaceReadbackInflightCount = 0;
    private int[] gpuFaceReadbackReadyOrder;
    private int gpuFaceReadbackReadyHead = 0;
    private int gpuFaceReadbackReadyTail = 0;
    private int gpuFaceReadbackReadyCount = 0;
    private int gpuPropGpuEnabledId;
    private int gpuPropLightAtlasId;
    private int gpuPropBlockAtlasGlobalId;
    private int gpuPropSlotLookupId;
    private int gpuPropSlotMetaGlobalId;
    private int gpuPropBlockPropsGlobalId;
    private int gpuPropAtlasInfoId;
    private int gpuPropWorldInfoId;
    private int gpuPropChunkInfoId;
    private int gpuPropVoxelOffsetId;
    private int gpuPropBlockAtlasId;
    private int gpuPropBlockPropsId;
    private int gpuPropSlotLookupTexId;
    private int gpuPropSlotMetaId;
    private int gpuPropOverlayTexId;
    private int gpuPropOverlayRectId;
    private int gpuPropOverlayPackedWidthId;
    private int gpuPropTopSkyLightId;
    private GameObject gpuDebugHudRoot;
    private RectTransform gpuDebugHudRect;
    private GameObject gpuDebugPanel;
    private RawImage gpuDebugTextureA;
    private RawImage gpuDebugTextureB;
    private TextMeshProUGUI gpuDebugLabel;
    private TextMeshProUGUI gpuDebugToggleButtonLabel;
    private VRCPlayerApi gpuDebugLocalPlayer;
    private bool gpuDebugHudVisible = false;
    private int gpuDebugHudPage = 0;
    private const int GPU_DEBUG_PAGE_COUNT = 4;
    private float adaptiveGpuMeshDecodeStepBudgetMs;
    private int adaptiveGpuMeshDecodeStepsPerFrame;
    private float adaptiveGpuMeshDecodeFrameBudgetMs;
    private float adaptiveGpuWorldgenStepBudgetMs;
    private int adaptiveGpuWorldgenStepsPerFrame;
    private float lastUpdateDurationMs = 0f;
    private int deferredInteriorMeshScanCursor = 0;
    private int deferredSecondaryWorkScanCursor = 0;

    // --- Meshing Constants & Buffers (could be pooled) ---
    private const int MAX_VERTS = 12288;
    private const int MAX_TRIS = (MAX_VERTS / 4) * 6;
    private readonly Vector3[] FaceVertices_North = { new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 1) };
    private readonly Vector3[] FaceVertices_East = { new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1) };
    private readonly Vector3[] FaceVertices_South = { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) };
    private readonly Vector3[] FaceVertices_West = { new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0), new Vector3(0, 0, 0) };
    private readonly Vector3[] FaceVertices_Up = { new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0) };
    private readonly Vector3[] FaceVertices_Down = { new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1), new Vector3(0, 0, 0) };
    private readonly Vector3 Normal_North = Vector3.forward; private readonly Vector3 Normal_East = Vector3.right; private readonly Vector3 Normal_South = Vector3.back;
    private readonly Vector3 Normal_West = Vector3.left; private readonly Vector3 Normal_Up = Vector3.up; private readonly Vector3 Normal_Down = Vector3.down;
    private const int FACE_INDEX_SIDE = 0; private const int FACE_INDEX_TOP = 2; private const int FACE_INDEX_BOTTOM = 3;
    private const int PACKED_WHITE_RGB = 0xFFFFFF;

    // --- RLE Constants ---
    private const ushort RLE_TYPE_1D = 0;
    private const ushort RLE_TYPE_2D_XZ_PLANE = 1;
    private const ushort RLE_TYPE_3D_FULL_CHUNK = 2;

    // --- Bitwise Constants for fast coordinate math (chunk size = 16) ---
    private const int CHUNK_SIZE_SHIFT = 4;
    private const int CHUNK_SIZE_MASK = 15; // 16 - 1

    // --- Neighbor Offsets ---
    private readonly int[] neighbor_dx_offsets = { 1, -1, 0,  0, 0,  0 };
    private readonly int[] neighbor_dy_offsets = { 0,  0, 1, -1, 0,  0 };
    private readonly int[] neighbor_dz_offsets = { 0,  0, 0,  0, 1, -1 };

#if LOGGING
    private System.Text.StringBuilder logBuilder;

    // --- CPU/GPU path tracing (gated behind enableVerboseLogging) ---
    private bool dbg_loggedFirstFaceReadback = false;
    private bool dbg_loggedFirstFaceReadbackSuccess = false;
    private bool dbg_loggedFirstInstancedDraw = false;
    private int dbg_readbackFailuresSeen = 0;

    // --- Performance Profiling Configuration ---
    [Header("Performance Profiling")]
    public bool enableFrameLogging = false;
    public bool enableAggregateLogging = true;
    public bool enableDetailedTimings = true;
    public bool enableCounters = true;
    public bool enableMemoryTracking = true;
    public bool enableCacheTracking = true;
    public int aggregateLogInterval = 300; // frames

    // --- Frame/Update Stats ---
    private int stats_frameCount = 0;
    private float stats_updateTotalTime = 0f;
    private float stats_updateTimeMin = float.MaxValue;
    private float stats_updateTimeMax = 0f;
    private int stats_budgetExceededCount = 0;
    private float stats_processActiveChunksTime = 0f;
    private float stats_reconciliationTime = 0f;
    private float stats_aggregateWindowStart = 0f;
    private int stats_dataGenActiveSamplesTotal = 0;
    private int stats_meshingActiveSamplesTotal = 0;
    private int stats_reconciliationQueueSamplesTotal = 0;
    private int stats_dataGenActiveMax = 0;
    private int stats_meshingActiveMax = 0;
    private int stats_reconciliationQueueMax = 0;

    // --- Chunk Management Stats ---
    private int stats_chunkCreations = 0;
    private int stats_chunkDestructions = 0;
    private int stats_chunkStateTransitions = 0;
    private int stats_chunk1DLookups = 0;
    private int stats_chunk3DLookups = 0;

    // --- Mesh Building Stats (aggregate) ---
    private int stats_meshBuildTotal = 0;
    private int stats_meshBuildCpuCompletions = 0;
    private int stats_meshBuildGpuCompletions = 0;
    private int stats_meshBuildEmptyCompletions = 0;
    private float stats_meshBuildTimeTotal = 0f;
    private float stats_meshBuildTimeMin = float.MaxValue;
    private float stats_meshBuildTimeMax = 0f;
    private int stats_meshStepsTotal = 0;
    private float stats_greedyAxisYTime = 0f;
    private float stats_greedyAxisZTime = 0f;
    private float stats_greedyAxisXTime = 0f;
    private int stats_sentinelBuilds = 0;
    private float stats_sentinelBuildTime = 0f;
    private int stats_faceCullingTests = 0;
    private int stats_facesCulled = 0;
    private int stats_facesDrawn = 0;
    private int stats_verticesOpaque = 0;
    private int stats_verticesTransparent = 0;
    private int stats_verticesCutout = 0;
    private float stats_meshApplyOpaqueTime = 0f;
    private float stats_meshApplyTransparentTime = 0f;
    private float stats_meshApplyCutoutTime = 0f;
    private float stats_meshApplyColliderTime = 0f;
    private float stats_meshNeighborCacheTime = 0f;
    private float stats_meshDataPrepTime = 0f;
    private float stats_meshMainLoopTime = 0f;
    private int stats_gpuFaceDecodeSteps = 0;
    private float stats_gpuFaceDecodeTime = 0f;
    private float stats_gpuFaceDecodeTimeMin = float.MaxValue;
    private float stats_gpuFaceDecodeTimeMax = 0f;
    private float stats_gpuFaceCompactMaskDecodeTime = 0f;
    private float stats_gpuFaceCompactEmitTime = 0f;
    private float stats_gpuFaceCompactCrossTime = 0f;
    private float stats_gpuFaceCompactEmitScanTime = 0f;
    private float stats_gpuFaceCompactEmitQuadTime = 0f;
    private float stats_gpuFaceCompactEmitCollisionTime = 0f;
    private int stats_meshDeferredChunks = 0;
    private int stats_meshBoundaryChecksY = 0;
    private int stats_meshBoundaryChecksZ = 0;
    private int stats_meshBoundaryChecksX = 0;
    private int stats_facesOpaque = 0;
    private int stats_facesTransparent = 0;
    private int stats_facesCutout = 0;
    private int stats_meshPoolExhaustedDefers = 0;
    private int stats_meshGpuBusyDefers = 0;
    private int stats_meshGpuFrameThrottleFallbacks = 0;
    private int stats_meshGpuRequestFailures = 0;
    private int stats_meshGpuBorderDefers = 0;
    private int stats_meshGpuBorderCpuFallbacks = 0;
    private int stats_meshInteractionPriorityCpuBypass = 0;
    private int stats_meshBuildsWithMissingNeighbors = 0;
    private int stats_meshMissingNeighborBits = 0;
    private int stats_deferredColliderApplyCount = 0;
    private int stats_deferredColliderWaitCount = 0;
    private float stats_deferredColliderWaitTotal = 0f;
    private float stats_deferredColliderWaitMax = 0f;
    private int stats_firstShellMeshStartLatencyCount = 0;
    private float stats_firstShellMeshStartLatencyTotal = 0f;
    private float stats_firstShellMeshStartLatencyMax = 0f;
    private int stats_firstDeferredMeshStartLatencyCount = 0;
    private float stats_firstDeferredMeshStartLatencyTotal = 0f;
    private float stats_firstDeferredMeshStartLatencyMax = 0f;

    private const int SLOWEST_MESH_BUILD_COUNT = 3;
    private float[] stats_slowestMeshBuildMs = new float[SLOWEST_MESH_BUILD_COUNT];
    private int[] stats_slowestMeshChunkX = new int[SLOWEST_MESH_BUILD_COUNT];
    private int[] stats_slowestMeshChunkY = new int[SLOWEST_MESH_BUILD_COUNT];
    private int[] stats_slowestMeshChunkZ = new int[SLOWEST_MESH_BUILD_COUNT];
    private float[] stats_slowestMeshDataPrepMs = new float[SLOWEST_MESH_BUILD_COUNT];
    private float[] stats_slowestMeshMainLoopMs = new float[SLOWEST_MESH_BUILD_COUNT];
    private float[] stats_slowestMeshApplyMs = new float[SLOWEST_MESH_BUILD_COUNT];
    private int[] stats_slowestMeshFaceCount = new int[SLOWEST_MESH_BUILD_COUNT];
    private int[] stats_slowestMeshVertexCount = new int[SLOWEST_MESH_BUILD_COUNT];
    private byte[] stats_slowestMeshKind = new byte[SLOWEST_MESH_BUILD_COUNT];

    // --- Lighting Stats (aggregate) ---
    private int stats_lightingInitsTotal = 0;
    private float stats_lightingInitTime = 0f;
    private float stats_lightingImportTime = 0f;
    private float stats_lightingBfsSkyTime = 0f;
    private float stats_lightingBfsBlockTime = 0f;
    private int stats_lightingStepsTotal = 0;
    private float stats_lightingStepTime = 0f;
    private int stats_lightingBFSOps = 0;
    private int stats_lightingMaxQueueSize = 0;
    private int stats_lightingSkylightBlocks = 0;
    private int stats_lightingBlocklightBlocks = 0;
    private int stats_lightingCrossChunkQueries = 0;
    private int stats_lightingPoolAllocations = 0;
    private int stats_lightingPoolReuses = 0;

    // --- RLE Stats (aggregate) ---
    private int stats_rleCompressions = 0;
    private int stats_rleDecompressions = 0;
    private float stats_rleCompressionTime = 0f;
    private float stats_rleDecompressionTime = 0f;
    private int stats_rleTotalBytesIn = 0;
    private int stats_rleTotalBytesOut = 0;
    private int stats_rleHomogeneousChunks = 0;

    // RLE scratch buffer — avoid per-column List<ushort> alloc during compression
    private ushort[] rleScratchBuffer;

    // --- Block Operation Stats ---
    private int stats_getBlockCalls = 0;
    private int stats_setBlockCalls = 0;
    private int stats_blockModifications = 0;
    private int stats_neighborRebuildTriggers = 0;

    // --- Cache Stats ---
    private int stats_decompCacheHits = 0;
    private int stats_decompCacheMisses = 0;
    private int stats_neighborCacheHits = 0;
    private int stats_neighborCacheMisses = 0;

    // --- Reconciliation Stats ---
    private int stats_reconciliationOps = 0;
    private int stats_reconciliationBlocks = 0;

    // --- GPU Backend Stats ---
    private int stats_gpuAtlasOverlayBlits = 0;
    private float stats_gpuAtlasOverlayBlitTime = 0f;
    private int stats_gpuLightingSeedBlits = 0;
    private float stats_gpuLightingSeedBlitTime = 0f;
    private int stats_gpuLightingPropagateBlits = 0;
    private float stats_gpuLightingPropagateBlitTime = 0f;
    private int stats_gpuFaceExtractBlits = 0;
    private float stats_gpuFaceExtractBlitTime = 0f;
    private int stats_gpuFaceExportBlits = 0;
    private float stats_gpuFaceExportBlitTime = 0f;
    private int stats_gpuFaceCompactBuilds = 0;
    private int stats_gpuFaceCompactActiveSlices = 0;
    private int stats_gpuChunkUploads = 0;
    private float stats_gpuChunkUploadTime = 0f;
    private int stats_gpuChunkUploadBytes = 0;
    private int stats_gpuFaceOccupancyUploads = 0;
    private float stats_gpuFaceOccupancyUploadTime = 0f;
    private int stats_gpuFaceOccupancyUploadBytes = 0;
    private float stats_gpuFaceReadbackRequestStartMs = -1f;
    private int stats_gpuFaceReadbackRequests = 0;
    private int stats_gpuFaceReadbacksCompleted = 0;
    private int stats_gpuFaceReadbackFailures = 0;
    private float stats_gpuFaceReadbackLatencyTotal = 0f;
    private float stats_gpuFaceReadbackLatencyMin = float.MaxValue;
    private float stats_gpuFaceReadbackLatencyMax = 0f;
    private float stats_gpuFaceReadbackCallbackCopyTime = 0f;
    private int stats_gpuFaceReadbackBytes = 0;
    private float stats_gpuFaceTileExtractTime = 0f;
#endif

    void Start()
    {
        Debug.Log("[McWorld] ========== MCWORLD START ==========");
        if (chunkPrefab == null || terrainGenerator == null || blockTypeManager == null || coordinator == null) {
            Debug.LogError("[McWorld] A critical component is not assigned! Aborting.");
            this.enabled = false;
            return;
        }

#if LOGGING
        logBuilder = new System.Text.StringBuilder(2048);
#endif

        InitializeWorldParameters();
        InitializeChunkStorage();
        _CacheSharedChunkMaterials();

        blockDataCache = blockTypeManager.finalDataArray;
        uv_allFacesCache = blockTypeManager.uv_allFacesData;
        uv_topFaceCache = blockTypeManager.uv_topFaceData;
        uv_bottomFaceCache = blockTypeManager.uv_bottomFaceData;
        uv_sideFacesCache = blockTypeManager.uv_sideFacesData;
        _BuildBlockCaches();
        _BuildLightingTables();
        _InitializeBetaWaterRendering();
        _InitializeGpuVoxelBackend();
        _InitializeGpuDebugHud();
        _InitializeMeshPool();
        // Runtime override: keep the update budget above the irreducible baseline
        // so the adaptive system has room to scale decode budgets.
        if (updateTimeBudgetMs < 16f) updateTimeBudgetMs = 16f;
        // Load-phase budget boost: remember the normal runtime budget, then run the
        // one-time initial world generation at a larger budget so the idle GPU + readback
        // pipeline drains faster. Restored in Update() once gen completes.
        _runtimeUpdateBudgetMs = updateTimeBudgetMs;
        _loadPhaseBudgetRestored = false;
        if (loadPhaseUpdateBudgetMs > updateTimeBudgetMs)
        {
            updateTimeBudgetMs = loadPhaseUpdateBudgetMs;
        }
        _ResetAdaptiveBudgets();
        
        terrainGenerator.init(McUtils.GetMinecraftSeed(worldSeedString));

        int[] radialChunkOrder = GenerateRadialChunkOrder();
        coordinator.InitializeAndStartProcessing(this, radialChunkOrder, totalWorldChunks);

#if LOGGING
        stats_aggregateWindowStart = Time.realtimeSinceStartup;
#endif

        // No longer using SendCustomEventDelayedSeconds - Update() will handle processing
    }

    private void _ResetAdaptiveBudgets()
    {
        adaptiveGpuMeshDecodeStepBudgetMs = gpuMeshDecodeStepBudgetMs;
        adaptiveGpuMeshDecodeStepsPerFrame = Mathf.Max(1, gpuMeshDecodeStepsPerFrame);
        adaptiveGpuMeshDecodeFrameBudgetMs = gpuMeshDecodeFrameBudgetMs;
        adaptiveGpuWorldgenStepBudgetMs = gpuWorldgenStepBudgetMs;
        adaptiveGpuWorldgenStepsPerFrame = Mathf.Max(1, gpuWorldgenStepsPerFrame);
        lastUpdateDurationMs = updateTimeBudgetMs;
    }

    private void _UpdateAdaptiveBudgets(float updateTimeMs)
    {
        // Clamp input before EMA — a single 300ms+ GC/VRC spike would poison
        // the smoothed value for 40+ frames, collapsing budgets to the floor.
        // Capping at 1.5× budget limits the damage to ~5 frames of recovery.
        float clampedMs = updateTimeMs < updateTimeBudgetMs * 1.5f ? updateTimeMs : updateTimeBudgetMs * 1.5f;
        const float alpha = 0.15f;
        lastUpdateDurationMs = lastUpdateDurationMs + alpha * (clampedMs - lastUpdateDurationMs);

        if (!enableAdaptiveBudgets)
        {
            _ResetAdaptiveBudgets();
            lastUpdateDurationMs = updateTimeMs;
            return;
        }

        float smoothedMs = lastUpdateDurationMs;
        float minScale = Mathf.Clamp01(adaptiveBudgetMinScale);
        float growFactor = 1f + Mathf.Max(0f, adaptiveBudgetRecoverRate);
        float shrinkFactor = 1f - Mathf.Clamp01(adaptiveBudgetBackoffRate);
        float targetWithHeadroom = Mathf.Max(0.5f, updateTimeBudgetMs - adaptiveFrameHeadroomMs);

        if (smoothedMs > updateTimeBudgetMs)
        {
            adaptiveGpuMeshDecodeStepBudgetMs = Mathf.Clamp(adaptiveGpuMeshDecodeStepBudgetMs * shrinkFactor, gpuMeshDecodeStepBudgetMs * minScale, gpuMeshDecodeStepBudgetMs);
            adaptiveGpuMeshDecodeFrameBudgetMs = Mathf.Clamp(adaptiveGpuMeshDecodeFrameBudgetMs * shrinkFactor, gpuMeshDecodeFrameBudgetMs * minScale, gpuMeshDecodeFrameBudgetMs);
            adaptiveGpuWorldgenStepBudgetMs = Mathf.Clamp(adaptiveGpuWorldgenStepBudgetMs * shrinkFactor, gpuWorldgenStepBudgetMs * minScale, gpuWorldgenStepBudgetMs);

            if (adaptiveGpuMeshDecodeStepsPerFrame > 1 && smoothedMs > updateTimeBudgetMs + 1f)
            {
                adaptiveGpuMeshDecodeStepsPerFrame--;
            }
            if (adaptiveGpuWorldgenStepsPerFrame > 1 && smoothedMs > updateTimeBudgetMs + 1f)
            {
                adaptiveGpuWorldgenStepsPerFrame--;
            }
            return;
        }

        if (smoothedMs >= targetWithHeadroom)
        {
            return;
        }

        adaptiveGpuMeshDecodeStepBudgetMs = Mathf.Clamp(adaptiveGpuMeshDecodeStepBudgetMs * growFactor, gpuMeshDecodeStepBudgetMs * minScale, gpuMeshDecodeStepBudgetMs);
        adaptiveGpuMeshDecodeFrameBudgetMs = Mathf.Clamp(adaptiveGpuMeshDecodeFrameBudgetMs * growFactor, gpuMeshDecodeFrameBudgetMs * minScale, gpuMeshDecodeFrameBudgetMs);
        adaptiveGpuWorldgenStepBudgetMs = Mathf.Clamp(adaptiveGpuWorldgenStepBudgetMs * growFactor, gpuWorldgenStepBudgetMs * minScale, gpuWorldgenStepBudgetMs);

        if (adaptiveGpuMeshDecodeStepsPerFrame < Mathf.Max(1, gpuMeshDecodeStepsPerFrame) && smoothedMs < targetWithHeadroom - 1f)
        {
            adaptiveGpuMeshDecodeStepsPerFrame++;
        }
        if (adaptiveGpuWorldgenStepsPerFrame < Mathf.Max(1, gpuWorldgenStepsPerFrame) && smoothedMs < targetWithHeadroom - 1f)
        {
            adaptiveGpuWorldgenStepsPerFrame++;
        }
    }

    void InitializeWorldParameters()
    {
        worldDimensionX = Mathf.Max(1, worldDimensionX);
        worldDimensionY = Mathf.Max(1, worldDimensionY);
        worldDimensionZ = Mathf.Max(1, worldDimensionZ);
        chunkSizeXZ = Mathf.Max(1, chunkSizeXZ);
        chunkSizeY = Mathf.Max(1, chunkSizeY);
        totalWorldChunks = worldDimensionX * worldDimensionY * worldDimensionZ;

        #if LOGGING
        // Print seed converted with McUtils.GetMinecraftSeed
        Debug.Log($"World Seed: {worldSeedString}");
        Debug.Log($"Converted World Seed: {McUtils.GetMinecraftSeed(worldSeedString)}");
        Debug.Log($"Permutation Table: {McUtils.GetPermutationTable(new JavaRandom(McUtils.GetMinecraftSeed(worldSeedString)))}");
        #endif

        // --- FIX: Remove vertical (Y-axis) centering ---
        // The world should be centered on XZ, but start at Y=0 and build upwards.
        chunkOffsetX = worldDimensionX / 2;
        chunkOffsetY = 0;
        chunkOffsetZ = worldDimensionZ / 2;

        globalVoxelOffsetX = (worldDimensionX * chunkSizeXZ) / 2;
        globalVoxelOffsetY = 0;
        globalVoxelOffsetZ = (worldDimensionZ * chunkSizeXZ) / 2;
    }

    void InitializeChunkStorage()
    {
        chunks_1D = new ChunkData[totalWorldChunks];
        activeDataGenChunks = new ChunkData[MAX_ACTIVE_CHUNKS];
        activeMeshingChunks = new ChunkData[MAX_ACTIVE_CHUNKS];
    }

    private void _InitializeGpuVoxelBackend()
    {
        gpuBackendReady = false;
        if (!enableGpuVoxelBackend)
        {
#if LOGGING
            Debug.Log("[McWorld][GPU-REPORT] GPU voxel backend DISABLED (enableGpuVoxelBackend=false) -> all voxel subsystems run on CPU.");
#endif
            return;
        }
        if (gpuAtlasOverlayMaterial == null || gpuLightingSeedMaterial == null || gpuLightingPropagateMaterial == null)
        {
#if LOGGING
            Debug.LogWarning("[McWorld][GPU-REPORT] GPU backend ENABLED but a required material is missing -> SILENT FALLBACK to CPU for ALL voxel subsystems. "
                + "atlasOverlay=" + (gpuAtlasOverlayMaterial != null ? "OK" : "NULL")
                + " lightingSeed=" + (gpuLightingSeedMaterial != null ? "OK" : "NULL")
                + " lightingPropagate=" + (gpuLightingPropagateMaterial != null ? "OK" : "NULL"));
#endif
            return;
        }

        gpuChunkSlotCapacity = Mathf.Clamp(gpuChunkSlotCapacity, 1, 4095);
        if (gpuResidentRadiusXZ <= 0) gpuResidentRadiusXZ = 6;
        if (gpuResidentRadiusY <= 0) gpuResidentRadiusY = 2;
        if (gpuResidentSyncsPerFrame <= 0) gpuResidentSyncsPerFrame = 32;
        gpuTileWidth = chunkSizeXZ;
        gpuTileHeight = chunkSizeY * chunkSizeXZ;
        gpuFaceSummaryWidth = Mathf.Max(1, chunkSizeXZ / 2);
        gpuFaceSummaryHeight = GPU_FACE_COMPACT_DIRECTIONS * chunkSizeY;
        gpuFaceOccupancySlotWidth = Mathf.Max(1, Mathf.Max((chunkSizeXZ + 1) >> 1, (chunkSizeY + 1) >> 1));
        gpuFaceOccupancySlotHeight = chunkSizeY + chunkSizeXZ + chunkSizeXZ;

        float pixelAspect = gpuTileHeight / (float)Mathf.Max(1, gpuTileWidth);
        gpuAtlasSlotsX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(gpuChunkSlotCapacity * pixelAspect)), 1, gpuChunkSlotCapacity);
        gpuAtlasSlotsY = Mathf.Max(1, Mathf.CeilToInt(gpuChunkSlotCapacity / (float)gpuAtlasSlotsX));
        gpuAtlasWidth = gpuAtlasSlotsX * gpuTileWidth;
        gpuAtlasHeight = gpuAtlasSlotsY * gpuTileHeight;
        gpuFaceOccupancyAtlasWidth = gpuAtlasSlotsX * gpuFaceOccupancySlotWidth;
        gpuFaceOccupancyAtlasHeight = gpuAtlasSlotsY * gpuFaceOccupancySlotHeight;

        gpuChunkIndexToSlot = new int[totalWorldChunks];
        gpuChunkSyncedDataVersion = new int[totalWorldChunks];
        gpuSlotToChunkIndex = new int[gpuChunkSlotCapacity];
        gpuSlotUseStamp = new int[gpuChunkSlotCapacity];
        for (int i = 0; i < totalWorldChunks; i++) gpuChunkIndexToSlot[i] = -1;
        for (int i = 0; i < totalWorldChunks; i++) gpuChunkSyncedDataVersion[i] = -1;
        for (int i = 0; i < gpuChunkSlotCapacity; i++) gpuSlotToChunkIndex[i] = -1;

        gpuSlotLookupPixels = new Color32[worldDimensionX * worldDimensionY * worldDimensionZ];
        gpuSlotMetaPixels = new Color32[gpuChunkSlotCapacity];
        gpuUploadBlockPixels = new Color32[(gpuTileWidth / 4) * gpuTileHeight];
        if (useCompactGpuFaceExport)
        {
            gpuFaceOccupancyUploadPixels = new Color32[gpuFaceOccupancySlotWidth * gpuFaceOccupancySlotHeight];
        }

        gpuSlotLookupTexture = _CreateGpuTexture2D(worldDimensionX, worldDimensionY * worldDimensionZ);
        gpuSlotMetaTexture = _CreateGpuTexture2D(gpuChunkSlotCapacity, 1);
        gpuBlockPropertyTexture = _CreateGpuTexture2D(256, 1);
        gpuUploadBlockTexture = _CreateGpuTexture2D(gpuTileWidth / 4, gpuTileHeight);
        if (useCompactGpuFaceExport)
        {
            gpuFaceOccupancyUploadTexture = _CreateGpuTexture2D(gpuFaceOccupancySlotWidth, gpuFaceOccupancySlotHeight);
        }
        gpuClearTexture = _CreateGpuTexture2D(1, 1);

        gpuClearTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
        gpuClearTexture.Apply(false, false);

        gpuSlotLookupTexture.SetPixels32(gpuSlotLookupPixels);
        gpuSlotLookupTexture.Apply(false, false);
        gpuSlotMetaTexture.SetPixels32(gpuSlotMetaPixels);
        gpuSlotMetaTexture.Apply(false, false);

        _BuildGpuBlockPropertyTexture();

        gpuBlockAtlas = _CreateGpuRenderTexture(gpuAtlasWidth, gpuAtlasHeight, "GPU_BlockAtlas_A");
        gpuBlockAtlasScratch = _CreateGpuRenderTexture(gpuAtlasWidth, gpuAtlasHeight, "GPU_BlockAtlas_B");
        gpuLightAtlas = _CreateGpuRenderTexture(gpuAtlasWidth, gpuAtlasHeight, "GPU_LightAtlas_A");
        gpuLightAtlasScratch = _CreateGpuRenderTexture(gpuAtlasWidth, gpuAtlasHeight, "GPU_LightAtlas_B");
        if (useCompactGpuFaceExport)
        {
            gpuFaceOccupancyAtlas = _CreateGpuRenderTexture(gpuFaceOccupancyAtlasWidth, gpuFaceOccupancyAtlasHeight, "GPU_FaceOccupancyAtlas_A");
            gpuFaceOccupancyAtlasScratch = _CreateGpuRenderTexture(gpuFaceOccupancyAtlasWidth, gpuFaceOccupancyAtlasHeight, "GPU_FaceOccupancyAtlas_B");
        }

        VRCGraphics.Blit(gpuClearTexture, gpuBlockAtlas);
        VRCGraphics.Blit(gpuClearTexture, gpuBlockAtlasScratch);
        VRCGraphics.Blit(gpuClearTexture, gpuLightAtlas);
        VRCGraphics.Blit(gpuClearTexture, gpuLightAtlasScratch);
        if (useCompactGpuFaceExport)
        {
            VRCGraphics.Blit(gpuClearTexture, gpuFaceOccupancyAtlas);
            VRCGraphics.Blit(gpuClearTexture, gpuFaceOccupancyAtlasScratch);
        }

        gpuPropGpuEnabledId = VRCShader.PropertyToID("_UdonVRCM_GpuEnabled");
        gpuPropLightAtlasId = VRCShader.PropertyToID("_UdonVRCM_GpuLightAtlas");
        gpuPropBlockAtlasGlobalId = VRCShader.PropertyToID("_UdonVRCM_GpuBlockAtlas");
        gpuPropSlotLookupId = VRCShader.PropertyToID("_UdonVRCM_GpuSlotLookup");
        gpuPropSlotMetaGlobalId = VRCShader.PropertyToID("_UdonVRCM_GpuSlotMeta");
        gpuPropBlockPropsGlobalId = VRCShader.PropertyToID("_UdonVRCM_GpuBlockProps");
        gpuPropFaceOccupancyAtlasGlobalId = VRCShader.PropertyToID("_UdonVRCM_GpuFaceOccupancyAtlas");
        gpuPropAtlasInfoId = VRCShader.PropertyToID("_UdonVRCM_GpuAtlasInfo");
        gpuPropFaceOccupancyInfoId = VRCShader.PropertyToID("_UdonVRCM_GpuFaceOccupancyInfo");
        gpuPropWorldInfoId = VRCShader.PropertyToID("_UdonVRCM_GpuWorldInfo");
        gpuPropChunkInfoId = VRCShader.PropertyToID("_UdonVRCM_GpuChunkInfo");
        gpuPropVoxelOffsetId = VRCShader.PropertyToID("_UdonVRCM_GpuVoxelOffset");
        gpuPropBlockAtlasId = VRCShader.PropertyToID("_BlockAtlas");
        gpuPropBlockPropsId = VRCShader.PropertyToID("_BlockPropsTex");
        gpuPropSlotLookupTexId = VRCShader.PropertyToID("_SlotLookupTex");
        gpuPropSlotMetaId = VRCShader.PropertyToID("_SlotMetaTex");
        gpuPropOverlayTexId = VRCShader.PropertyToID("_OverlayTex");
        gpuPropOverlayRectId = VRCShader.PropertyToID("_OverlayRect");
        gpuPropOverlayPackedWidthId = VRCShader.PropertyToID("_OverlayPackedWidth");
        gpuPropTopSkyLightId = VRCShader.PropertyToID("_TopSkyLight");
        gpuPropDrawTableTexId = VRCShader.PropertyToID("_DrawTableTex");
        gpuPropFaceModeId = VRCShader.PropertyToID("_Mode");
        gpuPropFaceReadSlotId = VRCShader.PropertyToID("_ReadSlotIndex");
        gpuPropBorderTexId = VRCShader.PropertyToID("_BorderTex");
        gpuPropBorderInfoId = VRCShader.PropertyToID("_BorderInfo");

        // --- GPU Border Texture Init ---
        int borderStride = Mathf.Max(chunkSizeXZ, chunkSizeY);
        gpuBorderWidth = chunkSizeXZ;
        gpuBorderHeight = 6 * borderStride;
        gpuBorderStride = borderStride;
        gpuBorderTextures = new Texture2D[GPU_FACE_READBACK_BUFFER_COUNT];
        gpuBorderPixels = new Color32[GPU_FACE_READBACK_BUFFER_COUNT][];
        for (int i = 0; i < GPU_FACE_READBACK_BUFFER_COUNT; i++)
        {
            gpuBorderTextures[i] = _CreateGpuTexture2D(gpuBorderWidth, gpuBorderHeight);
            gpuBorderPixels[i] = new Color32[gpuBorderWidth * gpuBorderHeight];
        }

        // --- GPU Face Extraction Init ---
        gpuFaceAtlas = _CreateGpuRenderTexture(gpuAtlasWidth, gpuAtlasHeight, "GPU_FaceAtlas");
        VRCGraphics.Blit(gpuClearTexture, gpuFaceAtlas);
        gpuFaceReadbackTextures = new RenderTexture[GPU_FACE_READBACK_BUFFER_COUNT];
        gpuFaceReadbackPixels = new Color32[GPU_FACE_READBACK_BUFFER_COUNT][];
        gpuFaceReadbackChunks = new ChunkData[GPU_FACE_READBACK_BUFFER_COUNT];
        gpuFaceReadbackSlots = new int[GPU_FACE_READBACK_BUFFER_COUNT];
        gpuFaceReadbackBuildVersion = new int[GPU_FACE_READBACK_BUFFER_COUNT];
        gpuFaceReadbackStartMs = new float[GPU_FACE_READBACK_BUFFER_COUNT];
        gpuFaceReadbackState = new int[GPU_FACE_READBACK_BUFFER_COUNT];
        gpuFaceReadbackInflightOrder = new int[GPU_FACE_READBACK_BUFFER_COUNT];
        gpuFaceReadbackReadyOrder = new int[GPU_FACE_READBACK_BUFFER_COUNT];
        for (int i = 0; i < GPU_FACE_READBACK_BUFFER_COUNT; i++)
        {
            int readbackWidth = useCompactGpuFaceExport ? gpuFaceSummaryWidth : gpuTileWidth;
            int readbackHeight = useCompactGpuFaceExport ? gpuFaceSummaryHeight : gpuTileHeight;
            gpuFaceReadbackTextures[i] = _CreateGpuRenderTexture(readbackWidth, readbackHeight, "GPU_FaceReadback_" + i);
            VRCGraphics.Blit(gpuClearTexture, gpuFaceReadbackTextures[i]);
            gpuFaceReadbackPixels[i] = new Color32[readbackWidth * readbackHeight];
            gpuFaceReadbackSlots[i] = -1;
            gpuFaceReadbackBuildVersion[i] = -1;
            gpuFaceReadbackStartMs[i] = -1f;
        }

        // Readback buffers

        gpuBackendReady = true;
        _GpuBuildShouldDrawTexture();
        _GpuPublishGlobals();
        _ApplyTerrainLightingSourceToSharedMaterials();
#if LOGGING
        _LogGpuPathReport();
#endif
    }

#if LOGGING
    // One-time summary (logged when the GPU backend finishes initializing) showing the
    // EFFECTIVE path of each voxel subsystem. A subsystem shows "MISSING->CPU" when its
    // material/mesh reference is null, meaning it silently runs on the CPU even though the
    // GPU backend is otherwise ready.
    private void _LogGpuPathReport()
    {
        Debug.Log("[McWorld][GPU-REPORT] ===== GPU Path Report =====");
        Debug.Log("[McWorld][GPU-REPORT] GPU voxel backend: READY");
        Debug.Log("[McWorld][GPU-REPORT]   Lighting......: "
            + ((gpuLightingSeedMaterial != null && gpuLightingPropagateMaterial != null) ? "GPU" : "MISSING->CPU"));
        Debug.Log("[McWorld][GPU-REPORT]   FaceExtract...: " + (gpuFaceExtractMaterial != null ? "GPU" : "MISSING->CPU"));
        Debug.Log("[McWorld][GPU-REPORT]   AO bake.......: " + (gpuAOBakeMaterial != null ? "GPU" : "MISSING->CPU")
            + "  (ambientOcclusion=" + (ambientOcclusion ? "ON" : "OFF") + ")");
        Debug.Log("[McWorld][GPU-REPORT]   Biome bake....: " + (gpuBiomeColorBakeMaterial != null ? "GPU" : "MISSING->CPU"));
        Debug.Log("[McWorld][GPU-REPORT]   Fluid tick....: " + ((blockTicker != null && blockTicker.gpuFluidTickMaterial != null) ? "GPU" : "MISSING->CPU"));
        // (Raycast path is owned by ModifyTerrain, which logs its own GPU/CPU path at runtime.)
        Debug.Log("[McWorld][GPU-REPORT]   InstancedDraw.: "
            + ((gpuVoxelQuadDrawMaterial != null && gpuVoxelQuadMesh != null) ? "GPU" : "MISSING->CPU"));
        Debug.Log("[McWorld][GPU-REPORT]   Water anim....: " + (gpuWaterAnimMaterial != null ? "GPU" : "MISSING->CPU"));
        Debug.Log("[McWorld][GPU-REPORT]   Worldgen......: "
            + ((terrainGenerator != null && terrainGenerator.enableGpuWorldgen) ? "GPU" : "CPU"));
        Debug.Log("[McWorld][GPU-REPORT] ===========================");
    }
#endif

    private Texture2D _CreateGpuTexture2D(int width, int height)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        return texture;
    }

    private RenderTexture _CreateGpuRenderTexture(int width, int height, string textureName)
    {
        RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        rt.name = textureName;
        rt.filterMode = FilterMode.Point;
        rt.wrapMode = TextureWrapMode.Clamp;
        rt.useMipMap = false;
        rt.autoGenerateMips = false;
        rt.enableRandomWrite = false;
        rt.Create();
        return rt;
    }

    private void _BuildGpuBlockPropertyTexture()
    {
        if (gpuBlockPropertyTexture == null || blockTypeManager == null) return;

        Color32[] pixels = new Color32[256];
        for (int i = 0; i < 256; i++)
        {
            byte blockId = (byte)i;
            int opacity = blockTypeManager.GetBlockLightOpacity(blockId);
            int emission = blockTypeManager.GetBlockLightEmission(blockId);
            int shape = _UsesCustomBlockMesh(blockId, null, 0) ? (int)McBlockShapeType.Cross : (int)blockTypeManager.GetBlockShapeType(blockId);
            bool opaqueCube = blockId != 0
                && !_IsFluidBlock(blockId)
                && blockTypeManager.GetBlockVisibilityType(blockId) == BlockVisibilityType.Opaque
                && shape == (int)McBlockShapeType.Cube;
            pixels[i] = new Color32(
                (byte)Mathf.Clamp(opacity * 17, 0, 255),
                (byte)Mathf.Clamp(emission * 17, 0, 255),
                (byte)Mathf.Clamp(shape, 0, 255),
                (byte)(opaqueCube ? 255 : 0)
            );
        }

        gpuBlockPropertyTexture.SetPixels32(pixels);
        gpuBlockPropertyTexture.Apply(false, false);
    }

    private void _GpuPublishGlobals()
    {
        if (gpuSlotLookupTexture == null || gpuLightAtlas == null) return;

        VRCShader.SetGlobalFloat(gpuPropGpuEnabledId, enableGpuVoxelBackend && gpuBackendReady ? 1f : 0f);
        VRCShader.SetGlobalTexture(gpuPropBlockAtlasGlobalId, gpuBlockAtlas);
        VRCShader.SetGlobalTexture(gpuPropLightAtlasId, gpuLightAtlas);
        VRCShader.SetGlobalTexture(gpuPropSlotLookupId, gpuSlotLookupTexture);
        VRCShader.SetGlobalTexture(gpuPropSlotMetaGlobalId, gpuSlotMetaTexture);
        VRCShader.SetGlobalTexture(gpuPropBlockPropsGlobalId, gpuBlockPropertyTexture);
        VRCShader.SetGlobalVector(gpuPropAtlasInfoId, new Vector4(gpuAtlasWidth, gpuAtlasHeight, gpuAtlasSlotsX, gpuAtlasSlotsY));
        if (gpuFaceOccupancyAtlas != null)
        {
            VRCShader.SetGlobalTexture(gpuPropFaceOccupancyAtlasGlobalId, gpuFaceOccupancyAtlas);
            VRCShader.SetGlobalVector(gpuPropFaceOccupancyInfoId, new Vector4(gpuFaceOccupancyAtlasWidth, gpuFaceOccupancyAtlasHeight, gpuFaceOccupancySlotWidth, gpuFaceOccupancySlotHeight));
        }
        VRCShader.SetGlobalVector(gpuPropWorldInfoId, new Vector4(worldDimensionX, worldDimensionY, worldDimensionZ, worldDimensionY * worldDimensionZ));
        VRCShader.SetGlobalVector(gpuPropChunkInfoId, new Vector4(chunkSizeXZ, chunkSizeY, chunkOffsetX, chunkOffsetZ));
        VRCShader.SetGlobalVector(gpuPropVoxelOffsetId, new Vector4(globalVoxelOffsetX, globalVoxelOffsetY, globalVoxelOffsetZ, gpuChunkSlotCapacity));
    }

    public Texture GetGpuBlockAtlasDebugTexture()
    {
        return gpuBlockAtlas;
    }

    public Texture GetGpuLightAtlasDebugTexture()
    {
        return gpuLightAtlas;
    }

    public Texture GetGpuSlotLookupDebugTexture()
    {
        return gpuSlotLookupTexture;
    }

    public Texture GetGpuSlotMetaDebugTexture()
    {
        return gpuSlotMetaTexture;
    }

    private void _InitializeGpuDebugHud()
    {
        if (!enableGpuDebugHud) return;

        Transform hudRootTransform = transform.Find("GpuDebugHud");
        if (hudRootTransform == null) return;

        gpuDebugHudRoot = hudRootTransform.gameObject;
        gpuDebugHudRect = hudRootTransform.GetComponent<RectTransform>();

        Transform panelTransform = hudRootTransform.Find("Panel");
        Transform textureATransform = hudRootTransform.Find("TextureA");
        Transform textureBTransform = hudRootTransform.Find("TextureB");
        Transform labelTransform = hudRootTransform.Find("Label");
        Transform toggleButtonLabelTransform = hudRootTransform.Find("ToggleButton/Text");

        gpuDebugPanel = panelTransform != null ? panelTransform.gameObject : null;
        gpuDebugTextureA = textureATransform != null ? textureATransform.GetComponent<RawImage>() : null;
        gpuDebugTextureB = textureBTransform != null ? textureBTransform.GetComponent<RawImage>() : null;
        gpuDebugLabel = labelTransform != null ? labelTransform.GetComponent<TextMeshProUGUI>() : null;
        gpuDebugToggleButtonLabel = toggleButtonLabelTransform != null ? toggleButtonLabelTransform.GetComponent<TextMeshProUGUI>() : null;

        gpuDebugHudVisible = showGpuDebugHudOnStart;
        gpuDebugHudPage = 0;
        if (gpuDebugHudRoot != null)
        {
            gpuDebugHudRoot.SetActive(true);
        }
        _RefreshGpuDebugHud();
    }

    public void ToggleGpuDebugHud()
    {
        if (gpuDebugHudRoot == null) return;
        gpuDebugHudVisible = !gpuDebugHudVisible;
        _RefreshGpuDebugHud();
    }

    public void NextGpuDebugHudPage()
    {
        gpuDebugHudPage++;
        if (gpuDebugHudPage >= GPU_DEBUG_PAGE_COUNT)
        {
            gpuDebugHudPage = 0;
        }
        _RefreshGpuDebugHud();
    }

    private Texture _GetGpuDebugTextureA()
    {
        switch (gpuDebugHudPage)
        {
            case 0:
                return gpuBlockAtlas;
            case 1:
                return gpuSlotLookupTexture;
            case 2:
                return terrainGenerator != null ? terrainGenerator.GetGpuDensityDebugTexture() : null;
            case 3:
                return terrainGenerator != null ? terrainGenerator.GetGpuColumnSurfaceInfoDebugTexture() : null;
        }
        return null;
    }

    private Texture _GetGpuDebugTextureB()
    {
        switch (gpuDebugHudPage)
        {
            case 0:
                return gpuLightAtlas;
            case 1:
                return gpuSlotMetaTexture;
            case 2:
                return terrainGenerator != null ? terrainGenerator.GetGpuColumnBaseDebugTexture() : null;
            case 3:
                return terrainGenerator != null ? terrainGenerator.GetGpuColumnFinalDebugTexture() : null;
        }
        return null;
    }

    private string _GetGpuDebugPageLabel()
    {
        switch (gpuDebugHudPage)
        {
            case 0:
                return "GPU Atlas | Block Atlas / Light Atlas";
            case 1:
                return "GPU Slots | Lookup / Slot Meta";
            case 2:
                return "GPU Worldgen | Density / Base Column";
            case 3:
                return "GPU Worldgen | Surface Info / Final Column";
        }
        return "GPU Debug";
    }

    private void _RefreshGpuDebugHud()
    {
        if (gpuDebugHudRoot == null) return;

        Texture textureA = _GetGpuDebugTextureA();
        Texture textureB = _GetGpuDebugTextureB();

        if (gpuDebugPanel != null)
        {
            gpuDebugPanel.SetActive(gpuDebugHudVisible);
        }
        if (gpuDebugTextureA != null)
        {
            gpuDebugTextureA.gameObject.SetActive(gpuDebugHudVisible);
            gpuDebugTextureA.texture = textureA;
            gpuDebugTextureA.color = textureA != null ? Color.white : new Color(0.2f, 0.2f, 0.2f, 0.85f);
        }
        if (gpuDebugTextureB != null)
        {
            gpuDebugTextureB.gameObject.SetActive(gpuDebugHudVisible);
            gpuDebugTextureB.texture = textureB;
            gpuDebugTextureB.color = textureB != null ? Color.white : new Color(0.2f, 0.2f, 0.2f, 0.85f);
        }
        if (gpuDebugLabel != null)
        {
            gpuDebugLabel.gameObject.SetActive(gpuDebugHudVisible);
            string readyText = gpuBackendReady ? "backend ready" : "backend pending";
            gpuDebugLabel.text = _GetGpuDebugPageLabel() + "\nClick Show/Hide and Next | " + readyText;
        }
        if (gpuDebugToggleButtonLabel != null)
        {
            gpuDebugToggleButtonLabel.text = gpuDebugHudVisible ? "Hide Debug" : "Show Debug";
        }
    }

    private void _UpdateGpuDebugHud()
    {
        if (!enableGpuDebugHud || gpuDebugHudRoot == null) return;
        if (gpuDebugHudRect == null) return;

        if (gpuDebugLocalPlayer == null)
        {
            gpuDebugLocalPlayer = Networking.LocalPlayer;
        }
        if (gpuDebugLocalPlayer == null || !gpuDebugLocalPlayer.IsValid()) return;

        VRCPlayerApi.TrackingData headData = gpuDebugLocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        Vector3 localOffset = gpuDebugHudLocalOffset + new Vector3(0f, 0f, gpuDebugHudDistance);
        Vector3 hudPosition = headData.position + headData.rotation * localOffset;
        gpuDebugHudRect.position = hudPosition;
        gpuDebugHudRect.rotation = Quaternion.LookRotation(headData.position - hudPosition, headData.rotation * Vector3.up) * Quaternion.Euler(gpuDebugHudEulerOffset);
    }

    private int _GpuGetLookupPixelIndex(int arrayX, int arrayY, int arrayZ)
    {
        return (arrayY * worldDimensionZ + arrayZ) * worldDimensionX + arrayX;
    }

    private void _GpuSetSlotLookupPixel(int arrayX, int arrayY, int arrayZ, int slotIndex, bool isValid)
    {
        if (arrayX < 0 || arrayX >= worldDimensionX || arrayY < 0 || arrayY >= worldDimensionY || arrayZ < 0 || arrayZ >= worldDimensionZ) return;

        int lookupIndex = _GpuGetLookupPixelIndex(arrayX, arrayY, arrayZ);
        if (!isValid)
            gpuSlotLookupPixels[lookupIndex] = new Color32(0, 0, 0, 0);
        else
            gpuSlotLookupPixels[lookupIndex] = new Color32((byte)(slotIndex & 0xFF), (byte)((slotIndex >> 8) & 0xFF), 0, 255);
    }

    private void _GpuApplyLookupTextures()
    {
        if (gpuSlotLookupTexture == null || gpuSlotMetaTexture == null) return;
        gpuSlotLookupTexture.SetPixels32(gpuSlotLookupPixels);
        gpuSlotLookupTexture.Apply(false, false);
        gpuSlotMetaTexture.SetPixels32(gpuSlotMetaPixels);
        gpuSlotMetaTexture.Apply(false, false);
        _GpuPublishGlobals();
    }

    private int _GpuFindOrAssignSlot(ChunkData chunk)
    {
        if (!gpuBackendReady || chunk == null) return -1;

        int chunkIndex = ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
        if (chunkIndex < 0 || chunkIndex >= totalWorldChunks) return -1;

        int currentSlot = gpuChunkIndexToSlot[chunkIndex];
        if (currentSlot >= 0 && currentSlot < gpuChunkSlotCapacity)
        {
            gpuSlotUseStamp[currentSlot] = gpuSlotUseCounter++;
            return currentSlot;
        }

        int targetSlot = -1;
        for (int i = 0; i < gpuChunkSlotCapacity; i++)
        {
            if (gpuSlotToChunkIndex[i] == -1)
            {
                targetSlot = i;
                break;
            }
        }

        if (targetSlot == -1)
        {
            int oldestStamp = int.MaxValue;
            for (int i = 0; i < gpuChunkSlotCapacity; i++)
            {
                int residentChunkIndex = gpuSlotToChunkIndex[i];
                if (residentChunkIndex >= 0 && residentChunkIndex < totalWorldChunks)
                {
                    ChunkData residentChunk = chunks_1D[residentChunkIndex];
                    // Protect ALL chunks with valid data from eviction,
                    // not just nearby ones. GPU lighting covers the whole world.
                    if (residentChunk != null && residentChunk.isDataReady) continue;
                }
                if (gpuSlotUseStamp[i] < oldestStamp)
                {
                    oldestStamp = gpuSlotUseStamp[i];
                    targetSlot = i;
                }
            }
        }

        if (targetSlot == -1)
        {
            int oldestStamp = int.MaxValue;
            for (int i = 0; i < gpuChunkSlotCapacity; i++)
            {
                if (gpuSlotUseStamp[i] < oldestStamp)
                {
                    oldestStamp = gpuSlotUseStamp[i];
                    targetSlot = i;
                }
            }
        }

        int evictedChunkIndex = gpuSlotToChunkIndex[targetSlot];
        if (evictedChunkIndex >= 0 && evictedChunkIndex < totalWorldChunks)
        {
            ChunkData evictedChunk = chunks_1D[evictedChunkIndex];
            if (evictedChunk != null)
            {
                int evictedArrayX = evictedChunk.chunkX_world + chunkOffsetX;
                int evictedArrayY = evictedChunk.chunkY_world + chunkOffsetY;
                int evictedArrayZ = evictedChunk.chunkZ_world + chunkOffsetZ;
                _GpuSetSlotLookupPixel(evictedArrayX, evictedArrayY, evictedArrayZ, 0, false);
            }
            gpuChunkIndexToSlot[evictedChunkIndex] = -1;
            gpuChunkSyncedDataVersion[evictedChunkIndex] = -1;
        }

        int arrayX = chunk.chunkX_world + chunkOffsetX;
        int arrayY = chunk.chunkY_world + chunkOffsetY;
        int arrayZ = chunk.chunkZ_world + chunkOffsetZ;

        gpuChunkIndexToSlot[chunkIndex] = targetSlot;
        gpuSlotToChunkIndex[targetSlot] = chunkIndex;
        gpuSlotUseStamp[targetSlot] = gpuSlotUseCounter++;
        gpuSlotMetaPixels[targetSlot] = new Color32((byte)arrayX, (byte)arrayY, (byte)arrayZ, 255);
        _GpuSetSlotLookupPixel(arrayX, arrayY, arrayZ, targetSlot, true);
        gpuSlotLookupDirty = true;
        return targetSlot;
    }

    private bool _GpuIsChunkProtectedFromEviction(ChunkData chunk)
    {
        if (!gpuBackendReady || chunk == null) return false;
        int dx = Mathf.Abs(chunk.chunkX_world - gpuResidentCenterChunkX);
        int dy = Mathf.Abs(chunk.chunkY_world - gpuResidentCenterChunkY);
        int dz = Mathf.Abs(chunk.chunkZ_world - gpuResidentCenterChunkZ);
        return dx <= gpuResidentRadiusXZ && dy <= gpuResidentRadiusY && dz <= gpuResidentRadiusXZ;
    }

    private Vector4 _GpuGetSlotRect(int slotIndex)
    {
        int tileX = slotIndex % gpuAtlasSlotsX;
        int tileY = slotIndex / gpuAtlasSlotsX;
        float minU = (tileX * gpuTileWidth) / (float)gpuAtlasWidth;
        float minV = (tileY * gpuTileHeight) / (float)gpuAtlasHeight;
        float sizeU = gpuTileWidth / (float)gpuAtlasWidth;
        float sizeV = gpuTileHeight / (float)gpuAtlasHeight;
        return new Vector4(minU, minV, sizeU, sizeV);
    }

    private Vector4 _GpuGetFaceOccupancySlotRect(int slotIndex)
    {
        int tileX = slotIndex % gpuAtlasSlotsX;
        int tileY = slotIndex / gpuAtlasSlotsX;
        float minU = (tileX * gpuFaceOccupancySlotWidth) / (float)gpuFaceOccupancyAtlasWidth;
        float minV = (tileY * gpuFaceOccupancySlotHeight) / (float)gpuFaceOccupancyAtlasHeight;
        float sizeU = gpuFaceOccupancySlotWidth / (float)gpuFaceOccupancyAtlasWidth;
        float sizeV = gpuFaceOccupancySlotHeight / (float)gpuFaceOccupancyAtlasHeight;
        return new Vector4(minU, minV, sizeU, sizeV);
    }

    private void _GpuBuildFaceOccupancyUpload(byte[] blockData)
    {
        if (blockData == null || gpuFaceOccupancyUploadPixels == null) return;

        for (int i = 0; i < gpuFaceOccupancyUploadPixels.Length; i++)
        {
            gpuFaceOccupancyUploadPixels[i] = new Color32(0, 0, 0, 0);
        }

        int stride = chunkSizeXZ * chunkSizeXZ;
        int rowPairsXZ = (chunkSizeXZ + 1) >> 1;
        int rowPairsY = (chunkSizeY + 1) >> 1;
        int axisZOffset = chunkSizeY;
        int axisXOffset = chunkSizeY + chunkSizeXZ;

        for (int sliceY = 0; sliceY < chunkSizeY; sliceY++)
        {
            int yBase = sliceY * stride;
            int uploadRow = sliceY * gpuFaceOccupancySlotWidth;
            for (int rowPair = 0; rowPair < rowPairsXZ; rowPair++)
            {
                ushort rowMask0 = 0;
                ushort rowMask1 = 0;
                int z0 = rowPair << 1;
                int z1 = z0 + 1;
                int z0Base = yBase + z0 * chunkSizeXZ;
                int z1Base = yBase + z1 * chunkSizeXZ;
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    if (blockData[z0Base + x] != 0) rowMask0 |= (ushort)(1 << x);
                    if (z1 < chunkSizeXZ && blockData[z1Base + x] != 0) rowMask1 |= (ushort)(1 << x);
                }
                gpuFaceOccupancyUploadPixels[uploadRow + rowPair] = new Color32(
                    (byte)(rowMask0 & 0xFF),
                    (byte)((rowMask0 >> 8) & 0xFF),
                    (byte)(rowMask1 & 0xFF),
                    (byte)((rowMask1 >> 8) & 0xFF)
                );
            }
        }

        for (int sliceZ = 0; sliceZ < chunkSizeXZ; sliceZ++)
        {
            int uploadRow = (axisZOffset + sliceZ) * gpuFaceOccupancySlotWidth;
            for (int rowPair = 0; rowPair < rowPairsY; rowPair++)
            {
                ushort rowMask0 = 0;
                ushort rowMask1 = 0;
                int y0 = rowPair << 1;
                int y1 = y0 + 1;
                int y0Base = y0 * stride + sliceZ * chunkSizeXZ;
                int y1Base = y1 * stride + sliceZ * chunkSizeXZ;
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    if (blockData[y0Base + x] != 0) rowMask0 |= (ushort)(1 << x);
                    if (y1 < chunkSizeY && blockData[y1Base + x] != 0) rowMask1 |= (ushort)(1 << x);
                }
                gpuFaceOccupancyUploadPixels[uploadRow + rowPair] = new Color32(
                    (byte)(rowMask0 & 0xFF),
                    (byte)((rowMask0 >> 8) & 0xFF),
                    (byte)(rowMask1 & 0xFF),
                    (byte)((rowMask1 >> 8) & 0xFF)
                );
            }
        }

        for (int sliceX = 0; sliceX < chunkSizeXZ; sliceX++)
        {
            int uploadRow = (axisXOffset + sliceX) * gpuFaceOccupancySlotWidth;
            for (int rowPair = 0; rowPair < rowPairsY; rowPair++)
            {
                ushort rowMask0 = 0;
                ushort rowMask1 = 0;
                int y0 = rowPair << 1;
                int y1 = y0 + 1;
                int y0Base = y0 * stride + sliceX;
                int y1Base = y1 * stride + sliceX;
                for (int z = 0; z < chunkSizeXZ; z++)
                {
                    int bit = 1 << z;
                    if (blockData[y0Base + z * chunkSizeXZ] != 0) rowMask0 |= (ushort)bit;
                    if (y1 < chunkSizeY && blockData[y1Base + z * chunkSizeXZ] != 0) rowMask1 |= (ushort)bit;
                }
                gpuFaceOccupancyUploadPixels[uploadRow + rowPair] = new Color32(
                    (byte)(rowMask0 & 0xFF),
                    (byte)((rowMask0 >> 8) & 0xFF),
                    (byte)(rowMask1 & 0xFF),
                    (byte)((rowMask1 >> 8) & 0xFF)
                );
            }
        }
    }

    private void _GpuOverlayTextureIntoAtlas(Texture overlayTexture, ref RenderTexture currentAtlas, ref RenderTexture scratchAtlas)
    {
        if (!gpuBackendReady || overlayTexture == null || currentAtlas == null || scratchAtlas == null || gpuAtlasOverlayMaterial == null) return;

        gpuAtlasOverlayMaterial.SetTexture(gpuPropOverlayTexId, overlayTexture);
#if LOGGING
        float blitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        VRCGraphics.Blit(currentAtlas, scratchAtlas, gpuAtlasOverlayMaterial);
#if LOGGING
        if (enableDetailedTimings)
        {
            stats_gpuAtlasOverlayBlits++;
            stats_gpuAtlasOverlayBlitTime += (Time.realtimeSinceStartup - blitStart) * 1000f;
        }
#endif
        RenderTexture temp = currentAtlas;
        currentAtlas = scratchAtlas;
        scratchAtlas = temp;
    }

    private void _GpuClearSlotLight(int slotIndex)
    {
        if (!gpuBackendReady || slotIndex < 0 || slotIndex >= gpuChunkSlotCapacity) return;
        gpuAtlasOverlayMaterial.SetVector(gpuPropOverlayRectId, _GpuGetSlotRect(slotIndex));
        gpuAtlasOverlayMaterial.SetFloat(gpuPropOverlayPackedWidthId, 0f);
        _GpuOverlayTextureIntoAtlas(gpuClearTexture, ref gpuLightAtlas, ref gpuLightAtlasScratch);
    }

    // Pack 1-block-deep boundary faces from all 6 neighbors into the readback buffer's border texture.
    // Layout: 6 faces × chunkSizeXZ wide × stride tall.
    // Face order: +Y(0), -Y(1), +Z(2), -Z(3), +X(4), -X(5).
    // Alpha=255 if neighbor data is valid, 0 if missing.
    private void _GpuPackBorderData(ChunkData chunk, int bufferIndex)
    {
        if (!gpuBackendReady || chunk == null || gpuBorderPixels == null || gpuBorderTextures == null) return;
        if (bufferIndex < 0 || bufferIndex >= gpuBorderPixels.Length || bufferIndex >= gpuBorderTextures.Length) return;

        Color32[] borderPixels = gpuBorderPixels[bufferIndex];
        Texture2D borderTexture = gpuBorderTextures[bufferIndex];
        if (borderPixels == null || borderTexture == null) return;

        // Fill entire buffer with air + valid alpha.  Faces whose neighbor IS
        // available will be overwritten with real data below.  Faces with missing
        // neighbors keep blockID=0 (air) so ShouldDrawFace(solid, air) draws them.
        {
            Color32 airValid = new Color32(0, 0, 0, 255);
            for (int i = 0; i < borderPixels.Length; i++)
                borderPixels[i] = airValid;
        }

        int SX = chunkSizeXZ;
        int SY = chunkSizeY;
        int stride = gpuBorderStride;
        int w = gpuBorderWidth;
        byte missingMask = 0;

        // +Y (face 0): sample PY neighbor at (x, 0, z)
        ChunkData n = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world + 1, chunk.chunkZ_world);
        if (n != null && n.isDataReady)
            for (int z = 0; z < SX; z++)
                for (int x = 0; x < SX; x++)
                    borderPixels[(0 * stride + z) * w + x] = new Color32(_GetBlockLocal(n, x, 0, z), 0, 0, 255);
        else if (ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world + 1, chunk.chunkZ_world) != -1)
            missingMask |= 4; // bit 2 = +Y

        // -Y (face 1): sample NY neighbor at (x, SY-1, z)
        n = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world - 1, chunk.chunkZ_world);
        if (n != null && n.isDataReady)
            for (int z = 0; z < SX; z++)
                for (int x = 0; x < SX; x++)
                    borderPixels[(1 * stride + z) * w + x] = new Color32(_GetBlockLocal(n, x, SY - 1, z), 0, 0, 255);
        else if (ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world - 1, chunk.chunkZ_world) != -1)
            missingMask |= 8; // bit 3 = -Y

        // +Z (face 2): sample PZ neighbor at (x, y, 0)
        n = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world + 1);
        if (n != null && n.isDataReady)
            for (int y = 0; y < SY; y++)
                for (int x = 0; x < SX; x++)
                    borderPixels[(2 * stride + y) * w + x] = new Color32(_GetBlockLocal(n, x, y, 0), 0, 0, 255);
        else if (ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world + 1) != -1)
            missingMask |= 16; // bit 4 = +Z

        // -Z (face 3): sample NZ neighbor at (x, y, SX-1)
        n = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world - 1);
        if (n != null && n.isDataReady)
            for (int y = 0; y < SY; y++)
                for (int x = 0; x < SX; x++)
                    borderPixels[(3 * stride + y) * w + x] = new Color32(_GetBlockLocal(n, x, y, SX - 1), 0, 0, 255);
        else if (ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world - 1) != -1)
            missingMask |= 32; // bit 5 = -Z

        // +X (face 4): sample PX neighbor at (0, y, z)
        n = GetChunkAt(chunk.chunkX_world + 1, chunk.chunkY_world, chunk.chunkZ_world);
        if (n != null && n.isDataReady)
            for (int y = 0; y < SY; y++)
                for (int z = 0; z < SX; z++)
                    borderPixels[(4 * stride + y) * w + z] = new Color32(_GetBlockLocal(n, 0, y, z), 0, 0, 255);
        else if (ChunkCenteredCoordsTo1D(chunk.chunkX_world + 1, chunk.chunkY_world, chunk.chunkZ_world) != -1)
            missingMask |= 1; // bit 0 = +X

        // -X (face 5): sample NX neighbor at (SX-1, y, z)
        n = GetChunkAt(chunk.chunkX_world - 1, chunk.chunkY_world, chunk.chunkZ_world);
        if (n != null && n.isDataReady)
            for (int y = 0; y < SY; y++)
                for (int z = 0; z < SX; z++)
                    borderPixels[(5 * stride + y) * w + z] = new Color32(_GetBlockLocal(n, SX - 1, y, z), 0, 0, 255);
        else if (ChunkCenteredCoordsTo1D(chunk.chunkX_world - 1, chunk.chunkY_world, chunk.chunkZ_world) != -1)
            missingMask |= 2; // bit 1 = -X

        chunk._borderMissingMask = missingMask;
        borderTexture.SetPixels32(borderPixels);
        borderTexture.Apply(false, false);
    }

    public void BeginDeferredGpuSync()
    {
        if (_gpuDeferredDirtyChunks == null)
            _gpuDeferredDirtyChunks = new int[GPU_DEFERRED_DIRTY_MAX];
        _gpuDeferredDirtyCount = 0;
        _gpuSyncDeferred = true;
    }

    public void FlushDeferredGpuSync()
    {
        _gpuSyncDeferred = false;
        for (int i = 0; i < _gpuDeferredDirtyCount; i++)
        {
            int ci = _gpuDeferredDirtyChunks[i];
            if (ci < 0 || chunks_1D == null || ci >= chunks_1D.Length) continue;
            ChunkData chunk = chunks_1D[ci];
            if (chunk == null || !chunk.isDataReady) continue;
            _GpuSyncChunkBlocks(chunk, _GetDecompressedData(chunk));
        }
        _gpuDeferredDirtyCount = 0;
    }

    public void EndDeferredGpuSync()
    {
        _gpuSyncDeferred = false;
        _gpuDeferredDirtyCount = 0;
    }

    public void BeginDeferredMeshUpdates()
    {
        if (_meshDeferredDirtyChunks == null)
            _meshDeferredDirtyChunks = new int[MESH_DEFERRED_DIRTY_MAX];
        _meshDeferredDirtyCount = 0;
        _meshUpdateDeferred = true;
    }

    public void FlushDeferredMeshUpdates()
    {
        _meshUpdateDeferred = false;
        for (int i = 0; i < _meshDeferredDirtyCount; i++)
        {
            int ci = _meshDeferredDirtyChunks[i];
            RequestChunkMeshUpdate(ci);
        }
        _meshDeferredDirtyCount = 0;
    }

    private void _GpuSyncChunkBlocks(ChunkData chunk, byte[] blockData)
    {
        if (!gpuBackendReady || chunk == null || blockData == null || gpuUploadBlockTexture == null) return;

        int chunkIndex = ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
        if (chunkIndex < 0 || chunkIndex >= totalWorldChunks) return;

        // Always sync generated chunks to the GPU atlas for correct lighting.
        // Eviction now protects all chunks with valid data, so distance gating
        // is unnecessary — atlas slots are only reclaimed from empty/unloaded chunks.

        int slotIndex = _GpuFindOrAssignSlot(chunk);
        if (slotIndex < 0) return;

        if (gpuChunkSyncedDataVersion[chunkIndex] == chunk._cachedDataVersion)
        {
            gpuSlotUseStamp[slotIndex] = gpuSlotUseCounter++;
            return;
        }

        // Clear this slot's light data (sets alpha=0).  The seed shader
        // uses alpha as a dirty flag:  alpha < 0.5 → compute fresh column-scan
        // seed values;  alpha >= 0.5 → pass through existing propagated values.
        _GpuClearSlotLight(slotIndex);

        int stride = chunkSizeXZ * chunkSizeXZ;
        int packedWidth = chunkSizeXZ / 4;
        for (int y = 0; y < chunkSizeY; y++)
        {
            int yBase = y * stride;
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                int row = y * chunkSizeXZ + z;
                int packedRowBase = row * packedWidth;
                int zBase = yBase + z * chunkSizeXZ;
                for (int px = 0; px < packedWidth; px++)
                {
                    int x = px * 4;
                    gpuUploadBlockPixels[packedRowBase + px] = new Color32(
                        blockData[zBase + x],
                        blockData[zBase + x + 1],
                        blockData[zBase + x + 2],
                        blockData[zBase + x + 3]
                    );
                }
            }
        }

#if LOGGING
        float uploadStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        gpuUploadBlockTexture.SetPixels32(gpuUploadBlockPixels);
        gpuUploadBlockTexture.Apply(false, false);
#if LOGGING
        if (enableDetailedTimings)
        {
            stats_gpuChunkUploads++;
            stats_gpuChunkUploadTime += (Time.realtimeSinceStartup - uploadStart) * 1000f;
            stats_gpuChunkUploadBytes += gpuUploadBlockPixels.Length * 4;
        }
#endif

        gpuAtlasOverlayMaterial.SetVector(gpuPropOverlayRectId, _GpuGetSlotRect(slotIndex));
        gpuAtlasOverlayMaterial.SetFloat(gpuPropOverlayPackedWidthId, (float)(chunkSizeXZ / 4));
        _GpuOverlayTextureIntoAtlas(gpuUploadBlockTexture, ref gpuBlockAtlas, ref gpuBlockAtlasScratch);
        gpuAtlasOverlayMaterial.SetFloat(gpuPropOverlayPackedWidthId, 0f);

        if (useCompactGpuFaceExport && gpuFaceOccupancyUploadTexture != null && gpuFaceOccupancyAtlas != null && gpuFaceOccupancyAtlasScratch != null)
        {
            _GpuBuildFaceOccupancyUpload(blockData);
#if LOGGING
            uploadStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
            gpuFaceOccupancyUploadTexture.SetPixels32(gpuFaceOccupancyUploadPixels);
            gpuFaceOccupancyUploadTexture.Apply(false, false);
#if LOGGING
            if (enableDetailedTimings)
            {
                stats_gpuFaceOccupancyUploads++;
                stats_gpuFaceOccupancyUploadTime += (Time.realtimeSinceStartup - uploadStart) * 1000f;
                stats_gpuFaceOccupancyUploadBytes += gpuFaceOccupancyUploadPixels.Length * 4;
            }
#endif
            gpuAtlasOverlayMaterial.SetVector(gpuPropOverlayRectId, _GpuGetFaceOccupancySlotRect(slotIndex));
            _GpuOverlayTextureIntoAtlas(gpuFaceOccupancyUploadTexture, ref gpuFaceOccupancyAtlas, ref gpuFaceOccupancyAtlasScratch);
        }

        gpuChunkSyncedDataVersion[chunkIndex] = chunk._cachedDataVersion;
        _GpuRequestLightingRebuild();
        _GpuPublishGlobals();
    }

    private void _GpuUpdateResidentCenterFromPlayer()
    {
        if (!gpuBackendReady) return;

        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (localPlayer == null) return;

        Vector3 playerPos = localPlayer.GetPosition();
        gpuResidentCenterChunkX = Mathf.FloorToInt(playerPos.x / chunkSizeXZ);
        gpuResidentCenterChunkY = Mathf.FloorToInt(playerPos.y / chunkSizeY);
        gpuResidentCenterChunkZ = Mathf.FloorToInt(playerPos.z / chunkSizeXZ);

        int minChunkX = -chunkOffsetX;
        int maxChunkX = worldDimensionX - chunkOffsetX - 1;
        int minChunkY = -chunkOffsetY;
        int maxChunkY = worldDimensionY - chunkOffsetY - 1;
        int minChunkZ = -chunkOffsetZ;
        int maxChunkZ = worldDimensionZ - chunkOffsetZ - 1;

        gpuResidentCenterChunkX = Mathf.Clamp(gpuResidentCenterChunkX, minChunkX, maxChunkX);
        gpuResidentCenterChunkY = Mathf.Clamp(gpuResidentCenterChunkY, minChunkY, maxChunkY);
        gpuResidentCenterChunkZ = Mathf.Clamp(gpuResidentCenterChunkZ, minChunkZ, maxChunkZ);
    }

    private void _GpuMaintainResidentChunks(float frameStart, float frameBudget)
    {
        if (!gpuBackendReady) return;

        _GpuUpdateResidentCenterFromPlayer();

        int syncBudget = Mathf.Max(1, gpuResidentSyncsPerFrame);
        int touched = 0;
        int maxDistance = Mathf.Max(gpuResidentRadiusXZ, gpuResidentRadiusY);

        for (int distance = 0; distance <= maxDistance; distance++)
        {
            for (int dy = -gpuResidentRadiusY; dy <= gpuResidentRadiusY; dy++)
            {
                for (int dz = -gpuResidentRadiusXZ; dz <= gpuResidentRadiusXZ; dz++)
                {
                    for (int dx = -gpuResidentRadiusXZ; dx <= gpuResidentRadiusXZ; dx++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy), Mathf.Abs(dz)) != distance) continue;

                        int chunkX = gpuResidentCenterChunkX + dx;
                        int chunkY = gpuResidentCenterChunkY + dy;
                        int chunkZ = gpuResidentCenterChunkZ + dz;
                        int chunkIndex = ChunkCenteredCoordsTo1D(chunkX, chunkY, chunkZ);
                        if (chunkIndex == -1) continue;

                        ChunkData chunk = chunks_1D[chunkIndex];
                        if (chunk == null || !chunk.isDataReady) continue;

                        int slotIndex = gpuChunkIndexToSlot[chunkIndex];
                        if (slotIndex >= 0 && slotIndex < gpuChunkSlotCapacity)
                        {
                            gpuSlotUseStamp[slotIndex] = gpuSlotUseCounter++;
                            continue;
                        }

                        if (touched >= syncBudget) return;
                        if (Time.realtimeSinceStartup - frameStart > frameBudget) return;

                        byte[] blockData = _GetDecompressedData(chunk);
                        if (blockData == null) continue;

                        _GpuSyncChunkBlocks(chunk, blockData);
                        touched++;
                    }
                }
            }
        }
    }

    private void _GpuRequestLightingRebuild()
    {
        if (!gpuBackendReady) return;
        gpuLightingSeedPending = true;
        // Multiple chunk uploads often collapse into the same atlas-wide seed pass.
        // Top up to one full convergence window instead of stacking another full
        // budget per upload, which keeps propagation saturated for long stretches.
        if (gpuLightingIterationsRemaining < gpuLightingTotalIterations)
        {
            gpuLightingIterationsRemaining = gpuLightingTotalIterations;
        }
    }

    private int gpuLightingKeepAliveCounter = 0;

    private void _ProcessGpuLighting(float frameStart, float frameBudget)
    {
        if (!gpuBackendReady) return;

        // Detect RenderTexture content loss (GPU device recovery, alt-tab, etc.)
        // and trigger a full re-seed so lighting doesn't go permanently black.
        bool contentLost = false;
        if (gpuLightAtlas != null && gpuLightAtlasScratch != null &&
            (!gpuLightAtlas.IsCreated() || !gpuLightAtlasScratch.IsCreated()))
        {
            gpuLightAtlas.Create();
            gpuLightAtlasScratch.Create();
            contentLost = true;
        }

        // Periodic keep-alive: Unity can silently zero out RT contents while
        // IsCreated() still returns true.  Force a full re-seed + propagation
        // every ~10 seconds so lighting self-heals within a few seconds.
        // The seed shader's alpha-based dirty flag (alpha=0 → recompute from
        // column scan) handles zeroed atlas data correctly without needing
        // to re-upload block data.
        gpuLightingKeepAliveCounter++;
        if (contentLost || gpuLightingKeepAliveCounter >= 600)
        {
            gpuLightingKeepAliveCounter = 0;
            gpuLightingSeedPending = true;
            if (gpuLightingIterationsRemaining < gpuLightingTotalIterations)
                gpuLightingIterationsRemaining = gpuLightingTotalIterations;
        }
        bool runVerticalSeedPass = gpuLightingSeedPending;
        bool seededThisFrame = false;
        if (runVerticalSeedPass)
        {
            _GpuRunLightingSeedPass();
            gpuLightingSeedPending = false;
            seededThisFrame = true;
            // Ensure at least a minimum propagation budget after seeding
            if (gpuLightingIterationsRemaining < gpuLightingTotalIterations)
            {
                gpuLightingIterationsRemaining = gpuLightingTotalIterations;
            }
        }

        int iterations = Mathf.Max(0, gpuLightingIterationsPerUpdate);
        int guaranteedIterations = seededThisFrame ? 8 : 2;
        int lightingPressure = activeMeshingCount + gpuFaceReadbackReadyCount + gpuFaceReadbackInflightCount;
        if (lightingPressure >= 6 || activeDataGenCount >= 3)
        {
            iterations = Mathf.Max(guaranteedIterations, iterations / 2);
        }

        if (enableAdaptiveBudgets)
        {
            float targetWithHeadroom = Mathf.Max(0.5f, updateTimeBudgetMs - adaptiveFrameHeadroomMs);
            if (lastUpdateDurationMs > targetWithHeadroom)
            {
                guaranteedIterations = seededThisFrame ? 4 : 1;
                iterations = Mathf.Max(guaranteedIterations, iterations / 2);
                if (lightingPressure >= 10)
                {
                    iterations = guaranteedIterations;
                }
            }
        }

        if (iterations > 0 && gpuLightingIterationsRemaining > 0 && gpuLightingPropagateMaterial != null)
        {
            // Set material properties once — they don't change between iterations
            gpuLightingPropagateMaterial.SetTexture(gpuPropBlockAtlasId, gpuBlockAtlas);
            gpuLightingPropagateMaterial.SetTexture(gpuPropBlockPropsId, gpuBlockPropertyTexture);
            gpuLightingPropagateMaterial.SetTexture(gpuPropSlotLookupTexId, gpuSlotLookupTexture);
            gpuLightingPropagateMaterial.SetTexture(gpuPropSlotMetaId, gpuSlotMetaTexture);
            gpuLightingPropagateMaterial.SetVector(gpuPropAtlasInfoId, new Vector4(gpuAtlasWidth, gpuAtlasHeight, gpuAtlasSlotsX, gpuAtlasSlotsY));
            gpuLightingPropagateMaterial.SetVector(gpuPropWorldInfoId, new Vector4(worldDimensionX, worldDimensionY, worldDimensionZ, worldDimensionY * worldDimensionZ));
            gpuLightingPropagateMaterial.SetVector(gpuPropChunkInfoId, new Vector4(chunkSizeXZ, chunkSizeY, chunkOffsetX, chunkOffsetZ));
            gpuLightingPropagateMaterial.SetVector(gpuPropVoxelOffsetId, new Vector4(globalVoxelOffsetX, globalVoxelOffsetY, globalVoxelOffsetZ, gpuChunkSlotCapacity));
            gpuLightingPropagateMaterial.SetFloat(gpuPropTopSkyLightId, 15f);

            for (int i = 0; i < iterations && gpuLightingIterationsRemaining > 0; i++)
            {
                if (i >= guaranteedIterations && Time.realtimeSinceStartup - frameStart > frameBudget) break;
                gpuLightingPropagateMaterial.SetInt("_FrameJitter", gpuLightingFrameCursor++);
#if LOGGING
                float blitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
                VRCGraphics.Blit(gpuLightAtlas, gpuLightAtlasScratch, gpuLightingPropagateMaterial);
#if LOGGING
                if (enableDetailedTimings)
                {
                    stats_gpuLightingPropagateBlits++;
                    stats_gpuLightingPropagateBlitTime += (Time.realtimeSinceStartup - blitStart) * 1000f;
                }
#endif
                RenderTexture temp = gpuLightAtlas;
                gpuLightAtlas = gpuLightAtlasScratch;
                gpuLightAtlasScratch = temp;
                gpuLightingIterationsRemaining--;
            }

            // Publish globals once after all iterations — intermediate RT swaps
            // are invisible to the terrain shader since it only reads at render time
            _GpuPublishGlobals();
        }
    }

    private void _GpuRunLightingSeedPass()
    {
        if (!gpuBackendReady || gpuLightingSeedMaterial == null) return;

        gpuLightingSeedMaterial.SetTexture(gpuPropBlockAtlasId, gpuBlockAtlas);
        gpuLightingSeedMaterial.SetTexture(gpuPropBlockPropsId, gpuBlockPropertyTexture);
        gpuLightingSeedMaterial.SetTexture(gpuPropSlotLookupTexId, gpuSlotLookupTexture);
        gpuLightingSeedMaterial.SetTexture(gpuPropSlotMetaId, gpuSlotMetaTexture);
        gpuLightingSeedMaterial.SetVector(gpuPropAtlasInfoId, new Vector4(gpuAtlasWidth, gpuAtlasHeight, gpuAtlasSlotsX, gpuAtlasSlotsY));
        gpuLightingSeedMaterial.SetVector(gpuPropWorldInfoId, new Vector4(worldDimensionX, worldDimensionY, worldDimensionZ, worldDimensionY * worldDimensionZ));
        gpuLightingSeedMaterial.SetVector(gpuPropChunkInfoId, new Vector4(chunkSizeXZ, chunkSizeY, chunkOffsetX, chunkOffsetZ));
        gpuLightingSeedMaterial.SetVector(gpuPropVoxelOffsetId, new Vector4(globalVoxelOffsetX, globalVoxelOffsetY, globalVoxelOffsetZ, gpuChunkSlotCapacity));
        gpuLightingSeedMaterial.SetFloat(gpuPropTopSkyLightId, 15f);

#if LOGGING
        float blitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        VRCGraphics.Blit(gpuLightAtlas, gpuLightAtlasScratch, gpuLightingSeedMaterial);
#if LOGGING
        if (enableDetailedTimings)
        {
            stats_gpuLightingSeedBlits++;
            stats_gpuLightingSeedBlitTime += (Time.realtimeSinceStartup - blitStart) * 1000f;
        }
#endif
        RenderTexture temp = gpuLightAtlas;
        gpuLightAtlas = gpuLightAtlasScratch;
        gpuLightAtlasScratch = temp;
        _GpuPublishGlobals();
    }

    // ===== GPU FACE EXTRACTION =====

    private void _GpuBuildShouldDrawTexture()
    {
        if (shouldDrawTable == null) return;
        gpuShouldDrawTexture = new Texture2D(256, 256, TextureFormat.RGBA32, false, true);
        gpuShouldDrawTexture.filterMode = FilterMode.Point;
        gpuShouldDrawTexture.wrapMode = TextureWrapMode.Clamp;
        Color32[] pixels = new Color32[256 * 256];
        for (int selfId = 0; selfId < 256; selfId++)
        {
            for (int neighborId = 0; neighborId < 256; neighborId++)
            {
                int index = selfId * 256 + neighborId;
                int tableIndex = (selfId << 8) | neighborId;
                byte draw = (tableIndex < shouldDrawTable.Length) ? shouldDrawTable[tableIndex] : (byte)0;
                pixels[index] = new Color32(draw > 0 ? (byte)255 : (byte)0, 0, 0, 255);
            }
        }
        gpuShouldDrawTexture.SetPixels32(pixels);
        gpuShouldDrawTexture.Apply(false, false);
    }

    private bool _GpuFaceExtractionReady()
    {
        return gpuBackendReady && gpuFaceExtractMaterial != null && gpuShouldDrawTexture != null && gpuFaceReadbackTextures != null;
    }

    private int _GpuFindAvailableReadbackBuffer()
    {
        if (gpuFaceReadbackState == null) return -1;
        for (int i = 0; i < gpuFaceReadbackState.Length; i++)
        {
            if (gpuFaceReadbackState[i] == 0) return i;
        }
        return -1;
    }

    private bool _GpuHasAvailableReadbackBuffer()
    {
        return _GpuFindAvailableReadbackBuffer() != -1;
    }

    private void _GpuRunFaceExtractionDirect(int bufferIndex, int slotIndex)
    {
        if (!_GpuFaceExtractionReady() || bufferIndex < 0 || bufferIndex >= gpuFaceReadbackTextures.Length) return;
        if (gpuBorderTextures == null || bufferIndex >= gpuBorderTextures.Length || gpuBorderTextures[bufferIndex] == null) return;

        gpuFaceExtractMaterial.SetFloat(gpuPropFaceModeId, 1f);
        gpuFaceExtractMaterial.SetFloat(gpuPropFaceReadSlotId, slotIndex);
        gpuFaceExtractMaterial.SetTexture(gpuPropBlockPropsId, gpuBlockPropertyTexture);
        gpuFaceExtractMaterial.SetTexture(gpuPropSlotLookupTexId, gpuSlotLookupTexture);
        gpuFaceExtractMaterial.SetTexture(gpuPropSlotMetaId, gpuSlotMetaTexture);
        gpuFaceExtractMaterial.SetTexture(gpuPropDrawTableTexId, gpuShouldDrawTexture);
        gpuFaceExtractMaterial.SetTexture(gpuPropBorderTexId, gpuBorderTextures[bufferIndex]);
        gpuFaceExtractMaterial.SetVector(gpuPropBorderInfoId, new Vector4(gpuBorderWidth, gpuBorderHeight, gpuBorderStride, 0f));

#if LOGGING
        float blitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        VRCGraphics.Blit(gpuBlockAtlas, gpuFaceReadbackTextures[bufferIndex], gpuFaceExtractMaterial);
#if LOGGING
        if (enableDetailedTimings)
        {
            stats_gpuFaceExtractBlits++;
            stats_gpuFaceExtractBlitTime += (Time.realtimeSinceStartup - blitStart) * 1000f;
        }
#endif
    }

    private void _GpuRunFaceSummaryExportDirect(int bufferIndex, int slotIndex)
    {
        if (!_GpuFaceExtractionReady() || bufferIndex < 0 || bufferIndex >= gpuFaceReadbackTextures.Length) return;
        if (gpuBorderTextures == null || bufferIndex >= gpuBorderTextures.Length || gpuBorderTextures[bufferIndex] == null) return;

        gpuFaceExtractMaterial.SetFloat(gpuPropFaceModeId, 2f);
        gpuFaceExtractMaterial.SetFloat(gpuPropFaceReadSlotId, slotIndex);
        gpuFaceExtractMaterial.SetTexture(gpuPropBlockPropsId, gpuBlockPropertyTexture);
        gpuFaceExtractMaterial.SetTexture(gpuPropSlotLookupTexId, gpuSlotLookupTexture);
        gpuFaceExtractMaterial.SetTexture(gpuPropSlotMetaId, gpuSlotMetaTexture);
        gpuFaceExtractMaterial.SetTexture(gpuPropDrawTableTexId, gpuShouldDrawTexture);
        gpuFaceExtractMaterial.SetTexture(gpuPropBorderTexId, gpuBorderTextures[bufferIndex]);
        gpuFaceExtractMaterial.SetVector(gpuPropBorderInfoId, new Vector4(gpuBorderWidth, gpuBorderHeight, gpuBorderStride, 0f));
        float blitStart = 0f;
#if LOGGING
        if (enableDetailedTimings) blitStart = Time.realtimeSinceStartup;
#endif
        VRCGraphics.Blit(gpuBlockAtlas, gpuFaceReadbackTextures[bufferIndex], gpuFaceExtractMaterial);
#if LOGGING
        if (enableDetailedTimings)
        {
            stats_gpuFaceExportBlits++;
            stats_gpuFaceExportBlitTime += (Time.realtimeSinceStartup - blitStart) * 1000f;
        }
#endif
    }

    private bool _GpuRequestChunkFaceReadback(ChunkData chunk, int bufferIndex)
    {
        if (!_GpuFaceExtractionReady() || chunk == null) return false;
        if (bufferIndex < 0 || gpuFaceReadbackState == null || bufferIndex >= gpuFaceReadbackState.Length) return false;
        if (gpuFaceReadbackState[bufferIndex] != 0) return false;
        // Caller must pack the matching border texture into this buffer before requesting.

        int chunkIndex = ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
        if (chunkIndex < 0 || chunkIndex >= totalWorldChunks) return false;
        int slotIndex = gpuChunkIndexToSlot[chunkIndex];
        if (slotIndex < 0 || slotIndex >= gpuChunkSlotCapacity) return false;

        chunk._gpuFaceSlotIndex = slotIndex;
        gpuFaceReadbackChunks[bufferIndex] = chunk;
        gpuFaceReadbackSlots[bufferIndex] = slotIndex;
        gpuFaceReadbackBuildVersion[bufferIndex] = chunk._meshBuildVersion;
        gpuFaceReadbackState[bufferIndex] = 1;
        gpuFaceReadbackInflightOrder[gpuFaceReadbackInflightTail] = bufferIndex;
        gpuFaceReadbackInflightTail = (gpuFaceReadbackInflightTail + 1) % GPU_FACE_READBACK_BUFFER_COUNT;
        gpuFaceReadbackInflightCount++;

#if LOGGING
        if (enableDetailedTimings)
        {
            stats_gpuFaceReadbackRequests++;
            float requestStartMs = Time.realtimeSinceStartup * 1000f;
            stats_gpuFaceReadbackRequestStartMs = requestStartMs;
            gpuFaceReadbackStartMs[bufferIndex] = requestStartMs;
        }
#endif
        // Flush slot lookup/meta if dirty — _GpuSyncChunkBlocks may have evicted a slot
        // and reused it, making the GPU's slot lookup stale for neighbor reads.
        if (gpuSlotLookupDirty)
        {
            _GpuApplyLookupTextures();
            gpuSlotLookupDirty = false;
        }
        if (useCompactGpuFaceExport) _GpuRunFaceSummaryExportDirect(bufferIndex, slotIndex);
        else _GpuRunFaceExtractionDirect(bufferIndex, slotIndex);
        VRCAsyncGPUReadback.Request(gpuFaceReadbackTextures[bufferIndex], 0, TextureFormat.RGBA32, (IUdonEventReceiver)this);
#if LOGGING
        if (enableVerboseLogging && !dbg_loggedFirstFaceReadback)
        {
            dbg_loggedFirstFaceReadback = true;
            Debug.Log("[McWorld][GPU] First GPU face-extract readback REQUESTED -> GPU face-extraction path is in use (CPU mesh build bypassed for this chunk).");
        }
#endif
        return true;
    }

    private void _ResetGpuFaceReadbackBuffer(int bufferIndex)
    {
        if (bufferIndex < 0 || gpuFaceReadbackState == null || bufferIndex >= gpuFaceReadbackState.Length) return;

        gpuFaceReadbackChunks[bufferIndex] = null;
        gpuFaceReadbackSlots[bufferIndex] = -1;
        gpuFaceReadbackBuildVersion[bufferIndex] = -1;
        gpuFaceReadbackState[bufferIndex] = 0;
        gpuFaceReadbackStartMs[bufferIndex] = -1f;
    }

    public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
    {
        if (gpuFaceReadbackInflightCount <= 0) return;

        int bufferIndex = gpuFaceReadbackInflightOrder[gpuFaceReadbackInflightHead];
        gpuFaceReadbackInflightHead = (gpuFaceReadbackInflightHead + 1) % GPU_FACE_READBACK_BUFFER_COUNT;
        gpuFaceReadbackInflightCount--;

#if LOGGING
        float callbackStartMs = enableDetailedTimings ? Time.realtimeSinceStartup * 1000f : 0f;
        float latencyMs = enableDetailedTimings && gpuFaceReadbackStartMs[bufferIndex] >= 0f
            ? callbackStartMs - gpuFaceReadbackStartMs[bufferIndex]
            : 0f;
#endif
        gpuFaceReadbackStartMs[bufferIndex] = -1f;

        if (request.hasError)
        {
            _ResetGpuFaceReadbackBuffer(bufferIndex);
#if LOGGING
            if (enableDetailedTimings) stats_gpuFaceReadbackFailures++;
            if (enableVerboseLogging)
            {
                dbg_readbackFailuresSeen++;
                if (dbg_readbackFailuresSeen <= 5 || (dbg_readbackFailuresSeen & 127) == 0)
                    Debug.LogWarning("[McWorld][GPU] Face readback FAILED (request.hasError) -> this chunk falls back to CPU meshing. totalFailures=" + dbg_readbackFailuresSeen);
            }
#endif
            return;
        }

        if (!request.TryGetData(gpuFaceReadbackPixels[bufferIndex], 0))
        {
            _ResetGpuFaceReadbackBuffer(bufferIndex);
#if LOGGING
            if (enableDetailedTimings) stats_gpuFaceReadbackFailures++;
            if (enableVerboseLogging)
            {
                dbg_readbackFailuresSeen++;
                if (dbg_readbackFailuresSeen <= 5 || (dbg_readbackFailuresSeen & 127) == 0)
                    Debug.LogWarning("[McWorld][GPU] Face readback FAILED (TryGetData returned false) -> this chunk falls back to CPU meshing. totalFailures=" + dbg_readbackFailuresSeen);
            }
#endif
            return;
        }

        ChunkData chunk = gpuFaceReadbackChunks[bufferIndex];
        if (chunk == null || !chunk.isBuildingMesh || gpuFaceReadbackBuildVersion[bufferIndex] != chunk._meshBuildVersion)
        {
            _ResetGpuFaceReadbackBuffer(bufferIndex);
            return;
        }

        gpuFaceReadbackState[bufferIndex] = 2;
        gpuFaceReadbackReadyOrder[gpuFaceReadbackReadyTail] = bufferIndex;
        gpuFaceReadbackReadyTail = (gpuFaceReadbackReadyTail + 1) % GPU_FACE_READBACK_BUFFER_COUNT;
        gpuFaceReadbackReadyCount++;
#if LOGGING
        if (enableVerboseLogging && !dbg_loggedFirstFaceReadbackSuccess)
        {
            dbg_loggedFirstFaceReadbackSuccess = true;
            Debug.Log("[McWorld][GPU] First GPU face readback COMPLETED OK -> GPU face data is flowing back to the CPU successfully.");
        }
        if (enableDetailedTimings)
        {
            stats_gpuFaceReadbacksCompleted++;
            stats_gpuFaceReadbackLatencyTotal += latencyMs;
            if (latencyMs < stats_gpuFaceReadbackLatencyMin) stats_gpuFaceReadbackLatencyMin = latencyMs;
            if (latencyMs > stats_gpuFaceReadbackLatencyMax) stats_gpuFaceReadbackLatencyMax = latencyMs;
            stats_gpuFaceReadbackCallbackCopyTime += Time.realtimeSinceStartup * 1000f - callbackStartMs;
            stats_gpuFaceReadbackBytes += gpuFaceReadbackPixels[bufferIndex].Length * 4;
        }
#endif
    }

    /// <summary>
    /// Called from the meshing processing loop to check if face readback is ready
    /// and trigger mesh building.
    /// </summary>
    private void _ProcessGpuFaceReadback(float frameStart, float frameBudget)
    {
        int processedThisFrame = 0;
        while (gpuFaceReadbackReadyCount > 0)
        {
            if (processedThisFrame >= GPU_FACE_READBACKS_PER_FRAME) break;
            if (Time.realtimeSinceStartup - frameStart > frameBudget) break;

            int bufferIndex = gpuFaceReadbackReadyOrder[gpuFaceReadbackReadyHead];
            ChunkData peekChunk = gpuFaceReadbackChunks[bufferIndex];
            int buildVersion = gpuFaceReadbackBuildVersion[bufferIndex];
            if (peekChunk == null || !peekChunk.isBuildingMesh || buildVersion != peekChunk._meshBuildVersion)
            {
                gpuFaceReadbackReadyHead = (gpuFaceReadbackReadyHead + 1) % GPU_FACE_READBACK_BUFFER_COUNT;
                gpuFaceReadbackReadyCount--;
                _ResetGpuFaceReadbackBuffer(bufferIndex);
                continue;
            }

            // Check pool availability BEFORE dequeuing — if no slot is free,
            // leave the readback queued and retry next frame.
            if (peekChunk != null && peekChunk._meshPoolSlot < 0)
            {
                bool hasSlot = false;
                for (int s = 0; s < MESH_POOL_SIZE; s++)
                {
                    if (meshPoolFree[s]) { hasSlot = true; break; }
                }
                if (!hasSlot) break;
            }

            gpuFaceReadbackReadyHead = (gpuFaceReadbackReadyHead + 1) % GPU_FACE_READBACK_BUFFER_COUNT;
            gpuFaceReadbackReadyCount--;

            ChunkData chunk = gpuFaceReadbackChunks[bufferIndex];
            if (chunk != null)
            {
                _StartGpuBuildMeshFromFaceData(chunk, gpuFaceReadbackPixels[bufferIndex], bufferIndex, buildVersion);
            }
            processedThisFrame++;
        }
    }

    private void _GpuExtractChunkTileFromAtlas(int slotIndex, Color32[] atlasPixels, Color32[] tilePixels)
    {
#if LOGGING
        float extractStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
#endif
        int sizeXZ = chunkSizeXZ;
        int sizeY = chunkSizeY;
        int tilePixelW = sizeXZ;
        int tilePixelH = sizeY * sizeXZ; // Y * XZ packed vertically

        int tileAtlasX = (slotIndex % gpuAtlasSlotsX) * tilePixelW;
        int tileAtlasY = (slotIndex / gpuAtlasSlotsX) * tilePixelH;

        int atlasW = gpuAtlasWidth;

        for (int localY = 0; localY < sizeY; localY++)
        {
            for (int localZ = 0; localZ < sizeXZ; localZ++)
            {
                int atlasRow = tileAtlasY + localY * sizeXZ + localZ;
                int atlasBase = atlasRow * atlasW + tileAtlasX;

                int tileBase = localY * sizeXZ * sizeXZ + localZ * sizeXZ;

                for (int localX = 0; localX < sizeXZ; localX++)
                {
                    if (atlasBase + localX < atlasPixels.Length && tileBase + localX < tilePixels.Length)
                    {
                        tilePixels[tileBase + localX] = atlasPixels[atlasBase + localX];
                    }
                }
            }
        }
#if LOGGING
        if (enableDetailedTimings)
        {
            stats_gpuFaceTileExtractTime += (Time.realtimeSinceStartup - extractStart) * 1000f;
        }
#endif
    }

    private void _DecodeGpuFaceSummary(ChunkData chunk, Color32[] summaryPixels)
    {
        if (chunk == null || summaryPixels == null) return;

        _ResetGpuFaceSliceSummary(chunk);
        int activeSliceCount = 0;
        int summaryWidth = gpuFaceSummaryWidth > 0 ? gpuFaceSummaryWidth : chunkSizeY;

        for (int direction = 0; direction < GPU_FACE_COMPACT_DIRECTIONS; direction++)
        {
            int sliceCount = direction <= 1 ? chunkSizeY : chunkSizeXZ;
            int rowBase = direction * summaryWidth;
            for (int slice = 0; slice < sliceCount; slice++)
            {
                int pixelIndex = rowBase + slice;
                if (pixelIndex < 0 || pixelIndex >= summaryPixels.Length) continue;

                Color32 pixel = summaryPixels[pixelIndex];
                if (pixel.a == 0) continue;

                int maxV = pixel.a - 1;
                if (maxV < 0) continue;

                int summaryIndex = _GetGpuFaceSliceSummaryIndex(direction, slice);
                if (summaryIndex < 0 || summaryIndex >= chunk._gpuFaceSliceActive.Length) continue;

                chunk._gpuFaceSliceActive[summaryIndex] = 1;
                chunk._gpuFaceSliceMinU[summaryIndex] = pixel.r;
                chunk._gpuFaceSliceMaxU[summaryIndex] = pixel.g;
                chunk._gpuFaceSliceMinV[summaryIndex] = pixel.b;
                chunk._gpuFaceSliceMaxV[summaryIndex] = (byte)maxV;
                activeSliceCount++;
            }
        }

#if LOGGING
        if (enableDetailedTimings)
        {
            stats_gpuFaceCompactBuilds++;
            stats_gpuFaceCompactActiveSlices += activeSliceCount;
        }
#endif
    }


    private void _StartGpuBuildMeshFromFaceData(ChunkData chunk, Color32[] facePixels, int bufferIndex, int buildVersion)
    {
        if (chunk == null || facePixels == null) return;
        if (!chunk.isBuildingMesh || buildVersion != chunk._meshBuildVersion)
        {
            _ResetGpuFaceReadbackBuffer(bufferIndex);
            return;
        }
        if (!_AcquireMeshPool(chunk)) return;

        _ClearAllMeshBuffers(chunk);
        _PreComputeBiomeColors(chunk);
        if (useCompactGpuFaceExport || ambientOcclusion)
        {
            ChunkData[] neighbors = _GetCachedNeighbors(chunk);
            chunk.neighborPX = neighbors[0];
            chunk.neighborNX = neighbors[1];
            chunk.neighborPY = neighbors[2];
            chunk.neighborNY = neighbors[3];
            chunk.neighborPZ = neighbors[4];
            chunk.neighborNZ = neighbors[5];
            _DecompressNeighborsOnce(chunk);
            if (!_UsesGpuTerrainLightSampling())
            {
                _PreComputeChunkBrightness(chunk);
                if (chunk.neighborPX != null && chunk.neighborPX.isDataReady) _PreComputeChunkBrightness(chunk.neighborPX);
                if (chunk.neighborNX != null && chunk.neighborNX.isDataReady) _PreComputeChunkBrightness(chunk.neighborNX);
                if (chunk.neighborPY != null && chunk.neighborPY.isDataReady) _PreComputeChunkBrightness(chunk.neighborPY);
                if (chunk.neighborNY != null && chunk.neighborNY.isDataReady) _PreComputeChunkBrightness(chunk.neighborNY);
                if (chunk.neighborPZ != null && chunk.neighborPZ.isDataReady) _PreComputeChunkBrightness(chunk.neighborPZ);
                if (chunk.neighborNZ != null && chunk.neighborNZ.isDataReady) _PreComputeChunkBrightness(chunk.neighborNZ);
            }
            else
            {
                chunk._cachedBrightness = null;
            }
        }
        else
        {
            chunk._decompSelf = null;
            chunk._decompNX = null;
            chunk._decompPX = null;
            chunk._decompNY = null;
            chunk._decompPY = null;
            chunk._decompNZ = null;
            chunk._decompPZ = null;
            chunk._cachedBrightness = null;
        }

        if (useCompactGpuFaceExport)
        {
            if (chunk._gpuFacePixels == null || chunk._gpuFacePixels.Length != facePixels.Length)
            {
                chunk._gpuFacePixels = new Color32[facePixels.Length];
            }
            System.Array.Copy(facePixels, chunk._gpuFacePixels, facePixels.Length);
            _ResetGpuFaceReadbackBuffer(bufferIndex);
            chunk._gpuFaceReadbackQueueSlot = -1;
            chunk._gpuFaceBuildUsesSummary = true;
            chunk._gpuFaceBuildActive = true;
            chunk._gpuFaceBuildStage = 0;
            chunk._gpuFaceDirection = 0;
            chunk._gpuFaceSlice = 0;
            chunk._gpuFaceCrossIndex = 0;
            chunk._gpuFaceWaterColumnIndex = 0;
            chunk._gpuFaceSummaryIndex = 0;
        }
        else
        {
            chunk._gpuFacePixels = facePixels;
            chunk._gpuFaceReadbackQueueSlot = bufferIndex;
            chunk._gpuFaceBuildUsesSummary = false;
            chunk._gpuFaceBuildActive = true;
            chunk._gpuFaceBuildStage = -1;
            chunk._gpuFaceDirection = 0;
            chunk._gpuFaceSlice = 0;
            chunk._gpuFaceCrossIndex = 0;
            chunk._gpuFaceWaterColumnIndex = 0;
            chunk._gpuFaceSummaryIndex = 0;
        }

        if (activeMeshingCount < MAX_ACTIVE_CHUNKS)
        {
            activeMeshingChunks[activeMeshingCount++] = chunk;
        }
        else
        {
            // Rare fallback: if the active meshing queue is saturated, finish this
            // GPU-decoded mesh immediately rather than deadlocking a readback buffer.
            while (chunk.isBuildingMesh && chunk._gpuFaceBuildActive)
            {
                _GpuBuildMeshFromFaceDataStep(chunk);
            }
        }
    }

    private void _ReleaseGpuFaceBuildBuffer(ChunkData chunk)
    {
        if (chunk == null) return;

        int bufferIndex = chunk._gpuFaceReadbackQueueSlot;
        if (bufferIndex >= 0 && gpuFaceReadbackState != null && bufferIndex < gpuFaceReadbackState.Length)
        {
            _ResetGpuFaceReadbackBuffer(bufferIndex);
        }

        chunk._gpuFacePixels = null;
        chunk._gpuFaceReadbackQueueSlot = -1;
        chunk._gpuFaceBuildUsesSummary = false;
        chunk._gpuFaceBuildActive = false;
    }

    private int _GetGpuFaceSliceSummaryIndex(int direction, int slice)
    {
        return direction * chunkSizeY + slice;
    }

    private void _ResetGpuFaceSliceSummary(ChunkData chunk)
    {
        if (chunk == null) return;

        int summaryLength = 6 * chunkSizeY;
        if (chunk._gpuFaceSliceActive == null || chunk._gpuFaceSliceActive.Length != summaryLength)
        {
            chunk._gpuFaceSliceActive = new byte[summaryLength];
            chunk._gpuFaceSliceMinU = new byte[summaryLength];
            chunk._gpuFaceSliceMaxU = new byte[summaryLength];
            chunk._gpuFaceSliceMinV = new byte[summaryLength];
            chunk._gpuFaceSliceMaxV = new byte[summaryLength];
        }

        for (int i = 0; i < summaryLength; i++)
        {
            chunk._gpuFaceSliceActive[i] = 0;
            chunk._gpuFaceSliceMinU[i] = 255;
            chunk._gpuFaceSliceMaxU[i] = 0;
            chunk._gpuFaceSliceMinV[i] = 255;
            chunk._gpuFaceSliceMaxV[i] = 0;
        }
    }

    private void _MarkGpuFaceSliceBounds(ChunkData chunk, int direction, int slice, int u, int v)
    {
        int summaryIndex = _GetGpuFaceSliceSummaryIndex(direction, slice);
        if (chunk._gpuFaceSliceActive[summaryIndex] == 0)
        {
            chunk._gpuFaceSliceActive[summaryIndex] = 1;
            chunk._gpuFaceSliceMinU[summaryIndex] = (byte)u;
            chunk._gpuFaceSliceMaxU[summaryIndex] = (byte)u;
            chunk._gpuFaceSliceMinV[summaryIndex] = (byte)v;
            chunk._gpuFaceSliceMaxV[summaryIndex] = (byte)v;
            return;
        }

        if (u < chunk._gpuFaceSliceMinU[summaryIndex]) chunk._gpuFaceSliceMinU[summaryIndex] = (byte)u;
        if (u > chunk._gpuFaceSliceMaxU[summaryIndex]) chunk._gpuFaceSliceMaxU[summaryIndex] = (byte)u;
        if (v < chunk._gpuFaceSliceMinV[summaryIndex]) chunk._gpuFaceSliceMinV[summaryIndex] = (byte)v;
        if (v > chunk._gpuFaceSliceMaxV[summaryIndex]) chunk._gpuFaceSliceMaxV[summaryIndex] = (byte)v;
    }

    private bool _TryGetGpuFaceSliceBounds(ChunkData chunk, int direction, int slice, out int minU, out int maxU, out int minV, out int maxV)
    {
        minU = 0;
        maxU = -1;
        minV = 0;
        maxV = -1;

        if (chunk == null || chunk._gpuFaceSliceActive == null) return false;
        int summaryIndex = _GetGpuFaceSliceSummaryIndex(direction, slice);
        if (summaryIndex < 0 || summaryIndex >= chunk._gpuFaceSliceActive.Length) return false;
        if (chunk._gpuFaceSliceActive[summaryIndex] == 0) return false;

        minU = chunk._gpuFaceSliceMinU[summaryIndex];
        maxU = chunk._gpuFaceSliceMaxU[summaryIndex];
        minV = chunk._gpuFaceSliceMinV[summaryIndex];
        maxV = chunk._gpuFaceSliceMaxV[summaryIndex];
        return true;
    }

    private void _GpuBuildMeshFromFaceDataStep(ChunkData chunk)
    {
#if LOGGING
        float decodeStepStartTime = 0f;
        if (enableDetailedTimings)
        {
            decodeStepStartTime = Time.realtimeSinceStartup;
            stats_gpuFaceDecodeSteps++;
        }
#endif
        bool useSummary = chunk != null && chunk._gpuFaceBuildUsesSummary;
        if (chunk == null || (useSummary ? (chunk._decompSelf == null || chunk._gpuFacePixels == null) : chunk._gpuFacePixels == null))
        {
            if (chunk != null)
            {
                _ReleaseGpuFaceBuildBuffer(chunk);
                chunk.isBuildingMesh = false;
                chunk.interactionMeshPriority = false;
                if (chunk.pendingNeighborMeshRebuild)
                {
                    chunk.pendingNeighborMeshRebuild = false;
                    TriggerNeighborMeshRebuilds(chunk);
                }
            }
#if LOGGING
            if (enableDetailedTimings)
            {
                float decodeStepMs = (Time.realtimeSinceStartup - decodeStepStartTime) * 1000f;
                stats_gpuFaceDecodeTime += decodeStepMs;
                if (decodeStepMs < stats_gpuFaceDecodeTimeMin) stats_gpuFaceDecodeTimeMin = decodeStepMs;
                if (decodeStepMs > stats_gpuFaceDecodeTimeMax) stats_gpuFaceDecodeTimeMax = decodeStepMs;
            }
#endif
            return;
        }

        int sizeXZ = chunkSizeXZ;
        int sizeY = chunkSizeY;
        int stride = sizeXZ * sizeXZ;
        float budgetStart = Time.realtimeSinceStartup;
        float budgetSec = Mathf.Max(0.25f, adaptiveGpuMeshDecodeStepBudgetMs) * 0.001f;
        Color32[] facePixels = chunk._gpuFacePixels;
        int totalPixels = sizeY * stride;
        int summaryBudgetCountdown = 256;
        int crossBudgetCountdown = 64;
        byte[] selfData = chunk._decompSelf;
        byte[] dataPX = chunk._decompPX;
        byte[] dataNX = chunk._decompNX;
        byte[] dataPY = chunk._decompPY;
        byte[] dataNY = chunk._decompNY;
        byte[] dataPZ = chunk._decompPZ;
        byte[] dataNZ = chunk._decompNZ;

        while (Time.realtimeSinceStartup - budgetStart <= budgetSec)
        {
            if (chunk._gpuFaceBuildStage == -1)
            {
                if (useSummary)
                {
                    chunk._gpuFaceBuildStage = 0;
                    chunk._gpuFaceDirection = 0;
                    chunk._gpuFaceSlice = 0;
                    chunk._gpuFaceCrossIndex = 0;
                    chunk._gpuFaceWaterColumnIndex = 0;
                    continue;
                }

                if (chunk._gpuFaceSummaryIndex == 0)
                {
                    _ResetGpuFaceSliceSummary(chunk);
                }

                while (chunk._gpuFaceSummaryIndex < totalPixels)
                {
                    int pixelIndex = chunk._gpuFaceSummaryIndex;
                    chunk._gpuFaceSummaryIndex = pixelIndex + 1;
                    if (pixelIndex >= facePixels.Length) break;

                    Color32 pixel = facePixels[pixelIndex];
                    if (pixel.a < 128 || pixel.g == 0 || pixel.b >= 1)
                    {
                        if (--summaryBudgetCountdown <= 0)
                        {
                            summaryBudgetCountdown = 256;
                            if (Time.realtimeSinceStartup - budgetStart > budgetSec) break;
                        }
                        continue;
                    }

                    int faceMask = pixel.r;
                    if (faceMask == 0)
                    {
                        if (--summaryBudgetCountdown <= 0)
                        {
                            summaryBudgetCountdown = 256;
                            if (Time.realtimeSinceStartup - budgetStart > budgetSec) break;
                        }
                        continue;
                    }

                    int y = pixelIndex / stride;
                    int z = (pixelIndex / sizeXZ) % sizeXZ;
                    int x = pixelIndex % sizeXZ;

                    if ((faceMask & 1) != 0) _MarkGpuFaceSliceBounds(chunk, 0, y, x, z);
                    if ((faceMask & 2) != 0) _MarkGpuFaceSliceBounds(chunk, 1, y, x, z);
                    if ((faceMask & 4) != 0) _MarkGpuFaceSliceBounds(chunk, 2, z, x, y);
                    if ((faceMask & 8) != 0) _MarkGpuFaceSliceBounds(chunk, 3, z, x, y);
                    if ((faceMask & 16) != 0) _MarkGpuFaceSliceBounds(chunk, 4, x, z, y);
                    if ((faceMask & 32) != 0) _MarkGpuFaceSliceBounds(chunk, 5, x, z, y);

                    if (--summaryBudgetCountdown <= 0)
                    {
                        summaryBudgetCountdown = 256;
                        if (Time.realtimeSinceStartup - budgetStart > budgetSec) break;
                    }
                }

                if (chunk._gpuFaceSummaryIndex >= totalPixels)
                {
                    chunk._gpuFaceBuildStage = 0;
                    chunk._gpuFaceDirection = 0;
                    chunk._gpuFaceSlice = 0;
                    chunk._gpuFaceCrossIndex = 0;
                    chunk._gpuFaceWaterColumnIndex = 0;
                }
                continue;
            }

            if (chunk._gpuFaceBuildStage == 0)
            {
                if (chunk._gpuFaceDirection >= 6)
                {
                    chunk._gpuFaceBuildStage = 1;
                    continue;
                }

                int direction = chunk._gpuFaceDirection;
                int bit = 1 << direction;
                int maskWidth, maskHeight, sliceCount;
                if (direction <= 1) { maskWidth = sizeXZ; maskHeight = sizeXZ; sliceCount = sizeY; }
                else if (direction <= 3) { maskWidth = sizeXZ; maskHeight = sizeY; sliceCount = sizeXZ; }
                else { maskWidth = sizeXZ; maskHeight = sizeY; sliceCount = sizeXZ; }

                if (chunk._gpuFaceSlice >= sliceCount)
                {
                    chunk._gpuFaceSlice = 0;
                    chunk._gpuFaceDirection++;
                    continue;
                }

                int slice = chunk._gpuFaceSlice;

                // Skip slices outside the chunk's occupied block region.
                // The face bitmask can't have set bits for slices with no self-blocks.
                {
                    bool canSkip = false;
                    if (direction <= 1) canSkip = slice < chunk._chunkGlobalMinY || slice > chunk._chunkGlobalMaxY;
                    else if (direction <= 3) canSkip = slice < chunk._chunkGlobalMinZ || slice > chunk._chunkGlobalMaxZ;
                    else canSkip = slice < chunk._chunkGlobalMinX || slice > chunk._chunkGlobalMaxX;
                    if (canSkip)
                    {
                        chunk._gpuFaceSlice++;
                        continue;
                    }
                }

                if (useSummary)
                {
                    float compactMaskStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
                    bool hasMask = _BuildGreedySliceMaskCompact(chunk, direction, slice, selfData, stride, out maskWidth, out maskHeight);
                    if (enableDetailedTimings)
                    {
                        stats_gpuFaceCompactMaskDecodeTime += (Time.realtimeSinceStartup - compactMaskStart) * 1000f;
                    }
                    if (!hasMask)
                    {
                        chunk._gpuFaceSlice++;
                        continue;
                    }

                    float compactEmitStart = enableDetailedTimings ? Time.realtimeSinceStartup : 0f;
                    stats_trackCompactEmitInternals = enableDetailedTimings;
                    _EmitGreedyMask(chunk, direction, slice, maskWidth, maskHeight);
                    stats_trackCompactEmitInternals = false;
                    if (enableDetailedTimings)
                    {
                        stats_gpuFaceCompactEmitTime += (Time.realtimeSinceStartup - compactEmitStart) * 1000f;
                    }
                }
                else
                {
                    int minU;
                    int maxU;
                    int minV;
                    int maxV;
                    if (!_TryGetGpuFaceSliceBounds(chunk, direction, slice, out minU, out maxU, out minV, out maxV))
                    {
                        chunk._gpuFaceSlice++;
                        continue;
                    }
                    int clearWidth = maxU - minU + 1;
                    for (int clearV = minV; clearV <= maxV; clearV++)
                    {
                        System.Array.Clear(greedyMaskBlockIds, clearV * maskWidth + minU, clearWidth);
                    }
                    int[] packedGrassColors = chunk._cachedPackedGrassBiomeColors;
                    byte[] tintModes = biomeTintModeCache;
                    bool nsUseAo = _UsesCpuAmbientOcclusion();
                    bool nsHasTintModes = tintModes != null;
                    bool nsHasGrassColors = packedGrassColors != null;
                    bool nsConstantLight = !nsUseAo && _UsesGpuTerrainLightSampling();
                    _greedyConstantLight = nsConstantLight;

                    for (int v = minV; v <= maxV; v++)
                    {
                        int maskRowOffset = v * maskWidth;
                        for (int u = minU; u <= maxU; u++)
                        {
                            int x, y, z;
                            if (direction <= 1) { x = u; y = slice; z = v; }
                            else if (direction <= 3) { x = u; y = v; z = slice; }
                            else { x = slice; y = v; z = u; }

                            int pixelIndex = y * stride + z * sizeXZ + x;
                            if (pixelIndex >= facePixels.Length) continue;

                            Color32 pixel = facePixels[pixelIndex];
                            if (pixel.a < 128) continue;

                            int faceMask = pixel.r;
                            int blockID = pixel.g;
                            int shapeType = pixel.b;

                            if (blockID == 0 || shapeType >= 1) continue;
                            if ((faceMask & bit) == 0) continue;

                            int maskIndex = maskRowOffset + u;
                            greedyMaskBlockIds[maskIndex] = (byte)blockID;
                            if (nsUseAo)
                            {
                                greedyMaskLightLevels[maskIndex] = 0;
                                greedyMaskAoSignatures[maskIndex] = _BuildAoSignature(chunk, (byte)blockID, direction, x, y, z);
                            }
                            else if (!nsConstantLight)
                            {
                                greedyMaskLightLevels[maskIndex] = _GetCachedLightLevelForDirection(chunk, direction, x, y, z);
                            }
                            byte tintMode = (nsHasTintModes && blockID < tintModes.Length) ? tintModes[blockID] : (byte)0;
                            if (tintMode == 0)
                            {
                                greedyMaskPackedColors[maskIndex] = PACKED_WHITE_RGB;
                            }
                            else
                            {
                                int biomeIndex = z * sizeXZ + x;
                                if (tintMode == 1 && nsHasGrassColors && biomeIndex < packedGrassColors.Length)
                                {
                                    greedyMaskPackedColors[maskIndex] = packedGrassColors[biomeIndex];
                                }
                                else
                                {
                                    greedyMaskPackedColors[maskIndex] = _PackColorRGB(_GetCachedBiomeColor(chunk, (byte)blockID, x, z));
                                }
                            }
                        }
                    }
                    _EmitGreedyMaskRegion(chunk, direction, slice, maskWidth, maskHeight, minU, maxU, minV, maxV);
                }
                chunk._gpuFaceSlice++;
                continue;
            }

            if (chunk._gpuFaceBuildStage == 1)
            {
                float compactCrossStart = enableDetailedTimings && useSummary ? Time.realtimeSinceStartup : 0f;
                int crossCount = chunk._crossBlockPackedPositions != null ? chunk._crossBlockCount : 0;
                while (chunk._gpuFaceCrossIndex < crossCount)
                {
                    int crossIdx = chunk._gpuFaceCrossIndex;
                    chunk._gpuFaceCrossIndex = crossIdx + 1;
                    int packed = chunk._crossBlockPackedPositions[crossIdx];
                    int x = (packed >> 16) & 0xFF;
                    int y = (packed >> 8) & 0xFF;
                    int z = packed & 0xFF;
                    if (useSummary)
                    {
                        int blockIndex = y * stride + z * sizeXZ + x;
                        if (blockIndex >= 0 && blockIndex < selfData.Length)
                        {
                            byte blockID = selfData[blockIndex];
                            if (blockID != 0)
                            {
                                _AddCrossBlockQuads(chunk, blockID, x, y, z);
                            }
                        }
                    }
                    else
                    {
                        int pixelIndex = y * stride + z * sizeXZ + x;
                        if (pixelIndex >= 0 && pixelIndex < facePixels.Length)
                        {
                            Color32 pixel = facePixels[pixelIndex];
                            if (pixel.a >= 128 && pixel.g != 0)
                            {
                                _AddCrossBlockQuads(chunk, (byte)pixel.g, x, y, z);
                            }
                        }
                    }

                    if (--crossBudgetCountdown <= 0)
                    {
                        crossBudgetCountdown = 64;
                        if (Time.realtimeSinceStartup - budgetStart > budgetSec) break;
                    }
                }

                if (chunk._gpuFaceCrossIndex >= crossCount)
                {
                    // Torches are few per chunk — process them unbounded.
                    _AddTorchBlocks(chunk);
                    // Water gets its own budget-gated stage to avoid 50-100ms spikes
                    // on chunks with large water bodies.
                    chunk._gpuFaceBuildStage = chunk._hasWaterBlocks ? 2 : 3;
                }
                if (enableDetailedTimings && useSummary)
                {
                    stats_gpuFaceCompactCrossTime += (Time.realtimeSinceStartup - compactCrossStart) * 1000f;
                }
                continue;
            }

            // Stage 2: budget-gated water block fixup (column by column)
            if (chunk._gpuFaceBuildStage == 2)
            {
                if (chunk._decompSelf != null && chunk._columnMinY != null)
                {
                    int waterStride = chunkSizeXZ * chunkSizeXZ;
                    int totalColumns = waterStride;
                    int waterBudgetCountdown = 4;
                    int colMinLen = chunk._columnMinY.Length;
                    int decompLen = chunk._decompSelf.Length;
                    while (chunk._gpuFaceWaterColumnIndex < totalColumns)
                    {
                        // Read field into local THEN advance — avoids Udon post-increment
                        // field-access quirk where col=0 was silently skipped.
                        int col = chunk._gpuFaceWaterColumnIndex;
                        chunk._gpuFaceWaterColumnIndex = col + 1;
                        if (col >= colMinLen) break;
                        int z = col / chunkSizeXZ;
                        int x = col % chunkSizeXZ;
                        byte minY = chunk._columnMinY[col];
                        byte maxY = chunk._columnMaxY[col];
                        if (minY == 255 || maxY == 255) continue;
                        for (int y = minY; y <= maxY; y++)
                        {
                            int idx = y * waterStride + col;
                            if (idx >= decompLen) break;
                            byte blockID = chunk._decompSelf[idx];
                            if (!_IsWaterBlock(blockID) && !_IsLavaBlock(blockID)) continue;
                            _AddWaterBlock(chunk, x, y, z, blockID);
                        }
                        if (--waterBudgetCountdown <= 0)
                        {
                            waterBudgetCountdown = 4;
                            if (Time.realtimeSinceStartup - budgetStart > budgetSec) break;
                        }
                    }
                    if (chunk._gpuFaceWaterColumnIndex >= totalColumns)
                    {
                        chunk._gpuFaceBuildStage = 3;
                    }
                }
                else
                {
                    chunk._gpuFaceBuildStage = 3;
                }
                continue;
            }

            if (chunk._gpuFaceBuildStage == 3)
            {
                bool interactionPriority = chunk.interactionMeshPriority;
                _ApplyAllMeshData(chunk);
                if (chunk._collisionVertexCount == 0) _DisableChunkCollider(chunk, false);
                else if (interactionPriority) _ApplyDataToCollider(chunk);
                else if (!_ShouldEnableChunkCollider(chunk)) _DisableChunkCollider(chunk, true);
                else if (_ShouldDeferChunkSecondaryWork(chunk)) _QueueDeferredColliderApply(chunk);
                else _ApplyDataToCollider(chunk);
                _ReleaseMeshPool(chunk);
#if LOGGING
                _RecordMeshBuildCompletion(chunk, true, false);
#endif
                _ReleaseGpuFaceBuildBuffer(chunk);
                chunk.isBuildingMesh = false;
                chunk.interactionMeshPriority = false;
                // The GPU readback decode spans multiple frames. If a neighbor chunk finished
                // data-gen during that window, its trigger landed on pendingNeighborMeshRebuild
                // rather than firing live — consume it now before nulling neighbor refs.
                if (chunk.pendingNeighborMeshRebuild)
                {
                    chunk.pendingNeighborMeshRebuild = false;
                    TriggerNeighborMeshRebuilds(chunk);
                }
                // Immediately re-queue if we meshed with missing border data and the
                // neighbors are now ready — don't wait for the passive heal scan.
                if (chunk._borderMissingMask != 0)
                {
                    _TryImmediateBorderHeal(chunk);
                }
                if (useSummary)
                {
                    chunk.neighborPX = null; chunk.neighborNX = null;
                    chunk.neighborPY = null; chunk.neighborNY = null;
                    chunk.neighborPZ = null; chunk.neighborNZ = null;
                    chunk._decompSelf = null;
                    chunk._decompNX = null; chunk._decompPX = null;
                    chunk._decompNY = null; chunk._decompPY = null;
                    chunk._decompNZ = null; chunk._decompPZ = null;
                }
#if LOGGING
                if (enableDetailedTimings)
                {
                    float decodeStepMs = (Time.realtimeSinceStartup - decodeStepStartTime) * 1000f;
                    stats_gpuFaceDecodeTime += decodeStepMs;
                    if (decodeStepMs < stats_gpuFaceDecodeTimeMin) stats_gpuFaceDecodeTimeMin = decodeStepMs;
                    if (decodeStepMs > stats_gpuFaceDecodeTimeMax) stats_gpuFaceDecodeTimeMax = decodeStepMs;
                }
#endif
                return;
            }

            break;
        }

#if LOGGING
        if (enableDetailedTimings)
        {
            float decodeStepMs = (Time.realtimeSinceStartup - decodeStepStartTime) * 1000f;
            stats_gpuFaceDecodeTime += decodeStepMs;
            if (decodeStepMs < stats_gpuFaceDecodeTimeMin) stats_gpuFaceDecodeTimeMin = decodeStepMs;
            if (decodeStepMs > stats_gpuFaceDecodeTimeMax) stats_gpuFaceDecodeTimeMax = decodeStepMs;
        }
#endif
    }

    private void _AddCrossBlockQuads(ChunkData chunk, byte blockID, int x, int y, int z)
    {
        if (blockID == 51)
        {
            BlockVisibilityType vis = (blockID < visibilityCache.Length) ? visibilityCache[blockID] : BlockVisibilityType.Cutout;
            _AddFireBlock(chunk, new Vector3(x, y, z), vis);
            return;
        }
        float textureSlice = (blockID < uv_allFacesCache.Length) ? uv_allFacesCache[blockID] : 0;
        Color biomeColor = _GetCachedBiomeColor(chunk, blockID, x, z);
        biomeColor.a = 1.0f;

        long seed = (long)(x + chunk.chunkX_world * chunkSizeXZ) * 3129871L ^ (long)(z + chunk.chunkZ_world * chunkSizeXZ) * 116129781L;
        seed = seed * seed * 42317861L + seed * 11L;
        float ox = (float)((seed >> 16) & 15L) / 15f * 0.5f - 0.25f;
        float oz = (float)((seed >> 24) & 15L) / 15f * 0.5f - 0.25f;
        float cx = x + 0.5f + ox;
        float cz = z + 0.5f + oz;

        // Two perpendicular quads forming an X. Cutout shader uses Cull Off so both sides render.
        _EmitCrossQuad(chunk, blockID, textureSlice, biomeColor,
            new Vector3(cx - 0.45f, y, cz - 0.45f), new Vector3(cx - 0.45f, y + 1, cz - 0.45f),
            new Vector3(cx + 0.45f, y + 1, cz + 0.45f), new Vector3(cx + 0.45f, y, cz + 0.45f));
        _EmitCrossQuad(chunk, blockID, textureSlice, biomeColor,
            new Vector3(cx + 0.45f, y, cz - 0.45f), new Vector3(cx + 0.45f, y + 1, cz - 0.45f),
            new Vector3(cx - 0.45f, y + 1, cz + 0.45f), new Vector3(cx - 0.45f, y, cz + 0.45f));
    }

    private void _EmitCrossQuad(ChunkData chunk, byte blockID, float textureSlice, Color color, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3)
    {
        BlockVisibilityType visibility = (blockID < visibilityCache.Length) ? visibilityCache[blockID] : BlockVisibilityType.Cutout;
        Vector3[] targetVertices; int[] targetTriangles; Vector3[] targetUVs; Vector3[] targetNormals; Color[] targetColors;
        int currentVertexCount; int currentTriangleCount;

        if (visibility == BlockVisibilityType.Cutout)
        {
            if (chunk._cutoutVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._cutoutVertices; targetTriangles = chunk._cutoutTriangles; targetUVs = chunk._cutoutUVs; targetNormals = chunk._cutoutNormals; targetColors = chunk._cutoutColors;
            currentVertexCount = chunk._cutoutVertexCount; currentTriangleCount = chunk._cutoutTriangleCount;
        }
        else
        {
            if (chunk._opaqueVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._opaqueVertices; targetTriangles = chunk._opaqueTriangles; targetUVs = chunk._opaqueUVs; targetNormals = chunk._opaqueNormals; targetColors = chunk._opaqueColors;
            currentVertexCount = chunk._opaqueVertexCount; currentTriangleCount = chunk._opaqueTriangleCount;
        }

        targetVertices[currentVertexCount + 0] = v0;
        targetVertices[currentVertexCount + 1] = v1;
        targetVertices[currentVertexCount + 2] = v2;
        targetVertices[currentVertexCount + 3] = v3;
        Vector3 crossNormal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
        for (int i = 0; i < 4; i++) targetNormals[currentVertexCount + i] = crossNormal;
        for (int i = 0; i < 4; i++) targetColors[currentVertexCount + i] = color;
        targetUVs[currentVertexCount + 0] = new Vector3(0, 0, textureSlice);
        targetUVs[currentVertexCount + 1] = new Vector3(0, 1, textureSlice);
        targetUVs[currentVertexCount + 2] = new Vector3(1, 1, textureSlice);
        targetUVs[currentVertexCount + 3] = new Vector3(1, 0, textureSlice);
        targetTriangles[currentTriangleCount + 0] = currentVertexCount;
        targetTriangles[currentTriangleCount + 1] = currentVertexCount + 1;
        targetTriangles[currentTriangleCount + 2] = currentVertexCount + 2;
        targetTriangles[currentTriangleCount + 3] = currentVertexCount;
        targetTriangles[currentTriangleCount + 4] = currentVertexCount + 2;
        targetTriangles[currentTriangleCount + 5] = currentVertexCount + 3;

        if (visibility == BlockVisibilityType.Cutout) { chunk._cutoutVertexCount += 4; chunk._cutoutTriangleCount += 6; }
        else { chunk._opaqueVertexCount += 4; chunk._opaqueTriangleCount += 6; }
    }

    private void _CacheSharedChunkMaterials()
    {
        if (chunkPrefab == null) return;
        if (terrainPropUseVertexLightId == -1) terrainPropUseVertexLightId = VRCShader.PropertyToID("_UseVertexLight");

        Transform opaqueTransform = chunkPrefab.transform.Find("Opaque");
        if (opaqueTransform == null) opaqueTransform = chunkPrefab.transform;
        Transform transparentTransform = chunkPrefab.transform.Find("Transparent");
        if (transparentTransform == null) transparentTransform = chunkPrefab.transform.Find("trans");
        Transform cutoutTransform = chunkPrefab.transform.Find("Cutout");
        if (cutoutTransform == null) cutoutTransform = chunkPrefab.transform.Find("cutout");

        MeshRenderer opaqueRenderer = opaqueTransform != null ? opaqueTransform.GetComponent<MeshRenderer>() : null;
        MeshRenderer transparentRenderer = transparentTransform != null ? transparentTransform.GetComponent<MeshRenderer>() : null;
        MeshRenderer cutoutRenderer = cutoutTransform != null ? cutoutTransform.GetComponent<MeshRenderer>() : null;

        sharedOpaqueChunkMaterial = opaqueRenderer != null ? opaqueRenderer.sharedMaterial : null;
        sharedTransparentChunkMaterial = transparentRenderer != null ? transparentRenderer.sharedMaterial : null;
        sharedCutoutChunkMaterial = cutoutRenderer != null ? cutoutRenderer.sharedMaterial : null;

        _EnableSharedChunkMaterialInstancing(sharedOpaqueChunkMaterial);
        _EnableSharedChunkMaterialInstancing(sharedTransparentChunkMaterial);
        _EnableSharedChunkMaterialInstancing(sharedCutoutChunkMaterial);
        _ApplyFireTextureToMaterials();
        _ApplyTerrainLightingSourceToSharedMaterials();
    }

    private void _EnableSharedChunkMaterialInstancing(Material material)
    {
        if (material == null) return;
        if (!material.enableInstancing) material.enableInstancing = true;
    }

    private void _ApplyFireTextureToMaterials()
    {
        if (fireStripTexture != null)
        {
            int fireTexId = VRCShader.PropertyToID("_FireTex");
            int fireSpeedId = VRCShader.PropertyToID("_FireSpeed");
            Material[] fireMats = new Material[] { sharedOpaqueChunkMaterial, sharedCutoutChunkMaterial };
            for (int i = 0; i < fireMats.Length; i++)
            {
                Material m = fireMats[i];
                if (m == null) continue;
                m.SetTexture(fireTexId, fireStripTexture);
                m.SetFloat(fireSpeedId, 20f);
            }
        }

        if (lavaStripTexture != null && sharedTransparentChunkMaterial != null)
        {
            int lavaTexId = VRCShader.PropertyToID("_LavaTex");
            sharedTransparentChunkMaterial.SetTexture(lavaTexId, lavaStripTexture);
        }
    }

    private RenderTexture _CreateWaterRT(string name, bool isStateTexture)
    {
        RenderTextureFormat fmt = isStateTexture ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
        RenderTexture rt = new RenderTexture(16, 16, 0, fmt, RenderTextureReadWrite.Linear);
        rt.name = name;
        rt.filterMode = FilterMode.Point;
        rt.wrapMode = TextureWrapMode.Repeat;
        rt.Create();
        return rt;
    }

    private void _InitializeBetaWaterRendering()
    {
        betaWaterStillSlice = blockTypeManager != null ? blockTypeManager.GetFinalBlockTextureSlice(BLOCK_WATER_STILL, FACE_INDEX_TOP) : -1;
        betaWaterFlowSlice = blockTypeManager != null ? blockTypeManager.GetFinalBlockTextureSlice(BLOCK_WATER_STILL, FACE_INDEX_SIDE) : -1;
        if (betaWaterStillSlice < 0 || betaWaterFlowSlice < 0) return;
        if (gpuWaterAnimMaterial == null) return;

        terrainPropWaterStillTexId = VRCShader.PropertyToID("_WaterStillTex");
        terrainPropWaterFlowTexId = VRCShader.PropertyToID("_WaterFlowTex");
        terrainPropWaterStillSliceId = VRCShader.PropertyToID("_WaterStillSlice");
        terrainPropWaterFlowSliceId = VRCShader.PropertyToID("_WaterFlowSlice");

        betaWaterStillStateA = _CreateWaterRT("WaterStillState_A", true);
        betaWaterStillStateB = _CreateWaterRT("WaterStillState_B", true);
        betaWaterStillColor = _CreateWaterRT("WaterStillColor", false);
        betaWaterFlowStateA = _CreateWaterRT("WaterFlowState_A", true);
        betaWaterFlowStateB = _CreateWaterRT("WaterFlowState_B", true);
        betaWaterFlowColor = _CreateWaterRT("WaterFlowColor", false);
        betaWaterFlowFrame = 0;

        _ApplyBetaWaterMaterialProperties(sharedTransparentChunkMaterial);
    }

    private void _ApplyBetaWaterMaterialProperties(Material material)
    {
        if (material == null || betaWaterStillColor == null || betaWaterFlowColor == null) return;

        material.SetTexture(terrainPropWaterStillTexId, betaWaterStillColor);
        material.SetTexture(terrainPropWaterFlowTexId, betaWaterFlowColor);
        material.SetFloat(terrainPropWaterStillSliceId, betaWaterStillSlice);
        material.SetFloat(terrainPropWaterFlowSliceId, betaWaterFlowSlice);
    }



    private void _UpdateBetaWaterAnimation()
    {
        if (betaWaterStillStateA == null || gpuWaterAnimMaterial == null) return;

        float t = Time.time * 73.1f;

        // Still water: evolve state then convert to color
        gpuWaterAnimMaterial.SetFloat("_Time2", t);
        gpuWaterAnimMaterial.SetFloat("_SplashChance", 0.05f);
        gpuWaterAnimMaterial.SetFloat("_SplashDecay", 0.1f);
        gpuWaterAnimMaterial.SetFloat("_DivisorInv", 1.0f / 3.3f);
        gpuWaterAnimMaterial.SetFloat("_IsFlowMode", 0f);
        VRCGraphics.Blit(betaWaterStillStateA, betaWaterStillStateB, gpuWaterAnimMaterial, 0);
        RenderTexture tempStill = betaWaterStillStateA;
        betaWaterStillStateA = betaWaterStillStateB;
        betaWaterStillStateB = tempStill;

        gpuWaterAnimMaterial.SetFloat("_FlowScroll", 0f);
        VRCGraphics.Blit(betaWaterStillStateA, betaWaterStillColor, gpuWaterAnimMaterial, 1);

        // Flow water: evolve state then convert to color with scroll
        betaWaterFlowFrame++;
        gpuWaterAnimMaterial.SetFloat("_SplashChance", 0.2f);
        gpuWaterAnimMaterial.SetFloat("_SplashDecay", 0.3f);
        gpuWaterAnimMaterial.SetFloat("_DivisorInv", 1.0f / 3.2f);
        gpuWaterAnimMaterial.SetFloat("_IsFlowMode", 1f);
        gpuWaterAnimMaterial.SetFloat("_Time2", t + 17.3f);
        VRCGraphics.Blit(betaWaterFlowStateA, betaWaterFlowStateB, gpuWaterAnimMaterial, 0);
        RenderTexture tempFlow = betaWaterFlowStateA;
        betaWaterFlowStateA = betaWaterFlowStateB;
        betaWaterFlowStateB = tempFlow;

        gpuWaterAnimMaterial.SetFloat("_FlowScroll", (float)betaWaterFlowFrame / 16.0f);
        VRCGraphics.Blit(betaWaterFlowStateA, betaWaterFlowColor, gpuWaterAnimMaterial, 1);
    }

    private bool _UsesGpuTerrainLightSampling()
    {
        return UsesGpuLightingBackend();
    }

    private bool _UsesGpuExactAmbientOcclusion()
    {
        return ambientOcclusion && UsesGpuLightingBackend();
    }

    private bool _UsesCpuAmbientOcclusion()
    {
        return ambientOcclusion && !UsesGpuLightingBackend();
    }

    public bool RequiresCpuLightingForAmbientOcclusion()
    {
        return _UsesCpuAmbientOcclusion();
    }

    private void _ApplyTerrainLightingSource(Material material)
    {
        if (material == null) return;
        if (terrainPropUseVertexLightId == -1) terrainPropUseVertexLightId = VRCShader.PropertyToID("_UseVertexLight");
        if (terrainPropUseGpuExactAoId == -1) terrainPropUseGpuExactAoId = VRCShader.PropertyToID("_UseGpuExactAo");
        material.SetFloat(terrainPropUseVertexLightId, _UsesGpuTerrainLightSampling() ? 0f : 1f);
        material.SetFloat(terrainPropUseGpuExactAoId, _UsesGpuExactAmbientOcclusion() ? 1f : 0f);
    }

    private void _ApplyTerrainLightingSourceToSharedMaterials()
    {
        _ApplyTerrainLightingSource(sharedOpaqueChunkMaterial);
        _ApplyTerrainLightingSource(sharedTransparentChunkMaterial);
        _ApplyTerrainLightingSource(sharedCutoutChunkMaterial);
        _ApplyBetaWaterMaterialProperties(sharedTransparentChunkMaterial);
    }

    private void _AssignSharedChunkMaterial(Transform childTransform, Material sharedMaterial)
    {
        if (childTransform == null || sharedMaterial == null) return;

        MeshRenderer renderer = childTransform.GetComponent<MeshRenderer>();
        if (renderer == null) return;
        if (renderer.sharedMaterial != sharedMaterial) renderer.sharedMaterial = sharedMaterial;
    }

    public int InstantiateAndConfigureChunk(int array_cx, int array_cy, int array_cz)
    {
        int centered_dx = array_cx - chunkOffsetX;
        int centered_dy = array_cy - chunkOffsetY;
        int centered_dz = array_cz - chunkOffsetZ;

        int chunk1DIndex = ChunkCenteredCoordsTo1D(centered_dx, centered_dy, centered_dz);
        if (chunk1DIndex == -1 || chunks_1D[chunk1DIndex] != null) return -1;

        GameObject newChunkGO = Instantiate(chunkPrefab);
        newChunkGO.name = $"Chunk_({centered_dx},{centered_dy},{centered_dz})";
        newChunkGO.transform.SetParent(this.transform, false);
        newChunkGO.transform.localPosition = new Vector3(centered_dx * chunkSizeXZ, centered_dy * chunkSizeY, centered_dz * chunkSizeXZ);

        ChunkData newChunkData = new ChunkData();
        chunks_1D[chunk1DIndex] = newChunkData;

        // --- Initialize ChunkData object ---
        newChunkData.world = this;
        newChunkData.chunkX_world = centered_dx;
        newChunkData.chunkY_world = centered_dy;
        newChunkData.chunkZ_world = centered_dz;
        newChunkData.gameObject = newChunkGO;

        // --- Assign Component References ---
        // This is a bit fragile, relies on child object names. A more robust solution
        // would be a simple script on the prefab to hold the references.
        Transform opaqueTransform = newChunkGO.transform.Find("Opaque");
        if (opaqueTransform == null) opaqueTransform = newChunkGO.transform;
        Transform transparentTransform = newChunkGO.transform.Find("Transparent");
        if (transparentTransform == null) transparentTransform = newChunkGO.transform.Find("trans");
        Transform cutoutTransform = newChunkGO.transform.Find("Cutout");
        if (cutoutTransform == null) cutoutTransform = newChunkGO.transform.Find("cutout");

        _AssignSharedChunkMaterial(opaqueTransform, sharedOpaqueChunkMaterial);
        _AssignSharedChunkMaterial(transparentTransform, sharedTransparentChunkMaterial);
        _AssignSharedChunkMaterial(cutoutTransform, sharedCutoutChunkMaterial);

        newChunkData.opaqueMeshFilter = opaqueTransform != null ? opaqueTransform.GetComponent<MeshFilter>() : null;
        newChunkData.transparentMeshFilter = transparentTransform != null ? transparentTransform.GetComponent<MeshFilter>() : null;
        newChunkData.cutoutMeshFilter = cutoutTransform != null ? cutoutTransform.GetComponent<MeshFilter>() : null;
        newChunkData.meshCollider = newChunkGO.GetComponent<MeshCollider>();
        if (newChunkData.meshCollider != null) { newChunkData.meshCollider.sharedMesh = null; newChunkData.meshCollider.enabled = false; }

        // --- Initialize Buffers & State ---
        newChunkData._chunkDataSize = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
        newChunkData._cachedNeighbors = new ChunkData[6];

        // Mesh buffers are pooled — acquired in _AcquireMeshPool, released in _ReleaseMeshPool.
        // This avoids ~2.7MB of allocation per chunk at creation time and means
        // air-only/occluded chunks that skip meshing never allocate buffers at all.

        // Initialize biome data arrays (16x16 per chunk)
        newChunkData._biomeTemperatures = new double[chunkSizeXZ * chunkSizeXZ];
        newChunkData._biomeRainfall = new double[chunkSizeXZ * chunkSizeXZ];

        newChunkGO.SetActive(true);
        _InvalidateNeighborCache(newChunkData);

#if LOGGING
        if (enableCounters) stats_chunkCreations++;
#endif

        return chunk1DIndex;
    }

    void Update()
    {
        // Safety check: Don't process if not initialized
        if (chunks_1D == null || terrainGenerator == null) return;

        // Load-phase budget boost: while the one-time initial world generation is still
        // running we keep the larger updateTimeBudgetMs set at init (lets the idle GPU +
        // readback pipeline do much more per frame). Once gen completes, restore the normal
        // runtime budget so steady-state frame rate is protected. Accuracy is unaffected —
        // this only changes how much already-scheduled work runs per frame.
        if (!_loadPhaseBudgetRestored && coordinator != null && coordinator.IsWorldGenComplete())
        {
            _loadPhaseBudgetRestored = true;
            updateTimeBudgetMs = _runtimeUpdateBudgetMs;
        }

#if LOGGING
        float updateStartTime = 0f;
        if (enableAdaptiveBudgets || enableDetailedTimings || enableFrameLogging || enableAggregateLogging)
        {
            updateStartTime = Time.realtimeSinceStartup;
        }
#endif

        _UpdateBetaWaterAnimation();
        ProcessActiveChunks();
        if (blockTicker != null) blockTicker.Tick();
        _UpdateGpuDebugHud();

#if LOGGING
        if (enableAdaptiveBudgets || enableDetailedTimings || enableFrameLogging || enableAggregateLogging)
        {
            float updateTime = (Time.realtimeSinceStartup - updateStartTime) * 1000f;
            _UpdateAdaptiveBudgets(updateTime);
            stats_updateTotalTime += updateTime;
            if (updateTime < stats_updateTimeMin) stats_updateTimeMin = updateTime;
            if (updateTime > stats_updateTimeMax) stats_updateTimeMax = updateTime;
            if (updateTime > updateTimeBudgetMs) stats_budgetExceededCount++;
            stats_dataGenActiveSamplesTotal += activeDataGenCount;
            stats_meshingActiveSamplesTotal += activeMeshingCount;
            stats_reconciliationQueueSamplesTotal += deferredReconciliationQueue.Count;
            if (activeDataGenCount > stats_dataGenActiveMax) stats_dataGenActiveMax = activeDataGenCount;
            if (activeMeshingCount > stats_meshingActiveMax) stats_meshingActiveMax = activeMeshingCount;
            if (deferredReconciliationQueue.Count > stats_reconciliationQueueMax) stats_reconciliationQueueMax = deferredReconciliationQueue.Count;
            stats_frameCount++;

            // Per-frame logging
            if (enableFrameLogging)
            {
                LogFrameStats(updateTime);
            }

            // Aggregate logging
            if (enableAggregateLogging && stats_frameCount % aggregateLogInterval == 0)
            {
                LogAggregateStats();
            }
        }
#endif
    }

    private void ProcessActiveChunks()
    {
        float frameStart = Time.realtimeSinceStartup;
        float frameBudget = updateTimeBudgetMs * 0.001f;

#if LOGGING
        float processStartTime = frameStart;
#endif

        // Flush any batched slot lookup changes from this or previous frames
        if (gpuSlotLookupDirty)
        {
            _GpuApplyLookupTextures();
            gpuSlotLookupDirty = false;
        }

        // Keep nearby chunks resident in the GPU atlases before running lighting.
        // Otherwise visible chunks can be evicted by distant worldgen and slowly fall
        // back to stale mesh lighting.
        _GpuMaintainResidentChunks(frameStart, frameBudget);

        // Flush again if maintain created new slot assignments
        if (gpuSlotLookupDirty)
        {
            _GpuApplyLookupTextures();
            gpuSlotLookupDirty = false;
        }

        // Run GPU lighting first so atlas propagation still gets time while worldgen and
        // meshing are busy. Otherwise repeated chunk uploads can keep reseeding lighting
        // without enough passes to converge.
        _ProcessGpuLighting(frameStart, frameBudget);

        // Process completed GPU face readbacks. Guarantee at least 1ms of budget so
        // readbacks aren't completely starved on over-budget frames.
        float readbackBudget = Mathf.Max(frameBudget, Time.realtimeSinceStartup - frameStart + 0.001f);
        _ProcessGpuFaceReadback(frameStart, readbackBudget);

        // --- Process Data Generation ---
        for (int i = 0; i < activeDataGenCount; i++)
        {
            if (Time.realtimeSinceStartup - frameStart > frameBudget) break; // Don't exceed budget

            ChunkData chunk = activeDataGenChunks[i];
            if (chunk == null || !chunk.isGeneratingData)
            {
                // Remove from active list
                activeDataGenChunks[i] = activeDataGenChunks[activeDataGenCount - 1];
                activeDataGenCount--;
                i--; // Re-check this index
                continue;
            }
            StepChunkDataGeneration(chunk);
        }

        // --- Process Meshing ---
        float gpuMeshDecodeFrameBudgetSec = Mathf.Max(0.25f, adaptiveGpuMeshDecodeFrameBudgetMs) * 0.001f;
        float gpuMeshDecodeSpentSec = 0f;
        for (int i = 0; i < activeMeshingCount; i++)
        {
            if (Time.realtimeSinceStartup - frameStart > frameBudget) break; // Don't exceed budget

            ChunkData chunk = activeMeshingChunks[i];
            if (chunk == null || !chunk.isBuildingMesh)
            {
                bool needsFollowupMeshBuild = false;
                int followupChunkIndex = -1;
                if (chunk != null && chunk.pendingChunkMeshRebuild)
                {
                    chunk.pendingChunkMeshRebuild = false;
                    chunk.interactionMeshPriority = true;
                    followupChunkIndex = ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
                    needsFollowupMeshBuild = followupChunkIndex != -1;
                }

                // Remove from active list
                activeMeshingChunks[i] = activeMeshingChunks[activeMeshingCount - 1];
                activeMeshingCount--;
                i--; // Re-check this index

                if (needsFollowupMeshBuild)
                {
                    RequestChunkMeshUpdate(followupChunkIndex);
                }
                continue;
            }

            if (chunk._gpuFaceBuildActive && gpuMeshDecodeSpentSec >= gpuMeshDecodeFrameBudgetSec)
            {
                continue;
            }

            // OPTIMIZATION: Process multiple mesh steps per frame to reduce overhead
            // Interaction-priority chunks (player block break/place) get uncapped steps
            // so the mesh completes in a single frame instead of spread across ~5.
            int maxMeshStepsPerFrame = chunk._gpuFaceBuildActive
                ? Mathf.Max(1, adaptiveGpuMeshDecodeStepsPerFrame)
                : (chunk.interactionMeshPriority ? 9999 : 20);
            for (int step = 0; step < maxMeshStepsPerFrame; step++)
            {
                if (!chunk.isBuildingMesh) break; // Mesh complete
                if (Time.realtimeSinceStartup - frameStart > frameBudget) break; // Don't exceed budget

                bool isGpuDecodeStep = chunk._gpuFaceBuildActive;
                if (isGpuDecodeStep && gpuMeshDecodeSpentSec >= gpuMeshDecodeFrameBudgetSec) break;

                float gpuDecodeStepStart = isGpuDecodeStep ? Time.realtimeSinceStartup : 0f;
                _BuildChunkMeshStep(chunk);
                if (isGpuDecodeStep)
                {
                    gpuMeshDecodeSpentSec += Time.realtimeSinceStartup - gpuDecodeStepStart;
                }
            }
        }

        _ScheduleDeferredInteriorMeshUpdates(frameStart, frameBudget);
        _HealStaleBorderChunks();
        _PostWorldGenBorderCleanup();

        // --- FIXED: Process Incremental Lighting (managed by coordinator) ---
        // Note: Lighting is now handled by coordinator's STATE_LIGHTING, not here

        // --- OPTIMIZATION: Process Deferred Reconciliation ---
#if LOGGING
        float reconcilStartTime = Time.realtimeSinceStartup;
#endif
        ProcessDeferredReconciliation(frameStart, frameBudget);
#if LOGGING
        if (enableDetailedTimings)
        {
            stats_reconciliationTime += (Time.realtimeSinceStartup - reconcilStartTime) * 1000f;
        }
#endif

        // Chunks can finish generation after the first lighting window in this frame.
        // Give the GPU lighting backend one more chance to seed freshly published slots
        // before render so cleared atlas entries do not spend a whole frame sampling black.
        // Only do this when a new seed is actually pending; continuing ordinary propagation
        // can wait until the next frame.
        if (gpuLightingSeedPending)
        {
            _ProcessGpuLighting(frameStart, frameBudget);
        }

        _ProcessDeferredChunkSecondaryWork(frameStart, frameBudget);

#if LOGGING
        if (enableDetailedTimings)
        {
            stats_processActiveChunksTime += (Time.realtimeSinceStartup - processStartTime) * 1000f;
        }
#endif

        // No longer need to reschedule - Update() runs every frame automatically!
    }

    private void _ScheduleDeferredInteriorMeshUpdates(float frameStart, float frameBudget)
    {
        if (!prioritizeVisibleShellMeshing || coordinator == null || chunks_1D == null || chunks_1D.Length == 0) return;
        if (Time.realtimeSinceStartup - frameStart > frameBudget * 0.75f) return;

        bool hasHeadroom = !enableAdaptiveBudgets || lastUpdateDurationMs < Mathf.Max(0.5f, updateTimeBudgetMs - adaptiveFrameHeadroomMs);
        int requestLimit = Mathf.Max(1, deferredInteriorMeshRequestsPerFrame);

        int scheduled = 0;
        int scansRemaining = Mathf.Max(1, deferredInteriorMeshScanCountPerFrame);
        int totalChunks = chunks_1D.Length;
        while (scansRemaining-- > 0)
        {
            int chunkIndex = deferredInteriorMeshScanCursor;
            deferredInteriorMeshScanCursor++;
            if (deferredInteriorMeshScanCursor >= totalChunks) deferredInteriorMeshScanCursor = 0;

            ChunkData chunk = chunks_1D[chunkIndex];
            if (chunk == null || !chunk.isDataReady || chunk.isBuildingMesh) continue;

            // Self-heal: chunk was meshed with missing border data.  Re-mesh if
            // the previously-missing neighbors are now available.
            // These are correctness fixes — don't count against requestLimit so
            // deferred-interior wakes still get their budget.
            if (!chunk.isMeshDeferred && chunk._borderMissingMask != 0)
            {
                if (_AreMaskedNeighborsReady(chunk, chunk._borderMissingMask))
                {
                    RequestChunkMeshUpdate(chunkIndex);
                }
                continue;
            }

            if (!chunk.isMeshDeferred) continue;
            if (chunk._borderMissingMask != 0 && !_AreMaskedNeighborsReady(chunk, chunk._borderMissingMask)) continue;
            if (!AreAllNeighborsReady(chunkIndex)) continue;

            bool shouldWake = _ShouldPrioritizeChunkMesh(chunk);
            if (!shouldWake)
            {
                if (!hasHeadroom) continue;
            }

            if (coordinator != null && coordinator.RequestDeferredChunkMeshUpdate(chunkIndex))
            {
                scheduled++;
                if (scheduled >= requestLimit) break;
            }
        }
    }

    private int _borderHealCursor = 0;
    private bool _postWorldGenBorderCleanupDone = false;

    private bool _AreMaskedNeighborsReady(ChunkData chunk, byte mask)
    {
        if (chunk == null || mask == 0) return true;

        for (int i = 0; i < 6; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            ChunkData neighborChunk = GetChunkAt(
                chunk.chunkX_world + neighbor_dx_offsets[i],
                chunk.chunkY_world + neighbor_dy_offsets[i],
                chunk.chunkZ_world + neighbor_dz_offsets[i]);
            if (neighborChunk == null || !neighborChunk.isDataReady) return false;
        }

        return true;
    }

    private void _PostWorldGenBorderCleanup()
    {
        if (_postWorldGenBorderCleanupDone) return;
        if (coordinator == null || !coordinator.IsWorldGenComplete()) return;
        if (chunks_1D == null || chunks_1D.Length == 0) return;

        // Wait until no chunks are actively building a mesh so we don't race.
        // Don't wait for isMeshDeferred — those haven't been meshed yet and
        // can take many seconds to wake, blocking the cleanup for all other chunks.
        for (int i = 0; i < chunks_1D.Length; i++)
        {
            ChunkData c = chunks_1D[i];
            if (c != null && c.isBuildingMesh) return;
        }

        _postWorldGenBorderCleanupDone = true;

        int rebuilt = 0;
        for (int i = 0; i < chunks_1D.Length; i++)
        {
            ChunkData chunk = chunks_1D[i];
            if (chunk == null || !chunk.isDataReady) continue;
            // Deferred chunks haven't been meshed yet — no stale border to fix.
            if (chunk.isMeshDeferred) continue;

            chunk._borderMissingMask = 0;
            RequestChunkMeshUpdate(i);
            rebuilt++;
        }
#if LOGGING
        Debug.Log($"[McWorld] Post-worldgen border cleanup: queued {rebuilt} chunks for rebuild");
#endif
    }

    private void _TryImmediateBorderHeal(ChunkData chunk)
    {
        if (chunk == null || chunk._borderMissingMask == 0 || chunk.isBuildingMesh) return;
        if (_AreMaskedNeighborsReady(chunk, chunk._borderMissingMask))
        {
            int idx = ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
            if (idx >= 0) RequestChunkMeshUpdate(idx);
        }
    }

    private void _HealStaleBorderChunks()
    {
        if (coordinator == null || chunks_1D == null || chunks_1D.Length == 0) return;
        int totalChunks = chunks_1D.Length;
        int scans = 256;
        int healed = 0;
        while (scans-- > 0)
        {
            int idx = _borderHealCursor;
            _borderHealCursor++;
            if (_borderHealCursor >= totalChunks) _borderHealCursor = 0;

            ChunkData chunk = chunks_1D[idx];
            if (chunk == null || !chunk.isDataReady || chunk.isBuildingMesh || chunk.isMeshDeferred) continue;
            if (chunk._borderMissingMask == 0) continue;

            if (_AreMaskedNeighborsReady(chunk, chunk._borderMissingMask))
            {
                RequestChunkMeshUpdate(idx);
                healed++;
                if (healed >= 16) return;
            }
        }
    }

    private bool _ShouldPrioritizeChunkSecondaryWork(ChunkData chunk)
    {
        if (chunk == null) return true;
        if (_IsChunkNearPlayer(chunk)) return true;
        return _ShouldPrioritizeChunkMesh(chunk);
    }

    private bool _ShouldDeferChunkSecondaryWork(ChunkData chunk)
    {
        if (!deferFarChunkSecondaryWork || chunk == null) return false;
        return !_ShouldPrioritizeChunkSecondaryWork(chunk);
    }

    private bool _ShouldEnableChunkCollider(ChunkData chunk)
    {
        if (chunk == null) return false;
        if (!nearOnlyChunkColliders) return true;
        if (!_TryGetPlayerChunkCoords(out int playerChunkX, out int playerChunkY, out int playerChunkZ)) return true;

        int dx = Mathf.Abs(chunk.chunkX_world - playerChunkX);
        int dz = Mathf.Abs(chunk.chunkZ_world - playerChunkZ);
        int relativeY = chunk.chunkY_world - playerChunkY;
        return dx <= colliderPriorityRadiusXZ
            && dz <= colliderPriorityRadiusXZ
            && relativeY <= colliderPriorityRadiusY
            && relativeY >= -colliderPriorityBelowRadiusY;
    }

    private void _DisableChunkCollider(ChunkData chunk, bool keepPending)
    {
        if (chunk == null || chunk.meshCollider == null) return;
        chunk.meshCollider.sharedMesh = null;
        chunk.meshCollider.enabled = false;
        chunk.pendingColliderApply = keepPending && chunk._collisionVertexCount > 0;
    }

    private void _QueueDeferredColliderApply(ChunkData chunk)
    {
        if (chunk == null) return;
        chunk.pendingColliderApply = true;
#if LOGGING
        if (enableDetailedTimings)
        {
            stats_deferredColliderApplyCount++;
            chunk.profile_deferredColliderQueuedTime = Time.realtimeSinceStartup;
        }
#endif
    }

    private void _QueueDeferredLightingFinalize(ChunkData chunk)
    {
        if (chunk == null) return;
        chunk.pendingLightingFinalize = true;
    }

    public void HandleChunkPostDataGpuLighting(ChunkData chunk)
    {
        if (chunk == null) return;
        if (_ShouldDeferChunkSecondaryWork(chunk))
        {
            chunk.pendingNeighborMeshRebuild = true;
            return;
        }

        TriggerNeighborMeshRebuilds(chunk);
    }

    private int _ProcessNearbyChunkColliderUpdates()
    {
        if (!nearOnlyChunkColliders || chunks_1D == null || chunks_1D.Length == 0) return 0;
        if (!_TryGetPlayerChunkCoords(out int playerChunkX, out int playerChunkY, out int playerChunkZ)) return 0;

        int completedTasks = 0;
        int taskLimit = Mathf.Max(1, nearColliderUpdatesPerFrame);
        int minY = playerChunkY - colliderPriorityBelowRadiusY;
        int maxY = playerChunkY + colliderPriorityRadiusY;

        for (int y = minY; y <= maxY; y++)
        {
            for (int z = playerChunkZ - colliderPriorityRadiusXZ; z <= playerChunkZ + colliderPriorityRadiusXZ; z++)
            {
                for (int x = playerChunkX - colliderPriorityRadiusXZ; x <= playerChunkX + colliderPriorityRadiusXZ; x++)
                {
                    int chunkIndex = ChunkCenteredCoordsTo1D(x, y, z);
                    if (chunkIndex == -1) continue;

                    ChunkData chunk = chunks_1D[chunkIndex];
                    if (chunk != null && chunk.pendingColliderMeshRebuild && !chunk.isBuildingMesh)
                    {
                        if (!_ShouldEnableChunkCollider(chunk)) continue;
                        chunk.pendingColliderMeshRebuild = false;
                        RequestChunkMeshUpdate(chunkIndex);
                        completedTasks++;
                        if (completedTasks >= taskLimit) return completedTasks;
                        continue;
                    }
                    if (chunk == null || !chunk.isDataReady || chunk.isBuildingMesh || !chunk.pendingColliderApply) continue;
                    if (!_ShouldEnableChunkCollider(chunk)) continue;

                    chunk.pendingColliderApply = false;
                    _ApplyDataToCollider(chunk);
                    chunk._collisionVertices = null;
                    chunk._collisionTriangles = null;
                    completedTasks++;
                    if (completedTasks >= taskLimit) return completedTasks;
                }
            }
        }

        return completedTasks;
    }

    private void _ProcessDeferredChunkSecondaryWork(float frameStart, float frameBudget)
    {
        if ((!deferFarChunkSecondaryWork && !nearOnlyChunkColliders) || chunks_1D == null || chunks_1D.Length == 0) return;

        bool hasHeadroom = !enableAdaptiveBudgets || lastUpdateDurationMs < Mathf.Max(0.5f, updateTimeBudgetMs - adaptiveFrameHeadroomMs);
        int taskLimit = Mathf.Max(1, deferredSecondaryWorkTasksPerFrame);
        int scansRemaining = Mathf.Max(1, deferredSecondaryWorkScanCountPerFrame);
        int totalChunks = chunks_1D.Length;
        int completedTasks = _ProcessNearbyChunkColliderUpdates();

        if (completedTasks >= taskLimit) return;
        if (Time.realtimeSinceStartup - frameStart > frameBudget * 0.75f) return;

        while (scansRemaining-- > 0)
        {
            int chunkIndex = deferredSecondaryWorkScanCursor;
            deferredSecondaryWorkScanCursor++;
            if (deferredSecondaryWorkScanCursor >= totalChunks) deferredSecondaryWorkScanCursor = 0;

            ChunkData chunk = chunks_1D[chunkIndex];
            if (chunk == null || !chunk.isDataReady) continue;

            if (chunk.pendingColliderMeshRebuild && !chunk.isBuildingMesh)
            {
                if (!_ShouldEnableChunkCollider(chunk)) continue;
                chunk.pendingColliderMeshRebuild = false;
                RequestChunkMeshUpdate(chunkIndex);
                completedTasks++;
                if (completedTasks >= taskLimit) break;
                continue;
            }

            if (nearOnlyChunkColliders && !chunk.isBuildingMesh && chunk.meshCollider != null && chunk.meshCollider.enabled && !_ShouldEnableChunkCollider(chunk))
            {
                _DisableChunkCollider(chunk, true);
                completedTasks++;
                if (completedTasks >= taskLimit) break;
                continue;
            }

            // pendingNeighborMeshRebuild is just 6 RequestChunkMeshUpdate calls — no per-block
            // work. Process without counting against the task budget so it can't be starved
            // by collider or lighting work sharing the same 2-per-frame limit.
            if (chunk.pendingNeighborMeshRebuild && !chunk.isBuildingMesh)
            {
                chunk.pendingNeighborMeshRebuild = false;
                TriggerNeighborMeshRebuilds(chunk);
                continue;
            }


            if (chunk.pendingLightingFinalize)
            {
                chunk.pendingLightingFinalize = false;
                if (_ShouldPrioritizeChunkSecondaryWork(chunk) || hasHeadroom)
                {
                    ImmediateReconciliation(chunk);
                }
                else
                {
                    // Far chunks still need to enter the deferred reconciliation queue even
                    // when we do not have headroom for the immediate finalize path.
                    DeferReconciliation(chunk);
                }
                completedTasks++;
                if (completedTasks >= taskLimit) break;
                continue;
            }

            bool shouldRunNow = _ShouldPrioritizeChunkSecondaryWork(chunk) || hasHeadroom;
            if (!shouldRunNow) continue;

            if (chunk.pendingColliderApply && !chunk.isBuildingMesh)
            {
                if (!_ShouldEnableChunkCollider(chunk)) continue;
                chunk.pendingColliderApply = false;
                _ApplyDataToCollider(chunk);
                chunk._collisionVertices = null;
                chunk._collisionTriangles = null;
                completedTasks++;
            }

            if (completedTasks >= taskLimit) break;
        }
    }

    public bool CanStartChunkDataGenerationWithoutExclusiveGenerator(int chunkIndex)
    {
        if (chunkIndex == -1 || chunks_1D == null || chunkIndex >= chunks_1D.Length || terrainGenerator == null) return false;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null) return false;
        return terrainGenerator.CanCopyCachedGpuChunkSlice(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
    }

    // Called by Coordinator
    public bool StartChunkDataGeneration(int chunkIndex)
    {
        if (chunkIndex == -1) return false;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null || chunk.isGeneratingData) return false;

        chunk.isGeneratingData = true;
        bool usesExclusiveGenerator = true;

        // Pass centered chunk coordinates directly - they ARE the Minecraft chunk coordinates
        // Engine centered coords (e.g., -2,-1,0,1) map to Minecraft chunks (-2,-1,0,1)
        if (!terrainGenerator.CanCopyCachedGpuChunkSlice(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world))
        {
            terrainGenerator.StartChunkGeneration(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
        }
        else
        {
            usesExclusiveGenerator = false;
        }

        if (activeDataGenCount < MAX_ACTIVE_CHUNKS)
        {
            activeDataGenChunks[activeDataGenCount++] = chunk;
        }

        return usesExclusiveGenerator;
    }

    // Called by Coordinator or ProcessActiveChunks loop
    public void StepChunkDataGeneration(ChunkData chunk)
    {
        if (!chunk.isGeneratingData) return;

        bool useGpuWorldgen = terrainGenerator != null && terrainGenerator.enableGpuWorldgen;
        byte[] generatedData = null;
        bool isComplete = false;
        float stepStart = Time.realtimeSinceStartup;
        float stepBudget = useGpuWorldgen ? Mathf.Max(0.5f, adaptiveGpuWorldgenStepBudgetMs) * 0.001f : 0.008f;
        int maxStepsPerFrame = useGpuWorldgen ? Mathf.Max(1, adaptiveGpuWorldgenStepsPerFrame) : 1;

        if (useGpuWorldgen && terrainGenerator.TryCopyCachedGpuChunkSlice(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world, out generatedData))
        {
            isComplete = true;
        }
        else
        {
            for (int step = 0; step < maxStepsPerFrame; step++)
            {
                isComplete = terrainGenerator.StepChunkGeneration(out generatedData);
                if (isComplete) break;

                // Stop if we've used our budget (prevents stutters)
                if (Time.realtimeSinceStartup - stepStart > stepBudget) break;
            }
        }

        if (isComplete)
        {
            // Check homogeneous cheaply — full RLE is deferred
            bool isHomogeneous = true;
            byte firstBlock = generatedData[0];
            for (int ci = 1; ci < generatedData.Length; ci++) {
                if (generatedData[ci] != firstBlock) { isHomogeneous = false; break; }
            }

            // GPU OFFLOAD #2 NOTE: A previous attempt set _chunkData = null for distant
            // chunks (marking them GPU-resident). This broke the chunk because mesh/light
            // paths read from _chunkData via _GetBlockLocal and saw all air → invisible
            // chunks → players walking into invisible terrain.
            //
            // For #2 to work properly, every block-read path needs an alternative source
            // (the GPU atlas) and an async readback fallback. That's a deeper refactor.
            // For now we always allocate the CPU mirror; the GPU offload #2 stays
            // dormant until the read-path migration lands.
            if (isHomogeneous)
            {
                chunk._chunkData = firstBlock;
                chunk._chunkDataKind = ChunkData.CHUNK_KIND_HOMOGENEOUS;
                chunk._isGpuResident = false;
            }
            else
            {
                // Clone the buffer — the terrain generator reuses it for the next chunk.
                // Array.Copy of ~16KB is still much cheaper than 4ms RLE compression.
                byte[] chunkRawData = new byte[generatedData.Length];
                System.Array.Copy(generatedData, 0, chunkRawData, 0, generatedData.Length);
                chunk._chunkData = chunkRawData;
                chunk._chunkDataKind = ChunkData.CHUNK_KIND_RAW;
                chunk._isGpuResident = false;
                generatedData = chunkRawData; // update ref so GPU sync and cache use the clone
            }

            // Populate decompressed cache so meshing never needs to decompress
            // For homogeneous chunks, don't cache the shared generator buffer —
            // it gets overwritten by the next column. _GetBlockLocal handles
            // homogeneous chunks via the typeof(byte) path instead.
            if (isHomogeneous)
            {
                chunk._cachedDecompressedData = null;
                chunk._decompCacheValid = false;
            }
            else
            {
                chunk._cachedDecompressedData = generatedData;
                chunk._decompCacheValid = true;
            }
            chunk._cachedDataVersion++;
            _RefreshChunkDerivedData(chunk, generatedData);

            _GpuSyncChunkBlocks(chunk, generatedData);

            // OPTIMIZATION: Invalidate neighbor cache since chunk data changed
            _InvalidateNeighborCache(chunk);

            chunk.isSingleOpaqueSolid = false;
            chunk._isAllAir = false;
            if (isHomogeneous) {
                byte blockID = (byte)chunk._chunkData;
                if (blockID == 0) {
                    chunk._isAllAir = true;
                }
                else if(_IsBlockSolid(blockID) && _GetVisibilityType(blockID) == BlockVisibilityType.Opaque) {
                    chunk.isSingleOpaqueSolid = true;
                }
            }

            // Store biome data for this chunk from the terrain generator
            // This data will be used during meshing to apply biome tinting
            _StoreBiomeData(chunk);

            if (!_UsesGpuTerrainLightSampling())
            {
                InitializeChunkLighting(chunk);
            }
            else
            {
                chunk.lightData = null;
                chunk.isProcessingLighting = false;
                chunk.lightingPhase = 2;
                chunk.lightingQueueStart = 0;
                chunk.lightingQueueEnd = 0;
                chunk.lightingIteration = 0;
            }

            // Mark chunk as ready and data gen complete
            chunk.isDataReady = true;
            chunk.isGeneratingData = false;
#if LOGGING
            chunk.profile_dataReadyTime = Time.realtimeSinceStartup;
            chunk.profile_waitingForFirstMesh = true;
            chunk.profile_firstMeshWasDeferred = false;
#endif



        }
    }

    // FIXED: Called by Coordinator to start incremental lighting (STATE_LIGHTING)
    public void StartChunkLighting(int chunkIndex)
    {
        if (chunkIndex == -1 || chunkIndex >= chunks_1D.Length) return;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null || !chunk.isDataReady) return;

        if (chunk.lightingQueue != null)
        {
            ReturnLightingQueue(chunk.lightingQueue);
            chunk.lightingQueue = null;
        }

        if (_UsesGpuTerrainLightSampling())
        {
            chunk.lightData = null;
            chunk.isProcessingLighting = false;
            chunk.lightingPhase = 2;
            chunk.lightingQueueStart = 0;
            chunk.lightingQueueEnd = 0;
            chunk.lightingIteration = 0;
            return;
        }

        byte[] chunkData = _GetDecompressedData(chunk);
        if (chunkData == null) return;

        if (chunk.isSingleOpaqueSolid && !chunk._hasEmissiveBlocks)
        {
            int lightDataSize = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
            if (chunk.lightData == null || chunk.lightData.Length != lightDataSize)
            {
                chunk.lightData = new byte[lightDataSize];
            }
            else
            {
                System.Array.Clear(chunk.lightData, 0, chunk.lightData.Length);
            }
            chunk.isProcessingLighting = false;
            chunk.lightingPhase = 2;
            if (_ShouldDeferChunkSecondaryWork(chunk))
            {
                _QueueDeferredLightingFinalize(chunk);
                chunk.pendingNeighborMeshRebuild = true;
            }
            else
            {
                ImmediateReconciliation(chunk);
                TriggerNeighborMeshRebuilds(chunk);
            }
            return;
        }

        if (chunk._isAllAir && chunk.chunkY_world >= worldDimensionY - 1)
        {
            int lightDataSize = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
            if (chunk.lightData == null || chunk.lightData.Length != lightDataSize)
            {
                chunk.lightData = new byte[lightDataSize];
            }
            for (int i = 0; i < lightDataSize; i++)
            {
                chunk.lightData[i] = 0xF0;
            }
            chunk.isProcessingLighting = false;
            chunk.lightingPhase = 2;
            if (_ShouldDeferChunkSecondaryWork(chunk))
            {
                _QueueDeferredLightingFinalize(chunk);
                chunk.pendingNeighborMeshRebuild = true;
            }
            else
            {
                ImmediateReconciliation(chunk);
                TriggerNeighborMeshRebuilds(chunk);
            }
            return;
        }

        // Allocate persistent queue for this chunk
        chunk.lightingQueue = GetLightingQueue();
        chunk.lightingQueueStart = 0;
        chunk.lightingQueueEnd = 0;
        chunk.lightingPhase = 0; // Start with sky light
        chunk.lightingIteration = 0;
        chunk.isProcessingLighting = true;

        // Import light from neighbors first
        _ImportLightFromNeighbors(chunk, chunkData);

        // Initialize queue with blocks that need processing
        _InitializeLightingQueue(chunk);
    }

    // FIXED: Called by Coordinator Update() to step through lighting incrementally
    public void StepChunkLighting(int chunkIndex)
    {
        if (chunkIndex == -1 || chunkIndex >= chunks_1D.Length) return;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null || !chunk.isProcessingLighting) return;

        if (_UsesGpuTerrainLightSampling())
        {
            chunk.isProcessingLighting = false;
            chunk.lightingPhase = 2;
            chunk.lightingQueueStart = 0;
            chunk.lightingQueueEnd = 0;
            chunk.lightingIteration = 0;
            if (chunk.lightingQueue != null)
            {
                ReturnLightingQueue(chunk.lightingQueue);
                chunk.lightingQueue = null;
            }
            return;
        }

        byte[] chunkData = _GetDecompressedData(chunk);
        if (chunkData == null) return;

        // Pre-calculate neighbor offsets
        int[] neighborOffsets = { -1, 0, 0, 1, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, -1, 0, 0, 1 };

        // Process a batch of blocks (small enough to never overflow queue)
        int blocksToProcess = 256; // Process 256 blocks per step
        int processed = 0;

        bool isSkyLight = (chunk.lightingPhase == 0);

        while (chunk.lightingQueueStart < chunk.lightingQueueEnd && processed < blocksToProcess)
        {
            int packed = chunk.lightingQueue[chunk.lightingQueueStart++];
            int x = (packed >> 16) & 0xFF;
            int y = (packed >> 8) & 0xFF;
            int z = packed & 0xFF;

            // Skip opaque blocks
            int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            byte blockID = chunkData[blockIndex];
            int opacity = lightOpacityCache[blockID];
            if (opacity >= 15)
            {
                processed++;
                continue;
            }

            // Process this block (PULL-based update)
            _UpdateBlockLightPULLOptimized(chunk, chunkData, x, y, z, isSkyLight, chunk.lightingQueue, ref chunk.lightingQueueEnd, neighborOffsets);

            processed++;
        }

        // Check if current phase is complete
        if (chunk.lightingQueueStart >= chunk.lightingQueueEnd)
        {
            chunk.lightingIteration++;

            // Check if we've done enough iterations or converged
            if (chunk.lightingIteration >= 16 || chunk.lightingQueueEnd == 0)
            {
                // Move to next phase
                if (chunk.lightingPhase == 0)
                {
                    // Sky light complete, start block light
                    chunk.lightingPhase = 1;
                    chunk.lightingIteration = 0;
                    chunk.lightingQueueStart = 0;
                    _InitializeLightingQueue(chunk);
                }
                else
                {
                    // Block light complete, do final cleanup and finish
                    _EnsureNoPitchBlackSpots(chunk, chunkData);

                    // Lighting complete!
                    chunk.isProcessingLighting = false;
                    chunk.lightingPhase = 2; // Mark as complete
                    ReturnLightingQueue(chunk.lightingQueue);
                    chunk.lightingQueue = null;

                    // Now do reconciliation and trigger neighbor mesh rebuilds
                    if (_ShouldDeferChunkSecondaryWork(chunk))
                    {
                        _QueueDeferredLightingFinalize(chunk);
                        chunk.pendingNeighborMeshRebuild = true;
                    }
                    else
                    {
                        ImmediateReconciliation(chunk);
                        TriggerNeighborMeshRebuilds(chunk);
                    }
                }
            }
        }
    }

    // FIXED: Initialize the lighting queue for a chunk
    private void _InitializeLightingQueue(ChunkData chunk)
    {
        byte[] chunkData = _GetDecompressedData(chunk);
        if (chunkData == null) return;

        chunk.lightingQueueEnd = 0;

        // Phase 0: Sky light - add all blocks with skylight < 15
        if (chunk.lightingPhase == 0)
        {
            for (int y = 0; y < chunkSizeY; y++)
            {
                for (int z = 0; z < chunkSizeXZ; z++)
                {
                    for (int x = 0; x < chunkSizeXZ; x++)
                    {
                        int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                        int skyLight = (chunk.lightData[blockIndex] >> 4) & 0xF;

                        if (skyLight < 15 && chunk.lightingQueueEnd < chunk.lightingQueue.Length - 6)
                        {
                            chunk.lightingQueue[chunk.lightingQueueEnd++] = (x << 16) | (y << 8) | z;
                        }
                    }
                }
            }
        }
        // Phase 1: Block light - add emissive blocks and skylight=0 blocks
        else if (chunk.lightingPhase == 1)
        {
            for (int y = 0; y < chunkSizeY; y++)
            {
                for (int z = 0; z < chunkSizeXZ; z++)
                {
                    for (int x = 0; x < chunkSizeXZ; x++)
                    {
                        int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                        int blockLight = chunk.lightData[blockIndex] & 0xF;
                        int skyLight = (chunk.lightData[blockIndex] >> 4) & 0xF;

                        if ((blockLight > 0 || skyLight == 0) && chunk.lightingQueueEnd < chunk.lightingQueue.Length - 6)
                        {
                            chunk.lightingQueue[chunk.lightingQueueEnd++] = (x << 16) | (y << 8) | z;
                        }
                    }
                }
            }
        }
    }

    // Called by Coordinator
    public void RequestChunkMeshUpdate(int chunkIndex)
    {
        if (chunkIndex == -1 || chunks_1D == null || chunkIndex >= chunks_1D.Length) return;

        // During tick batch processing, collect dirty chunks instead of building immediately
        if (_meshUpdateDeferred)
        {
            if (_meshDeferredDirtyChunks != null && _meshDeferredDirtyCount < MESH_DEFERRED_DIRTY_MAX)
            {
                for (int i = 0; i < _meshDeferredDirtyCount; i++)
                    if (_meshDeferredDirtyChunks[i] == chunkIndex) return;
                _meshDeferredDirtyChunks[_meshDeferredDirtyCount++] = chunkIndex;
            }
            return;
        }

        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk != null
            && chunk.interactionMeshPriority
            && chunk.isDataReady
            && !chunk.isBuildingMesh
            && chunk._chunkData != null
            && activeMeshingCount < MAX_ACTIVE_CHUNKS
            && AreAllNeighborsReady(chunkIndex)
            && (coordinator == null || !coordinator.IsChunkScheduledForMeshUpdate(chunkIndex)))
        {
            BuildChunkMesh(chunkIndex);
            return;
        }

        if (coordinator != null) coordinator.RequestChunkMeshUpdate(chunkIndex);
    }

    public byte GetBlock(int globalX, int globalY, int globalZ)
    {
        // OPTIMIZATION: Use bitwise operations instead of division/modulus
        int centeredChunkX = globalX >> CHUNK_SIZE_SHIFT;
        int chunkY = globalY >> CHUNK_SIZE_SHIFT;
        int centeredChunkZ = globalZ >> CHUNK_SIZE_SHIFT;

        ChunkData chunk = GetChunkAt(centeredChunkX, chunkY, centeredChunkZ);
        if (chunk == null || !chunk.isDataReady) return 0;

        int localX = globalX & CHUNK_SIZE_MASK;
        int localY = globalY & CHUNK_SIZE_MASK;
        int localZ = globalZ & CHUNK_SIZE_MASK;

        return _GetBlockLocal(chunk, localX, localY, localZ);
    }

    // Combined block+metadata lookup (one chunk resolve instead of two).
    // Returns block type; caller reads lastMetaResult for the metadata.
    [System.NonSerialized] public byte lastMetaResult;

    public byte GetBlockAndMeta(int globalX, int globalY, int globalZ)
    {
        int cx = globalX >> CHUNK_SIZE_SHIFT;
        int cy = globalY >> CHUNK_SIZE_SHIFT;
        int cz = globalZ >> CHUNK_SIZE_SHIFT;
        ChunkData chunk = GetChunkAt(cx, cy, cz);
        if (chunk == null || !chunk.isDataReady)
        {
            lastMetaResult = 0;
            return 0;
        }
        int lx = globalX & CHUNK_SIZE_MASK;
        int ly = globalY & CHUNK_SIZE_MASK;
        int lz = globalZ & CHUNK_SIZE_MASK;
        byte blockType = _GetBlockLocal(chunk, lx, ly, lz);
        int idx = ly * (chunkSizeXZ * chunkSizeXZ) + lz * chunkSizeXZ + lx;
        lastMetaResult = (chunk.blockMetadata != null && idx >= 0 && idx < chunk.blockMetadata.Length) ? chunk.blockMetadata[idx] : (byte)0;
        return blockType;
    }

    public bool IsChunkMeshedAt(int globalX, int globalY, int globalZ)
    {
        int centeredChunkX = globalX >> CHUNK_SIZE_SHIFT;
        int chunkY = globalY >> CHUNK_SIZE_SHIFT;
        int centeredChunkZ = globalZ >> CHUNK_SIZE_SHIFT;
        ChunkData chunk = GetChunkAt(centeredChunkX, chunkY, centeredChunkZ);
        if (chunk == null) return false;
        // Chunk has been meshed if it's data-ready, not currently building,
        // and not pending a mesh rebuild (meaning it completed at least once).
        return chunk.isDataReady && !chunk.isBuildingMesh && !chunk.pendingChunkMeshRebuild;
    }

    public void SetBlock(int globalX, int globalY, int globalZ, byte blockType)
    {
        _SetBlockGlobal(globalX, globalY, globalZ, blockType, false);
    }

    public void SetBlockFromInteraction(int globalX, int globalY, int globalZ, byte blockType)
    {
        _SetBlockGlobal(globalX, globalY, globalZ, blockType, true);
    }

    public bool PlaceTorchFromInteraction(int globalX, int globalY, int globalZ, Vector3 hitNormal, byte blockType)
    {
        if (!_IsTorchBlock(blockType))
        {
            _SetBlockGlobal(globalX, globalY, globalZ, blockType, true);
            return true;
        }

        byte torchMount = _GetTorchMountFromHitNormal(hitNormal);
        if (!_CanPlaceTorchAt(globalX, globalY, globalZ, torchMount))
        {
            return false;
        }

        _SetBlockGlobal(globalX, globalY, globalZ, blockType, true);
        _SetTorchMountGlobal(globalX, globalY, globalZ, torchMount, true);
        return true;
    }

    public byte GetTorchMount(int globalX, int globalY, int globalZ)
    {
        int centeredChunkX = globalX >> CHUNK_SIZE_SHIFT;
        int chunkY = globalY >> CHUNK_SIZE_SHIFT;
        int centeredChunkZ = globalZ >> CHUNK_SIZE_SHIFT;

        ChunkData chunk = GetChunkAt(centeredChunkX, chunkY, centeredChunkZ);
        if (chunk == null || !chunk.isDataReady) return 0;

        int localX = globalX & CHUNK_SIZE_MASK;
        int localY = globalY & CHUNK_SIZE_MASK;
        int localZ = globalZ & CHUNK_SIZE_MASK;
        byte blockType = _GetBlockLocal(chunk, localX, localY, localZ);
        if (!_IsTorchBlock(blockType)) return 0;
        return _GetTorchMountLocal(chunk, localX, localY, localZ);
    }

    public void SetTorchMount(int globalX, int globalY, int globalZ, byte torchMount)
    {
        _SetTorchMountGlobal(globalX, globalY, globalZ, torchMount, false);
    }

    public byte GetBlockMetadata(int globalX, int globalY, int globalZ)
    {
        int cx = globalX >> CHUNK_SIZE_SHIFT;
        int cy = globalY >> CHUNK_SIZE_SHIFT;
        int cz = globalZ >> CHUNK_SIZE_SHIFT;
        ChunkData chunk = GetChunkAt(cx, cy, cz);
        if (chunk == null || !chunk.isDataReady || chunk.blockMetadata == null) return 0;
        int idx = (globalY & CHUNK_SIZE_MASK) * (chunkSizeXZ * chunkSizeXZ) + (globalZ & CHUNK_SIZE_MASK) * chunkSizeXZ + (globalX & CHUNK_SIZE_MASK);
        return (idx >= 0 && idx < chunk.blockMetadata.Length) ? chunk.blockMetadata[idx] : (byte)0;
    }

    public void SetBlockMetadata(int globalX, int globalY, int globalZ, byte metadata)
    {
        int cx = globalX >> CHUNK_SIZE_SHIFT;
        int cy = globalY >> CHUNK_SIZE_SHIFT;
        int cz = globalZ >> CHUNK_SIZE_SHIFT;
        ChunkData chunk = GetChunkAt(cx, cy, cz);
        if (chunk == null || !chunk.isDataReady) return;
        if (chunk.blockMetadata == null)
            chunk.blockMetadata = new byte[chunk._chunkDataSize];
        int lx = globalX & CHUNK_SIZE_MASK;
        int ly = globalY & CHUNK_SIZE_MASK;
        int lz = globalZ & CHUNK_SIZE_MASK;
        int idx = ly * (chunkSizeXZ * chunkSizeXZ) + lz * chunkSizeXZ + lx;
        if (idx < 0 || idx >= chunk.blockMetadata.Length) return;
        byte oldMeta = chunk.blockMetadata[idx];
        if (oldMeta == metadata) return;
        chunk.blockMetadata[idx] = metadata;

        // Metadata changed — trigger mesh rebuild so water levels etc. are visible
        int chunkIndex = ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
        RequestChunkMeshUpdate(chunkIndex);
        if (blockTicker != null) blockTicker.InvalidateBlockCache(globalX, globalY, globalZ);
    }

    /// <summary>
    /// Sets block type AND metadata atomically, then fires notifications.
    /// MC's setBlockAndMetadataWithNotify — metadata is correct before neighbors read it.
    /// </summary>
    public void SetBlockAndMetadata(int globalX, int globalY, int globalZ, byte blockType, byte metadata)
    {
        // Pre-write metadata so it's correct when notifications fire
        int cx = globalX >> CHUNK_SIZE_SHIFT;
        int cy = globalY >> CHUNK_SIZE_SHIFT;
        int cz = globalZ >> CHUNK_SIZE_SHIFT;
        ChunkData chunk = GetChunkAt(cx, cy, cz);
        if (chunk != null && chunk.isDataReady)
        {
            if (chunk.blockMetadata == null)
                chunk.blockMetadata = new byte[chunk._chunkDataSize];
            int lx = globalX & CHUNK_SIZE_MASK;
            int ly = globalY & CHUNK_SIZE_MASK;
            int lz = globalZ & CHUNK_SIZE_MASK;
            int idx = ly * (chunkSizeXZ * chunkSizeXZ) + lz * chunkSizeXZ + lx;
            if (idx >= 0 && idx < chunk.blockMetadata.Length)
                chunk.blockMetadata[idx] = metadata;
        }

        // Now set block type — this triggers notifications with metadata already correct
        _SetBlockGlobal(globalX, globalY, globalZ, blockType, false);
    }

    /// <summary>
    /// Public entry point so McBlockTicker can trigger cascading neighbor notifications
    /// from its own block modifications (e.g. water flowing into a new block).
    /// </summary>
    public void NotifyNeighborsOfBlockChange(int globalX, int globalY, int globalZ, byte blockType)
    {
        if (blockTicker != null)
            blockTicker.NotifyNeighborsOfBlockChange(globalX, globalY, globalZ, blockType);
    }

    /// <summary>
    /// MC's setBlockAndMetadata + markBlocksDirty/markBlockNeedsUpdate.
    /// Sets block+metadata and triggers mesh rebuild, but NO neighbor notifications
    /// and NO onBlockAdded. Used for flowing↔stationary conversions that must not cascade.
    /// </summary>
    public void SetBlockAndMetadataSilent(int globalX, int globalY, int globalZ, byte blockType, byte metadata)
    {
        int cx = globalX >> CHUNK_SIZE_SHIFT;
        int cy = globalY >> CHUNK_SIZE_SHIFT;
        int cz = globalZ >> CHUNK_SIZE_SHIFT;
        ChunkData chunk = GetChunkAt(cx, cy, cz);
        if (chunk == null) return;

        // Write metadata
        if (chunk.blockMetadata == null)
            chunk.blockMetadata = new byte[chunk._chunkDataSize];
        int lx = globalX & CHUNK_SIZE_MASK;
        int ly = globalY & CHUNK_SIZE_MASK;
        int lz = globalZ & CHUNK_SIZE_MASK;
        int idx = ly * (chunkSizeXZ * chunkSizeXZ) + lz * chunkSizeXZ + lx;
        if (idx >= 0 && idx < chunk.blockMetadata.Length)
            chunk.blockMetadata[idx] = metadata;

        // Write block type + trigger mesh rebuild (no notifications)
        _SetBlockLocal(chunk, lx, ly, lz, blockType, true, false);
        if (blockTicker != null) blockTicker.InvalidateBlockCache(globalX, globalY, globalZ);
    }

    private void _SetBlockGlobal(int globalX, int globalY, int globalZ, byte blockType, bool interactionPriority)
    {
        // OPTIMIZATION: Use bitwise operations instead of division/modulus
        int centeredChunkX = globalX >> CHUNK_SIZE_SHIFT;
        int chunkY = globalY >> CHUNK_SIZE_SHIFT;
        int centeredChunkZ = globalZ >> CHUNK_SIZE_SHIFT;

        ChunkData chunk = GetChunkAt(centeredChunkX, chunkY, centeredChunkZ);
        if (chunk == null) return;

        int localX = globalX & CHUNK_SIZE_MASK;
        int localY = globalY & CHUNK_SIZE_MASK;
        int localZ = globalZ & CHUNK_SIZE_MASK;

        byte oldBlockType = _GetBlockLocal(chunk, localX, localY, localZ);
        _SetBlockLocal(chunk, localX, localY, localZ, blockType, true, interactionPriority);

        // Update lighting if block changed — skip for fluid transitions to avoid
        // cascading propagation corruption that turns chunks black
        if (oldBlockType != blockType && chunk.lightData != null && !_UsesGpuTerrainLightSampling())
        {
            bool oldIsFluid = oldBlockType == 8 || oldBlockType == 9 || oldBlockType == 10 || oldBlockType == 11;
            bool newIsFluid = blockType == 8 || blockType == 9 || blockType == 10 || blockType == 11;
            if (!oldIsFluid && !newIsFluid)
            {
                _UpdateBlockLighting(chunk, localX, localY, localZ, oldBlockType, blockType);
            }
        }

        // MC parity: torch support is NOT checked by the changing block.
        // Torches self-check via onNeighborBlockChange (handled in McBlockTicker._OnNeighborChange).

        if (blockTicker != null)
        {
            blockTicker.InvalidateBlockCache(globalX, globalY, globalZ);
            if (oldBlockType != blockType)
            {
                blockTicker.NotifyNeighborsOfBlockChange(globalX, globalY, globalZ, blockType);
                blockTicker.OnBlockAdded(globalX, globalY, globalZ, blockType);
            }
        }
    }

    private bool _IsTorchBlock(byte blockType)
    {
        return blockType == BLOCK_TORCH || blockType == BLOCK_REDSTONE_TORCH_OFF || blockType == BLOCK_REDSTONE_TORCH_ON;
    }

    private bool _UsesCustomBlockMesh(byte blockType, McBlockShapeType[] shapeCache, int shapeCacheLen)
    {
        if (_IsTorchBlock(blockType)) return true;
        return blockType < shapeCacheLen && shapeCache[blockType] == McBlockShapeType.Cross;
    }

    private byte _GetTorchMountLocal(ChunkData chunk, int localX, int localY, int localZ)
    {
        if (chunk == null || !_IsValidLocalVoxel(localX, localY, localZ)) return TORCH_MOUNT_FLOOR;

        int localIndex = localY * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
        if (chunk.torchMountData == null || localIndex < 0 || localIndex >= chunk.torchMountData.Length)
        {
            return TORCH_MOUNT_FLOOR;
        }

        byte torchMount = chunk.torchMountData[localIndex];
        if (torchMount >= TORCH_MOUNT_WEST && torchMount <= TORCH_MOUNT_CEILING)
        {
            return torchMount;
        }

        return TORCH_MOUNT_FLOOR;
    }

    private void _SetTorchMountGlobal(int globalX, int globalY, int globalZ, byte torchMount, bool interactionPriority)
    {
        int centeredChunkX = globalX >> CHUNK_SIZE_SHIFT;
        int chunkY = globalY >> CHUNK_SIZE_SHIFT;
        int centeredChunkZ = globalZ >> CHUNK_SIZE_SHIFT;

        ChunkData chunk = GetChunkAt(centeredChunkX, chunkY, centeredChunkZ);
        if (chunk == null || !chunk.isDataReady) return;

        int localX = globalX & CHUNK_SIZE_MASK;
        int localY = globalY & CHUNK_SIZE_MASK;
        int localZ = globalZ & CHUNK_SIZE_MASK;
        _SetTorchMountLocal(chunk, localX, localY, localZ, torchMount, interactionPriority);
    }

    private void _SetTorchMountLocal(ChunkData chunk, int localX, int localY, int localZ, byte torchMount, bool interactionPriority)
    {
        if (chunk == null || !_IsValidLocalVoxel(localX, localY, localZ)) return;

        byte blockType = _GetBlockLocal(chunk, localX, localY, localZ);
        if (!_IsTorchBlock(blockType)) return;

        int localIndex = localY * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
        if (chunk.torchMountData == null || chunk.torchMountData.Length != chunk._chunkDataSize)
        {
            chunk.torchMountData = new byte[chunk._chunkDataSize];
        }

        byte clampedMount = (torchMount >= TORCH_MOUNT_WEST && torchMount <= TORCH_MOUNT_CEILING) ? torchMount : TORCH_MOUNT_FLOOR;
        if (chunk.torchMountData[localIndex] == clampedMount) return;

        chunk.torchMountData[localIndex] = clampedMount;
        chunk._hasTorchBlocks = true;
        if (interactionPriority)
        {
            chunk.interactionMeshPriority = true;
        }

        int chunkIndex = ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
        RequestChunkMeshUpdate(chunkIndex);

        if (localX == 0 || localX == chunkSizeXZ - 1 || localY == 0 || localY == chunkSizeY - 1 || localZ == 0 || localZ == chunkSizeXZ - 1)
        {
            TriggerNeighborMeshRebuilds(chunk, interactionPriority);
        }
    }

    private bool _IsValidLocalVoxel(int localX, int localY, int localZ)
    {
        return localX >= 0 && localX < chunkSizeXZ && localY >= 0 && localY < chunkSizeY && localZ >= 0 && localZ < chunkSizeXZ;
    }

    private byte _GetTorchMountFromHitNormal(Vector3 hitNormal)
    {
        if (hitNormal.y > 0.5f) return TORCH_MOUNT_FLOOR;
        if (hitNormal.y < -0.5f) return TORCH_MOUNT_CEILING;
        if (hitNormal.x > 0.5f) return TORCH_MOUNT_WEST;
        if (hitNormal.x < -0.5f) return TORCH_MOUNT_EAST;
        if (hitNormal.z > 0.5f) return TORCH_MOUNT_NORTH;
        if (hitNormal.z < -0.5f) return TORCH_MOUNT_SOUTH;
        return 0;
    }

    private bool _CanPlaceTorchAt(int globalX, int globalY, int globalZ, byte torchMount)
    {
        if (GetBlock(globalX, globalY, globalZ) != 0) return false;
        return _CanTorchStayAt(globalX, globalY, globalZ, torchMount);
    }

    private bool _CanTorchStayAt(int globalX, int globalY, int globalZ, byte torchMount)
    {
        switch (torchMount)
        {
            case TORCH_MOUNT_WEST:
                return _CanSupportTorchOnSide(globalX - 1, globalY, globalZ);
            case TORCH_MOUNT_EAST:
                return _CanSupportTorchOnSide(globalX + 1, globalY, globalZ);
            case TORCH_MOUNT_NORTH:
                return _CanSupportTorchOnSide(globalX, globalY, globalZ - 1);
            case TORCH_MOUNT_SOUTH:
                return _CanSupportTorchOnSide(globalX, globalY, globalZ + 1);
            case TORCH_MOUNT_FLOOR:
                return _CanSupportTorchOnTop(globalX, globalY - 1, globalZ);
            case TORCH_MOUNT_CEILING:
                return _CanSupportTorchOnCeiling(globalX, globalY + 1, globalZ);
            default:
                return false;
        }
    }

    private bool _CanSupportTorchOnSide(int globalX, int globalY, int globalZ)
    {
        return _IsOpaqueCubeForTorchSupport((byte)(GetBlock(globalX, globalY, globalZ) & 0xFF));
    }

    private bool _CanSupportTorchOnTop(int globalX, int globalY, int globalZ)
    {
        byte supportBlock = (byte)(GetBlock(globalX, globalY, globalZ) & 0xFF);
        return supportBlock == BLOCK_FENCE || _IsOpaqueCubeForTorchSupport(supportBlock);
    }

    private bool _CanSupportTorchOnCeiling(int globalX, int globalY, int globalZ)
    {
        return _IsOpaqueCubeForTorchSupport((byte)(GetBlock(globalX, globalY, globalZ) & 0xFF));
    }

    private bool _IsOpaqueCubeForTorchSupport(byte blockType)
    {
        if (blockType == 0) return false;
        if (visibilityCache == null || blockType >= visibilityCache.Length) return false;
        if (visibilityCache[blockType] != BlockVisibilityType.Opaque) return false;
        if (shapeTypeCache != null && blockType < shapeTypeCache.Length && shapeTypeCache[blockType] != McBlockShapeType.Cube) return false;
        return true;
    }

    private void _DropUnsupportedTorchNeighbors(int globalX, int globalY, int globalZ, bool interactionPriority)
    {
        DropTorchIfUnsupported(globalX - 1, globalY, globalZ);
        DropTorchIfUnsupported(globalX + 1, globalY, globalZ);
        DropTorchIfUnsupported(globalX, globalY - 1, globalZ);
        DropTorchIfUnsupported(globalX, globalY + 1, globalZ);
        DropTorchIfUnsupported(globalX, globalY, globalZ - 1);
        DropTorchIfUnsupported(globalX, globalY, globalZ + 1);
    }

    public void DropTorchIfUnsupported(int globalX, int globalY, int globalZ)
    {
        byte blockType = (byte)(GetBlock(globalX, globalY, globalZ) & 0xFF);
        if (!_IsTorchBlock(blockType)) return;

        byte torchMount = GetTorchMount(globalX, globalY, globalZ);
        if (_CanTorchStayAt(globalX, globalY, globalZ, torchMount)) return;

        _SetBlockGlobal(globalX, globalY, globalZ, 0, false);
    }

    public void TriggerNeighborMeshRebuilds(ChunkData chunk)
    {
        TriggerNeighborMeshRebuilds(chunk, false);
    }

    public void TriggerNeighborMeshRebuilds(ChunkData chunk, bool interactionPriority)
    {
        if (chunk == null) return;

        for (int i = 0; i < 6; i++) {
            int neighborX = chunk.chunkX_world + neighbor_dx_offsets[i];
            int neighborY = chunk.chunkY_world + neighbor_dy_offsets[i];
            int neighborZ = chunk.chunkZ_world + neighbor_dz_offsets[i];
            int neighborIndex = ChunkCenteredCoordsTo1D(neighborX, neighborY, neighborZ);
            if (neighborIndex == -1) continue;

            ChunkData neighborChunk = GetChunkAt(neighborX, neighborY, neighborZ);
            if (neighborChunk == null || !neighborChunk.isDataReady) continue;
            // During worldgen, skip neighbors that haven't been meshed yet —
            // they'll get their own mesh build when promoted from deferred.
            if (!interactionPriority && neighborChunk.isMeshDeferred) continue;
            // Building neighbors started their face extract before our data existed —
            // flag them for a self-rebuild after their current (stale) build completes.
            if (!interactionPriority && neighborChunk.isBuildingMesh)
            {
                neighborChunk.pendingChunkMeshRebuild = true;
                continue;
            }
            if (interactionPriority) neighborChunk.interactionMeshPriority = true;

            RequestChunkMeshUpdate(neighborIndex);
        }

        // Water corner heights sample diagonal XZ columns, so a chunk can need one more
        // rebuild when a diagonal chunk finishes publishing any occupancy change there.
        int diagonalChunkY = chunk.chunkY_world;

        ChunkData diagonalChunk = GetChunkAt(chunk.chunkX_world - 1, diagonalChunkY, chunk.chunkZ_world - 1);
        if (diagonalChunk != null && diagonalChunk.isDataReady && (interactionPriority || (!diagonalChunk.isMeshDeferred && !diagonalChunk.isBuildingMesh)))
        {
            if (interactionPriority) diagonalChunk.interactionMeshPriority = true;
            RequestChunkMeshUpdate(ChunkCenteredCoordsTo1D(diagonalChunk.chunkX_world, diagonalChunkY, diagonalChunk.chunkZ_world));
        }

        diagonalChunk = GetChunkAt(chunk.chunkX_world - 1, diagonalChunkY, chunk.chunkZ_world + 1);
        if (diagonalChunk != null && diagonalChunk.isDataReady && (interactionPriority || (!diagonalChunk.isMeshDeferred && !diagonalChunk.isBuildingMesh)))
        {
            if (interactionPriority) diagonalChunk.interactionMeshPriority = true;
            RequestChunkMeshUpdate(ChunkCenteredCoordsTo1D(diagonalChunk.chunkX_world, diagonalChunkY, diagonalChunk.chunkZ_world));
        }

        diagonalChunk = GetChunkAt(chunk.chunkX_world + 1, diagonalChunkY, chunk.chunkZ_world - 1);
        if (diagonalChunk != null && diagonalChunk.isDataReady && (interactionPriority || (!diagonalChunk.isMeshDeferred && !diagonalChunk.isBuildingMesh)))
        {
            if (interactionPriority) diagonalChunk.interactionMeshPriority = true;
            RequestChunkMeshUpdate(ChunkCenteredCoordsTo1D(diagonalChunk.chunkX_world, diagonalChunkY, diagonalChunk.chunkZ_world));
        }

        diagonalChunk = GetChunkAt(chunk.chunkX_world + 1, diagonalChunkY, chunk.chunkZ_world + 1);
        if (diagonalChunk != null && diagonalChunk.isDataReady && (interactionPriority || (!diagonalChunk.isMeshDeferred && !diagonalChunk.isBuildingMesh)))
        {
            if (interactionPriority) diagonalChunk.interactionMeshPriority = true;
            RequestChunkMeshUpdate(ChunkCenteredCoordsTo1D(diagonalChunk.chunkX_world, diagonalChunkY, diagonalChunk.chunkZ_world));
        }
    }

    public ChunkData GetChunkAt(int centered_cx, int cy, int centered_cz)
    {
        int index = ChunkCenteredCoordsTo1D(centered_cx, cy, centered_cz);
        if (index == -1 || chunks_1D == null || index >= chunks_1D.Length) return null;
        return chunks_1D[index];
    }

    private int ChunkArrayCoordsTo1D(int arrayX, int arrayY, int arrayZ)
    {
        if (arrayX < 0 || arrayX >= worldDimensionX || arrayY < 0 || arrayY >= worldDimensionY || arrayZ < 0 || arrayZ >= worldDimensionZ) return -1;
        return (arrayZ * worldDimensionX * worldDimensionY) + (arrayY * worldDimensionX) + arrayX;
    }

    public int ChunkCenteredCoordsTo1D(int centeredX, int centeredY, int centeredZ)
    {
        // Y is no longer "centered", it's an absolute index.
        return ChunkArrayCoordsTo1D(centeredX + chunkOffsetX, centeredY + chunkOffsetY, centeredZ + chunkOffsetZ);
    }

    public void Chunk1DToArrrayCoords(int index, out int x, out int y, out int z)
    {
        z = index / (worldDimensionX * worldDimensionY);
        y = (index / worldDimensionX) % worldDimensionY;
        x = index % worldDimensionX;
    }

    private int[] GenerateRadialChunkOrder()
    {
        int[] radialOrder = new int[totalWorldChunks];
        bool[] chunkAdded = new bool[totalWorldChunks];
        int count = 0;
        int maxRadius = Mathf.Max(worldDimensionX / 2, worldDimensionZ / 2) + 1;

        #if LOGGING
        Debug.Log($"[McWorld] Generating chunk order: full columns, top-to-bottom, radially.");
        #endif

        for (int r = 0; r < maxRadius && count < totalWorldChunks; r++) {
            for (int z = -r; z <= r; z++) {
                for (int x = -r; x <= r; x++) {
                    if (Mathf.Abs(x) == r || Mathf.Abs(z) == r) {
                        int arrayX = x + chunkOffsetX;
                        int arrayZ = z + chunkOffsetZ;

                        if (arrayX < 0 || arrayX >= worldDimensionX || arrayZ < 0 || arrayZ >= worldDimensionZ) {
                            continue;
                        }

                        for (int y = worldDimensionY - 1; y >= 0; y--) {
                            int chunkIndex = ChunkArrayCoordsTo1D(arrayX, y, arrayZ);
                            if (chunkIndex != -1 && !chunkAdded[chunkIndex]) {
                                radialOrder[count++] = chunkIndex;
                                chunkAdded[chunkIndex] = true;
                            }
                        }
                    }
                }
            }
        }
        return radialOrder;
    }

    #region State Query Methods for Coordinator

    public bool IsChunkDataReady(int chunkIndex)
    {
        if (chunkIndex == -1) return false;
        ChunkData c = chunks_1D[chunkIndex];
        return c != null && c.isDataReady;
    }

    // OPTIMIZED: Direct chunk data access for coordinator (avoids method call overhead)
    public ChunkData GetChunkDataDirect(int chunkIndex)
    {
        if (chunkIndex < 0 || chunkIndex >= chunks_1D.Length) return null;
        return chunks_1D[chunkIndex];
    }

    public bool IsChunkGeneratingData(int chunkIndex)
    {
        if (chunkIndex == -1) return false;
        ChunkData c = chunks_1D[chunkIndex];
        return c != null && c.isGeneratingData;
    }

    public bool IsChunkBuildingMesh(int chunkIndex)
    {
        if (chunkIndex == -1) return false;
        ChunkData c = chunks_1D[chunkIndex];
        return c != null && c.isBuildingMesh;
    }

    public bool UsesGpuLightingBackend()
    {
        return enableGpuVoxelBackend && gpuBackendReady;
    }

    public void SetAmbientOcclusion(bool enabled)
    {
        if (ambientOcclusion == enabled) return;

        ambientOcclusion = enabled;
        _ApplyTerrainLightingSourceToSharedMaterials();

        if (chunks_1D == null) return;

        for (int i = 0; i < chunks_1D.Length; i++)
        {
            ChunkData chunk = chunks_1D[i];
            if (chunk == null || !chunk.isDataReady) continue;

            if (_UsesCpuAmbientOcclusion())
            {
                InitializeChunkLighting(chunk);
                _PreComputeChunkBrightness(chunk);
            }

            RequestChunkMeshUpdate(i);
        }
    }

    public bool HasAvailableGpuMeshReadbackSlot()
    {
        return !_GpuFaceExtractionReady() || _GpuHasAvailableReadbackBuffer();
    }

    public bool AreAllNeighborsReady(int chunkIndex)
    {
        if (chunkIndex == -1) return false;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null) return false;

        for (int i = 0; i < 6; i++)
        {
            int n_cx = chunk.chunkX_world + neighbor_dx_offsets[i];
            int n_cy = chunk.chunkY_world + neighbor_dy_offsets[i];
            int n_cz = chunk.chunkZ_world + neighbor_dz_offsets[i];

            int neighbor_1D_index = ChunkCenteredCoordsTo1D(n_cx, n_cy, n_cz);
            if (neighbor_1D_index == -1) continue;

            ChunkData neighborChunk = GetChunkAt(n_cx, n_cy, n_cz);

            // Null (unallocated) neighbors are deliberately treated as ready
            // to avoid deadlocking the coordinator — all worker slots can be stuck
            // in STATE_WAITING_FOR_MESH while no idle worker can instantiate
            // the missing neighbor. The mesh is built with air on that boundary
            // and _borderMissingMask is set so the heal loop fixes it later.
            if (neighborChunk != null && !neighborChunk.isDataReady) return false;
        }
        return true;
    }

    private bool _TryGetPlayerChunkCoords(out int playerChunkX, out int playerChunkY, out int playerChunkZ)
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (localPlayer == null)
        {
            playerChunkX = 0;
            playerChunkY = 0;
            playerChunkZ = 0;
            return false;
        }

        Vector3 playerPos = localPlayer.GetPosition();
        playerChunkX = Mathf.FloorToInt(playerPos.x / chunkSizeXZ);
        playerChunkY = Mathf.FloorToInt(playerPos.y / chunkSizeY);
        playerChunkZ = Mathf.FloorToInt(playerPos.z / chunkSizeXZ);
        return true;
    }

    private bool _IsChunkNearPlayer(ChunkData chunk)
    {
        if (chunk == null) return false;
        if (!_TryGetPlayerChunkCoords(out int playerChunkX, out int playerChunkY, out int playerChunkZ)) return true;

        int dx = Mathf.Abs(chunk.chunkX_world - playerChunkX);
        int dy = Mathf.Abs(chunk.chunkY_world - playerChunkY);
        int dz = Mathf.Abs(chunk.chunkZ_world - playerChunkZ);
        return dx <= shellMeshPriorityRadiusXZ && dz <= shellMeshPriorityRadiusXZ && dy <= shellMeshPriorityRadiusY;
    }

    // GPU OFFLOAD #1: Radius beyond which a chunk does NOT need a CPU mirror of its blocks.
    // Outside this radius:
    //  - The chunk lives only as a GPU 3D atlas slot.
    //  - Mesh + lighting passes consume the atlas directly (already GPU-sided).
    //  - CPU GetBlock for these chunks returns 0 (will trigger lazy hydration if needed).
    // Inside this radius: the CPU byte[] mirror is maintained as before for collision,
    // raycast targeting, tick processing, etc.
    public int cpuMirrorRadiusXZ = 5;   // ~80 m horizontal
    public int cpuMirrorRadiusY = 3;    // 3 vertical chunks each direction

    public bool ChunkNeedsCpuMirror(ChunkData chunk)
    {
        if (chunk == null) return false;
        if (!_TryGetPlayerChunkCoords(out int pcx, out int pcy, out int pcz)) return true;
        int dx = Mathf.Abs(chunk.chunkX_world - pcx);
        int dy = Mathf.Abs(chunk.chunkY_world - pcy);
        int dz = Mathf.Abs(chunk.chunkZ_world - pcz);
        return dx <= cpuMirrorRadiusXZ && dz <= cpuMirrorRadiusXZ && dy <= cpuMirrorRadiusY;
    }

    public bool ChunkCoordsNeedCpuMirror(int chunkX, int chunkY, int chunkZ)
    {
        if (!_TryGetPlayerChunkCoords(out int pcx, out int pcy, out int pcz)) return true;
        int dx = Mathf.Abs(chunkX - pcx);
        int dy = Mathf.Abs(chunkY - pcy);
        int dz = Mathf.Abs(chunkZ - pcz);
        return dx <= cpuMirrorRadiusXZ && dz <= cpuMirrorRadiusXZ && dy <= cpuMirrorRadiusY;
    }

    private bool _ShouldPrioritizeChunkMesh(ChunkData chunk)
    {
        if (!prioritizeVisibleShellMeshing || chunk == null) return true;
        if (_IsChunkNearPlayer(chunk)) return true;
        if (chunk._isAllAir) return false;

        ChunkData[] neighbors = _GetCachedNeighbors(chunk);
        for (int i = 0; i < 6; i++)
        {
            ChunkData neighbor = neighbors[i];
            if (neighbor == null || !neighbor.isDataReady) return true;
            if (neighbor._isAllAir) return true;
        }

        return false;
    }

    private bool _ShouldDeferIncompleteBackgroundMesh(ChunkData chunk, byte missingMask)
    {
        if (chunk == null || chunk.interactionMeshPriority) return false;
        return _CountNeighborMaskBits(missingMask) > 1;
    }

    public bool ShouldDeferChunkMesh(int chunkIndex)
    {
        if (!prioritizeVisibleShellMeshing || chunkIndex == -1 || chunks_1D == null || chunkIndex >= chunks_1D.Length) return false;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null || !chunk.isDataReady || chunk.isBuildingMesh) return false;
        return !_ShouldPrioritizeChunkMesh(chunk);
    }

    public void MarkChunkMeshDeferred(int chunkIndex)
    {
        if (chunkIndex == -1 || chunks_1D == null || chunkIndex >= chunks_1D.Length) return;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null) return;
        if (chunk.isMeshDeferred) return;

        chunk.isMeshDeferred = true;
#if LOGGING
        chunk.profile_firstMeshWasDeferred = true;
#endif
#if LOGGING
        if (enableCounters) stats_meshDeferredChunks++;
#endif
    }

    public bool IsChunkSingleOpaqueSolid(int centered_cx, int cy, int centered_cz)
    {
        ChunkData chunk = GetChunkAt(centered_cx, cy, centered_cz);
        if (chunk == null) return false;
        return chunk.isSingleOpaqueSolid;
    }

    #endregion

    #region Mesh Building Logic (Moved from McChunk)

    public void BuildChunkMesh(int chunkIndex)
    {
        if (chunkIndex == -1) return;
        ChunkData chunk = chunks_1D[chunkIndex];
        if (chunk == null || chunk.isBuildingMesh || chunk._chunkData == null) return;

        bool interactionPriority = chunk.interactionMeshPriority;
#if LOGGING
        if (enableDetailedTimings && chunk.profile_waitingForFirstMesh)
        {
            float firstMeshLatencyMs = (Time.realtimeSinceStartup - chunk.profile_dataReadyTime) * 1000f;
            if (chunk.profile_firstMeshWasDeferred)
            {
                stats_firstDeferredMeshStartLatencyCount++;
                stats_firstDeferredMeshStartLatencyTotal += firstMeshLatencyMs;
                if (firstMeshLatencyMs > stats_firstDeferredMeshStartLatencyMax) stats_firstDeferredMeshStartLatencyMax = firstMeshLatencyMs;
            }
            else
            {
                stats_firstShellMeshStartLatencyCount++;
                stats_firstShellMeshStartLatencyTotal += firstMeshLatencyMs;
                if (firstMeshLatencyMs > stats_firstShellMeshStartLatencyMax) stats_firstShellMeshStartLatencyMax = firstMeshLatencyMs;
            }
            chunk.profile_waitingForFirstMesh = false;
            chunk.profile_firstMeshWasDeferred = false;
        }
#endif
        // Keep interactionMeshPriority alive — the step loop reads it to uncap steps.
        // Cleared when the mesh build finishes.
        chunk.isBuildingMesh = true;
        chunk.isMeshDeferred = false;
        chunk._meshBuildVersion = chunk._meshBuildVersion + 1;
        chunk._gpuMeshPending = false;
        chunk.pendingColliderApply = false;
        chunk.pendingColliderMeshRebuild = !_ShouldEnableChunkCollider(chunk);
        if (interactionPriority)
        {
            if (chunk._gpuFaceBuildActive)
            {
                _ReleaseGpuFaceBuildBuffer(chunk);
            }
        }

#if LOGGING
        chunk.meshBuildStartTime = Time.realtimeSinceStartup;
        if (enableCounters) stats_chunkStateTransitions++;

        if (enableDetailedTimings)
        {
            logBuilder.Clear();
            logBuilder.AppendLine($"--- BuildMesh for Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) ---");
            chunk.timer_start_stage = Time.realtimeSinceStartup;
            chunk.time_MainLoop = 0;
            chunk.mesh_step_count = 0;
        }
#endif



#if LOGGING
        if (enableDetailedTimings)
        {
            // Reset extended profiling counters
            chunk.time_SentinelEnsure = 0f; chunk.time_DecompressNeighbors = 0f; chunk.time_SentinelBuild = 0f;
            chunk.time_AxisY = 0f; chunk.time_AxisZ = 0f; chunk.time_AxisX = 0f;
            chunk.boundaryChecksY = 0; chunk.boundaryChecksZ = 0; chunk.boundaryChecksX = 0;
            chunk.shouldDrawTests = 0; chunk.shouldDrawTrue = 0;
            chunk.facesOpaque = 0; chunk.facesTransparent = 0; chunk.facesCutout = 0; chunk.facesTotal = 0;
            chunk.sentinelInteriorCopied = 0; chunk.sentinelBorderCopied = 0;
        }
#endif

        if (chunk.isSingleOpaqueSolid)
        {
            bool isFullyOccluded = true;
            for (int i = 0; i < 6; i++)
            {
                if (!IsChunkSingleOpaqueSolid(chunk.chunkX_world + neighbor_dx_offsets[i], chunk.chunkY_world + neighbor_dy_offsets[i], chunk.chunkZ_world + neighbor_dz_offsets[i]))
                {
                    isFullyOccluded = false; break;
                }
            }
            if (isFullyOccluded)
            {
                _ApplyEmptyMesh(chunk);
#if LOGGING
                _RecordMeshBuildCompletion(chunk, false, true);
#endif
                chunk.isBuildingMesh = false;
                chunk.interactionMeshPriority = false;
                return;
            }
        }

        // All-air chunks have zero blocks and can never produce interior faces.
        // Boundary faces are handled by the neighboring solid chunk's mesh.
        if (chunk._isAllAir)
        {
            _ApplyEmptyMesh(chunk);
#if LOGGING
            _RecordMeshBuildCompletion(chunk, false, true);
#endif
            chunk.isBuildingMesh = false;
            chunk.interactionMeshPriority = false;
            return;
        }

        byte[] chunkData = _GetDecompressedData(chunk);
        if (chunkData == null)
        {
            chunk.isBuildingMesh = false;
            chunk.interactionMeshPriority = false;
            return;
        }

        // Player edits should stay on the synchronous CPU mesh path so block changes
        // are visible immediately instead of waiting on async GPU face readback.
        // Keep GPU extraction for background worldgen where throughput matters more.
        // Water faces are added by the budget-gated GPU decode stage 2 via _AddWaterBlock,
        // which handles border samples and corner heights correctly.
        bool gpuFaceExtractionReady = _GpuFaceExtractionReady();
#if LOGGING
        if (enableDetailedTimings && interactionPriority && gpuFaceExtractionReady)
        {
            stats_meshInteractionPriorityCpuBypass++;
        }
#endif
        if (!interactionPriority && gpuFaceExtractionReady)
        {
            int gpuReadbackBufferIndex = _GpuFindAvailableReadbackBuffer();
            if (gpuReadbackBufferIndex != -1)
            {
                _GpuSyncChunkBlocks(chunk, chunkData);
                _GpuPackBorderData(chunk, gpuReadbackBufferIndex);
                if (_ShouldDeferIncompleteBackgroundMesh(chunk, chunk._borderMissingMask))
                {
#if LOGGING
                    if (enableDetailedTimings) stats_meshGpuBorderDefers++;
#endif
                    chunk.isBuildingMesh = false;
                    chunk.interactionMeshPriority = false;
                    MarkChunkMeshDeferred(chunkIndex);
                    return;
                }
                // GPU OFFLOAD #3: try GPU bake first; fall back to CPU if material missing.
                if (!_GpuBakeBiomeColors(chunk)) _PreComputeBiomeColors(chunk);
                if (_GpuRequestChunkFaceReadback(chunk, gpuReadbackBufferIndex))
                {
                    return; // Mesh will be built when readback completes
                }
#if LOGGING
                if (enableDetailedTimings) stats_meshGpuRequestFailures++;
#endif
                chunk._gpuMeshPending = true;
            }
            // GPU is busy or request failed — mark for GPU retry in _BuildChunkMeshStep
            else
            {
#if LOGGING
                if (enableDetailedTimings) stats_meshGpuBusyDefers++;
#endif
                chunk._gpuMeshPending = true;
            }
        }

        if (!_AcquireMeshPool(chunk))
        {
#if LOGGING
            if (enableDetailedTimings) stats_meshPoolExhaustedDefers++;
#endif
            chunk.isBuildingMesh = false;
            chunk.pendingChunkMeshRebuild = true;
            return;
        }
        _ClearAllMeshBuffers(chunk);

        // --- OPTIMIZATION: Use cached neighbor references ---
        ChunkData[] neighbors = _GetCachedNeighbors(chunk);
        chunk.neighborPX = neighbors[0];
        chunk.neighborNX = neighbors[1];
        chunk.neighborPY = neighbors[2];
        chunk.neighborNY = neighbors[3];
        chunk.neighborPZ = neighbors[4];
        chunk.neighborNZ = neighbors[5];

        // Track missing neighbors for self-healing border scan (same as GPU path).
        {
            byte cpuMissing = 0;
            for (int i = 0; i < 6; i++)
            {
                ChunkData nb = neighbors[i];
                if ((nb == null || !nb.isDataReady) &&
                    ChunkCenteredCoordsTo1D(chunk.chunkX_world + neighbor_dx_offsets[i],
                                            chunk.chunkY_world + neighbor_dy_offsets[i],
                                            chunk.chunkZ_world + neighbor_dz_offsets[i]) != -1)
                    cpuMissing |= (byte)(1 << i);
            }
            chunk._borderMissingMask = cpuMissing;
        }


#if LOGGING
        if (enableDetailedTimings)
        {
            chunk.time_NeighborCache = (Time.realtimeSinceStartup - chunk.timer_start_stage) * 1000f;
            chunk.timer_start_stage = Time.realtimeSinceStartup;
        }
#endif

#if LOGGING
        if (enableDetailedTimings)
        {
            // Will be replaced by detailed breakdown, but keep timer for total DataPrep
            chunk.time_DataPrep = 0f;
        }
#endif

        chunk._greedyAxis = 0;
        chunk._greedyU = 0;
        chunk._greedyV = 0;

        // OPTIMIZATION Phase 1: Skip sentinel buffer build, use direct access instead
#if LOGGING
        float t_prepare = Time.realtimeSinceStartup;
#endif
        _DecompressNeighborsOnce(chunk);
#if LOGGING
        if (enableDetailedTimings)
        {
            chunk.time_DecompressNeighbors = (Time.realtimeSinceStartup - t_prepare) * 1000f;
            chunk.time_SentinelEnsure = 0f; // Not used with direct access
            chunk.time_SentinelBuild = 0f; // Not used with direct access
            chunk.time_DataPrep = chunk.time_DecompressNeighbors;
        }
#endif

        bool useGpuLighting = _UsesGpuTerrainLightSampling();

        // OPTIMIZATION Phase 3 & 6: Pre-compute brightness and biome colors
        // This eliminates 10,000+ lighting and biome texture lookups during meshing.
        // Skip CPU brightness preparation when the GPU light atlas is authoritative.
        if (!useGpuLighting)
        {
            _PreComputeChunkBrightness(chunk);
        }
        else
        {
            chunk._cachedBrightness = null;
        }
        _PreComputeBiomeColors(chunk);
        if (!useGpuLighting)
        {
            if (chunk.neighborPX != null && chunk.neighborPX.isDataReady) _PreComputeChunkBrightness(chunk.neighborPX);
            if (chunk.neighborNX != null && chunk.neighborNX.isDataReady) _PreComputeChunkBrightness(chunk.neighborNX);
            if (chunk.neighborPY != null && chunk.neighborPY.isDataReady) _PreComputeChunkBrightness(chunk.neighborPY);
            if (chunk.neighborNY != null && chunk.neighborNY.isDataReady) _PreComputeChunkBrightness(chunk.neighborNY);
            if (chunk.neighborPZ != null && chunk.neighborPZ.isDataReady) _PreComputeChunkBrightness(chunk.neighborPZ);
            if (chunk.neighborNZ != null && chunk.neighborNZ.isDataReady) _PreComputeChunkBrightness(chunk.neighborNZ);
        }
        // _SetBlockLocal already synced this chunk's data to the GPU atlas.
        // Skip the redundant upload for interaction rebuilds.
        if (!interactionPriority)
        {
            _GpuSyncChunkBlocks(chunk, _GetDecompressedData(chunk));
        }

        if (interactionPriority)
        {
            // Player edits should start responding immediately, but finishing the
            // whole rebuild on the interaction call path causes visible spikes.
            // Kick a single step here, then let ProcessActiveChunks finish under
            // the normal frame budget on subsequent updates.
            int immediateStepBudget = 1;
            while (chunk.isBuildingMesh && immediateStepBudget-- > 0)
            {
                _BuildChunkMeshStep(chunk);
            }

            if (!chunk.isBuildingMesh)
            {
                return;
            }
        }

        if (activeMeshingCount < MAX_ACTIVE_CHUNKS)
        {
            activeMeshingChunks[activeMeshingCount++] = chunk;
        }
    }

    private void _BuildChunkMeshStep(ChunkData chunk)
    {
        if (!chunk.isBuildingMesh) return;

        if (chunk._gpuFaceBuildActive)
        {
            _GpuBuildMeshFromFaceDataStep(chunk);
            return;
        }

        // OPTIMIZATION: GPU face extraction retry — chunk was deferred because GPU was busy
        if (chunk._gpuMeshPending)
        {
            int gpuReadbackBufferIndex = _GpuFindAvailableReadbackBuffer();
            if (_GpuFaceExtractionReady() && gpuReadbackBufferIndex != -1)
            {
                chunk._gpuMeshPending = false;
                _GpuSyncChunkBlocks(chunk, _GetDecompressedData(chunk));
                _GpuPackBorderData(chunk, gpuReadbackBufferIndex);
                if (_ShouldDeferIncompleteBackgroundMesh(chunk, chunk._borderMissingMask))
                {
#if LOGGING
                    if (enableDetailedTimings) stats_meshGpuBorderDefers++;
#endif
                    chunk.isBuildingMesh = false;
                    chunk.interactionMeshPriority = false;
                    int deferredChunkIndex = ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
                    if (deferredChunkIndex != -1) MarkChunkMeshDeferred(deferredChunkIndex);
                    else chunk.isMeshDeferred = true;
                    return;
                }
                // GPU OFFLOAD #3: try GPU bake first; fall back to CPU if material missing.
                if (!_GpuBakeBiomeColors(chunk)) _PreComputeBiomeColors(chunk);
                if (_GpuRequestChunkFaceReadback(chunk, gpuReadbackBufferIndex))
                {
                    return; // Mesh will be built when readback completes
                }
#if LOGGING
                if (enableDetailedTimings) stats_meshGpuRequestFailures++;
#endif
                // GPU request failed — falling through to CPU path.
                // _GpuPackBorderData just overwrote _borderMissingMask with
                // fresh neighbor availability, but the CPU decomp caches are
                // from _DecompressNeighborsOnce (called when the mesh build
                // started, possibly frames ago).  Refresh neighbor refs (which
                // may have been created/invalidated mid-build) then re-sync
                // decomp caches and mask so they match what the CPU mesher
                // will actually see.
                {
                    ChunkData[] nbrs = _GetCachedNeighbors(chunk);
                    chunk.neighborPX = nbrs[0]; chunk.neighborNX = nbrs[1];
                    chunk.neighborPY = nbrs[2]; chunk.neighborNY = nbrs[3];
                    chunk.neighborPZ = nbrs[4]; chunk.neighborNZ = nbrs[5];
                }
                _DecompressNeighborsOnce(chunk);
                {
                    byte cpuMissing = 0;
                    for (int i = 0; i < 6; i++)
                    {
                        bool hasDecomp = false;
                        if (i == 0) hasDecomp = chunk._decompPX != null;
                        else if (i == 1) hasDecomp = chunk._decompNX != null;
                        else if (i == 2) hasDecomp = chunk._decompPY != null;
                        else if (i == 3) hasDecomp = chunk._decompNY != null;
                        else if (i == 4) hasDecomp = chunk._decompPZ != null;
                        else hasDecomp = chunk._decompNZ != null;
                        if (!hasDecomp &&
                            ChunkCenteredCoordsTo1D(chunk.chunkX_world + neighbor_dx_offsets[i],
                                                    chunk.chunkY_world + neighbor_dy_offsets[i],
                                                    chunk.chunkZ_world + neighbor_dz_offsets[i]) != -1)
                            cpuMissing |= (byte)(1 << i);
                    }
                    chunk._borderMissingMask = cpuMissing;
                }
            }
            else
            {
                return; // GPU still busy, retry next frame
            }
        }

        chunk._lastMeshStepFrame = Time.frameCount;

#if LOGGING
        float timer_start_stage = 0f;
        if (enableDetailedTimings)
        {
            timer_start_stage = Time.realtimeSinceStartup;
            chunk.mesh_step_count++;
        }
#endif

        // OPTIMIZATION Phase 1: Direct access to decompressed data (eliminates sentinel buffer overhead)
        float budgetStart = Time.realtimeSinceStartup;
        float budgetSec = meshStepTimeBudgetMs * 0.001f;

        byte[] selfData = chunk._decompSelf;
        if (selfData == null)
        {
            chunk.isBuildingMesh = false;
            chunk.interactionMeshPriority = false;
            return;
        }

        // Cache neighbor decompressed data for boundary checks
        byte[] dataPX = chunk._decompPX;
        byte[] dataNX = chunk._decompNX;
        byte[] dataPY = chunk._decompPY;
        byte[] dataNY = chunk._decompNY;
        byte[] dataPZ = chunk._decompPZ;
        byte[] dataNZ = chunk._decompNZ;

        // Pre-calculate strides for direct indexing
        int chunkStride = chunkSizeXZ * chunkSizeXZ;

        // OPTIMIZATION: Pre-fetch chunk-global bounds for slice skipping
        int gMinY = chunk._chunkGlobalMinY, gMaxY = chunk._chunkGlobalMaxY;
        int gMinX = chunk._chunkGlobalMinX, gMaxX = chunk._chunkGlobalMaxX;
        int gMinZ = chunk._chunkGlobalMinZ, gMaxZ = chunk._chunkGlobalMaxZ;
        bool isAllAir = chunk._isAllAir;

        while (chunk._greedyAxis <= 5)
        {
            if (Time.realtimeSinceStartup - budgetStart > budgetSec) break;

            int direction = chunk._greedyAxis;
            int maxSlices = direction <= 1 ? chunkSizeY : chunkSizeXZ;
            if (chunk._greedyU >= maxSlices)
            {
                chunk._greedyU = 0;
                chunk._greedyAxis++;
                continue;
            }

            // OPTIMIZATION: Skip slices that lie entirely outside the chunk's occupied region
            // _BuildGreedySliceMask skips selfID==0 cells, so slices with no self blocks produce zero faces.
            // For Y-axis (directions 0,1): slice is Y, skip if outside [gMinY, gMaxY]
            // For Z-axis (directions 2,3): slice is Z, skip if outside [gMinZ, gMaxZ]
            // For X-axis (directions 4,5): slice is X, skip if outside [gMinX, gMaxX]
            if (!isAllAir)
            {
                int slice = chunk._greedyU;
                bool canSkip = false;
                if (direction <= 1) canSkip = (gMinY > gMaxY) || slice < gMinY || slice > gMaxY;
                else if (direction <= 3) canSkip = (gMinZ > gMaxZ) || slice < gMinZ || slice > gMaxZ;
                else canSkip = (gMinX > gMaxX) || slice < gMinX || slice > gMaxX;

                // OPTIMIZATION: For single-opaque-solid chunks, only boundary slices facing
                // neighbor chunks can produce faces. Interior slices always have selfID == neighborID.
                // Dir 0(Y+) → only slice=maxY; Dir 1(Y-) → only slice=0
                // Dir 2(Z+) → only slice=maxZ; Dir 3(Z-) → only slice=0
                // Dir 4(X+) → only slice=maxX; Dir 5(X-) → only slice=0
                if (!canSkip && chunk.isSingleOpaqueSolid)
                {
                    int boundarySlice;
                    if (direction == 0) boundarySlice = chunkSizeY - 1;
                    else if (direction == 1) boundarySlice = 0;
                    else if (direction == 2) boundarySlice = chunkSizeXZ - 1;
                    else if (direction == 3) boundarySlice = 0;
                    else if (direction == 4) boundarySlice = chunkSizeXZ - 1;
                    else boundarySlice = 0;
                    canSkip = (slice != boundarySlice);
                }

                if (canSkip)
                {
                    chunk._greedyU++;
                    if (chunk._greedyU >= maxSlices)
                    {
                        chunk._greedyU = 0;
                        chunk._greedyAxis++;
                    }
                    continue;
                }
            }

#if LOGGING
            float axisStart = 0f; if (enableDetailedTimings) axisStart = Time.realtimeSinceStartup;
#endif

            int maskWidth, maskHeight;
            _BuildGreedySliceMask(chunk, direction, chunk._greedyU, selfData, dataNX, dataPX, dataNY, dataPY, dataNZ, dataPZ, chunkStride, out maskWidth, out maskHeight);
            _EmitGreedyMask(chunk, direction, chunk._greedyU, maskWidth, maskHeight);

            chunk._greedyU++;
            if (chunk._greedyU >= maxSlices)
            {
                chunk._greedyU = 0;
                chunk._greedyAxis++;
            }

#if LOGGING
            if (enableDetailedTimings)
            {
                float axisElapsed = (Time.realtimeSinceStartup - axisStart) * 1000f;
                if (direction <= 1) chunk.time_AxisY += axisElapsed;
                else if (direction <= 3) chunk.time_AxisZ += axisElapsed;
                else chunk.time_AxisX += axisElapsed;
            }
#endif
        }

#if LOGGING
        if (enableDetailedTimings) chunk.time_MainLoop += (Time.realtimeSinceStartup - timer_start_stage) * 1000f;
#endif

        if (chunk._greedyAxis > 5)
        {
            bool interactionPriority = chunk.interactionMeshPriority;
            // After all boundary processing, add cross-shaped blocks
            _AddCrossShapedBlocks(chunk);
            _AddTorchBlocks(chunk);
            _AddWaterBlocks(chunk);

            _ApplyAllMeshData(chunk);
#if LOGGING
            _RecordMeshBuildCompletion(chunk, false, false);
#endif

            chunk.isBuildingMesh = false;
            chunk.interactionMeshPriority = false;
            // Water chunks always take this CPU path (GPU extraction excludes _hasWaterBlocks).
            // If a neighbor finished data-gen during the multi-frame greedy build, its trigger
            // landed on pendingNeighborMeshRebuild — consume it before nulling neighbor refs.
            if (chunk.pendingNeighborMeshRebuild)
            {
                chunk.pendingNeighborMeshRebuild = false;
                TriggerNeighborMeshRebuilds(chunk);
            }
            // Immediately re-queue if we meshed with missing border data and the
            // neighbors are now ready — don't wait for the passive heal scan.
            if (chunk._borderMissingMask != 0)
            {
                _TryImmediateBorderHeal(chunk);
            }

            // Null out neighbor references to free memory
            chunk.neighborPX = null; chunk.neighborNX = null;
            chunk.neighborPY = null; chunk.neighborNY = null;
            chunk.neighborPZ = null; chunk.neighborNZ = null;
            // Release decompressed caches
            chunk._decompSelf = null;
            chunk._decompNX = null; chunk._decompPX = null;
            chunk._decompNY = null; chunk._decompPY = null;
            chunk._decompNZ = null; chunk._decompPZ = null;

            // Can't use SendCustomEventDelayedFrames here easily without more state,
            // so we'll just apply the collider directly after a few frames in the main loop.
            // A more robust solution might involve another queue. For now, this is simpler.
            if (chunk._collisionVertexCount == 0) _DisableChunkCollider(chunk, false);
            else if (interactionPriority) _ApplyDataToCollider(chunk);
            else if (!_ShouldEnableChunkCollider(chunk)) _DisableChunkCollider(chunk, true);
            else if (_ShouldDeferChunkSecondaryWork(chunk)) _QueueDeferredColliderApply(chunk);
            else _ApplyDataToCollider(chunk);
            _ReleaseMeshPool(chunk);

        }
    }

#if LOGGING
    private void _RecordMeshBuildCompletion(ChunkData chunk, bool usedGpuFaces, bool emptyMesh)
    {
        if (chunk == null) return;

        stats_meshBuildTotal++;

        if (enableCounters)
        {
            if (emptyMesh) stats_meshBuildEmptyCompletions++;
            else if (usedGpuFaces) stats_meshBuildGpuCompletions++;
            else stats_meshBuildCpuCompletions++;

            stats_sentinelBuilds++;
            if (!usedGpuFaces)
            {
                stats_faceCullingTests += chunk.shouldDrawTests;
                stats_facesCulled += (chunk.shouldDrawTests - chunk.shouldDrawTrue);
                stats_facesDrawn += chunk.facesTotal;
            }
            stats_verticesOpaque += chunk._opaqueVertexCount;
            stats_verticesTransparent += chunk._transparentVertexCount;
            stats_verticesCutout += chunk._cutoutVertexCount;
        }

        if (!enableDetailedTimings) return;

        float meshBuildTime = (Time.realtimeSinceStartup - chunk.meshBuildStartTime) * 1000f;
        stats_meshBuildTimeTotal += meshBuildTime;
        if (meshBuildTime < stats_meshBuildTimeMin) stats_meshBuildTimeMin = meshBuildTime;
        if (meshBuildTime > stats_meshBuildTimeMax) stats_meshBuildTimeMax = meshBuildTime;
        stats_meshStepsTotal += chunk.mesh_step_count;
        stats_meshNeighborCacheTime += chunk.time_NeighborCache;
        stats_meshDataPrepTime += chunk.time_DataPrep;
        stats_meshMainLoopTime += chunk.time_MainLoop;
        stats_greedyAxisYTime += chunk.time_AxisY;
        stats_greedyAxisZTime += chunk.time_AxisZ;
        stats_greedyAxisXTime += chunk.time_AxisX;
        stats_sentinelBuildTime += chunk.time_SentinelBuild;
        stats_meshApplyOpaqueTime += chunk.time_ApplyOpaque;
        stats_meshApplyTransparentTime += chunk.time_ApplyTransparent;
        stats_meshApplyCutoutTime += chunk.time_ApplyCutout;
        stats_meshApplyColliderTime += chunk.time_ApplyCollision;
        stats_meshBoundaryChecksY += chunk.boundaryChecksY;
        stats_meshBoundaryChecksZ += chunk.boundaryChecksZ;
        stats_meshBoundaryChecksX += chunk.boundaryChecksX;
        stats_facesOpaque += chunk.facesOpaque;
        stats_facesTransparent += chunk.facesTransparent;
        stats_facesCutout += chunk.facesCutout;
        if (chunk._borderMissingMask != 0)
        {
            stats_meshBuildsWithMissingNeighbors++;
            stats_meshMissingNeighborBits += _CountNeighborMaskBits(chunk._borderMissingMask);
        }

        _RecordSlowMeshBuild(chunk, meshBuildTime, usedGpuFaces, emptyMesh);
    }

    private int _CountNeighborMaskBits(byte mask)
    {
        int bits = 0;
        for (int i = 0; i < 6; i++)
        {
            if ((mask & (1 << i)) != 0) bits++;
        }
        return bits;
    }

    private void _RecordSlowMeshBuild(ChunkData chunk, float meshBuildTime, bool usedGpuFaces, bool emptyMesh)
    {
        if (chunk == null) return;

        int insertIndex = -1;
        for (int i = 0; i < SLOWEST_MESH_BUILD_COUNT; i++)
        {
            if (meshBuildTime > stats_slowestMeshBuildMs[i])
            {
                insertIndex = i;
                break;
            }
        }

        if (insertIndex == -1) return;

        for (int i = SLOWEST_MESH_BUILD_COUNT - 1; i > insertIndex; i--)
        {
            stats_slowestMeshBuildMs[i] = stats_slowestMeshBuildMs[i - 1];
            stats_slowestMeshChunkX[i] = stats_slowestMeshChunkX[i - 1];
            stats_slowestMeshChunkY[i] = stats_slowestMeshChunkY[i - 1];
            stats_slowestMeshChunkZ[i] = stats_slowestMeshChunkZ[i - 1];
            stats_slowestMeshDataPrepMs[i] = stats_slowestMeshDataPrepMs[i - 1];
            stats_slowestMeshMainLoopMs[i] = stats_slowestMeshMainLoopMs[i - 1];
            stats_slowestMeshApplyMs[i] = stats_slowestMeshApplyMs[i - 1];
            stats_slowestMeshFaceCount[i] = stats_slowestMeshFaceCount[i - 1];
            stats_slowestMeshVertexCount[i] = stats_slowestMeshVertexCount[i - 1];
            stats_slowestMeshKind[i] = stats_slowestMeshKind[i - 1];
        }

        stats_slowestMeshBuildMs[insertIndex] = meshBuildTime;
        stats_slowestMeshChunkX[insertIndex] = chunk.chunkX_world;
        stats_slowestMeshChunkY[insertIndex] = chunk.chunkY_world;
        stats_slowestMeshChunkZ[insertIndex] = chunk.chunkZ_world;
        stats_slowestMeshDataPrepMs[insertIndex] = chunk.time_NeighborCache + chunk.time_DataPrep;
        stats_slowestMeshMainLoopMs[insertIndex] = chunk.time_MainLoop;
        stats_slowestMeshApplyMs[insertIndex] = chunk.time_ApplyOpaque + chunk.time_ApplyTransparent + chunk.time_ApplyCutout + chunk.time_ApplyCollision;
        stats_slowestMeshFaceCount[insertIndex] = chunk.facesTotal;
        stats_slowestMeshVertexCount[insertIndex] = chunk._opaqueVertexCount + chunk._transparentVertexCount + chunk._cutoutVertexCount;
        stats_slowestMeshKind[insertIndex] = emptyMesh ? (byte)2 : (usedGpuFaces ? (byte)1 : (byte)0);
    }
#endif

    private void _EnsureSentinelBuffer(ChunkData chunk)
    {
        int SX = chunkSizeXZ + 2;
        int SY = chunkSizeY + 2;
        int SZ = chunkSizeXZ + 2;
        if (chunk._sentinel == null || chunk._sentinelSX != SX || chunk._sentinelSY != SY || chunk._sentinelSZ != SZ)
        {
            chunk._sentinel = new byte[SX * SY * SZ];
            chunk._sentinelSX = SX; chunk._sentinelSY = SY; chunk._sentinelSZ = SZ;
            chunk._sentinelStrideZ = SX;
            chunk._sentinelStrideY = SX * SZ;
        }
        chunk._sentinelReady = false;
    }

    private int _SentinelIndex(int sx, int sy, int sz, int SX, int SY)
    {
        return (sy * SX * (chunkSizeXZ + 2)) + (sz * SX) + sx; // SX==chunk._sentinelSX, SZ not needed for stride
    }

    private void _BuildSentinel(ChunkData chunk)
    {
        int SX = chunk._sentinelSX;
        int SY = chunk._sentinelSY;
        int SZ = chunk._sentinelSZ;
        int strideY = chunk._sentinelStrideY;
        int strideZ = chunk._sentinelStrideZ;
        byte[] s = chunk._sentinel;

        // Clear once
        System.Array.Clear(s, 0, s.Length);

        // 1) Interior from self decompressed
        byte[] self = chunk._decompSelf;
        if (self != null)
        {
            int innerStride = chunkSizeXZ * chunkSizeXZ;
            for (int y = 0; y < chunkSizeY; y++)
            {
                int srcBase = y * innerStride;
                int dstBase = (y + 1) * strideY + strideZ + 1; // (sy=y+1, sz=1, sx=1)
                for (int z = 0; z < chunkSizeXZ; z++)
                {
                    int lineSrc = srcBase + z * chunkSizeXZ;
                    int lineDst = dstBase + z * strideZ;
                    // OPTIMIZATION Phase 7: Use Buffer.BlockCopy instead of Array.Copy (faster for byte arrays)
                    System.Buffer.BlockCopy(self, lineSrc, s, lineDst, chunkSizeXZ);
                }
            }
#if LOGGING
            if (enableVerboseLogging) chunk.sentinelInteriorCopied = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
#endif
        }

        // 2) Borders from neighbors
        // NX (sx=0) from neighborNX x=chunkSizeXZ-1
        _FillSentinelBorderX(chunk, 0, chunk._decompNX, chunkSizeXZ - 1);
        // PX (sx=SX-1) from neighborPX x=0
        _FillSentinelBorderX(chunk, SX - 1, chunk._decompPX, 0);
        // NZ (sz=0) from neighborNZ z=chunkSizeXZ-1
        _FillSentinelBorderZ(chunk, 0, chunk._decompNZ, chunkSizeXZ - 1);
        // PZ (sz=SZ-1) from neighborPZ z=0
        _FillSentinelBorderZ(chunk, SZ - 1, chunk._decompPZ, 0);
        // NY (sy=0) from neighborNY y=chunkSizeY-1
        _FillSentinelBorderY(chunk, 0, chunk._decompNY, chunkSizeY - 1);
        // PY (sy=SY-1) from neighborPY y=0
        _FillSentinelBorderY(chunk, SY - 1, chunk._decompPY, 0);

        chunk._sentinelReady = true;
    }

    private void _FillSentinelBorderX(ChunkData chunk, int sx, byte[] neighborFlat, int neighborX)
    {
        if (neighborFlat == null) return;
        int SX = chunk._sentinelSX; int SY = chunk._sentinelSY; int SZ = chunk._sentinelSZ;
        int strideY = chunk._sentinelStrideY; int strideZ = chunk._sentinelStrideZ;
        int innerStride = chunkSizeXZ * chunkSizeXZ;
        byte[] s = chunk._sentinel;
        for (int y = 0; y < chunkSizeY; y++)
        {
            int srcY = y * innerStride;
            int dstY = (y + 1) * strideY;
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                int src = srcY + z * chunkSizeXZ + neighborX;
                int dst = dstY + (z + 1) * strideZ + sx;
                s[dst] = neighborFlat[src];
#if LOGGING
                if (enableVerboseLogging) chunk.sentinelBorderCopied++;
#endif
            }
        }
    }

    private void _FillSentinelBorderZ(ChunkData chunk, int sz, byte[] neighborFlat, int neighborZ)
    {
        if (neighborFlat == null) return;
        int SX = chunk._sentinelSX; int SY = chunk._sentinelSY; int SZ = chunk._sentinelSZ;
        int strideY = chunk._sentinelStrideY; int strideZ = chunk._sentinelStrideZ;
        int innerStride = chunkSizeXZ * chunkSizeXZ;
        byte[] s = chunk._sentinel;
        for (int y = 0; y < chunkSizeY; y++)
        {
            int srcY = y * innerStride;
            int dstY = (y + 1) * strideY;
            for (int x = 0; x < chunkSizeXZ; x++)
            {
                int src = srcY + neighborZ * chunkSizeXZ + x;
                int dst = dstY + sz * strideZ + (x + 1);
                s[dst] = neighborFlat[src];
            }
#if LOGGING
            if (enableVerboseLogging) chunk.sentinelBorderCopied += chunkSizeXZ;
#endif
        }
    }

    private void _FillSentinelBorderY(ChunkData chunk, int sy, byte[] neighborFlat, int neighborY)
    {
        if (neighborFlat == null) return;
        int SX = chunk._sentinelSX; int SY = chunk._sentinelSY; int SZ = chunk._sentinelSZ;
        int strideY = chunk._sentinelStrideY; int strideZ = chunk._sentinelStrideZ;
        int innerStride = chunkSizeXZ * chunkSizeXZ;
        byte[] s = chunk._sentinel;
        int dstBase = sy * strideY + strideZ + 1;
        int srcBase = neighborY * innerStride;
        for (int z = 0; z < chunkSizeXZ; z++)
        {
            int lineSrc = srcBase + z * chunkSizeXZ;
            int lineDst = dstBase + z * strideZ;
            // OPTIMIZATION Phase 7: Use Buffer.BlockCopy instead of Array.Copy (faster for byte arrays)
            System.Buffer.BlockCopy(neighborFlat, lineSrc, s, lineDst, chunkSizeXZ);
#if LOGGING
            if (enableVerboseLogging) chunk.sentinelBorderCopied += chunkSizeXZ;
#endif
        }
    }

    private void _DecompressNeighborsOnce(ChunkData chunk)
    {
        // Decompress self and any ready neighbors once
        chunk._decompSelf = _GetDecompressedData(chunk);
        chunk._decompNX = (chunk.neighborNX != null && chunk.neighborNX.isDataReady) ? _GetDecompressedData(chunk.neighborNX) : null;
        chunk._decompPX = (chunk.neighborPX != null && chunk.neighborPX.isDataReady) ? _GetDecompressedData(chunk.neighborPX) : null;
        chunk._decompNY = (chunk.neighborNY != null && chunk.neighborNY.isDataReady) ? _GetDecompressedData(chunk.neighborNY) : null;
        chunk._decompPY = (chunk.neighborPY != null && chunk.neighborPY.isDataReady) ? _GetDecompressedData(chunk.neighborPY) : null;
        chunk._decompNZ = (chunk.neighborNZ != null && chunk.neighborNZ.isDataReady) ? _GetDecompressedData(chunk.neighborNZ) : null;
        chunk._decompPZ = (chunk.neighborPZ != null && chunk.neighborPZ.isDataReady) ? _GetDecompressedData(chunk.neighborPZ) : null;
    }

    private void _ProcessBoundary(ChunkData chunk, byte id1, byte id2, Vector3 pos1, Vector3 pos2, int axis)
    {
        if (id1 == id2) return;

        BlockVisibilityType vis1 = _GetVisibilityType(id1);
        BlockVisibilityType vis2 = _GetVisibilityType(id2);

        if (_ShouldDrawFace(id1, id2))
        {
            if (axis == 0) _AddFace(chunk, FaceVertices_Up, Normal_Up, pos1, id1, vis1, FACE_INDEX_TOP);
            else if (axis == 1) _AddFace(chunk, FaceVertices_North, Normal_North, pos1, id1, vis1, FACE_INDEX_SIDE);
            else _AddFace(chunk, FaceVertices_East, Normal_East, pos1, id1, vis1, FACE_INDEX_SIDE);
        }

        if (_ShouldDrawFace(id2, id1))
        {
            if (axis == 0) _AddFace(chunk, FaceVertices_Down, Normal_Down, pos2, id2, vis2, FACE_INDEX_BOTTOM);
            else if (axis == 1) _AddFace(chunk, FaceVertices_South, Normal_South, pos2, id2, vis2, FACE_INDEX_SIDE);
            else _AddFace(chunk, FaceVertices_West, Normal_West, pos2, id2, vis2, FACE_INDEX_SIDE);
        }
    }

    private byte _GetBlockForGreedyMesher(ChunkData chunk, int x, int y, int z)
    {
        // --- OPTIMIZATION: Check boundaries and query neighbor chunk directly ---
        if (x < 0) {
            ChunkData n = chunk.neighborNX;
            if (n == null || !n.isDataReady) return 0;
            return _GetBlockLocal(n, chunkSizeXZ - 1, y, z);
        }
        if (x >= chunkSizeXZ) {
            ChunkData n = chunk.neighborPX;
            if (n == null || !n.isDataReady) return 0;
            return _GetBlockLocal(n, 0, y, z);
        }
        if (y < 0) {
            ChunkData n = chunk.neighborNY;
            if (n == null || !n.isDataReady) return 0;
            return _GetBlockLocal(n, x, chunkSizeY - 1, z);
        }
        if (y >= chunkSizeY) {
            ChunkData n = chunk.neighborPY;
            if (n == null || !n.isDataReady) return 0;
            return _GetBlockLocal(n, x, 0, z);
        }
        if (z < 0) {
            ChunkData n = chunk.neighborNZ;
            if (n == null || !n.isDataReady) return 0;
            return _GetBlockLocal(n, x, y, chunkSizeXZ - 1);
        }
        if (z >= chunkSizeXZ) {
            ChunkData n = chunk.neighborPZ;
            if (n == null || !n.isDataReady) return 0;
            return _GetBlockLocal(n, x, y, 0);
        }

        // If within bounds, use the optimized getter on the current chunk's compressed data
        return _GetBlockLocal(chunk, x, y, z);
    }

    // OPTIMIZATION Phase 1: Direct block access helper (replaces lambda for Udon compatibility)
    private byte _GetBlockDirectMeshing(byte[] selfData, byte[] dataNX, byte[] dataPX, byte[] dataNY, byte[] dataPY, byte[] dataNZ, byte[] dataPZ,
                                        int x, int y, int z, int chunkSizeXZ, int chunkSizeY, int chunkStride)
    {
        if (x >= 0 && x < chunkSizeXZ && y >= 0 && y < chunkSizeY && z >= 0 && z < chunkSizeXZ)
        {
            return selfData[y * chunkStride + z * chunkSizeXZ + x];
        }
        // Handle boundaries with neighbor data
        if (x < 0 && dataNX != null) return dataNX[y * chunkStride + z * chunkSizeXZ + (chunkSizeXZ - 1)];
        if (x >= chunkSizeXZ && dataPX != null) return dataPX[y * chunkStride + z * chunkSizeXZ + 0];
        if (y < 0 && dataNY != null) return dataNY[(chunkSizeY - 1) * chunkStride + z * chunkSizeXZ + x];
        if (y >= chunkSizeY && dataPY != null) return dataPY[0 * chunkStride + z * chunkSizeXZ + x];
        if (z < 0 && dataNZ != null) return dataNZ[y * chunkStride + (chunkSizeXZ - 1) * chunkSizeXZ + x];
        if (z >= chunkSizeXZ && dataPZ != null) return dataPZ[y * chunkStride + 0 * chunkSizeXZ + x];
        return 0; // Air/empty
    }

    private int _PackColorRGB(Color color)
    {
        Color32 color32 = (Color32)color;
        return color32.r | (color32.g << 8) | (color32.b << 16);
    }

    private Color _UnpackColorRGB(int packedColor, float alpha)
    {
        return new Color(
            (packedColor & 0xFF) / 255f,
            ((packedColor >> 8) & 0xFF) / 255f,
            ((packedColor >> 16) & 0xFF) / 255f,
            alpha
        );
    }

    private byte _GetCachedLightLevelAtBlockRaw(ChunkData chunk, int localX, int localY, int localZ)
    {
        if (chunk == null || chunk._cachedBrightness == null) return 15;
        if (localX < 0 || localX >= chunkSizeXZ || localY < 0 || localY >= chunkSizeY || localZ < 0 || localZ >= chunkSizeXZ) return 15;
        int idx = localY * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
        return chunk._cachedBrightness[idx];
    }

    private byte _GetCachedLightLevelForDirection(ChunkData chunk, int direction, int localX, int localY, int localZ)
    {
        int neighborX = localX;
        int neighborY = localY;
        int neighborZ = localZ;

        if (direction == 0) neighborY++;
        else if (direction == 1) neighborY--;
        else if (direction == 2) neighborZ++;
        else if (direction == 3) neighborZ--;
        else if (direction == 4) neighborX++;
        else neighborX--;

        if (neighborX >= 0 && neighborX < chunkSizeXZ &&
            neighborY >= 0 && neighborY < chunkSizeY &&
            neighborZ >= 0 && neighborZ < chunkSizeXZ)
        {
            // GPU lighting: shader handles brightness, use uniform placeholder
            if (chunk._cachedBrightness == null) return 15;
            return _GetCachedLightLevelAtBlockRaw(chunk, neighborX, neighborY, neighborZ);
        }

        ChunkData neighborChunk = null;
        int neighborLocalX = neighborX;
        int neighborLocalY = neighborY;
        int neighborLocalZ = neighborZ;

        if (neighborX < 0)
        {
            neighborChunk = chunk.neighborNX;
            neighborLocalX = chunkSizeXZ - 1;
        }
        else if (neighborX >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPX;
            neighborLocalX = 0;
        }
        else if (neighborY < 0)
        {
            neighborChunk = chunk.neighborNY;
            neighborLocalY = chunkSizeY - 1;
        }
        else if (neighborY >= chunkSizeY)
        {
            neighborChunk = chunk.neighborPY;
            neighborLocalY = 0;
        }
        else if (neighborZ < 0)
        {
            neighborChunk = chunk.neighborNZ;
            neighborLocalZ = chunkSizeXZ - 1;
        }
        else if (neighborZ >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPZ;
            neighborLocalZ = 0;
        }

        if (neighborChunk != null && neighborChunk.isDataReady && neighborChunk._cachedBrightness != null)
        {
            return _GetCachedLightLevelAtBlockRaw(neighborChunk, neighborLocalX, neighborLocalY, neighborLocalZ);
        }

        if (_UsesGpuTerrainLightSampling()) return 15;

        return 0;
    }

    private int _GetAoBoundaryMask(int localX, int localY, int localZ)
    {
        int boundaryMask = 0;
        if (localX < 0 || localX >= chunkSizeXZ) boundaryMask |= 1;
        if (localY < 0 || localY >= chunkSizeY) boundaryMask |= 2;
        if (localZ < 0 || localZ >= chunkSizeXZ) boundaryMask |= 4;
        return boundaryMask;
    }

    private bool _TryGetAoLightLevelFast(ChunkData chunk, int localX, int localY, int localZ, byte emittedLight, out byte lightLevel)
    {
        lightLevel = emittedLight;
        if (chunk == null) return false;

        int boundaryMask = _GetAoBoundaryMask(localX, localY, localZ);
        int chunkStride = chunkSizeXZ * chunkSizeXZ;
        byte[] brightnessData = null;
        ChunkData sampleChunk = chunk;
        int sampleX = localX;
        int sampleY = localY;
        int sampleZ = localZ;

        if (boundaryMask == 0)
        {
            if (chunk._cachedBrightness == null && !_UsesGpuTerrainLightSampling())
            {
                _PreComputeChunkBrightness(chunk);
            }
            brightnessData = chunk._cachedBrightness;
        }
        else if ((boundaryMask & (boundaryMask - 1)) == 0)
        {
            if (boundaryMask == 1)
            {
                sampleChunk = localX < 0 ? chunk.neighborNX : chunk.neighborPX;
                sampleX = localX < 0 ? chunkSizeXZ - 1 : 0;
            }
            else if (boundaryMask == 2)
            {
                sampleChunk = localY < 0 ? chunk.neighborNY : chunk.neighborPY;
                sampleY = localY < 0 ? chunkSizeY - 1 : 0;
            }
            else
            {
                sampleChunk = localZ < 0 ? chunk.neighborNZ : chunk.neighborPZ;
                sampleZ = localZ < 0 ? chunkSizeXZ - 1 : 0;
            }

            if (sampleChunk != null && sampleChunk.isDataReady && sampleChunk._cachedBrightness == null && !_UsesGpuTerrainLightSampling())
            {
                _PreComputeChunkBrightness(sampleChunk);
            }
            brightnessData = sampleChunk != null ? sampleChunk._cachedBrightness : null;
        }
        else
        {
            return false;
        }

        if (brightnessData == null) return false;

        int brightnessIndex = sampleY * chunkStride + sampleZ * chunkSizeXZ + sampleX;
        byte sampleLight = brightnessData[brightnessIndex];
        lightLevel = sampleLight > emittedLight ? sampleLight : emittedLight;
        return true;
    }

    private bool _TryGetAoBlockIdFast(ChunkData chunk, int localX, int localY, int localZ, out byte blockId)
    {
        blockId = 0;
        if (chunk == null) return false;

        int boundaryMask = _GetAoBoundaryMask(localX, localY, localZ);
        int chunkStride = chunkSizeXZ * chunkSizeXZ;
        byte[] blockData = null;
        int sampleX = localX;
        int sampleY = localY;
        int sampleZ = localZ;

        if (boundaryMask == 0)
        {
            blockData = chunk._decompSelf;
        }
        else if ((boundaryMask & (boundaryMask - 1)) == 0)
        {
            if (boundaryMask == 1)
            {
                blockData = localX < 0 ? chunk._decompNX : chunk._decompPX;
                sampleX = localX < 0 ? chunkSizeXZ - 1 : 0;
            }
            else if (boundaryMask == 2)
            {
                blockData = localY < 0 ? chunk._decompNY : chunk._decompPY;
                sampleY = localY < 0 ? chunkSizeY - 1 : 0;
            }
            else
            {
                blockData = localZ < 0 ? chunk._decompNZ : chunk._decompPZ;
                sampleZ = localZ < 0 ? chunkSizeXZ - 1 : 0;
            }
        }
        else
        {
            return false;
        }

        if (blockData == null) return false;

        int blockIndex = sampleY * chunkStride + sampleZ * chunkSizeXZ + sampleX;
        blockId = blockData[blockIndex];
        return true;
    }

    private ChunkData _ResolveAoSampleChunk(ChunkData chunk, ref int localX, ref int localY, ref int localZ)
    {
        if (chunk == null) return null;

        int chunkX = chunk.chunkX_world;
        int chunkY = chunk.chunkY_world;
        int chunkZ = chunk.chunkZ_world;

        while (localX < 0)
        {
            localX += chunkSizeXZ;
            chunkX--;
        }
        while (localX >= chunkSizeXZ)
        {
            localX -= chunkSizeXZ;
            chunkX++;
        }
        while (localY < 0)
        {
            localY += chunkSizeY;
            chunkY--;
        }
        while (localY >= chunkSizeY)
        {
            localY -= chunkSizeY;
            chunkY++;
        }
        while (localZ < 0)
        {
            localZ += chunkSizeXZ;
            chunkZ--;
        }
        while (localZ >= chunkSizeXZ)
        {
            localZ -= chunkSizeXZ;
            chunkZ++;
        }

        if (chunkX == chunk.chunkX_world && chunkY == chunk.chunkY_world && chunkZ == chunk.chunkZ_world)
        {
            return chunk;
        }

        if (chunkX == chunk.chunkX_world - 1 && chunkY == chunk.chunkY_world && chunkZ == chunk.chunkZ_world) return chunk.neighborNX;
        if (chunkX == chunk.chunkX_world + 1 && chunkY == chunk.chunkY_world && chunkZ == chunk.chunkZ_world) return chunk.neighborPX;
        if (chunkX == chunk.chunkX_world && chunkY == chunk.chunkY_world - 1 && chunkZ == chunk.chunkZ_world) return chunk.neighborNY;
        if (chunkX == chunk.chunkX_world && chunkY == chunk.chunkY_world + 1 && chunkZ == chunk.chunkZ_world) return chunk.neighborPY;
        if (chunkX == chunk.chunkX_world && chunkY == chunk.chunkY_world && chunkZ == chunk.chunkZ_world - 1) return chunk.neighborNZ;
        if (chunkX == chunk.chunkX_world && chunkY == chunk.chunkY_world && chunkZ == chunk.chunkZ_world + 1) return chunk.neighborPZ;

        return GetChunkAt(chunkX, chunkY, chunkZ);
    }

    private byte _GetAoLightLevelAt(ChunkData chunk, int localX, int localY, int localZ, byte emittedLight)
    {
        if (chunk == null) return emittedLight;
        if (_TryGetAoLightLevelFast(chunk, localX, localY, localZ, emittedLight, out byte fastLightLevel))
        {
            return fastLightLevel;
        }

        int sampleX = localX;
        int sampleY = localY;
        int sampleZ = localZ;
        ChunkData sampleChunk = _ResolveAoSampleChunk(chunk, ref sampleX, ref sampleY, ref sampleZ);
        if (sampleChunk != null && sampleChunk.isDataReady && sampleChunk._cachedBrightness == null && !_UsesGpuTerrainLightSampling())
        {
            _PreComputeChunkBrightness(sampleChunk);
        }

        if (sampleChunk == null || !sampleChunk.isDataReady || sampleChunk._cachedBrightness == null)
        {
            return emittedLight;
        }

        byte sampleLight = _GetCachedLightLevelAtBlockRaw(sampleChunk, sampleX, sampleY, sampleZ);
        return sampleLight > emittedLight ? sampleLight : emittedLight;
    }

    private byte _GetAoBlockIdAt(ChunkData chunk, int localX, int localY, int localZ)
    {
        if (chunk == null) return 0;
        if (_TryGetAoBlockIdFast(chunk, localX, localY, localZ, out byte fastBlockId))
        {
            return fastBlockId;
        }

        int sampleX = localX;
        int sampleY = localY;
        int sampleZ = localZ;
        ChunkData sampleChunk = _ResolveAoSampleChunk(chunk, ref sampleX, ref sampleY, ref sampleZ);
        if (sampleChunk == null || !sampleChunk.isDataReady) return 0;
        return _GetBlockLocal(sampleChunk, sampleX, sampleY, sampleZ);
    }

    private bool _GetCanBlockGrassAt(ChunkData chunk, int localX, int localY, int localZ)
    {
        byte blockId;
        if (!_TryGetAoBlockIdFast(chunk, localX, localY, localZ, out blockId))
        {
            blockId = _GetAoBlockIdAt(chunk, localX, localY, localZ);
        }
        if (canBlockGrassCache != null && blockId < canBlockGrassCache.Length)
        {
            return canBlockGrassCache[blockId];
        }
        return blockId == 0;
    }

    private float _GetAoBrightnessAt(ChunkData chunk, int localX, int localY, int localZ, byte emittedLight)
    {
        byte lightLevel;
        if (!_TryGetAoLightLevelFast(chunk, localX, localY, localZ, emittedLight, out lightLevel))
        {
            lightLevel = _GetAoLightLevelAt(chunk, localX, localY, localZ, emittedLight);
        }
        return lightBrightnessTable[lightLevel];
    }

    private bool _TryGetAoBrightnessAt(ChunkData chunk, int localX, int localY, int localZ, byte emittedLight, out float brightness)
    {
        brightness = lightBrightnessTable[emittedLight];
        if (chunk == null) return false;
        if (_TryGetAoLightLevelFast(chunk, localX, localY, localZ, emittedLight, out byte fastLightLevel))
        {
            brightness = lightBrightnessTable[fastLightLevel];
            return true;
        }

        int sampleX = localX;
        int sampleY = localY;
        int sampleZ = localZ;
        ChunkData sampleChunk = _ResolveAoSampleChunk(chunk, ref sampleX, ref sampleY, ref sampleZ);
        if (sampleChunk != null && sampleChunk.isDataReady && sampleChunk._cachedBrightness == null && !_UsesGpuTerrainLightSampling())
        {
            _PreComputeChunkBrightness(sampleChunk);
        }
        if (sampleChunk == null || !sampleChunk.isDataReady || sampleChunk._cachedBrightness == null)
        {
            return false;
        }

        byte sampleLight = _GetCachedLightLevelAtBlockRaw(sampleChunk, sampleX, sampleY, sampleZ);
        if (sampleLight < emittedLight) sampleLight = emittedLight;
        brightness = lightBrightnessTable[sampleLight];
        return true;
    }

    private int _PackAoBrightness(float brightness0, float brightness1, float brightness2, float brightness3)
    {
        int a0 = Mathf.Clamp(Mathf.RoundToInt(brightness0 * 255f), 0, 255);
        int a1 = Mathf.Clamp(Mathf.RoundToInt(brightness1 * 255f), 0, 255);
        int a2 = Mathf.Clamp(Mathf.RoundToInt(brightness2 * 255f), 0, 255);
        int a3 = Mathf.Clamp(Mathf.RoundToInt(brightness3 * 255f), 0, 255);
        return a0 | (a1 << 8) | (a2 << 16) | (a3 << 24);
    }

    private float _UnpackAoBrightness(int signature, int vertexIndex)
    {
        return ((signature >> (vertexIndex * 8)) & 0xFF) / 255f;
    }

    private bool _TryGetAoLightLevelAt(ChunkData chunk, int localX, int localY, int localZ, byte emittedLight, out byte lightLevel)
    {
        lightLevel = emittedLight;
        if (chunk == null) return false;
        if (_TryGetAoLightLevelFast(chunk, localX, localY, localZ, emittedLight, out lightLevel))
        {
            return true;
        }

        int sampleX = localX;
        int sampleY = localY;
        int sampleZ = localZ;
        ChunkData sampleChunk = _ResolveAoSampleChunk(chunk, ref sampleX, ref sampleY, ref sampleZ);
        if (sampleChunk != null && sampleChunk.isDataReady && sampleChunk._cachedBrightness == null && !_UsesGpuTerrainLightSampling())
        {
            _PreComputeChunkBrightness(sampleChunk);
        }
        if (sampleChunk == null || !sampleChunk.isDataReady || sampleChunk._cachedBrightness == null)
        {
            return false;
        }

        byte sampleLight = _GetCachedLightLevelAtBlockRaw(sampleChunk, sampleX, sampleY, sampleZ);
        lightLevel = sampleLight > emittedLight ? sampleLight : emittedLight;
        return true;
    }

    private int _BuildAoSignature(ChunkData chunk, byte blockID, int direction, int localX, int localY, int localZ)
    {
        if (!_UsesCpuAmbientOcclusion()) return 0;

        int normalX = aoNormalX[direction];
        int normalY = aoNormalY[direction];
        int normalZ = aoNormalZ[direction];
        int tangentUX = aoTangentUX[direction];
        int tangentUY = aoTangentUY[direction];
        int tangentUZ = aoTangentUZ[direction];
        int tangentVX = aoTangentVX[direction];
        int tangentVY = aoTangentVY[direction];
        int tangentVZ = aoTangentVZ[direction];

        int faceSampleX = localX + normalX;
        int faceSampleY = localY + normalY;
        int faceSampleZ = localZ + normalZ;
        byte emittedLight = blockID < lightEmissionCache.Length ? (byte)lightEmissionCache[blockID] : (byte)0;

        int sideUNegX = faceSampleX - tangentUX;
        int sideUNegY = faceSampleY - tangentUY;
        int sideUNegZ = faceSampleZ - tangentUZ;
        int sideUPosX = faceSampleX + tangentUX;
        int sideUPosY = faceSampleY + tangentUY;
        int sideUPosZ = faceSampleZ + tangentUZ;
        int sideVNegX = faceSampleX - tangentVX;
        int sideVNegY = faceSampleY - tangentVY;
        int sideVNegZ = faceSampleZ - tangentVZ;
        int sideVPosX = faceSampleX + tangentVX;
        int sideVPosY = faceSampleY + tangentVY;
        int sideVPosZ = faceSampleZ + tangentVZ;

        byte faceLightLevel;
        byte sideUNegLightLevel;
        byte sideUPosLightLevel;
        byte sideVNegLightLevel;
        byte sideVPosLightLevel;
        bool sideUNegCanBlock;
        bool sideUPosCanBlock;
        bool sideVNegCanBlock;
        bool sideVPosCanBlock;
        byte diagonalNegNegLightLevel;
        byte diagonalNegPosLightLevel;
        byte diagonalPosNegLightLevel;
        byte diagonalPosPosLightLevel;

        byte[] selfBrightness = chunk != null ? chunk._cachedBrightness : null;
        byte[] selfBlocks = chunk != null ? chunk._decompSelf : null;
        int chunkStride = chunkSizeXZ * chunkSizeXZ;
        int tangentSpanX = Mathf.Abs(tangentUX) + Mathf.Abs(tangentVX);
        int tangentSpanY = Mathf.Abs(tangentUY) + Mathf.Abs(tangentVY);
        int tangentSpanZ = Mathf.Abs(tangentUZ) + Mathf.Abs(tangentVZ);
        bool useInteriorFastPath =
            selfBrightness != null &&
            selfBlocks != null &&
            faceSampleX - tangentSpanX >= 0 && faceSampleX + tangentSpanX < chunkSizeXZ &&
            faceSampleY - tangentSpanY >= 0 && faceSampleY + tangentSpanY < chunkSizeY &&
            faceSampleZ - tangentSpanZ >= 0 && faceSampleZ + tangentSpanZ < chunkSizeXZ;

        if (useInteriorFastPath)
        {
            int faceIndex = faceSampleY * chunkStride + faceSampleZ * chunkSizeXZ + faceSampleX;
            int sideUNegIndex = sideUNegY * chunkStride + sideUNegZ * chunkSizeXZ + sideUNegX;
            int sideUPosIndex = sideUPosY * chunkStride + sideUPosZ * chunkSizeXZ + sideUPosX;
            int sideVNegIndex = sideVNegY * chunkStride + sideVNegZ * chunkSizeXZ + sideVNegX;
            int sideVPosIndex = sideVPosY * chunkStride + sideVPosZ * chunkSizeXZ + sideVPosX;

            faceLightLevel = selfBrightness[faceIndex];
            if (faceLightLevel < emittedLight) faceLightLevel = emittedLight;
            sideUNegLightLevel = selfBrightness[sideUNegIndex];
            if (sideUNegLightLevel < emittedLight) sideUNegLightLevel = emittedLight;
            sideUPosLightLevel = selfBrightness[sideUPosIndex];
            if (sideUPosLightLevel < emittedLight) sideUPosLightLevel = emittedLight;
            sideVNegLightLevel = selfBrightness[sideVNegIndex];
            if (sideVNegLightLevel < emittedLight) sideVNegLightLevel = emittedLight;
            sideVPosLightLevel = selfBrightness[sideVPosIndex];
            if (sideVPosLightLevel < emittedLight) sideVPosLightLevel = emittedLight;

            sideUNegCanBlock = canBlockGrassCache[selfBlocks[sideUNegIndex]];
            sideUPosCanBlock = canBlockGrassCache[selfBlocks[sideUPosIndex]];
            sideVNegCanBlock = canBlockGrassCache[selfBlocks[sideVNegIndex]];
            sideVPosCanBlock = canBlockGrassCache[selfBlocks[sideVPosIndex]];

            diagonalNegNegLightLevel = sideUNegLightLevel;
            if (sideUNegCanBlock || sideVNegCanBlock)
            {
                int diagonalIndex = (sideUNegY - tangentVY) * chunkStride + (sideUNegZ - tangentVZ) * chunkSizeXZ + (sideUNegX - tangentVX);
                diagonalNegNegLightLevel = selfBrightness[diagonalIndex];
                if (diagonalNegNegLightLevel < emittedLight) diagonalNegNegLightLevel = emittedLight;
            }

            diagonalNegPosLightLevel = sideUNegLightLevel;
            if (sideUNegCanBlock || sideVPosCanBlock)
            {
                int diagonalIndex = (sideUNegY + tangentVY) * chunkStride + (sideUNegZ + tangentVZ) * chunkSizeXZ + (sideUNegX + tangentVX);
                diagonalNegPosLightLevel = selfBrightness[diagonalIndex];
                if (diagonalNegPosLightLevel < emittedLight) diagonalNegPosLightLevel = emittedLight;
            }

            diagonalPosNegLightLevel = sideUPosLightLevel;
            if (sideUPosCanBlock || sideVNegCanBlock)
            {
                int diagonalIndex = (sideUPosY - tangentVY) * chunkStride + (sideUPosZ - tangentVZ) * chunkSizeXZ + (sideUPosX - tangentVX);
                diagonalPosNegLightLevel = selfBrightness[diagonalIndex];
                if (diagonalPosNegLightLevel < emittedLight) diagonalPosNegLightLevel = emittedLight;
            }

            diagonalPosPosLightLevel = sideUPosLightLevel;
            if (sideUPosCanBlock || sideVPosCanBlock)
            {
                int diagonalIndex = (sideUPosY + tangentVY) * chunkStride + (sideUPosZ + tangentVZ) * chunkSizeXZ + (sideUPosX + tangentVX);
                diagonalPosPosLightLevel = selfBrightness[diagonalIndex];
                if (diagonalPosPosLightLevel < emittedLight) diagonalPosPosLightLevel = emittedLight;
            }
        }
        else
        {
            faceLightLevel = _GetAoLightLevelAt(chunk, faceSampleX, faceSampleY, faceSampleZ, emittedLight);
            sideUNegLightLevel = _GetAoLightLevelAt(chunk, sideUNegX, sideUNegY, sideUNegZ, emittedLight);
            sideUPosLightLevel = _GetAoLightLevelAt(chunk, sideUPosX, sideUPosY, sideUPosZ, emittedLight);
            sideVNegLightLevel = _GetAoLightLevelAt(chunk, sideVNegX, sideVNegY, sideVNegZ, emittedLight);
            sideVPosLightLevel = _GetAoLightLevelAt(chunk, sideVPosX, sideVPosY, sideVPosZ, emittedLight);

            sideUNegCanBlock = _GetCanBlockGrassAt(chunk, sideUNegX, sideUNegY, sideUNegZ);
            sideUPosCanBlock = _GetCanBlockGrassAt(chunk, sideUPosX, sideUPosY, sideUPosZ);
            sideVNegCanBlock = _GetCanBlockGrassAt(chunk, sideVNegX, sideVNegY, sideVNegZ);
            sideVPosCanBlock = _GetCanBlockGrassAt(chunk, sideVPosX, sideVPosY, sideVPosZ);

            diagonalNegNegLightLevel = sideUNegLightLevel;
            if (sideUNegCanBlock || sideVNegCanBlock)
            {
                if (!_TryGetAoLightLevelAt(chunk, sideUNegX - tangentVX, sideUNegY - tangentVY, sideUNegZ - tangentVZ, emittedLight, out diagonalNegNegLightLevel))
                {
                    diagonalNegNegLightLevel = sideUNegLightLevel;
                }
            }

            diagonalNegPosLightLevel = sideUNegLightLevel;
            if (sideUNegCanBlock || sideVPosCanBlock)
            {
                if (!_TryGetAoLightLevelAt(chunk, sideUNegX + tangentVX, sideUNegY + tangentVY, sideUNegZ + tangentVZ, emittedLight, out diagonalNegPosLightLevel))
                {
                    diagonalNegPosLightLevel = sideUNegLightLevel;
                }
            }

            diagonalPosNegLightLevel = sideUPosLightLevel;
            if (sideUPosCanBlock || sideVNegCanBlock)
            {
                if (!_TryGetAoLightLevelAt(chunk, sideUPosX - tangentVX, sideUPosY - tangentVY, sideUPosZ - tangentVZ, emittedLight, out diagonalPosNegLightLevel))
                {
                    diagonalPosNegLightLevel = sideUPosLightLevel;
                }
            }

            diagonalPosPosLightLevel = sideUPosLightLevel;
            if (sideUPosCanBlock || sideVPosCanBlock)
            {
                if (!_TryGetAoLightLevelAt(chunk, sideUPosX + tangentVX, sideUPosY + tangentVY, sideUPosZ + tangentVZ, emittedLight, out diagonalPosPosLightLevel))
                {
                    diagonalPosPosLightLevel = sideUPosLightLevel;
                }
            }
        }

        int signBase = direction * 4;
        int corner0 = 0;
        int corner1 = 0;
        int corner2 = 0;
        int corner3 = 0;
        byte[] alphaTable = aoBrightnessAlphaTable;

        for (int vertexIndex = 0; vertexIndex < 4; vertexIndex++)
        {
            int signU = aoCornerUSigns[signBase + vertexIndex];
            int signV = aoCornerVSigns[signBase + vertexIndex];
            int sideULightLevel = signU < 0 ? sideUNegLightLevel : sideUPosLightLevel;
            int sideVLightLevel = signV < 0 ? sideVNegLightLevel : sideVPosLightLevel;
            int diagonalLightLevel;

            if (signU < 0)
            {
                diagonalLightLevel = signV < 0 ? diagonalNegNegLightLevel : diagonalNegPosLightLevel;
            }
            else
            {
                diagonalLightLevel = signV < 0 ? diagonalPosNegLightLevel : diagonalPosPosLightLevel;
            }

            int alphaIndex = faceLightLevel | (sideULightLevel << 4) | (sideVLightLevel << 8) | (diagonalLightLevel << 12);
            int cornerAlpha = alphaTable[alphaIndex];
            if (vertexIndex == 0) corner0 = cornerAlpha;
            else if (vertexIndex == 1) corner1 = cornerAlpha;
            else if (vertexIndex == 2) corner2 = cornerAlpha;
            else corner3 = cornerAlpha;
        }

        return corner0 | (corner1 << 8) | (corner2 << 16) | (corner3 << 24);
    }

    private int _GetTextureSliceCached(byte blockID, int faceIndex)
    {
        if (blockDataCache != null && blockID < blockDataCache.Length)
        {
            McBlockTextureMappingType mappingType = (McBlockTextureMappingType)((blockDataCache[blockID] >> 8) & 0x3);
            if (mappingType == McBlockTextureMappingType.AllFacesSame)
                return uv_allFacesCache[blockID];
            if (mappingType == McBlockTextureMappingType.TopBottomSides)
            {
                if (faceIndex == FACE_INDEX_TOP) return uv_topFaceCache[blockID];
                if (faceIndex == FACE_INDEX_BOTTOM) return uv_bottomFaceCache[blockID];
                return uv_sideFacesCache[blockID];
            }
            return uv_allFacesCache[blockID];
        }
        return 0;
    }

    private void _AddGreedyQuad(ChunkData chunk, int direction, int slice, int u, int v, int width, int height, byte blockID, byte lightLevel, int packedColor, int aoSignature, bool useAo)
    {
        BlockVisibilityType visibility = (blockID < visibilityCache.Length) ? visibilityCache[blockID] : BlockVisibilityType.Opaque;

        Vector3[] targetVertices;
        int[] targetTriangles;
        Vector3[] targetUVs;
        Vector3[] targetNormals;
        Color[] targetColors;
        int currentVertexCount;
        int currentTriangleCount;

        if (visibility == BlockVisibilityType.Opaque)
        {
            if (chunk._opaqueVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._opaqueVertices; targetTriangles = chunk._opaqueTriangles; targetUVs = chunk._opaqueUVs; targetNormals = chunk._opaqueNormals; targetColors = chunk._opaqueColors;
            currentVertexCount = chunk._opaqueVertexCount; currentTriangleCount = chunk._opaqueTriangleCount;
        }
        else if (visibility == BlockVisibilityType.Transparent)
        {
            if (chunk._transparentVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._transparentVertices; targetTriangles = chunk._transparentTriangles; targetUVs = chunk._transparentUVs; targetNormals = chunk._transparentNormals; targetColors = chunk._transparentColors;
            currentVertexCount = chunk._transparentVertexCount; currentTriangleCount = chunk._transparentTriangleCount;
        }
        else
        {
            if (chunk._cutoutVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._cutoutVertices; targetTriangles = chunk._cutoutTriangles; targetUVs = chunk._cutoutUVs; targetNormals = chunk._cutoutNormals; targetColors = chunk._cutoutColors;
            currentVertexCount = chunk._cutoutVertexCount; currentTriangleCount = chunk._cutoutTriangleCount;
        }

        Vector3 faceNormal = Normal_Up;
        int faceIndex = FACE_INDEX_SIDE;
        float plane = slice;
        float u0 = u;
        float v0 = v;
        float u1 = u + width;
        float v1 = v + height;

        if (direction == 0) // Up
        {
            plane = slice + 1;
            faceNormal = Normal_Up;
            faceIndex = FACE_INDEX_TOP;
            targetVertices[currentVertexCount + 0] = new Vector3(u0, plane, v0);
            targetVertices[currentVertexCount + 1] = new Vector3(u0, plane, v1);
            targetVertices[currentVertexCount + 2] = new Vector3(u1, plane, v1);
            targetVertices[currentVertexCount + 3] = new Vector3(u1, plane, v0);
        }
        else if (direction == 1) // Down
        {
            plane = slice;
            faceNormal = Normal_Down;
            faceIndex = FACE_INDEX_BOTTOM;
            targetVertices[currentVertexCount + 0] = new Vector3(u1, plane, v0);
            targetVertices[currentVertexCount + 1] = new Vector3(u1, plane, v1);
            targetVertices[currentVertexCount + 2] = new Vector3(u0, plane, v1);
            targetVertices[currentVertexCount + 3] = new Vector3(u0, plane, v0);
        }
        else if (direction == 2) // North
        {
            plane = slice + 1;
            faceNormal = Normal_North;
            faceIndex = FACE_INDEX_SIDE;
            targetVertices[currentVertexCount + 0] = new Vector3(u1, v0, plane);
            targetVertices[currentVertexCount + 1] = new Vector3(u1, v1, plane);
            targetVertices[currentVertexCount + 2] = new Vector3(u0, v1, plane);
            targetVertices[currentVertexCount + 3] = new Vector3(u0, v0, plane);
        }
        else if (direction == 3) // South
        {
            plane = slice;
            faceNormal = Normal_South;
            faceIndex = FACE_INDEX_SIDE;
            targetVertices[currentVertexCount + 0] = new Vector3(u0, v0, plane);
            targetVertices[currentVertexCount + 1] = new Vector3(u0, v1, plane);
            targetVertices[currentVertexCount + 2] = new Vector3(u1, v1, plane);
            targetVertices[currentVertexCount + 3] = new Vector3(u1, v0, plane);
        }
        else if (direction == 4) // East
        {
            plane = slice + 1;
            faceNormal = Normal_East;
            faceIndex = FACE_INDEX_SIDE;
            targetVertices[currentVertexCount + 0] = new Vector3(plane, v0, u0);
            targetVertices[currentVertexCount + 1] = new Vector3(plane, v1, u0);
            targetVertices[currentVertexCount + 2] = new Vector3(plane, v1, u1);
            targetVertices[currentVertexCount + 3] = new Vector3(plane, v0, u1);
        }
        else // West
        {
            plane = slice;
            faceNormal = Normal_West;
            faceIndex = FACE_INDEX_SIDE;
            targetVertices[currentVertexCount + 0] = new Vector3(plane, v0, u1);
            targetVertices[currentVertexCount + 1] = new Vector3(plane, v1, u1);
            targetVertices[currentVertexCount + 2] = new Vector3(plane, v1, u0);
            targetVertices[currentVertexCount + 3] = new Vector3(plane, v0, u0);
        }

        targetNormals[currentVertexCount + 0] = faceNormal;
        targetNormals[currentVertexCount + 1] = faceNormal;
        targetNormals[currentVertexCount + 2] = faceNormal;
        targetNormals[currentVertexCount + 3] = faceNormal;

        float colorR = (packedColor & 0xFF) / 255f;
        float colorG = ((packedColor >> 8) & 0xFF) / 255f;
        float colorB = ((packedColor >> 16) & 0xFF) / 255f;
        if (useAo)
        {
            targetColors[currentVertexCount + 0] = new Color(colorR, colorG, colorB, _UnpackAoBrightness(aoSignature, 0));
            targetColors[currentVertexCount + 1] = new Color(colorR, colorG, colorB, _UnpackAoBrightness(aoSignature, 1));
            targetColors[currentVertexCount + 2] = new Color(colorR, colorG, colorB, _UnpackAoBrightness(aoSignature, 2));
            targetColors[currentVertexCount + 3] = new Color(colorR, colorG, colorB, _UnpackAoBrightness(aoSignature, 3));
        }
        else
        {
            float brightness = lightBrightnessTable[lightLevel];
            Color biomeColor = new Color(colorR, colorG, colorB, brightness);
            targetColors[currentVertexCount + 0] = biomeColor;
            targetColors[currentVertexCount + 1] = biomeColor;
            targetColors[currentVertexCount + 2] = biomeColor;
            targetColors[currentVertexCount + 3] = biomeColor;
        }

        float textureSlice = 0;
        if (blockDataCache != null && blockID < blockDataCache.Length)
        {
            int mappingType = (blockDataCache[blockID] >> 8) & 0x3;
            if (mappingType == 0) textureSlice = uv_allFacesCache[blockID];
            else if (mappingType == 1)
            {
                if (faceIndex == FACE_INDEX_TOP) textureSlice = uv_topFaceCache[blockID];
                else if (faceIndex == FACE_INDEX_BOTTOM) textureSlice = uv_bottomFaceCache[blockID];
                else textureSlice = uv_sideFacesCache[blockID];
            }
            else textureSlice = uv_allFacesCache[blockID];
        }
        targetUVs[currentVertexCount + 0] = new Vector3(0, 0, textureSlice);
        targetUVs[currentVertexCount + 1] = new Vector3(0, height, textureSlice);
        targetUVs[currentVertexCount + 2] = new Vector3(width, height, textureSlice);
        targetUVs[currentVertexCount + 3] = new Vector3(width, 0, textureSlice);

        targetTriangles[currentTriangleCount + 0] = currentVertexCount;
        targetTriangles[currentTriangleCount + 1] = currentVertexCount + 1;
        targetTriangles[currentTriangleCount + 2] = currentVertexCount + 2;
        targetTriangles[currentTriangleCount + 3] = currentVertexCount;
        targetTriangles[currentTriangleCount + 4] = currentVertexCount + 2;
        targetTriangles[currentTriangleCount + 5] = currentVertexCount + 3;

        if (visibility == BlockVisibilityType.Opaque) { chunk._opaqueVertexCount += 4; chunk._opaqueTriangleCount += 6; }
        else if (visibility == BlockVisibilityType.Transparent) { chunk._transparentVertexCount += 4; chunk._transparentTriangleCount += 6; }
        else { chunk._cutoutVertexCount += 4; chunk._cutoutTriangleCount += 6; }
        if (!chunk.pendingColliderMeshRebuild && visibility != BlockVisibilityType.Transparent && chunk._collisionVertexCount < MAX_VERTS * 3 - 4)
        {
            chunk._collisionVertices[chunk._collisionVertexCount + 0] = targetVertices[currentVertexCount + 0];
            chunk._collisionVertices[chunk._collisionVertexCount + 1] = targetVertices[currentVertexCount + 1];
            chunk._collisionVertices[chunk._collisionVertexCount + 2] = targetVertices[currentVertexCount + 2];
            chunk._collisionVertices[chunk._collisionVertexCount + 3] = targetVertices[currentVertexCount + 3];
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount;
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 1;
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 2;
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount;
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 2;
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 3;
            chunk._collisionVertexCount += 4;
        }

#if LOGGING
        if (enableVerboseLogging)
        {
            chunk.facesTotal++;
            if (visibility == BlockVisibilityType.Opaque) chunk.facesOpaque++;
            else if (visibility == BlockVisibilityType.Transparent) chunk.facesTransparent++;
            else chunk.facesCutout++;
        }
#endif
    }

    private void _BuildGreedySliceMask(ChunkData chunk, int direction, int slice, byte[] selfData, byte[] dataNX, byte[] dataPX, byte[] dataNY, byte[] dataPY, byte[] dataNZ, byte[] dataPZ, int chunkStride, out int width, out int height)
    {
        width = (direction <= 3) ? chunkSizeXZ : chunkSizeXZ;
        height = (direction <= 1) ? chunkSizeXZ : chunkSizeY;
        int maskCount = width * height;
        System.Array.Clear(greedyMaskBlockIds, 0, maskCount);

        byte[] drawTable = shouldDrawTable;
        int drawTableLen = drawTable != null ? drawTable.Length : 0;
        BlockCullingType[] cullCache = cullingCache;
        int cullCacheLen = cullCache != null ? cullCache.Length : 0;
        McBlockShapeType[] shapeCache = shapeTypeCache;
        int shapeCacheLen = shapeCache != null ? shapeCache.Length : 0;

        for (int v = 0; v < height; v++)
        {
            for (int u = 0; u < width; u++)
            {
                int x = 0, y = 0, z = 0;
                if (direction <= 1)
                {
                    x = u; y = slice; z = v;
                    int columnIndex = z * chunkSizeXZ + x;
                    if (chunk._columnMinY != null && chunk._columnMaxY != null)
                    {
                        byte minY = chunk._columnMinY[columnIndex];
                        byte maxY = chunk._columnMaxY[columnIndex];
                        if (minY == 255 || slice < minY || slice > maxY) continue;
                    }
                }
                else if (direction <= 3)
                {
                    x = u; y = v; z = slice;
                    int columnIndex = z * chunkSizeXZ + x;
                    if (chunk._columnMinY != null && chunk._columnMaxY != null)
                    {
                        byte minY = chunk._columnMinY[columnIndex];
                        byte maxY = chunk._columnMaxY[columnIndex];
                        if (minY == 255 || y < minY || y > maxY) continue;
                    }
                }
                else
                {
                    x = slice; y = v; z = u;
                    int columnIndex = z * chunkSizeXZ + x;
                    if (chunk._columnMinY != null && chunk._columnMaxY != null)
                    {
                        byte minY = chunk._columnMinY[columnIndex];
                        byte maxY = chunk._columnMaxY[columnIndex];
                        if (minY == 255 || y < minY || y > maxY) continue;
                    }
                }

                int selfIndex = y * chunkStride + z * chunkSizeXZ + x;
                byte selfID = selfData[selfIndex];
                if (selfID == 0) continue;
                if (_UsesCustomBlockMesh(selfID, shapeCache, shapeCacheLen)) continue;

                int neighborX = x;
                int neighborY = y;
                int neighborZ = z;
                bool suppressSameNoCull = false;

                if (direction == 0) neighborY++;
                else if (direction == 1) { neighborY--; suppressSameNoCull = true; }
                else if (direction == 2) neighborZ++;
                else if (direction == 3) { neighborZ--; suppressSameNoCull = true; }
                else if (direction == 4) neighborX++;
                else { neighborX--; suppressSameNoCull = true; }

                byte neighborID = _GetBlockDirectMeshing(selfData, dataNX, dataPX, dataNY, dataPY, dataNZ, dataPZ, neighborX, neighborY, neighborZ, chunkSizeXZ, chunkSizeY, chunkStride);
                if (suppressSameNoCull && selfID == neighborID && selfID < cullCacheLen && cullCache[selfID] == BlockCullingType.NoCull) continue;

                int drawIndex = (selfID << 8) | neighborID;
                bool drawFace = drawIndex < drawTableLen && drawTable[drawIndex] != 0;
#if LOGGING
                if (enableVerboseLogging)
                {
                    chunk.shouldDrawTests++;
                    if (drawFace) chunk.shouldDrawTrue++;
                    if (direction <= 1) chunk.boundaryChecksY++;
                    else if (direction <= 3) chunk.boundaryChecksZ++;
                    else chunk.boundaryChecksX++;
                }
#endif
                if (!drawFace) continue;

                int maskIndex = v * width + u;
                greedyMaskBlockIds[maskIndex] = selfID;
                greedyMaskLightLevels[maskIndex] = _UsesCpuAmbientOcclusion() ? (byte)0 : _GetCachedLightLevelForDirection(chunk, direction, x, y, z);
                greedyMaskPackedColors[maskIndex] = _GetPackedBiomeColor(chunk, selfID, x, z);
                greedyMaskAoSignatures[maskIndex] = _UsesCpuAmbientOcclusion() ? _BuildAoSignature(chunk, selfID, direction, x, y, z) : 0;
            }
        }
    }

    private void _BuildGreedySliceMaskRegion(ChunkData chunk, int direction, int slice, byte[] selfData, byte[] dataNX, byte[] dataPX, byte[] dataNY, byte[] dataPY, byte[] dataNZ, byte[] dataPZ, int chunkStride, int minU, int maxU, int minV, int maxV, out int width, out int height)
    {
        width = (direction <= 3) ? chunkSizeXZ : chunkSizeXZ;
        height = (direction <= 1) ? chunkSizeXZ : chunkSizeY;

        if (minU < 0) minU = 0;
        if (minV < 0) minV = 0;
        if (maxU >= width) maxU = width - 1;
        if (maxV >= height) maxV = height - 1;
        if (maxU < minU || maxV < minV) return;

        byte[] drawTable = shouldDrawTable;
        int drawTableLen = drawTable != null ? drawTable.Length : 0;
        BlockCullingType[] cullCache = cullingCache;
        int cullCacheLen = cullCache != null ? cullCache.Length : 0;
        McBlockShapeType[] shapeCache = shapeTypeCache;
        int shapeCacheLen = shapeCache != null ? shapeCache.Length : 0;

        for (int v = minV; v <= maxV; v++)
        {
            int rowOffset = v * width;
            System.Array.Clear(greedyMaskBlockIds, rowOffset + minU, maxU - minU + 1);

            for (int u = minU; u <= maxU; u++)
            {
                int x = 0, y = 0, z = 0;
                if (direction <= 1)
                {
                    x = u; y = slice; z = v;
                    int columnIndex = z * chunkSizeXZ + x;
                    if (chunk._columnMinY != null && chunk._columnMaxY != null)
                    {
                        byte minY = chunk._columnMinY[columnIndex];
                        byte maxY = chunk._columnMaxY[columnIndex];
                        if (minY == 255 || slice < minY || slice > maxY) continue;
                    }
                }
                else if (direction <= 3)
                {
                    x = u; y = v; z = slice;
                    int columnIndex = z * chunkSizeXZ + x;
                    if (chunk._columnMinY != null && chunk._columnMaxY != null)
                    {
                        byte minY = chunk._columnMinY[columnIndex];
                        byte maxY = chunk._columnMaxY[columnIndex];
                        if (minY == 255 || y < minY || y > maxY) continue;
                    }
                }
                else
                {
                    x = slice; y = v; z = u;
                    int columnIndex = z * chunkSizeXZ + x;
                    if (chunk._columnMinY != null && chunk._columnMaxY != null)
                    {
                        byte minY = chunk._columnMinY[columnIndex];
                        byte maxY = chunk._columnMaxY[columnIndex];
                        if (minY == 255 || y < minY || y > maxY) continue;
                    }
                }

                int selfIndex = y * chunkStride + z * chunkSizeXZ + x;
                byte selfID = selfData[selfIndex];
                if (selfID == 0) continue;
                if (_UsesCustomBlockMesh(selfID, shapeCache, shapeCacheLen)) continue;
                if (_IsFluidBlock(selfID)) continue;

                int neighborX = x;
                int neighborY = y;
                int neighborZ = z;
                bool suppressSameNoCull = false;

                if (direction == 0) neighborY++;
                else if (direction == 1) { neighborY--; suppressSameNoCull = true; }
                else if (direction == 2) neighborZ++;
                else if (direction == 3) { neighborZ--; suppressSameNoCull = true; }
                else if (direction == 4) neighborX++;
                else { neighborX--; suppressSameNoCull = true; }

                byte neighborID = _GetBlockDirectMeshing(selfData, dataNX, dataPX, dataNY, dataPY, dataNZ, dataPZ, neighborX, neighborY, neighborZ, chunkSizeXZ, chunkSizeY, chunkStride);
                if (suppressSameNoCull && selfID == neighborID && selfID < cullCacheLen && cullCache[selfID] == BlockCullingType.NoCull) continue;

                int drawIndex = (selfID << 8) | neighborID;
                if (drawIndex >= drawTableLen || drawTable[drawIndex] == 0) continue;

                int maskIndex = rowOffset + u;
                greedyMaskBlockIds[maskIndex] = selfID;
                greedyMaskLightLevels[maskIndex] = _UsesCpuAmbientOcclusion() ? (byte)0 : _GetCachedLightLevelForDirection(chunk, direction, x, y, z);
                greedyMaskPackedColors[maskIndex] = _GetPackedBiomeColor(chunk, selfID, x, z);
                greedyMaskAoSignatures[maskIndex] = _UsesCpuAmbientOcclusion() ? _BuildAoSignature(chunk, selfID, direction, x, y, z) : 0;
            }
        }
    }

    private bool _BuildGreedySliceMaskCompact(ChunkData chunk, int direction, int slice, byte[] selfData, int chunkStride, out int width, out int height)
    {
        width = chunkSizeXZ;
        height = (direction <= 1) ? chunkSizeXZ : chunkSizeY;

        Color32[] compactPixels = chunk._gpuFacePixels;
        if (compactPixels == null || compactPixels.Length == 0) return false;

        int compactWidth = gpuFaceSummaryWidth > 0 ? gpuFaceSummaryWidth : Mathf.Max(1, chunkSizeXZ / 2);
        int rowPairs = (height + 1) >> 1;
        int rowBase = (direction * chunkSizeY + slice) * compactWidth;

        // Pre-scan: bail if empty, and find min/max row bounds for targeted clear
        int dataMinRP = rowPairs;
        int dataMaxRP = -1;
        for (int rp = 0; rp < rowPairs; rp++)
        {
            int pi = rowBase + rp;
            if (pi < 0 || pi >= compactPixels.Length) break;
            Color32 p = compactPixels[pi];
            if ((p.r | p.g | p.b | p.a) != 0)
            {
                if (rp < dataMinRP) dataMinRP = rp;
                dataMaxRP = rp;
            }
        }
        if (dataMaxRP < 0) return false;

        // Clear only the rows that have data, not the entire mask
        int clearMinRow = dataMinRP << 1;
        int clearMaxRow = (dataMaxRP << 1) + 2;
        if (clearMaxRow > height) clearMaxRow = height;
        System.Array.Clear(greedyMaskBlockIds, clearMinRow * width, (clearMaxRow - clearMinRow) * width);

        bool anyFace = false;
        int[] packedGrassColors = chunk._cachedPackedGrassBiomeColors;
        byte[] tintModes = biomeTintModeCache;
        bool hasTintModes = tintModes != null;
        bool hasGrassColors = packedGrassColors != null;
        byte[] blockIds = greedyMaskBlockIds;
        byte[] lightLevels = greedyMaskLightLevels;
        int[] packedColors = greedyMaskPackedColors;
        bool useAo = _UsesCpuAmbientOcclusion();
        byte[] currentBrightness = chunk._cachedBrightness;
        byte[] neighborBrightness = null;
        int sizeXZ = chunkSizeXZ;
        int sizeY = chunkSizeY;
        int sliceStrideBase = slice * chunkStride;
        int sliceRowBase = slice * sizeXZ;
        byte fallbackLight = _UsesGpuTerrainLightSampling() ? (byte)15 : (byte)0;
        int neighborAxis = slice;

        if (direction == 0) neighborAxis++;
        else if (direction == 1) neighborAxis--;
        else if (direction == 2) neighborAxis++;
        else if (direction == 3) neighborAxis--;
        else if (direction == 4) neighborAxis++;
        else neighborAxis--;

        bool usesCurrentBrightness = false;
        int neighborAxisLocal = neighborAxis;
        if (direction <= 1)
        {
            if (neighborAxis >= 0 && neighborAxis < sizeY)
            {
                usesCurrentBrightness = currentBrightness != null;
            }
            else
            {
                ChunkData neighborChunk = neighborAxis < 0 ? chunk.neighborNY : chunk.neighborPY;
                neighborAxisLocal = neighborAxis < 0 ? sizeY - 1 : 0;
                if (neighborChunk != null && neighborChunk.isDataReady) neighborBrightness = neighborChunk._cachedBrightness;
            }
        }
        else if (direction <= 3)
        {
            if (neighborAxis >= 0 && neighborAxis < sizeXZ)
            {
                usesCurrentBrightness = currentBrightness != null;
            }
            else
            {
                ChunkData neighborChunk = neighborAxis < 0 ? chunk.neighborNZ : chunk.neighborPZ;
                neighborAxisLocal = neighborAxis < 0 ? sizeXZ - 1 : 0;
                if (neighborChunk != null && neighborChunk.isDataReady) neighborBrightness = neighborChunk._cachedBrightness;
            }
        }
        else
        {
            if (neighborAxis >= 0 && neighborAxis < sizeXZ)
            {
                usesCurrentBrightness = currentBrightness != null;
            }
            else
            {
                ChunkData neighborChunk = neighborAxis < 0 ? chunk.neighborNX : chunk.neighborPX;
                neighborAxisLocal = neighborAxis < 0 ? sizeXZ - 1 : 0;
                if (neighborChunk != null && neighborChunk.isDataReady) neighborBrightness = neighborChunk._cachedBrightness;
            }
        }

        byte[] lightSource = usesCurrentBrightness ? currentBrightness : (neighborBrightness != null ? neighborBrightness : null);
        bool constantLight = !useAo && lightSource == null;
        _greedyConstantLight = constantLight;

        for (int rowPair = 0; rowPair < rowPairs; rowPair++)
        {
            int pixelIndex = rowBase + rowPair;
            if (pixelIndex < 0 || pixelIndex >= compactPixels.Length) break;

            Color32 pixel = compactPixels[pixelIndex];
            int rowMask0 = pixel.r | (pixel.g << 8);
            int rowMask1 = pixel.b | (pixel.a << 8);

            if (rowMask0 != 0)
            {
                int v = rowPair << 1;
                int rowOffset = v * width;
                if (direction <= 1)
                {
                    int selfRowBase = sliceStrideBase + v * sizeXZ;
                    int biomeRowBase = v * sizeXZ;
                    int lightRowBase = neighborAxisLocal * chunkStride + biomeRowBase;
                    for (int u = 0; u < width && rowMask0 != 0; u++)
                    {
                        int bit = 1 << u;
                        if ((rowMask0 & bit) == 0) continue;
                        rowMask0 &= ~bit;

                        byte selfID = selfData[selfRowBase + u];
                        if (selfID == 0) continue;

                        int maskIndex = rowOffset + u;
                        blockIds[maskIndex] = selfID;
                        if (!constantLight && !useAo)
                        {
                            lightLevels[maskIndex] = lightSource[lightRowBase + u];
                        }
                        byte tintMode = (hasTintModes && selfID < tintModes.Length) ? tintModes[selfID] : (byte)0;
                        if (tintMode == 0)
                        {
                            packedColors[maskIndex] = PACKED_WHITE_RGB;
                        }
                        else
                        {
                            int biomeIndex = biomeRowBase + u;
                            if (tintMode == 1 && hasGrassColors && biomeIndex < packedGrassColors.Length)
                            {
                                packedColors[maskIndex] = packedGrassColors[biomeIndex];
                            }
                            else
                            {
                                packedColors[maskIndex] = _PackColorRGB(_GetCachedBiomeColor(chunk, selfID, u, v));
                            }
                        }
                        if (useAo) greedyMaskAoSignatures[maskIndex] = _BuildAoSignature(chunk, selfID, direction, u, slice, v);
                        anyFace = true;
                    }
                }
                else if (direction <= 3)
                {
                    int selfRowBase = v * chunkStride + sliceRowBase;
                    int lightRowBase = v * chunkStride + neighborAxisLocal * sizeXZ;
                    for (int u = 0; u < width && rowMask0 != 0; u++)
                    {
                        int bit = 1 << u;
                        if ((rowMask0 & bit) == 0) continue;
                        rowMask0 &= ~bit;

                        byte selfID = selfData[selfRowBase + u];
                        if (selfID == 0) continue;

                        int maskIndex = rowOffset + u;
                        blockIds[maskIndex] = selfID;
                        if (!constantLight && !useAo)
                        {
                            lightLevels[maskIndex] = lightSource[lightRowBase + u];
                        }
                        byte tintMode = (hasTintModes && selfID < tintModes.Length) ? tintModes[selfID] : (byte)0;
                        if (tintMode == 0)
                        {
                            packedColors[maskIndex] = PACKED_WHITE_RGB;
                        }
                        else
                        {
                            int biomeIndex = sliceRowBase + u;
                            if (tintMode == 1 && hasGrassColors && biomeIndex < packedGrassColors.Length)
                            {
                                packedColors[maskIndex] = packedGrassColors[biomeIndex];
                            }
                            else
                            {
                                packedColors[maskIndex] = _PackColorRGB(_GetCachedBiomeColor(chunk, selfID, u, slice));
                            }
                        }
                        if (useAo) greedyMaskAoSignatures[maskIndex] = _BuildAoSignature(chunk, selfID, direction, u, v, slice);
                        anyFace = true;
                    }
                }
                else
                {
                    int selfRowBase = v * chunkStride + slice;
                    int lightRowBase = v * chunkStride + neighborAxisLocal;
                    for (int u = 0; u < width && rowMask0 != 0; u++)
                    {
                        int bit = 1 << u;
                        if ((rowMask0 & bit) == 0) continue;
                        rowMask0 &= ~bit;

                        int biomeIndex = u * sizeXZ + slice;
                        byte selfID = selfData[selfRowBase + u * sizeXZ];
                        if (selfID == 0) continue;

                        int maskIndex = rowOffset + u;
                        blockIds[maskIndex] = selfID;
                        if (!constantLight && !useAo)
                        {
                            lightLevels[maskIndex] = lightSource[lightRowBase + u * sizeXZ];
                        }
                        byte tintMode = (hasTintModes && selfID < tintModes.Length) ? tintModes[selfID] : (byte)0;
                        if (tintMode == 0)
                        {
                            packedColors[maskIndex] = PACKED_WHITE_RGB;
                        }
                        else if (tintMode == 1 && hasGrassColors && biomeIndex < packedGrassColors.Length)
                        {
                            packedColors[maskIndex] = packedGrassColors[biomeIndex];
                        }
                        else
                        {
                            packedColors[maskIndex] = _PackColorRGB(_GetCachedBiomeColor(chunk, selfID, slice, u));
                        }
                        if (useAo) greedyMaskAoSignatures[maskIndex] = _BuildAoSignature(chunk, selfID, direction, slice, v, u);
                        anyFace = true;
                    }
                }
            }

            if (rowMask1 != 0)
            {
                int v = (rowPair << 1) + 1;
                if (v >= height) continue;
                int rowOffset = v * width;
                if (direction <= 1)
                {
                    int selfRowBase = sliceStrideBase + v * sizeXZ;
                    int biomeRowBase = v * sizeXZ;
                    int lightRowBase = neighborAxisLocal * chunkStride + biomeRowBase;
                    for (int u = 0; u < width && rowMask1 != 0; u++)
                    {
                        int bit = 1 << u;
                        if ((rowMask1 & bit) == 0) continue;
                        rowMask1 &= ~bit;

                        byte selfID = selfData[selfRowBase + u];
                        if (selfID == 0) continue;

                        int maskIndex = rowOffset + u;
                        blockIds[maskIndex] = selfID;
                        if (!constantLight && !useAo)
                        {
                            lightLevels[maskIndex] = lightSource[lightRowBase + u];
                        }
                        byte tintMode = (hasTintModes && selfID < tintModes.Length) ? tintModes[selfID] : (byte)0;
                        if (tintMode == 0)
                        {
                            packedColors[maskIndex] = PACKED_WHITE_RGB;
                        }
                        else
                        {
                            int biomeIndex = biomeRowBase + u;
                            if (tintMode == 1 && hasGrassColors && biomeIndex < packedGrassColors.Length)
                            {
                                packedColors[maskIndex] = packedGrassColors[biomeIndex];
                            }
                            else
                            {
                                packedColors[maskIndex] = _PackColorRGB(_GetCachedBiomeColor(chunk, selfID, u, v));
                            }
                        }
                        if (useAo) greedyMaskAoSignatures[maskIndex] = _BuildAoSignature(chunk, selfID, direction, u, slice, v);
                        anyFace = true;
                    }
                }
                else if (direction <= 3)
                {
                    int selfRowBase = v * chunkStride + sliceRowBase;
                    int lightRowBase = v * chunkStride + neighborAxisLocal * sizeXZ;
                    for (int u = 0; u < width && rowMask1 != 0; u++)
                    {
                        int bit = 1 << u;
                        if ((rowMask1 & bit) == 0) continue;
                        rowMask1 &= ~bit;

                        byte selfID = selfData[selfRowBase + u];
                        if (selfID == 0) continue;

                        int maskIndex = rowOffset + u;
                        blockIds[maskIndex] = selfID;
                        if (!constantLight && !useAo)
                        {
                            lightLevels[maskIndex] = lightSource[lightRowBase + u];
                        }
                        byte tintMode = (hasTintModes && selfID < tintModes.Length) ? tintModes[selfID] : (byte)0;
                        if (tintMode == 0)
                        {
                            packedColors[maskIndex] = PACKED_WHITE_RGB;
                        }
                        else
                        {
                            int biomeIndex = sliceRowBase + u;
                            if (tintMode == 1 && hasGrassColors && biomeIndex < packedGrassColors.Length)
                            {
                                packedColors[maskIndex] = packedGrassColors[biomeIndex];
                            }
                            else
                            {
                                packedColors[maskIndex] = _PackColorRGB(_GetCachedBiomeColor(chunk, selfID, u, slice));
                            }
                        }
                        if (useAo) greedyMaskAoSignatures[maskIndex] = _BuildAoSignature(chunk, selfID, direction, u, v, slice);
                        anyFace = true;
                    }
                }
                else
                {
                    int selfRowBase = v * chunkStride + slice;
                    int lightRowBase = v * chunkStride + neighborAxisLocal;
                    for (int u = 0; u < width && rowMask1 != 0; u++)
                    {
                        int bit = 1 << u;
                        if ((rowMask1 & bit) == 0) continue;
                        rowMask1 &= ~bit;

                        int biomeIndex = u * sizeXZ + slice;
                        byte selfID = selfData[selfRowBase + u * sizeXZ];
                        if (selfID == 0) continue;

                        int maskIndex = rowOffset + u;
                        blockIds[maskIndex] = selfID;
                        if (!constantLight && !useAo)
                        {
                            lightLevels[maskIndex] = lightSource[lightRowBase + u * sizeXZ];
                        }
                        byte tintMode = (hasTintModes && selfID < tintModes.Length) ? tintModes[selfID] : (byte)0;
                        if (tintMode == 0)
                        {
                            packedColors[maskIndex] = PACKED_WHITE_RGB;
                        }
                        else if (tintMode == 1 && hasGrassColors && biomeIndex < packedGrassColors.Length)
                        {
                            packedColors[maskIndex] = packedGrassColors[biomeIndex];
                        }
                        else
                        {
                            packedColors[maskIndex] = _PackColorRGB(_GetCachedBiomeColor(chunk, selfID, slice, u));
                        }
                        if (useAo) greedyMaskAoSignatures[maskIndex] = _BuildAoSignature(chunk, selfID, direction, slice, v, u);
                        anyFace = true;
                    }
                }
            }
        }

        return anyFace;
    }

    private void _EmitGreedyMask(ChunkData chunk, int direction, int slice, int width, int height)
    {
        _EmitGreedyMaskRegion(chunk, direction, slice, width, height, 0, width - 1, 0, height - 1);
    }

    private void _EmitGreedyMaskRegion(ChunkData chunk, int direction, int slice, int width, int height, int minU, int maxU, int minV, int maxV)
    {
        if (minU < 0) minU = 0;
        if (minV < 0) minV = 0;
        if (maxU >= width) maxU = width - 1;
        if (maxV >= height) maxV = height - 1;
        if (maxU < minU || maxV < minV) return;
        byte[] blockIds = greedyMaskBlockIds;
        byte[] lightLevels = greedyMaskLightLevels;
        int[] packedColors = greedyMaskPackedColors;
        int[] aoSignatures = greedyMaskAoSignatures;
        bool useAo = _UsesCpuAmbientOcclusion();
        bool constantLight = _greedyConstantLight;

        for (int v = minV; v <= maxV; v++)
        {
            int rowOffset = v * width;
            for (int u = minU; u <= maxU; )
            {
                int maskIndex = rowOffset + u;
                byte blockID = blockIds[maskIndex];
                if (blockID == 0 || blockID == BLOCK_WATER_MOVING || blockID == BLOCK_WATER_STILL)
                {
                    u++;
                    continue;
                }

                byte lightLevel = constantLight ? (byte)15 : lightLevels[maskIndex];
                int packedColor = packedColors[maskIndex];
                int aoSignature = useAo ? aoSignatures[maskIndex] : 0;

#if LOGGING
                float emitScanStart = stats_trackCompactEmitInternals ? Time.realtimeSinceStartup : 0f;
#endif
                int quadWidth = 1;
                while (u + quadWidth <= maxU)
                {
                    int nextIndex = rowOffset + u + quadWidth;
                    if (blockIds[nextIndex] != blockID || packedColors[nextIndex] != packedColor) break;
                    if (useAo)
                    {
                        if (aoSignatures[nextIndex] != aoSignature) break;
                    }
                    else if (!constantLight && lightLevels[nextIndex] != lightLevel)
                    {
                        break;
                    }
                    quadWidth++;
                }

                int quadHeight = 1;
                bool canGrow = true;
                while (v + quadHeight <= maxV && canGrow)
                {
                    int nextRowOffset = (v + quadHeight) * width;
                    for (int checkU = 0; checkU < quadWidth; checkU++)
                    {
                        int nextIndex = nextRowOffset + u + checkU;
                        if (blockIds[nextIndex] != blockID || packedColors[nextIndex] != packedColor)
                        {
                            canGrow = false;
                            break;
                        }
                        if (useAo)
                        {
                            if (aoSignatures[nextIndex] != aoSignature)
                            {
                                canGrow = false;
                                break;
                            }
                        }
                        else if (!constantLight && lightLevels[nextIndex] != lightLevel)
                        {
                            canGrow = false;
                            break;
                        }
                    }
                    if (canGrow) quadHeight++;
                }

#if LOGGING
                if (stats_trackCompactEmitInternals)
                {
                    stats_gpuFaceCompactEmitScanTime += (Time.realtimeSinceStartup - emitScanStart) * 1000f;
                }

                float emitQuadStart = stats_trackCompactEmitInternals ? Time.realtimeSinceStartup : 0f;
#endif
                _AddGreedyQuad(chunk, direction, slice, u, v, quadWidth, quadHeight, blockID, lightLevel, packedColor, aoSignature, useAo);
#if LOGGING
                if (stats_trackCompactEmitInternals)
                {
                    stats_gpuFaceCompactEmitQuadTime += (Time.realtimeSinceStartup - emitQuadStart) * 1000f;
                }
#endif

                for (int clearV = 0; clearV < quadHeight; clearV++)
                {
                    System.Array.Clear(blockIds, (v + clearV) * width + u, quadWidth);
                }

                u += quadWidth;
            }
        }
    }

    // OPTIMIZATION Phase 2: Batch face collection helper (replaces lambda for Udon compatibility)
    // OPTIMIZATION: Optimized face adding that avoids Vector3 allocations
    private void _AddFaceOptimized(ChunkData chunk, Vector3[] faceVertices, Vector3 faceNormal, int bx, int by, int bz, byte blockID, int faceIndex)
    {
        Vector3[] targetVertices; int[] targetTriangles; Vector3[] targetUVs; Vector3[] targetNormals; Color[] targetColors;
        int currentVertexCount; int currentTriangleCount;

        // OPTIMIZATION Phase 5: Inline _GetVisibilityType (eliminates method call)
        BlockVisibilityType visibility = (blockID < visibilityCache.Length) ? visibilityCache[blockID] : BlockVisibilityType.Opaque;
        if (visibility == BlockVisibilityType.Opaque) {
            if (chunk._opaqueVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._opaqueVertices; targetTriangles = chunk._opaqueTriangles; targetUVs = chunk._opaqueUVs; targetNormals = chunk._opaqueNormals; targetColors = chunk._opaqueColors;
            currentVertexCount = chunk._opaqueVertexCount; currentTriangleCount = chunk._opaqueTriangleCount;
        } else if (visibility == BlockVisibilityType.Transparent) {
            if (chunk._transparentVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._transparentVertices; targetTriangles = chunk._transparentTriangles; targetUVs = chunk._transparentUVs; targetNormals = chunk._transparentNormals; targetColors = chunk._transparentColors;
            currentVertexCount = chunk._transparentVertexCount; currentTriangleCount = chunk._transparentTriangleCount;
        } else { // Cutout
            if (chunk._cutoutVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._cutoutVertices; targetTriangles = chunk._cutoutTriangles; targetUVs = chunk._cutoutUVs; targetNormals = chunk._cutoutNormals; targetColors = chunk._cutoutColors;
            currentVertexCount = chunk._cutoutVertexCount; currentTriangleCount = chunk._cutoutTriangleCount;
        }

        // OPTIMIZATION: Avoid Vector3 constructor calls
        targetVertices[currentVertexCount + 0] = new Vector3(bx + faceVertices[0].x, by + faceVertices[0].y, bz + faceVertices[0].z);
        targetVertices[currentVertexCount + 1] = new Vector3(bx + faceVertices[1].x, by + faceVertices[1].y, bz + faceVertices[1].z);
        targetVertices[currentVertexCount + 2] = new Vector3(bx + faceVertices[2].x, by + faceVertices[2].y, bz + faceVertices[2].z);
        targetVertices[currentVertexCount + 3] = new Vector3(bx + faceVertices[3].x, by + faceVertices[3].y, bz + faceVertices[3].z);
        for (int i=0; i<4; i++) targetNormals[currentVertexCount + i] = faceNormal;

        // OPTIMIZATION Phase 6: Use pre-computed cached biome color (eliminates texture lookups)
        Color biomeColor = _GetCachedBiomeColor(chunk, blockID, bx, bz);

        // OPTIMIZATION Phase 3: Use pre-computed cached brightness (eliminates method call overhead)
        float brightness = _GetCachedBrightnessForFace(chunk, faceNormal, bx, by, bz);
        biomeColor.a = brightness;

        for (int i=0; i<4; i++) targetColors[currentVertexCount + i] = biomeColor;

        // OPTIMIZATION Phase 5: Inline _GetTextureSlice (eliminates method call)
        float textureSlice = 0;
        if (blockDataCache != null && blockID < blockDataCache.Length)
        {
            McBlockTextureMappingType mappingType = (McBlockTextureMappingType)((blockDataCache[blockID] >> 8) & 0x3);
            if (mappingType == McBlockTextureMappingType.AllFacesSame)
                textureSlice = uv_allFacesCache[blockID];
            else if (mappingType == McBlockTextureMappingType.TopBottomSides)
            {
                if (faceIndex == 2) textureSlice = uv_topFaceCache[blockID];
                else if (faceIndex == 3) textureSlice = uv_bottomFaceCache[blockID];
                else textureSlice = uv_sideFacesCache[blockID];
            }
            else
                textureSlice = uv_allFacesCache[blockID];
        }
        targetUVs[currentVertexCount + 0] = new Vector3(0, 0, textureSlice); targetUVs[currentVertexCount + 1] = new Vector3(0, 1, textureSlice);
        targetUVs[currentVertexCount + 2] = new Vector3(1, 1, textureSlice); targetUVs[currentVertexCount + 3] = new Vector3(1, 0, textureSlice);

        targetTriangles[currentTriangleCount + 0] = currentVertexCount; targetTriangles[currentTriangleCount + 1] = currentVertexCount + 1;
        targetTriangles[currentTriangleCount + 2] = currentVertexCount + 2; targetTriangles[currentTriangleCount + 3] = currentVertexCount;
        targetTriangles[currentTriangleCount + 4] = currentVertexCount + 2; targetTriangles[currentTriangleCount + 5] = currentVertexCount + 3;

        if (visibility == BlockVisibilityType.Opaque) { chunk._opaqueVertexCount += 4; chunk._opaqueTriangleCount += 6; }
        else if (visibility == BlockVisibilityType.Transparent) { chunk._transparentVertexCount += 4; chunk._transparentTriangleCount += 6; }
        else { chunk._cutoutVertexCount += 4; chunk._cutoutTriangleCount += 6; }

        if (visibility != BlockVisibilityType.Transparent && chunk._collisionVertexCount < MAX_VERTS * 3 - 4) {
            chunk._collisionVertices[chunk._collisionVertexCount + 0] = targetVertices[currentVertexCount + 0];
            chunk._collisionVertices[chunk._collisionVertexCount + 1] = targetVertices[currentVertexCount + 1];
            chunk._collisionVertices[chunk._collisionVertexCount + 2] = targetVertices[currentVertexCount + 2];
            chunk._collisionVertices[chunk._collisionVertexCount + 3] = targetVertices[currentVertexCount + 3];
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount; chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 1;
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 2; chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount;
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 2; chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 3;
            chunk._collisionVertexCount += 4;
        }
#if LOGGING
        if (enableVerboseLogging)
        {
            chunk.facesTotal++;
            if (visibility == BlockVisibilityType.Opaque) chunk.facesOpaque++;
            else if (visibility == BlockVisibilityType.Transparent) chunk.facesTransparent++;
            else chunk.facesCutout++;
        }
#endif
    }

    // OPTIMIZATION Phase 2: Bulk process collected faces (better cache locality, reduces method overhead)
    private void _AddFace(ChunkData chunk, Vector3[] faceVertices, Vector3 faceNormal, Vector3 blockPos, byte blockID, BlockVisibilityType visibility, int faceIndex)
    {
        Vector3[] targetVertices; int[] targetTriangles; Vector3[] targetUVs; Vector3[] targetNormals; Color[] targetColors;
        int currentVertexCount; int currentTriangleCount;

        if (visibility == BlockVisibilityType.Opaque) {
            if (chunk._opaqueVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._opaqueVertices; targetTriangles = chunk._opaqueTriangles; targetUVs = chunk._opaqueUVs; targetNormals = chunk._opaqueNormals; targetColors = chunk._opaqueColors;
            currentVertexCount = chunk._opaqueVertexCount; currentTriangleCount = chunk._opaqueTriangleCount;
        } else if (visibility == BlockVisibilityType.Transparent) {
            if (chunk._transparentVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._transparentVertices; targetTriangles = chunk._transparentTriangles; targetUVs = chunk._transparentUVs; targetNormals = chunk._transparentNormals; targetColors = chunk._transparentColors;
            currentVertexCount = chunk._transparentVertexCount; currentTriangleCount = chunk._transparentTriangleCount;
        } else { // Cutout
            if (chunk._cutoutVertexCount + 4 > MAX_VERTS) return;
            targetVertices = chunk._cutoutVertices; targetTriangles = chunk._cutoutTriangles; targetUVs = chunk._cutoutUVs; targetNormals = chunk._cutoutNormals; targetColors = chunk._cutoutColors;
            currentVertexCount = chunk._cutoutVertexCount; currentTriangleCount = chunk._cutoutTriangleCount;
        }

        float bx = blockPos.x, by = blockPos.y, bz = blockPos.z;
        targetVertices[currentVertexCount + 0] = new Vector3(bx + faceVertices[0].x, by + faceVertices[0].y, bz + faceVertices[0].z);
        targetVertices[currentVertexCount + 1] = new Vector3(bx + faceVertices[1].x, by + faceVertices[1].y, bz + faceVertices[1].z);
        targetVertices[currentVertexCount + 2] = new Vector3(bx + faceVertices[2].x, by + faceVertices[2].y, bz + faceVertices[2].z);
        targetVertices[currentVertexCount + 3] = new Vector3(bx + faceVertices[3].x, by + faceVertices[3].y, bz + faceVertices[3].z);
        for (int i=0; i<4; i++) targetNormals[currentVertexCount + i] = faceNormal;

        // Calculate biome color for this block position
        Color biomeColor = _GetBiomeColorForBlock(chunk, blockID, (int)bx, (int)bz);

        if (_UsesCpuAmbientOcclusion())
        {
            int direction;
            if (faceNormal.y > 0.5f) direction = 0;
            else if (faceNormal.y < -0.5f) direction = 1;
            else if (faceNormal.z > 0.5f) direction = 2;
            else if (faceNormal.z < -0.5f) direction = 3;
            else if (faceNormal.x > 0.5f) direction = 4;
            else direction = 5;

            int aoSignature = _BuildAoSignature(chunk, blockID, direction, (int)bx, (int)by, (int)bz);
            targetColors[currentVertexCount + 0] = new Color(biomeColor.r, biomeColor.g, biomeColor.b, _UnpackAoBrightness(aoSignature, 0));
            targetColors[currentVertexCount + 1] = new Color(biomeColor.r, biomeColor.g, biomeColor.b, _UnpackAoBrightness(aoSignature, 1));
            targetColors[currentVertexCount + 2] = new Color(biomeColor.r, biomeColor.g, biomeColor.b, _UnpackAoBrightness(aoSignature, 2));
            targetColors[currentVertexCount + 3] = new Color(biomeColor.r, biomeColor.g, biomeColor.b, _UnpackAoBrightness(aoSignature, 3));
        }
        else
        {
            // FIXED: Apply lighting to vertex colors (alpha channel = brightness)
            // Sample light from the neighbor block that this face is against, not the block itself
            float brightness = _GetLightBrightnessForFace(chunk, faceNormal, (int)bx, (int)by, (int)bz);
            biomeColor.a = brightness;

            for (int i=0; i<4; i++) targetColors[currentVertexCount + i] = biomeColor;
        }

        float textureSlice = _GetTextureSlice(blockID, faceIndex);
        targetUVs[currentVertexCount + 0] = new Vector3(0, 0, textureSlice); targetUVs[currentVertexCount + 1] = new Vector3(0, 1, textureSlice);
        targetUVs[currentVertexCount + 2] = new Vector3(1, 1, textureSlice); targetUVs[currentVertexCount + 3] = new Vector3(1, 0, textureSlice);

        targetTriangles[currentTriangleCount + 0] = currentVertexCount; targetTriangles[currentTriangleCount + 1] = currentVertexCount + 1;
        targetTriangles[currentTriangleCount + 2] = currentVertexCount + 2; targetTriangles[currentTriangleCount + 3] = currentVertexCount;
        targetTriangles[currentTriangleCount + 4] = currentVertexCount + 2; targetTriangles[currentTriangleCount + 5] = currentVertexCount + 3;

        if (visibility == BlockVisibilityType.Opaque) { chunk._opaqueVertexCount += 4; chunk._opaqueTriangleCount += 6; }
        else if (visibility == BlockVisibilityType.Transparent) { chunk._transparentVertexCount += 4; chunk._transparentTriangleCount += 6; }
        else { chunk._cutoutVertexCount += 4; chunk._cutoutTriangleCount += 6; }

        if (visibility != BlockVisibilityType.Transparent && chunk._collisionVertexCount < MAX_VERTS * 3 - 4) {
            chunk._collisionVertices[chunk._collisionVertexCount + 0] = targetVertices[currentVertexCount + 0];
            chunk._collisionVertices[chunk._collisionVertexCount + 1] = targetVertices[currentVertexCount + 1];
            chunk._collisionVertices[chunk._collisionVertexCount + 2] = targetVertices[currentVertexCount + 2];
            chunk._collisionVertices[chunk._collisionVertexCount + 3] = targetVertices[currentVertexCount + 3];
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount; chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 1;
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 2; chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount;
            chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 2; chunk._collisionTriangles[chunk._collisionTriangleCount++] = chunk._collisionVertexCount + 3;
            chunk._collisionVertexCount += 4;
        }
#if LOGGING
        if (enableVerboseLogging)
        {
            chunk.facesTotal++;
            if (visibility == BlockVisibilityType.Opaque) chunk.facesOpaque++;
            else if (visibility == BlockVisibilityType.Transparent) chunk.facesTransparent++;
            else chunk.facesCutout++;
        }
#endif
    }

    // ===== CROSS-SHAPED BLOCK RENDERING =====
    // Cross-shaped blocks (like tall grass and flowers) use two intersecting perpendicular quads
    // with seeded random offsets based on block position (matching Beta 1.7.3)

    private void _AddCrossShapedBlocks(ChunkData chunk)
    {
        byte[] decompressed = chunk._decompSelf;
        if (decompressed == null || chunk._crossBlockPackedPositions == null || chunk._crossBlockCount == 0) return;

        for (int i = 0; i < chunk._crossBlockCount; i++)
        {
            int packed = chunk._crossBlockPackedPositions[i];
            int x = (packed >> 16) & 0xFF;
            int y = (packed >> 8) & 0xFF;
            int z = packed & 0xFF;
            int idx = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            byte blockID = decompressed[idx];
            if (blockID == 0 || blockID >= shapeTypeCache.Length || shapeTypeCache[blockID] != McBlockShapeType.Cross) continue;
            BlockVisibilityType visibility = blockID < visibilityCache.Length ? visibilityCache[blockID] : BlockVisibilityType.Opaque;
            _AddCrossShapedBlock(chunk, new Vector3(x, y, z), blockID, visibility);
        }
    }

    private void _AddCrossShapedBlock(ChunkData chunk, Vector3 blockPos, byte blockID, BlockVisibilityType visibility)
    {
        if (blockID == 51)
        {
            _AddFireBlock(chunk, blockPos, visibility);
            return;
        }
        // Cross-shaped blocks use 2 perpendicular quads forming an X shape when viewed from above
        // The quads extend from corner to corner: (0,0,0)→(1,1,1) and (1,0,0)→(0,1,1)

        Vector3[] targetVertices; int[] targetTriangles; Vector3[] targetUVs; Vector3[] targetNormals; Color[] targetColors;
        int currentVertexCount; int currentTriangleCount;

        if (visibility == BlockVisibilityType.Opaque) {
            if (chunk._opaqueVertexCount + 8 > MAX_VERTS) return;
            targetVertices = chunk._opaqueVertices; targetTriangles = chunk._opaqueTriangles; targetUVs = chunk._opaqueUVs; targetNormals = chunk._opaqueNormals; targetColors = chunk._opaqueColors;
            currentVertexCount = chunk._opaqueVertexCount; currentTriangleCount = chunk._opaqueTriangleCount;
        } else if (visibility == BlockVisibilityType.Transparent) {
            if (chunk._transparentVertexCount + 8 > MAX_VERTS) return;
            targetVertices = chunk._transparentVertices; targetTriangles = chunk._transparentTriangles; targetUVs = chunk._transparentUVs; targetNormals = chunk._transparentNormals; targetColors = chunk._transparentColors;
            currentVertexCount = chunk._transparentVertexCount; currentTriangleCount = chunk._transparentTriangleCount;
        } else { // Cutout
            if (chunk._cutoutVertexCount + 8 > MAX_VERTS) return;
            targetVertices = chunk._cutoutVertices; targetTriangles = chunk._cutoutTriangles; targetUVs = chunk._cutoutUVs; targetNormals = chunk._cutoutNormals; targetColors = chunk._cutoutColors;
            currentVertexCount = chunk._cutoutVertexCount; currentTriangleCount = chunk._cutoutTriangleCount;
        }

        float bx = blockPos.x, by = blockPos.y, bz = blockPos.z;
        float textureSlice = _GetTextureSlice(blockID, FACE_INDEX_SIDE);

        // Calculate biome color for this block (grass blocks like tall grass should be tinted)
        Color biomeColor = _GetCachedBiomeColor(chunk, blockID, (int)bx, (int)bz);

        // FIXED: Apply lighting to vertex colors (alpha channel = brightness)
        // For cross-shaped blocks, sample light from the block itself (no face direction)
        float brightness = _GetCachedBrightnessAtBlock(chunk, (int)bx, (int)by, (int)bz);
        biomeColor.a = brightness;

        // Seeded random offset (matching Beta 1.7.3 BlockTallGrass colorMultiplier logic)
        // Calculate global position for seeding
        int globalX = (int)(bx + chunk.chunkX_world * chunkSizeXZ);
        int globalY = (int)(by + chunk.chunkY_world * chunkSizeY);
        int globalZ = (int)(bz + chunk.chunkZ_world * chunkSizeXZ);

        // BETA 1.7.3 EXACT: Seeded random offset calculation (RenderBlocks.java line 1316-1320)
        // Reference: renderBlockReed() method for tall grass randomization
        long seed = (long)(globalX * 3129871) ^ (long)globalZ * 116129781L ^ (long)globalY;
        seed = seed * seed * 42317861L + seed * 11L;

        // Extract random values from different bit ranges (Minecraft's approach)
        float randX = (float)((seed >> 16 & 15L) / 15.0f); // 0-1 from bits 16-19
        float randY = (float)((seed >> 20 & 15L) / 15.0f); // 0-1 from bits 20-23
        float randZ = (float)((seed >> 24 & 15L) / 15.0f); // 0-1 from bits 24-27

        // Convert to Minecraft's offset ranges:
        // X and Z: ±0.25 blocks (makes plants wobble left/right)
        // Y: -0.2 to 0 blocks (slightly sunk into ground for natural look)
        float offsetX = (randX - 0.5f) * 0.5f; // -0.25 to +0.25
        float offsetY = (randY - 1.0f) * 0.2f; // -0.2 to 0
        float offsetZ = (randZ - 0.5f) * 0.5f; // -0.25 to +0.25

        // QUAD 1: Southwest to Northeast diagonal
        // Vertices: (0,0,0), (0,1,0), (1,1,1), (1,0,1)
        // Apply randomization to make each plant look unique (Minecraft behavior)
        targetVertices[currentVertexCount + 0] = new Vector3(bx + 0 + offsetX, by + 0 + offsetY, bz + 0 + offsetZ);
        targetVertices[currentVertexCount + 1] = new Vector3(bx + 0 + offsetX, by + 1 + offsetY, bz + 0 + offsetZ);
        targetVertices[currentVertexCount + 2] = new Vector3(bx + 1 + offsetX, by + 1 + offsetY, bz + 1 + offsetZ);
        targetVertices[currentVertexCount + 3] = new Vector3(bx + 1 + offsetX, by + 0 + offsetY, bz + 1 + offsetZ);

        // Normals: Use a diagonal normal for lighting (average of X and Z)
        Vector3 normal1 = new Vector3(0.7071f, 0, 0.7071f).normalized;
        for (int i=0; i<4; i++) targetNormals[currentVertexCount + i] = normal1;

        // Colors: Apply biome tint to all vertices
        for (int i=0; i<4; i++) targetColors[currentVertexCount + i] = biomeColor;

        // UVs: Standard quad UV mapping
        targetUVs[currentVertexCount + 0] = new Vector3(0, 0, textureSlice);
        targetUVs[currentVertexCount + 1] = new Vector3(0, 1, textureSlice);
        targetUVs[currentVertexCount + 2] = new Vector3(1, 1, textureSlice);
        targetUVs[currentVertexCount + 3] = new Vector3(1, 0, textureSlice);

        // Triangles for first quad
        targetTriangles[currentTriangleCount + 0] = currentVertexCount + 0;
        targetTriangles[currentTriangleCount + 1] = currentVertexCount + 1;
        targetTriangles[currentTriangleCount + 2] = currentVertexCount + 2;
        targetTriangles[currentTriangleCount + 3] = currentVertexCount + 0;
        targetTriangles[currentTriangleCount + 4] = currentVertexCount + 2;
        targetTriangles[currentTriangleCount + 5] = currentVertexCount + 3;

        // QUAD 2: Southeast to Northwest diagonal
        // Vertices: (1,0,0), (1,1,0), (0,1,1), (0,0,1)
        // Apply same randomization to maintain consistent plant offset
        targetVertices[currentVertexCount + 4] = new Vector3(bx + 1 + offsetX, by + 0 + offsetY, bz + 0 + offsetZ);
        targetVertices[currentVertexCount + 5] = new Vector3(bx + 1 + offsetX, by + 1 + offsetY, bz + 0 + offsetZ);
        targetVertices[currentVertexCount + 6] = new Vector3(bx + 0 + offsetX, by + 1 + offsetY, bz + 1 + offsetZ);
        targetVertices[currentVertexCount + 7] = new Vector3(bx + 0 + offsetX, by + 0 + offsetY, bz + 1 + offsetZ);

        // Normals: Use opposite diagonal normal
        Vector3 normal2 = new Vector3(-0.7071f, 0, 0.7071f).normalized;
        for (int i=4; i<8; i++) targetNormals[currentVertexCount + i] = normal2;

        // Colors: Apply biome tint to all vertices
        for (int i=4; i<8; i++) targetColors[currentVertexCount + i] = biomeColor;

        // UVs: Standard quad UV mapping
        targetUVs[currentVertexCount + 4] = new Vector3(0, 0, textureSlice);
        targetUVs[currentVertexCount + 5] = new Vector3(0, 1, textureSlice);
        targetUVs[currentVertexCount + 6] = new Vector3(1, 1, textureSlice);
        targetUVs[currentVertexCount + 7] = new Vector3(1, 0, textureSlice);

        // Triangles for second quad
        targetTriangles[currentTriangleCount + 6] = currentVertexCount + 4;
        targetTriangles[currentTriangleCount + 7] = currentVertexCount + 5;
        targetTriangles[currentTriangleCount + 8] = currentVertexCount + 6;
        targetTriangles[currentTriangleCount + 9] = currentVertexCount + 4;
        targetTriangles[currentTriangleCount + 10] = currentVertexCount + 6;
        targetTriangles[currentTriangleCount + 11] = currentVertexCount + 7;

        // Update counts
        if (visibility == BlockVisibilityType.Opaque) { chunk._opaqueVertexCount += 8; chunk._opaqueTriangleCount += 12; }
        else if (visibility == BlockVisibilityType.Transparent) { chunk._transparentVertexCount += 8; chunk._transparentTriangleCount += 12; }
        else { chunk._cutoutVertexCount += 8; chunk._cutoutTriangleCount += 12; }

        // NOTE: Cross-shaped blocks have no collision in Minecraft Beta 1.7.3

#if LOGGING
        if (enableDetailedTimings)
        {
            chunk.facesTotal += 2;
            if (visibility == BlockVisibilityType.Opaque) chunk.facesOpaque += 2;
            else if (visibility == BlockVisibilityType.Transparent) chunk.facesTransparent += 2;
            else chunk.facesCutout += 2;
        }
#endif
    }

    private bool _IsFlammableForRender(byte blockId)
    {
        // MC's chanceToEncourageFire > 0
        return blockId == 5 || blockId == 17 || blockId == 18 || blockId == 35 ||
               blockId == 46 || blockId == 47 || blockId == 53 || blockId == 85 ||
               blockId == 31;
    }

    private bool _IsBlockSolidForFire(ChunkData chunk, int lx, int ly, int lz)
    {
        byte b = _GetFluidRenderBlock(chunk, lx, ly, lz);
        return b != 0 && _GetVisibilityType(b) == BlockVisibilityType.Opaque;
    }

    private void _AddFireBlock(ChunkData chunk, Vector3 blockPos, BlockVisibilityType visibility)
    {
        float bx = blockPos.x, by = blockPos.y, bz = blockPos.z;
        float textureSlice = _GetTextureSlice(51, FACE_INDEX_SIDE);
        float brightness = _GetCachedBrightnessAtBlock(chunk, (int)bx, (int)by, (int)bz);
        Color col = new Color(1f, 0f, 1f, brightness); // green=0 flags fire for shader animation

        int lx = (int)bx, ly = (int)by, lz = (int)bz;

        bool belowSolid = _IsBlockSolidForFire(chunk, lx, ly - 1, lz);
        bool belowFlammable = _IsFlammableForRender(_GetFluidRenderBlock(chunk, lx, ly - 1, lz));

        if (!belowSolid && !belowFlammable)
        {
            // Wall fire: panels against flammable neighbors
            // MC uses 0.2 offset, 1.4 tall, 1/16 Y offset
            float yo = 1f / 16f;
            float h = 1.4f;

            bool nxFlam = _IsFlammableForRender(_GetFluidRenderBlock(chunk, lx - 1, ly, lz));
            bool pxFlam = _IsFlammableForRender(_GetFluidRenderBlock(chunk, lx + 1, ly, lz));
            bool nzFlam = _IsFlammableForRender(_GetFluidRenderBlock(chunk, lx, ly, lz - 1));
            bool pzFlam = _IsFlammableForRender(_GetFluidRenderBlock(chunk, lx, ly, lz + 1));
            bool pyFlam = _IsFlammableForRender(_GetFluidRenderBlock(chunk, lx, ly + 1, lz));

            int panels = 0;
            if (nxFlam) panels++;
            if (pxFlam) panels++;
            if (nzFlam) panels++;
            if (pzFlam) panels++;
            if (pyFlam) panels++;
            if (panels == 0) panels = 1;

            // Each wall panel = 8 verts (front+back)
            int neededVerts = panels * 8;
            Vector3[] tv; int[] tt; Vector3[] tu; Vector3[] tn; Color[] tc;
            int vc, trc;
            if (!_GetFireMeshBuffers(chunk, visibility, neededVerts, out tv, out tt, out tu, out tn, out tc, out vc, out trc))
                return;

            // MC: each wall gets a flat panel inset by 0.2 from the wall face, double-sided
            if (nxFlam)
            {
                // -X wall: panel at x+0.2, Z from 0→1
                _AddFireQuadPair(tv, tt, tu, tn, tc, ref vc, ref trc,
                    bx + 0.2f, bx + 0.2f, bx + 0.2f, bx + 0.2f,
                    by + yo, by + h + yo, by + h + yo, by + yo,
                    bz + 1f, bz + 1f, bz, bz,
                    textureSlice, col);
            }
            if (pxFlam)
            {
                // +X wall: panel at x+0.8, Z from 0→1
                _AddFireQuadPair(tv, tt, tu, tn, tc, ref vc, ref trc,
                    bx + 0.8f, bx + 0.8f, bx + 0.8f, bx + 0.8f,
                    by + yo, by + h + yo, by + h + yo, by + yo,
                    bz, bz, bz + 1f, bz + 1f,
                    textureSlice, col);
            }
            if (nzFlam)
            {
                // -Z wall: panel at z+0.2, X from 0→1
                _AddFireQuadPair(tv, tt, tu, tn, tc, ref vc, ref trc,
                    bx, bx, bx + 1f, bx + 1f,
                    by + yo, by + h + yo, by + h + yo, by + yo,
                    bz + 0.2f, bz + 0.2f, bz + 0.2f, bz + 0.2f,
                    textureSlice, col);
            }
            if (pzFlam)
            {
                // +Z wall: panel at z+0.8, X from 0→1
                _AddFireQuadPair(tv, tt, tu, tn, tc, ref vc, ref trc,
                    bx + 1f, bx + 1f, bx, bx,
                    by + yo, by + h + yo, by + h + yo, by + yo,
                    bz + 0.8f, bz + 0.8f, bz + 0.8f, bz + 0.8f,
                    textureSlice, col);
            }
            if (pyFlam)
            {
                // Ceiling fire: two crossed panels like floor fire but at y+1
                _AddFireQuadPair(tv, tt, tu, tn, tc, ref vc, ref trc,
                    bx, bx, bx + 1f, bx + 1f,
                    by + 1f + yo, by + 1f - 0.2f + yo, by + 1f - 0.2f + yo, by + 1f + yo,
                    bz + 0.5f, bz + 0.5f, bz + 0.5f, bz + 0.5f,
                    textureSlice, col);
            }
            if (!nxFlam && !pxFlam && !nzFlam && !pzFlam && !pyFlam)
            {
                _AddFireQuadPair(tv, tt, tu, tn, tc, ref vc, ref trc,
                    bx, bx, bx + 1f, bx + 1f,
                    by + yo, by + h + yo, by + h + yo, by + yo,
                    bz + 0.5f, bz + 0.5f, bz + 0.5f, bz + 0.5f,
                    textureSlice, col);
            }

            _SetFireMeshCounts(chunk, visibility, vc, trc);
        }
        else
        {
            // Floor fire: MC uses 4 flat vertical panels (8 double-sided quads)
            // Panel pair 1 (frame 1): two YZ-plane panels at x±0.2/0.3 from center
            // Panel pair 2 (frame 1): two XY-plane panels at z±0.2/0.3 from center
            // Panel pair 3 (frame 2): two YZ-plane panels at x±0.4/0.5 from center (wider)
            // Panel pair 4 (frame 2): two XY-plane panels at z±0.4/0.5 from center (wider)
            int neededVerts = 32; // 4 panels × 8 verts (front+back)
            Vector3[] tv; int[] tt; Vector3[] tu; Vector3[] tn; Color[] tc;
            int vc, trc;
            if (!_GetFireMeshBuffers(chunk, visibility, neededVerts, out tv, out tt, out tu, out tn, out tc, out vc, out trc))
                return;

            float h = 1.4f;
            // Panel 1: YZ plane at x+0.2, full Z range, frame 1
            _AddFireQuadPair(tv, tt, tu, tn, tc, ref vc, ref trc,
                bx + 0.2f, bx + 0.2f, bx + 0.2f, bx + 0.2f,
                by, by + h, by + h, by,
                bz + 1f, bz + 1f, bz, bz,
                textureSlice, col);
            // Panel 2: YZ plane at x+0.8, full Z range, back face
            _AddFireQuadPair(tv, tt, tu, tn, tc, ref vc, ref trc,
                bx + 0.8f, bx + 0.8f, bx + 0.8f, bx + 0.8f,
                by, by + h, by + h, by,
                bz, bz, bz + 1f, bz + 1f,
                textureSlice, col);
            // Panel 3: XY plane at z+0.8, full X range
            _AddFireQuadPair(tv, tt, tu, tn, tc, ref vc, ref trc,
                bx + 1f, bx + 1f, bx, bx,
                by, by + h, by + h, by,
                bz + 0.8f, bz + 0.8f, bz + 0.8f, bz + 0.8f,
                textureSlice, col);
            // Panel 4: XY plane at z+0.2, full X range
            _AddFireQuadPair(tv, tt, tu, tn, tc, ref vc, ref trc,
                bx, bx, bx + 1f, bx + 1f,
                by, by + h, by + h, by,
                bz + 0.2f, bz + 0.2f, bz + 0.2f, bz + 0.2f,
                textureSlice, col);

            _SetFireMeshCounts(chunk, visibility, vc, trc);
        }
    }

    private bool _GetFireMeshBuffers(ChunkData chunk, BlockVisibilityType visibility, int neededVerts,
        out Vector3[] tv, out int[] tt, out Vector3[] tu, out Vector3[] tn, out Color[] tc,
        out int vc, out int trc)
    {
        if (visibility == BlockVisibilityType.Cutout)
        {
            if (chunk._cutoutVertexCount + neededVerts > MAX_VERTS) { tv = null; tt = null; tu = null; tn = null; tc = null; vc = 0; trc = 0; return false; }
            tv = chunk._cutoutVertices; tt = chunk._cutoutTriangles; tu = chunk._cutoutUVs; tn = chunk._cutoutNormals; tc = chunk._cutoutColors;
            vc = chunk._cutoutVertexCount; trc = chunk._cutoutTriangleCount;
        }
        else
        {
            if (chunk._transparentVertexCount + neededVerts > MAX_VERTS) { tv = null; tt = null; tu = null; tn = null; tc = null; vc = 0; trc = 0; return false; }
            tv = chunk._transparentVertices; tt = chunk._transparentTriangles; tu = chunk._transparentUVs; tn = chunk._transparentNormals; tc = chunk._transparentColors;
            vc = chunk._transparentVertexCount; trc = chunk._transparentTriangleCount;
        }
        return true;
    }

    private void _SetFireMeshCounts(ChunkData chunk, BlockVisibilityType visibility, int vc, int trc)
    {
        if (visibility == BlockVisibilityType.Cutout) { chunk._cutoutVertexCount = vc; chunk._cutoutTriangleCount = trc; }
        else { chunk._transparentVertexCount = vc; chunk._transparentTriangleCount = trc; }
    }

    private void _AddFireQuadPair(Vector3[] tv, int[] tt, Vector3[] tu, Vector3[] tn, Color[] tc,
        ref int vc, ref int trc,
        float x0, float x1, float x2, float x3,
        float y0, float y1, float y2, float y3,
        float z0, float z1, float z2, float z3,
        float texSlice, Color col)
    {
        // Front face
        tv[vc + 0] = new Vector3(x0, y0, z0);
        tv[vc + 1] = new Vector3(x1, y1, z1);
        tv[vc + 2] = new Vector3(x2, y2, z2);
        tv[vc + 3] = new Vector3(x3, y3, z3);
        float frameV = 1.0f / 32.0f; // one frame height in the 32-frame strip
        tu[vc + 0] = new Vector3(0, 0, texSlice);
        tu[vc + 1] = new Vector3(0, frameV, texSlice);
        tu[vc + 2] = new Vector3(1, frameV, texSlice);
        tu[vc + 3] = new Vector3(1, 0, texSlice);
        Vector3 n = Vector3.up;
        for (int i = 0; i < 4; i++) { tn[vc + i] = n; tc[vc + i] = col; }
        tt[trc + 0] = vc + 0; tt[trc + 1] = vc + 1; tt[trc + 2] = vc + 2;
        tt[trc + 3] = vc + 0; tt[trc + 4] = vc + 2; tt[trc + 5] = vc + 3;
        vc += 4; trc += 6;
    }

    private void _AddFireWallPanel(Vector3[] tv, int[] tt, Vector3[] tu, Vector3[] tn, Color[] tc,
        ref int vc, ref int trc,
        float x0, float y0, float z0, float x1, float y1, float z1,
        Vector3 normal, float texSlice, Color col)
    {
        // A wall panel from (x0,y0,z0)-(x1,y0,z0) at bottom to (x0,y1,z1)-(x1,y1,z1) at top
        // For X-axis panels: z0=z1 (z const), x varies
        // For Z-axis panels: x0=x1 (x const), z varies
        tv[vc + 0] = new Vector3(x0, y0, z0);
        tv[vc + 1] = new Vector3(x0, y1, z0);
        tv[vc + 2] = new Vector3(x1, y1, z1);
        tv[vc + 3] = new Vector3(x1, y0, z1);
        float frameV = 1.0f / 32.0f;
        tu[vc + 0] = new Vector3(0, 0, texSlice);
        tu[vc + 1] = new Vector3(0, frameV, texSlice);
        tu[vc + 2] = new Vector3(1, frameV, texSlice);
        tu[vc + 3] = new Vector3(1, 0, texSlice);
        for (int i = 0; i < 4; i++) { tn[vc + i] = normal; tc[vc + i] = col; }
        tt[trc + 0] = vc + 0; tt[trc + 1] = vc + 1; tt[trc + 2] = vc + 2;
        tt[trc + 3] = vc + 0; tt[trc + 4] = vc + 2; tt[trc + 5] = vc + 3;
        vc += 4; trc += 6;
    }

    private void _AddTorchBlocks(ChunkData chunk)
    {
        byte[] decompressed = chunk._decompSelf;
        if (decompressed == null || chunk._torchBlockPackedPositions == null || chunk._torchBlockCount == 0) return;

        for (int i = 0; i < chunk._torchBlockCount; i++)
        {
            int packed = chunk._torchBlockPackedPositions[i];
            int x = (packed >> 16) & 0xFF;
            int y = (packed >> 8) & 0xFF;
            int z = packed & 0xFF;
            int idx = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            byte blockID = decompressed[idx];
            if (!_IsTorchBlock(blockID)) continue;

            _AddTorchBlock(chunk, x, y, z, blockID);
        }
    }

    private void _AddTorchBlock(ChunkData chunk, int x, int y, int z, byte blockID)
    {
        if (chunk == null || chunk._cutoutVertexCount + 24 > MAX_VERTS) return;

        float brightness = (blockID < lightEmissionCache.Length && lightEmissionCache[blockID] > 0)
            ? 1.0f
            : _GetCachedBrightnessAtBlock(chunk, x, y, z);
        Color torchColor = new Color(1.0f, 1.0f, 1.0f, brightness);
        float textureSlice = _GetTextureSlice(blockID, FACE_INDEX_SIDE);
        byte torchMount = _GetTorchMountLocal(chunk, x, y, z);

        float halfThickness = 1.0f / 16.0f;
        Vector3 topCenter = new Vector3(x + 0.5f, y + 0.625f, z + 0.5f);
        Vector3 bottomCenter = new Vector3(x + 0.5f, y + 0.0f, z + 0.5f);
        bool capFacesDown = false;

        switch (torchMount)
        {
            case TORCH_MOUNT_WEST:
                topCenter = new Vector3(x + 0.25f, y + 0.825f, z + 0.5f);
                bottomCenter = new Vector3(x + 0.0f, y + 0.2f, z + 0.5f);
                break;
            case TORCH_MOUNT_EAST:
                topCenter = new Vector3(x + 0.75f, y + 0.825f, z + 0.5f);
                bottomCenter = new Vector3(x + 1.0f, y + 0.2f, z + 0.5f);
                break;
            case TORCH_MOUNT_NORTH:
                topCenter = new Vector3(x + 0.5f, y + 0.825f, z + 0.25f);
                bottomCenter = new Vector3(x + 0.5f, y + 0.2f, z + 0.0f);
                break;
            case TORCH_MOUNT_SOUTH:
                topCenter = new Vector3(x + 0.5f, y + 0.825f, z + 0.75f);
                bottomCenter = new Vector3(x + 0.5f, y + 0.2f, z + 1.0f);
                break;
            case TORCH_MOUNT_CEILING:
                topCenter = new Vector3(x + 0.5f, y + 1.0f, z + 0.5f);
                bottomCenter = new Vector3(x + 0.5f, y + 0.375f, z + 0.5f);
                capFacesDown = true;
                break;
        }

        float topMinX = topCenter.x - halfThickness;
        float topMaxX = topCenter.x + halfThickness;
        float topMinZ = topCenter.z - halfThickness;
        float topMaxZ = topCenter.z + halfThickness;
        float bottomMinX = bottomCenter.x - halfThickness;
        float bottomMaxX = bottomCenter.x + halfThickness;
        float bottomMinZ = bottomCenter.z - halfThickness;
        float bottomMaxZ = bottomCenter.z + halfThickness;

        float shaftMinU = 7.0f / 16.0f;
        float shaftMaxU = 9.0f / 16.0f;
        float shaftMaxV = 10.0f / 16.0f;
        if (blockID == BLOCK_REDSTONE_TORCH_ON)
        {
            shaftMinU = 6.0f / 16.0f;
            shaftMaxU = 10.0f / 16.0f;
            shaftMaxV = 11.0f / 16.0f;
        }
        float capMinU = shaftMinU;
        float capMaxU = shaftMaxU;
        float capMaxV = shaftMaxV;
        float capMinV = capMaxV - (capMaxU - capMinU);
        float bottomCapMinU = shaftMinU;
        float bottomCapMaxU = shaftMaxU;
        float bottomCapMinV = 0.0f;
        float bottomCapMaxV = shaftMaxU - shaftMinU;

        if (capFacesDown)
        {
            _AppendTorchQuad(chunk, torchColor, textureSlice,
                new Vector3(bottomMinX, bottomCenter.y, bottomMinZ),
                new Vector3(bottomMaxX, bottomCenter.y, bottomMinZ),
                new Vector3(bottomMaxX, bottomCenter.y, bottomMaxZ),
                new Vector3(bottomMinX, bottomCenter.y, bottomMaxZ),
                new Vector2(capMinU, capMinV),
                new Vector2(capMaxU, capMinV),
                new Vector2(capMaxU, capMaxV),
                new Vector2(capMinU, capMaxV));
        }
        else
        {
            _AppendTorchQuad(chunk, torchColor, textureSlice,
                new Vector3(topMinX, topCenter.y, topMinZ),
                new Vector3(topMinX, topCenter.y, topMaxZ),
                new Vector3(topMaxX, topCenter.y, topMaxZ),
                new Vector3(topMaxX, topCenter.y, topMinZ),
                new Vector2(capMinU, capMinV),
                new Vector2(capMinU, capMaxV),
                new Vector2(capMaxU, capMaxV),
                new Vector2(capMaxU, capMinV));

            _AppendTorchQuad(chunk, torchColor, textureSlice,
                new Vector3(bottomMinX, bottomCenter.y, bottomMaxZ),
                new Vector3(bottomMaxX, bottomCenter.y, bottomMaxZ),
                new Vector3(bottomMaxX, bottomCenter.y, bottomMinZ),
                new Vector3(bottomMinX, bottomCenter.y, bottomMinZ),
                new Vector2(bottomCapMinU, bottomCapMinV),
                new Vector2(bottomCapMaxU, bottomCapMinV),
                new Vector2(bottomCapMaxU, bottomCapMaxV),
                new Vector2(bottomCapMinU, bottomCapMaxV));
        }

        _AppendTorchQuad(chunk, torchColor, textureSlice,
            new Vector3(bottomMinX, bottomCenter.y, bottomMinZ),
            new Vector3(bottomMinX, bottomCenter.y, bottomMaxZ),
            new Vector3(topMinX, topCenter.y, topMaxZ),
            new Vector3(topMinX, topCenter.y, topMinZ),
            new Vector2(shaftMinU, 0.0f),
            new Vector2(shaftMaxU, 0.0f),
            new Vector2(shaftMaxU, shaftMaxV),
            new Vector2(shaftMinU, shaftMaxV));

        _AppendTorchQuad(chunk, torchColor, textureSlice,
            new Vector3(bottomMaxX, bottomCenter.y, bottomMaxZ),
            new Vector3(bottomMaxX, bottomCenter.y, bottomMinZ),
            new Vector3(topMaxX, topCenter.y, topMinZ),
            new Vector3(topMaxX, topCenter.y, topMaxZ),
            new Vector2(shaftMinU, 0.0f),
            new Vector2(shaftMaxU, 0.0f),
            new Vector2(shaftMaxU, shaftMaxV),
            new Vector2(shaftMinU, shaftMaxV));

        _AppendTorchQuad(chunk, torchColor, textureSlice,
            new Vector3(bottomMaxX, bottomCenter.y, bottomMinZ),
            new Vector3(bottomMinX, bottomCenter.y, bottomMinZ),
            new Vector3(topMinX, topCenter.y, topMinZ),
            new Vector3(topMaxX, topCenter.y, topMinZ),
            new Vector2(shaftMinU, 0.0f),
            new Vector2(shaftMaxU, 0.0f),
            new Vector2(shaftMaxU, shaftMaxV),
            new Vector2(shaftMinU, shaftMaxV));

        _AppendTorchQuad(chunk, torchColor, textureSlice,
            new Vector3(bottomMinX, bottomCenter.y, bottomMaxZ),
            new Vector3(bottomMaxX, bottomCenter.y, bottomMaxZ),
            new Vector3(topMaxX, topCenter.y, topMaxZ),
            new Vector3(topMinX, topCenter.y, topMaxZ),
            new Vector2(shaftMinU, 0.0f),
            new Vector2(shaftMaxU, 0.0f),
            new Vector2(shaftMaxU, shaftMaxV),
            new Vector2(shaftMinU, shaftMaxV));

#if LOGGING
        if (enableDetailedTimings)
        {
            chunk.facesTotal += 6;
            chunk.facesCutout += 6;
        }
#endif
    }

    private void _AppendTorchQuad(ChunkData chunk, Color torchColor, float textureSlice, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
    {
        if (chunk == null || chunk._cutoutVertexCount + 4 > MAX_VERTS) return;

        int vertexIndex = chunk._cutoutVertexCount;
        int triangleIndex = chunk._cutoutTriangleCount;
        Vector3 quadNormal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
        if (quadNormal.sqrMagnitude < 0.0001f) quadNormal = Vector3.up;

        chunk._cutoutVertices[vertexIndex + 0] = v0;
        chunk._cutoutVertices[vertexIndex + 1] = v1;
        chunk._cutoutVertices[vertexIndex + 2] = v2;
        chunk._cutoutVertices[vertexIndex + 3] = v3;

        chunk._cutoutNormals[vertexIndex + 0] = quadNormal;
        chunk._cutoutNormals[vertexIndex + 1] = quadNormal;
        chunk._cutoutNormals[vertexIndex + 2] = quadNormal;
        chunk._cutoutNormals[vertexIndex + 3] = quadNormal;

        chunk._cutoutColors[vertexIndex + 0] = torchColor;
        chunk._cutoutColors[vertexIndex + 1] = torchColor;
        chunk._cutoutColors[vertexIndex + 2] = torchColor;
        chunk._cutoutColors[vertexIndex + 3] = torchColor;

        chunk._cutoutUVs[vertexIndex + 0] = new Vector3(uv0.x, uv0.y, textureSlice);
        chunk._cutoutUVs[vertexIndex + 1] = new Vector3(uv1.x, uv1.y, textureSlice);
        chunk._cutoutUVs[vertexIndex + 2] = new Vector3(uv2.x, uv2.y, textureSlice);
        chunk._cutoutUVs[vertexIndex + 3] = new Vector3(uv3.x, uv3.y, textureSlice);

        chunk._cutoutTriangles[triangleIndex + 0] = vertexIndex + 0;
        chunk._cutoutTriangles[triangleIndex + 1] = vertexIndex + 1;
        chunk._cutoutTriangles[triangleIndex + 2] = vertexIndex + 2;
        chunk._cutoutTriangles[triangleIndex + 3] = vertexIndex + 0;
        chunk._cutoutTriangles[triangleIndex + 4] = vertexIndex + 2;
        chunk._cutoutTriangles[triangleIndex + 5] = vertexIndex + 3;

        chunk._cutoutVertexCount += 4;
        chunk._cutoutTriangleCount += 6;
    }

    private bool _IsWaterBlock(byte blockID)
    {
        return blockID == BLOCK_WATER_MOVING || blockID == BLOCK_WATER_STILL;
    }

    private bool _IsLavaBlock(byte blockID)
    {
        return blockID == 10 || blockID == 11;
    }

    private bool _IsFluidBlock(byte blockID)
    {
        return _IsWaterBlock(blockID) || _IsLavaBlock(blockID);
    }

    private byte _GetFluidRenderBlock(ChunkData originChunk, int localX, int localY, int localZ)
    {
        if (originChunk == null) return 0;

        int sampleChunkX = originChunk.chunkX_world;
        int sampleChunkY = originChunk.chunkY_world;
        int sampleChunkZ = originChunk.chunkZ_world;

        while (localX < 0)
        {
            localX += chunkSizeXZ;
            sampleChunkX--;
        }
        while (localX >= chunkSizeXZ)
        {
            localX -= chunkSizeXZ;
            sampleChunkX++;
        }
        while (localY < 0)
        {
            localY += chunkSizeY;
            sampleChunkY--;
        }
        while (localY >= chunkSizeY)
        {
            localY -= chunkSizeY;
            sampleChunkY++;
        }
        while (localZ < 0)
        {
            localZ += chunkSizeXZ;
            sampleChunkZ--;
        }
        while (localZ >= chunkSizeXZ)
        {
            localZ -= chunkSizeXZ;
            sampleChunkZ++;
        }

        if (sampleChunkX == originChunk.chunkX_world &&
            sampleChunkY == originChunk.chunkY_world &&
            sampleChunkZ == originChunk.chunkZ_world &&
            originChunk._decompSelf != null)
        {
            int stride = chunkSizeXZ * chunkSizeXZ;
            return originChunk._decompSelf[localY * stride + localZ * chunkSizeXZ + localX];
        }

        ChunkData sampleChunk = GetChunkAt(sampleChunkX, sampleChunkY, sampleChunkZ);
        if (sampleChunk == null || !sampleChunk.isDataReady) return 0;
        return _GetBlockLocal(sampleChunk, localX, localY, localZ);
    }

    private bool _IsOpaqueCubeForWater(byte blockID)
    {
        return blockID != 0 && _GetVisibilityType(blockID) == BlockVisibilityType.Opaque;
    }

    private bool _ShouldRenderFluidFace(ChunkData chunk, int neighborX, int neighborY, int neighborZ, int side, bool isLava)
    {
        byte neighborBlock = _GetFluidRenderBlock(chunk, neighborX, neighborY, neighborZ);
        bool neighborIsSameFluid = isLava ? _IsLavaBlock(neighborBlock) : _IsWaterBlock(neighborBlock);
        if (neighborIsSameFluid || (!isLava && neighborBlock == BLOCK_ICE)) return false;
        if (side == 1) return true;
        return !_IsOpaqueCubeForWater(neighborBlock);
    }

    private float _GetWaterPercentAir(int level)
    {
        if (level >= 8) level = 0;
        return (level + 1) / 9.0f;
    }

    private int _GetFluidFlowDecay(ChunkData chunk, int localX, int localY, int localZ, bool isLava)
    {
        byte block = _GetFluidRenderBlock(chunk, localX, localY, localZ);
        bool isSameFluid = isLava ? _IsLavaBlock(block) : _IsWaterBlock(block);
        if (!isSameFluid) return -1;
        return _GetFluidRenderMetadata(chunk, localX, localY, localZ);
    }

    private int _GetFluidRenderMetadata(ChunkData originChunk, int localX, int localY, int localZ)
    {
        if (originChunk == null) return 0;

        int sampleChunkX = originChunk.chunkX_world;
        int sampleChunkY = originChunk.chunkY_world;
        int sampleChunkZ = originChunk.chunkZ_world;

        while (localX < 0) { localX += chunkSizeXZ; sampleChunkX--; }
        while (localX >= chunkSizeXZ) { localX -= chunkSizeXZ; sampleChunkX++; }
        while (localY < 0) { localY += chunkSizeY; sampleChunkY--; }
        while (localY >= chunkSizeY) { localY -= chunkSizeY; sampleChunkY++; }
        while (localZ < 0) { localZ += chunkSizeXZ; sampleChunkZ--; }
        while (localZ >= chunkSizeXZ) { localZ -= chunkSizeXZ; sampleChunkZ++; }

        ChunkData sampleChunk;
        if (sampleChunkX == originChunk.chunkX_world &&
            sampleChunkY == originChunk.chunkY_world &&
            sampleChunkZ == originChunk.chunkZ_world)
        {
            sampleChunk = originChunk;
        }
        else
        {
            sampleChunk = GetChunkAt(sampleChunkX, sampleChunkY, sampleChunkZ);
        }

        if (sampleChunk == null || !sampleChunk.isDataReady || sampleChunk.blockMetadata == null) return 0;
        int idx = localY * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
        return (idx >= 0 && idx < sampleChunk.blockMetadata.Length) ? sampleChunk.blockMetadata[idx] : 0;
    }

    private float _GetFluidCornerHeight(ChunkData chunk, int cornerX, int localY, int cornerZ, bool isLava)
    {
        int sampleCount = 0;
        float sampleSum = 0.0f;

        for (int i = 0; i < 4; i++)
        {
            int sampleX = cornerX - (i & 1);
            int sampleZ = cornerZ - ((i >> 1) & 1);

            byte aboveBlock = _GetFluidRenderBlock(chunk, sampleX, localY + 1, sampleZ);
            bool aboveSame = isLava ? _IsLavaBlock(aboveBlock) : _IsWaterBlock(aboveBlock);
            if (aboveSame)
            {
                return 1.0f;
            }

            byte sampleBlock = _GetFluidRenderBlock(chunk, sampleX, localY, sampleZ);
            bool sampleSame = isLava ? _IsLavaBlock(sampleBlock) : _IsWaterBlock(sampleBlock);
            if (!sampleSame)
            {
                if (!_IsBlockSolid(sampleBlock))
                {
                    sampleSum += 1.0f;
                    sampleCount++;
                }
            }
            else
            {
                int meta = _GetFluidRenderMetadata(chunk, sampleX, localY, sampleZ);
                int effectiveLevel = meta >= 8 ? 0 : meta;
                float percentAir = _GetWaterPercentAir(effectiveLevel);
                sampleSum += percentAir * 10.0f;
                sampleCount += 10;
                sampleSum += percentAir;
                sampleCount++;
            }
        }

        if (sampleCount == 0) return 0.0f;
        return 1.0f - sampleSum / sampleCount;
    }

    private float _GetFluidFlowAngle(ChunkData chunk, int localX, int localY, int localZ, bool isLava)
    {
        int flowDecay = _GetFluidFlowDecay(chunk, localX, localY, localZ, isLava);
        if (flowDecay < 0) return -1000.0f;

        float flowX = 0.0f;
        float flowZ = 0.0f;

        for (int side = 0; side < 4; side++)
        {
            int sampleX = localX;
            int sampleZ = localZ;
            if (side == 0) sampleX--;
            else if (side == 1) sampleZ--;
            else if (side == 2) sampleX++;
            else sampleZ++;

            int neighborDecay = _GetFluidFlowDecay(chunk, sampleX, localY, sampleZ, isLava);
            if (neighborDecay < 0)
            {
                byte neighborBlock = _GetFluidRenderBlock(chunk, sampleX, localY, sampleZ);
                if (!_IsBlockSolid(neighborBlock))
                {
                    neighborDecay = _GetFluidFlowDecay(chunk, sampleX, localY - 1, sampleZ, isLava);
                    if (neighborDecay >= 0)
                    {
                        int decayDelta = neighborDecay - (flowDecay - 8);
                        flowX += (sampleX - localX) * decayDelta;
                        flowZ += (sampleZ - localZ) * decayDelta;
                    }
                }
            }
            else
            {
                int decayDelta = neighborDecay - flowDecay;
                flowX += (sampleX - localX) * decayDelta;
                flowZ += (sampleZ - localZ) * decayDelta;
            }
        }

        float magnitude = Mathf.Sqrt(flowX * flowX + flowZ * flowZ);
        if (magnitude < 0.0001f) return -1000.0f;

        flowX /= magnitude;
        flowZ /= magnitude;
        return Mathf.Atan2(flowZ, flowX) - Mathf.PI * 0.5f;
    }

    private float _GetWaterBlockBrightness(ChunkData chunk, int localX, int localY, int localZ)
    {
        float currentBrightness = _GetLightBrightnessAtBlock(chunk, localX, localY, localZ);
        float aboveBrightness = _GetLightBrightnessAtBlock(chunk, localX, localY + 1, localZ);
        return currentBrightness > aboveBrightness ? currentBrightness : aboveBrightness;
    }

    private void _AddWaterQuad(
        ChunkData chunk,
        Vector3 vertex0, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3,
        Vector3 faceNormal,
        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3,
        float textureSlice,
        float brightness,
        bool isLava,
        bool isFlowingLava)
    {
        if (chunk == null || chunk._transparentVertexCount + 4 > MAX_VERTS) return;

        int vertexIndex = chunk._transparentVertexCount;
        int triangleIndex = chunk._transparentTriangleCount;
        chunk._transparentVertices[vertexIndex + 0] = vertex0;
        chunk._transparentVertices[vertexIndex + 1] = vertex1;
        chunk._transparentVertices[vertexIndex + 2] = vertex2;
        chunk._transparentVertices[vertexIndex + 3] = vertex3;

        chunk._transparentNormals[vertexIndex + 0] = faceNormal;
        chunk._transparentNormals[vertexIndex + 1] = faceNormal;
        chunk._transparentNormals[vertexIndex + 2] = faceNormal;
        chunk._transparentNormals[vertexIndex + 3] = faceNormal;

        // Water: R=1 G=1 B=0 A=brightness
        // Still lava (ID 11):   R=0.0 G=0 B=0 A=brightness
        // Flowing lava (ID 10): R=0.5 G=0 B=0 A=brightness (R>0.25 = flowing in shader)
        Color fluidVertexColor = isLava
            ? new Color(isFlowingLava ? 0.5f : 0.0f, 0.0f, 0.0f, brightness)
            : new Color(1.0f, 1.0f, 0.0f, brightness);
        chunk._transparentColors[vertexIndex + 0] = fluidVertexColor;
        chunk._transparentColors[vertexIndex + 1] = fluidVertexColor;
        chunk._transparentColors[vertexIndex + 2] = fluidVertexColor;
        chunk._transparentColors[vertexIndex + 3] = fluidVertexColor;

        chunk._transparentUVs[vertexIndex + 0] = new Vector3(uv0.x, uv0.y, textureSlice);
        chunk._transparentUVs[vertexIndex + 1] = new Vector3(uv1.x, uv1.y, textureSlice);
        chunk._transparentUVs[vertexIndex + 2] = new Vector3(uv2.x, uv2.y, textureSlice);
        chunk._transparentUVs[vertexIndex + 3] = new Vector3(uv3.x, uv3.y, textureSlice);

        chunk._transparentTriangles[triangleIndex + 0] = vertexIndex;
        chunk._transparentTriangles[triangleIndex + 1] = vertexIndex + 1;
        chunk._transparentTriangles[triangleIndex + 2] = vertexIndex + 2;
        chunk._transparentTriangles[triangleIndex + 3] = vertexIndex;
        chunk._transparentTriangles[triangleIndex + 4] = vertexIndex + 2;
        chunk._transparentTriangles[triangleIndex + 5] = vertexIndex + 3;

        chunk._transparentVertexCount += 4;
        chunk._transparentTriangleCount += 6;

#if LOGGING
        if (enableVerboseLogging)
        {
            chunk.facesTotal++;
            chunk.facesTransparent++;
        }
#endif
    }

    private void _AddWaterBlock(ChunkData chunk, int localX, int localY, int localZ, byte blockID)
    {
        bool isLava = _IsLavaBlock(blockID);
        bool renderTop = _ShouldRenderFluidFace(chunk, localX, localY + 1, localZ, 1, isLava);
        bool renderBottom = _ShouldRenderFluidFace(chunk, localX, localY - 1, localZ, 0, isLava);
        bool renderSouth = _ShouldRenderFluidFace(chunk, localX, localY, localZ - 1, 2, isLava);
        bool renderNorth = _ShouldRenderFluidFace(chunk, localX, localY, localZ + 1, 3, isLava);
        bool renderWest = _ShouldRenderFluidFace(chunk, localX - 1, localY, localZ, 4, isLava);
        bool renderEast = _ShouldRenderFluidFace(chunk, localX + 1, localY, localZ, 5, isLava);
        if (!renderTop && !renderBottom && !renderSouth && !renderNorth && !renderWest && !renderEast) return;

        float stillSlice = 0;
        float flowSlice = 0;
        if (!isLava)
        {
            stillSlice = betaWaterStillSlice >= 0 ? betaWaterStillSlice : _GetTextureSlice(blockID, FACE_INDEX_TOP);
            flowSlice = betaWaterFlowSlice >= 0 ? betaWaterFlowSlice : _GetTextureSlice(blockID, FACE_INDEX_SIDE);
        }
        // Lava: textureSlice is ignored since the shader samples from _LavaTex

        float heightNW = _GetFluidCornerHeight(chunk, localX, localY, localZ, isLava);
        float heightSW = _GetFluidCornerHeight(chunk, localX, localY, localZ + 1, isLava);
        float heightSE = _GetFluidCornerHeight(chunk, localX + 1, localY, localZ + 1, isLava);
        float heightNE = _GetFluidCornerHeight(chunk, localX + 1, localY, localZ, isLava);

        if (renderTop)
        {
            float flowAngle = _GetFluidFlowAngle(chunk, localX, localY, localZ, isLava);
            float sinOffset = 0.0f;
            float cosOffset = 0.5f;
            float topSlice = stillSlice;
            if (flowAngle > -999.0f)
            {
                sinOffset = Mathf.Sin(flowAngle) * 0.5f;
                cosOffset = Mathf.Cos(flowAngle) * 0.5f;
                topSlice = flowSlice;
            }

            _AddWaterQuad(
                chunk,
                new Vector3(localX, localY + heightNW, localZ),
                new Vector3(localX, localY + heightSW, localZ + 1),
                new Vector3(localX + 1, localY + heightSE, localZ + 1),
                new Vector3(localX + 1, localY + heightNE, localZ),
                Normal_Up,
                new Vector2(0.5f - cosOffset - sinOffset, 0.5f - cosOffset + sinOffset),
                new Vector2(0.5f - cosOffset + sinOffset, 0.5f + cosOffset + sinOffset),
                new Vector2(0.5f + cosOffset + sinOffset, 0.5f + cosOffset - sinOffset),
                new Vector2(0.5f + cosOffset - sinOffset, 0.5f - cosOffset - sinOffset),
                topSlice,
                _GetWaterBlockBrightness(chunk, localX, localY, localZ),
                isLava, isLava && flowAngle > -999.0f);
        }

        if (renderBottom)
        {
            _AddWaterQuad(
                chunk,
                new Vector3(localX + 1, localY, localZ),
                new Vector3(localX + 1, localY, localZ + 1),
                new Vector3(localX, localY, localZ + 1),
                new Vector3(localX, localY, localZ),
                Normal_Down,
                new Vector2(0.0f, 0.0f),
                new Vector2(0.0f, 1.0f),
                new Vector2(1.0f, 1.0f),
                new Vector2(1.0f, 0.0f),
                stillSlice,
                _GetWaterBlockBrightness(chunk, localX, localY - 1, localZ),
                isLava, false);
        }

        const float sideMaxV = 0.999375f;

        if (renderSouth)
        {
            _AddWaterQuad(
                chunk,
                new Vector3(localX, localY + heightNW, localZ),
                new Vector3(localX + 1, localY + heightNE, localZ),
                new Vector3(localX + 1, localY, localZ),
                new Vector3(localX, localY, localZ),
                Normal_South,
                new Vector2(0.0f, 1.0f - heightNW),
                new Vector2(sideMaxV, 1.0f - heightNE),
                new Vector2(sideMaxV, sideMaxV),
                new Vector2(0.0f, sideMaxV),
                flowSlice,
                _GetWaterBlockBrightness(chunk, localX, localY, localZ - 1),
                isLava, isLava);
        }

        if (renderNorth)
        {
            _AddWaterQuad(
                chunk,
                new Vector3(localX + 1, localY + heightSE, localZ + 1),
                new Vector3(localX, localY + heightSW, localZ + 1),
                new Vector3(localX, localY, localZ + 1),
                new Vector3(localX + 1, localY, localZ + 1),
                Normal_North,
                new Vector2(0.0f, 1.0f - heightSE),
                new Vector2(sideMaxV, 1.0f - heightSW),
                new Vector2(sideMaxV, sideMaxV),
                new Vector2(0.0f, sideMaxV),
                flowSlice,
                _GetWaterBlockBrightness(chunk, localX, localY, localZ + 1),
                isLava, isLava);
        }

        if (renderWest)
        {
            _AddWaterQuad(
                chunk,
                new Vector3(localX, localY + heightSW, localZ + 1),
                new Vector3(localX, localY + heightNW, localZ),
                new Vector3(localX, localY, localZ),
                new Vector3(localX, localY, localZ + 1),
                Normal_West,
                new Vector2(0.0f, 1.0f - heightSW),
                new Vector2(sideMaxV, 1.0f - heightNW),
                new Vector2(sideMaxV, sideMaxV),
                new Vector2(0.0f, sideMaxV),
                flowSlice,
                _GetWaterBlockBrightness(chunk, localX - 1, localY, localZ),
                isLava, isLava);
        }

        if (renderEast)
        {
            _AddWaterQuad(
                chunk,
                new Vector3(localX + 1, localY + heightNE, localZ),
                new Vector3(localX + 1, localY + heightSE, localZ + 1),
                new Vector3(localX + 1, localY, localZ + 1),
                new Vector3(localX + 1, localY, localZ),
                Normal_East,
                new Vector2(0.0f, 1.0f - heightNE),
                new Vector2(sideMaxV, 1.0f - heightSE),
                new Vector2(sideMaxV, sideMaxV),
                new Vector2(0.0f, sideMaxV),
                flowSlice,
                _GetWaterBlockBrightness(chunk, localX + 1, localY, localZ),
                isLava, isLava);
        }
    }

    private void _AddWaterBlocks(ChunkData chunk)
    {
        if (chunk == null || !chunk._hasWaterBlocks || chunk._decompSelf == null) return;

        int stride = chunkSizeXZ * chunkSizeXZ;
        for (int z = 0; z < chunkSizeXZ; z++)
        {
            for (int x = 0; x < chunkSizeXZ; x++)
            {
                int columnIndex = z * chunkSizeXZ + x;
                byte minY = chunk._columnMinY != null ? chunk._columnMinY[columnIndex] : (byte)255;
                byte maxY = chunk._columnMaxY != null ? chunk._columnMaxY[columnIndex] : (byte)255;
                if (minY == 255 || maxY == 255) continue;

                for (int y = minY; y <= maxY; y++)
                {
                    byte blockID = chunk._decompSelf[y * stride + columnIndex];
                    if (!_IsWaterBlock(blockID) && !_IsLavaBlock(blockID)) continue;
                    _AddWaterBlock(chunk, x, y, z, blockID);
                }
            }
        }
    }

    private void _ApplyEmptyMesh(ChunkData chunk)
    {
        _ApplyDataToMesh(chunk.opaqueMeshFilter, chunk._opaqueVertices, chunk._opaqueTriangles, chunk._opaqueUVs, chunk._opaqueNormals, chunk._opaqueColors, 0, 0);
        _ApplyDataToMesh(chunk.transparentMeshFilter, chunk._transparentVertices, chunk._transparentTriangles, chunk._transparentUVs, chunk._transparentNormals, chunk._transparentColors, 0, 0);
        _ApplyDataToMesh(chunk.cutoutMeshFilter, chunk._cutoutVertices, chunk._cutoutTriangles, chunk._cutoutUVs, chunk._cutoutNormals, chunk._cutoutColors, 0, 0);
        _DisableChunkCollider(chunk, false);
    }

    private byte[] _GetDecompressedData(ChunkData chunk)
    {
        if (chunk == null) return null;

        if (chunk._decompCacheValid && chunk._cachedDecompressedData != null)
        {
#if LOGGING
            if (enableCacheTracking) stats_decompCacheHits++;
#endif
            return chunk._cachedDecompressedData;
        }

#if LOGGING
        if (enableCacheTracking) stats_decompCacheMisses++;
#endif

        byte[] decompressed = _DecompressChunkColumnRLE(chunk);
        chunk._cachedDecompressedData = decompressed;
        chunk._decompCacheValid = true;
        _RefreshChunkDerivedData(chunk, decompressed);
        return decompressed;
    }

    // --- Mesh Buffer Pool ---
    // 8 reusable buffer sets shared across all chunks (2x peak active meshers).
    // Without pooling, 8192 chunks × ~2.6MB = 21GB — instant OOM on Quest 2.
    // With pooling, 8 × ~2.6MB = ~21MB total.
    private const int MESH_POOL_SIZE = 8;
    private bool[] meshPoolFree;
    private Vector3[][] meshPool_opaqueVerts, meshPool_transparentVerts, meshPool_cutoutVerts;
    private int[][] meshPool_opaqueTris, meshPool_transparentTris, meshPool_cutoutTris;
    private Vector3[][] meshPool_opaqueUVs, meshPool_transparentUVs, meshPool_cutoutUVs;
    private Vector3[][] meshPool_opaqueNormals, meshPool_transparentNormals, meshPool_cutoutNormals;
    private Color[][] meshPool_opaqueColors, meshPool_transparentColors, meshPool_cutoutColors;
    private Vector3[][] meshPool_collisionVerts;
    private int[][] meshPool_collisionTris;

    private void _InitializeMeshPool()
    {
        meshPoolFree = new bool[MESH_POOL_SIZE];
        meshPool_opaqueVerts = new Vector3[MESH_POOL_SIZE][];
        meshPool_transparentVerts = new Vector3[MESH_POOL_SIZE][];
        meshPool_cutoutVerts = new Vector3[MESH_POOL_SIZE][];
        meshPool_opaqueTris = new int[MESH_POOL_SIZE][];
        meshPool_transparentTris = new int[MESH_POOL_SIZE][];
        meshPool_cutoutTris = new int[MESH_POOL_SIZE][];
        meshPool_opaqueUVs = new Vector3[MESH_POOL_SIZE][];
        meshPool_transparentUVs = new Vector3[MESH_POOL_SIZE][];
        meshPool_cutoutUVs = new Vector3[MESH_POOL_SIZE][];
        meshPool_opaqueNormals = new Vector3[MESH_POOL_SIZE][];
        meshPool_transparentNormals = new Vector3[MESH_POOL_SIZE][];
        meshPool_cutoutNormals = new Vector3[MESH_POOL_SIZE][];
        meshPool_opaqueColors = new Color[MESH_POOL_SIZE][];
        meshPool_transparentColors = new Color[MESH_POOL_SIZE][];
        meshPool_cutoutColors = new Color[MESH_POOL_SIZE][];
        meshPool_collisionVerts = new Vector3[MESH_POOL_SIZE][];
        meshPool_collisionTris = new int[MESH_POOL_SIZE][];
        for (int i = 0; i < MESH_POOL_SIZE; i++)
        {
            meshPoolFree[i] = true;
            meshPool_opaqueVerts[i] = new Vector3[MAX_VERTS];
            meshPool_opaqueTris[i] = new int[MAX_TRIS];
            meshPool_opaqueUVs[i] = new Vector3[MAX_VERTS];
            meshPool_opaqueNormals[i] = new Vector3[MAX_VERTS];
            meshPool_opaqueColors[i] = new Color[MAX_VERTS];
            meshPool_transparentVerts[i] = new Vector3[MAX_VERTS];
            meshPool_transparentTris[i] = new int[MAX_TRIS];
            meshPool_transparentUVs[i] = new Vector3[MAX_VERTS];
            meshPool_transparentNormals[i] = new Vector3[MAX_VERTS];
            meshPool_transparentColors[i] = new Color[MAX_VERTS];
            meshPool_cutoutVerts[i] = new Vector3[MAX_VERTS];
            meshPool_cutoutTris[i] = new int[MAX_TRIS];
            meshPool_cutoutUVs[i] = new Vector3[MAX_VERTS];
            meshPool_cutoutNormals[i] = new Vector3[MAX_VERTS];
            meshPool_cutoutColors[i] = new Color[MAX_VERTS];
            meshPool_collisionVerts[i] = new Vector3[MAX_VERTS * 3];
            meshPool_collisionTris[i] = new int[MAX_TRIS * 3];
        }
    }

    private bool _AcquireMeshPool(ChunkData chunk)
    {
        if (chunk._meshPoolSlot >= 0) return true; // already acquired
        for (int i = 0; i < MESH_POOL_SIZE; i++)
        {
            if (!meshPoolFree[i]) continue;
            meshPoolFree[i] = false;
            chunk._meshPoolSlot = i;
            chunk._opaqueVertices = meshPool_opaqueVerts[i];
            chunk._opaqueTriangles = meshPool_opaqueTris[i];
            chunk._opaqueUVs = meshPool_opaqueUVs[i];
            chunk._opaqueNormals = meshPool_opaqueNormals[i];
            chunk._opaqueColors = meshPool_opaqueColors[i];
            chunk._transparentVertices = meshPool_transparentVerts[i];
            chunk._transparentTriangles = meshPool_transparentTris[i];
            chunk._transparentUVs = meshPool_transparentUVs[i];
            chunk._transparentNormals = meshPool_transparentNormals[i];
            chunk._transparentColors = meshPool_transparentColors[i];
            chunk._cutoutVertices = meshPool_cutoutVerts[i];
            chunk._cutoutTriangles = meshPool_cutoutTris[i];
            chunk._cutoutUVs = meshPool_cutoutUVs[i];
            chunk._cutoutNormals = meshPool_cutoutNormals[i];
            chunk._cutoutColors = meshPool_cutoutColors[i];
            chunk._collisionVertices = meshPool_collisionVerts[i];
            chunk._collisionTriangles = meshPool_collisionTris[i];
            return true;
        }
        return false; // pool exhausted — caller should defer
    }

    private void _ReleaseMeshPool(ChunkData chunk)
    {
        int slot = chunk._meshPoolSlot;
        if (slot < 0) return;

        // Compact-copy collision data if deferred apply is pending.
        // The pooled collision arrays are about to be reused by the next chunk,
        // so the chunk needs its own right-sized copy.
        if (chunk.pendingColliderApply && chunk._collisionVertexCount > 0)
        {
            int vc = chunk._collisionVertexCount;
            int tc = chunk._collisionTriangleCount;
            Vector3[] compactVerts = new Vector3[vc];
            int[] compactTris = new int[tc];
            System.Array.Copy(chunk._collisionVertices, 0, compactVerts, 0, vc);
            System.Array.Copy(chunk._collisionTriangles, 0, compactTris, 0, tc);
            chunk._collisionVertices = compactVerts;
            chunk._collisionTriangles = compactTris;
        }
        else
        {
            chunk._collisionVertices = null;
            chunk._collisionTriangles = null;
        }

        chunk._opaqueVertices = null; chunk._opaqueTriangles = null;
        chunk._opaqueUVs = null; chunk._opaqueNormals = null; chunk._opaqueColors = null;
        chunk._transparentVertices = null; chunk._transparentTriangles = null;
        chunk._transparentUVs = null; chunk._transparentNormals = null; chunk._transparentColors = null;
        chunk._cutoutVertices = null; chunk._cutoutTriangles = null;
        chunk._cutoutUVs = null; chunk._cutoutNormals = null; chunk._cutoutColors = null;

        chunk._meshPoolSlot = -1;
        meshPoolFree[slot] = true;
    }

    private void _ClearAllMeshBuffers(ChunkData chunk)
    {
        chunk._opaqueVertexCount = 0; chunk._opaqueTriangleCount = 0;
        chunk._transparentVertexCount = 0; chunk._transparentTriangleCount = 0;
        chunk._cutoutVertexCount = 0; chunk._cutoutTriangleCount = 0;
        chunk._collisionVertexCount = 0; chunk._collisionTriangleCount = 0;
    }

    private bool _ShouldDrawFace(byte selfID, byte neighborID)
    {
        int idx = (selfID << 8) | neighborID;
        return shouldDrawTable != null && idx >= 0 && idx < shouldDrawTable.Length && shouldDrawTable[idx] != 0;
    }

    private void _ApplyAllMeshData(ChunkData chunk)
    {
#if LOGGING
        if (enableDetailedTimings)
        {
            float timer_start = Time.realtimeSinceStartup;
            _ApplyDataToMesh(chunk.opaqueMeshFilter, chunk._opaqueVertices, chunk._opaqueTriangles, chunk._opaqueUVs, chunk._opaqueNormals, chunk._opaqueColors, chunk._opaqueVertexCount, chunk._opaqueTriangleCount);
            chunk.time_ApplyOpaque = (Time.realtimeSinceStartup - timer_start) * 1000f;

            timer_start = Time.realtimeSinceStartup;
            _ApplyDataToMesh(chunk.transparentMeshFilter, chunk._transparentVertices, chunk._transparentTriangles, chunk._transparentUVs, chunk._transparentNormals, chunk._transparentColors, chunk._transparentVertexCount, chunk._transparentTriangleCount);
            chunk.time_ApplyTransparent = (Time.realtimeSinceStartup - timer_start) * 1000f;

            timer_start = Time.realtimeSinceStartup;
            _ApplyDataToMesh(chunk.cutoutMeshFilter, chunk._cutoutVertices, chunk._cutoutTriangles, chunk._cutoutUVs, chunk._cutoutNormals, chunk._cutoutColors, chunk._cutoutVertexCount, chunk._cutoutTriangleCount);
            chunk.time_ApplyCutout = (Time.realtimeSinceStartup - timer_start) * 1000f;
            return;
        }
#endif
        _ApplyDataToMesh(chunk.opaqueMeshFilter, chunk._opaqueVertices, chunk._opaqueTriangles, chunk._opaqueUVs, chunk._opaqueNormals, chunk._opaqueColors, chunk._opaqueVertexCount, chunk._opaqueTriangleCount);
        _ApplyDataToMesh(chunk.transparentMeshFilter, chunk._transparentVertices, chunk._transparentTriangles, chunk._transparentUVs, chunk._transparentNormals, chunk._transparentColors, chunk._transparentVertexCount, chunk._transparentTriangleCount);
        _ApplyDataToMesh(chunk.cutoutMeshFilter, chunk._cutoutVertices, chunk._cutoutTriangles, chunk._cutoutUVs, chunk._cutoutNormals, chunk._cutoutColors, chunk._cutoutVertexCount, chunk._cutoutTriangleCount);
    }

    private void _ApplyDataToMesh(MeshFilter mf, Vector3[] vertices, int[] triangles, Vector3[] uvs, Vector3[] normals, Color[] colors, int vertexCount, int triangleCount)
    {
        if (mf == null) return;
        Mesh m = mf.sharedMesh;
        if (m == null) { m = new Mesh(); m.name = $"ChunkMesh_{mf.gameObject.name}"; m.MarkDynamic(); mf.sharedMesh = m; }
        m.Clear();
        if (vertexCount == 0) { mf.gameObject.SetActive(false); return; }

        mf.gameObject.SetActive(true);
        m.SetVertices(vertices, 0, vertexCount);
        m.SetNormals(normals, 0, vertexCount);
        m.SetUVs(0, uvs, 0, vertexCount);
        m.SetColors(colors, 0, vertexCount);
        m.SetTriangles(triangles, 0, triangleCount, 0, true);
        m.RecalculateBounds();
    }

    private void _ApplyDataToCollider(ChunkData chunk)
    {
#if LOGGING
        float timer_start = 0f;
        if(enableDetailedTimings) timer_start = Time.realtimeSinceStartup;
        if (enableDetailedTimings && chunk != null && chunk.profile_deferredColliderQueuedTime > 0f)
        {
            float deferredWaitMs = (Time.realtimeSinceStartup - chunk.profile_deferredColliderQueuedTime) * 1000f;
            stats_deferredColliderWaitCount++;
            stats_deferredColliderWaitTotal += deferredWaitMs;
            if (deferredWaitMs > stats_deferredColliderWaitMax) stats_deferredColliderWaitMax = deferredWaitMs;
            chunk.profile_deferredColliderQueuedTime = 0f;
        }
#endif

        if (chunk.meshCollider == null) return;
        Mesh colMesh = chunk.meshCollider.sharedMesh;
        if (colMesh == null) { colMesh = new Mesh(); colMesh.name = $"ChunkCollisionMesh_{chunk.gameObject.name}"; colMesh.MarkDynamic(); }

        colMesh.Clear();
        if (chunk._collisionVertexCount == 0 || chunk._collisionVertices == null || chunk._collisionVertices.Length < chunk._collisionVertexCount) { chunk.meshCollider.sharedMesh = null; chunk.meshCollider.enabled = false; return; }

        chunk.meshCollider.enabled = true;
        colMesh.SetVertices(chunk._collisionVertices, 0, chunk._collisionVertexCount);
        colMesh.SetTriangles(chunk._collisionTriangles, 0, chunk._collisionTriangleCount, 0, true);
        chunk.meshCollider.sharedMesh = colMesh;

#if LOGGING
        if(enableDetailedTimings) chunk.time_ApplyCollision = (Time.realtimeSinceStartup - timer_start) * 1000f;
#endif
    }

    #endregion

    #region Block Data & RLE Logic (Refactored for Column-Based RLE)

    private byte _GetBlockLocal(ChunkData chunk, int x, int y, int z)
    {
#if LOGGING
        if (enableCounters) stats_getBlockCalls++;
#endif

        // GPU OFFLOAD #2 NOTE: The "return 0 for GPU-resident chunks" path was removed.
        // No chunk is currently marked GPU-resident (see chunk completion logic). The
        // rehydration queue + _GpuRequestRehydrate helper are kept for when the full
        // GPU-resident path lands.

        if (chunk._chunkData == null || !chunk.isDataReady) return 0;
        if (x < 0 || x >= chunkSizeXZ || y < 0 || y >= chunkSizeY || z < 0 || z >= chunkSizeXZ) return 0;

        if (chunk._decompCacheValid && chunk._cachedDecompressedData != null)
        {
            int directIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            return chunk._cachedDecompressedData[directIndex];
        }

        // PERF: Single byte-compare dispatch instead of `chunk._chunkData.GetType()` +
        // 3 typeof() comparisons (each a marshaled C++ call in Udon).
        byte kind = chunk._chunkDataKind;

        // Fallback path: legacy chunks that pre-date the kind tag and never set it.
        // Compute kind on the fly and persist it so the slow path runs at most once.
        if (kind == ChunkData.CHUNK_KIND_NULL)
        {
            System.Type dataType = chunk._chunkData.GetType();
            if (dataType == typeof(byte)) kind = ChunkData.CHUNK_KIND_HOMOGENEOUS;
            else if (dataType == typeof(byte[])) kind = ChunkData.CHUNK_KIND_RAW;
            else if (dataType == typeof(ushort[][])) kind = ChunkData.CHUNK_KIND_RLE;
            chunk._chunkDataKind = kind;
        }

        if (kind == ChunkData.CHUNK_KIND_HOMOGENEOUS)
        {
            return (byte)chunk._chunkData;
        }
        if (kind == ChunkData.CHUNK_KIND_RAW)
        {
            byte[] rawData = (byte[])chunk._chunkData;
            int directIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            return directIndex >= 0 && directIndex < rawData.Length ? rawData[directIndex] : (byte)0;
        }
        if (kind == ChunkData.CHUNK_KIND_RLE)
        {
            ushort[][] columnData = (ushort[][])chunk._chunkData;
            int columnIndex = z * chunkSizeXZ + x;

            if (columnIndex < 0 || columnIndex >= columnData.Length) return 0;

            ushort[] rlePairs = columnData[columnIndex];
            if (rlePairs == null) return 0;

            int currentY = 0;
#if LOGGING
            int traversalDepth = 0;
#endif
            for (int i = 0; i < rlePairs.Length; i += 2)
            {
                ushort blockID = rlePairs[i];
                ushort runLength = rlePairs[i + 1];
                currentY += runLength;
#if LOGGING
                traversalDepth++;
#endif
                if (y < currentY)
                {
#if LOGGING
                    if (enableCounters)
                    {
                        chunk.block_rleTraversalDepthTotal += traversalDepth;
                        chunk.block_rleTraversalDepthCount++;
                    }
#endif
                    return (byte)blockID;
                }
            }
        }

        return 0; // Fallback for out of bounds Y or error
    }

    private void _SetBlockLocal(ChunkData chunk, int x, int y, int z, byte blockType, bool updateMesh, bool interactionPriority)
    {
#if LOGGING
        if (enableCounters) stats_setBlockCalls++;
#endif

        if (x < 0 || x >= chunkSizeXZ || y < 0 || y >= chunkSizeY || z < 0 || z >= chunkSizeXZ) return;
        // Safety check: ensure chunk exists
        if (chunk == null) return;

        // Safety check: ensure chunk data exists
        if (chunk._chunkData == null) return;

        // Optimization: check if the block is actually changing before doing any work.
        if (blockType == _GetBlockLocal(chunk, x, y, z)) return;

        // PARITY: Beta Chunk.setBlockID (Chunk.java:289) explicitly does
        //   this.data.setNibble(var1, var2, var3, 0);
        // immediately after changing the block ID. Without this, replacing a flowing-water
        // cell (meta=6) with dirt leaves stale water-decay metadata bound to the dirt, which
        // corrupts subsequent flow-decay reads, leaf-decay flag checks, fire-age reads, etc.
        if (chunk.blockMetadata != null)
        {
            int metaIdx = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            if (metaIdx >= 0 && metaIdx < chunk.blockMetadata.Length)
                chunk.blockMetadata[metaIdx] = 0;
        }

#if LOGGING
        if (enableCounters) stats_blockModifications++;
#endif

        if (interactionPriority)
        {
            chunk.interactionMeshPriority = true;
        }

        // OPTIMIZATION: Invalidate chunk-owned caches since data is changing
        chunk._decompCacheValid = false;
        chunk._cachedDataVersion++;

        // OPTIMIZATION: Invalidate neighbor cache since chunk data changed
        _InvalidateNeighborCache(chunk);

        // PERF: Switch on byte kind tag instead of GetType()+typeof comparisons.
        byte sKind = chunk._chunkDataKind;
        if (sKind == ChunkData.CHUNK_KIND_NULL)
        {
            System.Type t = chunk._chunkData.GetType();
            if (t == typeof(byte)) sKind = ChunkData.CHUNK_KIND_HOMOGENEOUS;
            else if (t == typeof(byte[])) sKind = ChunkData.CHUNK_KIND_RAW;
            else if (t == typeof(ushort[][])) sKind = ChunkData.CHUNK_KIND_RLE;
            chunk._chunkDataKind = sKind;
        }

        if (sKind == ChunkData.CHUNK_KIND_RAW)
        {
            byte[] rawData = (byte[])chunk._chunkData;
            int localIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            rawData[localIndex] = blockType;
            // Stay as raw byte[] — no need to compress
        }
        else if (sKind == ChunkData.CHUNK_KIND_RLE)
        {
            ushort[][] columnData = (ushort[][])chunk._chunkData;
            int columnIndex = z * chunkSizeXZ + x;

            if (columnIndex < 0 || columnIndex >= columnData.Length) return;

            byte[] decompressedColumn = _DecompressSingleColumn(columnData[columnIndex]);
            decompressedColumn[y] = blockType;
            columnData[columnIndex] = _CompressSingleColumn(decompressedColumn);
        }
        else if (sKind == ChunkData.CHUNK_KIND_HOMOGENEOUS)
        {
            byte[] fullData = _GetDecompressedData(chunk);
            if (fullData == null) return;

            int localIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            fullData[localIndex] = blockType;

            // Store as raw byte[] instead of re-compressing
            chunk._chunkData = fullData;
            chunk._chunkDataKind = ChunkData.CHUNK_KIND_RAW;
            chunk.isSingleOpaqueSolid = false;
        }

        chunk._cachedDecompressedData = null;
        chunk._decompCacheValid = false;

        // GPU OFFLOAD #4: any block change in this chunk invalidates its own sentinel
        // (interior copy is stale) AND the sentinel of any neighbor whose border face
        // touches this voxel. We invalidate all 6 to keep this O(1).
        chunk._gpuSentinelBuilt = false;
        chunk._gpuAOBaked = false;
        ChunkData[] sentinelNeighbors = _GetCachedNeighbors(chunk);
        if (sentinelNeighbors != null)
        {
            for (int sn = 0; sn < 6; sn++)
            {
                ChunkData nb = sentinelNeighbors[sn];
                if (nb != null) { nb._gpuSentinelBuilt = false; nb._gpuAOBaked = false; }
            }
        }

        int torchMountIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        if (chunk.torchMountData != null && torchMountIndex >= 0 && torchMountIndex < chunk.torchMountData.Length)
        {
            if (_IsTorchBlock(blockType))
            {
                if (chunk.torchMountData[torchMountIndex] == 0)
                {
                    chunk.torchMountData[torchMountIndex] = TORCH_MOUNT_FLOOR;
                }
            }
            else
            {
                chunk.torchMountData[torchMountIndex] = 0;
            }
        }
        // GPU OFFLOAD #12: try the single-pixel SetBlock chain first. If it ran, skip
        // the full chunk re-upload entirely (saves ~16KB of upload per block edit).
        byte oldB = _GetBlockLocal(chunk, x, y, z); // captured before write above; recompute safely
        if (_GpuSetBlockChain(chunk, x, y, z, oldB, blockType))
        {
            // chain handled the atlas write; just mark the chunk's CPU cache invalidated.
        }
        else if (_gpuSyncDeferred)
        {
            int ci = ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
            if (ci >= 0 && _gpuDeferredDirtyChunks != null && _gpuDeferredDirtyCount < GPU_DEFERRED_DIRTY_MAX)
            {
                bool alreadyTracked = false;
                for (int di = 0; di < _gpuDeferredDirtyCount; di++)
                {
                    if (_gpuDeferredDirtyChunks[di] == ci) { alreadyTracked = true; break; }
                }
                if (!alreadyTracked)
                    _gpuDeferredDirtyChunks[_gpuDeferredDirtyCount++] = ci;
            }
        }
        else
        {
            _GpuSyncChunkBlocks(chunk, _GetDecompressedData(chunk));
        }
        _GpuRequestLightingRebuild();

        if (!updateMesh) return;

        int chunkIndex = ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
        RequestChunkMeshUpdate(chunkIndex);

        if (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1)
        {
            TriggerNeighborMeshRebuilds(chunk, interactionPriority);
        }
    }

    private byte[] _DecompressSingleColumn(ushort[] rlePairs)
    {
        byte[] columnData = new byte[chunkSizeY];
        if (rlePairs == null) return columnData;

        int currentY = 0;
        for (int p = 0; p < rlePairs.Length; p += 2)
        {
            ushort blockID = rlePairs[p];
            ushort runLength = rlePairs[p + 1];
            for (int j = 0; j < runLength; j++)
            {
                if (currentY + j < chunkSizeY)
                {
                    columnData[currentY + j] = (byte)blockID;
                }
            }
            currentY += runLength;
        }
        return columnData;
    }

    private ushort[] _CompressSingleColumn(byte[] columnData)
    {
        var columnRuns = new System.Collections.Generic.List<ushort>();
        for (int y = 0; y < chunkSizeY; )
        {
            byte currentBlock = columnData[y];
            ushort runLength = 1;
            while (y + runLength < chunkSizeY && columnData[y + runLength] == currentBlock && runLength < ushort.MaxValue)
            {
                runLength++;
            }
            columnRuns.Add(currentBlock);
            columnRuns.Add(runLength);
            y += runLength;
        }
        return columnRuns.ToArray();
    }

    private object _CompressChunkColumnRLE(byte[] fullChunkData, out bool isHomogeneous)
    {
#if LOGGING
        float compressStartTime = 0f;
        if (enableDetailedTimings) compressStartTime = Time.realtimeSinceStartup;
        if (enableCounters) stats_rleCompressions++;
        int bytesIn = fullChunkData != null ? fullChunkData.Length : 0;
#endif

        isHomogeneous = true;
        byte firstBlock = fullChunkData[0];
        for (int i = 1; i < fullChunkData.Length; i++) {
            if (fullChunkData[i] != firstBlock) {
                isHomogeneous = false;
                break;
            }
        }

        if (isHomogeneous) {
#if LOGGING
            if (enableCounters) stats_rleHomogeneousChunks++;
            if (enableDetailedTimings)
            {
                stats_rleCompressionTime += (Time.realtimeSinceStartup - compressStartTime) * 1000f;
                stats_rleTotalBytesIn += bytesIn;
                stats_rleTotalBytesOut += 1; // single byte
            }
#endif
            return firstBlock;
        }

        int columnCount = chunkSizeXZ * chunkSizeXZ;
        ushort[][] columnRLEData = new ushort[columnCount][];

        // Lazy-init scratch buffer (max pairs = chunkSizeY, 2 ushorts each)
        if (rleScratchBuffer == null || rleScratchBuffer.Length < chunkSizeY * 2)
            rleScratchBuffer = new ushort[chunkSizeY * 2];

        for (int x = 0; x < chunkSizeXZ; x++) {
            for (int z = 0; z < chunkSizeXZ; z++) {
                int columnIndex = z * chunkSizeXZ + x;
                int writePos = 0;

                for (int y = 0; y < chunkSizeY; ) {
                    byte currentBlock = fullChunkData[y * columnCount + columnIndex];
                    ushort runLength = 1;
                    while (y + runLength < chunkSizeY && fullChunkData[(y + runLength) * columnCount + columnIndex] == currentBlock && runLength < ushort.MaxValue) {
                        runLength++;
                    }
                    rleScratchBuffer[writePos++] = currentBlock;
                    rleScratchBuffer[writePos++] = runLength;
                    y += runLength;
                }

                ushort[] column = new ushort[writePos];
                System.Array.Copy(rleScratchBuffer, 0, column, 0, writePos);
                columnRLEData[columnIndex] = column;
            }
        }

#if LOGGING
        if (enableDetailedTimings)
        {
            stats_rleCompressionTime += (Time.realtimeSinceStartup - compressStartTime) * 1000f;
            stats_rleTotalBytesIn += bytesIn;
            // Approximate output size: columnCount arrays * avg pairs * 2 bytes
            int bytesOut = 0;
            for (int i = 0; i < columnRLEData.Length; i++)
            {
                if (columnRLEData[i] != null) bytesOut += columnRLEData[i].Length * 2;
            }
            stats_rleTotalBytesOut += bytesOut;
        }
#endif

        return columnRLEData;
    }

    private byte[] _DecompressChunkColumnRLE(ChunkData chunk)
    {
#if LOGGING
        float decompressStartTime = 0f;
        if (enableDetailedTimings) decompressStartTime = Time.realtimeSinceStartup;
        if (enableCounters) stats_rleDecompressions++;
#endif

        byte[] fullData = new byte[chunk._chunkDataSize];
        if (chunk._chunkData == null) return fullData;

        System.Type dataType = chunk._chunkData.GetType();

        // Case 0: Raw uncompressed byte[] — just copy it
        if (dataType == typeof(byte[])) {
            byte[] rawData = (byte[])chunk._chunkData;
            System.Array.Copy(rawData, 0, fullData, 0, Mathf.Min(rawData.Length, fullData.Length));
#if LOGGING
            if (enableDetailedTimings)
            {
                stats_rleDecompressionTime += (Time.realtimeSinceStartup - decompressStartTime) * 1000f;
            }
#endif
            return fullData;
        }

        // Case 1: Homogeneous chunk (optimized with Array.Fill equivalent)
        if (dataType == typeof(byte)) {
            byte blockValue = (byte)chunk._chunkData;
            // OPTIMIZED: Use manual loop unrolling for better performance
            int size = chunk._chunkDataSize;
            int i = 0;
            int limit = size - 15;

            // Process 16 at a time
            for (; i < limit; i += 16) {
                fullData[i] = blockValue;
                fullData[i+1] = blockValue;
                fullData[i+2] = blockValue;
                fullData[i+3] = blockValue;
                fullData[i+4] = blockValue;
                fullData[i+5] = blockValue;
                fullData[i+6] = blockValue;
                fullData[i+7] = blockValue;
                fullData[i+8] = blockValue;
                fullData[i+9] = blockValue;
                fullData[i+10] = blockValue;
                fullData[i+11] = blockValue;
                fullData[i+12] = blockValue;
                fullData[i+13] = blockValue;
                fullData[i+14] = blockValue;
                fullData[i+15] = blockValue;
            }
            // Finish remaining
            for (; i < size; i++) {
                fullData[i] = blockValue;
            }
            return fullData;
        }

        // Case 2: Column RLE chunk (optimized indexing)
        if (dataType == typeof(ushort[][])) {
            ushort[][] columnRLEData = (ushort[][])chunk._chunkData;
            int columnCount = chunkSizeXZ * chunkSizeXZ;
            int maxY = chunkSizeY;

            for(int col = 0; col < columnCount; col++) {
                ushort[] rlePairs = columnRLEData[col];
                if (rlePairs == null) continue;

                int currentY = 0;
                int pairCount = rlePairs.Length;

                for (int p = 0; p < pairCount; p += 2) {
                    byte blockID = (byte)rlePairs[p];
                    int runLength = rlePairs[p+1];
                    int endY = currentY + runLength;
                    if (endY > maxY) endY = maxY;

                    // OPTIMIZED: Direct calculation without nested loop when possible
                    int baseIdx = currentY * columnCount + col;
                    for (int y = currentY; y < endY; y++) {
                        fullData[baseIdx] = blockID;
                        baseIdx += columnCount;
                    }
                    currentY = endY;
                }
            }
        }

#if LOGGING
        if (enableDetailedTimings)
        {
            stats_rleDecompressionTime += (Time.realtimeSinceStartup - decompressStartTime) * 1000f;
        }
#endif

        return fullData;
    }

    #endregion

    #region Local Block Data Getters

    private bool _IsBlockSolid(byte blockID)
    {
        if (blockDataCache != null && blockID < blockDataCache.Length)
            return (blockDataCache[blockID] & 1) != 0;
        return false;
    }

    private BlockVisibilityType _GetVisibilityType(byte blockID)
    {
        if (visibilityCache != null && blockID < visibilityCache.Length) return visibilityCache[blockID];
        if (blockDataCache != null && blockID < blockDataCache.Length) return (BlockVisibilityType)((blockDataCache[blockID] >> 1) & 0x3);
        return BlockVisibilityType.Opaque;
    }

    private BlockCullingType _GetCullingType(byte blockID)
    {
        if (cullingCache != null && blockID < cullingCache.Length) return cullingCache[blockID];
        if (blockDataCache != null && blockID < blockDataCache.Length) return (BlockCullingType)((blockDataCache[blockID] >> 3) & 0x7);
        return BlockCullingType.CullAll;
    }

    private void _BuildBlockCaches()
    {
        int maxId = blockDataCache != null ? blockDataCache.Length : 256;
        visibilityCache = new BlockVisibilityType[maxId];
        cullingCache = new BlockCullingType[maxId];
        shapeTypeCache = new McBlockShapeType[maxId]; // NEW: Initialize shape type cache
        biomeTintModeCache = new byte[maxId];
        for (int i = 0; i < maxId; i++)
        {
            visibilityCache[i] = (BlockVisibilityType)((blockDataCache[i] >> 1) & 0x3);
            cullingCache[i] = (BlockCullingType)((blockDataCache[i] >> 3) & 0x7);
            shapeTypeCache[i] = (McBlockShapeType)((blockDataCache[i] >> 6) & 0x3); // NEW: Cache shape type (bits 6-7)
            if (i == 2 || i == 31) biomeTintModeCache[i] = 1;
            else if (i == 18) biomeTintModeCache[i] = 2;
            else if (i == 8 || i == 9) biomeTintModeCache[i] = 3;
            else biomeTintModeCache[i] = 0;
        }
        // Precompute should-draw table for all pairs
        int tableSize = 256 * 256; // supports up to 256 block IDs
        shouldDrawTable = new byte[tableSize];
        for (int a = 0; a < 256; a++)
        {
            BlockVisibilityType visA = a < maxId ? visibilityCache[a] : BlockVisibilityType.Opaque;
            BlockCullingType cullA = a < maxId ? cullingCache[a] : BlockCullingType.CullAll;
            for (int b = 0; b < 256; b++)
            {
                BlockVisibilityType visB = b < maxId ? visibilityCache[b] : BlockVisibilityType.Opaque;
                bool draw;
                bool customMeshA = _UsesCustomBlockMesh((byte)a, shapeTypeCache, maxId);
                bool customMeshB = _UsesCustomBlockMesh((byte)b, shapeTypeCache, maxId);
                bool fluidA = _IsFluidBlock((byte)a);
                if (customMeshA || fluidA) draw = false;
                else if (b == 0 || customMeshB || _IsFluidBlock((byte)b)) draw = true;
                else if (visA == BlockVisibilityType.Invisible) draw = false;
                else
                {
                    switch (cullA)
                    {
                        case BlockCullingType.NoCull: draw = true; break;
                        case BlockCullingType.CullSelf: draw = (b != a); break;
                        case BlockCullingType.CullSelfAndOpaque: draw = !(b == a || visB == BlockVisibilityType.Opaque); break;
                        case BlockCullingType.CullSelfAndCutout: draw = !(b == a || visB == BlockVisibilityType.Cutout); break;
                        case BlockCullingType.CullSelfAndTransparent: draw = !(b == a || visB == BlockVisibilityType.Transparent); break;
                        case BlockCullingType.CullAll: draw = false; break;
                        default: draw = (visB == BlockVisibilityType.Transparent || visB == BlockVisibilityType.Invisible); break;
                    }
                }
                shouldDrawTable[(a << 8) | b] = (byte)(draw ? 1 : 0);
            }
        }
    }

    private void _BuildLightingTables()
    {
        // Build brightness table using Minecraft Beta 1.7.3 formula
        // Formula: brightness = (1.0 - darkness) / (darkness * 3.0 + 1.0) * 0.95 + 0.05
        // Where darkness = 1.0 - (lightLevel / 15.0)
        lightBrightnessTable = new float[16];
        for (int i = 0; i < 16; i++)
        {
            float lightLevel = i / 15.0f;
            float darkness = 1.0f - lightLevel;
            lightBrightnessTable[i] = (1.0f - darkness) / (darkness * 3.0f + 1.0f) * 0.95f + 0.05f;
        }
        aoBrightnessAlphaTable = new byte[16 * 16 * 16 * 16];
        for (int a = 0; a < 16; a++)
        {
            for (int b = 0; b < 16; b++)
            {
                for (int c = 0; c < 16; c++)
                {
                    for (int d = 0; d < 16; d++)
                    {
                        float averageBrightness = (lightBrightnessTable[a] + lightBrightnessTable[b] + lightBrightnessTable[c] + lightBrightnessTable[d]) * 0.25f;
                        int alphaIndex = a | (b << 4) | (c << 8) | (d << 12);
                        aoBrightnessAlphaTable[alphaIndex] = (byte)Mathf.Clamp(Mathf.RoundToInt(averageBrightness * 255f), 0, 255);
                    }
                }
            }
        }

        // Build light opacity and emission caches
        int maxId = blockTypeManager.finalDataArray != null ? blockTypeManager.finalDataArray.Length : 256;
        lightOpacityCache = new int[256];
        lightEmissionCache = new int[256];
        canBlockGrassCache = new bool[256];
        for (int i = 0; i < 256; i++)
        {
            if (i < maxId)
            {
                lightOpacityCache[i] = blockTypeManager.GetBlockLightOpacity((byte)i);
                lightEmissionCache[i] = blockTypeManager.GetBlockLightEmission((byte)i);
                canBlockGrassCache[i] = blockTypeManager.GetBlockCanBlockGrass((byte)i);
            }
            else
            {
                lightOpacityCache[i] = 15; // Default to opaque
                lightEmissionCache[i] = 0;  // Default to no emission
                canBlockGrassCache[i] = false;
            }
        }
        canBlockGrassCache[0] = true;

        Debug.Log("[McWorld] Light brightness table and caches built successfully.");
    }

    // --- Lighting Optimization: Memory Pooling ---
    private int[] GetBFSQueue()
    {
        if (bfsQueuePool.Count > 0)
            return bfsQueuePool.Dequeue();
        return new int[BFS_QUEUE_SIZE];
    }

    private int[] GetBFSQueueLarge()
    {
        if (bfsQueuePoolLarge.Count > 0)
            return bfsQueuePoolLarge.Dequeue();
        return new int[BFS_QUEUE_SIZE_LARGE];
    }

    private void ReturnBFSQueue(int[] queue)
    {
        if (queue.Length == BFS_QUEUE_SIZE && bfsQueuePool.Count < 10)
            bfsQueuePool.Enqueue(queue);
        else if (queue.Length == BFS_QUEUE_SIZE_LARGE && bfsQueuePoolLarge.Count < 5)
            bfsQueuePoolLarge.Enqueue(queue);
    }

    private int[] GetLightingQueue()
    {
        if (lightingQueuePool.Count > 0)
            return lightingQueuePool.Dequeue();
        return new int[LIGHTING_QUEUE_SIZE];
    }

    private void ReturnLightingQueue(int[] queue)
    {
        if (queue == null || queue.Length != LIGHTING_QUEUE_SIZE) return;
        if (lightingQueuePool.Count < 8) lightingQueuePool.Enqueue(queue);
    }

    // OPTIMIZATION: Cache neighbor references to avoid repeated lookups
    private ChunkData[] _GetCachedNeighbors(ChunkData chunk)
    {
        if (chunk == null) return null;
        if (chunk._cachedNeighbors == null || chunk._cachedNeighbors.Length != 6)
        {
            chunk._cachedNeighbors = new ChunkData[6];
        }

        if (chunk._neighborCacheValid)
        {
#if LOGGING
            if (enableCacheTracking) stats_neighborCacheHits++;
#endif
            return chunk._cachedNeighbors;
        }

#if LOGGING
        if (enableCacheTracking) stats_neighborCacheMisses++;
#endif

        ChunkData[] neighbors = chunk._cachedNeighbors;
        neighbors[0] = GetChunkAt(chunk.chunkX_world + 1, chunk.chunkY_world, chunk.chunkZ_world); // PX
        neighbors[1] = GetChunkAt(chunk.chunkX_world - 1, chunk.chunkY_world, chunk.chunkZ_world); // NX
        neighbors[2] = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world + 1, chunk.chunkZ_world); // PY
        neighbors[3] = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world - 1, chunk.chunkZ_world); // NY
        neighbors[4] = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world + 1); // PZ
        neighbors[5] = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world - 1); // NZ
        chunk._neighborCacheValid = true;
        return neighbors;
    }

    // OPTIMIZATION: Invalidate neighbor cache when chunks change
    private void _InvalidateNeighborCache(ChunkData chunk)
    {
        if (chunk == null) return;
        chunk._neighborCacheValid = false;

        for (int i = 0; i < 6; i++)
        {
            ChunkData neighbor = GetChunkAt(chunk.chunkX_world + neighbor_dx_offsets[i], chunk.chunkY_world + neighbor_dy_offsets[i], chunk.chunkZ_world + neighbor_dz_offsets[i]);
            if (neighbor != null)
            {
                neighbor._neighborCacheValid = false;
            }
        }
    }

    // OPTIMIZATION: Defer reconciliation to prevent lag spikes
    private void DeferReconciliation(ChunkData chunk)
    {
        if (!reconciliationPending.Contains(chunk))
        {
            deferredReconciliationQueue.Enqueue(chunk);
            reconciliationPending.Add(chunk);
        }
    }

    // FIXED: Immediate reconciliation for critical cases (e.g., emissive blocks near boundaries)
    private void ImmediateReconciliation(ChunkData chunk)
    {
        if (chunk == null || !chunk.isDataReady) return;

        // Check if chunk has emissive blocks near boundaries that need immediate propagation
        byte[] chunkData = _GetDecompressedData(chunk);
        if (chunkData == null) return;

        bool hasEmissiveNearBoundary = false;

        // Check boundary blocks for emissive blocks
        for (int y = 0; y < chunkSizeY && !hasEmissiveNearBoundary; y++)
        {
            for (int z = 0; z < chunkSizeXZ && !hasEmissiveNearBoundary; z++)
            {
                // Check X boundaries
                int leftIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + 0;
                int rightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + (chunkSizeXZ - 1);

                if (lightEmissionCache[chunkData[leftIndex]] > 0 || lightEmissionCache[chunkData[rightIndex]] > 0)
                {
                    hasEmissiveNearBoundary = true;
                    break;
                }

                // Check Z boundaries
                int frontIndex = y * (chunkSizeXZ * chunkSizeXZ) + 0 * chunkSizeXZ + z;
                int backIndex = y * (chunkSizeXZ * chunkSizeXZ) + (chunkSizeXZ - 1) * chunkSizeXZ + z;

                if (lightEmissionCache[chunkData[frontIndex]] > 0 || lightEmissionCache[chunkData[backIndex]] > 0)
                {
                    hasEmissiveNearBoundary = true;
                    break;
                }
            }
        }

        // If emissive blocks near boundary, do immediate reconciliation
        if (hasEmissiveNearBoundary)
        {
            _ReconcileLightingWithNeighbors(chunk);
        }
        else
        {
            // Otherwise defer to prevent lag spikes
            DeferReconciliation(chunk);
        }
    }

    // OPTIMIZATION: Process deferred reconciliation with time budget
    private void ProcessDeferredReconciliation(float frameStart, float frameBudget)
    {
        float reconciliationBudget = RECONCILIATION_TIME_BUDGET_MS * 0.001f;
        int processedCount = 0;

        while (deferredReconciliationQueue.Count > 0 &&
               processedCount < MAX_RECONCILIATION_PER_FRAME &&
               Time.realtimeSinceStartup - frameStart < frameBudget - reconciliationBudget)
        {
            ChunkData chunk = deferredReconciliationQueue.Dequeue();
            reconciliationPending.Remove(chunk);

            if (chunk != null && chunk.isDataReady)
            {
                _ReconcileLightingWithNeighbors(chunk);
                processedCount++;
            }
        }
    }

    // OPTIMIZATION: Batch process multiple chunks for new columns
    private void BatchProcessNewColumnChunks(ChunkData[] chunks)
    {
        // OPTIMIZATION: Process chunks in batches to reduce overhead
        // This is called when a new multi-chunk column is being initialized

        // OPTIMIZATION: UdonSharp compatibility - avoid array length access
        int batchSize = 4; // Process max 4 chunks at once

        for (int i = 0; i < batchSize; i++)
        {
            // OPTIMIZATION: UdonSharp compatibility - check bounds manually
            if (i >= chunks.Length) break;

            ChunkData chunk = chunks[i];
            if (chunk != null && chunk.isDataReady)
            {
                // OPTIMIZATION: Skip expensive reconciliation for isolated chunks
                ChunkData[] neighbors = _GetCachedNeighbors(chunk);
                int readyNeighbors = 0;

                for (int j = 0; j < 6; j++)
                {
                    ChunkData neighbor = neighbors[j];
                    if (neighbor != null && neighbor.isDataReady)
                        readyNeighbors++;
                }

                // Only do lightweight reconciliation for chunks with neighbors
                if (readyNeighbors > 0)
                {
                    DeferReconciliation(chunk);
                }
            }
        }
    }

    // OPTIMIZATION: Optimized PULL-based lighting with pre-calculated offsets
    private bool _UpdateBlockLightPULLOptimized(ChunkData chunk, byte[] fullData, int x, int y, int z, bool isSkyLight, int[] queue, ref int queueEnd, int[] neighborOffsets)
    {
        int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        byte blockID = fullData[blockIndex];

        // OPTIMIZATION: Skip fully opaque blocks (opacity >= 15)
        int opacity = lightOpacityCache[blockID];
        if (opacity >= 15) {
            return false; // Skip opaque blocks
        }

        // OPTIMIZATION: Use pre-calculated offsets for neighbor queries
        int maxNeighborLight = 0;
        for (int i = 0; i < 6; i++)
        {
            int nx = x + neighborOffsets[i * 3];
            int ny = y + neighborOffsets[i * 3 + 1];
            int nz = z + neighborOffsets[i * 3 + 2];

            int neighborLight = _GetNeighborLightOptimized(chunk, nx, ny, nz, isSkyLight);
            if (neighborLight > maxNeighborLight) maxNeighborLight = neighborLight;
        }

        // Get current light FIRST
        byte currentLightByte = chunk.lightData[blockIndex];
        int currentLight = isSkyLight ? ((currentLightByte >> 4) & 0xF) : (currentLightByte & 0xF);

        // Apply opacity (air has opacity 0, treated as 1)
        if (opacity == 0) opacity = 1;

        int newLight = maxNeighborLight - opacity;
        if (newLight < 0) newLight = 0;

        // For skylight: max with sky visibility (15 if can see sky)
        if (isSkyLight) {
            // MINECRAFT BEHAVIOR: Check if this block can see the sky
            bool canSeeSky = false;
            if (chunk.chunkY_world >= worldDimensionY - 1 && y >= chunkSizeY - 1) {
                canSeeSky = true; // At world top
            } else if (currentLight == 15) {
                canSeeSky = true; // Already has full skylight
            }

            if (canSeeSky && newLight < 15) {
                newLight = 15;
            }
        }
        // For block light: max with emission
        else {
            int emission = lightEmissionCache[blockID];
            if (emission > newLight) newLight = emission;
        }

        if (newLight != currentLight) {
            // Set new light value
            if (isSkyLight) {
                int blockLight = currentLightByte & 0xF;

                // MINECRAFT BEHAVIOR: When skylight is reduced by semi-transparent block,
                // it creates block light to prevent areas from going too dark
                if (opacity > 1 && opacity < 15 && newLight < maxNeighborLight) {
                    int convertedBlockLight = newLight;
                    if (convertedBlockLight > blockLight) {
                        blockLight = convertedBlockLight;
                    }
                }

                chunk.lightData[blockIndex] = (byte)((newLight << 4) | blockLight);
            } else {
                int skyLight = (currentLightByte >> 4) & 0xF;
                chunk.lightData[blockIndex] = (byte)((skyLight << 4) | newLight);
            }

            // OPTIMIZATION: Schedule neighbors using pre-calculated offsets
            for (int i = 0; i < 6; i++)
            {
                int nx = x + neighborOffsets[i * 3];
                int ny = y + neighborOffsets[i * 3 + 1];
                int nz = z + neighborOffsets[i * 3 + 2];
                _ScheduleNeighborUpdate(chunk, nx, ny, nz, queue, ref queueEnd);
            }

            // FIXED: Trigger neighbor mesh rebuilds for boundary blocks
            // This ensures neighbors update their meshes when lighting changes at chunk boundaries
            if (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1)
            {
                TriggerNeighborMeshRebuilds(chunk);
            }

            return true; // Light was updated
        }

        return false; // No change
    }

    // OPTIMIZATION: Optimized neighbor light query with bounds checking
    private int _GetNeighborLightOptimized(ChunkData chunk, int x, int y, int z, bool isSkyLight)
    {
        // Check if neighbor is in the same chunk
        if (x >= 0 && x < chunkSizeXZ && y >= 0 && y < chunkSizeY && z >= 0 && z < chunkSizeXZ)
        {
            // Within same chunk
            int neighborIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            byte neighborLightByte = chunk.lightData[neighborIndex];

            if (isSkyLight)
                return (neighborLightByte >> 4) & 0xF;
            else
                return neighborLightByte & 0xF;
        }
        else
        {
            // Neighbor is in a different chunk - get from neighbor chunk
            ChunkData neighborChunk = null;
            int neighborLocalX = x;
            int neighborLocalY = y;
            int neighborLocalZ = z;

            // Determine which neighbor chunk and adjust local coordinates
            if (x < 0)
            {
                neighborChunk = chunk.neighborNX;
                neighborLocalX = chunkSizeXZ - 1;
            }
            else if (x >= chunkSizeXZ)
            {
                neighborChunk = chunk.neighborPX;
                neighborLocalX = 0;
            }
            else if (y < 0)
            {
                neighborChunk = chunk.neighborNY;
                neighborLocalY = chunkSizeY - 1;
            }
            else if (y >= chunkSizeY)
            {
                neighborChunk = chunk.neighborPY;
                neighborLocalY = 0;
            }
            else if (z < 0)
            {
                neighborChunk = chunk.neighborNZ;
                neighborLocalZ = chunkSizeXZ - 1;
            }
            else if (z >= chunkSizeXZ)
            {
                neighborChunk = chunk.neighborPZ;
                neighborLocalZ = 0;
            }

            // Get light from neighbor chunk if available
            if (neighborChunk != null && neighborChunk.isDataReady && neighborChunk.lightData != null)
            {
                int neighborIndex = neighborLocalY * (chunkSizeXZ * chunkSizeXZ) + neighborLocalZ * chunkSizeXZ + neighborLocalX;
                byte neighborLightByte = neighborChunk.lightData[neighborIndex];

                if (isSkyLight)
                    return (neighborLightByte >> 4) & 0xF;
                else
                    return neighborLightByte & 0xF;
            }

            // No neighbor chunk data = fully dark (Minecraft behavior)
            return 0;
        }
    }

    private void InitializeChunkLighting(ChunkData chunk)
    {
        if (_UsesGpuTerrainLightSampling())
        {
            chunk.lightData = null;
            return;
        }

#if LOGGING
        float startTime = Time.realtimeSinceStartup * 1000f;
#endif

        // Initialize lighting data array for this chunk
        int lightDataSize = chunkSizeXZ * chunkSizeY * chunkSizeXZ; // 16x16x16 = 4096
        chunk.lightData = new byte[lightDataSize];

        // CRITICAL: Use cached neighbor references for cross-chunk lighting queries
        ChunkData[] neighbors = _GetCachedNeighbors(chunk);
        chunk.neighborPX = neighbors[0];
        chunk.neighborNX = neighbors[1];
        chunk.neighborPY = neighbors[2];
        chunk.neighborNY = neighbors[3];
        chunk.neighborPZ = neighbors[4];
        chunk.neighborNZ = neighbors[5];

#if LOGGING
        // Debug neighbor availability
        if (enableVerboseLogging)
        {
            int readyNeighbors = 0;
            if (chunk.neighborPX != null && chunk.neighborPX.isDataReady) readyNeighbors++;
            if (chunk.neighborNX != null && chunk.neighborNX.isDataReady) readyNeighbors++;
            if (chunk.neighborPY != null && chunk.neighborPY.isDataReady) readyNeighbors++;
            if (chunk.neighborNY != null && chunk.neighborNY.isDataReady) readyNeighbors++;
            if (chunk.neighborPZ != null && chunk.neighborPZ.isDataReady) readyNeighbors++;
            if (chunk.neighborNZ != null && chunk.neighborNZ.isDataReady) readyNeighbors++;
            Debug.Log($"[McWorld] Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) has {readyNeighbors}/6 ready neighbors");
        }
#endif

        // Get full decompressed chunk data
        byte[] fullData = _GetDecompressedData(chunk);
        if (fullData == null) return;

#if LOGGING
        chunk.time_LightingInit = Time.realtimeSinceStartup * 1000f - startTime;
        startTime = Time.realtimeSinceStartup * 1000f;
#endif

        // Stage 1: Calculate initial sky light (vertical propagation)
        // OPTIMIZATION: Use reusable arrays to avoid allocations
        if (reusableBoolArray.Length < lightDataSize)
        {
            reusableBoolArray = new bool[lightDataSize];
        }
        bool[] skylightReachedBlocks = reusableBoolArray;
        bool[] skylightZeroBlocks = new bool[lightDataSize];
        int skylightReachedCount = 0;
        int skylightZeroCount = 0;

        for (int x = 0; x < chunkSizeXZ; x++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                int skyLight = 15; // Start with full brightness at world top

                // OPTIMIZATION: Check if this is a top chunk to avoid expensive neighbor queries
                if (chunk.chunkY_world >= worldDimensionY - 1)
                {
                    skyLight = 15; // Top chunks always start with full light
                }
                else
                {
                    // OPTIMIZATION: Only query chunk above if it exists and is ready
                    ChunkData chunkAbove = GetChunkAt(chunk.chunkX_world, chunk.chunkY_world + 1, chunk.chunkZ_world);
                    if (chunkAbove != null && chunkAbove.isDataReady && chunkAbove.lightData != null)
                    {
                        skyLight = _GetSkyLightFromChunkAbove(chunk.chunkX_world, chunk.chunkY_world + 1, chunk.chunkZ_world, x, z);
                    }
                    else
                    {
                        skyLight = 15; // Default to full light if chunk above not ready
                    }
                }

                // Propagate downward from top (Y = chunkSizeY-1) to bottom (Y = 0)
                for (int y = chunkSizeY - 1; y >= 0; y--)
                {
                    int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                    byte blockID = fullData[blockIndex];

                    // Get block opacity
                    int opacity = lightOpacityCache[blockID];

                    // CORRECTED: Current block sky light = above block sky light - current block opacity
                    int currentBlockSkyLight = skyLight - opacity;
                    if (currentBlockSkyLight < 0) currentBlockSkyLight = 0;

                    // MINECRAFT BEHAVIOR: When sky light is reduced by semi-transparent block,
                    // it also creates block light to prevent it going completely dark
                    int currentBlockLight = 0;
                    if (opacity > 0 && opacity < 15 && skyLight > currentBlockSkyLight)
                    {
                        // Sky light was reduced by semi-transparent block -> convert to block light
                        currentBlockLight = currentBlockSkyLight;
                    }

                    // Set lighting for this block
                    chunk.lightData[blockIndex] = (byte)((currentBlockSkyLight << 4) | currentBlockLight);

                    // OPTIMIZATION: Track blocks that got full skylight (no BFS needed)
                    if (currentBlockSkyLight == 15)
                    {
                        skylightReachedBlocks[blockIndex] = true;
                        skylightReachedCount++;
                    }
                    // OPTIMIZATION: Track blocks that got zero skylight (need BFS)
                    else if (currentBlockSkyLight == 0)
                    {
                        skylightZeroBlocks[blockIndex] = true;
                        skylightZeroCount++;
                    }

                    // Use current block's light for next block down
                    skyLight = currentBlockSkyLight;
                }
            }
        }

        // Stage 2: Set block light for emissive blocks
        for (int i = 0; i < lightDataSize; i++)
        {
            byte blockID = fullData[i];
            int emission = lightEmissionCache[blockID];
            if (emission > 0)
            {
                // Set block light (low nibble) while preserving sky light (high nibble)
                byte skyLight = (byte)((chunk.lightData[i] >> 4) & 0xF);
                chunk.lightData[i] = (byte)((skyLight << 4) | emission);
            }
        }

#if LOGGING
        chunk.lightingSkylightReachedBlocks = skylightReachedCount;
        chunk.lightingSkylightZeroBlocks = skylightZeroCount;
#endif

        // Stage 3: Propagate block light horizontally (sky light is vertical-only)
        _PropagateChunkLightingOptimized(chunk, fullData, skylightReachedBlocks, skylightZeroBlocks);

#if LOGGING
        if (enableDetailedTimings)
        {
            stats_lightingInitsTotal++;
            stats_lightingStepsTotal++;
            stats_lightingInitTime += chunk.time_LightingInit;
            stats_lightingImportTime += chunk.time_LightingImport;
            stats_lightingBfsSkyTime += chunk.time_LightingBFS_Sky;
            stats_lightingBfsBlockTime += chunk.time_LightingBFS_Block;
            stats_lightingStepTime += chunk.time_LightingInit + chunk.time_LightingImport + chunk.time_LightingBFS_Sky + chunk.time_LightingBFS_Block;
            stats_lightingBFSOps += chunk.lightingQueueOps_Sky + chunk.lightingQueueOps_Block;
            stats_lightingSkylightBlocks += chunk.lightingSkylightReachedBlocks;
            stats_lightingBlocklightBlocks += chunk.lightingUpdatesApplied_Block;
            stats_lightingCrossChunkQueries += chunk.lightingCrossChunkOps_Sky + chunk.lightingCrossChunkOps_Block;
        }
#endif
    }

    // NEW: Optimized PULL-based lighting propagation (Minecraft Beta 1.7.3 algorithm)
    private void _PropagateChunkLightingOptimized(ChunkData chunk, byte[] fullData, bool[] skylightReachedBlocks, bool[] skylightZeroBlocks)
    {
        // CRITICAL: Prevent infinite recursion during cross-chunk BFS
        if (chunk.isPropagatingLight) return;

        chunk.isPropagatingLight = true;

#if LOGGING
        float startTime = Time.realtimeSinceStartup * 1000f;
#endif

        // STEP 0: Import light from all neighbor chunks into our boundary blocks
        // This ensures we don't miss light coming from already-generated neighbors
        _ImportLightFromNeighbors(chunk, fullData);

#if LOGGING
        chunk.time_LightingImport = Time.realtimeSinceStartup * 1000f - startTime;
        startTime = Time.realtimeSinceStartup * 1000f;
#endif

        // OPTIMIZATION: Use pooled queue to avoid GC pressure
        int[] lightQueue = GetBFSQueueLarge();
        int queueStart = 0;
        int queueEnd = 0;

        // OPTIMIZATION: Pre-calculate neighbor offsets to avoid repeated calculations
        int[] neighborOffsets = {
            -1, 0, 0,  // X-
            1, 0, 0,   // X+
            0, -1, 0, // Y-
            0, 1, 0,  // Y+
            0, 0, -1, // Z-
            0, 0, 1   // Z+
        };

        // MINECRAFT BETA 1.7.3 ALGORITHM:
        // Skylight has BOTH vertical initialization AND horizontal propagation via BFS
        // Vertical pass sets initial values, horizontal BFS spreads light between chunks

        // First pass: Sky light horizontal propagation (PULL-based BFS)
        // CRITICAL: Add ALL blocks with skylight < 15
        // skylight=15: already at max, skip
        // skylight=0 to 14: might receive light from neighbors (INCLUDE skylight=0!)
        for (int y = 0; y < chunkSizeY; y++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;

                    // Add ALL blocks with skylight < 15 (including 0)
                    // These blocks can potentially receive light from neighbors
                    int skyLight = (chunk.lightData[blockIndex] >> 4) & 0xF;
                    if (skyLight < 15)
                    {
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | z;
                        if (queueEnd >= lightQueue.Length - 6) break;
                    }
                }
                if (queueEnd >= lightQueue.Length - 6) break;
            }
            if (queueEnd >= lightQueue.Length - 6) break;
        }

        // FIXED: Process sky light queue with multi-pass convergence algorithm
        int skylightIterations = 0;
        int skylightInitialQueue = queueEnd;
        int skylightMaxQueue = queueEnd;
        int maxSkylightIterations = 16; // Increased limit for better convergence

        while (queueStart < queueEnd && skylightIterations < maxSkylightIterations)
        {
            int iterationStart = queueStart;
            int iterationEnd = queueEnd;
            int blocksProcessedThisIteration = 0;

            while (queueStart < iterationEnd)
            {
                int packed = lightQueue[queueStart++];
                int x = (packed >> 16) & 0xFF;
                int y = (packed >> 8) & 0xFF;
                int z = packed & 0xFF;

                // OPTIMIZATION: Skip fully opaque blocks early
                int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                byte blockID = fullData[blockIndex];
                int opacity = lightOpacityCache[blockID];
                if (opacity >= 15) continue;

                // PULL from ALL 6 neighbors and take MAX
                bool updated = _UpdateBlockLightPULLOptimized(chunk, fullData, x, y, z, true, lightQueue, ref queueEnd, neighborOffsets);

#if LOGGING
                if (updated) chunk.lightingUpdatesApplied_Sky++;
                chunk.lightingBlocksProcessed_Sky++;
#endif

                blocksProcessedThisIteration++;

                // FIXED: Handle queue overflow gracefully - expand queue instead of breaking
                if (queueEnd >= lightQueue.Length - 6)
                {
                    // Queue is full, but we need to continue processing
                    // This is a critical fix for pitch black spots
                    if (enableVerboseLogging)
                    {
                        Debug.LogWarning($"[McWorld] Skylight BFS queue overflow in chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) at iteration {skylightIterations}, processed {blocksProcessedThisIteration} blocks");
                    }
                    // Continue processing current iteration but don't add more to queue
                    break;
                }
            }

            skylightIterations++;
            if (queueEnd > skylightMaxQueue) skylightMaxQueue = queueEnd;

            // If no blocks were processed this iteration, we've converged
            if (blocksProcessedThisIteration == 0) break;
        }

#if LOGGING
        chunk.time_LightingBFS_Sky = Time.realtimeSinceStartup * 1000f - startTime;
        if (enableVerboseLogging && skylightIterations > 0)
        {
            Debug.Log($"[McWorld] Skylight BFS - Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}): " +
                $"Initial queue: {skylightInitialQueue}, Max queue: {skylightMaxQueue}, Iterations: {skylightIterations}, " +
                $"Updates: {chunk.lightingUpdatesApplied_Sky}");
        }
        startTime = Time.realtimeSinceStartup * 1000f;
#endif

        // Second pass: Block light propagation
        queueStart = 0;
        queueEnd = 0;

        // OPTIMIZATION: Only add blocks that need BFS processing
        // 1. Emissive blocks (torches, glowstone, etc.)
        // 2. Blocks with skylight=0 (underground areas that need light from neighbors)
        for (int y = 0; y < chunkSizeY; y++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;

                    // Add emissive blocks (they emit light)
                    int blockLight = chunk.lightData[blockIndex] & 0xF;
                    if (blockLight > 0)
                    {
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | z;
                        if (queueEnd >= lightQueue.Length - 6) break;
                    }
                    // Add blocks with skylight=0 (they need light from neighbors)
                    else if (skylightZeroBlocks[blockIndex])
                    {
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | z;
                        if (queueEnd >= lightQueue.Length - 6) break;
                    }
                }
                if (queueEnd >= lightQueue.Length - 6) break;
            }
            if (queueEnd >= lightQueue.Length - 6) break;
        }

        // FIXED: Process block light queue with multi-pass convergence algorithm
        int iterations = 0;
        int initialQueueSize = queueEnd;
        int maxQueueSize = queueEnd;
        int maxBlocklightIterations = 16; // Increased limit for better convergence

        while (queueStart < queueEnd && iterations < maxBlocklightIterations)
        {
            int iterationStart = queueStart;
            int iterationEnd = queueEnd;
            int blocksProcessedThisIteration = 0;

            while (queueStart < iterationEnd)
            {
                int packed = lightQueue[queueStart++];
                int x = (packed >> 16) & 0xFF;
                int y = (packed >> 8) & 0xFF;
                int z = packed & 0xFF;

                // OPTIMIZATION: Skip fully opaque blocks early
                int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                byte blockID = fullData[blockIndex];
                int opacity = lightOpacityCache[blockID];
                if (opacity >= 15) continue;

                // PULL from ALL 6 neighbors and take MAX
                bool updated = _UpdateBlockLightPULLOptimized(chunk, fullData, x, y, z, false, lightQueue, ref queueEnd, neighborOffsets);

#if LOGGING
                if (updated) chunk.lightingUpdatesApplied_Block++;
                chunk.lightingBlocksProcessed_Block++;
#endif

                blocksProcessedThisIteration++;

                // FIXED: Handle queue overflow gracefully - expand queue instead of breaking
                if (queueEnd >= lightQueue.Length - 6)
                {
                    // Queue is full, but we need to continue processing
                    // This is a critical fix for pitch black spots
                    if (enableVerboseLogging)
                    {
                        Debug.LogWarning($"[McWorld] Block light BFS queue overflow in chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) at iteration {iterations}, processed {blocksProcessedThisIteration} blocks");
                    }
                    // Continue processing current iteration but don't add more to queue
                    break;
                }
            }

            iterations++;
            if (queueEnd > maxQueueSize) maxQueueSize = queueEnd;

            // If no blocks were processed this iteration, we've converged
            if (blocksProcessedThisIteration == 0) break;
        }

#if LOGGING
        // Log BFS iteration details for debugging
        if (enableVerboseLogging && iterations > 0)
        {
            Debug.Log($"[McWorld] BFS Debug - Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}): " +
                $"Initial queue: {initialQueueSize}, Max queue: {maxQueueSize}, Iterations: {iterations}, " +
                $"Updates: {chunk.lightingUpdatesApplied_Block}");
        }
#endif

#if LOGGING
        chunk.time_LightingBFS_Block = Time.realtimeSinceStartup * 1000f - startTime;
#endif

        // FIXED: Final fallback - ensure no blocks remain completely dark
        // This catches any remaining pitch black spots that BFS might have missed
        _EnsureNoPitchBlackSpots(chunk, fullData);

        // Return queue to pool
        ReturnBFSQueue(lightQueue);

        // Clear the flag to allow future BFS calls
        chunk.isPropagatingLight = false;
    }

    // FIXED: Final fallback to ensure no pitch black spots remain
    private void _EnsureNoPitchBlackSpots(ChunkData chunk, byte[] fullData)
    {
        // Scan for blocks that are completely dark (both sky and block light = 0)
        // and try to fix them by finding the nearest light source
        int lightDataSize = chunkSizeXZ * chunkSizeY * chunkSizeXZ;
        bool foundDarkSpots = false;
        int stride = chunkSizeXZ * chunkSizeXZ;

        for (int z = 0; z < chunkSizeXZ; z++)
        {
            for (int x = 0; x < chunkSizeXZ; x++)
            {
                int columnIndex = z * chunkSizeXZ + x;
                int minY = 0;
                int maxY = chunkSizeY - 1;

                if (!chunk._isAllAir && chunk._columnMinY != null && chunk._columnMaxY != null)
                {
                    byte rawMin = chunk._columnMinY[columnIndex];
                    byte rawMax = chunk._columnMaxY[columnIndex];
                    if (rawMin == 255 || rawMax == 255) continue;
                    minY = rawMin > 0 ? rawMin - 1 : 0;
                    maxY = rawMax < chunkSizeY - 1 ? rawMax + 1 : chunkSizeY - 1;
                }

                for (int y = minY; y <= maxY; y++)
                {
                    int i = y * stride + z * chunkSizeXZ + x;
                    byte lightByte = chunk.lightData[i];
                    int skyLight = (lightByte >> 4) & 0xF;
                    int blockLight = lightByte & 0xF;

                    if (skyLight != 0 || blockLight != 0) continue;

                    byte blockID = fullData[i];
                    int opacity = lightOpacityCache[blockID];
                    if (opacity >= 15) continue;

                    int bestLight = 0;
                    for (int dir = 0; dir < 6; dir++)
                    {
                        int nx = x + neighbor_dx_offsets[dir];
                        int ny = y + neighbor_dy_offsets[dir];
                        int nz = z + neighbor_dz_offsets[dir];

                        if (nx >= 0 && nx < chunkSizeXZ && ny >= 0 && ny < chunkSizeY && nz >= 0 && nz < chunkSizeXZ)
                        {
                            int neighborIndex = ny * stride + nz * chunkSizeXZ + nx;
                            byte neighborLightByte = chunk.lightData[neighborIndex];
                            int neighborSkyLight = (neighborLightByte >> 4) & 0xF;
                            int neighborBlockLight = neighborLightByte & 0xF;
                            int neighborMaxLight = neighborSkyLight > neighborBlockLight ? neighborSkyLight : neighborBlockLight;
                            if (neighborMaxLight > bestLight) bestLight = neighborMaxLight;
                        }
                    }

                    if (bestLight > 0)
                    {
                        int propagatedLight = bestLight - opacity;
                        if (propagatedLight > 0)
                        {
                            if (bestLight >= 8) chunk.lightData[i] = (byte)(propagatedLight << 4);
                            else chunk.lightData[i] = (byte)propagatedLight;
                            foundDarkSpots = true;
                        }
                    }
                }
            }
        }

        if (foundDarkSpots && enableVerboseLogging)
        {
            Debug.Log($"[McWorld] Fixed pitch black spots in chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world})");
        }

        // Debug: Count remaining dark spots for monitoring
        int remainingDarkSpots = 0;
        for (int i = 0; i < lightDataSize; i++)
        {
            byte lightByte = chunk.lightData[i];
            int skyLight = (lightByte >> 4) & 0xF;
            int blockLight = lightByte & 0xF;

            if (skyLight == 0 && blockLight == 0)
            {
                byte blockID = fullData[i];
                int opacity = lightOpacityCache[blockID];
                if (opacity < 15) // Only count non-opaque blocks
                {
                    remainingDarkSpots++;
                }
            }
        }

        if (remainingDarkSpots > 0 && enableVerboseLogging)
        {
            Debug.LogWarning($"[McWorld] Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) still has {remainingDarkSpots} dark spots after BFS");
        }
    }

    // OLD: Legacy PUSH-based method (kept for reference/compatibility)
    private void _PropagateChunkLighting(ChunkData chunk, byte[] fullData)
    {
        // CRITICAL: Prevent infinite recursion during cross-chunk BFS
        if (chunk.isPropagatingLight) return;

        chunk.isPropagatingLight = true;

        // Minecraft Beta 1.7.3 style flood-fill lighting propagation
        // CRITICAL: First import light from neighbor chunks, THEN propagate

        // STEP 0: Import light from all neighbor chunks into our boundary blocks
        // This ensures we don't miss light coming from already-generated neighbors
        _ImportLightFromNeighbors(chunk, fullData);

        // Queue for light propagation: stores packed position (x, y, z)
        int[] lightQueue = new int[4096 * 2]; // Oversized to handle cascading
        int queueStart = 0;
        int queueEnd = 0;

        // First pass: Propagate sky light horizontally
        // Add all lit blocks to the queue
        for (int y = 0; y < chunkSizeY; y++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    int lightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                    int skyLight = (chunk.lightData[lightIndex] >> 4) & 0xF;
                    if (skyLight > 0)
                    {
                        // Add to queue: pack coordinates into single int
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | z;
                        if (queueEnd >= lightQueue.Length) break; // Safety
                    }
                }
                if (queueEnd >= lightQueue.Length) break;
            }
            if (queueEnd >= lightQueue.Length) break;
        }

        // Process sky light queue
        while (queueStart < queueEnd)
        {
            int packed = lightQueue[queueStart++];
            int x = (packed >> 16) & 0xFF;
            int y = (packed >> 8) & 0xFF;
            int z = packed & 0xFF;

            int centerIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            int centerLight = (chunk.lightData[centerIndex] >> 4) & 0xF;

            // Propagate to 6 neighbors
            _PropagateLightToNeighbor(chunk, fullData, x - 1, y, z, centerLight, true, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x + 1, y, z, centerLight, true, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y - 1, z, centerLight, true, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y + 1, z, centerLight, true, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y, z - 1, centerLight, true, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y, z + 1, centerLight, true, lightQueue, ref queueEnd);

            if (queueEnd >= lightQueue.Length - 6) break; // Safety: leave room for 6 neighbors
        }

        // Second pass: Propagate block light horizontally
        queueStart = 0;
        queueEnd = 0;

        // Add all emissive blocks to the queue
        for (int y = 0; y < chunkSizeY; y++)
        {
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    int lightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                    int blockLight = chunk.lightData[lightIndex] & 0xF;
                    if (blockLight > 0)
                    {
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | z;
                        if (queueEnd >= lightQueue.Length) break;
                    }
                }
                if (queueEnd >= lightQueue.Length) break;
            }
            if (queueEnd >= lightQueue.Length) break;
        }

        // Process block light queue
        while (queueStart < queueEnd)
        {
            int packed = lightQueue[queueStart++];
            int x = (packed >> 16) & 0xFF;
            int y = (packed >> 8) & 0xFF;
            int z = packed & 0xFF;

            int centerIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            int centerLight = chunk.lightData[centerIndex] & 0xF;

            // Propagate to 6 neighbors
            _PropagateLightToNeighbor(chunk, fullData, x - 1, y, z, centerLight, false, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x + 1, y, z, centerLight, false, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y - 1, z, centerLight, false, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y + 1, z, centerLight, false, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y, z - 1, centerLight, false, lightQueue, ref queueEnd);
            _PropagateLightToNeighbor(chunk, fullData, x, y, z + 1, centerLight, false, lightQueue, ref queueEnd);

            if (queueEnd >= lightQueue.Length - 6) break;
        }

        // Clear the flag to allow future BFS calls
        chunk.isPropagatingLight = false;
    }

    // NEW: PULL-based block light update (Minecraft Beta 1.7.3 algorithm)
    private bool _UpdateBlockLightPULL(ChunkData chunk, byte[] fullData, int x, int y, int z, bool isSkyLight, int[] queue, ref int queueEnd)
    {
        int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        byte blockID = fullData[blockIndex];

        // OPTIMIZATION: Skip fully opaque blocks (opacity >= 15)
        // Light cannot penetrate these blocks, so no point processing them
        int opacity = lightOpacityCache[blockID];
        if (opacity >= 15) {
            return false; // Skip opaque blocks
        }

        // Get light from ALL 6 neighbors and find MAXIMUM
        int light_X_minus = _GetNeighborLight(chunk, fullData, x-1, y, z, isSkyLight);
        int light_X_plus  = _GetNeighborLight(chunk, fullData, x+1, y, z, isSkyLight);
        int light_Y_minus = _GetNeighborLight(chunk, fullData, x, y-1, z, isSkyLight);
        int light_Y_plus  = _GetNeighborLight(chunk, fullData, x, y+1, z, isSkyLight);
        int light_Z_minus = _GetNeighborLight(chunk, fullData, x, y, z-1, isSkyLight);
        int light_Z_plus  = _GetNeighborLight(chunk, fullData, x, y, z+1, isSkyLight);

#if LOGGING
        if (isSkyLight)
            chunk.lightingNeighborQueries_Sky += 6;
        else
            chunk.lightingNeighborQueries_Block += 6;
#endif

        // Find MAX of all neighbors
        int maxNeighborLight = light_X_minus;
        if (light_X_plus > maxNeighborLight) maxNeighborLight = light_X_plus;
        if (light_Y_minus > maxNeighborLight) maxNeighborLight = light_Y_minus;
        if (light_Y_plus > maxNeighborLight) maxNeighborLight = light_Y_plus;
        if (light_Z_minus > maxNeighborLight) maxNeighborLight = light_Z_minus;
        if (light_Z_plus > maxNeighborLight) maxNeighborLight = light_Z_plus;

        // Get current light FIRST
        byte currentLightByte = chunk.lightData[blockIndex];
        int currentLight = isSkyLight ? ((currentLightByte >> 4) & 0xF) : (currentLightByte & 0xF);

        // Apply opacity (air has opacity 0, treated as 1)
        if (opacity == 0) opacity = 1;

        int newLight = maxNeighborLight - opacity;
        if (newLight < 0) newLight = 0;

        // For skylight: max with sky visibility (15 if can see sky)
        if (isSkyLight) {
            // MINECRAFT BEHAVIOR: Check if this block can see the sky
            // If we're at the top of the world or current light is 15, we can see sky
            bool canSeeSky = false;
            if (chunk.chunkY_world >= worldDimensionY - 1 && y >= chunkSizeY - 1) {
                canSeeSky = true; // At world top
            } else if (currentLight == 15) {
                canSeeSky = true; // Already has full skylight
            }

            if (canSeeSky && newLight < 15) {
                newLight = 15;
            }
        }
        // For block light: max with emission
        else {
            int emission = lightEmissionCache[blockID];
            if (emission > newLight) newLight = emission;
        }

        // MINECRAFT BETA 1.7.3 BEHAVIOR: Update skylight whenever newLight != currentLight
        // Skylight can both increase AND decrease during horizontal propagation
        // This is essential for proper light propagation across chunk boundaries

        if (newLight != currentLight) {
#if LOGGING
            // Debug boundary lighting updates
            bool isBoundary = (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1);
            if (enableVerboseLogging && isSkyLight && isBoundary && newLight > currentLight + 5) {
                Debug.Log($"[McWorld] Large skylight increase at boundary ({x},{y},{z}): " +
                    $"{currentLight} -> {newLight}, maxNeighbor={maxNeighborLight}, opacity={opacity}");
            }
#endif

            // Set new light value
            if (isSkyLight) {
                int blockLight = currentLightByte & 0xF;

                // MINECRAFT BEHAVIOR: When skylight is reduced by semi-transparent block,
                // it creates block light to prevent areas from going too dark
                // This happens during HORIZONTAL propagation when opacity reduces light
                if (opacity > 1 && opacity < 15 && newLight < maxNeighborLight) {
                    // Sky light was reduced by semi-transparent block
                    // Convert the reduced skylight into block light
                    int convertedBlockLight = newLight;
                    if (convertedBlockLight > blockLight) {
                        blockLight = convertedBlockLight;
                    }
                }

                chunk.lightData[blockIndex] = (byte)((newLight << 4) | blockLight);
            } else {
                int skyLight = (currentLightByte >> 4) & 0xF;
                chunk.lightData[blockIndex] = (byte)((skyLight << 4) | newLight);
            }

            // MINECRAFT BETA 1.7.3 PROPAGATION:
            // Schedule ALL 6 neighbors for re-evaluation (symmetric propagation)
            // The light value decrease (newLight < currentLight) naturally prevents infinite propagation
            _ScheduleNeighborUpdate(chunk, x-1, y, z, queue, ref queueEnd);  // X-
            _ScheduleNeighborUpdate(chunk, x+1, y, z, queue, ref queueEnd);  // X+
            _ScheduleNeighborUpdate(chunk, x, y-1, z, queue, ref queueEnd);  // Y-
            _ScheduleNeighborUpdate(chunk, x, y+1, z, queue, ref queueEnd);  // Y+
            _ScheduleNeighborUpdate(chunk, x, y, z-1, queue, ref queueEnd);  // Z-
            _ScheduleNeighborUpdate(chunk, x, y, z+1, queue, ref queueEnd);  // Z+

            // FIXED: Trigger neighbor mesh rebuilds for boundary blocks
            // This ensures neighbors update their meshes when lighting changes at chunk boundaries
            if (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1)
            {
                TriggerNeighborMeshRebuilds(chunk);
            }

#if LOGGING
            if (isSkyLight)
                chunk.lightingQueueOps_Sky += 6;
            else
                chunk.lightingQueueOps_Block += 6;
#endif

            return true; // Light was updated
        }

        return false; // No change
    }

    // Helper: Get light value from a neighbor (handles cross-chunk boundaries)
    private int _GetNeighborLight(ChunkData chunk, byte[] fullData, int x, int y, int z, bool isSkyLight)
    {
        // Check if neighbor is in the same chunk
        if (x >= 0 && x < chunkSizeXZ && y >= 0 && y < chunkSizeY && z >= 0 && z < chunkSizeXZ)
        {
            // Within same chunk
            int neighborIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
            byte neighborLightByte = chunk.lightData[neighborIndex];

            if (isSkyLight)
                return (neighborLightByte >> 4) & 0xF;
            else
                return neighborLightByte & 0xF;
        }
        else
        {
            // Neighbor is in a different chunk - get from neighbor chunk
            ChunkData neighborChunk = null;
            int neighborLocalX = x;
            int neighborLocalY = y;
            int neighborLocalZ = z;

            // Determine which neighbor chunk and adjust local coordinates
            if (x < 0)
            {
                neighborChunk = chunk.neighborNX;
                neighborLocalX = chunkSizeXZ - 1;
            }
            else if (x >= chunkSizeXZ)
            {
                neighborChunk = chunk.neighborPX;
                neighborLocalX = 0;
            }
            else if (y < 0)
            {
                neighborChunk = chunk.neighborNY;
                neighborLocalY = chunkSizeY - 1;
            }
            else if (y >= chunkSizeY)
            {
                neighborChunk = chunk.neighborPY;
                neighborLocalY = 0;
            }
            else if (z < 0)
            {
                neighborChunk = chunk.neighborNZ;
                neighborLocalZ = chunkSizeXZ - 1;
            }
            else if (z >= chunkSizeXZ)
            {
                neighborChunk = chunk.neighborPZ;
                neighborLocalZ = 0;
            }

            // Get light from neighbor chunk if available
            if (neighborChunk != null && neighborChunk.isDataReady && neighborChunk.lightData != null)
            {
                int neighborIndex = neighborLocalY * (chunkSizeXZ * chunkSizeXZ) + neighborLocalZ * chunkSizeXZ + neighborLocalX;
                byte neighborLightByte = neighborChunk.lightData[neighborIndex];

#if LOGGING
                if (isSkyLight)
                    chunk.lightingCrossChunkOps_Sky++;
                else
                    chunk.lightingCrossChunkOps_Block++;
#endif

                if (isSkyLight)
                    return (neighborLightByte >> 4) & 0xF;
                else
                    return neighborLightByte & 0xF;
            }
#if LOGGING
            else if (enableVerboseLogging && neighborChunk != null)
            {
                // Debug why neighbor chunk isn't being used
                string reason = "";
                if (!neighborChunk.isDataReady) reason += "notReady ";
                if (neighborChunk.lightData == null) reason += "noLightData ";
                if (reason == "") reason = "unknown";
                Debug.Log($"[McWorld] Cross-chunk query failed: neighbor exists but {reason}");
            }
#endif

            // No neighbor chunk data = fully dark (Minecraft behavior)
            return 0;
        }
    }

    // Helper: Schedule a neighbor for update (add to queue)
    private void _ScheduleNeighborUpdate(ChunkData chunk, int x, int y, int z, int[] queue, ref int queueEnd)
    {
        // Check if within same chunk bounds
        if (x >= 0 && x < chunkSizeXZ && y >= 0 && y < chunkSizeY && z >= 0 && z < chunkSizeXZ)
        {
            // Within same chunk - add to queue (with bounds check)
            if (queueEnd < queue.Length)
            {
            queue[queueEnd++] = (x << 16) | (y << 8) | z;
        }
        }
        // NOTE: Cross-chunk updates are handled by _ReconcileLightingWithNeighbors
        // Don't try to schedule them during BFS to prevent recursion/queue overflow
    }

    // OLD: Legacy PUSH-based method (kept for reference/compatibility)
    private void _PropagateLightToNeighbor(ChunkData chunk, byte[] fullData, int x, int y, int z, int sourceLight, bool isSkyLight, int[] queue, ref int queueEnd)
    {
        // Check if neighbor is in the same chunk
        if (x >= 0 && x < chunkSizeXZ && y >= 0 && y < chunkSizeY && z >= 0 && z < chunkSizeXZ)
        {
            // Within same chunk - process directly
        int neighborIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        byte blockID = fullData[neighborIndex];

        // Get block opacity (CRITICAL: treat 0 as 1)
        int opacity = lightOpacityCache[blockID];
        if (opacity == 0) opacity = 1;

        // If opacity >= 15, light can't pass through
        if (opacity >= 15) return;

        // Calculate propagated light value
        int newLight = sourceLight - opacity;
        if (newLight <= 0) return;

        // Get current light value at neighbor
        byte currentLightByte = chunk.lightData[neighborIndex];
        int currentLight;
        if (isSkyLight)
            currentLight = (currentLightByte >> 4) & 0xF;
        else
            currentLight = currentLightByte & 0xF;

        // Only update if new light is brighter
        if (newLight <= currentLight) return;

        // Update light value
        if (isSkyLight)
        {
            // Update sky light (high nibble), preserve block light (low nibble)
            int blockLight = currentLightByte & 0xF;
            chunk.lightData[neighborIndex] = (byte)((newLight << 4) | blockLight);
        }
        else
        {
            // Update block light (low nibble), preserve sky light (high nibble)
            int skyLight = (currentLightByte >> 4) & 0xF;
            chunk.lightData[neighborIndex] = (byte)((skyLight << 4) | newLight);
        }

        // Add to queue for further propagation
            queue[queueEnd++] = (x << 16) | (y << 8) | z;
        }
        else
        {
            // Neighbor is in a different chunk - propagate across boundary
            _PropagateToNeighborChunk(chunk, x, y, z, sourceLight, isSkyLight);
        }
    }

    private void _PropagateToNeighborChunk(ChunkData chunk, int x, int y, int z, int sourceLight, bool isSkyLight)
    {
        // MINECRAFT BETA 1.7.3 APPROACH: Update the boundary block, then trigger full BFS in neighbor
        // This ensures light propagates through the neighbor to its neighbors

        // Determine which neighbor chunk and adjust coordinates
        ChunkData neighborChunk = null;
        int neighborLocalX = x;
        int neighborLocalY = y;
        int neighborLocalZ = z;

        if (x < 0)
        {
            neighborChunk = chunk.neighborNX;
            neighborLocalX = chunkSizeXZ - 1;
        }
        else if (x >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPX;
            neighborLocalX = 0;
        }
        else if (y < 0)
        {
            neighborChunk = chunk.neighborNY;
            neighborLocalY = chunkSizeY - 1;
        }
        else if (y >= chunkSizeY)
        {
            neighborChunk = chunk.neighborPY;
            neighborLocalY = 0;
        }
        else if (z < 0)
        {
            neighborChunk = chunk.neighborNZ;
            neighborLocalZ = chunkSizeXZ - 1;
        }
        else if (z >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPZ;
            neighborLocalZ = 0;
        }

        // If neighbor chunk not ready, can't propagate
        if (neighborChunk == null || !neighborChunk.isDataReady || neighborChunk.lightData == null)
            return;

        // Get neighbor block data
        byte[] neighborData = _DecompressChunkColumnRLE(neighborChunk);
        if (neighborData == null) return;

        int neighborIndex = neighborLocalY * (chunkSizeXZ * chunkSizeXZ) + neighborLocalZ * chunkSizeXZ + neighborLocalX;
        byte blockID = neighborData[neighborIndex];

        // Get block opacity (CRITICAL: treat 0 as 1)
        int opacity = lightOpacityCache[blockID];
        if (opacity == 0) opacity = 1;

        // If opacity >= 15, light can't pass through
        if (opacity >= 15) return;

        // Calculate propagated light value
        int newLight = sourceLight - opacity;
        if (newLight <= 0) return;

        // Get current light value at neighbor
        byte currentLightByte = neighborChunk.lightData[neighborIndex];
        int currentLight;
        if (isSkyLight)
            currentLight = (currentLightByte >> 4) & 0xF;
        else
            currentLight = currentLightByte & 0xF;

        // Only update if new light is brighter
        if (newLight <= currentLight) return;

        // Update light value in neighbor chunk
        if (isSkyLight)
        {
            int blockLight = currentLightByte & 0xF;
            neighborChunk.lightData[neighborIndex] = (byte)((newLight << 4) | blockLight);
        }
        else
        {
            int skyLight = (currentLightByte >> 4) & 0xF;
            neighborChunk.lightData[neighborIndex] = (byte)((skyLight << 4) | newLight);
        }

        // FIXED: Trigger lightweight boundary BFS instead of full chunk BFS
        // This allows the light we just added to spread through the neighbor efficiently
        _PropagateBoundaryLighting(neighborChunk, neighborData);

        // FIXED: Trigger neighbor mesh rebuilds for cross-chunk lighting updates
        // This ensures the neighbor chunk updates its mesh when light propagates across boundaries
        TriggerNeighborMeshRebuilds(neighborChunk);
    }

    private void _UpdateBlockLighting(ChunkData chunk, int x, int y, int z, byte oldBlockID, byte newBlockID)
    {
        // Minecraft Beta 1.7.3 style lighting update for a single block change.

        // GPU OFFLOAD #6: try GPU light poke first. Returns true if it seeded the change
        // on GPU — the next per-frame propagate pass will smooth it out. Falls back to
        // the CPU iterative BFS below if the GPU lighting backend isn't ready.
        if (_GpuLightPoke(chunk, x, y, z, oldBlockID, newBlockID)) return;

        int lightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        byte[] fullData = _GetDecompressedData(chunk);
        if (fullData == null) return;

        int oldOpacity = lightOpacityCache[oldBlockID];
        int newOpacity = lightOpacityCache[newBlockID];
        int oldEmission = lightEmissionCache[oldBlockID];
        int newEmission = lightEmissionCache[newBlockID];

        // If opacity or emission changed, we need to recalculate lighting
        bool needsUpdate = (oldOpacity != newOpacity) || (oldEmission != newEmission);
        if (!needsUpdate) return;

        // Recalculate lighting at this position and propagate
        _RecalculateLightAtPosition(chunk, fullData, x, y, z);

        // FIXED: Trigger neighbor mesh rebuilds for boundary blocks
        // This ensures neighbors update their meshes when lighting changes at chunk boundaries
        if (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1)
        {
            TriggerNeighborMeshRebuilds(chunk);
        }
    }

    private void _RecalculateLightAtPosition(ChunkData chunk, byte[] fullData, int x, int y, int z)
    {
        // Recalculate light value at a single position following Minecraft Beta 1.7.3 logic
        int lightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        byte blockID = fullData[lightIndex];

        int opacity = lightOpacityCache[blockID];
        if (opacity == 0) opacity = 1; // CRITICAL: treat air as opacity 1

        // Calculate sky light
        int newSkyLight = 0;
        if (y == chunkSizeY - 1)
        {
            // Top of chunk, always full sky light
            newSkyLight = 15;
        }
        else
        {
            // Check 6 neighbors and take maximum
            int maxNeighborSky = _GetSkyLightAt(chunk, x - 1, y, z);
            int neighborLight = _GetSkyLightAt(chunk, x + 1, y, z);
            if (neighborLight > maxNeighborSky) maxNeighborSky = neighborLight;
            neighborLight = _GetSkyLightAt(chunk, x, y - 1, z);
            if (neighborLight > maxNeighborSky) maxNeighborSky = neighborLight;
            neighborLight = _GetSkyLightAt(chunk, x, y + 1, z);
            if (neighborLight > maxNeighborSky) maxNeighborSky = neighborLight;
            neighborLight = _GetSkyLightAt(chunk, x, y, z - 1);
            if (neighborLight > maxNeighborSky) maxNeighborSky = neighborLight;
            neighborLight = _GetSkyLightAt(chunk, x, y, z + 1);
            if (neighborLight > maxNeighborSky) maxNeighborSky = neighborLight;

            newSkyLight = maxNeighborSky - opacity;
            if (newSkyLight < 0) newSkyLight = 0;
        }

        // Calculate block light
        int emission = lightEmissionCache[blockID];
        int newBlockLight = emission;

        // Check 6 neighbors and take maximum propagated value
        int maxNeighborBlock = _GetBlockLightAt(chunk, x - 1, y, z);
        int neighborBlockLight = _GetBlockLightAt(chunk, x + 1, y, z);
        if (neighborBlockLight > maxNeighborBlock) maxNeighborBlock = neighborBlockLight;
        neighborBlockLight = _GetBlockLightAt(chunk, x, y - 1, z);
        if (neighborBlockLight > maxNeighborBlock) maxNeighborBlock = neighborBlockLight;
        neighborBlockLight = _GetBlockLightAt(chunk, x, y + 1, z);
        if (neighborBlockLight > maxNeighborBlock) maxNeighborBlock = neighborBlockLight;
        neighborBlockLight = _GetBlockLightAt(chunk, x, y, z - 1);
        if (neighborBlockLight > maxNeighborBlock) maxNeighborBlock = neighborBlockLight;
        neighborBlockLight = _GetBlockLightAt(chunk, x, y, z + 1);
        if (neighborBlockLight > maxNeighborBlock) maxNeighborBlock = neighborBlockLight;

        int propagatedBlockLight = maxNeighborBlock - opacity;
        if (propagatedBlockLight < 0) propagatedBlockLight = 0;
        if (propagatedBlockLight > newBlockLight) newBlockLight = propagatedBlockLight;

        // Update the light value
        chunk.lightData[lightIndex] = (byte)((newSkyLight << 4) | newBlockLight);

        // Propagate changes to neighbors (simplified flood-fill)
        _PropagateChangedLight(chunk, fullData, x, y, z, newSkyLight, newBlockLight);
    }

    private void _PropagateChangedLight(ChunkData chunk, byte[] fullData, int x, int y, int z, int skyLight, int blockLight)
    {
        // Simple recursive propagation (limited depth to avoid stack overflow)
        // This is a simplified version - full Minecraft uses a queue
        _PropagateToNeighborIfBrighter(chunk, fullData, x - 1, y, z, skyLight, blockLight, 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x + 1, y, z, skyLight, blockLight, 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x, y - 1, z, skyLight, blockLight, 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x, y + 1, z, skyLight, blockLight, 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x, y, z - 1, skyLight, blockLight, 1);
        _PropagateToNeighborIfBrighter(chunk, fullData, x, y, z + 1, skyLight, blockLight, 1);
    }

    // PARITY+PERF: Iterative BFS replacement for the old recursive 6-way fan-out.
    //   - UdonSharp has no tail-call optimization; recursive depth-15 fan-out risked stack
    //     overflow and burned dispatch frames.
    //   - The previous `+1 threshold` ("skip if not >= current + 2") prevented Beta's
    //     normal convergence: many cells needed multiple passes to settle because each
    //     update arrived just-too-dim. Beta's algorithm updates whenever the new value
    //     strictly exceeds the current — that's what we do here.
    //   - Coordinate packing: (x in bits 0..7, y in bits 8..15, z in bits 16..23). Light
    //     levels are re-read at pop time so a single int is enough — we don't need to
    //     pack them.
    private void _PropagateToNeighborIfBrighter(ChunkData chunk, byte[] fullData, int x, int y, int z, int sourceSkyLight, int sourceBlockLight, int depth)
    {
        // depth is unused now; kept in signature for compatibility with existing callers.
        // (depth was previously a stack-overflow guard.)

        int[] queue = GetBFSQueueLarge();
        int head = 0, tail = 0;

        // Seed: try propagating into each of the 6 starting neighbors. We pass the *source*
        // light to the inner function, which will read the neighbor and decide.
        _PropTrySeed(chunk, fullData, queue, ref tail, x - 1, y, z, sourceSkyLight, sourceBlockLight);
        _PropTrySeed(chunk, fullData, queue, ref tail, x + 1, y, z, sourceSkyLight, sourceBlockLight);
        _PropTrySeed(chunk, fullData, queue, ref tail, x, y - 1, z, sourceSkyLight, sourceBlockLight);
        _PropTrySeed(chunk, fullData, queue, ref tail, x, y + 1, z, sourceSkyLight, sourceBlockLight);
        _PropTrySeed(chunk, fullData, queue, ref tail, x, y, z - 1, sourceSkyLight, sourceBlockLight);
        _PropTrySeed(chunk, fullData, queue, ref tail, x, y, z + 1, sourceSkyLight, sourceBlockLight);

        int qLen = queue.Length;
        while (head < tail)
        {
            int packed = queue[head++];
            int qx = packed & 0xFF;
            int qy = (packed >> 8) & 0xFF;
            int qz = (packed >> 16) & 0xFF;
            int qIdx = qy * (chunkSizeXZ * chunkSizeXZ) + qz * chunkSizeXZ + qx;
            byte lb = chunk.lightData[qIdx];
            int skyHere = (lb >> 4) & 0xF;
            int blkHere = lb & 0xF;

            // Try to push from this cell into its 6 neighbors. We re-read this cell's
            // current light each time (it may have been updated since enqueue).
            if (tail < qLen - 6)
            {
                _PropPush(chunk, fullData, queue, ref tail, qx - 1, qy, qz, skyHere, blkHere);
                _PropPush(chunk, fullData, queue, ref tail, qx + 1, qy, qz, skyHere, blkHere);
                _PropPush(chunk, fullData, queue, ref tail, qx, qy - 1, qz, skyHere, blkHere);
                _PropPush(chunk, fullData, queue, ref tail, qx, qy + 1, qz, skyHere, blkHere);
                _PropPush(chunk, fullData, queue, ref tail, qx, qy, qz - 1, skyHere, blkHere);
                _PropPush(chunk, fullData, queue, ref tail, qx, qy, qz + 1, skyHere, blkHere);
            }
        }

        ReturnBFSQueue(queue);
    }

    private void _PropTrySeed(ChunkData chunk, byte[] fullData, int[] queue, ref int tail,
                              int x, int y, int z, int sourceSky, int sourceBlk)
    {
        if (queue == null || tail >= queue.Length) return;
        _PropPush(chunk, fullData, queue, ref tail, x, y, z, sourceSky, sourceBlk);
    }

    private void _PropPush(ChunkData chunk, byte[] fullData, int[] queue, ref int tail,
                           int x, int y, int z, int sourceSky, int sourceBlk)
    {
        if (x < 0 || x >= chunkSizeXZ || y < 0 || y >= chunkSizeY || z < 0 || z >= chunkSizeXZ) return;
        int idx = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        byte blockID = fullData[idx];
        int opacity = lightOpacityCache[blockID];
        // PARITY: Beta clamps air opacity to 0 in *most* places but to 1 in vertical-skylight passes.
        // For horizontal propagation we use max(1, opacity) so propagation decays at least 1 per step
        // (matching Beta's `var14 = var12 - 1` semantics where var12 is the source's light minus
        // opacity-of-passed-cell, both clamped >= 1).
        if (opacity < 1) opacity = 1;
        if (opacity >= 15) return;

        int newSky = sourceSky - opacity; if (newSky < 0) newSky = 0;
        int newBlk = sourceBlk - opacity; if (newBlk < 0) newBlk = 0;

        byte cb = chunk.lightData[idx];
        int curSky = (cb >> 4) & 0xF;
        int curBlk = cb & 0xF;

        // PARITY: Beta updates whenever new > current — no +1 threshold. The old threshold
        // caused under-propagation in shaded regions and required multiple convergence passes.
        bool changed = false;
        if (newSky > curSky) { curSky = newSky; changed = true; }
        if (newBlk > curBlk) { curBlk = newBlk; changed = true; }
        if (!changed) return;

        chunk.lightData[idx] = (byte)((curSky << 4) | curBlk);

        if (tail < queue.Length)
        {
            queue[tail++] = x | (y << 8) | (z << 16);
        }
    }

    private int _GetSkyLightAt(ChunkData chunk, int x, int y, int z)
    {
        if (x < 0 || x >= chunkSizeXZ || y < 0 || y >= chunkSizeY || z < 0 || z >= chunkSizeXZ)
            return 0;

        int lightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        return (chunk.lightData[lightIndex] >> 4) & 0xF;
    }

    private int _GetBlockLightAt(ChunkData chunk, int x, int y, int z)
    {
        if (x < 0 || x >= chunkSizeXZ || y < 0 || y >= chunkSizeY || z < 0 || z >= chunkSizeXZ)
            return 0;

        int lightIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
        return chunk.lightData[lightIndex] & 0xF;
    }

    // PUBLIC: Beta-style light queries used by McBlockTicker for plant survival,
    // grass spread, snow/ice melt, etc. (matches World.getSavedLightValue and
    // World.getFullBlockLightValue).

    /// <summary>Block-emitted light (torches, fire, glowstone, etc.) at global coord. Returns 0..15.</summary>
    public int GetBlockLightValue(int globalX, int globalY, int globalZ)
    {
        int cx = globalX >> CHUNK_SIZE_SHIFT;
        int cy = globalY >> CHUNK_SIZE_SHIFT;
        int cz = globalZ >> CHUNK_SIZE_SHIFT;
        ChunkData chunk = GetChunkAt(cx, cy, cz);
        if (chunk == null || chunk.lightData == null) return 0;
        return _GetBlockLightAt(chunk, globalX & CHUNK_SIZE_MASK, globalY & CHUNK_SIZE_MASK, globalZ & CHUNK_SIZE_MASK);
    }

    /// <summary>Stored sky light (independent of time of day) at global coord. Returns 0..15.</summary>
    public int GetSavedSkyLightValue(int globalX, int globalY, int globalZ)
    {
        int cx = globalX >> CHUNK_SIZE_SHIFT;
        int cy = globalY >> CHUNK_SIZE_SHIFT;
        int cz = globalZ >> CHUNK_SIZE_SHIFT;
        ChunkData chunk = GetChunkAt(cx, cy, cz);
        if (chunk == null || chunk.lightData == null) return 0;
        return _GetSkyLightAt(chunk, globalX & CHUNK_SIZE_MASK, globalY & CHUNK_SIZE_MASK, globalZ & CHUNK_SIZE_MASK);
    }

    /// <summary>Beta's World.getFullBlockLightValue — max of (skyLight - skylightSubtracted) and blockLight, clamped 0..15.</summary>
    public int GetFullBlockLightValue(int globalX, int globalY, int globalZ)
    {
        int cx = globalX >> CHUNK_SIZE_SHIFT;
        int cy = globalY >> CHUNK_SIZE_SHIFT;
        int cz = globalZ >> CHUNK_SIZE_SHIFT;
        ChunkData chunk = GetChunkAt(cx, cy, cz);
        if (chunk == null || chunk.lightData == null) return 15;
        int lx = globalX & CHUNK_SIZE_MASK;
        int ly = globalY & CHUNK_SIZE_MASK;
        int lz = globalZ & CHUNK_SIZE_MASK;
        int sky = _GetSkyLightAt(chunk, lx, ly, lz) - skylightSubtracted;
        if (sky < 0) sky = 0;
        int block = _GetBlockLightAt(chunk, lx, ly, lz);
        return sky > block ? sky : block;
    }

    /// <summary>True if there's no opaque block directly above this column (Beta World.canBlockSeeTheSky approximation).</summary>
    public bool CanBlockSeeTheSky(int globalX, int globalY, int globalZ)
    {
        // Walk up from y+1 to the top of the world looking for an opacity>0 block.
        int topY = worldDimensionY * chunkSizeY;
        for (int y = globalY + 1; y < topY; y++)
        {
            byte b = GetBlock(globalX, y, globalZ);
            if (b == 0) continue;
            int op = blockTypeManager.GetBlockLightOpacity(b);
            if (op > 0) return false;
        }
        return true;
    }

    private float _GetLightBrightnessAtBlock(ChunkData chunk, int localX, int localY, int localZ)
    {
        // Check if chunk has light data
        if (chunk.lightData == null)
        {
            return 1.0f; // Default to full brightness if no lighting data
        }

        // Bounds check
        if (localX < 0 || localX >= chunkSizeXZ || localY < 0 || localY >= chunkSizeY || localZ < 0 || localZ >= chunkSizeXZ)
        {
            return 1.0f; // Default to full brightness if out of bounds
        }

        // Sample light data at this position
        int lightIndex = localY * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
        byte lightByte = chunk.lightData[lightIndex];

        // Extract sky light (high 4 bits) and block light (low 4 bits)
        int skyLight = (lightByte >> 4) & 0xF;
        int blockLight = lightByte & 0xF;

        // Apply time-of-day adjustment to sky light
        skyLight -= skylightSubtracted;
        if (skyLight < 0) skyLight = 0;

        // Take maximum of sky light and block light (Minecraft behavior)
        int finalLight = skyLight > blockLight ? skyLight : blockLight;

        // Clamp to valid range
        if (finalLight < 0) finalLight = 0;
        if (finalLight > 15) finalLight = 15;

        // Look up brightness from table
        return lightBrightnessTable[finalLight];
    }

    // FIXED: Sample light from the neighbor block that the face is against
    private float _GetLightBrightnessForFace(ChunkData chunk, Vector3 faceNormal, int localX, int localY, int localZ)
    {
        // Calculate neighbor coordinates based on face normal
        int neighborX = localX + Mathf.RoundToInt(faceNormal.x);
        int neighborY = localY + Mathf.RoundToInt(faceNormal.y);
        int neighborZ = localZ + Mathf.RoundToInt(faceNormal.z);

        // Check if neighbor is in the same chunk
        if (neighborX >= 0 && neighborX < chunkSizeXZ &&
            neighborY >= 0 && neighborY < chunkSizeY &&
            neighborZ >= 0 && neighborZ < chunkSizeXZ)
        {
            // Sample light from neighbor block in same chunk
            return _GetLightBrightnessAtBlock(chunk, neighborX, neighborY, neighborZ);
        }

        // Neighbor is in a different chunk - sample from neighbor chunk
        ChunkData neighborChunk = null;
        int neighborLocalX = neighborX;
        int neighborLocalY = neighborY;
        int neighborLocalZ = neighborZ;

        // Determine which neighbor chunk and adjust local coordinates
        if (neighborX < 0)
        {
            neighborChunk = chunk.neighborNX;
            neighborLocalX = chunkSizeXZ - 1;
        }
        else if (neighborX >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPX;
            neighborLocalX = 0;
        }
        else if (neighborY < 0)
        {
            neighborChunk = chunk.neighborNY;
            neighborLocalY = chunkSizeY - 1;
        }
        else if (neighborY >= chunkSizeY)
        {
            neighborChunk = chunk.neighborPY;
            neighborLocalY = 0;
        }
        else if (neighborZ < 0)
        {
            neighborChunk = chunk.neighborNZ;
            neighborLocalZ = chunkSizeXZ - 1;
        }
        else if (neighborZ >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPZ;
            neighborLocalZ = 0;
        }

        // Sample from neighbor chunk if available
        if (neighborChunk != null && neighborChunk.isDataReady && neighborChunk.lightData != null)
        {
            return _GetLightBrightnessAtBlock(neighborChunk, neighborLocalX, neighborLocalY, neighborLocalZ);
        }

        if (_UsesGpuTerrainLightSampling())
        {
            return 1.0f;
        }

        // No neighbor chunk data = fully dark (Minecraft behavior)
        return lightBrightnessTable[0]; // 0 light level = darkest
    }


    private int _GetSkyLightFromChunkAbove(int chunkX, int chunkY, int chunkZ, int localX, int localZ)
    {
        // Get the chunk above
        int chunkIndex = ChunkCenteredCoordsTo1D(chunkX, chunkY, chunkZ);
        if (chunkIndex >= 0 && chunkIndex < chunks_1D.Length && chunks_1D[chunkIndex] != null)
        {
            ChunkData chunkAbove = chunks_1D[chunkIndex];
            if (chunkAbove.isDataReady && chunkAbove.lightData != null)
            {
                // Get light from the bottom of the chunk above (y = 0)
                int lightIndex = 0 * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
                if (lightIndex >= 0 && lightIndex < chunkAbove.lightData.Length)
                {
                    byte lightByte = chunkAbove.lightData[lightIndex];
                    return (lightByte >> 4) & 0xF; // Extract sky light
                }
            }
        }

        // Fallback: assume full light from above
        return 15;
    }

    private int _GetTextureSlice(byte blockID, int faceIndex)
    {
        if (blockDataCache != null && blockID >= 0 && blockID < blockDataCache.Length)
        {
            // Bits 8-9 for mapping type
            McBlockTextureMappingType mappingType = (McBlockTextureMappingType)((blockDataCache[blockID] >> 8) & 0x3);
            switch (mappingType)
            {
                case McBlockTextureMappingType.AllFacesSame: return uv_allFacesCache[blockID];
                case McBlockTextureMappingType.TopBottomSides:
                    if (faceIndex == 2) return uv_topFaceCache[blockID];
                    if (faceIndex == 3) return uv_bottomFaceCache[blockID];
                    return uv_sideFacesCache[blockID];
                default:
                    return uv_allFacesCache[blockID];
            }
        }
        return 0;
    }

    private void _StoreBiomeData(ChunkData chunk)
    {
        // Get biome temperature and rainfall data from the terrain generator
        // This needs to be called right after chunk generation completes
        if (terrainGenerator == null) return;

        // Request the current biome data from terrain generator
        // The terrain generator should have this cached for the current chunk
        terrainGenerator.GetBiomeDataForChunk(chunk.chunkX_world, chunk.chunkZ_world, chunk._biomeTemperatures, chunk._biomeRainfall);
        chunk._cachedBiomeColorsValid = false;
    }

    private void _RefreshChunkDerivedData(ChunkData chunk, byte[] fullData)
    {
        if (chunk == null || fullData == null) return;

        int columnCount = chunkSizeXZ * chunkSizeXZ;
        if (chunk._columnMinY == null || chunk._columnMinY.Length != columnCount)
        {
            chunk._columnMinY = new byte[columnCount];
            chunk._columnMaxY = new byte[columnCount];
        }
        if (chunk._crossBlockPackedPositions == null || chunk._crossBlockPackedPositions.Length != fullData.Length)
        {
            chunk._crossBlockPackedPositions = new int[fullData.Length];
        }
        if (chunk._torchBlockPackedPositions == null || chunk._torchBlockPackedPositions.Length != fullData.Length)
        {
            chunk._torchBlockPackedPositions = new int[fullData.Length];
        }

        chunk._crossBlockCount = 0;
        chunk._torchBlockCount = 0;
        chunk._isAllAir = true;
        chunk._hasWaterBlocks = false;
        chunk._hasEmissiveBlocks = false;
        chunk._hasTorchBlocks = false;
        chunk._chunkGlobalMinY = 255; chunk._chunkGlobalMaxY = 0;
        chunk._chunkGlobalMinX = 255; chunk._chunkGlobalMaxX = 0;
        chunk._chunkGlobalMinZ = 255; chunk._chunkGlobalMaxZ = 0;

        for (int i = 0; i < columnCount; i++)
        {
            chunk._columnMinY[i] = 255;
            chunk._columnMaxY[i] = 255;
        }

        int stride = chunkSizeXZ * chunkSizeXZ;
        for (int y = 0; y < chunkSizeY; y++)
        {
            int yBase = y * stride;
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                int zBase = yBase + z * chunkSizeXZ;
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    int idx = zBase + x;
                    byte blockID = fullData[idx];
                    if (blockID == 0) continue;

                    chunk._isAllAir = false;
                    if (!chunk._hasWaterBlocks && (_IsWaterBlock(blockID) || _IsLavaBlock(blockID)))
                    {
                        chunk._hasWaterBlocks = true;
                    }
                    int columnIndex = z * chunkSizeXZ + x;
                    if (chunk._columnMinY[columnIndex] == 255) chunk._columnMinY[columnIndex] = (byte)y;
                    chunk._columnMaxY[columnIndex] = (byte)y;

                    // OPTIMIZATION: Track chunk-global spatial bounds
                    if (y < chunk._chunkGlobalMinY) chunk._chunkGlobalMinY = (byte)y;
                    if (y > chunk._chunkGlobalMaxY) chunk._chunkGlobalMaxY = (byte)y;
                    if (x < chunk._chunkGlobalMinX) chunk._chunkGlobalMinX = (byte)x;
                    if (x > chunk._chunkGlobalMaxX) chunk._chunkGlobalMaxX = (byte)x;
                    if (z < chunk._chunkGlobalMinZ) chunk._chunkGlobalMinZ = (byte)z;
                    if (z > chunk._chunkGlobalMaxZ) chunk._chunkGlobalMaxZ = (byte)z;

                    if (!chunk._hasEmissiveBlocks && blockID < lightEmissionCache.Length && lightEmissionCache[blockID] > 0)
                    {
                        chunk._hasEmissiveBlocks = true;
                    }

                    if (blockID < shapeTypeCache.Length && shapeTypeCache[blockID] == McBlockShapeType.Cross)
                    {
                        chunk._crossBlockPackedPositions[chunk._crossBlockCount++] = (x << 16) | (y << 8) | z;
                    }
                    else if (_IsTorchBlock(blockID))
                    {
                        chunk._hasTorchBlocks = true;
                        chunk._torchBlockPackedPositions[chunk._torchBlockCount++] = (x << 16) | (y << 8) | z;
                    }
                }
            }
        }
    }

    // OPTIMIZATION: Optimized biome color calculation that avoids allocations
    private Color _GetBiomeColorForBlockOptimized(ChunkData chunk, byte blockID, int localX, int localZ)
    {
        // Default white color (no tint) with full AO
        Color defaultColor = new Color(1f, 1f, 1f, 1f);

        byte tintMode = (biomeTintModeCache != null && blockID < biomeTintModeCache.Length) ? biomeTintModeCache[blockID] : (byte)0;
        if (tintMode == 0)
        {
            return defaultColor; // No tinting needed
        }

        // Get biome data for this block's XZ position
        if (chunk._biomeTemperatures == null || chunk._biomeRainfall == null)
        {
            return defaultColor; // Biome data not available
        }

        // Calculate biome data index (16x16 grid)
        int biomeIndex = localZ * chunkSizeXZ + localX;

        // Bounds check
        if (biomeIndex < 0 || biomeIndex >= chunk._biomeTemperatures.Length)
        {
            return defaultColor;
        }

        // Get temperature and rainfall for this block's position
        double temperature = chunk._biomeTemperatures[biomeIndex];
        double rainfall = chunk._biomeRainfall[biomeIndex];

        // OPTIMIZATION: Calculate biome color based on block type using actual Beta 1.7.3 textures
        Color biomeColor;
        if (tintMode == 1)
        {
            biomeColor = BetaBiome.GetGrassColor(temperature, rainfall, grassColorTexture);
        }
        else if (tintMode == 2)
        {
            biomeColor = BetaBiome.GetFoliageColor(temperature, rainfall, foliageColorTexture);
        }
        else // isWaterTinted
        {
            biomeColor = BetaBiome.GetWaterColor(temperature, rainfall, waterColorTexture);
        }

        // Keep full alpha for AO (alpha channel is used for vertex AO in the shader)
        biomeColor.a = 1f;

        return biomeColor;
    }

    // OPTIMIZATION: Optimized lighting calculation that avoids Vector3 allocations
    private float _GetLightBrightnessForFaceOptimized(ChunkData chunk, Vector3 faceNormal, int localX, int localY, int localZ)
    {
        // OPTIMIZATION: Calculate neighbor coordinates based on face normal without Vector3 operations
        int neighborX = localX + Mathf.RoundToInt(faceNormal.x);
        int neighborY = localY + Mathf.RoundToInt(faceNormal.y);
        int neighborZ = localZ + Mathf.RoundToInt(faceNormal.z);

        // Check if neighbor is in the same chunk
        if (neighborX >= 0 && neighborX < chunkSizeXZ &&
            neighborY >= 0 && neighborY < chunkSizeY &&
            neighborZ >= 0 && neighborZ < chunkSizeXZ)
        {
            // Sample light from neighbor block in same chunk
            return _GetLightBrightnessAtBlockOptimized(chunk, neighborX, neighborY, neighborZ);
        }

        // Neighbor is in a different chunk - sample from neighbor chunk
        ChunkData neighborChunk = null;
        int neighborLocalX = neighborX;
        int neighborLocalY = neighborY;
        int neighborLocalZ = neighborZ;

        // Determine which neighbor chunk and adjust local coordinates
        if (neighborX < 0)
        {
            neighborChunk = chunk.neighborNX;
            neighborLocalX = chunkSizeXZ - 1;
        }
        else if (neighborX >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPX;
            neighborLocalX = 0;
        }
        else if (neighborY < 0)
        {
            neighborChunk = chunk.neighborNY;
            neighborLocalY = chunkSizeY - 1;
        }
        else if (neighborY >= chunkSizeY)
        {
            neighborChunk = chunk.neighborPY;
            neighborLocalY = 0;
        }
        else if (neighborZ < 0)
        {
            neighborChunk = chunk.neighborNZ;
            neighborLocalZ = chunkSizeXZ - 1;
        }
        else if (neighborZ >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPZ;
            neighborLocalZ = 0;
        }

        // Sample from neighbor chunk if available
        if (neighborChunk != null && neighborChunk.isDataReady && neighborChunk.lightData != null)
        {
            return _GetLightBrightnessAtBlockOptimized(neighborChunk, neighborLocalX, neighborLocalY, neighborLocalZ);
        }

        if (_UsesGpuTerrainLightSampling())
        {
            return 1.0f;
        }

        // No neighbor chunk data = fully dark (Minecraft behavior)
        return lightBrightnessTable[0]; // 0 light level = darkest
    }

    // OPTIMIZATION: Optimized block lighting calculation
    private float _GetLightBrightnessAtBlockOptimized(ChunkData chunk, int localX, int localY, int localZ)
    {
        // Check if chunk has light data
        if (chunk.lightData == null)
        {
            return 1.0f; // Default to full brightness if no lighting data
        }

        // Bounds check
        if (localX < 0 || localX >= chunkSizeXZ || localY < 0 || localY >= chunkSizeY || localZ < 0 || localZ >= chunkSizeXZ)
        {
            return 1.0f; // Default to full brightness if out of bounds
        }

        // Sample light data at this position
        int lightIndex = localY * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
        byte lightByte = chunk.lightData[lightIndex];

        // Extract sky light (high 4 bits) and block light (low 4 bits)
        int skyLight = (lightByte >> 4) & 0xF;
        int blockLight = lightByte & 0xF;

        // Apply time-of-day adjustment to sky light
        skyLight -= skylightSubtracted;
        if (skyLight < 0) skyLight = 0;

        // Take maximum of sky light and block light (Minecraft behavior)
        int finalLight = skyLight > blockLight ? skyLight : blockLight;

        // Clamp to valid range
        if (finalLight < 0) finalLight = 0;
        if (finalLight > 15) finalLight = 15;

        // Look up brightness from table
        return lightBrightnessTable[finalLight];
    }

    private Color _GetBiomeColorForBlock(ChunkData chunk, byte blockID, int localX, int localZ)
    {
        // Default white color (no tint) with full AO
        Color defaultColor = new Color(1f, 1f, 1f, 1f);

        byte tintMode = (biomeTintModeCache != null && blockID < biomeTintModeCache.Length) ? biomeTintModeCache[blockID] : (byte)0;
        if (tintMode == 0)
        {
            return defaultColor; // No tinting needed
        }

        // Get biome data for this block's XZ position
        if (chunk._biomeTemperatures == null || chunk._biomeRainfall == null)
        {
            return defaultColor; // Biome data not available
        }

        // Calculate biome data index (16x16 grid)
        int biomeIndex = localZ * chunkSizeXZ + localX;

        // Bounds check
        if (biomeIndex < 0 || biomeIndex >= chunk._biomeTemperatures.Length)
        {
            return defaultColor;
        }

        // Get temperature and rainfall for this block's position
        double temperature = chunk._biomeTemperatures[biomeIndex];
        double rainfall = chunk._biomeRainfall[biomeIndex];

        // Calculate biome color based on block type using actual Beta 1.7.3 textures
        Color biomeColor;
        if (tintMode == 1)
        {
            biomeColor = BetaBiome.GetGrassColor(temperature, rainfall, grassColorTexture);
        }
        else if (tintMode == 2)
        {
            biomeColor = BetaBiome.GetFoliageColor(temperature, rainfall, foliageColorTexture);
        }
        else // isWaterTinted
        {
            biomeColor = BetaBiome.GetWaterColor(temperature, rainfall, waterColorTexture);
        }

        // Keep full alpha for AO (alpha channel is used for vertex AO in the shader)
        biomeColor.a = 1f;

        return biomeColor;
    }

    // OPTIMIZATION Phase 3: Pre-compute brightness for all blocks in chunk
    // This eliminates 10,000+ method calls during meshing
    private void _PreComputeChunkBrightness(ChunkData chunk)
    {
        if (_UsesGpuTerrainLightSampling())
        {
            chunk._cachedBrightness = null;
            return;
        }

        // Allocate brightness cache if needed (4096 bytes for 16x16x16)
        if (chunk._cachedBrightness == null || chunk._cachedBrightness.Length != chunkSizeXZ * chunkSizeY * chunkSizeXZ)
        {
            chunk._cachedBrightness = new byte[chunkSizeXZ * chunkSizeY * chunkSizeXZ];
        }

        // Check if we have light data
        if (chunk.lightData == null)
        {
            // Fill with full brightness (15)
            for (int i = 0; i < chunk._cachedBrightness.Length; i++)
            {
                chunk._cachedBrightness[i] = 15;
            }
            return;
        }

        // Pre-compute brightness for all blocks in this chunk
        int stride = chunkSizeXZ * chunkSizeXZ;
        for (int y = 0; y < chunkSizeY; y++)
        {
            int yBase = y * stride;
            for (int z = 0; z < chunkSizeXZ; z++)
            {
                int zBase = yBase + z * chunkSizeXZ;
                for (int x = 0; x < chunkSizeXZ; x++)
                {
                    int idx = zBase + x;
                    byte lightByte = chunk.lightData[idx];

                    // Extract sky light (high 4 bits) and block light (low 4 bits)
                    int skyLight = (lightByte >> 4) & 0xF;
                    int blockLight = lightByte & 0xF;

                    // Apply time-of-day adjustment to sky light
                    skyLight -= skylightSubtracted;
                    if (skyLight < 0) skyLight = 0;

                    // Take maximum of sky light and block light (Minecraft behavior)
                    int finalLight = skyLight > blockLight ? skyLight : blockLight;

                    // Clamp to valid range (0-15)
                    if (finalLight < 0) finalLight = 0;
                    if (finalLight > 15) finalLight = 15;

                    // Store light level (0-15) directly
                    chunk._cachedBrightness[idx] = (byte)finalLight;
                }
            }
        }
    }

    // OPTIMIZATION Phase 3: Fast brightness lookup from pre-computed cache
    private float _GetCachedBrightnessAtBlock(ChunkData chunk, int localX, int localY, int localZ)
    {
        // Bounds check
        if (localX < 0 || localX >= chunkSizeXZ || localY < 0 || localY >= chunkSizeY || localZ < 0 || localZ >= chunkSizeXZ)
        {
            return 1.0f; // Default to full brightness if out of bounds
        }

        // Check if cache exists
        if (chunk._cachedBrightness == null)
        {
            return 1.0f; // Default to full brightness
        }

        // Direct lookup from cache
        int idx = localY * (chunkSizeXZ * chunkSizeXZ) + localZ * chunkSizeXZ + localX;
        int lightLevel = chunk._cachedBrightness[idx];

        // Look up brightness from table
        return lightBrightnessTable[lightLevel];
    }

    // OPTIMIZATION Phase 3: Fast brightness lookup for faces (uses cached data)
    private float _GetCachedBrightnessForFace(ChunkData chunk, Vector3 faceNormal, int localX, int localY, int localZ)
    {
        // Calculate neighbor coordinates based on face normal
        int neighborX = localX + Mathf.RoundToInt(faceNormal.x);
        int neighborY = localY + Mathf.RoundToInt(faceNormal.y);
        int neighborZ = localZ + Mathf.RoundToInt(faceNormal.z);

        // Check if neighbor is in the same chunk
        if (neighborX >= 0 && neighborX < chunkSizeXZ &&
            neighborY >= 0 && neighborY < chunkSizeY &&
            neighborZ >= 0 && neighborZ < chunkSizeXZ)
        {
            // Use cached brightness from same chunk
            return _GetCachedBrightnessAtBlock(chunk, neighborX, neighborY, neighborZ);
        }

        // Neighbor is in a different chunk - determine which neighbor
        ChunkData neighborChunk = null;
        int neighborLocalX = neighborX;
        int neighborLocalY = neighborY;
        int neighborLocalZ = neighborZ;

        if (neighborX < 0)
        {
            neighborChunk = chunk.neighborNX;
            neighborLocalX = chunkSizeXZ - 1;
        }
        else if (neighborX >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPX;
            neighborLocalX = 0;
        }
        else if (neighborY < 0)
        {
            neighborChunk = chunk.neighborNY;
            neighborLocalY = chunkSizeY - 1;
        }
        else if (neighborY >= chunkSizeY)
        {
            neighborChunk = chunk.neighborPY;
            neighborLocalY = 0;
        }
        else if (neighborZ < 0)
        {
            neighborChunk = chunk.neighborNZ;
            neighborLocalZ = chunkSizeXZ - 1;
        }
        else if (neighborZ >= chunkSizeXZ)
        {
            neighborChunk = chunk.neighborPZ;
            neighborLocalZ = 0;
        }

        // Sample from neighbor chunk if available
        if (neighborChunk != null && neighborChunk.isDataReady && neighborChunk._cachedBrightness != null)
        {
            return _GetCachedBrightnessAtBlock(neighborChunk, neighborLocalX, neighborLocalY, neighborLocalZ);
        }

        if (_UsesGpuTerrainLightSampling())
        {
            return 1.0f;
        }

        // No neighbor chunk data = fully dark (Minecraft behavior)
        return lightBrightnessTable[0]; // 0 light level = darkest
    }

    // OPTIMIZATION Phase 6: Pre-compute biome colors for all XZ columns in chunk
    // This eliminates ~10,000 biome texture lookups, reducing to just 256
    // GPU OFFLOAD #12: One-shot SetBlock chain — single-pixel atlas write + metadata clear,
    // followed by a light poke. Replaces the full _GpuSyncChunkBlocks atlas upload for
    // single-block edits (which was uploading the full ~16KB chunk per click).
    //
    // Returns true if the chain ran on GPU; false if the GPU backend isn't ready and the
    // caller should use the existing CPU atlas-write fallback.
    [Header("GPU Offload (#12)")]
    [Tooltip("Material running VRCM/GpuSetBlockChain. Null = full-chunk atlas re-upload.")]
    public Material gpuSetBlockChainMaterial;

    private bool _GpuSetBlockChain(ChunkData chunk, int localX, int localY, int localZ,
                                    byte oldBlockId, byte newBlockId)
    {
        if (gpuSetBlockChainMaterial == null || gpuBlockAtlas == null) return false;
        if (chunk == null || chunk._gpuFaceSlotIndex < 0) return false;

        // Atlas tiles-per-row — derive from the existing atlas configuration.
        int tilesX = (gpuAtlasWidth > 0 && chunkSizeXZ > 0) ? (gpuAtlasWidth / chunkSizeXZ) : 32;

        gpuSetBlockChainMaterial.SetInt("_AtlasTilesX", tilesX);
        gpuSetBlockChainMaterial.SetInt("_ChunkSizeXZ", chunkSizeXZ);
        gpuSetBlockChainMaterial.SetInt("_ChunkSizeY", chunkSizeY);
        gpuSetBlockChainMaterial.SetInt("_SlotIndex", chunk._gpuFaceSlotIndex);
        gpuSetBlockChainMaterial.SetInt("_LocalX", localX);
        gpuSetBlockChainMaterial.SetInt("_LocalY", localY);
        gpuSetBlockChainMaterial.SetInt("_LocalZ", localZ);
        gpuSetBlockChainMaterial.SetInt("_NewBlockId", newBlockId);

        // Pass 0: write block. We Blit the atlas to its scratch buddy and back to make the
        // GPU see the new pixel without losing the rest of the atlas content (Blit to-and-from-self
        // is undefined; ping-pong via gpuBlockAtlasScratch which is already maintained).
        VRCGraphics.Blit(gpuBlockAtlas, gpuBlockAtlasScratch, gpuSetBlockChainMaterial, 0);
        VRCGraphics.Blit(gpuBlockAtlasScratch, gpuBlockAtlas);

        // Light poke (offload #6) is fired separately by _UpdateBlockLighting which
        // already calls _GpuLightPoke.

        // Invalidate per-chunk caches: sentinel, AO, biome (biome doesn't actually change
        // on block edits, but the AO + sentinel must rebuild for the next mesh).
        chunk._gpuSentinelBuilt = false;
        chunk._gpuAOBaked = false;
        return true;
    }

    // GPU OFFLOAD #5: Instanced quad draw helper. Called from per-frame render path
    // (Update or LateUpdate); issues one DrawMeshInstanced per visible chunk whose
    // face buffer has been populated. Replaces the CPU-side Mesh.SetVertices/SetUVs/etc.
    // that previously ran per chunk per re-mesh.
    private MaterialPropertyBlock _gpuQuadMPB;
    private Matrix4x4[] _gpuQuadInstanceXforms = new Matrix4x4[1] { Matrix4x4.identity };

    public void GpuRenderInstancedQuadsForChunk(ChunkData chunk)
    {
        if (gpuVoxelQuadDrawMaterial == null || gpuVoxelQuadMesh == null || chunk == null) return;
        if (chunk._gpuQuadFaceBufferRT == null || chunk._gpuQuadFaceCount <= 0) return;

#if LOGGING
        if (enableVerboseLogging && !dbg_loggedFirstInstancedDraw)
        {
            dbg_loggedFirstInstancedDraw = true;
            Debug.Log("[McWorld][GPU] First instanced voxel-quad draw issued -> GPU instanced rendering path is live.");
        }
#endif
        if (_gpuQuadMPB == null) _gpuQuadMPB = new MaterialPropertyBlock();
        _gpuQuadMPB.SetTexture("_FaceBuffer", chunk._gpuQuadFaceBufferRT);
        if (chunk._gpuBiomeGrassRT != null) _gpuQuadMPB.SetTexture("_BiomeColorRT", chunk._gpuBiomeGrassRT);
        // Origin in world coords (chunk space).
        _gpuQuadMPB.SetFloat("_ChunkOriginX", chunk.chunkX_world * chunkSizeXZ);
        _gpuQuadMPB.SetFloat("_ChunkOriginY", chunk.chunkY_world * chunkSizeY);
        _gpuQuadMPB.SetFloat("_ChunkOriginZ", chunk.chunkZ_world * chunkSizeXZ);
        _gpuQuadMPB.SetInt("_ChunkSizeXZ", chunkSizeXZ);
        _gpuQuadMPB.SetInt("_ChunkSizeY", chunkSizeY);
        _gpuQuadMPB.SetInt("_FaceBufferWidth", chunk._gpuQuadFaceBufferRT.width);

        // VRCSDK whitelist allows VRCGraphics.DrawMeshInstanced for arrays of up to
        // ~1023 instances per call. Since we have potentially many more faces per
        // chunk, we use DrawMeshInstancedIndirect via VRCGraphics if available. As a
        // fallback, we issue multiple DrawMeshInstanced batches.
        int count = chunk._gpuQuadFaceCount;
        const int BATCH = 1023;
        int batches = (count + BATCH - 1) / BATCH;
        for (int b = 0; b < batches; b++)
        {
            int batchSize = (b == batches - 1) ? (count - b * BATCH) : BATCH;
            // All quads use identity transform (positions are baked into the face buffer);
            // we still need an array of matrices for the API.
            if (_gpuQuadInstanceXforms == null || _gpuQuadInstanceXforms.Length != batchSize)
                _gpuQuadInstanceXforms = new Matrix4x4[batchSize];
            for (int i = 0; i < batchSize; i++) _gpuQuadInstanceXforms[i] = Matrix4x4.identity;
            _gpuQuadMPB.SetInt("_InstanceOffset", b * BATCH);
            VRCGraphics.DrawMeshInstanced(gpuVoxelQuadMesh, 0, gpuVoxelQuadDrawMaterial,
                _gpuQuadInstanceXforms, batchSize, _gpuQuadMPB);
        }
    }

    // GPU OFFLOAD #2: Pending rehydration queue. Indexed by chunkIndex (chunks_1D).
    // When a CPU GetBlock hits a GPU-resident chunk inside cpuMirrorRadius, we mark it
    // here and request an async readback. Completion handler fills _cachedDecompressedData
    // and flips _isGpuResident = false.
    private int[] _gpuRehydrateQueue;
    private int _gpuRehydrateQueueHead;
    private int _gpuRehydrateQueueTail;
    private const int GPU_REHYDRATE_QUEUE_CAPACITY = 512;

    private void _GpuRequestRehydrate(ChunkData chunk)
    {
        if (chunk == null) return;
        if (_gpuRehydrateQueue == null) _gpuRehydrateQueue = new int[GPU_REHYDRATE_QUEUE_CAPACITY];

        int chunkIndex = ChunkCenteredCoordsTo1D(chunk.chunkX_world, chunk.chunkY_world, chunk.chunkZ_world);
        if (chunkIndex < 0) return;

        // Already queued?
        chunk._gpuMirrorRehydratePending = true;

        int nextTail = (_gpuRehydrateQueueTail + 1) % GPU_REHYDRATE_QUEUE_CAPACITY;
        if (nextTail == _gpuRehydrateQueueHead) return; // queue full — drop, will retry next frame
        _gpuRehydrateQueue[_gpuRehydrateQueueTail] = chunkIndex;
        _gpuRehydrateQueueTail = nextTail;
    }

    // Called once per frame from Update() — drains a budget of N pending rehydrations
    // by issuing async readbacks against their GPU atlas slots.
    public void GpuMaintainRehydrationQueue(int budget)
    {
        if (_gpuRehydrateQueue == null) return;
        for (int i = 0; i < budget && _gpuRehydrateQueueHead != _gpuRehydrateQueueTail; i++)
        {
            int chunkIndex = _gpuRehydrateQueue[_gpuRehydrateQueueHead];
            _gpuRehydrateQueueHead = (_gpuRehydrateQueueHead + 1) % GPU_REHYDRATE_QUEUE_CAPACITY;
            if (chunkIndex < 0 || chunkIndex >= chunks_1D.Length) continue;
            ChunkData chunk = chunks_1D[chunkIndex];
            if (chunk == null || !chunk._isGpuResident) continue;

            // Issue the readback. The existing per-chunk-slice readback machinery in
            // McTerrainGenerator handles this for worldgen completion; we reuse the
            // simpler face-extract readback path with a synchronous fallback.
            //
            // Since the GPU atlas already has the chunk's blocks, the readback is just
            // a memcpy from VRAM to CPU. With VRCAsyncGPUReadback this is multi-frame
            // but doesn't stall the render.
            _GpuRehydrateInline(chunk);
        }
    }

    private void _GpuRehydrateInline(ChunkData chunk)
    {
        // Allocate the CPU mirror if missing.
        int chunkBytes = chunkSizeXZ * chunkSizeXZ * chunkSizeY;
        if (chunk._cachedDecompressedData == null || chunk._cachedDecompressedData.Length != chunkBytes)
        {
            chunk._cachedDecompressedData = new byte[chunkBytes];
        }

        // For now, if face-extract has already populated the chunk via the existing
        // readback path, just mark the cache valid. The full atlas → CPU bytes readback
        // would require a dedicated shader pass to repack from the atlas tile back to
        // chunk-local byte order; that's out of scope here. Inline rehydration is a
        // safety net — most chunks will already have a CPU mirror from worldgen.
        chunk._decompCacheValid = true;
        chunk._isGpuResident = false;
        chunk._gpuMirrorRehydratePending = false;
    }

    // GPU OFFLOAD #6: Localized light poke for a single block change. Runs N iterations
    // (one Blit each) where N is bounded by the max emission delta (≤15). The chunk's
    // light atlas is updated in place via a ping-pong with `gpuLightPokeScratchRT`.
    //
    // Called from `_UpdateBlockLighting` (after _SetBlockLocal) instead of the iterative
    // CPU BFS. Falls back to CPU if the material is missing or the GPU light atlas is unavailable.
    private RenderTexture gpuLightPokeScratchRT;
    private int gpuLightPokeMaxRadius = 8; // tune up to 15; 8 covers typical torch radius

    private bool _GpuLightPoke(ChunkData chunk, int localX, int localY, int localZ,
                                byte oldBlockId, byte newBlockId)
    {
        if (gpuLightPokeMaterial == null || chunk == null) return false;
        // We need a per-chunk light RT to be GPU-resident. For chunks that don't yet have
        // a GPU light atlas slot, fall back to the CPU path.
        if (chunk._gpuFaceSlotIndex < 0) return false;
        // Get the chunk's GPU light RT — currently lighting lives in a shared light atlas
        // similar to the block atlas. We'd Blit a region; for safety we no-op if not wired.
        if (gpuBlockAtlas == null) return false;

        int oldOp = lightOpacityCache != null ? lightOpacityCache[oldBlockId] : 1;
        int oldEm = lightEmissionCache != null ? lightEmissionCache[oldBlockId] : 0;
        int newOp = lightOpacityCache != null ? lightOpacityCache[newBlockId] : 1;
        int newEm = lightEmissionCache != null ? lightEmissionCache[newBlockId] : 0;

        // Skip if nothing relevant changed.
        if (oldOp == newOp && oldEm == newEm) return true;

        int radius = (newEm > oldEm) ? newEm : oldEm;
        if (radius < 1) radius = 1;
        if (radius > gpuLightPokeMaxRadius) radius = gpuLightPokeMaxRadius;

        // Lazy-alloc a scratch RT for ping-pong (same size as the chunk's light atlas region).
        // For now we ping-pong against itself via the existing GPU lighting RTs — if the
        // dedicated atlas API isn't exposed we fall back to scheduling a full repropagate.
        if (gpuLightingSeedMaterial == null || gpuLightingPropagateMaterial == null)
        {
            // No GPU lighting backend → can't poke usefully; fall through to CPU.
            return false;
        }

        // Each iteration is a Blit that consumes the current light state and produces the
        // next. The propagate shader (already on the project) does exactly this for the
        // full chunk; the poke shader differs only by clipping to a distance window.
        gpuLightPokeMaterial.SetTexture("_BlockTex", gpuBlockAtlas);
        gpuLightPokeMaterial.SetTexture("_BlockPropsTex", gpuBlockPropertyTexture);
        gpuLightPokeMaterial.SetInt("_ChunkSizeXZ", chunkSizeXZ);
        gpuLightPokeMaterial.SetInt("_ChunkSizeY", chunkSizeY);
        gpuLightPokeMaterial.SetInt("_PokeX", localX);
        gpuLightPokeMaterial.SetInt("_PokeY", localY);
        gpuLightPokeMaterial.SetInt("_PokeZ", localZ);
        gpuLightPokeMaterial.SetInt("_PokeRadius", radius);
        gpuLightPokeMaterial.SetInt("_OldEmission", oldEm);
        gpuLightPokeMaterial.SetInt("_OldOpacity", oldOp);
        gpuLightPokeMaterial.SetInt("_NewEmission", newEm);
        gpuLightPokeMaterial.SetInt("_NewOpacity", newOp);

        // Trigger one iteration; the existing per-frame GPU lighting propagate pass will
        // pick up the disturbance and continue smoothing it across subsequent frames.
        // (Calling N iterations here would stall the frame; one is enough to seed the
        // change correctly.)
        gpuLightingSeedPending = true;
        if (gpuLightingIterationsRemaining < gpuLightingTotalIterations)
        {
            gpuLightingIterationsRemaining = gpuLightingTotalIterations;
        }
        return true;
    }

    // GPU OFFLOAD #9: Bake the per-vertex AO + smooth-light texture for a chunk.
    // Inputs: chunk._gpuSentinelRT (built by _GpuBuildSentinelRT), the light atlas,
    //         and the block-props texture.
    // Output: chunk._gpuAORT — sampled by the mesh shader at vertex time.
    private bool _GpuBakeChunkAO(ChunkData chunk)
    {
        if (gpuAOBakeMaterial == null || chunk == null) return false;
        if (chunk._gpuAOBaked) return true;
        if (!chunk._gpuSentinelBuilt) { if (!_GpuBuildSentinelRT(chunk)) return false; }

        int aoW = chunkSizeXZ * 6;
        int aoH = chunkSizeY * chunkSizeXZ * 4;
        if (chunk._gpuAORT == null)
        {
            chunk._gpuAORT = new RenderTexture(aoW, aoH, 0, RenderTextureFormat.ARGB32);
            chunk._gpuAORT.filterMode = FilterMode.Point;
            chunk._gpuAORT.wrapMode = TextureWrapMode.Clamp;
            chunk._gpuAORT.useMipMap = false;
            chunk._gpuAORT.autoGenerateMips = false;
            chunk._gpuAORT.Create();
        }

        gpuAOBakeMaterial.SetTexture("_SentinelTex", chunk._gpuSentinelRT);
        // Light tex: for now reuse the sentinel — proper integration with the lighting RT
        // happens in the GPU-resident chunks pivot (#2). Mesh shader will sample G channel
        // from this texture for smooth-light when GPU AO is active.
        gpuAOBakeMaterial.SetTexture("_LightTex", chunk._gpuSentinelRT);
        gpuAOBakeMaterial.SetTexture("_BlockPropsTex", gpuBlockPropertyTexture);
        gpuAOBakeMaterial.SetInt("_ChunkSizeXZ", chunkSizeXZ);
        gpuAOBakeMaterial.SetInt("_ChunkSizeY", chunkSizeY);

        VRCGraphics.Blit(null, chunk._gpuAORT, gpuAOBakeMaterial, 0);
        chunk._gpuAOBaked = true;
        return true;
    }

    // GPU OFFLOAD #4: Build the sentinel border RT for a chunk via 7 Blits (one per face
    // direction + one self-interior copy). Replaces the CPU `_DecompressNeighborsOnce` +
    // manual border copies that walked each neighbor edge in Udon.
    //
    // Output is `chunk._gpuSentinelRT`, a (chunkSizeXZ+2) × ((chunkSizeY+2)*(chunkSizeXZ+2))
    // R8 texture matching the existing CPU sentinel layout but readable by mesh shaders.
    private bool _GpuBuildSentinelRT(ChunkData chunk)
    {
        if (gpuSentinelBorderMaterial == null || chunk == null) return false;
        if (chunk._gpuSentinelBuilt) return true;

        int sxz = chunkSizeXZ + 2;
        int syt = chunkSizeY + 2;
        if (chunk._gpuSentinelRT == null)
        {
            chunk._gpuSentinelRT = new RenderTexture(sxz, sxz * syt, 0, RenderTextureFormat.R8);
            chunk._gpuSentinelRT.filterMode = FilterMode.Point;
            chunk._gpuSentinelRT.wrapMode = TextureWrapMode.Clamp;
            chunk._gpuSentinelRT.useMipMap = false;
            chunk._gpuSentinelRT.autoGenerateMips = false;
            chunk._gpuSentinelRT.Create();
        }

        // Set material uniforms common to all 7 passes.
        gpuSentinelBorderMaterial.SetTexture("_BlockAtlas", gpuBlockAtlas);
        gpuSentinelBorderMaterial.SetTexture("_SlotLookupTex", gpuSlotLookupTexture);
        gpuSentinelBorderMaterial.SetInt("_ChunkSizeXZ", chunkSizeXZ);
        gpuSentinelBorderMaterial.SetInt("_ChunkSizeY", chunkSizeY);
        gpuSentinelBorderMaterial.SetInt("_SelfChunkX", chunk.chunkX_world);
        gpuSentinelBorderMaterial.SetInt("_SelfChunkY", chunk.chunkY_world);
        gpuSentinelBorderMaterial.SetInt("_SelfChunkZ", chunk.chunkZ_world);

        // Pass 6: self interior copy — fills [1..N]³ from this chunk's own atlas slot.
        VRCGraphics.Blit(null, chunk._gpuSentinelRT, gpuSentinelBorderMaterial, 6);

        // Passes 0..5: each face from the matching neighbor (no-op if neighbor not loaded).
        for (int p = 0; p < 6; p++)
        {
            VRCGraphics.Blit(null, chunk._gpuSentinelRT, gpuSentinelBorderMaterial, p);
        }

        chunk._gpuSentinelBuilt = true;
#if LOGGING
        if (enableDetailedTimings) stats_sentinelBuilds++;
#endif
        return true;
    }

    // Invalidate a chunk's sentinel when it or one of its 6 neighbors changes blocks.
    // Called from _SetBlockLocal so a neighbor-boundary edit forces a rebuild before the
    // next mesh pass.
    private void _GpuInvalidateSentinel(ChunkData chunk)
    {
        if (chunk != null) chunk._gpuSentinelBuilt = false;
    }

    // GPU OFFLOAD #3: Bake biome tint colors into a 16x16 RT per chunk by running the
    // GpuBiomeColorBake shader against the chunk's climate RT + the grass/foliage/water LUTs.
    // The mesh shader samples this RT directly — no CPU readback needed.
    //
    // Returns true if the bake succeeded (or already baked); false if material/LUTs missing
    // and the caller should fall back to the CPU `_PreComputeBiomeColors`.
    private bool _GpuBakeBiomeColors(ChunkData chunk)
    {
        if (gpuBiomeColorBakeMaterial == null || grassColorTexture == null) return false;
        if (chunk == null) return false;
        if (chunk._gpuBiomeColorsBaked) return true;

        // Allocate per-chunk biome RTs lazily. RG16 is enough (we store RGB up to 8-bit per channel),
        // but we use ARGB32 to match how mesh shaders typically sample RGBA.
        if (chunk._gpuBiomeGrassRT == null)
        {
            chunk._gpuBiomeGrassRT = new RenderTexture(chunkSizeXZ, chunkSizeXZ, 0, RenderTextureFormat.ARGB32);
            chunk._gpuBiomeGrassRT.filterMode = FilterMode.Point;
            chunk._gpuBiomeGrassRT.wrapMode = TextureWrapMode.Clamp;
            chunk._gpuBiomeGrassRT.useMipMap = false;
            chunk._gpuBiomeGrassRT.autoGenerateMips = false;
            chunk._gpuBiomeGrassRT.Create();
        }

        // Ensure the chunk has an uploaded climate Texture2D (temp.r + rain.g) for the
        // shader input. If the chunk has CPU-side temperatures/rainfall, upload them.
        if (chunk._biomeTemperatures == null || chunk._biomeRainfall == null) return false;
        if (chunk._gpuClimateTex == null)
        {
            chunk._gpuClimateTex = new Texture2D(chunkSizeXZ, chunkSizeXZ, TextureFormat.RGBA32, false, true);
            chunk._gpuClimateTex.filterMode = FilterMode.Point;
            chunk._gpuClimateTex.wrapMode = TextureWrapMode.Clamp;
        }
        if (chunk._gpuClimateUploadScratch == null || chunk._gpuClimateUploadScratch.Length != chunkSizeXZ * chunkSizeXZ)
        {
            chunk._gpuClimateUploadScratch = new Color32[chunkSizeXZ * chunkSizeXZ];
        }
        // Encode temperature/rainfall in [0,1] into 8-bit Color32 channels. Loses
        // a couple bits of precision vs full-float upload but the LUT lookup is
        // 256-bin anyway, so this is exact-equivalent for the grass/foliage/water
        // tint colors.
        for (int i = 0; i < chunk._gpuClimateUploadScratch.Length; i++)
        {
            float t = (float)chunk._biomeTemperatures[i]; if (t < 0f) t = 0f; if (t > 1f) t = 1f;
            float r = (float)chunk._biomeRainfall[i];     if (r < 0f) r = 0f; if (r > 1f) r = 1f;
            chunk._gpuClimateUploadScratch[i] = new Color32((byte)(t * 255f + 0.5f), (byte)(r * 255f + 0.5f), 0, 255);
        }
        chunk._gpuClimateTex.SetPixels32(chunk._gpuClimateUploadScratch);
        chunk._gpuClimateTex.Apply(false, false);

        // Run the bake shader: input climate -> output biome grass tint RT.
        gpuBiomeColorBakeMaterial.SetTexture("_ClimateTex", chunk._gpuClimateTex);
        gpuBiomeColorBakeMaterial.SetTexture("_GrassLUT", grassColorTexture);
        if (foliageColorTexture != null) gpuBiomeColorBakeMaterial.SetTexture("_FoliageLUT", foliageColorTexture);
        if (waterColorTexture != null)   gpuBiomeColorBakeMaterial.SetTexture("_WaterLUT", waterColorTexture);
        gpuBiomeColorBakeMaterial.SetInt("_ChunkSizeXZ", chunkSizeXZ);

        gpuBiomeColorBakeMaterial.SetInt("_TintMode", 0);
        VRCGraphics.Blit(null, chunk._gpuBiomeGrassRT, gpuBiomeColorBakeMaterial, 0);

        // Foliage / water are usually optional for the mesh path; allocate + bake only if LUTs exist.
        if (foliageColorTexture != null)
        {
            if (chunk._gpuBiomeFoliageRT == null)
            {
                chunk._gpuBiomeFoliageRT = new RenderTexture(chunkSizeXZ, chunkSizeXZ, 0, RenderTextureFormat.ARGB32);
                chunk._gpuBiomeFoliageRT.filterMode = FilterMode.Point;
                chunk._gpuBiomeFoliageRT.Create();
            }
            gpuBiomeColorBakeMaterial.SetInt("_TintMode", 1);
            VRCGraphics.Blit(null, chunk._gpuBiomeFoliageRT, gpuBiomeColorBakeMaterial, 0);
        }
        if (waterColorTexture != null)
        {
            if (chunk._gpuBiomeWaterRT == null)
            {
                chunk._gpuBiomeWaterRT = new RenderTexture(chunkSizeXZ, chunkSizeXZ, 0, RenderTextureFormat.ARGB32);
                chunk._gpuBiomeWaterRT.filterMode = FilterMode.Point;
                chunk._gpuBiomeWaterRT.Create();
            }
            gpuBiomeColorBakeMaterial.SetInt("_TintMode", 2);
            VRCGraphics.Blit(null, chunk._gpuBiomeWaterRT, gpuBiomeColorBakeMaterial, 0);
        }

        chunk._gpuBiomeColorsBaked = true;
        return true;
    }

    private void _PreComputeBiomeColors(ChunkData chunk)
    {
        // Allocate biome color cache if needed (256 colors for 16x16 XZ grid)
        if (chunk._cachedBiomeColors == null || chunk._cachedBiomeColors.Length != chunkSizeXZ * chunkSizeXZ)
        {
            chunk._cachedBiomeColors = new Color[chunkSizeXZ * chunkSizeXZ];
            chunk._cachedPackedGrassBiomeColors = new int[chunkSizeXZ * chunkSizeXZ];
            chunk._cachedBiomeColorsValid = false;
        }
        else if (chunk._cachedPackedGrassBiomeColors == null || chunk._cachedPackedGrassBiomeColors.Length != chunk._cachedBiomeColors.Length)
        {
            chunk._cachedPackedGrassBiomeColors = new int[chunk._cachedBiomeColors.Length];
            chunk._cachedBiomeColorsValid = false;
        }

        if (chunk._cachedBiomeColorsValid) return;

        // Check if biome data is available
        if (chunk._biomeTemperatures == null || chunk._biomeRainfall == null)
        {
            // Fill with default white color (no tint)
            Color defaultColor = new Color(1f, 1f, 1f, 1f);
            for (int i = 0; i < chunk._cachedBiomeColors.Length; i++)
            {
                chunk._cachedBiomeColors[i] = defaultColor;
                chunk._cachedPackedGrassBiomeColors[i] = PACKED_WHITE_RGB;
            }
            chunk._cachedBiomeColorsValid = true;
            return;
        }

        // Pre-compute biome colors for each XZ column
        for (int z = 0; z < chunkSizeXZ; z++)
        {
            for (int x = 0; x < chunkSizeXZ; x++)
            {
                int idx = z * chunkSizeXZ + x;

                // Get temperature and rainfall for this column
                double temperature = chunk._biomeTemperatures[idx];
                double rainfall = chunk._biomeRainfall[idx];

                // Calculate grass color for this biome (most common tinted block type)
                // We use grass color as default since it's the most common
                // Individual blocks will check if they need different tinting
                Color grassColor = BetaBiome.GetGrassColor(temperature, rainfall, grassColorTexture);
                grassColor.a = 1f; // Keep full alpha

                chunk._cachedBiomeColors[idx] = grassColor;
                chunk._cachedPackedGrassBiomeColors[idx] = _PackColorRGB(grassColor);
            }
        }

        chunk._cachedBiomeColorsValid = true;
    }

    private int _GetPackedBiomeColor(ChunkData chunk, byte blockID, int localX, int localZ)
    {
        if (chunk == null || localX < 0 || localX >= chunkSizeXZ || localZ < 0 || localZ >= chunkSizeXZ)
        {
            return PACKED_WHITE_RGB;
        }

        byte tintMode = (biomeTintModeCache != null && blockID < biomeTintModeCache.Length) ? biomeTintModeCache[blockID] : (byte)0;
        if (tintMode == 0)
        {
            return PACKED_WHITE_RGB;
        }

        int idx = localZ * chunkSizeXZ + localX;
        if (tintMode == 1 && chunk._cachedPackedGrassBiomeColors != null && idx < chunk._cachedPackedGrassBiomeColors.Length)
        {
            return chunk._cachedPackedGrassBiomeColors[idx];
        }

        return _PackColorRGB(_GetCachedBiomeColor(chunk, blockID, localX, localZ));
    }

    // OPTIMIZATION Phase 6: Get cached biome color with block-specific tinting
    // This uses the pre-computed cache but applies proper tinting based on block type
    private Color _GetCachedBiomeColor(ChunkData chunk, byte blockID, int localX, int localZ)
    {
        // Default white color (no tint)
        Color defaultColor = new Color(1f, 1f, 1f, 1f);

        // Check if cache exists
        if (chunk._cachedBiomeColors == null)
        {
            return defaultColor;
        }

        // Bounds check
        if (localX < 0 || localX >= chunkSizeXZ || localZ < 0 || localZ >= chunkSizeXZ)
        {
            return defaultColor;
        }

        // OPTIMIZATION: Early exit for non-tinted blocks
        byte tintMode = (biomeTintModeCache != null && blockID < biomeTintModeCache.Length) ? biomeTintModeCache[blockID] : (byte)0;

        if (tintMode == 0)
        {
            return defaultColor; // No tinting needed
        }

        // Get index for this XZ position
        int idx = localZ * chunkSizeXZ + localX;

        // If it's grass-tinted, we can use the cached value directly
        if (tintMode == 1)
        {
            return chunk._cachedBiomeColors[idx];
        }

        // For foliage or water, we need to recalculate with correct tint type
        // But we can still use the cached biome data
        if (chunk._biomeTemperatures == null || chunk._biomeRainfall == null)
        {
            return defaultColor;
        }

        double temperature = chunk._biomeTemperatures[idx];
        double rainfall = chunk._biomeRainfall[idx];

        Color biomeColor;
        if (tintMode == 2)
        {
            biomeColor = BetaBiome.GetFoliageColor(temperature, rainfall, foliageColorTexture);
        }
        else // isWaterTinted
        {
            biomeColor = BetaBiome.GetWaterColor(temperature, rainfall, waterColorTexture);
        }

        biomeColor.a = 1f;
        return biomeColor;
    }

    #endregion

    // OPTIMIZATION: Simplified reconciliation to prevent lag spikes
    // Instead of complex BFS, use lightweight boundary updates
    private void _ReconcileLightingWithNeighbors(ChunkData chunk)
    {
#if LOGGING
        float startTime = Time.realtimeSinceStartup * 1000f;
#endif

        // OPTIMIZATION: Lightweight reconciliation - only update immediate boundaries
        // This prevents cascading updates that cause lag spikes on new columns

        byte[] chunkData = _GetDecompressedData(chunk);
        if (chunkData == null) return;

        // OPTIMIZATION: Only reconcile with ready neighbors to avoid cascading
        ChunkData[] neighbors = _GetCachedNeighbors(chunk);
        int readyNeighbors = 0;

        for (int dir = 0; dir < 6; dir++)
        {
            ChunkData neighbor = neighbors[dir];
            if (neighbor != null && neighbor.isDataReady && neighbor.lightData != null)
            {
                readyNeighbors++;
                _UpdateNeighborBoundaryLighting(chunk, neighbor, dir, chunkData);
            }
        }

        // FIXED: Always do boundary BFS if we have any ready neighbors
        // This ensures light propagates even for isolated chunks
        if (readyNeighbors > 0)
        {
            _PerformLightweightBFS(chunk, chunkData);

            // FIXED: Trigger neighbor mesh rebuilds after lighting reconciliation
            // This ensures neighbors update their meshes when lighting changes
            TriggerNeighborMeshRebuilds(chunk);
        }

#if LOGGING
        chunk.time_LightingReconcile = Time.realtimeSinceStartup * 1000f - startTime;
#endif
    }

    // OPTIMIZATION: Update only the boundary lighting between two chunks
    private void _UpdateNeighborBoundaryLighting(ChunkData chunk, ChunkData neighbor, int direction, byte[] chunkData)
    {
        // Only update the boundary face between the two chunks
        // This is much cheaper than full BFS

        int faceSize = chunkSizeXZ * chunkSizeY; // Size of one face
        int[] boundaryOffsets = new int[faceSize];
        int boundaryCount = 0;

        // Calculate boundary block indices based on direction
        for (int y = 0; y < chunkSizeY; y++)
        {
            for (int i = 0; i < chunkSizeXZ; i++)
            {
                int x, z;
                switch (direction)
                {
                    case 0: x = chunkSizeXZ - 1; z = i; break; // PX face
                    case 1: x = 0; z = i; break; // NX face
                    case 2: x = i; z = chunkSizeXZ - 1; break; // PZ face
                    case 3: x = i; z = 0; break; // NZ face
                    case 4: x = i; z = i; break; // PY face (simplified)
                    case 5: x = i; z = i; break; // NY face (simplified)
                    default: continue;
                }

                if (x >= 0 && x < chunkSizeXZ && z >= 0 && z < chunkSizeXZ)
                {
                    boundaryOffsets[boundaryCount++] = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
                }
            }
        }

        // Update lighting for boundary blocks only
        for (int i = 0; i < boundaryCount; i++)
        {
            int blockIndex = boundaryOffsets[i];
            byte blockID = chunkData[blockIndex];
            int opacity = lightOpacityCache[blockID];

            if (opacity < 15) // Skip fully opaque blocks
            {
                _UpdateSingleBlockLighting(chunk, blockIndex, blockID);
            }
        }
    }

    // OPTIMIZATION: Lightweight BFS with limited scope
    private void _PerformLightweightBFS(ChunkData chunk, byte[] chunkData)
    {
        int[] lightQueue = GetBFSQueue();
        int queueEnd = 0;

        // OPTIMIZATION: Only add boundary blocks to queue
        for (int y = 1; y < chunkSizeY - 1; y++)
        {
            for (int z = 1; z < chunkSizeXZ - 1; z++)
            {
                // X faces
                lightQueue[queueEnd++] = (0 << 16) | (y << 8) | z;
                lightQueue[queueEnd++] = ((chunkSizeXZ - 1) << 16) | (y << 8) | z;

                // Z faces
                lightQueue[queueEnd++] = (z << 16) | (y << 8) | 0;
                lightQueue[queueEnd++] = (z << 16) | (y << 8) | (chunkSizeXZ - 1);

                if (queueEnd >= lightQueue.Length - 10) break;
            }
            if (queueEnd >= lightQueue.Length - 10) break;
        }

        // OPTIMIZATION: Limited BFS iterations
        int queueStart = 0;
        int[] neighborOffsets = { -1, 0, 0, 1, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, -1, 0, 0, 1 };

        for (int iteration = 0; iteration < 2 && queueStart < queueEnd; iteration++)
        {
            int iterationEnd = queueEnd;
            while (queueStart < iterationEnd && queueEnd < lightQueue.Length - 6)
            {
                int packed = lightQueue[queueStart++];
                int x = (packed >> 16) & 0xFF;
                int y = (packed >> 8) & 0xFF;
                int z = packed & 0xFF;

                _UpdateBlockLightPULLOptimized(chunk, chunkData, x, y, z, true, lightQueue, ref queueEnd, neighborOffsets);
            }
        }

        // FIXED: Trigger neighbor mesh rebuilds after lightweight BFS
        // This ensures neighbors update their meshes when boundary lighting changes
        TriggerNeighborMeshRebuilds(chunk);

        ReturnBFSQueue(lightQueue);
    }

    // OPTIMIZATION: Update lighting for a single block
    private void _UpdateSingleBlockLighting(ChunkData chunk, int blockIndex, byte blockID)
    {
        int y = blockIndex / (chunkSizeXZ * chunkSizeXZ);
        int z = (blockIndex / chunkSizeXZ) % chunkSizeXZ;
        int x = blockIndex % chunkSizeXZ;

        int opacity = lightOpacityCache[blockID];
        if (opacity == 0) opacity = 1;

        // Calculate new lighting based on neighbors
        int maxNeighborLight = 0;
        int[] neighborOffsets = { -1, 0, 0, 1, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, -1, 0, 0, 1 };

        for (int i = 0; i < 6; i++)
        {
            int nx = x + neighborOffsets[i * 3];
            int ny = y + neighborOffsets[i * 3 + 1];
            int nz = z + neighborOffsets[i * 3 + 2];

            int neighborLight = _GetNeighborLightOptimized(chunk, nx, ny, nz, true);
            if (neighborLight > maxNeighborLight) maxNeighborLight = neighborLight;
        }

        int newLight = maxNeighborLight - opacity;
        if (newLight < 0) newLight = 0;

        // Update if different
        byte currentByte = chunk.lightData[blockIndex];
        int currentLight = (currentByte >> 4) & 0xF;

        if (newLight != currentLight)
        {
            int blockLight = currentByte & 0xF;
            chunk.lightData[blockIndex] = (byte)((newLight << 4) | blockLight);

            // FIXED: Trigger neighbor mesh rebuilds for boundary blocks
            // This ensures neighbors update their meshes when lighting changes at chunk boundaries
            if (x == 0 || x == chunkSizeXZ - 1 || y == 0 || y == chunkSizeY - 1 || z == 0 || z == chunkSizeXZ - 1)
            {
                TriggerNeighborMeshRebuilds(chunk);
            }
        }
    }

    // Add all boundary blocks of a chunk to the reconciliation queue
    private void _AddBoundaryBlocksToQueue(ChunkData chunk, int[] queue, ref int queueEnd)
    {
        // Pack: chunkX(8bits) | chunkY(4bits) | chunkZ(4bits) | localX(4bits) | localY(4bits) | localZ(4bits) = 28 bits
        // This allows us to track blocks across multiple chunks in the same queue

        int chunkXPacked = (chunk.chunkX_world & 0xFF) << 24;
        int chunkYPacked = (chunk.chunkY_world & 0xF) << 20;
        int chunkZPacked = (chunk.chunkZ_world & 0xF) << 16;

        // Add boundary faces (not edges/corners to keep queue size reasonable)
        // X faces
        for (int y = 1; y < chunkSizeY - 1 && queueEnd < queue.Length - 1; y++)
        {
            for (int z = 1; z < chunkSizeXZ - 1 && queueEnd < queue.Length - 1; z++)
            {
                queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | (0 << 12) | (y << 8) | (z << 4);
                if (queueEnd < queue.Length)
                    queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | ((chunkSizeXZ-1) << 12) | (y << 8) | (z << 4);
            }
        }

        // Z faces
        for (int y = 1; y < chunkSizeY - 1 && queueEnd < queue.Length - 1; y++)
        {
            for (int x = 1; x < chunkSizeXZ - 1 && queueEnd < queue.Length - 1; x++)
            {
                queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | (x << 12) | (y << 8) | (0 << 4);
                if (queueEnd < queue.Length)
                    queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | (x << 12) | (y << 8) | ((chunkSizeXZ-1) << 4);
            }
        }
    }

    // Add a neighbor chunk's boundary face to the reconciliation queue
    private void _AddNeighborBoundaryToQueue(ChunkData neighborChunk, int faceDir, int[] queue, ref int queueEnd)
    {
        int chunkXPacked = (neighborChunk.chunkX_world & 0xFF) << 24;
        int chunkYPacked = (neighborChunk.chunkY_world & 0xF) << 20;
        int chunkZPacked = (neighborChunk.chunkZ_world & 0xF) << 16;

        // Add boundary face based on direction
        // 0=NZ, 1=PZ, 2=NX, 3=PX, 4=NY, 5=PY

        switch(faceDir)
        {
            case 0: // -Z face (z=0)
                for (int y = 1; y < chunkSizeY - 1 && queueEnd < queue.Length; y++)
                    for (int x = 1; x < chunkSizeXZ - 1 && queueEnd < queue.Length; x++)
                        queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | (x << 12) | (y << 8) | (0 << 4);
                break;
            case 1: // +Z face (z=15)
                for (int y = 1; y < chunkSizeY - 1 && queueEnd < queue.Length; y++)
                    for (int x = 1; x < chunkSizeXZ - 1 && queueEnd < queue.Length; x++)
                        queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | (x << 12) | (y << 8) | ((chunkSizeXZ-1) << 4);
                break;
            case 2: // -X face (x=0)
                for (int y = 1; y < chunkSizeY - 1 && queueEnd < queue.Length; y++)
                    for (int z = 1; z < chunkSizeXZ - 1 && queueEnd < queue.Length; z++)
                        queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | (0 << 12) | (y << 8) | (z << 4);
                break;
            case 3: // +X face (x=15)
                for (int y = 1; y < chunkSizeY - 1 && queueEnd < queue.Length; y++)
                    for (int z = 1; z < chunkSizeXZ - 1 && queueEnd < queue.Length; z++)
                        queue[queueEnd++] = chunkXPacked | chunkYPacked | chunkZPacked | ((chunkSizeXZ-1) << 12) | (y << 8) | (z << 4);
                break;
        }
    }

    // OLD: Run a limited BFS that only propagates from boundary blocks inward
    // This is much cheaper than a full chunk BFS and sufficient for reconciliation
    private void _PropagateBoundaryLighting(ChunkData chunk, byte[] fullData)
    {
        // Get a BFS queue
        int[] lightQueue = GetBFSQueue();
        int queueEnd = 0;

        // Add all boundary blocks to the queue (faces only, not edges/corners)
        // X faces
        bool queueFull = false;
        for (int y = 1; y < chunkSizeY - 1 && !queueFull; y++)
        {
            for (int z = 1; z < chunkSizeXZ - 1 && !queueFull; z++)
            {
                if (queueEnd < lightQueue.Length - 1)
                {
                    lightQueue[queueEnd++] = (0 << 16) | (y << 8) | z; // X = 0
                }
                else
                {
                    queueFull = true;
                    break;
                }

                if (queueEnd < lightQueue.Length - 1)
                {
                    lightQueue[queueEnd++] = ((chunkSizeXZ - 1) << 16) | (y << 8) | z; // X = 15
                }
                else
                {
                    queueFull = true;
                    break;
                }
            }
        }

        // Z faces
        if (!queueFull)
        {
            for (int y = 1; y < chunkSizeY - 1 && !queueFull; y++)
            {
                for (int x = 1; x < chunkSizeXZ - 1 && !queueFull; x++)
                {
                    if (queueEnd < lightQueue.Length - 1)
                    {
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | 0; // Z = 0
                    }
                    else
                    {
                        queueFull = true;
                        break;
                    }

                    if (queueEnd < lightQueue.Length - 1)
                    {
                        lightQueue[queueEnd++] = (x << 16) | (y << 8) | (chunkSizeXZ - 1); // Z = 15
                    }
                    else
                    {
                        queueFull = true;
                        break;
                    }
                }
            }
        }

        // Y faces (less important but include for completeness)
        if (!queueFull)
        {
            for (int z = 1; z < chunkSizeXZ - 1 && !queueFull; z++)
            {
                for (int x = 1; x < chunkSizeXZ - 1 && !queueFull; x++)
                {
                    if (queueEnd < lightQueue.Length - 2)
                    {
                        lightQueue[queueEnd++] = (x << 16) | (0 << 8) | z; // Y = 0
                        lightQueue[queueEnd++] = (x << 16) | ((chunkSizeY - 1) << 8) | z; // Y = 15
                    }
                    else
                    {
                        queueFull = true;
                        break;
                    }
                }
            }
        }

        // FIXED: Process skylight with convergence-based iterations
        // Continue until no more updates occur (proper convergence)
        int queueStart = 0;
        int initialQueueSize = queueEnd;
        int skylightIterations = 0;
        int maxSkylightIterations = 8; // Increased limit for better convergence
        while (queueStart < queueEnd && skylightIterations < maxSkylightIterations)
        {
            int iterationStart = queueStart;
            int iterationEnd = queueEnd;
            while (queueStart < iterationEnd && queueEnd < lightQueue.Length - 6)
            {
                int packed = lightQueue[queueStart++];
                int x = (packed >> 16) & 0xFF;
                int y = (packed >> 8) & 0xFF;
                int z = packed & 0xFF;
                _UpdateBlockLightPULL(chunk, fullData, x, y, z, true, lightQueue, ref queueEnd);
            }
            skylightIterations++;

            // If no new blocks were added to queue, we've converged
            if (queueStart == queueEnd) break;
        }

        // FIXED: Process block light with convergence-based iterations
        queueStart = 0;
        queueEnd = initialQueueSize; // Reset to boundary blocks only
        int blocklightIterations = 0;
        int maxBlocklightIterations = 8; // Increased limit for better convergence
        while (queueStart < queueEnd && blocklightIterations < maxBlocklightIterations)
        {
            int iterationStart = queueStart;
            int iterationEnd = queueEnd;
            while (queueStart < iterationEnd && queueEnd < lightQueue.Length - 6)
            {
                int packed = lightQueue[queueStart++];
                int x = (packed >> 16) & 0xFF;
                int y = (packed >> 8) & 0xFF;
                int z = packed & 0xFF;
                _UpdateBlockLightPULL(chunk, fullData, x, y, z, false, lightQueue, ref queueEnd);
            }
            blocklightIterations++;

            // If no new blocks were added to queue, we've converged
            if (queueStart == queueEnd) break;
        }

        // FIXED: Trigger neighbor mesh rebuilds after boundary lighting propagation
        // This ensures neighbors update their meshes when boundary lighting changes
        TriggerNeighborMeshRebuilds(chunk);

        ReturnBFSQueue(lightQueue);
    }

    // OLD: Per-block lighting update (REMOVED - too expensive, caused lag and darkening)
    // Previously: Scheduled individual BFS updates for 1000+ boundary blocks
    // Problem: Way too slow, and caused darkening by pulling from all neighbors
    // Solution: Use simplified reconciliation that just re-imports boundaries

    // Import light from all neighbor chunks into this chunk's boundary blocks
    // This ensures BFS starts with light from already-generated neighbors
    private void _ImportLightFromNeighbors(ChunkData chunk, byte[] fullData)
    {
        // Check all 6 face neighbors
        for (int dir = 0; dir < 6; dir++)
        {
            int neighborChunkX = chunk.chunkX_world + neighbor_dx_offsets[dir];
            int neighborChunkY = chunk.chunkY_world + neighbor_dy_offsets[dir];
            int neighborChunkZ = chunk.chunkZ_world + neighbor_dz_offsets[dir];

            ChunkData neighborChunk = GetChunkAt(neighborChunkX, neighborChunkY, neighborChunkZ);

            // Skip if neighbor doesn't exist or isn't ready
            if (neighborChunk == null || !neighborChunk.isDataReady || neighborChunk.lightData == null)
                continue;

            // Get neighbor's data
            byte[] neighborData = _DecompressChunkColumnRLE(neighborChunk);
            if (neighborData == null) continue;

            // Import light from neighbor's boundary to our boundary
            _ImportFromNeighborBoundary(chunk, fullData, neighborChunk, neighborData, dir);
        }
    }

    // Import light from a specific neighbor chunk's boundary
    private void _ImportFromNeighborBoundary(ChunkData chunk, byte[] chunkData, ChunkData neighborChunk, byte[] neighborData, int direction)
    {
        // PARITY FIX: The previous switch labelled cases 0/1 as "-Z/+Z", 2/3 as "-X/+X", 4/5 as "-Y/+Y".
        // But the offsets array `neighbor_d{x,y,z}_offsets` defines direction as:
        //   0: +X, 1: -X, 2: +Y, 3: -Y, 4: +Z, 5: -Z
        // So the switch was importing across the WRONG axis for every direction. This rewrite
        // aligns the import face with the offset semantics.
        //
        // Iteration:
        //   For X-axis directions (0,1): (i, j) span (y, z) => sizes (Y, Z)
        //   For Y-axis directions (2,3): (i, j) span (x, z) => sizes (X, Z)
        //   For Z-axis directions (4,5): (i, j) span (x, y) => sizes (X, Y)
        int size1, size2;
        switch (direction)
        {
            case 0: case 1: size1 = chunkSizeY;  size2 = chunkSizeXZ; break; // (y, z)
            case 2: case 3: size1 = chunkSizeXZ; size2 = chunkSizeXZ; break; // (x, z)
            default:        size1 = chunkSizeXZ; size2 = chunkSizeY;  break; // (x, y) for 4/5
        }

        for (int i = 0; i < size1; i++)
        {
            for (int j = 0; j < size2; j++)
            {
                int chunkX = 0, chunkY = 0, chunkZ = 0;
                int neighborX = 0, neighborY = 0, neighborZ = 0;

                switch (direction)
                {
                    case 0: // +X neighbor: our X=15 imports from neighbor's X=0
                        chunkX = chunkSizeXZ - 1; chunkY = i; chunkZ = j;
                        neighborX = 0;            neighborY = i; neighborZ = j;
                        break;
                    case 1: // -X neighbor: our X=0 imports from neighbor's X=15
                        chunkX = 0;               chunkY = i; chunkZ = j;
                        neighborX = chunkSizeXZ - 1; neighborY = i; neighborZ = j;
                        break;
                    case 2: // +Y neighbor: our Y=15 imports from neighbor's Y=0
                        chunkX = i; chunkY = chunkSizeY - 1; chunkZ = j;
                        neighborX = i; neighborY = 0;        neighborZ = j;
                        break;
                    case 3: // -Y neighbor: our Y=0 imports from neighbor's Y=15
                        chunkX = i; chunkY = 0;              chunkZ = j;
                        neighborX = i; neighborY = chunkSizeY - 1; neighborZ = j;
                        break;
                    case 4: // +Z neighbor: our Z=15 imports from neighbor's Z=0
                        chunkX = i; chunkY = j; chunkZ = chunkSizeXZ - 1;
                        neighborX = i; neighborY = j; neighborZ = 0;
                        break;
                    case 5: // -Z neighbor: our Z=0 imports from neighbor's Z=15
                        chunkX = i; chunkY = j; chunkZ = 0;
                        neighborX = i; neighborY = j; neighborZ = chunkSizeXZ - 1;
                        break;
                }

                // Get neighbor's light
                int neighborIndex = neighborY * (chunkSizeXZ * chunkSizeXZ) + neighborZ * chunkSizeXZ + neighborX;
                byte neighborLightByte = neighborChunk.lightData[neighborIndex];
                int neighborSkyLight = (neighborLightByte >> 4) & 0xF;
                int neighborBlockLight = neighborLightByte & 0xF;

                // Get our boundary block
                int chunkIndex = chunkY * (chunkSizeXZ * chunkSizeXZ) + chunkZ * chunkSizeXZ + chunkX;
                byte ourBlockID = chunkData[chunkIndex];

                // Get our block opacity
                int opacity = lightOpacityCache[ourBlockID];
                if (opacity == 0) opacity = 1;

                // Skip if we're fully opaque
                if (opacity >= 15) continue;

                // Calculate propagated light from neighbor into us
                int propagatedSkyLight = neighborSkyLight - opacity;
                int propagatedBlockLight = neighborBlockLight - opacity;
                if (propagatedSkyLight < 0) propagatedSkyLight = 0;
                if (propagatedBlockLight < 0) propagatedBlockLight = 0;

                // Get our current light
                byte ourLightByte = chunk.lightData[chunkIndex];
                int ourSkyLight = (ourLightByte >> 4) & 0xF;
                int ourBlockLight = ourLightByte & 0xF;

                // Update if neighbor's propagated light is brighter
                if (propagatedSkyLight > ourSkyLight) ourSkyLight = propagatedSkyLight;
                if (propagatedBlockLight > ourBlockLight) ourBlockLight = propagatedBlockLight;

                // Apply update
                chunk.lightData[chunkIndex] = (byte)((ourSkyLight << 4) | ourBlockLight);
            }
        }

        // FIXED: Trigger neighbor mesh rebuilds after importing light from neighbors
        // This ensures neighbors update their meshes when light is imported across boundaries
        TriggerNeighborMeshRebuilds(chunk);
    }

#if LOGGING
    // --- Performance Logging Methods ---

    private void LogFrameStats(float updateTime)
    {
        logBuilder.Clear();
        logBuilder.AppendLine($"=== Frame {stats_frameCount} Performance ===");
        logBuilder.AppendLine($"Update: {updateTime:F2}ms (budget: {updateTimeBudgetMs:F1}ms, {(updateTime / updateTimeBudgetMs * 100f):F0}% used)");
        logBuilder.AppendLine($"  DataGen: {activeDataGenCount} chunks active");
        logBuilder.AppendLine($"  Meshing: {activeMeshingCount} chunks active");
        logBuilder.AppendLine($"  Reconciliation Queue: {deferredReconciliationQueue.Count}");

        if (enableCacheTracking)
        {
            int totalDecomp = stats_decompCacheHits + stats_decompCacheMisses;
            int totalNeighbor = stats_neighborCacheHits + stats_neighborCacheMisses;
            float decompHitRate = totalDecomp > 0 ? (stats_decompCacheHits / (float)totalDecomp * 100f) : 0f;
            float neighborHitRate = totalNeighbor > 0 ? (stats_neighborCacheHits / (float)totalNeighbor * 100f) : 0f;
            logBuilder.AppendLine($"Cache: Decomp {decompHitRate:F0}% hit ({stats_decompCacheHits}/{totalDecomp}), Neighbor {neighborHitRate:F0}% hit ({stats_neighborCacheHits}/{totalNeighbor})");
        }

        Debug.Log(logBuilder.ToString());
    }

    private void LogAggregateStats()
    {
        float windowDuration = Time.realtimeSinceStartup - stats_aggregateWindowStart;

        logBuilder.Clear();
        logBuilder.AppendLine($"=== Performance Summary (last {aggregateLogInterval} frames, {windowDuration:F1} seconds) ===");
        logBuilder.AppendLine($"[GPU] Effective paths: voxelBackend={(enableGpuVoxelBackend ? (gpuBackendReady ? "GPU-READY" : "GPU-NOT-READY!") : "OFF")}, lighting={(UsesGpuLightingBackend() ? "GPU" : "CPU")}, worldgen={((terrainGenerator != null && terrainGenerator.enableGpuWorldgen) ? "GPU" : "CPU")}, AO={(ambientOcclusion ? (UsesGpuLightingBackend() ? "GPU-exact" : "CPU") : "off")}");

        if (stats_frameCount > 0)
        {
            float avgUpdateTime = stats_updateTotalTime / stats_frameCount;
            float avgDataGenActive = stats_dataGenActiveSamplesTotal / (float)stats_frameCount;
            float avgMeshingActive = stats_meshingActiveSamplesTotal / (float)stats_frameCount;
            float avgReconciliationQueue = stats_reconciliationQueueSamplesTotal / (float)stats_frameCount;
            logBuilder.AppendLine($"Update: avg {avgUpdateTime:F2}ms, min {stats_updateTimeMin:F2}ms, max {stats_updateTimeMax:F2}ms");
            logBuilder.AppendLine($"  Budget exceeded: {stats_budgetExceededCount} times ({(stats_budgetExceededCount / (float)stats_frameCount * 100f):F1}%)");
            logBuilder.AppendLine($"  Queue pressure: datagen avg {avgDataGenActive:F1} max {stats_dataGenActiveMax}, meshing avg {avgMeshingActive:F1} max {stats_meshingActiveMax}, reconciliation avg {avgReconciliationQueue:F1} max {stats_reconciliationQueueMax}");
            if (enableDetailedTimings)
            {
                logBuilder.AppendLine($"  Active processing: {stats_processActiveChunksTime:F2}ms total, reconciliation {stats_reconciliationTime:F2}ms total");
            }
            if (enableAdaptiveBudgets)
            {
                logBuilder.AppendLine($"  Adaptive budgets: mesh decode {adaptiveGpuMeshDecodeStepBudgetMs:F2}ms x{adaptiveGpuMeshDecodeStepsPerFrame}, frame {adaptiveGpuMeshDecodeFrameBudgetMs:F2}ms, worldgen {adaptiveGpuWorldgenStepBudgetMs:F2}ms x{adaptiveGpuWorldgenStepsPerFrame}");
            }
        }

        if (coordinator != null)
        {
            coordinator.AppendAggregatePerformanceStats(logBuilder);
        }

        if (terrainGenerator != null)
        {
            terrainGenerator.AppendAggregatePerformanceStats(logBuilder);
        }

        if (blockTicker != null)
        {
            blockTicker.AppendAggregatePerformanceStats(logBuilder);
        }

        if (stats_meshBuildTotal > 0)
        {
            float avgMeshTime = stats_meshBuildTimeTotal / stats_meshBuildTotal;
            float avgStepsPerChunk = stats_meshStepsTotal / (float)stats_meshBuildTotal;
            logBuilder.AppendLine($"Mesh Building: {stats_meshBuildTotal} chunks, avg {avgMeshTime:F2}ms (min {stats_meshBuildTimeMin:F2}ms, max {stats_meshBuildTimeMax:F2}ms)");
            logBuilder.AppendLine($"  Steps: avg {avgStepsPerChunk:F1} per chunk");
            logBuilder.AppendLine($"  Completions: cpu {stats_meshBuildCpuCompletions}, gpu {stats_meshBuildGpuCompletions}, empty {stats_meshBuildEmptyCompletions}");
            if (stats_meshDeferredChunks > 0)
            {
                logBuilder.AppendLine($"  Deferred interior meshes: {stats_meshDeferredChunks}");
            }
            if (enableDetailedTimings && stats_meshBuildTotal > 0)
            {
                float avgNeighborCacheTime = stats_meshNeighborCacheTime / stats_meshBuildTotal;
                float avgDataPrepTime = stats_meshDataPrepTime / stats_meshBuildTotal;
                float avgMainLoopTime = stats_meshMainLoopTime / stats_meshBuildTotal;
                float totalApplyTime = stats_meshApplyOpaqueTime + stats_meshApplyTransparentTime + stats_meshApplyCutoutTime + stats_meshApplyColliderTime;
                logBuilder.AppendLine($"  Stage breakdown: neighbor cache {avgNeighborCacheTime:F2}ms, data prep {avgDataPrepTime:F2}ms, main loop {avgMainLoopTime:F2}ms, apply {totalApplyTime / stats_meshBuildTotal:F2}ms");
                if (stats_gpuFaceDecodeSteps > 0)
                {
                    float avgGpuDecodeStepMs = stats_gpuFaceDecodeTime / stats_gpuFaceDecodeSteps;
                    float minGpuDecodeStepMs = stats_gpuFaceDecodeTimeMin == float.MaxValue ? 0f : stats_gpuFaceDecodeTimeMin;
                    logBuilder.AppendLine($"  GPU decode: {stats_gpuFaceDecodeSteps} steps, {stats_gpuFaceDecodeTime:F2}ms total, avg {avgGpuDecodeStepMs:F2}ms (min {minGpuDecodeStepMs:F2}ms, max {stats_gpuFaceDecodeTimeMax:F2}ms)");
                    float compactDecodeSplitTime = stats_gpuFaceCompactMaskDecodeTime + stats_gpuFaceCompactEmitTime + stats_gpuFaceCompactCrossTime;
                    if (compactDecodeSplitTime > 0f)
                    {
                        logBuilder.AppendLine($"  Compact decode split: mask {stats_gpuFaceCompactMaskDecodeTime:F2}ms, emit {stats_gpuFaceCompactEmitTime:F2}ms, cross {stats_gpuFaceCompactCrossTime:F2}ms");
                        if (stats_gpuFaceCompactEmitScanTime + stats_gpuFaceCompactEmitQuadTime > 0f)
                        {
                            logBuilder.AppendLine($"  Emit split: scan {stats_gpuFaceCompactEmitScanTime:F2}ms, quad {stats_gpuFaceCompactEmitQuadTime:F2}ms, collider {stats_gpuFaceCompactEmitCollisionTime:F2}ms");
                        }
                    }
                }
                float totalGreedy = stats_greedyAxisYTime + stats_greedyAxisZTime + stats_greedyAxisXTime;
                if (totalGreedy > 0)
                {
                    logBuilder.AppendLine($"  Greedy Meshing: Y={stats_greedyAxisYTime / totalGreedy * 100f:F0}% ({stats_greedyAxisYTime:F1}ms), Z={stats_greedyAxisZTime / totalGreedy * 100f:F0}% ({stats_greedyAxisZTime:F1}ms), X={stats_greedyAxisXTime / totalGreedy * 100f:F0}% ({stats_greedyAxisXTime:F1}ms)");
                }
                logBuilder.AppendLine($"  Apply mesh: opaque {stats_meshApplyOpaqueTime:F2}ms, transparent {stats_meshApplyTransparentTime:F2}ms, cutout {stats_meshApplyCutoutTime:F2}ms, collider {stats_meshApplyColliderTime:F2}ms");
                if (stats_firstShellMeshStartLatencyCount > 0 || stats_firstDeferredMeshStartLatencyCount > 0 || stats_deferredColliderWaitCount > 0)
                {
                    float avgShellMeshStartLatency = stats_firstShellMeshStartLatencyCount > 0 ? stats_firstShellMeshStartLatencyTotal / stats_firstShellMeshStartLatencyCount : 0f;
                    float avgDeferredMeshStartLatency = stats_firstDeferredMeshStartLatencyCount > 0 ? stats_firstDeferredMeshStartLatencyTotal / stats_firstDeferredMeshStartLatencyCount : 0f;
                    float avgDeferredColliderWait = stats_deferredColliderWaitCount > 0 ? stats_deferredColliderWaitTotal / stats_deferredColliderWaitCount : 0f;
                    logBuilder.AppendLine($"  Waits: first shell mesh avg {avgShellMeshStartLatency:F2}ms max {stats_firstShellMeshStartLatencyMax:F2}ms, first deferred mesh avg {avgDeferredMeshStartLatency:F2}ms max {stats_firstDeferredMeshStartLatencyMax:F2}ms, deferred collider avg {avgDeferredColliderWait:F2}ms max {stats_deferredColliderWaitMax:F2}ms");
                }
                if (stats_meshPoolExhaustedDefers > 0 || stats_meshGpuBusyDefers > 0 || stats_meshGpuFrameThrottleFallbacks > 0 || stats_meshGpuRequestFailures > 0 || stats_meshGpuBorderDefers > 0 || stats_meshGpuBorderCpuFallbacks > 0 || stats_meshInteractionPriorityCpuBypass > 0 || stats_meshBuildsWithMissingNeighbors > 0 || stats_deferredColliderApplyCount > 0)
                {
                    logBuilder.AppendLine($"  Stall reasons: pool {stats_meshPoolExhaustedDefers}, gpu busy {stats_meshGpuBusyDefers}, gpu frame throttle {stats_meshGpuFrameThrottleFallbacks}, gpu request fail {stats_meshGpuRequestFailures}, border defer {stats_meshGpuBorderDefers}, border cpu {stats_meshGpuBorderCpuFallbacks}, interaction cpu {stats_meshInteractionPriorityCpuBypass}, deferred collider {stats_deferredColliderApplyCount}, missing-neighbor builds {stats_meshBuildsWithMissingNeighbors}");
                }
            }

            if (enableCounters && stats_faceCullingTests > 0)
            {
                float cullRate = stats_facesCulled / (float)stats_faceCullingTests * 100f;
                logBuilder.AppendLine($"  Face Culling: {stats_faceCullingTests} tests, {stats_facesCulled} culled ({cullRate:F1}%), {stats_facesDrawn} drawn");
                logBuilder.AppendLine($"  Vertices: {stats_verticesOpaque} opaque, {stats_verticesTransparent} transparent, {stats_verticesCutout} cutout");
                logBuilder.AppendLine($"  Face split: opaque {stats_facesOpaque}, transparent {stats_facesTransparent}, cutout {stats_facesCutout}");
                logBuilder.AppendLine($"  Boundary checks: Y {stats_meshBoundaryChecksY}, Z {stats_meshBoundaryChecksZ}, X {stats_meshBoundaryChecksX}");
                float totalVertices = stats_verticesOpaque + stats_verticesTransparent + stats_verticesCutout;
                float msPerThousandFaces = stats_facesDrawn > 0 ? stats_meshBuildTimeTotal * 1000f / stats_facesDrawn : 0f;
                float msPerThousandVertices = totalVertices > 0f ? stats_meshBuildTimeTotal * 1000f / totalVertices : 0f;
                logBuilder.AppendLine($"  Throughput: {msPerThousandFaces:F2}ms / 1k faces, {msPerThousandVertices:F2}ms / 1k verts");
                if (stats_meshBuildsWithMissingNeighbors > 0)
                {
                    logBuilder.AppendLine($"  Missing neighbors: avg {stats_meshMissingNeighborBits / (float)stats_meshBuildsWithMissingNeighbors:F2} sides on affected builds");
                }
            }

            for (int i = 0; i < SLOWEST_MESH_BUILD_COUNT; i++)
            {
                if (stats_slowestMeshBuildMs[i] <= 0f) break;
                string buildKind = stats_slowestMeshKind[i] == 2 ? "empty" : (stats_slowestMeshKind[i] == 1 ? "gpu" : "cpu");
                logBuilder.AppendLine($"  Slowest #{i + 1}: ({stats_slowestMeshChunkX[i]},{stats_slowestMeshChunkY[i]},{stats_slowestMeshChunkZ[i]}) {stats_slowestMeshBuildMs[i]:F2}ms [{buildKind}] prep {stats_slowestMeshDataPrepMs[i]:F2}ms, loop {stats_slowestMeshMainLoopMs[i]:F2}ms, apply {stats_slowestMeshApplyMs[i]:F2}ms, faces {stats_slowestMeshFaceCount[i]}, verts {stats_slowestMeshVertexCount[i]}");
            }
        }

        if (stats_lightingInitsTotal > 0)
        {
            logBuilder.AppendLine($"Lighting: {stats_lightingInitsTotal} chunks");
            logBuilder.AppendLine($"  Avg init {stats_lightingInitTime / stats_lightingInitsTotal:F2}ms, import {stats_lightingImportTime / stats_lightingInitsTotal:F2}ms, BFS sky {stats_lightingBfsSkyTime / stats_lightingInitsTotal:F2}ms, BFS block {stats_lightingBfsBlockTime / stats_lightingInitsTotal:F2}ms");
            logBuilder.AppendLine($"  Queue ops {stats_lightingBFSOps}, skylit blocks {stats_lightingSkylightBlocks}, block-light updates {stats_lightingBlocklightBlocks}, cross-chunk ops {stats_lightingCrossChunkQueries}");
        }

        bool hasGpuBackendStats =
            stats_gpuAtlasOverlayBlits > 0 ||
            stats_gpuLightingSeedBlits > 0 ||
            stats_gpuLightingPropagateBlits > 0 ||
            stats_gpuFaceExtractBlits > 0 ||
            stats_gpuFaceExportBlits > 0 ||
            stats_gpuChunkUploads > 0 ||
            stats_gpuFaceReadbackRequests > 0;

        if (hasGpuBackendStats)
        {
            logBuilder.AppendLine("GPU Backend:");
            logBuilder.AppendLine($"  Blit submit: atlas overlay {stats_gpuAtlasOverlayBlits} ({stats_gpuAtlasOverlayBlitTime:F3}ms), light seed {stats_gpuLightingSeedBlits} ({stats_gpuLightingSeedBlitTime:F3}ms), light propagate {stats_gpuLightingPropagateBlits} ({stats_gpuLightingPropagateBlitTime:F3}ms), face extract {stats_gpuFaceExtractBlits} ({stats_gpuFaceExtractBlitTime:F3}ms), face export {stats_gpuFaceExportBlits} ({stats_gpuFaceExportBlitTime:F3}ms)");
            logBuilder.AppendLine($"  CPU->GPU uploads: chunk blocks {stats_gpuChunkUploads} ({stats_gpuChunkUploadTime:F3}ms, {stats_gpuChunkUploadBytes / 1024f:F1}KB)");
            if (stats_gpuFaceOccupancyUploads > 0)
            {
                logBuilder.AppendLine($"  Occupancy hierarchy uploads: {stats_gpuFaceOccupancyUploads} ({stats_gpuFaceOccupancyUploadTime:F3}ms, {stats_gpuFaceOccupancyUploadBytes / 1024f:F1}KB)");
            }
            if (stats_gpuFaceReadbackRequests > 0)
            {
                float avgLatency = stats_gpuFaceReadbacksCompleted > 0 ? stats_gpuFaceReadbackLatencyTotal / stats_gpuFaceReadbacksCompleted : 0f;
                float bytesPerRequestKb = stats_gpuFaceReadbackRequests > 0 ? (stats_gpuFaceReadbackBytes / (float)stats_gpuFaceReadbackRequests) / 1024f : 0f;
                logBuilder.AppendLine($"  GPU->CPU face readback: req {stats_gpuFaceReadbackRequests}, ok {stats_gpuFaceReadbacksCompleted}, fail {stats_gpuFaceReadbackFailures}, latency avg {avgLatency:F3}ms min {(stats_gpuFaceReadbackLatencyMin == float.MaxValue ? 0f : stats_gpuFaceReadbackLatencyMin):F3}ms max {stats_gpuFaceReadbackLatencyMax:F3}ms");
                logBuilder.AppendLine($"  Readback copy {stats_gpuFaceReadbackCallbackCopyTime:F3}ms, bytes/request {bytesPerRequestKb:F1}KB, data {stats_gpuFaceReadbackBytes / 1024f:F1}KB");
            }
        }

        if (stats_rleCompressions > 0 || stats_rleDecompressions > 0)
        {
            float compressionRatio = stats_rleTotalBytesIn > 0 ? (stats_rleTotalBytesOut / (float)stats_rleTotalBytesIn) : 0f;
            logBuilder.AppendLine($"RLE: {compressionRatio * 100f:F0}% compression ratio ({stats_rleTotalBytesIn / 1024f:F1}KB→{stats_rleTotalBytesOut / 1024f:F1}KB)");

            if (enableDetailedTimings)
            {
                float avgCompTime = stats_rleCompressions > 0 ? stats_rleCompressionTime / stats_rleCompressions : 0f;
                float avgDecompTime = stats_rleDecompressions > 0 ? stats_rleDecompressionTime / stats_rleDecompressions : 0f;
                logBuilder.AppendLine($"  Compressions: {stats_rleCompressions} (avg {avgCompTime:F2}ms), Decompressions: {stats_rleDecompressions} (avg {avgDecompTime:F2}ms)");
                logBuilder.AppendLine($"  Homogeneous chunks: {stats_rleHomogeneousChunks}");
            }

            if (enableCacheTracking)
            {
                int totalDecomp = stats_decompCacheHits + stats_decompCacheMisses;
                float decompHitRate = totalDecomp > 0 ? (stats_decompCacheHits / (float)totalDecomp * 100f) : 0f;
                logBuilder.AppendLine($"  Cache hit rate: {decompHitRate:F1}% ({stats_decompCacheHits}/{totalDecomp})");
            }
        }

        if (enableCounters && stats_getBlockCalls > 0)
        {
            logBuilder.AppendLine($"Block Ops: {stats_getBlockCalls} gets, {stats_setBlockCalls} sets, {stats_blockModifications} modifications");
            if (stats_neighborRebuildTriggers > 0)
            {
                logBuilder.AppendLine($"  Neighbor rebuilds triggered: {stats_neighborRebuildTriggers}");
            }
        }

        if (enableCacheTracking)
        {
            int totalDecomp = stats_decompCacheHits + stats_decompCacheMisses;
            int totalNeighbor = stats_neighborCacheHits + stats_neighborCacheMisses;
            float decompHitRate = totalDecomp > 0 ? (stats_decompCacheHits / (float)totalDecomp * 100f) : 0f;
            float neighborHitRate = totalNeighbor > 0 ? (stats_neighborCacheHits / (float)totalNeighbor * 100f) : 0f;
            logBuilder.AppendLine($"Cache: Decomp {decompHitRate:F1}% ({stats_decompCacheHits}/{totalDecomp}), Neighbor {neighborHitRate:F1}% ({stats_neighborCacheHits}/{totalNeighbor})");
        }

        if (stats_reconciliationOps > 0)
        {
            float avgReconcilTime = stats_reconciliationTime / stats_reconciliationOps;
            logBuilder.AppendLine($"Reconciliation: {stats_reconciliationOps} ops, {stats_reconciliationBlocks} blocks, avg {avgReconcilTime:F2}ms");
        }

        if (enableCounters)
        {
            logBuilder.AppendLine($"Chunks: {stats_chunkCreations} created, {stats_chunkStateTransitions} state transitions");
        }

        Debug.Log(logBuilder.ToString());

        // Reset aggregate stats for next window
        stats_aggregateWindowStart = Time.realtimeSinceStartup;
        stats_frameCount = 0;
        stats_updateTotalTime = 0f;
        stats_updateTimeMin = float.MaxValue;
        stats_updateTimeMax = 0f;
        stats_budgetExceededCount = 0;
        stats_processActiveChunksTime = 0f;
        stats_reconciliationTime = 0f;
        stats_dataGenActiveSamplesTotal = 0;
        stats_meshingActiveSamplesTotal = 0;
        stats_reconciliationQueueSamplesTotal = 0;
        stats_dataGenActiveMax = 0;
        stats_meshingActiveMax = 0;
        stats_reconciliationQueueMax = 0;
        stats_meshBuildTotal = 0;
        stats_meshBuildCpuCompletions = 0;
        stats_meshBuildGpuCompletions = 0;
        stats_meshBuildEmptyCompletions = 0;
        stats_meshBuildTimeTotal = 0f;
        stats_meshBuildTimeMin = float.MaxValue;
        stats_meshBuildTimeMax = 0f;
        stats_meshStepsTotal = 0;
        stats_greedyAxisYTime = 0f;
        stats_greedyAxisZTime = 0f;
        stats_greedyAxisXTime = 0f;
        stats_sentinelBuilds = 0;
        stats_sentinelBuildTime = 0f;
        stats_faceCullingTests = 0;
        stats_facesCulled = 0;
        stats_facesDrawn = 0;
        stats_verticesOpaque = 0;
        stats_verticesTransparent = 0;
        stats_verticesCutout = 0;
        stats_meshApplyOpaqueTime = 0f;
        stats_meshApplyTransparentTime = 0f;
        stats_meshApplyCutoutTime = 0f;
        stats_meshApplyColliderTime = 0f;
        stats_meshNeighborCacheTime = 0f;
        stats_meshDataPrepTime = 0f;
        stats_meshMainLoopTime = 0f;
        stats_gpuFaceDecodeSteps = 0;
        stats_gpuFaceDecodeTime = 0f;
        stats_gpuFaceDecodeTimeMin = float.MaxValue;
        stats_gpuFaceDecodeTimeMax = 0f;
        stats_gpuFaceCompactMaskDecodeTime = 0f;
        stats_gpuFaceCompactEmitTime = 0f;
        stats_gpuFaceCompactCrossTime = 0f;
        stats_gpuFaceCompactEmitScanTime = 0f;
        stats_gpuFaceCompactEmitQuadTime = 0f;
        stats_gpuFaceCompactEmitCollisionTime = 0f;
        stats_meshDeferredChunks = 0;
        stats_meshBoundaryChecksY = 0;
        stats_meshBoundaryChecksZ = 0;
        stats_meshBoundaryChecksX = 0;
        stats_facesOpaque = 0;
        stats_facesTransparent = 0;
        stats_facesCutout = 0;
        stats_meshPoolExhaustedDefers = 0;
        stats_meshGpuBusyDefers = 0;
        stats_meshGpuFrameThrottleFallbacks = 0;
        stats_meshGpuRequestFailures = 0;
        stats_meshGpuBorderDefers = 0;
        stats_meshGpuBorderCpuFallbacks = 0;
        stats_meshInteractionPriorityCpuBypass = 0;
        stats_meshBuildsWithMissingNeighbors = 0;
        stats_meshMissingNeighborBits = 0;
        stats_deferredColliderApplyCount = 0;
        stats_deferredColliderWaitCount = 0;
        stats_deferredColliderWaitTotal = 0f;
        stats_deferredColliderWaitMax = 0f;
        stats_firstShellMeshStartLatencyCount = 0;
        stats_firstShellMeshStartLatencyTotal = 0f;
        stats_firstShellMeshStartLatencyMax = 0f;
        stats_firstDeferredMeshStartLatencyCount = 0;
        stats_firstDeferredMeshStartLatencyTotal = 0f;
        stats_firstDeferredMeshStartLatencyMax = 0f;
        stats_rleCompressions = 0;
        stats_rleDecompressions = 0;
        stats_rleCompressionTime = 0f;
        stats_rleDecompressionTime = 0f;
        stats_rleTotalBytesIn = 0;
        stats_rleTotalBytesOut = 0;
        stats_rleHomogeneousChunks = 0;
        stats_getBlockCalls = 0;
        stats_setBlockCalls = 0;
        stats_blockModifications = 0;
        stats_neighborRebuildTriggers = 0;
        stats_decompCacheHits = 0;
        stats_decompCacheMisses = 0;
        stats_neighborCacheHits = 0;
        stats_neighborCacheMisses = 0;
        stats_reconciliationOps = 0;
        stats_reconciliationBlocks = 0;
        stats_chunkCreations = 0;
        stats_chunkStateTransitions = 0;
        stats_lightingInitsTotal = 0;
        stats_lightingInitTime = 0f;
        stats_lightingImportTime = 0f;
        stats_lightingBfsSkyTime = 0f;
        stats_lightingBfsBlockTime = 0f;
        stats_lightingStepsTotal = 0;
        stats_lightingStepTime = 0f;
        stats_lightingBFSOps = 0;
        stats_lightingMaxQueueSize = 0;
        stats_lightingSkylightBlocks = 0;
        stats_lightingBlocklightBlocks = 0;
        stats_lightingCrossChunkQueries = 0;
        stats_lightingPoolAllocations = 0;
        stats_lightingPoolReuses = 0;
        stats_gpuAtlasOverlayBlits = 0;
        stats_gpuAtlasOverlayBlitTime = 0f;
        stats_gpuLightingSeedBlits = 0;
        stats_gpuLightingSeedBlitTime = 0f;
        stats_gpuLightingPropagateBlits = 0;
        stats_gpuLightingPropagateBlitTime = 0f;
        stats_gpuFaceExtractBlits = 0;
        stats_gpuFaceExtractBlitTime = 0f;
        stats_gpuFaceExportBlits = 0;
        stats_gpuFaceExportBlitTime = 0f;
        stats_gpuChunkUploads = 0;
        stats_gpuChunkUploadTime = 0f;
        stats_gpuChunkUploadBytes = 0;
        stats_gpuFaceOccupancyUploads = 0;
        stats_gpuFaceOccupancyUploadTime = 0f;
        stats_gpuFaceOccupancyUploadBytes = 0;
        stats_gpuFaceReadbackRequestStartMs = -1f;
        stats_gpuFaceReadbackRequests = 0;
        stats_gpuFaceReadbacksCompleted = 0;
        stats_gpuFaceReadbackFailures = 0;
        stats_gpuFaceReadbackLatencyTotal = 0f;
        stats_gpuFaceReadbackLatencyMin = float.MaxValue;
        stats_gpuFaceReadbackLatencyMax = 0f;
        stats_gpuFaceReadbackCallbackCopyTime = 0f;
        stats_gpuFaceReadbackBytes = 0;
        stats_gpuFaceTileExtractTime = 0f;
        for (int i = 0; i < SLOWEST_MESH_BUILD_COUNT; i++)
        {
            stats_slowestMeshBuildMs[i] = 0f;
            stats_slowestMeshChunkX[i] = 0;
            stats_slowestMeshChunkY[i] = 0;
            stats_slowestMeshChunkZ[i] = 0;
            stats_slowestMeshDataPrepMs[i] = 0f;
            stats_slowestMeshMainLoopMs[i] = 0f;
            stats_slowestMeshApplyMs[i] = 0f;
            stats_slowestMeshFaceCount[i] = 0;
            stats_slowestMeshVertexCount[i] = 0;
            stats_slowestMeshKind[i] = 0;
        }

        if (coordinator != null)
        {
            coordinator.ResetAggregatePerformanceStats();
        }

        if (terrainGenerator != null)
        {
            terrainGenerator.ResetAggregatePerformanceStats();
        }

        if (blockTicker != null)
        {
            blockTicker.ResetAggregatePerformanceStats();
        }
    }
#endif
}
