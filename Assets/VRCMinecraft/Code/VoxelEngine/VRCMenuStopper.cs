
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRRefAssist;


/// <summary>
/// Determines if the menu is open
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
[Singleton]
public class VRCMenuStopper : UdonSharpBehaviour
{
    public bool isMenuOpen = false;
}
