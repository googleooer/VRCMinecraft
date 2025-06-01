using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McStructureTemplate : UdonSharpBehaviour
{
    [Header("Structure Definition")]
    public string structureName = "Unnamed Structure";
    // The GameObject this script is attached to IS the representationPrefab.

    [Header("Placement Rules")]
    [Tooltip("How common is this structure? Higher value = more common. e.g., 0.01 = 1% chance per eligible spot.")]
    public float spawnChance = 0.01f; 
    [Tooltip("Minimum Y level (global voxel coordinate, centered world) this structure can spawn its origin at.")]
    public int minYSpawnLevel = 0;
    [Tooltip("Maximum Y level (global voxel coordinate, centered world) this structure can spawn its origin at.")]
    public int maxYSpawnLevel = 64;
    [Tooltip("Does this structure require solid ground directly beneath its origin?")]
    public bool requiresSolidGround = true;
    [Tooltip("How many layers of solid ground are needed if requiresSolidGround is true?")]
    public int solidGroundDepth = 1;

    [Tooltip("Salt value to alter randomness for this specific template. Allows multiple templates to make different placement decisions even with same world seed and chunk coordinates.")]
    public int placementSalt = 0;

    [Tooltip("The specific block ID this structure must spawn on. Use -1 for any block.")]
    public int requiredSpawnBlockID = -1; // Default to any block

    // --- NEW: Baked Voxel Data ---
    [Header("Baked Structure Data (Do not edit manually)")]
    [Tooltip("Relative positions of each voxel in this structure. Populated by the 'Bake Structure' button.")]
    [HideInInspector] public Vector3Int[] bakedVoxelPositions;
    [Tooltip("Block IDs for each corresponding voxel position. Populated by the 'Bake Structure' button.")]
    [HideInInspector] public byte[] bakedVoxelBlockIDs;

    void Start()
    {
        // This script is primarily a data container.
        gameObject.SetActive(false); // Templates should always be disabled in the scene at runtime
    }
}
