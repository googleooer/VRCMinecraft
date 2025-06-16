using UdonSharp;
using UnityEngine;
using System.Text;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class BuildMeshForChunkCoroutine : BaseUdonCoroutine
{
    protected override bool Tick()
    {
        return true;
    }
}