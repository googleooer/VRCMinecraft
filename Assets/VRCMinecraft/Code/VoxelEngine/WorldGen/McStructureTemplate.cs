using UdonSharp;
using UnityEngine;
using System.Text; // Added for StringBuilder, though not strictly needed here unless more complex logs are added

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McStructureTemplate : UdonSharpBehaviour
{
    [Header("Structure Definition")]
    public string structureName = "Unnamed Structure";

    [Header("Placement Rules")]
    public float spawnChance = 0.01f;
    public int minYSpawnLevel = 0;
    public int maxYSpawnLevel = 64;
    public bool requiresSolidGround = true;
    public int solidGroundDepth = 1;
    public int placementSalt = 0;
    public int requiredSpawnBlockID = -1;

    [Header("Baked Structure Data (Do not edit manually)")]
    [HideInInspector] public Vector3Int[] bakedVoxelPositions;
    [HideInInspector] public byte[] bakedVoxelBlockIDs;
    
    // Logging - not much runtime logic here, but can add a StringBuilder if needed for future.
    // public bool enableVerboseLogging = true; // Could be added if runtime logic increases
    // private StringBuilder logBuilder;


    void Start()
    {
        // This script is primarily a data container.
        // Runtime logging is minimal unless specific operations are added.
        // If complex Start logic were added, profiling and logging would go here.
        // logBuilder = new StringBuilder(128);

        // Example:
#if UNITY_EDITOR
        // if(enableVerboseLogging) {
        //    logBuilder.Clear();
        //    logBuilder.AppendFormat("[McStructureTemplate:{0}] Started.", structureName);
        //    Debug.Log(logBuilder.ToString());
        // }
#endif
        gameObject.SetActive(false); // Templates should always be disabled in the scene at runtime
    }
}
