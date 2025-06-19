using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRRefAssist;
using System.Text;

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ModifyTerrain : UdonSharpBehaviour
{
    [Header("Core References")]
    [SerializeField] private McWorld world;
    [SerializeField] private McParticleManager particleManager;
    [SerializeField, FindObjectOfType(true)]
    private McBlockTypeManager blockTypeManager;

    [Header("Interaction Settings")]
    public float blockInteractionRange = 7f;
    public LayerMask terrainLayerMask;
    public byte blockTypeToPlace = 1;

    // State for the block currently being targeted by the player's gaze.
    private Vector3Int _currentLookingAtPos = new Vector3Int(0, -123456, 0);
    private byte _currentLookingAtBlock = 0;
    
    // State for block breaking progress
    private int _currentDestructionProgress = 0;
    [SerializeField] private int _defaultBreakIncrement = 1;
    [SerializeField] private int _defaultBlockHardness = 100;

    [Header("Visuals")]
    [SerializeField] private Transform blockOutline;
    [SerializeField] private Material blockOutlineMaterial;

    [Header("Logging")]
#if UNITY_EDITOR
    public bool enableVerboseLogging = true;
#endif

    // Private runtime variables
    private VRCPlayerApi localPlayer;
    private bool isInitialized = false;
    private StringBuilder logBuilder;

    // Raycast info is updated once per frame in Update()
    private bool currentFrameHitValid;
    private RaycastHit currentFrameHitInfo;

    private const float RAYCAST_OFFSET_ALONG_NORMAL = 0.01f;
    private const float RAYCAST_OFFSET_INTO_BLOCK = 0.01f;

    void Start()
    {
        logBuilder = new StringBuilder(256);
        localPlayer = Networking.LocalPlayer;
        if (localPlayer == null)
        {
            SendCustomEventDelayedSeconds(nameof(_InitializePlayer), 1.0f);
        }
        else
        {
            _InitializePlayer();
        }
    }

    public void _InitializePlayer()
    {
        if (isInitialized) return;

        localPlayer = Networking.LocalPlayer;
        if (localPlayer == null)
        {
            Debug.LogError("[ModifyTerrain] LocalPlayer API is null. Interaction will not work.");
            return;
        }

        if (world == null) Debug.LogError("[ModifyTerrain] McWorld reference is not set!");
        if (blockTypeManager == null) Debug.LogWarning("[ModifyTerrain] McBlockTypeManager reference not set.");
        if (particleManager == null) Debug.LogWarning("[ModifyTerrain] McParticleManager reference not set.");

        isInitialized = true;
#if UNITY_EDITOR
        if (enableVerboseLogging) Debug.Log("[ModifyTerrain] Initialization Complete.");
#endif
    }
    
    /// <summary>
    /// Update is called once per frame. It handles raycasting and player input.
    /// </summary>
    public void Update()
    {
        if (!isInitialized) return;

        // Perform raycast and update the targeted block state
        _UpdateInteractionRaycast();
        _UpdateTargetedBlock();

        // Update visuals and handle inputs
        _HandleBlockOutline();
        _HandleBlockPlacementInput();
    }

    /// <summary>
    /// FixedUpdate is called at a fixed interval and is used for physics-related updates.
    /// Here, it handles the continuous logic for breaking a block over time.
    /// </summary>
    public void FixedUpdate()
    {
        if (!isInitialized) return;

        // Check for continuous break action (holding the button/trigger)
        bool breakActionHeld = Input.GetMouseButton(0) || Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger") > 0.5f;

        // We only process breaking if the action is held and we're looking at a valid, non-air block
        if (breakActionHeld && _currentLookingAtBlock != 0)
        {
            // TODO: In the future, get hardness from blockTypeManager based on _currentLookingAtBlock
            int hardness = _defaultBlockHardness;

            _currentDestructionProgress += _defaultBreakIncrement;

            if (_currentDestructionProgress >= hardness)
            {
                BreakBlock(); // This method will also reset the progress
            }
        }
        else if (!breakActionHeld)
        {
            // If the button is released, reset the progress.
            // Progress is also reset if the player looks at a new block (_UpdateTargetedBlock).
            _currentDestructionProgress = 0;
        }
    }

    /// <summary>
    /// Performs the physics raycast from the player's head.
    /// </summary>
    private void _UpdateInteractionRaycast()
    {
        VRCPlayerApi.TrackingData headData = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        currentFrameHitValid = Physics.Raycast(
            headData.position,
            headData.rotation * Vector3.forward,
            out currentFrameHitInfo,
            blockInteractionRange,
            terrainLayerMask
        );
    }

    /// <summary>
    /// Determines which block is being looked at and resets breaking progress if it changes.
    /// </summary>
    private void _UpdateTargetedBlock()
    {
        Vector3Int newTargetPos = new Vector3Int(0, -123456, 0); // Default to an invalid position

        if (currentFrameHitValid)
        {
            Vector3 pointToConvert = currentFrameHitInfo.point - currentFrameHitInfo.normal * RAYCAST_OFFSET_INTO_BLOCK;
            newTargetPos = new Vector3Int(
                Mathf.FloorToInt(pointToConvert.x),
                Mathf.FloorToInt(pointToConvert.y),
                Mathf.FloorToInt(pointToConvert.z)
            );
        }
        
        // If the targeted block has changed since the last frame
        if (newTargetPos != _currentLookingAtPos)
        {
            _currentLookingAtPos = newTargetPos;
            _currentDestructionProgress = 0; // Reset progress when looking at a new block
            if (currentFrameHitValid)
            {
                _currentLookingAtBlock = (byte)(world.GetBlock(_currentLookingAtPos.x, _currentLookingAtPos.y, _currentLookingAtPos.z) & 0xFF);
            }
            else
            {
                _currentLookingAtBlock = 0; // No block is targeted
            }
        }
    }


    /// <summary>
    /// Manages the position and appearance of the block outline visual.
    /// </summary>
    private void _HandleBlockOutline()
    {
        if (currentFrameHitValid && _currentLookingAtBlock != 0)
        {
            blockOutline.position = (Vector3)_currentLookingAtPos + new Vector3(0.5f, 0.5f, 0.5f);
            
            // Normalize progress for the shader (assuming it's a 0-1 value)
            blockOutlineMaterial.SetInteger("_Progress", _currentDestructionProgress);
        }
        else
        {
            blockOutlineMaterial.SetInteger("_Progress", 0);
            blockOutline.position = new Vector3(0, -100, 0); // Move outline out of sight
        }
    }

    /// <summary>
    /// Handles single-press input for placing a block.
    /// </summary>
    private void _HandleBlockPlacementInput()
    {
        // Check for single-press place action
        bool primaryAction = Input.GetMouseButtonDown(1) || Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger") > 0.5f;

        if (primaryAction && currentFrameHitValid)
        {
            PlaceBlock();
        }
    }

    /// <summary>
    /// Contains all logic for breaking the currently targeted block.
    /// It sets the block to air, plays effects, and logs the action.
    /// </summary>
    public void BreakBlock()
    {
        if (!currentFrameHitValid || _currentLookingAtBlock == 0) return;

        int breakGlobalX = _currentLookingAtPos.x;
        int breakGlobalY = _currentLookingAtPos.y;
        int breakGlobalZ = _currentLookingAtPos.z;

        world.SetBlock(breakGlobalX, breakGlobalY, breakGlobalZ, 0);

        Vector3 effectPosition = new Vector3(breakGlobalX + 0.5f, breakGlobalY + 0.5f, breakGlobalZ + 0.5f);
        if (particleManager != null)
        {
            particleManager.PlayBreakEffect(effectPosition, blockTypeManager != null ? _currentLookingAtBlock : (byte)0);
        }

#if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            logBuilder.Clear();
            logBuilder.AppendFormat("[ModifyTerrain] Broke block ID {0} at G({1},{2},{3}).",
                _currentLookingAtBlock, breakGlobalX, breakGlobalY, breakGlobalZ);
            Debug.Log(logBuilder.ToString());
        }
#endif
        
        // Reset progress and targeted block ID after breaking
        _currentDestructionProgress = 0;
        _currentLookingAtBlock = 0; // The block is now air
    }

    /// <summary>
    /// Contains all logic for placing a new block.
    /// It calculates the target position, checks for collision with the player, sets the block, plays effects, and logs the action.
    /// </summary>
    public void PlaceBlock()
    {
        if (!currentFrameHitValid) return;

        Vector3 pointToConvert = currentFrameHitInfo.point + currentFrameHitInfo.normal * RAYCAST_OFFSET_ALONG_NORMAL;
        int placeGlobalX = Mathf.FloorToInt(pointToConvert.x);
        int placeGlobalY = Mathf.FloorToInt(pointToConvert.y);
        int placeGlobalZ = Mathf.FloorToInt(pointToConvert.z);

        // Prevent placing a block inside the player's head or feet space
        Vector3 playerHeadPos = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
        Vector3Int playerHeadVoxel = new Vector3Int(Mathf.FloorToInt(playerHeadPos.x), Mathf.FloorToInt(playerHeadPos.y), Mathf.FloorToInt(playerHeadPos.z));
        Vector3Int playerFeetVoxel = new Vector3Int(Mathf.FloorToInt(playerHeadPos.x), Mathf.FloorToInt(playerHeadPos.y - 1.7f), Mathf.FloorToInt(playerHeadPos.z));

        if ((placeGlobalX == playerHeadVoxel.x && placeGlobalY == playerHeadVoxel.y && placeGlobalZ == playerHeadVoxel.z) ||
            (placeGlobalX == playerFeetVoxel.x && placeGlobalY == playerFeetVoxel.y && placeGlobalZ == playerFeetVoxel.z))
        {
#if UNITY_EDITOR
            if (enableVerboseLogging) Debug.Log("[ModifyTerrain.PlaceBlock] Attempted to place block in self. Ignored.");
#endif
            return;
        }

        world.SetBlock(placeGlobalX, placeGlobalY, placeGlobalZ, blockTypeToPlace);
        
        Vector3 effectPosition = new Vector3(placeGlobalX + 0.5f, placeGlobalY + 0.5f, placeGlobalZ + 0.5f);
        if (particleManager != null)
        {
            particleManager.PlayPlaceEffect(effectPosition, blockTypeManager != null ? blockTypeToPlace : (byte)0);
        }
#if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            logBuilder.Clear();
            logBuilder.AppendFormat("[ModifyTerrain.PlaceBlock] Placed block ID {0} at G({1},{2},{3}).",
                blockTypeToPlace, placeGlobalX, placeGlobalY, placeGlobalZ);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }
}
