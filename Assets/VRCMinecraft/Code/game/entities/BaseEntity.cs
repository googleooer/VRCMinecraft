
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class BaseEntity : UdonSharpBehaviour
{
    [SerializeField] private MinecraftGame minecraftGame;
    void Start()
    {
        
    }
}
