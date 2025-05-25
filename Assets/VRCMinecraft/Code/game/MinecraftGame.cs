
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRRefAssist;

public enum Gamemode
{
    MENU,
    SURVIVAL,
    CREATIVE
}

[Singleton]
public class MinecraftGame : UdonSharpBehaviour
{
    [SerializeField] public MusicManager musicManager;
    public bool debugMode = false;
    public Gamemode currentGamemode = Gamemode.SURVIVAL;
    void Start()
    {
        
    }
}
