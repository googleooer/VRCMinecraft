using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common; 
using VRRefAssist; 

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ModifyTerrain : UdonSharpBehaviour
{
    [Header("Core References")]
    [Tooltip("Assign the McWorld UdonSharpBehaviour from your scene.")]
    [SerializeField] private McWorld world;
    
    [Tooltip("Assign the McParticleManager from your scene (Optional).")]
    [SerializeField] private McParticleManager particleManager; 
    
    [SerializeField, FindObjectOfType(true)] 
    private McBlockTypeManager blockTypeManager; 

    [Header("Interaction Settings")]
    [Tooltip("Maximum distance a player can place or break blocks.")]
    public float blockInteractionRange = 7f;
    [Tooltip("Layer mask for raycasting to hit only terrain colliders. CRITICAL: Set this in Inspector!")]
    public LayerMask terrainLayerMask;
    [Tooltip("The block ID to place. Implement player selection for this.")]
    public byte blockTypeToPlace = 1; 

    [Header("Visuals")]
    [Tooltip("Optional: A visual outline for placing/breaking blocks.")]
    [SerializeField] private Transform blockOutline;

    private VRCPlayerApi localPlayer;
    private bool isInitialized = false;
    private VRCPlayerApi.TrackingData playerHeadData;

    // Store raycast results per frame to avoid redundant raycasts
    private bool currentFrameHitValid;
    private RaycastHit currentFrameHitInfo;
    // private Vector3 currentFrameRayOrigin; // Not strictly needed if using mainCamera.transform
    // private Vector3 currentFrameRayDirection; // Not strictly needed if using mainCamera.transform

    private const float RAYCAST_OFFSET_ALONG_NORMAL = 0.01f; // Small offset to get center of block
    private const float RAYCAST_OFFSET_INTO_BLOCK = 0.01f; // Small offset to ensure inside block

    void Start()
    {
        localPlayer = Networking.LocalPlayer;
        if (localPlayer == null) {
            // Try again in a bit if player API not ready immediately
            SendCustomEventDelayedSeconds("_InitializePlayer", 1.0f);
        } else {
            _InitializePlayer();
        }
    }

    public void _InitializePlayer() {
        if (isInitialized) return;

        localPlayer = Networking.LocalPlayer;
        if (localPlayer == null) {
             Debug.LogError("[ModifyTerrain] LocalPlayer API is null after delay. Interaction will not work.");
             return;
        }
        
        // It's generally better to get the main camera once if it's not changing.
        // However, for VR, the "main" camera can be complex (e.g. specific eye camera).
        // VRCPlayerApi.GetTrackingData(VRCPlayerApi.TrackingDataType.Head) gives head position/rotation.
        // For simplicity, if Camera.main works for your setup, it's okay. Otherwise, use head tracking.
        playerHeadData = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);

        if (blockOutline != null) {
            blockOutline.gameObject.SetActive(false);
        }
        
        if (world == null) Debug.LogError("[ModifyTerrain] McWorld reference is not set!");
        if (blockTypeManager == null) Debug.LogWarning("[ModifyTerrain] McBlockTypeManager reference not set (optional for basic breaking, needed for effects).");
        if (particleManager == null) Debug.LogWarning("[ModifyTerrain] McParticleManager reference not set (optional, for effects).");


        isInitialized = true;
    }

    public void Update()
    {
        if (!isInitialized || localPlayer == null || world == null) return;

        // Perform raycast once and store results
        _UpdateInteractionRaycast();

        // Use stored raycast results for block outline and input handling
        _HandleBlockOutline();
        _HandleBlockPlacementInput();
    }

    private void _UpdateInteractionRaycast()
    {
        // Raycast from camera/head
        Vector3 rayOrigin;
        Quaternion rayRotation;

        // Prefer head tracking data for VR precision
        VRCPlayerApi.TrackingData headData = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        rayOrigin = headData.position;
        rayRotation = headData.rotation;
        
        // If Camera.main is reliable and preferred for your non-VR or specific VR setup:
        // rayOrigin = mainCamera.transform.position;
        // rayRotation = mainCamera.transform.rotation;
        // Vector3 rayDirection = mainCamera.transform.forward;

        Vector3 rayDirection = rayRotation * Vector3.forward;

        currentFrameHitValid = Physics.Raycast(rayOrigin, rayDirection, out currentFrameHitInfo, blockInteractionRange, terrainLayerMask);
    }

    private void _HandleBlockOutline()
    {
        if (blockOutline == null) return;

        if (currentFrameHitValid)
        {
            blockOutline.gameObject.SetActive(true);
            // Position outline on the face of the block, or inside the block to be placed
            Vector3 pointToConvert = currentFrameHitInfo.point + currentFrameHitInfo.normal * RAYCAST_OFFSET_ALONG_NORMAL;
            blockOutline.position = new Vector3(Mathf.FloorToInt(pointToConvert.x) + 0.5f, Mathf.FloorToInt(pointToConvert.y) + 0.5f, Mathf.FloorToInt(pointToConvert.z) + 0.5f);
        }
        else
        {
            blockOutline.gameObject.SetActive(false);
        }
    }

    private void _HandleBlockPlacementInput()
    {
        // Using VRCInput for VR compatibility
        // VRCInputMethod.Value for continuous input, VRCInputMethod.Down for button press
        bool primaryAction = Input.GetMouseButtonDown(0) || Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger") > 0.5f; 
        bool secondaryAction = Input.GetMouseButtonDown(1) || Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger") > 0.5f;
        
        // Debounce trigger input for VR to act like GetMouseButtonDown
        // This requires storing previous frame's trigger state, simple example:
        // if (Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger") > 0.5f && !wasPrimaryTriggerPressedLastFrame) primaryAction = true;
        // wasPrimaryTriggerPressedLastFrame = Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger") > 0.5f;
        // For simplicity, current code will allow holding trigger to continuously place/break. If single action per press is desired, add debounce.


        if (primaryAction || secondaryAction)
        {
            if (currentFrameHitValid) // Use the hit from _UpdateInteractionRaycast
            {
                RaycastHit hit = currentFrameHitInfo; // Use the stored hit
                Vector3 pointToConvert;
                Vector3 effectPosition;
                byte idOfAffectedBlock;

                if (primaryAction) // Placing block (assuming primary is place)
                {
                    pointToConvert = hit.point + hit.normal * RAYCAST_OFFSET_ALONG_NORMAL; // Place on the surface clicked
                    int placeGlobalX = Mathf.FloorToInt(pointToConvert.x);
                    int placeGlobalY = Mathf.FloorToInt(pointToConvert.y);
                    int placeGlobalZ = Mathf.FloorToInt(pointToConvert.z);

                    // Check if we are trying to place a block inside the player
                    Vector3 playerHeadPos = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
                    Vector3Int playerVoxelPos = new Vector3Int(Mathf.FloorToInt(playerHeadPos.x), Mathf.FloorToInt(playerHeadPos.y), Mathf.FloorToInt(playerHeadPos.z));
                    Vector3Int playerFeetVoxelPos = new Vector3Int(Mathf.FloorToInt(playerHeadPos.x), Mathf.FloorToInt(playerHeadPos.y - 1.7f), Mathf.FloorToInt(playerHeadPos.z)); // Approx feet

                    if ((placeGlobalX == playerVoxelPos.x && placeGlobalY == playerVoxelPos.y && placeGlobalZ == playerVoxelPos.z) ||
                        (placeGlobalX == playerFeetVoxelPos.x && placeGlobalY == playerFeetVoxelPos.y && placeGlobalZ == playerFeetVoxelPos.z) ) {
                        // Trying to place block in self, ignore.
                        return;
                    }


                    world.SetBlock(placeGlobalX, placeGlobalY, placeGlobalZ, blockTypeToPlace);
                    
                    effectPosition = new Vector3(placeGlobalX + 0.5f, placeGlobalY + 0.5f, placeGlobalZ + 0.5f);
                    byte idOfBlockToSetForPlacing = blockTypeToPlace;

                    if (particleManager != null && blockTypeManager != null) {
                        particleManager.PlayPlaceEffect(effectPosition, idOfBlockToSetForPlacing); 
                    } else if (particleManager != null) { 
                        particleManager.PlayPlaceEffect(effectPosition, 0); 
                    }
                }
                else // Breaking block (assuming secondary is break)
                {
                    pointToConvert = hit.point - hit.normal * RAYCAST_OFFSET_INTO_BLOCK; // Break the block hit
                    int breakGlobalX = Mathf.FloorToInt(pointToConvert.x);
                    int breakGlobalY = Mathf.FloorToInt(pointToConvert.y);
                    int breakGlobalZ = Mathf.FloorToInt(pointToConvert.z);

                    effectPosition = new Vector3(breakGlobalX + 0.5f, breakGlobalY + 0.5f, breakGlobalZ + 0.5f);
                    idOfAffectedBlock = world.GetBlock(breakGlobalX, breakGlobalY, breakGlobalZ); 
                    
                    if(idOfAffectedBlock != 0) { // If not air
                        world.SetBlock(breakGlobalX, breakGlobalY, breakGlobalZ, 0); // Set to air
                        if (particleManager != null && blockTypeManager != null) {
                            particleManager.PlayBreakEffect(effectPosition, idOfAffectedBlock); 
                        } else if (particleManager != null) {
                            particleManager.PlayBreakEffect(effectPosition, 0); 
                        }
                    }
                }
            }
        }
    }
}