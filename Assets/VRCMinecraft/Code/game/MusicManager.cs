
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
        // Note: Random.Range(int, int) upper bound is EXCLUSIVE — must pass .Length, not .Length-1,
        // otherwise the last track in the array can never be selected.
        switch (gamemode)
        {
            default:
                if (survivalTracks != null && survivalTracks.Length > 0)
                {
                    musicAudioSource.clip = survivalTracks[Random.Range(0, survivalTracks.Length)];
                    musicAudioSource.Play();
                }
                break;
            case Gamemode.CREATIVE:
                if (creativeTracks != null && creativeTracks.Length > 0)
                {
                    musicAudioSource.clip = creativeTracks[Random.Range(0, creativeTracks.Length)];
                    musicAudioSource.Play();
                }
                break;
            case Gamemode.MENU:
                if (menuTracks != null && menuTracks.Length > 0)
                {
                    musicAudioSource.clip = menuTracks[Random.Range(0, menuTracks.Length)];
                    musicAudioSource.Play();
                }
                break;
        }

    }
}
