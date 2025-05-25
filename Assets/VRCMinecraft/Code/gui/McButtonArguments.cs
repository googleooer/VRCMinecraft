
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// Passes arguments to a SendCustomEvent method
/// Argument MUST match type, if you make a mistake i'll kill myself
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McButtonArguments : UdonSharpBehaviour
{
    void Start()
    {
        
    }
}
