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
    // PARITY: Beta survival reach is 4.0 (PlayerControllerSP.getBlockReachDistance), creative is 5.0.
    // Previous default 7f let players reach further than legitimately allowed in either mode.
    public float blockInteractionRange = 4.5f;
    public LayerMask terrainLayerMask;
    public byte blockTypeToPlace = 1;
    public byte[] placeableBlockPalette;

    // State for the block currently being targeted by the player's gaze.
    private Vector3Int _currentLookingAtPos = new Vector3Int(0, -123456, 0);
    private byte _currentLookingAtBlock = 0;
    private int _selectedPlaceableBlockIndex = 0;
    
    // State for block breaking progress
    private float _currentDestructionProgress = 0f;
    [SerializeField] private int _defaultBreakIncrement = 1;
    [SerializeField] private int _defaultBlockHardness = 100;
    [SerializeField] private float _defaultBreakDurationSeconds = 0.35f;

    [Header("Visuals")]
    [SerializeField] private Transform blockOutline;
    [SerializeField] private Material blockOutlineMaterial;

    [Header("Logging")]
    // Player builds compile the #if LOGGING blocks too (LOGGING is defined project-wide), so
    // this field must exist whenever either define is active — the UNITY_EDITOR-only gate
    // made the standalone world build fail at the LOGGING use site below (CS0103).
#if UNITY_EDITOR || LOGGING
    public bool enableVerboseLogging = true;
