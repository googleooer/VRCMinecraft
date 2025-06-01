using UdonSharp;
using UnityEngine;
using VRC.SDKBase; 
// using VRRefAssist; // If McBlockTypeManager uses [Singleton] from VRRefAssist

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McParticleManager : UdonSharpBehaviour
{
    [Header("Particle System References (Fallback/Generic)")]
    [Tooltip("Fallback particle system if block-specific one is not found for breaking.")]
    public ParticleSystem genericBreakParticles;
    [Tooltip("Fallback particle system if block-specific one is not found for placing.")]
    public ParticleSystem genericPlaceParticles;
    // [Tooltip("GameObject Prefab with a ParticleSystem for footsteps. This will be instantiated.")]
    // public GameObject footstepParticlePrefab; // Removed

    [Header("Persistent Particle Systems")] // New Header
    [Tooltip("Persistent ParticleSystem in the scene for footsteps. Should be configured as a one-shot effect.")]
    public ParticleSystem persistentFootstepParticles; // New field

    // Audio Clip Arrays are removed; sounds will be fetched from McBlockTypeManager
    // Header("Audio Clip Arrays (Randomized)")
    // public AudioClip[] breakAudioClips;
    // public AudioClip[] placeAudioClips;
    // public AudioClip[] footstepAudioClips;

    [Header("Footstep Settings")]
    public float footstepSpeedThreshold = 0.5f;
    public float footstepInterval = 0.4f;
    public float footstepVerticalOffset = -1.0f;

    private VRCPlayerApi localPlayer;
    private float lastFootstepTime = 0f;
    private bool isInitialized = false;
    private AudioSource _audioSource; 
    [SerializeField] private McBlockTypeManager blockTypeManager; // Reference to the singleton
    private McWorld world; // Reference to McWorld to get block ID under player

    void Start()
    {
        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
        {
            Debug.LogError("[McParticleManager] AudioSource component missing on this GameObject! Add an AudioSource component to enable sound effects.");
        }

        // Get McBlockTypeManager instance
        if (blockTypeManager == null) {
            Debug.LogError("[McParticleManager] Could not find McBlockTypeManager instance! Block-specific sounds/particles will not work.");
            
        }
        
        // Get McWorld instance (needed for footsteps to determine block type under player)
        // This assumes McWorld is also a singleton or easily findable.
        // If McWorld is not a singleton, this reference needs to be assigned (e.g. via Inspector).
        // For simplicity, let's assume you might assign it or it's part of a game manager.
        // A more robust way: have a GameManager that provides these refs.
        GameObject worldGO = GameObject.Find("McWorld"); // Example: Find by name, not ideal for performance/reliability
        if (worldGO != null) {
            world = worldGO.GetComponent<McWorld>();
        }
        if (world == null) {
             Debug.LogWarning("[McParticleManager] McWorld instance not found. Footstep sounds based on block type will not work.");
        }


        if (genericBreakParticles == null) Debug.LogWarning($"[McParticleManager] GenericBreakParticles not assigned.");
        if (genericPlaceParticles == null) Debug.LogWarning($"[McParticleManager] GenericPlaceParticles not assigned.");
        // if (footstepParticlePrefab == null) Debug.LogWarning($"[McParticleManager] FootstepParticlePrefab not assigned."); // Removed check
        if (persistentFootstepParticles == null) Debug.LogWarning("[McParticleManager] PersistentFootstepParticles not assigned!"); // New check
        
        isInitialized = true;
    }

    void Update()
    {
        if (!isInitialized) return;

        if (localPlayer == null)
        {
            localPlayer = Networking.LocalPlayer;
            if (localPlayer == null) return;
        }

        HandleFootsteps();
    }

    private void HandleFootsteps()
    {
        if (persistentFootstepParticles == null || !localPlayer.IsValid() || !localPlayer.IsPlayerGrounded()) return;

        Vector3 playerVelocity = localPlayer.GetVelocity(); // Get velocity once

        if (playerVelocity.magnitude > footstepSpeedThreshold)
        {
            if (Time.time - lastFootstepTime > footstepInterval)
            {
                Vector3 horizontalVelocity = Vector3.ProjectOnPlane(playerVelocity, Vector3.up);

                // Only play footstep effect if there is significant horizontal movement
                if (horizontalVelocity.sqrMagnitude > 0.01f) 
                {
                    Vector3 playerReportedPosition = localPlayer.GetPosition();
                    Vector3 footstepEffectPosition = playerReportedPosition + new Vector3(0, footstepVerticalOffset, 0);
                    
                    persistentFootstepParticles.transform.position = footstepEffectPosition;
                    persistentFootstepParticles.transform.rotation = Quaternion.LookRotation(horizontalVelocity);
                    persistentFootstepParticles.Play();

                    if (blockTypeManager != null && world != null && _audioSource != null) {
                        Vector3 blockPosUnderPlayer = playerReportedPosition + new Vector3(0, footstepVerticalOffset - 0.1f, 0); 
                        int globalX = Mathf.FloorToInt(blockPosUnderPlayer.x);
                        int globalY = Mathf.FloorToInt(blockPosUnderPlayer.y);
                        int globalZ = Mathf.FloorToInt(blockPosUnderPlayer.z);
                        byte blockID = world.GetBlock(globalX, globalY, globalZ); 
                        AudioClip footstepSound = blockTypeManager.GetFootstepSound(blockID); 
                        if (footstepSound != null) _audioSource.PlayOneShot(footstepSound);
                    }
                    lastFootstepTime = Time.time; // Update time only if effect was played
                }
                // If no significant horizontal movement, we effectively skip this footstep occasion.
                // lastFootstepTime is not updated, so if player starts moving horizontally soon, it can trigger.
            }
        }
    }

    // MODIFIED: Method signatures now take blockID
    public void PlayBreakEffect(Vector3 position, byte blockID)
    {
        if (!isInitialized) return;
        ParticleSystem particlesToPlay = null;
        AudioClip soundToPlay = null;

        if (blockTypeManager != null) {
            particlesToPlay = blockTypeManager.GetBreakParticlesPrefab(blockID);
            soundToPlay = blockTypeManager.GetBreakSound(blockID); // Using renamed GetBreakSound
        }

        if (particlesToPlay != null) {
            // If it's a prefab, instantiate it. If it's a scene reference, teleport and play.
            // Assuming GetBreakParticlesPrefab returns a prefab that should be instantiated.
            GameObject psInstance = Instantiate(particlesToPlay.gameObject);
            if(psInstance != null) {
                psInstance.transform.position = position;
                ParticleSystem actualPS = psInstance.GetComponent<ParticleSystem>();
                if(actualPS != null) {
                    ParticleSystem.MainModule main = actualPS.main;
                    main.stopAction = ParticleSystemStopAction.Destroy;
                    actualPS.Play();
                }
            }
        } else if (genericBreakParticles != null) { // Fallback
            genericBreakParticles.transform.position = position;
            genericBreakParticles.Play();
        }

        if (soundToPlay != null && _audioSource != null) {
            _audioSource.PlayOneShot(soundToPlay);
        }
    }

    public void PlayPlaceEffect(Vector3 position, byte blockID)
    {
        if (!isInitialized) return;
        ParticleSystem particlesToPlay = null;
        AudioClip soundToPlay = null;

        if (blockTypeManager != null) {
            particlesToPlay = blockTypeManager.GetPlaceParticlesPrefab(blockID);
            soundToPlay = blockTypeManager.GetPlaceSound(blockID); // Using renamed GetPlaceSound
        }
        
        if (particlesToPlay != null) {
            GameObject psInstance = Instantiate(particlesToPlay.gameObject);
             if(psInstance != null) {
                psInstance.transform.position = position;
                ParticleSystem actualPS = psInstance.GetComponent<ParticleSystem>();
                if(actualPS != null) {
                    ParticleSystem.MainModule main = actualPS.main;
                    main.stopAction = ParticleSystemStopAction.Destroy;
                    actualPS.Play();
                }
            }
        } else if (genericPlaceParticles != null) { // Fallback
            genericPlaceParticles.transform.position = position;
            genericPlaceParticles.Play();
        }

        if (soundToPlay != null && _audioSource != null) {
            _audioSource.PlayOneShot(soundToPlay);
        }
    }
}
