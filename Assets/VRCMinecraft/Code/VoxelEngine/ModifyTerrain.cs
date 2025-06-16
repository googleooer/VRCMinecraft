using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRRefAssist;
using System.Text; // Added for StringBuilder

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

    [Header("Visuals")]
    [SerializeField] private Transform blockOutline;

    [Header("Logging")]
    #if UNITY_EDITOR
    public bool enableVerboseLogging = true; // Toggle for logging
    #endif

    private VRCPlayerApi localPlayer;
    private bool isInitialized = false;
    private VRCPlayerApi.TrackingData playerHeadData; // Not used directly for raycast origin after change

    private bool currentFrameHitValid;
    private RaycastHit currentFrameHitInfo;

    private const float RAYCAST_OFFSET_ALONG_NORMAL = 0.01f;
    private const float RAYCAST_OFFSET_INTO_BLOCK = 0.01f;
    
    private StringBuilder logBuilder;


    void Start()
    {
        float startTime = Time.realtimeSinceStartup;
        logBuilder = new StringBuilder(256); // Initialize StringBuilder

        localPlayer = Networking.LocalPlayer;
        if (localPlayer == null)
        {
            SendCustomEventDelayedSeconds(nameof(_InitializePlayer), 1.0f);
        }
        else
        {
            _InitializePlayer();
        }
#if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            logBuilder.Clear();
            logBuilder.AppendFormat("[ModifyTerrain.Start] Initialization scheduled/called. Time: {0:F2} ms.", (Time.realtimeSinceStartup - startTime) * 1000f);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }

    public void _InitializePlayer()
    {
        float startTime = Time.realtimeSinceStartup;
        if (isInitialized) return;

        localPlayer = Networking.LocalPlayer;
        if (localPlayer == null)
        {
            Debug.LogError("[ModifyTerrain._InitializePlayer] LocalPlayer API is null after delay. Interaction will not work.");
            return;
        }

        // playerHeadData = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head); // Storing this might not be necessary if always getting fresh data

        
        if (world == null) Debug.LogError("[ModifyTerrain._InitializePlayer] McWorld reference is not set!");
        if (blockTypeManager == null) Debug.LogWarning("[ModifyTerrain._InitializePlayer] McBlockTypeManager reference not set.");
        if (particleManager == null) Debug.LogWarning("[ModifyTerrain._InitializePlayer] McParticleManager reference not set.");
        
        // Inherit logging state from world if possible
        // if (world != null) enableVerboseLogging = world.enableVerboseLogging;


        isInitialized = true;
#if UNITY_EDITOR
        if (enableVerboseLogging)
        {
            logBuilder.Clear();
            logBuilder.AppendFormat("[ModifyTerrain._InitializePlayer] Complete. Time: {0:F2} ms.", (Time.realtimeSinceStartup - startTime) * 1000f);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }

    public void Update()
    {
        if (!isInitialized || localPlayer == null || world == null) return;

        float updateStartTime = Time.realtimeSinceStartup;
        _UpdateInteractionRaycast();
        _HandleBlockOutline(); // Outline update is usually very fast, direct profiling might be overkill
        _HandleBlockPlacementInput(); // This is where significant action happens

#if UNITY_EDITOR
        if (enableVerboseLogging && (Time.realtimeSinceStartup - updateStartTime) * 1000f > 2f) // Log if update took > 2ms
        {
            logBuilder.Clear();
            logBuilder.AppendFormat("[ModifyTerrain.Update] Frame update took {0:F2} ms.", (Time.realtimeSinceStartup - updateStartTime) * 1000f);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }

    private void _UpdateInteractionRaycast()
    {
        // Raycasting is usually fast, but can be profiled if it becomes a bottleneck.
        VRCPlayerApi.TrackingData headData = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        Vector3 rayOrigin = headData.position;
        Quaternion rayRotation = headData.rotation;
        Vector3 rayDirection = rayRotation * Vector3.forward;
        currentFrameHitValid = Physics.Raycast(rayOrigin, rayDirection, out currentFrameHitInfo, blockInteractionRange, terrainLayerMask);
    }

    private void _HandleBlockOutline()
    { 
        if (currentFrameHitValid)
        {;
            Vector3 pointToConvert = currentFrameHitInfo.point - currentFrameHitInfo.normal * RAYCAST_OFFSET_INTO_BLOCK;
            blockOutline.position = new Vector3(Mathf.FloorToInt(pointToConvert.x) + 0.5f, Mathf.FloorToInt(pointToConvert.y) + 0.5f, Mathf.FloorToInt(pointToConvert.z) + 0.5f);
        }
        else
        {
            blockOutline.position = new Vector3(512,512,512);
        }
    }

    private void _HandleBlockPlacementInput()
    {
        float methodStartTime = Time.realtimeSinceStartup;
        bool secondaryAction = Input.GetMouseButtonDown(0) || Input.GetAxisRaw("Oculus_CrossPlatform_PrimaryIndexTrigger") > 0.5f;
        bool primaryAction = Input.GetMouseButtonDown(1) || Input.GetAxisRaw("Oculus_CrossPlatform_SecondaryIndexTrigger") > 0.5f;

        if (primaryAction || secondaryAction)
        {
            if (currentFrameHitValid)
            {
                RaycastHit hit = currentFrameHitInfo;
                Vector3 pointToConvert;
                Vector3 effectPosition;
                byte idOfAffectedBlock;
                string actionType = ""; // For logging

                if (primaryAction) // Placing block
                {
                    actionType = "Place";
                    pointToConvert = hit.point + hit.normal * RAYCAST_OFFSET_ALONG_NORMAL;
                    int placeGlobalX = Mathf.FloorToInt(pointToConvert.x);
                    int placeGlobalY = Mathf.FloorToInt(pointToConvert.y);
                    int placeGlobalZ = Mathf.FloorToInt(pointToConvert.z);

                    Vector3 playerHeadPos = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
                    Vector3Int playerVoxelPos = new Vector3Int(Mathf.FloorToInt(playerHeadPos.x), Mathf.FloorToInt(playerHeadPos.y), Mathf.FloorToInt(playerHeadPos.z));
                    Vector3Int playerFeetVoxelPos = new Vector3Int(Mathf.FloorToInt(playerHeadPos.x), Mathf.FloorToInt(playerHeadPos.y - 1.7f), Mathf.FloorToInt(playerHeadPos.z));

                    if ((placeGlobalX == playerVoxelPos.x && placeGlobalY == playerVoxelPos.y && placeGlobalZ == playerVoxelPos.z) ||
                        (placeGlobalX == playerFeetVoxelPos.x && placeGlobalY == playerFeetVoxelPos.y && placeGlobalZ == playerFeetVoxelPos.z))
                    {
#if UNITY_EDITOR
                        if (enableVerboseLogging) Debug.Log("[ModifyTerrain._HandleBlockPlacementInput] Attempted to place block in self. Ignored.");
#endif
                        return;
                    }

                    world.SetBlock(placeGlobalX, placeGlobalY, placeGlobalZ, blockTypeToPlace, true);
                    effectPosition = new Vector3(placeGlobalX + 0.5f, placeGlobalY + 0.5f, placeGlobalZ + 0.5f);
                    byte idOfBlockToSetForPlacing = blockTypeToPlace;
                    if (particleManager != null)
                    {
                        particleManager.PlayPlaceEffect(effectPosition, blockTypeManager != null ? idOfBlockToSetForPlacing : (byte)0);
                    }
#if UNITY_EDITOR
                     if (enableVerboseLogging)
                    {
                        logBuilder.Clear();
                        logBuilder.AppendFormat("[ModifyTerrain] Placed block ID {0} at G({1},{2},{3}).",
                            blockTypeToPlace, placeGlobalX, placeGlobalY, placeGlobalZ);
                         Debug.Log(logBuilder.ToString());
                    }
#endif
                }
                else // Breaking block
                {
                    actionType = "Break";
                    pointToConvert = hit.point - hit.normal * RAYCAST_OFFSET_INTO_BLOCK;
                    int breakGlobalX = Mathf.FloorToInt(pointToConvert.x);
                    int breakGlobalY = Mathf.FloorToInt(pointToConvert.y);
                    int breakGlobalZ = Mathf.FloorToInt(pointToConvert.z);

                    effectPosition = new Vector3(breakGlobalX + 0.5f, breakGlobalY + 0.5f, breakGlobalZ + 0.5f);
                    idOfAffectedBlock = (byte)(world.GetBlock(breakGlobalX, breakGlobalY, breakGlobalZ) & 0xFF);

                    if (idOfAffectedBlock != 0)
                    {
                        world.SetBlock(breakGlobalX, breakGlobalY, breakGlobalZ, 0, true);
                        if (particleManager != null)
                        {
                            particleManager.PlayBreakEffect(effectPosition, blockTypeManager != null ? idOfAffectedBlock : (byte)0);
                        }
#if UNITY_EDITOR
                        if (enableVerboseLogging)
                        {
                            logBuilder.Clear();
                            logBuilder.AppendFormat("[ModifyTerrain] Broke block ID {0} at G({1},{2},{3}).",
                                idOfAffectedBlock, breakGlobalX, breakGlobalY, breakGlobalZ);
                            Debug.Log(logBuilder.ToString());
                        }
#endif
                    }
                    else
                    {
                        actionType = "Break (Air)"; // No action if breaking air
                    }
                }
#if UNITY_EDITOR
                if (enableVerboseLogging)
                {
                    logBuilder.Clear();
                    logBuilder.AppendFormat("[ModifyTerrain._HandleBlockPlacementInput] Action: {0}. Hit: {1}. Point: {2}. Normal: {3}. Time: {4:F3} ms.",
                        actionType, hit.collider != null ? hit.collider.name : "None", hit.point, hit.normal, (Time.realtimeSinceStartup - methodStartTime) * 1000f);
                    Debug.Log(logBuilder.ToString());
                }
#endif
            }
        }
    }
}