#endif

    // Private runtime variables
    private VRCPlayerApi localPlayer;
    private bool isInitialized = false;
    private StringBuilder logBuilder;

    // Raycast info is updated once per frame in Update()
    private bool currentFrameHitValid;
    private Vector3 currentFrameHitPoint;
    private Vector3 currentFrameHitNormal;
    private float currentFrameHitDistance;
    private bool placeTriggerWasHeld;

    private const float RAYCAST_OFFSET_ALONG_NORMAL = 0.01f;
    private const float RAYCAST_OFFSET_INTO_BLOCK = 0.01f;
    private const byte BLOCK_TORCH = 50;
    private const byte BLOCK_REDSTONE_TORCH_OFF = 75;
    private const byte BLOCK_REDSTONE_TORCH_ON = 76;
    private const byte TORCH_MOUNT_WEST = 1;
    private const byte TORCH_MOUNT_EAST = 2;
    private const byte TORCH_MOUNT_NORTH = 3;
    private const byte TORCH_MOUNT_SOUTH = 4;
    private const byte TORCH_MOUNT_FLOOR = 5;
    private const byte TORCH_MOUNT_CEILING = 6;

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

        _EnsurePlaceableBlockPalette();
        _ApplySelectedPlaceableBlock(false);

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
        _HandleBlockSelectionInput();
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
            float breakDuration = _GetBreakDurationSeconds();
            _currentDestructionProgress += 100f * Time.fixedDeltaTime / breakDuration;

            if (_currentDestructionProgress >= 100f)
            {
                BreakBlock(); // This method will also reset the progress
            }
        }
        else if (!breakActionHeld)
        {
            // If the button is released, reset the progress.
            // Progress is also reset if the player looks at a new block (_UpdateTargetedBlock).
            _currentDestructionProgress = 0f;
        }
    }

    private float _GetBreakDurationSeconds()
    {
        if (_defaultBreakDurationSeconds > 0f) return _defaultBreakDurationSeconds;

        int safeHardness = _defaultBlockHardness > 0 ? _defaultBlockHardness : 1;
        int safeBreakIncrement = _defaultBreakIncrement > 0 ? _defaultBreakIncrement : 1;
        return (safeHardness / (float)safeBreakIncrement) * Time.fixedDeltaTime;
    }

    /// <summary>
    /// Performs the physics raycast from the player's head.
    /// </summary>
    private void _UpdateInteractionRaycast()
    {
        VRCPlayerApi.TrackingData headData = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        Vector3 rayOrigin = headData.position;
        Vector3 rayDirection = headData.rotation * Vector3.forward;

        if (world != null && world.useBoxColliderCollision)
        {
            // Box-collider mode: terrain has no full chunk mesh colliders to hit, so target blocks with
            // ONE voxel DDA raycast that tests torch AABBs and solid blocks in the same walk (this used
            // to be two separate walks — 2-3 cross-behaviour GetBlock chains per voxel step per frame).
            // Voxels are visited in t-order and torch/solid ids are mutually exclusive per voxel, so the
            // first hit is identical to the old solid-walk-then-clipped-torch-walk result.
            float vDist; Vector3 vPoint; Vector3 vNormal;
            currentFrameHitValid = _TryGetVoxelOrTorchHit(rayOrigin, rayDirection, blockInteractionRange, out vDist, out vPoint, out vNormal);
            currentFrameHitPoint = currentFrameHitValid ? vPoint : Vector3.zero;
            currentFrameHitNormal = currentFrameHitValid ? vNormal : Vector3.zero;
            currentFrameHitDistance = currentFrameHitValid ? vDist : blockInteractionRange;
        }
        else
        {
            RaycastHit physicsHit;
            currentFrameHitValid = Physics.Raycast(
                rayOrigin,
                rayDirection,
                out physicsHit,
                blockInteractionRange,
                terrainLayerMask
            );

            currentFrameHitPoint = currentFrameHitValid ? physicsHit.point : Vector3.zero;
            currentFrameHitNormal = currentFrameHitValid ? physicsHit.normal : Vector3.zero;
            currentFrameHitDistance = currentFrameHitValid ? physicsHit.distance : blockInteractionRange;

            // Torches have no mesh collider — overlay a torch AABB walk clipped to the physics hit.
            float maxTorchDistance = currentFrameHitValid ? currentFrameHitDistance : blockInteractionRange;
            float torchHitDistance;
            Vector3 torchHitPoint;
            Vector3 torchHitNormal;
            if (_TryGetTorchHit(rayOrigin, rayDirection, maxTorchDistance, out torchHitDistance, out torchHitPoint, out torchHitNormal))
            {
                currentFrameHitValid = true;
                currentFrameHitPoint = torchHitPoint;
                currentFrameHitNormal = torchHitNormal;
                currentFrameHitDistance = torchHitDistance;
            }
        }
    }

    // Merged solid+torch DDA for box-collider mode: one GetBlock per voxel step, air short-circuits
    // before any further cross-behaviour calls. Same hit contract as the old pair of walks.
    private bool _TryGetVoxelOrTorchHit(Vector3 rayOrigin, Vector3 rayDirection, float maxDistance, out float hitDistance, out Vector3 hitPoint, out Vector3 hitNormal)
    {
        hitDistance = 0f;
        hitPoint = Vector3.zero;
        hitNormal = Vector3.zero;
        if (world == null || maxDistance <= 0f) return false;

        Vector3 dir = rayDirection.normalized;
        int vx = Mathf.FloorToInt(rayOrigin.x);
        int vy = Mathf.FloorToInt(rayOrigin.y);
        int vz = Mathf.FloorToInt(rayOrigin.z);

        int stepX = dir.x > 0f ? 1 : (dir.x < 0f ? -1 : 0);
        int stepY = dir.y > 0f ? 1 : (dir.y < 0f ? -1 : 0);
        int stepZ = dir.z > 0f ? 1 : (dir.z < 0f ? -1 : 0);

        float nbX = stepX > 0 ? vx + 1f : vx;
        float nbY = stepY > 0 ? vy + 1f : vy;
        float nbZ = stepZ > 0 ? vz + 1f : vz;

        float tMaxX = stepX != 0 ? (nbX - rayOrigin.x) / dir.x : float.PositiveInfinity;
        float tMaxY = stepY != 0 ? (nbY - rayOrigin.y) / dir.y : float.PositiveInfinity;
        float tMaxZ = stepZ != 0 ? (nbZ - rayOrigin.z) / dir.z : float.PositiveInfinity;
        float tDeltaX = stepX != 0 ? 1f / Mathf.Abs(dir.x) : float.PositiveInfinity;
        float tDeltaY = stepY != 0 ? 1f / Mathf.Abs(dir.y) : float.PositiveInfinity;
        float tDeltaZ = stepZ != 0 ? 1f / Mathf.Abs(dir.z) : float.PositiveInfinity;

        float t = 0f;
        int lastAxis = -1; // 0=x,1=y,2=z; -1 = still in the origin voxel
        int budget = 0;
        while (t <= maxDistance && budget < 256)
        {
            byte bid = (byte)(world.GetBlock(vx, vy, vz) & 0xFF);
            if (bid != 0)
            {
                if (bid == BLOCK_TORCH || bid == BLOCK_REDSTONE_TORCH_OFF || bid == BLOCK_REDSTONE_TORCH_ON)
                {
                    Vector3 boundsMin;
                    Vector3 boundsMax;
                    _GetTorchBounds(vx, vy, vz, world.GetTorchMount(vx, vy, vz), out boundsMin, out boundsMax);
                    float torchDist;
                    Vector3 torchNormal;
                    if (_TryRayBoundsHit(rayOrigin, dir, boundsMin, boundsMax, out torchDist, out torchNormal) && torchDist <= maxDistance)
                    {
                        hitDistance = torchDist;
                        hitPoint = rayOrigin + dir * torchDist;
                        hitNormal = torchNormal;
                        return true;
                    }
                }
                else if (world.BlockHasCollision(bid))
                {
                    hitDistance = t;
                    hitPoint = rayOrigin + dir * t;
                    if (lastAxis == 0) hitNormal = new Vector3(-stepX, 0f, 0f);
                    else if (lastAxis == 1) hitNormal = new Vector3(0f, -stepY, 0f);
                    else if (lastAxis == 2) hitNormal = new Vector3(0f, 0f, -stepZ);
                    else hitNormal = -dir; // ray started inside a solid block
                    return true;
                }
            }
            if (tMaxX <= tMaxY && tMaxX <= tMaxZ) { vx += stepX; t = tMaxX; tMaxX += tDeltaX; lastAxis = 0; }
            else if (tMaxY <= tMaxZ) { vy += stepY; t = tMaxY; tMaxY += tDeltaY; lastAxis = 1; }
            else { vz += stepZ; t = tMaxZ; tMaxZ += tDeltaZ; lastAxis = 2; }
            budget++;
        }
        return false;
    }

    /// <summary>
    /// Determines which block is being looked at and resets breaking progress if it changes.
    /// </summary>
    private void _UpdateTargetedBlock()
    {
        Vector3Int newTargetPos = new Vector3Int(0, -123456, 0); // Default to an invalid position

        if (currentFrameHitValid)
        {
            Vector3 pointToConvert = currentFrameHitPoint - currentFrameHitNormal * RAYCAST_OFFSET_INTO_BLOCK;
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
            _currentDestructionProgress = 0f; // Reset progress when looking at a new block
        }

        if (currentFrameHitValid)
        {
            _currentLookingAtBlock = (byte)(world.GetBlock(_currentLookingAtPos.x, _currentLookingAtPos.y, _currentLookingAtPos.z) & 0xFF);
        }
        else
        {
            _currentLookingAtBlock = 0; // No block is targeted
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
            
            blockOutlineMaterial.SetFloat("_Progress", _currentDestructionProgress);
        }
        else
        {
            blockOutlineMaterial.SetFloat("_Progress", 0f);
            blockOutline.position = new Vector3(0, -100, 0); // Move outline out of sight
        }
    }

    /// <summary>
    /// Handles single-press input for placing a block.
    /// </summary>
    private void _HandleBlockPlacementInput()
    {
        // Check for single-press place action
        bool placeTriggerHeld = Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger") > 0.5f;
        bool primaryAction = Input.GetMouseButtonDown(1) || (placeTriggerHeld && !placeTriggerWasHeld);
        placeTriggerWasHeld = placeTriggerHeld;

        if (primaryAction && currentFrameHitValid)
        {
            PlaceBlock();
        }
    }

    private void _HandleBlockSelectionInput()
    {
        if (placeableBlockPalette == null || placeableBlockPalette.Length == 0) return;

        float scrollInput = Input.GetAxisRaw("Mouse ScrollWheel");
        if (scrollInput > 0.01f)
        {
            _StepSelectedPlaceableBlock(1);
            return;
        }
        if (scrollInput < -0.01f)
        {
            _StepSelectedPlaceableBlock(-1);
            return;
        }

        if (Input.GetKeyDown(KeyCode.Alpha1)) { _SetSelectedPlaceableBlockIndex(0); return; }
        if (Input.GetKeyDown(KeyCode.Alpha2)) { _SetSelectedPlaceableBlockIndex(1); return; }
        if (Input.GetKeyDown(KeyCode.Alpha3)) { _SetSelectedPlaceableBlockIndex(2); return; }
        if (Input.GetKeyDown(KeyCode.Alpha4)) { _SetSelectedPlaceableBlockIndex(3); return; }
        if (Input.GetKeyDown(KeyCode.Alpha5)) { _SetSelectedPlaceableBlockIndex(4); return; }
        if (Input.GetKeyDown(KeyCode.Alpha6)) { _SetSelectedPlaceableBlockIndex(5); return; }
        if (Input.GetKeyDown(KeyCode.Alpha7)) { _SetSelectedPlaceableBlockIndex(6); return; }
        if (Input.GetKeyDown(KeyCode.Alpha8)) { _SetSelectedPlaceableBlockIndex(7); return; }
        if (Input.GetKeyDown(KeyCode.Alpha9)) { _SetSelectedPlaceableBlockIndex(8); return; }
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

        world.SetBlockFromInteraction(breakGlobalX, breakGlobalY, breakGlobalZ, 0);

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
        _currentDestructionProgress = 0f;
        _currentLookingAtBlock = 0; // The block is now air
    }

    /// <summary>
    /// Contains all logic for placing a new block.
    /// It calculates the target position, checks for collision with the player, sets the block, plays effects, and logs the action.
    /// </summary>
    public void PlaceBlock()
    {
        if (!currentFrameHitValid) return;

        Vector3 pointToConvert = currentFrameHitPoint + currentFrameHitNormal * RAYCAST_OFFSET_ALONG_NORMAL;
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

        bool placed = true;
        if (_IsTorchBlock(blockTypeToPlace))
        {
            placed = world != null && world.PlaceTorchFromInteraction(placeGlobalX, placeGlobalY, placeGlobalZ, currentFrameHitNormal, blockTypeToPlace);
        }
        else
        {
            world.SetBlockFromInteraction(placeGlobalX, placeGlobalY, placeGlobalZ, blockTypeToPlace);
        }
        if (!placed) return;
        
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

    private bool _IsTorchBlock(byte blockType)
    {
        return blockType == BLOCK_TORCH || blockType == BLOCK_REDSTONE_TORCH_OFF || blockType == BLOCK_REDSTONE_TORCH_ON;
    }

    private void _EnsurePlaceableBlockPalette()
    {
        if (placeableBlockPalette == null || placeableBlockPalette.Length == 0)
        {
            placeableBlockPalette = new byte[] { 1, 2, 12, 4, 5, 50, 8, 10, 51 };
        }

        if (_selectedPlaceableBlockIndex < 0 || _selectedPlaceableBlockIndex >= placeableBlockPalette.Length)
        {
            _selectedPlaceableBlockIndex = 0;
        }

        int matchingIndex = _FindPlaceableBlockIndex(blockTypeToPlace);
        if (matchingIndex >= 0)
        {
            _selectedPlaceableBlockIndex = matchingIndex;
        }
    }

    private int _FindPlaceableBlockIndex(byte blockType)
    {
        if (placeableBlockPalette == null) return -1;

        for (int i = 0; i < placeableBlockPalette.Length; i++)
        {
            if (placeableBlockPalette[i] == blockType)
            {
                return i;
            }
        }

        return -1;
    }

    private void _StepSelectedPlaceableBlock(int step)
    {
        if (placeableBlockPalette == null || placeableBlockPalette.Length == 0) return;

        int nextIndex = _selectedPlaceableBlockIndex + step;
        if (nextIndex < 0) nextIndex = placeableBlockPalette.Length - 1;
        else if (nextIndex >= placeableBlockPalette.Length) nextIndex = 0;

        _SetSelectedPlaceableBlockIndex(nextIndex);
    }

    private void _SetSelectedPlaceableBlockIndex(int nextIndex)
    {
        if (placeableBlockPalette == null || placeableBlockPalette.Length == 0) return;
        if (nextIndex < 0 || nextIndex >= placeableBlockPalette.Length) return;
        if (nextIndex == _selectedPlaceableBlockIndex && blockTypeToPlace == placeableBlockPalette[nextIndex]) return;

        _selectedPlaceableBlockIndex = nextIndex;
        _ApplySelectedPlaceableBlock(true);
    }

    private void _ApplySelectedPlaceableBlock(bool logSelection)
    {
        if (placeableBlockPalette == null || placeableBlockPalette.Length == 0) return;
        if (_selectedPlaceableBlockIndex < 0 || _selectedPlaceableBlockIndex >= placeableBlockPalette.Length)
        {
            _selectedPlaceableBlockIndex = 0;
        }

        blockTypeToPlace = placeableBlockPalette[_selectedPlaceableBlockIndex];
        if (!logSelection) return;

#if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            string blockName = blockTypeManager != null ? blockTypeManager.GetBlockName(blockTypeToPlace) : "Unknown";
            logBuilder.Clear();
            logBuilder.AppendFormat("[ModifyTerrain] Selected place block {0}/{1}: {2} (ID {3}).",
                _selectedPlaceableBlockIndex + 1, placeableBlockPalette.Length, blockName, blockTypeToPlace);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }

    private bool _TryGetTorchHit(Vector3 rayOrigin, Vector3 rayDirection, float maxDistance, out float hitDistance, out Vector3 hitPoint, out Vector3 hitNormal)
    {
        hitDistance = 0f;
        hitPoint = Vector3.zero;
        hitNormal = Vector3.zero;

        if (world == null || maxDistance <= 0f) return false;

        Vector3 direction = rayDirection.normalized;
        int voxelX = Mathf.FloorToInt(rayOrigin.x);
        int voxelY = Mathf.FloorToInt(rayOrigin.y);
        int voxelZ = Mathf.FloorToInt(rayOrigin.z);

        int stepX = direction.x > 0f ? 1 : (direction.x < 0f ? -1 : 0);
        int stepY = direction.y > 0f ? 1 : (direction.y < 0f ? -1 : 0);
        int stepZ = direction.z > 0f ? 1 : (direction.z < 0f ? -1 : 0);

        float nextBoundaryX = stepX > 0 ? voxelX + 1.0f : voxelX;
        float nextBoundaryY = stepY > 0 ? voxelY + 1.0f : voxelY;
        float nextBoundaryZ = stepZ > 0 ? voxelZ + 1.0f : voxelZ;

        float tMaxX = stepX != 0 ? (nextBoundaryX - rayOrigin.x) / direction.x : float.PositiveInfinity;
        float tMaxY = stepY != 0 ? (nextBoundaryY - rayOrigin.y) / direction.y : float.PositiveInfinity;
        float tMaxZ = stepZ != 0 ? (nextBoundaryZ - rayOrigin.z) / direction.z : float.PositiveInfinity;
        float tDeltaX = stepX != 0 ? 1.0f / Mathf.Abs(direction.x) : float.PositiveInfinity;
        float tDeltaY = stepY != 0 ? 1.0f / Mathf.Abs(direction.y) : float.PositiveInfinity;
        float tDeltaZ = stepZ != 0 ? 1.0f / Mathf.Abs(direction.z) : float.PositiveInfinity;

        float travelDistance = 0f;
        int stepBudget = 0;
        while (travelDistance <= maxDistance && stepBudget < 64)
        {
            float voxelHitDistance;
            Vector3 voxelHitPoint;
            Vector3 voxelHitNormal;
            if (_TryGetTorchHitInVoxel(voxelX, voxelY, voxelZ, rayOrigin, direction, maxDistance, out voxelHitDistance, out voxelHitPoint, out voxelHitNormal))
            {
                hitDistance = voxelHitDistance;
                hitPoint = voxelHitPoint;
                hitNormal = voxelHitNormal;
                return true;
            }

            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                voxelX += stepX;
                travelDistance = tMaxX;
                tMaxX += tDeltaX;
            }
            else if (tMaxY <= tMaxZ)
            {
                voxelY += stepY;
                travelDistance = tMaxY;
                tMaxY += tDeltaY;
            }
            else
            {
                voxelZ += stepZ;
                travelDistance = tMaxZ;
                tMaxZ += tDeltaZ;
            }

            stepBudget++;
        }

        return false;
    }

    private bool _TryGetTorchHitInVoxel(int voxelX, int voxelY, int voxelZ, Vector3 rayOrigin, Vector3 rayDirection, float maxDistance, out float hitDistance, out Vector3 hitPoint, out Vector3 hitNormal)
    {
        hitDistance = 0f;
        hitPoint = Vector3.zero;
        hitNormal = Vector3.zero;

        byte blockType = (byte)(world.GetBlock(voxelX, voxelY, voxelZ) & 0xFF);
        if (!_IsTorchBlock(blockType)) return false;

        Vector3 boundsMin;
        Vector3 boundsMax;
        _GetTorchBounds(voxelX, voxelY, voxelZ, world.GetTorchMount(voxelX, voxelY, voxelZ), out boundsMin, out boundsMax);

        if (!_TryRayBoundsHit(rayOrigin, rayDirection, boundsMin, boundsMax, out hitDistance, out hitNormal)) return false;
        if (hitDistance > maxDistance) return false;

        hitPoint = rayOrigin + rayDirection * hitDistance;
        return true;
    }

    private void _GetTorchBounds(int voxelX, int voxelY, int voxelZ, byte torchMount, out Vector3 boundsMin, out Vector3 boundsMax)
    {
        if (torchMount == TORCH_MOUNT_WEST)
        {
            boundsMin = new Vector3(voxelX + 0.0f, voxelY + 0.2f, voxelZ + 0.35f);
            boundsMax = new Vector3(voxelX + 0.3f, voxelY + 0.8f, voxelZ + 0.65f);
            return;
        }
        if (torchMount == TORCH_MOUNT_EAST)
        {
            boundsMin = new Vector3(voxelX + 0.7f, voxelY + 0.2f, voxelZ + 0.35f);
            boundsMax = new Vector3(voxelX + 1.0f, voxelY + 0.8f, voxelZ + 0.65f);
            return;
        }
        if (torchMount == TORCH_MOUNT_NORTH)
        {
            boundsMin = new Vector3(voxelX + 0.35f, voxelY + 0.2f, voxelZ + 0.0f);
            boundsMax = new Vector3(voxelX + 0.65f, voxelY + 0.8f, voxelZ + 0.3f);
            return;
        }
        if (torchMount == TORCH_MOUNT_SOUTH)
        {
            boundsMin = new Vector3(voxelX + 0.35f, voxelY + 0.2f, voxelZ + 0.7f);
            boundsMax = new Vector3(voxelX + 0.65f, voxelY + 0.8f, voxelZ + 1.0f);
            return;
        }
        if (torchMount == TORCH_MOUNT_CEILING)
        {
            boundsMin = new Vector3(voxelX + 0.4f, voxelY + 0.4f, voxelZ + 0.4f);
            boundsMax = new Vector3(voxelX + 0.6f, voxelY + 1.0f, voxelZ + 0.6f);
            return;
        }

        boundsMin = new Vector3(voxelX + 0.4f, voxelY + 0.0f, voxelZ + 0.4f);
        boundsMax = new Vector3(voxelX + 0.6f, voxelY + 0.6f, voxelZ + 0.6f);
    }

    private bool _TryRayBoundsHit(Vector3 rayOrigin, Vector3 rayDirection, Vector3 boundsMin, Vector3 boundsMax, out float hitDistance, out Vector3 hitNormal)
    {
        hitDistance = 0f;
        hitNormal = Vector3.zero;

        float tMin = 0f;
        float tMax = float.PositiveInfinity;

        if (!_ClipRayToBoundsAxis(rayOrigin.x, rayDirection.x, boundsMin.x, boundsMax.x, Vector3.left, Vector3.right, ref tMin, ref tMax, ref hitNormal)) return false;
        if (!_ClipRayToBoundsAxis(rayOrigin.y, rayDirection.y, boundsMin.y, boundsMax.y, Vector3.down, Vector3.up, ref tMin, ref tMax, ref hitNormal)) return false;
        if (!_ClipRayToBoundsAxis(rayOrigin.z, rayDirection.z, boundsMin.z, boundsMax.z, Vector3.back, Vector3.forward, ref tMin, ref tMax, ref hitNormal)) return false;
        if (tMax < 0f) return false;

        hitDistance = tMin >= 0f ? tMin : tMax;
        return hitDistance >= 0f;
    }

    private bool _ClipRayToBoundsAxis(float originAxis, float directionAxis, float boundsMin, float boundsMax, Vector3 minNormal, Vector3 maxNormal, ref float tMin, ref float tMax, ref Vector3 hitNormal)
    {
        if (Mathf.Abs(directionAxis) < 0.0001f)
        {
            return originAxis >= boundsMin && originAxis <= boundsMax;
        }

        float inverseDirection = 1.0f / directionAxis;
        float nearDistance = (boundsMin - originAxis) * inverseDirection;
        float farDistance = (boundsMax - originAxis) * inverseDirection;
        Vector3 nearNormal = directionAxis > 0f ? minNormal : maxNormal;

        if (nearDistance > farDistance)
        {
            float swapDistance = nearDistance;
            nearDistance = farDistance;
            farDistance = swapDistance;
            nearNormal = directionAxis > 0f ? maxNormal : minNormal;
        }

        if (nearDistance > tMin)
        {
            tMin = nearDistance;
            hitNormal = nearNormal;
        }
        if (farDistance < tMax)
        {
            tMax = farDistance;
        }

        return tMin <= tMax;
    }
}
