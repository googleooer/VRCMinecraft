using UnityEngine;
using System; // Required for [Serializable]

/// <summary>
/// Defines a structure or feature (like a tree) to be placed in the world.
/// This is a plain C# class for data storage.
/// </summary>
[Serializable]
public class VoxelStructureDefinition
{
    public string structureName = "Unnamed Structure";
    [Tooltip("The prefab that visually represents this structure. Children of this prefab will be scanned for voxel data.")]
    public GameObject representationPrefab; 

    [Header("Placement Rules")]
    [Tooltip("How common is this structure? Higher value = more common. e.g., 0.01 = 1% chance per eligible spot.")]
    public float spawnChance = 0.01f; // Chance to spawn at an eligible location
    [Tooltip("Minimum Y level (global voxel coordinate) this structure can spawn at.")]
    public int minYSpawnLevel = 0;
    [Tooltip("Maximum Y level (global voxel coordinate) this structure can spawn at.")]
    public int maxYSpawnLevel = 64;
    [Tooltip("Does this structure require solid ground directly beneath its origin?")]
    public bool requiresSolidGround = true;
    [Tooltip("How many layers of solid ground are needed if requiresSolidGround is true?")]
    public int solidGroundDepth = 1;
    [Tooltip("Allow this structure to overhang or float slightly if its base is supported?")]
    public bool allowOverhang = false;
    // Future: Biome restrictions, min/max distance from other structures, etc.

    // Voxel data for the structure (populated by scanning the prefab, or manually defined)
    // This part is more complex to handle directly in UdonSharp for large structures.
    // For now, we'll focus on the definition. The generator will handle processing.
    // One approach: An editor script could bake this into a simple byte[] or Vector3Int[]/byte[] pair.
    // For simplicity here, the McTerrainGenerator will contain the logic to scan the prefab at placement time.
}
