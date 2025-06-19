using UdonSharp;
using UnityEngine;
using VRC.Udon;

/// <summary>
/// This coroutine handles the sliced, asynchronous mesh generation for a single McChunk.
/// It is started by McChunk and runs across multiple frames to avoid performance spikes.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class BuildChunkMeshCoroutine : BaseUdonCoroutine
{
    // Executes when a script calls StartUdonCoroutine().
    // Should be used to initialize your coroutine.
    protected override void Setup() { }
    
    // Executes every frame, returning 'true' when this Udon Coroutine is considered complete.
    // Due to the slow speed of Udon, Tick() should be as fast as possible.
    protected override bool Tick() { return true; }
    
    // Internal callback when this Udon Coroutine is complete.
    // Should be used to clean up resources used during the execution of this coroutine.
    protected override void OnCompletion() { }


}
