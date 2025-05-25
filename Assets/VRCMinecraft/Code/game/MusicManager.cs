
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRRefAssist;


public enum MusicType{
    SURVIVAL,
    CREATIVE,
    SURVIVAL_AND_CREATIVE,
    MENU
}

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
[Singleton]
public class MusicManager : UdonSharpBehaviour
{
    [SerializeField] private MinecraftGame minecraftGame;
    [SerializeField, GetComponentInChildren] private AudioSource musicAudioSource;

    public AudioClip[] survivalTracks;
    public AudioClip[] creativeTracks;
    public AudioClip[] menuTracks;

    public bool isMusicPlaying()
    {
        return musicAudioSource.isPlaying;
    }

    public void playRandomTrack(Gamemode gamemode)
    {
        if(isMusicPlaying()) return;
        musicAudioSource.Stop();
        musicAudioSource.time = 0;
        switch (gamemode)
        {
            default:
                musicAudioSource.clip = survivalTracks[Random.Range(0, survivalTracks.Length-1)];
                musicAudioSource.Play();
                break;
            case Gamemode.CREATIVE:
                musicAudioSource.clip = creativeTracks[Random.Range(0, creativeTracks.Length-1)];
                musicAudioSource.Play();
                break;
            case Gamemode.MENU:
                musicAudioSource.clip = menuTracks[Random.Range(0, menuTracks.Length-1)];
                musicAudioSource.Play();
                break;
        }
        
    }
}
