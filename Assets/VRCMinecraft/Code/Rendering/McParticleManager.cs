using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// Manages all MC Beta 1.7.3 particle effects using native Unity ParticleSystems.
/// Each particle type has its own ParticleSystem configured in the Inspector.
/// Call SpawnParticle() to emit particles matching MC's RenderGlobal.spawnParticle dispatch.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McParticleManager : UdonSharpBehaviour
{
    // ── Particle type constants matching MC's string-based dispatch ──
    public const int PT_BUBBLE = 0;
    public const int PT_SMOKE = 1;
    public const int PT_LARGESMOKE = 2;
    public const int PT_NOTE = 3;
    public const int PT_PORTAL = 4;
    public const int PT_EXPLODE = 5;
    public const int PT_FLAME = 6;
    public const int PT_LAVA = 7;
    public const int PT_SPLASH = 8;
    public const int PT_REDDUST = 9;
    public const int PT_SNOWSHOVEL = 10;
    public const int PT_HEART = 11;
    public const int PT_RAIN = 12;

    // ── MC Particle Systems (auto-discovered from children in Start) ──
    private ParticleSystem smokePS;
    private ParticleSystem explodePS;
    private ParticleSystem flamePS;
    private ParticleSystem lavaPS;
    private ParticleSystem bubblePS;
    private ParticleSystem splashPS;
    private ParticleSystem heartPS;
    private ParticleSystem notePS;
    private ParticleSystem portalPS;
    private ParticleSystem reddustPS;
    private ParticleSystem snowPS;

    // ── Existing break/place/footstep systems ──

    [Header("Block Break/Place")]
    public ParticleSystem genericBreakParticles;
    public ParticleSystem genericPlaceParticles;

    [Header("Footstep")]
    public ParticleSystem persistentFootstepParticles;
    public float footstepSpeedThreshold = 0.5f;
    public float footstepInterval = 0.4f;
    public float footstepVerticalOffset = -1.0f;

    [Header("Particle Material")]
    public Material particleMaterial;
    public Material blockParticleMaterial;
    public Texture2D blockParticleAtlas;

    private VRCPlayerApi localPlayer;
    private float lastFootstepTime;
    private bool isInitialized;
    private AudioSource _audioSource;
    [SerializeField] private McBlockTypeManager blockTypeManager;
    [SerializeField] private McWorld world;

    // MC block IDs that emit ambient particles
    const byte BLOCK_LAVA_MOVING = 10;
    const byte BLOCK_LAVA_STILL = 11;
    const byte BLOCK_TORCH = 50;
    const byte BLOCK_FIRE = 51;
    const byte BLOCK_FURNACE_LIT = 62;
    const byte BLOCK_REDSTONE_ORE_LIT = 74;
    const byte BLOCK_REDSTONE_TORCH_ON = 76;
    const byte BLOCK_PORTAL = 90;
    const int PARTICLE_ATLAS_TILES = 16;
    const int PARTICLE_ATLAS_FRAME_SMOKE = 7;
    const int PARTICLE_ATLAS_FRAME_BUBBLE = 32;
    const int PARTICLE_ATLAS_FRAME_FLAME = 48;
    const int PARTICLE_ATLAS_FRAME_LAVA = 49;
    const int PARTICLE_ATLAS_FRAME_NOTE = 64;
    const int PARTICLE_ATLAS_FRAME_HEART = 80;
    const int PARTICLE_ATLAS_FRAME_RAIN_START = 19;
    const int PARTICLE_ATLAS_FRAME_SPLASH_START = 20;
    const int FACE_INDEX_TOP = 2;
    const int FACE_INDEX_BOTTOM = 3;
    const byte TORCH_MOUNT_WEST = 1;
    const byte TORCH_MOUNT_EAST = 2;
    const byte TORCH_MOUNT_NORTH = 3;
    const byte TORCH_MOUNT_SOUTH = 4;
    const byte TORCH_MOUNT_FLOOR = 5;
    const byte TORCH_MOUNT_CEILING = 6;

    void Start()
    {
        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
            Debug.LogError("[McParticleManager] AudioSource missing!");
        if (blockTypeManager == null)
            Debug.LogError("[McParticleManager] McBlockTypeManager not assigned!");

        if (persistentFootstepParticles != null)
        {
            var mainModule = persistentFootstepParticles.main;
            mainModule.simulationSpace = ParticleSystemSimulationSpace.World;
        }

        // Auto-discover child ParticleSystems by name
        smokePS = _FindChildPS("SmokePS");
        explodePS = _FindChildPS("ExplodePS");
        flamePS = _FindChildPS("FlamePS");
        lavaPS = _FindChildPS("LavaPS");
        bubblePS = _FindChildPS("BubblePS");
        splashPS = _FindChildPS("SplashPS");
        heartPS = _FindChildPS("HeartPS");
        notePS = _FindChildPS("NotePS");
        portalPS = _FindChildPS("PortalPS");
        reddustPS = _FindChildPS("ReddustPS");
        snowPS = _FindChildPS("SnowPS");

        // All gravity=0: MC uses per-tick discrete push, NOT continuous acceleration.
        // Upward/downward forces baked into initial velocity in emit methods.

        _ConfigurePSBase(smokePS, 500);
        _SetDrag(smokePS, 0.96f);

        _ConfigurePSBase(explodePS, 500);
        _SetDrag(explodePS, 0.9f);

        _ConfigurePSBase(flamePS, 500);
        _SetSizeOverLife(flamePS, AnimationCurve.Linear(0f, 1f, 1f, 0.5f));
        _SetDrag(flamePS, 0.96f);

        _ConfigurePSBase(lavaPS, 200);
        _SetSizeOverLife(lavaPS, AnimationCurve.Linear(0f, 1f, 1f, 0f));
        _SetDrag(lavaPS, 0.999f);
        _SetGravity(lavaPS, 0.03f); // lava has genuine downward pull

        _ConfigurePSBase(bubblePS, 200);
        _SetDrag(bubblePS, 0.85f);

        _ConfigurePSBase(splashPS, 200);
        _SetDrag(splashPS, 0.98f);

        _ConfigurePSBase(heartPS, 100);
        _SetDrag(heartPS, 0.86f);

        _ConfigurePSBase(notePS, 100);
        _SetDrag(notePS, 0.66f);

        _ConfigurePSBase(portalPS, 300);
        _SetSizeOverLife(portalPS, AnimationCurve.Linear(0f, 0f, 1f, 1f));

        _ConfigurePSBase(reddustPS, 300);
        _SetDrag(reddustPS, 0.96f);

        _ConfigurePSBase(snowPS, 200);
        _SetDrag(snowPS, 0.98f);

        // Animated: smoke/explode/reddust/snow cycle texIndex 7→0 over lifetime
        _SetParticleAtlasAnimated(smokePS, 0, 7);
        _SetParticleAtlasAnimated(explodePS, 0, 7);
        _SetParticleAtlasFrame(flamePS, PARTICLE_ATLAS_FRAME_FLAME);
        _SetParticleAtlasFrame(lavaPS, PARTICLE_ATLAS_FRAME_LAVA);
        _SetParticleAtlasFrame(bubblePS, PARTICLE_ATLAS_FRAME_BUBBLE);
        _SetParticleAtlasFrame(splashPS, PARTICLE_ATLAS_FRAME_SPLASH_START);
        _SetParticleAtlasFrame(heartPS, PARTICLE_ATLAS_FRAME_HEART);
        _SetParticleAtlasFrame(notePS, PARTICLE_ATLAS_FRAME_NOTE);
        _SetParticleAtlasFrame(portalPS, 0);
        _SetParticleAtlasAnimated(reddustPS, 0, 7);
        _SetParticleAtlasAnimated(snowPS, 0, 7);

        isInitialized = true;
    }

    ParticleSystem _FindChildPS(string childName)
    {
        Transform child = transform.Find(childName);
        if (child == null)
        {
            Debug.LogWarning("[McParticleManager] Child PS not found: " + childName);
            return null;
        }
        return child.GetComponent<ParticleSystem>();
    }

    void _ConfigurePSBase(ParticleSystem ps, int maxParticles)
    {
        if (ps == null) return;
        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.playOnAwake = false;
        main.loop = false;
        main.maxParticles = maxParticles;
        main.gravityModifier = 0f;
        main.startSpeed = 0f;
        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        shape.enabled = false;
        _AssignParticleMaterial(ps, null);
    }

    // Only used for lava which has a genuine downward pull (motionY -= 0.03 per tick)
    void _SetGravity(ParticleSystem ps, float mcGravPerTick)
    {
        if (ps == null) return;
        var main = ps.main;
        // MC: motionY -= mcGravPerTick each tick. Convert to Unity gravity modifier.
        // MC accel in blocks/s² = mcGravPerTick * 400. Unity: 9.81 * modifier.
        main.gravityModifier = mcGravPerTick * 400f / 9.81f;
    }

    void _SetDrag(ParticleSystem ps, float mcDragPerTick)
    {
        if (ps == null) return;
        // MC multiplies velocity by mcDragPerTick each tick (20 TPS).
        // Unity's LimitVelocityOverLifetime dampen removes a fraction per second.
        // Approximate: Unity dampen ≈ 1 - mcDragPerTick (applied differently but close)
        // More accurate: per-frame drag factor = mcDragPerTick^(20*dt).
        // Unity dampen = 1-drag^20 gives the per-second retention fraction.
        // Since Unity applies dampen as vel *= (1-dampen*dt) which isn't quite right,
        // use the LimitVelocityOverLifetime with a high speed limit and dampen factor.
        var lvol = ps.limitVelocityOverLifetime;
        lvol.enabled = true;
        lvol.separateAxes = false;
        lvol.limit = 100f;
        // dampen: fraction of excess speed removed per second
        // MC drag^20 = fraction retained per second. We want 1 - retained = removed.
        float retainedPerSec = Mathf.Pow(mcDragPerTick, TICK_RATE);
        lvol.dampen = 1f - retainedPerSec;
    }

    void _SetSizeOverLife(ParticleSystem ps, AnimationCurve curve)
    {
        if (ps == null) return;
        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, curve);
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
        _RandomDisplayTick();
    }

    // ════════════════════════════════════════════════════════════════
    // MC randomDisplayTick — ambient block particles
    // ════════════════════════════════════════════════════════════════

    // MC World.randomDisplayUpdates runs 1000 samples per FRAME (called from the render loop, not
    // the tick loop) using a triangular distribution that concentrates sampling near the player.
    //
    // PERF REWRITE: the literal port (1000 iterations x cross-behaviour world.GetBlock) cost
    // 25-80ms/frame in the Udon VM. Instead we walk the per-chunk ambient-emitter registry that
    // McWorld's derived-data scan collects (torch/fire/lava-surface/lit-furnace/redstone-torch-on/
    // portal) and fire each emitter with EXACTLY the probability MC's sampling would have hit it:
    //   P(hit emitter at offset dx,dy,dz) = 1000 * w(|dx|) * w(|dy|) * w(|dz|)
    //   where w(d) = (16-d)/256 = P[rand16 - rand16 == d]  (per-axis triangular weight)
    // Statistically identical particle output; cost scales with actual emitters in range (usually
    // a few dozen) instead of 1000 blind samples. deltaTime-scaled so the per-second rate matches
    // MC running at ambientParticleReferenceFps regardless of our frame rate.
    [Header("Ambient Particles")]
    public float ambientParticleReferenceFps = 60f;
    private const float AMBIENT_P_SCALE = 1000f / 16777216f; // 1000 * (1/256)^3 shared factor
    private const int AMBIENT_MAX_ITER_PER_CHUNK = 96;       // Poisson-thinning cap for dense chunks

    void _RandomDisplayTick()
    {
        if (world == null) return;
        ChunkData[] chunks = world.chunks_1D;
        if (chunks == null) return;

        Vector3 ppos = localPlayer.GetPosition();
        int px = Mathf.FloorToInt(ppos.x);
        int py = Mathf.FloorToInt(ppos.y);
        int pz = Mathf.FloorToInt(ppos.z);

        // Chunk-index math mirrors McWorld.GetChunkAt (fields read once per frame; element access
        // on the cached array reference is local, no cross-behaviour calls in the loop).
        int offX = world.chunkOffsetX, offY = world.chunkOffsetY, offZ = world.chunkOffsetZ;
        int dimX = world.worldDimensionX, dimY = world.worldDimensionY, dimZ = world.worldDimensionZ;

        float frameScale = Time.deltaTime * ambientParticleReferenceFps;
        if (frameScale > 4f) frameScale = 4f; // hitch guard: never burst more than ~4 frames worth
        float pShared = AMBIENT_P_SCALE * frameScale;

        // The +/-15 sample box spans at most the 3x3x3 chunk neighborhood around the player.
        int pcx = px >> 4, pcy = py >> 4, pcz = pz >> 4;
        for (int dcy = -1; dcy <= 1; dcy++)
        {
            int ay = pcy + dcy + offY;
            if (ay < 0 || ay >= dimY) continue;
            for (int dcz = -1; dcz <= 1; dcz++)
            {
                int az = pcz + dcz + offZ;
                if (az < 0 || az >= dimZ) continue;
                for (int dcx = -1; dcx <= 1; dcx++)
                {
                    int ax = pcx + dcx + offX;
                    if (ax < 0 || ax >= dimX) continue;
                    ChunkData chunk = chunks[(az * dimX * dimY) + (ay * dimX) + ax];
                    if (chunk == null || !chunk.isDataReady) continue;
                    int ec = chunk._ambientEmitterCount;
                    if (ec <= 0) continue;
                    int[] packed = chunk._ambientEmitterPacked;
                    if (packed == null) continue;

                    int baseX = (pcx + dcx) << 4;
                    int baseY = (pcy + dcy) << 4;
                    int baseZ = (pcz + dcz) << 4;

                    // Dense chunks (lava-lake surfaces): Poisson-thin — visit a random subset and
                    // boost each visit's probability by the skipped fraction. Same expected rate.
                    int iter = ec;
                    float boost = 1f;
                    bool subsample = false;
                    if (ec > AMBIENT_MAX_ITER_PER_CHUNK)
                    {
                        iter = AMBIENT_MAX_ITER_PER_CHUNK;
                        boost = ec / (float)AMBIENT_MAX_ITER_PER_CHUNK;
                        subsample = true;
                    }
                    for (int i = 0; i < iter; i++)
                    {
                        int p = subsample ? packed[Random.Range(0, ec)] : packed[i];
                        int wx = baseX + ((p >> 16) & 0xFF);
                        int wy = baseY + ((p >> 8) & 0xFF);
                        int wz = baseZ + (p & 0xFF);
                        int adx = wx - px; if (adx < 0) adx = -adx;
                        int ady = wy - py; if (ady < 0) ady = -ady;
                        int adz = wz - pz; if (adz < 0) adz = -adz;
                        if (adx > 15 || ady > 15 || adz > 15) continue;
                        float prob = pShared * boost * ((16 - adx) * (16 - ady) * (16 - adz));
                        if (Random.value >= prob) continue;
                        _BlockRandomDisplayTick((byte)((p >> 24) & 0xFF), wx, wy, wz);
                    }
                }
            }
        }
    }

    void _BlockRandomDisplayTick(byte blockId, int x, int y, int z)
    {
        switch (blockId)
        {
            case BLOCK_TORCH:
                _TickTorch(x, y, z);
                break;
            case BLOCK_FIRE:
                _TickFire(x, y, z);
                break;
            case BLOCK_FURNACE_LIT:
                _TickFurnace(x, y, z);
                break;
            case BLOCK_LAVA_MOVING:
            case BLOCK_LAVA_STILL:
                _TickLava(x, y, z);
                break;
            case BLOCK_PORTAL:
                _TickPortal(x, y, z);
                break;
            case BLOCK_REDSTONE_TORCH_ON:
                _TickRedstoneTorch(x, y, z);
                break;
        }
    }

    // MC BlockTorch.randomDisplayTick: flame + smoke at torch tip
    void _TickTorch(int x, int y, int z)
    {
        Vector3 torchTip = _GetTorchTipPosition(x, y, z);
        float tx = torchTip.x;
        float ty = torchTip.y;
        float tz = torchTip.z;

        SpawnParticle(PT_SMOKE, tx, ty, tz, 0f, 0f, 0f);
        SpawnParticle(PT_FLAME, tx, ty, tz, 0f, 0f, 0f);
    }

    // MC BlockFire.randomDisplayTick: largesmoke rising from fire
    void _TickFire(int x, int y, int z)
    {
        float fx = x + Random.value;
        float fy = y + Random.value + Random.value;
        float fz = z + Random.value;
        SpawnParticle(PT_LARGESMOKE, fx, fy, fz, 0f, 0f, 0f);
    }

    // MC BlockFurnace.randomDisplayTick: smoke + flame from front face
    void _TickFurnace(int x, int y, int z)
    {
        float fx = x + 0.5f;
        float fy = y + Random.value;
        float fz = z + 0.5f;

        // Simplified: emit from a random side. MC uses metadata for facing direction.
        float offset = Random.value * 0.6f - 0.3f;
        int side = Random.Range(0, 4);
        float sx = fx, sz = fz;
        if (side == 0) sz = z + 0.0f;
        else if (side == 1) sz = z + 1.0f;
        else if (side == 2) sx = x + 0.0f;
        else sx = x + 1.0f;

        SpawnParticle(PT_SMOKE, sx, fy, sz, 0f, 0f, 0f);
        SpawnParticle(PT_FLAME, sx, fy, sz, 0f, 0f, 0f);
    }

    // MC BlockFluid.randomDisplayTick for lava: lava ember particles
    void _TickLava(int x, int y, int z)
    {
        // MC spawns lava particles with ~10% chance per tick
        if (Random.value > 0.1f) return;

        // Check if block above is air
        byte above = (byte)(world.GetBlock(x, y + 1, z) & 0xFF);
        if (above != 0) return;

        float lx = x + Random.value;
        float ly = y + 1.02f;
        float lz = z + Random.value;
        SpawnParticle(PT_LAVA, lx, ly, lz, 0f, 0f, 0f);
    }

    // MC BlockPortal.randomDisplayTick: purple portal sparkles
    void _TickPortal(int x, int y, int z)
    {
        for (int i = 0; i < 4; i++)
        {
            float px = x + Random.value;
            float py = y + Random.value;
            float pz = z + Random.value;
            float vx = (Random.value - 0.5f) * 0.5f;
            float vy = (Random.value - 0.5f) * 0.5f;
            float vz = (Random.value - 0.5f) * 0.5f;
            SpawnParticle(PT_PORTAL, px, py, pz, vx, vy, vz);
        }
    }

    // MC BlockRedstoneTorch.randomDisplayTick: reddust particles
    void _TickRedstoneTorch(int x, int y, int z)
    {
        Vector3 torchTip = _GetTorchTipPosition(x, y, z);
        float rx = torchTip.x + (Random.value - 0.5f) * 0.2f;
        float ry = torchTip.y;
        float rz = torchTip.z + (Random.value - 0.5f) * 0.2f;
        SpawnParticle(PT_REDDUST, rx, ry, rz, 0f, 0f, 0f);
    }

    Vector3 _GetTorchTipPosition(int x, int y, int z)
    {
        byte mount = world != null ? world.GetTorchMount(x, y, z) : TORCH_MOUNT_FLOOR;
        // BlockTorch.randomDisplayTick: base = (x+0.5, y+0.7, z+0.5)
        // var13 = 0.22 (vertical offset for wall torches)
        // var15 = 0.27 (horizontal offset for wall torches)
        float tx = x + 0.5f;
        float ty = y + 0.7f;
        float tz = z + 0.5f;

        switch (mount)
        {
            case TORCH_MOUNT_WEST:  // metadata 1: -X wall
                tx -= 0.27f;
                ty += 0.22f;
                break;
            case TORCH_MOUNT_EAST:  // metadata 2: +X wall
                tx += 0.27f;
                ty += 0.22f;
                break;
            case TORCH_MOUNT_NORTH: // metadata 3: -Z wall
                tz -= 0.27f;
                ty += 0.22f;
                break;
            case TORCH_MOUNT_SOUTH: // metadata 4: +Z wall
                tz += 0.27f;
                ty += 0.22f;
                break;
            case TORCH_MOUNT_CEILING:
                ty = y + 0.4f;
                break;
            default: // metadata 5: floor torch — no offset
                break;
        }

        return new Vector3(tx, ty, tz);
    }

    // ════════════════════════════════════════════════════════════════
    // MC Particle Spawning API
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Spawn a particle by type ID. Position in world blocks, velocity in blocks/tick (MC units).
    /// Matches MC's RenderGlobal.spawnParticle dispatch table.
    /// </summary>
    public void SpawnParticle(int type, float x, float y, float z, float vx, float vy, float vz)
    {
        // MC skips particles > 16 blocks from camera
        if (localPlayer != null && localPlayer.IsValid())
        {
            Vector3 ppos = localPlayer.GetPosition();
            float dx = x - ppos.x, dy = y - ppos.y, dz = z - ppos.z;
            if (dx * dx + dy * dy + dz * dz > 256f) return;
        }

        switch (type)
        {
            case PT_SMOKE: _EmitSmoke(x, y, z, vx, vy, vz, 1.0f); break;
            case PT_LARGESMOKE: _EmitSmoke(x, y, z, vx, vy, vz, 2.5f); break;
            case PT_EXPLODE: _EmitExplode(x, y, z, vx, vy, vz); break;
            case PT_FLAME: _EmitFlame(x, y, z, vx, vy, vz); break;
            case PT_LAVA: _EmitLava(x, y, z); break;
            case PT_BUBBLE: _EmitBubble(x, y, z, vx, vy, vz); break;
            case PT_SPLASH: _EmitSplash(x, y, z, vx, vy, vz); break;
            case PT_HEART: _EmitHeart(x, y, z); break;
            case PT_NOTE: _EmitNote(x, y, z, vx); break;
            case PT_PORTAL: _EmitPortal(x, y, z, vx, vy, vz); break;
            case PT_REDDUST: _EmitReddust(x, y, z, vx, vy, vz); break;
            case PT_SNOWSHOVEL: _EmitSnowShovel(x, y, z, vx, vy, vz); break;
            case PT_RAIN: _EmitRain(x, y, z); break;
        }
    }

    /// <summary>
    /// String-based spawn matching MC's world.spawnParticle(String, ...) API.
    /// Slightly slower due to string comparison — prefer the int overload in hot paths.
    /// </summary>
    public void SpawnParticleByName(string name, float x, float y, float z, float vx, float vy, float vz)
    {
        int type = -1;
        if (name == "smoke") type = PT_SMOKE;
        else if (name == "largesmoke") type = PT_LARGESMOKE;
        else if (name == "explode") type = PT_EXPLODE;
        else if (name == "flame") type = PT_FLAME;
        else if (name == "lava") type = PT_LAVA;
        else if (name == "bubble") type = PT_BUBBLE;
        else if (name == "splash") type = PT_SPLASH;
        else if (name == "heart") type = PT_HEART;
        else if (name == "note") type = PT_NOTE;
        else if (name == "portal") type = PT_PORTAL;
        else if (name == "reddust") type = PT_REDDUST;
        else if (name == "snowshovel") type = PT_SNOWSHOVEL;
        else if (name == "rain") type = PT_RAIN;

        if (type >= 0)
            SpawnParticle(type, x, y, z, vx, vy, vz);
    }

    // ════════════════════════════════════════════════════════════════
    // Per-type emit methods — each matches its MC EntityFX subclass
    // ════════════════════════════════════════════════════════════════

    // MC motion is in blocks/tick. Unity velocity is blocks/second.
    // Conversion: unity_vel = mc_vel * 20
    // MC lifetime is in ticks. Unity is seconds.
    // Conversion: unity_life = mc_ticks / 20
    // MC particle visual size: diameter = 0.2 * particleScale
    const float TICK_RATE = 20f;
    const float SIZE_SCALE = 0.2f; // MC renders quads at 0.1 * particleScale half-size

    void _EmitSmoke(float x, float y, float z, float vx, float vy, float vz, float scale)
    {
        if (smokePS == null) return;
        // EntitySmokeFX: base motion * 0.1 then += var8.
        // Per-tick: motionY += 0.004 (floats up), drag 0.96.
        // Terminal upward vel ≈ 0.004/(1-0.96) = 0.1 blocks/tick.
        // Average over typical lifetime ≈ 0.03 blocks/tick = 0.6 blocks/sec.

        float grey = Random.value * 0.3f;
        float baseScale = (Random.value * 0.5f + 0.5f) * 2f * 0.75f * scale;
        float maxAge = (8.0f / (Random.value * 0.8f + 0.2f)) * scale;

        // MC base motion: random ~±0.06 blocks/tick, then *0.1 = ~±0.006
        float bmx = (Random.value * 2f - 1f) * 0.006f;
        float bmy = 0.01f; // base EntityFX adds 0.1 to motionY, then *0.1
        float bmz = (Random.value * 2f - 1f) * 0.006f;

        // Add the per-tick push as avg initial velocity (0.03 blocks/tick upward)
        float pushY = 0.03f;

        _DoEmit(smokePS,
            x, y, z,
            (bmx + vx) * TICK_RATE, (bmy + pushY + vy) * TICK_RATE, (bmz + vz) * TICK_RATE,
            baseScale * SIZE_SCALE,
            new Color(grey, grey, grey, 1f),
            maxAge / TICK_RATE);
    }

    void _EmitExplode(float x, float y, float z, float vx, float vy, float vz)
    {
        if (explodePS == null) return;
        // EntityExplodeFX: per-tick motionY += 0.004, drag 0.9.

        float addX = (Random.value * 2f - 1f) * 0.05f;
        float addZ = (Random.value * 2f - 1f) * 0.05f;
        float grey = Random.value * 0.3f + 0.7f;
        float baseScale = Random.value * Random.value * 6.0f + 1.0f;
        float maxAge = 16.0f / (Random.value * 0.8f + 0.2f) + 2f;

        // Per-tick push 0.004 up, avg ≈ 0.02 blocks/tick with 0.9 drag
        float pushY = 0.02f;

        _DoEmit(explodePS,
            x, y, z,
            (vx + addX) * TICK_RATE, (vy + pushY + (Random.value * 2f - 1f) * 0.05f) * TICK_RATE, (vz + addZ) * TICK_RATE,
            baseScale * SIZE_SCALE,
            new Color(grey, grey, grey, 1f),
            maxAge / TICK_RATE);
    }

    void _EmitFlame(float x, float y, float z, float vx, float vy, float vz)
    {
        if (flamePS == null) return;
        // EntityFlameFX: motionX = motionX*0.01 + var8, no gravity, drag 0.96

        float baseScale = (Random.value * 0.5f + 0.5f) * 2f;
        float maxAge = 8.0f / (Random.value * 0.8f + 0.2f) + 4f;

        // MC base: random motion * 0.01 is negligible
        _DoEmit(flamePS,
            x, y, z,
            vx * TICK_RATE, vy * TICK_RATE, vz * TICK_RATE,
            baseScale * SIZE_SCALE,
            Color.white,
            maxAge / TICK_RATE);
    }

    void _EmitLava(float x, float y, float z)
    {
        if (lavaPS == null) return;
        // EntityLavaFX: motionY = rand*0.4+0.05, arcs up then falls
        // Scale: base * (rand*2+0.2), shrinks as (1-t²)
        // MaxAge: 16/(rand*0.8+0.2) ticks
        // Self-lit (brightness = 1.0)
        // Spawns smoke sub-particles during flight (handled by lavaPS sub-emitter or code)

        float baseScale = (Random.value * 0.5f + 0.5f) * 2f * (Random.value * 2f + 0.2f);
        float maxAge = 16.0f / (Random.value * 0.8f + 0.2f);
        float vy = (Random.value * 0.4f + 0.05f) * TICK_RATE;

        _DoEmit(lavaPS,
            x, y, z,
            0f, vy, 0f,
            baseScale * SIZE_SCALE,
            Color.white,
            maxAge / TICK_RATE);
    }

    void _EmitBubble(float x, float y, float z, float vx, float vy, float vz)
    {
        if (bubblePS == null) return;
        // EntityBubbleFX: per-tick motionY += 0.002, drag 0.85

        float scaleMul = Random.value * 0.6f + 0.2f;
        float baseScale = (Random.value * 0.5f + 0.5f) * 2f * scaleMul;
        float maxAge = 8.0f / (Random.value * 0.8f + 0.2f);

        float ux = vx * 0.2f + (Random.value * 2f - 1f) * 0.02f;
        float uy = vy * 0.2f + (Random.value * 2f - 1f) * 0.02f;
        float uz = vz * 0.2f + (Random.value * 2f - 1f) * 0.02f;

        // Per-tick push 0.002 up, avg ≈ 0.013 blocks/tick with 0.85 drag
        float pushY = 0.013f;

        _DoEmit(bubblePS,
            x, y, z,
            ux * TICK_RATE, (uy + pushY) * TICK_RATE, uz * TICK_RATE,
            baseScale * SIZE_SCALE,
            Color.white,
            maxAge / TICK_RATE);
    }

    void _EmitSplash(float x, float y, float z, float vx, float vy, float vz)
    {
        if (splashPS == null) return;
        // EntitySplashFX extends EntityRainFX: gravity 0.04, small
        // MaxAge: 8/(rand*0.8+0.2)

        float baseScale = (Random.value * 0.5f + 0.5f) * 2f;
        float maxAge = 8.0f / (Random.value * 0.8f + 0.2f);

        float ux = vx, uy = vy, uz = vz;
        if (vy == 0f && (vx != 0f || vz != 0f))
            uy = 0.1f;

        _DoEmit(splashPS,
            x, y, z,
            ux * TICK_RATE, (uy + 0.1f) * TICK_RATE, uz * TICK_RATE,
            baseScale * SIZE_SCALE * 0.5f,
            Color.white,
            maxAge / TICK_RATE);
    }

    void _EmitRain(float x, float y, float z)
    {
        if (splashPS == null) return;
        // EntityRainFX: gravity 0.06, motionY = rand*0.2+0.1
        float baseScale = (Random.value * 0.5f + 0.5f) * 2f;
        float maxAge = 8.0f / (Random.value * 0.8f + 0.2f);
        float vy = (Random.value * 0.2f + 0.1f) * TICK_RATE;

        _DoEmit(splashPS,
            x, y, z,
            0f, vy, 0f,
            baseScale * SIZE_SCALE * 0.5f,
            Color.white,
            maxAge / TICK_RATE);
    }

    void _EmitHeart(float x, float y, float z)
    {
        if (heartPS == null) return;
        // EntityHeartFX: motionY += 0.1, drag 0.86, grows
        // MaxAge: 16 ticks = 0.8 seconds

        float baseScale = (Random.value * 0.5f + 0.5f) * 2f * 0.75f * 2.0f;

        _DoEmit(heartPS,
            x, y, z,
            0f, 0.1f * TICK_RATE, 0f,
            baseScale * SIZE_SCALE,
            Color.white,
            16f / TICK_RATE);
    }

    void _EmitNote(float x, float y, float z, float pitch)
    {
        if (notePS == null) return;
        // EntityNoteFX: color = rainbow from pitch (0-1), motionY += 0.2, drag 0.66
        // MaxAge: 6 ticks = 0.3 seconds

        float r = Mathf.Sin((pitch + 0.0f) * Mathf.PI * 2f) * 0.65f + 0.35f;
        float g = Mathf.Sin((pitch + 1f / 3f) * Mathf.PI * 2f) * 0.65f + 0.35f;
        float b = Mathf.Sin((pitch + 2f / 3f) * Mathf.PI * 2f) * 0.65f + 0.35f;

        float baseScale = (Random.value * 0.5f + 0.5f) * 2f * 0.75f * 2.0f;

        _DoEmit(notePS,
            x, y, z,
            0f, 0.2f * TICK_RATE, 0f,
            baseScale * SIZE_SCALE,
            new Color(r, g, b, 1f),
            6f / TICK_RATE);
    }

    void _EmitPortal(float x, float y, float z, float vx, float vy, float vz)
    {
        if (portalPS == null) return;
        // EntityPortalFX: purple color (r*0.9, g*0.3, b=1) * (rand*0.6+0.4)
        // motionX/Y/Z = var8/10/12 (velocity IS the offset from origin)
        // MaxAge: rand*10+40 ticks, noClip
        // Parametric motion converging toward origin (approximated by velocity drift)

        float brightness = Random.value * 0.6f + 0.4f;
        float r = 0.9f * brightness;
        float g = 0.3f * brightness;
        float b = 1.0f * brightness;

        float baseScale = Random.value * 0.2f + 0.5f;
        float maxAge = Random.value * 10f + 40f;

        // MC portal particles move FROM spawn TOWARD origin via parametric curve.
        // Approximate: set velocity pointing back toward spawn with drift
        _DoEmit(portalPS,
            x, y, z,
            vx * TICK_RATE, vy * TICK_RATE, vz * TICK_RATE,
            baseScale * SIZE_SCALE,
            new Color(r, g, b, 1f),
            maxAge / TICK_RATE);
    }

    void _EmitReddust(float x, float y, float z, float vr, float vg, float vb)
    {
        if (reddustPS == null) return;
        // EntityReddustFX: color from params (vx=r, vy=g, vz=b), animated 7→0
        // Scale: base * 0.75
        // MaxAge: 8/(rand*0.8+0.2) ticks

        // MC: if var9(green)==0, set to 1 (avoids invisible particles)
        if (vg == 0f) vg = 1f;

        float var12 = Random.value * 0.4f + 0.6f;
        float r = (Random.value * 0.2f + 0.8f) * vr * var12;
        float g = (Random.value * 0.2f + 0.8f) * vg * var12;
        float b = (Random.value * 0.2f + 0.8f) * vb * var12;

        float baseScale = (Random.value * 0.5f + 0.5f) * 2f * 0.75f;
        float maxAge = 8.0f / (Random.value * 0.8f + 0.2f);

        _DoEmit(reddustPS,
            x, y, z,
            0f, 0f, 0f,
            baseScale * SIZE_SCALE,
            new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f),
            maxAge / TICK_RATE);
    }

    void _EmitSnowShovel(float x, float y, float z, float vx, float vy, float vz)
    {
        if (snowPS == null) return;
        // EntitySnowShovelFX: white-ish (1 - rand*0.3), animated 7→0, falls
        // MaxAge: 8/(rand*0.8+0.2) ticks

        float white = 1.0f - Random.value * 0.3f;
        float baseScale = (Random.value * 0.5f + 0.5f) * 2f * 0.75f;
        float maxAge = 8.0f / (Random.value * 0.8f + 0.2f);

        _DoEmit(snowPS,
            x, y, z,
            vx * TICK_RATE, vy * TICK_RATE, vz * TICK_RATE,
            baseScale * SIZE_SCALE,
            new Color(white, white, white, 1f),
            maxAge / TICK_RATE);
    }

    // ── Core emit helper ──

    private bool _hasLoggedFirstEmit;

    void _DoEmit(ParticleSystem ps, float x, float y, float z,
                 float vx, float vy, float vz,
                 float size, Color color, float lifetime)
    {
        if (ps == null) return;
        if (!_hasLoggedFirstEmit)
        {
            Debug.Log("[McParticleManager] First particle emit: " + ps.name);
            _hasLoggedFirstEmit = true;
        }

        ParticleSystem.EmitParams emit = new ParticleSystem.EmitParams();
        emit.position = new Vector3(x, y, z);
        emit.velocity = new Vector3(vx, vy, vz);
        emit.startSize = size;
        emit.startColor = color;
        emit.startLifetime = lifetime;
        emit.applyShapeToPosition = false;
        ps.Emit(emit, 1);
    }

    // ════════════════════════════════════════════════════════════════
    // Block destroy/hit effects (MC EffectRenderer equivalent)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// MC EffectRenderer.addBlockDestroyEffects — spawns 4x4x4 grid of block-texture particles.
    /// Currently delegates to Unity ParticleSystem; will add terrain-atlas sampling later.
    /// </summary>
    public void AddBlockDestroyEffects(int bx, int by, int bz, int blockId)
    {
        if (blockId == 0) return;
        Vector3 center = new Vector3(bx + 0.5f, by + 0.5f, bz + 0.5f);
        PlayBreakEffect(center, (byte)(blockId & 0xFF));
    }

    /// <summary>
    /// MC EffectRenderer.addBlockHitEffects — spawns a single particle on the hit face.
    /// </summary>
    public void AddBlockHitEffects(int bx, int by, int bz, int face)
    {
        if (world == null) return;
        int blockId = world.GetBlock(bx, by, bz) & 0xFF;
        if (blockId == 0) return;

        float px = bx + Random.value * 0.8f + 0.1f;
        float py = by + Random.value * 0.8f + 0.1f;
        float pz = bz + Random.value * 0.8f + 0.1f;

        if (face == 0) py = by - 0.1f;
        if (face == 1) py = by + 1.1f;
        if (face == 2) pz = bz - 0.1f;
        if (face == 3) pz = bz + 1.1f;
        if (face == 4) px = bx - 0.1f;
        if (face == 5) px = bx + 1.1f;

        PlayBreakEffect(new Vector3(px, py, pz), (byte)blockId);
    }

    // ════════════════════════════════════════════════════════════════
    // Existing break/place/footstep (preserved from original)
    // ════════════════════════════════════════════════════════════════

    public void PlayBreakEffect(Vector3 position, byte blockID)
    {
        if (!isInitialized) return;
        ParticleSystem particlesToPlay = null;
        AudioClip soundToPlay = null;

        if (blockTypeManager != null)
        {
            particlesToPlay = blockTypeManager.GetBreakParticlesPrefab(blockID);
            soundToPlay = blockTypeManager.GetBreakSound(blockID);
        }

        if (particlesToPlay != null)
        {
            GameObject psInstance = Instantiate(particlesToPlay.gameObject);
            if (psInstance != null)
            {
                psInstance.transform.position = position;
                ParticleSystem actualPS = psInstance.GetComponent<ParticleSystem>();
                if (actualPS != null)
                {
                    _PrepareInstantiatedParticleSystem(actualPS);
                    _ConfigureBlockEffectTexture(actualPS, blockID, _PickBreakParticleFace());
                    actualPS.Play();
                }
            }
        }
        else if (genericBreakParticles != null)
        {
            _PrepareReusableParticleSystem(genericBreakParticles);
            _ConfigureBlockEffectTexture(genericBreakParticles, blockID, _PickBreakParticleFace());
            genericBreakParticles.transform.position = position;
            genericBreakParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            genericBreakParticles.Play();
        }

        if (soundToPlay != null && _audioSource != null)
            _audioSource.PlayOneShot(soundToPlay);
    }

    public void PlayPlaceEffect(Vector3 position, byte blockID)
    {
        if (!isInitialized) return;
        ParticleSystem particlesToPlay = null;
        AudioClip soundToPlay = null;

        if (blockTypeManager != null)
        {
            particlesToPlay = blockTypeManager.GetPlaceParticlesPrefab(blockID);
            soundToPlay = blockTypeManager.GetPlaceSound(blockID);
        }

        if (particlesToPlay != null)
        {
            GameObject psInstance = Instantiate(particlesToPlay.gameObject);
            if (psInstance != null)
            {
                psInstance.transform.position = position;
                ParticleSystem actualPS = psInstance.GetComponent<ParticleSystem>();
                if (actualPS != null)
                {
                    _PrepareInstantiatedParticleSystem(actualPS);
                    _ConfigureBlockEffectTexture(actualPS, blockID, FACE_INDEX_TOP);
                    actualPS.Play();
                }
            }
        }
        else if (genericPlaceParticles != null)
        {
            _PrepareReusableParticleSystem(genericPlaceParticles);
            _ConfigureBlockEffectTexture(genericPlaceParticles, blockID, FACE_INDEX_TOP);
            genericPlaceParticles.transform.position = position;
            genericPlaceParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            genericPlaceParticles.Play();
        }

        if (soundToPlay != null && _audioSource != null)
            _audioSource.PlayOneShot(soundToPlay);
    }

    private void HandleFootsteps()
    {
        if (persistentFootstepParticles == null || !localPlayer.IsValid() || !localPlayer.IsPlayerGrounded())
        {
            if (persistentFootstepParticles != null && persistentFootstepParticles.isPlaying)
                persistentFootstepParticles.Stop();
            return;
        }

        Vector3 playerVelocity = localPlayer.GetVelocity();
        Vector3 horizontalVelocity = Vector3.ProjectOnPlane(playerVelocity, Vector3.up);

        if (horizontalVelocity.magnitude > footstepSpeedThreshold)
        {
            if (Time.time - lastFootstepTime > footstepInterval)
            {
                Vector3 playerPos = localPlayer.GetPosition();
                Vector3 footstepPos = playerPos + new Vector3(0, footstepVerticalOffset, 0);

                persistentFootstepParticles.transform.position = footstepPos;
                if (horizontalVelocity.sqrMagnitude > 0.0001f)
                    persistentFootstepParticles.transform.rotation = Quaternion.LookRotation(horizontalVelocity);

                if (!persistentFootstepParticles.isPlaying) persistentFootstepParticles.Play();

                if (blockTypeManager != null && world != null && _audioSource != null)
                {
                    Vector3 blockPos = playerPos + new Vector3(0, footstepVerticalOffset - 0.1f, 0);
                    int gx = Mathf.FloorToInt(blockPos.x);
                    int gy = Mathf.FloorToInt(blockPos.y);
                    int gz = Mathf.FloorToInt(blockPos.z);
                    byte blockID = (byte)(world.GetBlock(gx, gy, gz) & 0xFF);
                    _PrepareReusableParticleSystem(persistentFootstepParticles);
                    _ConfigureBlockEffectTexture(persistentFootstepParticles, blockID, FACE_INDEX_TOP);
                    AudioClip footstepSound = blockTypeManager.GetFootstepSound(blockID);
                    if (footstepSound != null) _audioSource.PlayOneShot(footstepSound);
                }
                lastFootstepTime = Time.time;
            }
        }
        else
        {
            if (persistentFootstepParticles.isPlaying) persistentFootstepParticles.Stop();
        }
    }

    void _PrepareInstantiatedParticleSystem(ParticleSystem ps)
    {
        if (ps == null) return;

        ParticleSystem.MainModule main = ps.main;
        main.stopAction = ParticleSystemStopAction.Destroy;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        _AssignParticleMaterial(ps, null);
    }

    void _PrepareReusableParticleSystem(ParticleSystem ps)
    {
        if (ps == null) return;

        ParticleSystem.MainModule main = ps.main;
        main.stopAction = ParticleSystemStopAction.None;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
    }

    void _AssignParticleMaterial(ParticleSystem ps, Texture mainTextureOverride)
    {
        if (ps == null || particleMaterial == null) return;

        ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
        if (renderer == null) return;

        renderer.material = particleMaterial;
        renderer.material.SetTextureScale("_MainTex", Vector2.one);
        renderer.material.SetTextureOffset("_MainTex", Vector2.zero);
        if (mainTextureOverride != null)
            renderer.material.SetTexture("_MainTex", mainTextureOverride);
    }

    void _SetParticleAtlasFrame(ParticleSystem ps, int frameIndex)
    {
        _SetParticleAtlasFrame(ps, frameIndex, PARTICLE_ATLAS_TILES, PARTICLE_ATLAS_TILES);
    }

    void _SetParticleAtlasFrame(ParticleSystem ps, int frameIndex, int tilesX, int tilesY)
    {
        if (ps == null) return;

        ParticleSystem.TextureSheetAnimationModule uv = ps.textureSheetAnimation;
        uv.enabled = true;
        uv.numTilesX = tilesX;
        uv.numTilesY = tilesY;
        uv.cycleCount = 1;
        uv.startFrame = new ParticleSystem.MinMaxCurve((float)frameIndex / (tilesX * tilesY));
        uv.frameOverTime = new ParticleSystem.MinMaxCurve(0f);
    }

    // MC animated particles cycle from startFrame down to endFrame over lifetime.
    // e.g., smoke cycles texIndex 7→0 (8 frames)
    void _SetParticleAtlasAnimated(ParticleSystem ps, int endFrame, int startFrame)
    {
        if (ps == null) return;
        ParticleSystem.TextureSheetAnimationModule uv = ps.textureSheetAnimation;
        uv.enabled = true;
        uv.numTilesX = PARTICLE_ATLAS_TILES;
        uv.numTilesY = PARTICLE_ATLAS_TILES;
        uv.cycleCount = 1;
        int totalFrames = PARTICLE_ATLAS_TILES * PARTICLE_ATLAS_TILES;
        float startNorm = (float)startFrame / totalFrames;
        float endNorm = (float)endFrame / totalFrames;
        // startFrame is the FIRST frame shown, endFrame is the LAST frame shown
        uv.startFrame = new ParticleSystem.MinMaxCurve(startNorm);
        uv.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, (endNorm - startNorm)));
    }

    void _ConfigureBlockEffectTexture(ParticleSystem ps, byte blockID, int faceIndex)
    {
        if (ps == null || blockTypeManager == null || blockParticleAtlas == null) return;

        int textureSlice = blockTypeManager.GetFinalBlockTextureSlice(blockID, faceIndex);
        _AssignBlockParticleMaterial(ps);
        _SetParticleAtlasFrame(ps, _GetBlockParticleFrame(textureSlice), 64, 64);
    }

    void _AssignBlockParticleMaterial(ParticleSystem ps)
    {
        if (ps == null || blockParticleAtlas == null) return;

        ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
        if (renderer == null) return;

        Material sourceMaterial = blockParticleMaterial != null ? blockParticleMaterial : particleMaterial;
        if (sourceMaterial == null) return;

        renderer.material = sourceMaterial;
        renderer.material.SetTextureScale("_MainTex", Vector2.one);
        renderer.material.SetTextureOffset("_MainTex", Vector2.zero);
        if (blockParticleMaterial == null)
            renderer.material.SetTexture("_MainTex", blockParticleAtlas);
    }

    int _GetBlockParticleFrame(int textureSlice)
    {
        const int blockFragmentTilesPerAxis = PARTICLE_ATLAS_TILES * 4;
        int frameCount = PARTICLE_ATLAS_TILES * PARTICLE_ATLAS_TILES;
        if (textureSlice < 0) return 0;

        int clampedSlice = textureSlice % frameCount;
        int column = clampedSlice % PARTICLE_ATLAS_TILES;
        int row = clampedSlice / PARTICLE_ATLAS_TILES;
        int fragmentColumn = column * 4 + Random.Range(0, 4);
        int fragmentRow = row * 4 + Random.Range(0, 4);

        // MC dig particles sample a random quarter of the source tile, not the whole tile.
        return ((blockFragmentTilesPerAxis - 1 - fragmentRow) * blockFragmentTilesPerAxis) + fragmentColumn;
    }

    int _PickBreakParticleFace()
    {
        int face = Random.Range(0, 6);
        if (face == FACE_INDEX_BOTTOM && Random.value < 0.5f) return FACE_INDEX_TOP;
        return face;
    }
}
