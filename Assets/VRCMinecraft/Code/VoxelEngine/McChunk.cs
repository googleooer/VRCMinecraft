#define LOGGING

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using System.Text;

/// <summary>
/// This version of McChunk features a fully time-sliced generation and meshing pipeline.
///
/// 1.  GREEDY MESHING: A more advanced algorithm that iterates boundaries, not voxels.
/// 2.  MULTI-LAYER RLE: Advanced Run-Length Encoding compresses chunk data for
///     maximum memory efficiency.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McChunk : UdonSharpBehaviour
{
}
