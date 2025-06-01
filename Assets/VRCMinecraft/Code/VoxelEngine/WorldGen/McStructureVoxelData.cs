using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McStructureVoxelData : UdonSharpBehaviour
{
    [Tooltip("The Block ID this voxel represents (from McBlockTypeManager).")]
    public byte blockID = 1; // Default to stone, for example
    
    // You could add other per-voxel properties for structures if needed, like rotation or specific states.
    // For now, just blockID.
}
