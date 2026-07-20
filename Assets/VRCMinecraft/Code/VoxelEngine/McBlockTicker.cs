#define LOGGING

using UdonSharp;
using UnityEngine;
using VRRefAssist;
using System.Text;

/// <summary>
/// Implements Beta 1.7.3's two block tick systems:
/// 1. Scheduled ticks (deterministic, e.g. sand falling after neighbor change)
/// 2. Random ticks (probabilistic, e.g. grass spread, leaf decay)
/// Ported 1:1 from deobfuscated Beta 1.7.3 source.
/// </summary>
[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McBlockTicker : UdonSharpBehaviour
{
    [Header("References")]
    [SerializeField, FindObjectOfType(true)]
    private McWorld world;
    [SerializeField, FindObjectOfType(true)]
    private McBlockTypeManager blockTypeManager;

    [Header("Performance Tuning")]
    // CODE-OWNED TUNING: [System.NonSerialized] so the code initializers are authoritative and
    // scene/inspector values can't silently override tuning changes (see McWorld counterparts).
    // Max scheduled ticks to process per world-tick. Raised from 200: a large receding fluid body
    // schedules many ticks and lava (tickRate 30) drains slowly; too small a budget lets the heap
    // grow toward TICK_QUEUE_CAPACITY and overflow-drop reschedules. Time-budget still caps cost.
    [System.NonSerialized] public int scheduledTickBudget = 2048;
    // Max time in ms for scheduled tick processing per world-tick
    [System.NonSerialized] public float scheduledTickTimeBudgetMs = 2.0f;
    // Strict b1.7.3 parity: MC random-ticks EVERY loaded section EVERY tick (80 picks per
    // 16x16x128 section => ~10 picks per 16^3 chunk). Cover ALL resident chunks each world-tick
    // (round-robin + budget so a break resumes next tick). The old 4-chunks/tick throttle starved
    // the random-tick fallback, which is the ONLY re-trigger for a sticky-stalled (unscheduled)
    // lava cell after its recession front passes -> isolated lava that never dried up. PERF: this
    // is far more random-tick work than before; measure on Quest and dial randomTickChunksPerFrame /
    // randomTickTimeBudgetMs back if it costs FPS.
    [System.NonSerialized] public int randomTickChunksPerFrame = 4096;
    // Random voxel picks per chunk (MC: 80 per 16x16x128 section => ~10 per 16^3 sub-chunk)
    [System.NonSerialized] public int randomTicksPerChunk = 10;
    // Max time in ms for random tick processing per world-tick
    [System.NonSerialized] public float randomTickTimeBudgetMs = 2.0f;
    [Tooltip("Enable block tick processing")]
    public bool enableBlockTicks = true;

    [Tooltip("DIAGNOSTIC: log the fluid flood-fill decision (per-direction neighbor/below/cost/optimal) " +
        "for every horizontally-flowing fluid cell. Enable briefly, reproduce the bad flow, then disable.")]
    public bool debugFluidFlow = false;

    // --- Block ID constants (Beta 1.7.3) ---
    private const byte B_AIR = 0;
    private const byte B_STONE = 1;
    private const byte B_GRASS = 2;
    private const byte B_DIRT = 3;
    private const byte B_COBBLESTONE = 4;
    private const byte B_PLANKS = 5;
    private const byte B_SAPLING = 6;
    private const byte B_BEDROCK = 7;
    private const byte B_WATER_FLOWING = 8;
    private const byte B_WATER_STILL = 9;
    private const byte B_LAVA_FLOWING = 10;
    private const byte B_LAVA_STILL = 11;
    private const byte B_SAND = 12;
    private const byte B_GRAVEL = 13;
    private const byte B_LOG = 17;
    private const byte B_LEAVES = 18;
    private const byte B_SPONGE = 19;
    private const byte B_GLASS = 20;
    private const byte B_TALLGRASS = 31;
    private const byte B_DEADBUSH = 32;
    private const byte B_FLOWER_DANDELION = 37;
    private const byte B_FLOWER_ROSE = 38;
    private const byte B_MUSHROOM_BROWN = 39;
    private const byte B_MUSHROOM_RED = 40;
    private const byte B_TNT = 46;
    private const byte B_BOOKSHELF = 47;
    private const byte B_OBSIDIAN = 49;
    private const byte B_TORCH = 50;
    private const byte B_FIRE = 51;
    private const byte B_CROPS = 59;
    private const byte B_FARMLAND = 60;
    private const byte B_DOOR_WOOD = 64;
    private const byte B_LADDER = 65;
    private const byte B_RAIL = 66;
    private const byte B_DOOR_IRON = 71;
    private const byte B_REDSTONE_TORCH_OFF = 75;
    private const byte B_REDSTONE_TORCH_ON = 76;
    private const byte B_SNOW_LAYER = 78;
    private const byte B_ICE = 79;
    private const byte B_CACTUS = 81;
    private const byte B_REED = 83;
    private const byte B_FENCE = 85;
    private const byte B_NETHERRACK = 87;
    private const byte B_CLOTH = 35;
    private const byte B_SIGN = 63;

    // --- Scheduled Tick Min-Heap (sorted by scheduledFrame, MC-matching dedup) ---
    private const int TICK_QUEUE_CAPACITY = 32768; // raised from 8192: a large receding lava body
                                                   // (tickRate 30 holds ticks ~30 world-ticks) could
                                                   // overflow and silently drop reschedules, leaving
                                                   // cells that never re-tick. MC never drops.
    private int[] _tickX;
    private int[] _tickY;
    private int[] _tickZ;
    private byte[] _tickBlockId;
    private int[] _tickScheduledFrame;
    // Monotonic insertion sequence per queued tick. MC's NextTickListEntry sorts by
    // (scheduledTime, field_235_e) where field_235_e is a monotonic insertion id, so ties on the
    // same tick process in INSERTION ORDER. A plain scheduledFrame heap breaks ties arbitrarily,
    // which makes fluids fill cells in a different order -> different decay/path than vanilla. The
    // heap compares (scheduledFrame, seq) so same-frame ticks pop FIFO exactly like MC.
    private long[] _tickSeq;
    private long _tickSeqCounter = 0;
    private int _tickCount = 0;
    private int _currentFrame = 0;

    // --- Fixed 20 TPS world-tick gate ---
    // MC runs scheduled + random block ticks at a FIXED 20 ticks/sec. This behaviour must be
    // decoupled from the render framerate: the tick DELAYS (5=water, 30=lava, 3=sand, 40=fire)
    // are MC tick counts, so advancing one "frame" per render frame made fluids/fire/crops run
    // ~3.6x too fast at 72fps and framerate-dependent (lava flow was the most obvious).
    private const float MC_TICK_SECONDS = 0.05f; // 1/20s
    private const int MAX_CATCHUP_TICKS = 4;     // cap ticks/frame so a hitch can't avalanche
    // Sticky-stalled lava retry interval (see the parity shim in _UpdateTick_FlowingFluid):
    // mean 205 + 410/2 = 410 ticks = Beta's per-block random-tick expectation (32768/80).
    private const int LAVA_STICKY_RETRY_MIN_TICKS = 205;
    private const int LAVA_STICKY_RETRY_SPREAD_TICKS = 410;
    private float _tickAccumulator = 0f;

    // --- Random Tick State ---
    private int _randomTickSeed = 0;
    private int _randomTickChunkCursor = 0;

    // --- Fire Burn Rate Tables (from BlockFire.initializeBlock) ---
    private int[] _chanceToEncourageFire;
    private int[] _abilityToCatchFire;

    // --- Falling Block Entity Pool ---
    [Header("Falling Block Entities")]
    [Tooltip("Pre-placed cube GameObjects to use as falling block visuals. 16 recommended.")]
    [SerializeField] private Transform[] fallingBlockPool;
    [SerializeField] private MeshRenderer[] fallingBlockRenderers;
    private const int FALL_POOL_MAX = 16;
    private float[] _fallPosX;
    private float[] _fallPosY;
    private float[] _fallPosZ;
    private float[] _fallVelY;
    private byte[] _fallBlockId;
    private bool[] _fallActive;
    private int[] _fallTicks;

#if LOGGING
    // --- Profiling Stats ---
    private float stats_scheduledTickTimeMs;
    private float stats_meshFlushTimeMs;
    private float stats_randomTickTimeMs;
    private float stats_fallingBlockTimeMs;
    private int stats_scheduledTicksProcessed;

    private int stats_randomTicksProcessed;
    private int stats_randomTickChunksScanned;
    private int stats_tickFrames;
    private int stats_queuePeakCount;
    private int stats_queueOverflows;
    private int stats_fallingBlockSpawns;
    private int stats_fallingBlockLandings;
    private int stats_fallingBlockPoolExhausted;
    private int stats_activeFallingPeak;

    // Per-type dispatch counters
    private int stats_dispatchWaterFlowing;
    private int stats_dispatchLavaFlowing;
    private int stats_dispatchLavaStill;
    private int stats_dispatchFire;
    private int stats_dispatchSand;
    private int stats_dispatchGrass;
    private int stats_dispatchLeaves;
    private int stats_dispatchCactus;
    private int stats_dispatchReed;
    private int stats_dispatchSapling;
    private int stats_dispatchFarmland;
    private int stats_dispatchIce;
    private int stats_dispatchSnow;
    private int stats_dispatchCrops;
    private int stats_dispatchOther;
#endif

    // --- Initialization ---
    private bool _initialized = false;
    private int _numAdjacentSources = 0;

    // PERF: Pre-baked random-tick predicate as a flat array. Inner loop replaces
    // a 15-case switch with a single array index — Udon's switch is interpreted as
    // a branch chain, so this is many-times faster in hot tick loops.
    private bool[] _isRandomTickBlockLut;

    // PERF: Open-addressed hash set for tick dedup (replaces O(N) linear scan in
    // ScheduleBlockUpdate). Beta uses HashSet&lt;NextTickListEntry&gt;.contains() for the
    // same reason. Key = pack(x, y, z, blockId). Size kept power-of-two for &amp;-mask probing.
    private const int TICK_HASH_CAPACITY = 65536; // 2x queue capacity, power of two
    private const int TICK_HASH_MASK = TICK_HASH_CAPACITY - 1;
    // Deleted-slot marker. 0 = empty (terminates a probe); real keys are always negative (OR'd with
    // long.MinValue in _TickHashKey), so 1L is distinct from both. A popped entry becomes a TOMBSTONE
    // (not 0) so probe chains stay intact — writing 0 would short-circuit find() for any colliding key
    // further down the chain, silently dropping its next re-schedule (a fluid cell that never re-ticks
    // -> water that doesn't recede).
    private const long TICK_HASH_TOMBSTONE = 1L;
    private long[] _tickHashKeys;     // 0 = empty slot, TICK_HASH_TOMBSTONE = deleted, else a key
    private int[] _tickHashIndices;   // index into _tickX/_tickY/.../_tickScheduledFrame

    void Start()
    {
        if (world == null || blockTypeManager == null)
        {
            Debug.LogError("[McBlockTicker] Missing references. Disabling.");
            enabled = false;
            return;
        }

        _tickX = new int[TICK_QUEUE_CAPACITY];
        _tickY = new int[TICK_QUEUE_CAPACITY];
        _tickZ = new int[TICK_QUEUE_CAPACITY];
        _tickBlockId = new byte[TICK_QUEUE_CAPACITY];
        _tickScheduledFrame = new int[TICK_QUEUE_CAPACITY];
        _tickSeq = new long[TICK_QUEUE_CAPACITY];

        _tickHashKeys = new long[TICK_HASH_CAPACITY];
        _tickHashIndices = new int[TICK_HASH_CAPACITY];

        _chanceToEncourageFire = new int[256];
        _abilityToCatchFire = new int[256];
        _InitFireBurnRates();

        // Pre-bake the random-tick predicate. setTickOnLoad(true) blocks from Beta:
        _isRandomTickBlockLut = new bool[256];
        _isRandomTickBlockLut[B_GRASS] = true;
        _isRandomTickBlockLut[B_SAPLING] = true;
        _isRandomTickBlockLut[B_LEAVES] = true;
        _isRandomTickBlockLut[B_WATER_FLOWING] = true;
        _isRandomTickBlockLut[B_LAVA_FLOWING] = true;
        _isRandomTickBlockLut[B_LAVA_STILL] = true;
        _isRandomTickBlockLut[B_FIRE] = true;
        _isRandomTickBlockLut[B_CROPS] = true;
        _isRandomTickBlockLut[B_FARMLAND] = true;
        _isRandomTickBlockLut[B_SNOW_LAYER] = true;
        _isRandomTickBlockLut[B_ICE] = true;
        _isRandomTickBlockLut[B_CACTUS] = true;
        _isRandomTickBlockLut[B_REED] = true;
        _isRandomTickBlockLut[B_MUSHROOM_BROWN] = true;
        _isRandomTickBlockLut[B_MUSHROOM_RED] = true;

        _flowDirResult = new bool[4];
        _flowCostResult = new int[4];
        _flowOffX = new int[] { -1, 1, 0, 0 };
        _flowOffZ = new int[] { 0, 0, -1, 1 };
        _flowOpposites = new int[] { 1, 0, 3, 2 };

        _bcValid = new bool[BC_TOTAL];
        _bcPosX = new int[BC_TOTAL];
        _bcPosY = new int[BC_TOTAL];
        _bcPosZ = new int[BC_TOTAL];
        _bcBlock = new byte[BC_TOTAL];
        _bcMeta = new byte[BC_TOTAL];

        _fallPosX = new float[FALL_POOL_MAX];
        _fallPosY = new float[FALL_POOL_MAX];
        _fallPosZ = new float[FALL_POOL_MAX];
        _fallVelY = new float[FALL_POOL_MAX];
        _fallBlockId = new byte[FALL_POOL_MAX];
        _fallActive = new bool[FALL_POOL_MAX];
        _fallTicks = new int[FALL_POOL_MAX];
        if (fallingBlockPool != null)
        {
            for (int i = 0; i < fallingBlockPool.Length && i < FALL_POOL_MAX; i++)
            {
                if (fallingBlockPool[i] != null)
                    fallingBlockPool[i].gameObject.SetActive(false);
            }
        }

        _randomTickSeed = (int)(Time.realtimeSinceStartup * 1000f) ^ 0x5DEECE66;
        _initialized = true;
    }

    private void _InitFireBurnRates()
    {
        // Exact values from BlockFire.initializeBlock in Beta 1.7.3
        _SetBurnRate(B_PLANKS, 5, 20);
        _SetBurnRate(B_FENCE, 5, 20);
        // stairCompactPlanks = 53
        _SetBurnRate(53, 5, 20);
        _SetBurnRate(B_LOG, 5, 5);
        _SetBurnRate(B_LEAVES, 30, 60);
        _SetBurnRate(B_BOOKSHELF, 30, 20);
        _SetBurnRate(B_TNT, 15, 100);
        _SetBurnRate(B_TALLGRASS, 60, 100);
        _SetBurnRate(B_CLOTH, 30, 60);
    }

    private void _SetBurnRate(int blockId, int encouragement, int flammability)
    {
        if (blockId < 0 || blockId >= 256) return;
        _chanceToEncourageFire[blockId] = encouragement;
        _abilityToCatchFire[blockId] = flammability;
    }

    // --- Public API ---

    /// <summary>
    /// Schedule a block update after delayTicks frames.
    /// Equivalent to MC's World.scheduleBlockUpdate.
    /// </summary>
    public void ScheduleBlockUpdate(int gx, int gy, int gz, byte blockId, int delayTicks)
    {
        if (!_initialized) return;
        if (_tickCount >= TICK_QUEUE_CAPACITY)
        {
#if LOGGING
            stats_queueOverflows++;
#endif
            return;
        }

        // PERF: O(1) hash-set dedup (Beta uses HashSet&lt;NextTickListEntry&gt;.contains()).
        // Previous version did an O(N) linear scan; with TICK_QUEUE_CAPACITY=8192 and a
        // fluid flood scheduling thousands of ticks per frame, that was O(N^2).
        long key = _TickHashKey(gx, gy, gz, blockId);
        int slot = _TickHashFind(key);
        if (slot >= 0 && _tickHashKeys[slot] == key) return; // already scheduled

        int scheduledFrame = _currentFrame + delayTicks;
        int idx = _tickCount;
        _tickX[idx] = gx;
        _tickY[idx] = gy;
        _tickZ[idx] = gz;
        _tickBlockId[idx] = blockId;
        _tickScheduledFrame[idx] = scheduledFrame;
        _tickSeq[idx] = _tickSeqCounter;
        _tickSeqCounter = _tickSeqCounter + 1;
        _tickCount++;

        // Sift up to maintain min-heap on (scheduledFrame, seq) — ties pop in insertion order.
        while (idx > 0)
        {
            int parent = (idx - 1) >> 1;
            if (!_TickBefore(idx, parent)) break;
            _HeapSwap(idx, parent);
            idx = parent;
        }

        // Record this entry in the hash table for O(1) future dedup.
        // We don't track heap index moves (they happen during sift) — the hash is
        // purely "has this (x,y,z,blockId) been inserted?". It is cleared each frame
        // via _TickHashClear() so stale entries from yesterday's queue can't leak.
        if (slot >= 0) _tickHashKeys[slot] = key;
    }

    private long _TickHashKey(int gx, int gy, int gz, byte blockId)
    {
        // Pack into a non-zero long. We use 0 to mean "empty slot", so we set the high bit
        // to guarantee non-zero output. xz are in 24-bit windows, y in 8 bits, id in 8 bits.
        long k = ((long)(gx & 0xFFFFFF))
               | (((long)(gz & 0xFFFFFF)) << 24)
               | (((long)(gy & 0xFF)) << 48)
               | (((long)blockId) << 56);
        // long.MinValue == 0x8000000000000000 bit pattern; OR'd in to guarantee key != 0
        // (we use 0 as the "empty slot" sentinel in the hash table).
        return k | long.MinValue;
    }

    private int _TickHashFind(long key)
    {
        // Linear probe WITH tombstone support. Returns the slot holding `key` if present; otherwise
        // the first reusable slot (tombstone preferred, else the terminating empty) for insertion.
        // Caller checks _tickHashKeys[slot] == key to tell a hit from an insert slot. A tombstone
        // never terminates the search (the key may live further down the chain) but is remembered as
        // a reuse target — this is what keeps deletion from breaking colliding keys' probe chains.
        // 32-bit fold-into-int mix; uses high bits which have better LCG entropy.
        // UDON-CHECKED-CAST: mask to positive int range before casting, since Udon's
        // SystemConvert.ToInt32(long) throws when value exceeds int.MaxValue.
        // Loses one bit of hash entropy — acceptable, the mod-mask at the end uses
        // ~14 bits anyway (TICK_HASH_MASK = 16383).
        long folded = (key ^ (key >> 32)) & 0x7FFFFFFFL;
        int h = (int)folded;
        h ^= h >> 16;
        int slot = h & TICK_HASH_MASK;
        int firstFree = -1;
        for (int i = 0; i < TICK_HASH_CAPACITY; i++)
        {
            long existing = _tickHashKeys[slot];
            if (existing == 0L) return firstFree >= 0 ? firstFree : slot; // empty terminates chain
            if (existing == key) return slot;                             // hit (past any tombstones)
            if (existing == TICK_HASH_TOMBSTONE && firstFree < 0) firstFree = slot; // reuse target
            slot = (slot + 1) & TICK_HASH_MASK;
        }
        return firstFree; // saturated with keys+tombstones: reuse a tombstone if seen, else -1
    }

    private void _TickHashClear()
    {
        System.Array.Clear(_tickHashKeys, 0, TICK_HASH_CAPACITY);
    }

    // True if heap entry a sorts before b: earlier scheduledFrame, or same frame + earlier
    // insertion seq. This is MC's NextTickListEntry.compareTo (scheduledTime, then field_235_e).
    private bool _TickBefore(int a, int b)
    {
        if (_tickScheduledFrame[a] != _tickScheduledFrame[b])
            return _tickScheduledFrame[a] < _tickScheduledFrame[b];
        return _tickSeq[a] < _tickSeq[b];
    }

    private void _HeapSwap(int a, int b)
    {
        int t;
        t = _tickX[a]; _tickX[a] = _tickX[b]; _tickX[b] = t;
        t = _tickY[a]; _tickY[a] = _tickY[b]; _tickY[b] = t;
        t = _tickZ[a]; _tickZ[a] = _tickZ[b]; _tickZ[b] = t;
        t = _tickScheduledFrame[a]; _tickScheduledFrame[a] = _tickScheduledFrame[b]; _tickScheduledFrame[b] = t;
        long ls = _tickSeq[a]; _tickSeq[a] = _tickSeq[b]; _tickSeq[b] = ls;
        byte bt = _tickBlockId[a]; _tickBlockId[a] = _tickBlockId[b]; _tickBlockId[b] = bt;
    }

    private void _HeapRemoveMin()
    {
        _tickCount--;
        if (_tickCount > 0)
        {
            _tickX[0] = _tickX[_tickCount];
            _tickY[0] = _tickY[_tickCount];
            _tickZ[0] = _tickZ[_tickCount];
            _tickBlockId[0] = _tickBlockId[_tickCount];
            _tickScheduledFrame[0] = _tickScheduledFrame[_tickCount];
            _tickSeq[0] = _tickSeq[_tickCount];

            // Sift down on (scheduledFrame, seq)
            int idx = 0;
            while (true)
            {
                int left = (idx << 1) + 1;
                int right = left + 1;
                int smallest = idx;
                if (left < _tickCount && _TickBefore(left, smallest))
                    smallest = left;
                if (right < _tickCount && _TickBefore(right, smallest))
                    smallest = right;
                if (smallest == idx) break;
                _HeapSwap(idx, smallest);
                idx = smallest;
            }
        }
    }

    /// <summary>
    /// Called by McWorld every frame. Processes scheduled and random ticks within budget.
    /// </summary>
    public void Tick()
    {
        if (!_initialized || !enableBlockTicks) return;

        // Run the world simulation (scheduled + random ticks) at MC's fixed 20 TPS regardless of
        // render framerate. Accumulate real time and step 0..MAX_CATCHUP_TICKS whole ticks; a
        // small cap prevents a frame hitch from triggering a tick avalanche.
        _tickAccumulator += Time.deltaTime;
        int steps = 0;
        while (_tickAccumulator >= MC_TICK_SECONDS && steps < MAX_CATCHUP_TICKS)
        {
            _tickAccumulator -= MC_TICK_SECONDS;
            steps++;
            _WorldTickOnce();
        }
        if (steps >= MAX_CATCHUP_TICKS) _tickAccumulator = 0f; // shed backlog after a hitch

        // Falling sand/gravel is dt-scaled entity physics (see _UpdateFallingBlocks); keep it
        // per-frame for smooth motion, matching MC's per-frame-interpolated entity rendering.
        float fallStart = Time.realtimeSinceStartup;
        _UpdateFallingBlocks();
#if LOGGING
        stats_fallingBlockTimeMs += (Time.realtimeSinceStartup - fallStart) * 1000f;
        int activeFalling = 0;
        if (fallingBlockPool != null)
        {
            for (int i = 0; i < FALL_POOL_MAX && i < fallingBlockPool.Length; i++)
                if (_fallActive[i]) activeFalling++;
        }
        if (activeFalling > stats_activeFallingPeak) stats_activeFallingPeak = activeFalling;
#endif
    }

    // One MC world tick (20 TPS): advances the scheduling clock and runs all due scheduled +
    // random block ticks. Invoked 0..N times per render frame by the fixed-timestep gate above.
    private void _WorldTickOnce()
    {
        _currentFrame++;
        _BlockCacheClearAll();

        float frameStart = Time.realtimeSinceStartup;
#if LOGGING
        stats_tickFrames++;
        if (_tickCount > stats_queuePeakCount) stats_queuePeakCount = _tickCount;
#endif
        world.BeginDeferredGpuSync();
        world.BeginDeferredMeshUpdates();
        _ProcessScheduledTicks(frameStart);
#if LOGGING
        float afterScheduled = Time.realtimeSinceStartup;
        stats_scheduledTickTimeMs += (afterScheduled - frameStart) * 1000f;
#endif
        // Don't flush GPU sync — version tracking lets the budgeted
        // _GpuMaintainResidentChunks loop handle re-uploads across
        // multiple frames instead of stalling here.
        world.EndDeferredGpuSync();
        world.FlushDeferredMeshUpdates();
#if LOGGING
        float afterFlush = Time.realtimeSinceStartup;
        stats_meshFlushTimeMs += (afterFlush - afterScheduled) * 1000f;
#endif
        _ProcessRandomTicks(frameStart);
#if LOGGING
        stats_randomTickTimeMs += (Time.realtimeSinceStartup - afterFlush) * 1000f;
#endif
    }

    public void InvalidateBlockCache(int gx, int gy, int gz)
    {
        _BlockCacheInvalidate(gx, gy, gz);
    }

    // --- Scheduled Tick Processing ---

    private void _ProcessScheduledTicks(float frameStart)
    {
        float budget = scheduledTickTimeBudgetMs * 0.001f;
        int processed = 0;

        while (_tickCount > 0 && processed < scheduledTickBudget)
        {
            if ((Time.realtimeSinceStartup - frameStart) > budget) break;

            // Heap root is the earliest scheduled tick. Stop if it's not due yet.
            if (_tickScheduledFrame[0] > _currentFrame) break;

            int tx = _tickX[0];
            int ty = _tickY[0];
            int tz = _tickZ[0];
            byte tickBid = _tickBlockId[0];

            _HeapRemoveMin();

            // Free the hash slot so the dispatcher can re-schedule this coord+blockId again.
            // Write a real TOMBSTONE (not 0): linear probing requires deleted slots to keep the
            // chain walkable. Writing 0 (empty) would short-circuit _TickHashFind for any colliding
            // key hashed further down the chain, so its next ScheduleBlockUpdate would be falsely
            // deduped and dropped — a fluid cell that never re-ticks -> water that doesn't recede.
            long poppedKey = _TickHashKey(tx, ty, tz, tickBid);
            int poppedSlot = _TickHashFind(poppedKey);
            if (poppedSlot >= 0 && _tickHashKeys[poppedSlot] == poppedKey)
            {
                _tickHashKeys[poppedSlot] = TICK_HASH_TOMBSTONE;
            }

            byte currentBlock = world.GetBlock(tx, ty, tz);
            if (currentBlock == tickBid && currentBlock != B_AIR)
            {
                _DispatchUpdateTick(tx, ty, tz, currentBlock);
            }
            processed++;
        }

        // If the heap drained, periodically zero the hash table to keep its load factor low.
        // (Linear-probe tombstones can cluster over very long sessions.)
        if (_tickCount == 0)
        {
            _TickHashClear();
        }
#if LOGGING
        stats_scheduledTicksProcessed += processed;
#endif
    }

    // --- Random Tick Processing ---

    private void _ProcessRandomTicks(float frameStart)
    {
        // Random ticks get their OWN budget window measured from HERE, not from the world-tick
        // start: scheduled ticks run first and could consume the shared window, which previously
        // starved random ticks (crops/grass/fire/lava-recede-fallback) to ~zero whenever fluids
        // were busy. That starvation, on top of the 4-chunk throttle, is why isolated lava never
        // got its recovery tick. Measure elapsed from rtStart so random ticks always get their slice.
        float rtStart = Time.realtimeSinceStartup;
        float budget = randomTickTimeBudgetMs * 0.001f;
        ChunkData[] chunks = world.chunks_1D;
        if (chunks == null) return;
        int totalChunks = chunks.Length;
        if (totalChunks == 0) return;

        int chunksProcessed = 0;
#if LOGGING
        int randomDispatches = 0;
#endif
        // Cap at one full pass over resident chunks per world-tick (randomTickChunksPerFrame is now
        // large to cover all chunks; without this cap it would re-visit chunks when count < budget).
        int maxChunksThisTick = randomTickChunksPerFrame < totalChunks ? randomTickChunksPerFrame : totalChunks;
        while (chunksProcessed < maxChunksThisTick)
        {
            if ((Time.realtimeSinceStartup - rtStart) > budget) break;

            if (_randomTickChunkCursor >= totalChunks)
                _randomTickChunkCursor = 0;

            ChunkData chunk = chunks[_randomTickChunkCursor];
            _randomTickChunkCursor++;
            chunksProcessed++;

            if (chunk == null || !chunk.isDataReady) continue;

            int baseX = chunk.chunkX_world * 16;
            int baseY = chunk.chunkY_world * 16;
            int baseZ = chunk.chunkZ_world * 16;

            for (int i = 0; i < randomTicksPerChunk; i++)
            {
                _randomTickSeed = _randomTickSeed * 3 + 1013904223;
                int rv = _randomTickSeed >> 2;
                int lx = rv & 15;
                int lz = (rv >> 8) & 15;
                // PARITY: Java uses `& 127` to cover the full 0..127 Y range (World.java:1951).
                // This project uses 16-tall chunks, so we mask within the chunk and the chunk
                // cursor iterates all Y-chunks across the column to cover the same Y range.
                int ly = (rv >> 16) & 15;

                byte blockId = world.GetBlock(baseX + lx, baseY + ly, baseZ + lz);
                if (blockId != B_AIR && _IsRandomTickBlock(blockId))
                {
#if LOGGING
                    randomDispatches++;
#endif
                    _DispatchUpdateTick(baseX + lx, baseY + ly, baseZ + lz, blockId);
                }
            }
        }
#if LOGGING
        stats_randomTicksProcessed += randomDispatches;
        stats_randomTickChunksScanned += chunksProcessed;
#endif
    }

    /// <summary>
    /// Returns true if this block type receives random ticks (MC's tickOnLoad).
    /// PERF: Single byte-indexed array load — avoids a 15-case switch in the random tick hot loop.
    /// </summary>
    private bool _IsRandomTickBlock(byte blockId)
    {
        return _isRandomTickBlockLut[blockId];
    }

    // --- Tick Dispatch ---

    private void _DispatchUpdateTick(int gx, int gy, int gz, byte blockId)
    {
        switch (blockId)
        {
            case B_SAND:
            case B_GRAVEL:
#if LOGGING
                stats_dispatchSand++;
#endif
                _UpdateTick_Sand(gx, gy, gz, blockId);
                break;
            case B_WATER_FLOWING:
#if LOGGING
                stats_dispatchWaterFlowing++;
#endif
                _UpdateTick_FlowingFluid(gx, gy, gz, true);
                break;
            case B_LAVA_FLOWING:
#if LOGGING
                stats_dispatchLavaFlowing++;
#endif
                _UpdateTick_FlowingFluid(gx, gy, gz, false);
                break;
            case B_LAVA_STILL:
#if LOGGING
                stats_dispatchLavaStill++;
#endif
                _UpdateTick_StationaryLava(gx, gy, gz);
                break;
            case B_FIRE:
#if LOGGING
                stats_dispatchFire++;
#endif
                _UpdateTick_Fire(gx, gy, gz);
                break;
            case B_GRASS:
#if LOGGING
                stats_dispatchGrass++;
#endif
                _UpdateTick_Grass(gx, gy, gz);
                break;
            case B_LEAVES:
#if LOGGING
                stats_dispatchLeaves++;
#endif
                _UpdateTick_Leaves(gx, gy, gz);
                break;
            case B_CACTUS:
#if LOGGING
                stats_dispatchCactus++;
#endif
                _UpdateTick_Cactus(gx, gy, gz);
                break;
            case B_REED:
#if LOGGING
                stats_dispatchReed++;
#endif
                _UpdateTick_Reed(gx, gy, gz);
                break;
            case B_SAPLING:
#if LOGGING
                stats_dispatchSapling++;
#endif
                _UpdateTick_Sapling(gx, gy, gz);
                break;
            case B_FARMLAND:
#if LOGGING
                stats_dispatchFarmland++;
#endif
                _UpdateTick_Farmland(gx, gy, gz);
                break;
            case B_ICE:
#if LOGGING
                stats_dispatchIce++;
#endif
                _UpdateTick_Ice(gx, gy, gz);
                break;
            case B_SNOW_LAYER:
#if LOGGING
                stats_dispatchSnow++;
#endif
                _UpdateTick_Snow(gx, gy, gz);
                break;
            case B_CROPS:
#if LOGGING
                stats_dispatchCrops++;
#endif
                _UpdateTick_Crops(gx, gy, gz);
                break;
            case B_MUSHROOM_BROWN:
            case B_MUSHROOM_RED:
#if LOGGING
                stats_dispatchOther++;
#endif
                _UpdateTick_Mushroom(gx, gy, gz, blockId);
                break;
            default:
#if LOGGING
                stats_dispatchOther++;
#endif
                break;
        }
    }

    /// <summary>
    /// Called by McWorld when a block changes. Dispatches onNeighborBlockChange
    /// to the 6 neighbors. Equivalent to MC's notifyBlocksOfNeighborChange.
    /// </summary>
    public void NotifyNeighborsOfBlockChange(int gx, int gy, int gz, byte changedBlockId)
    {
        if (!_initialized) return;
        _OnNeighborChange(gx - 1, gy, gz, changedBlockId);
        _OnNeighborChange(gx + 1, gy, gz, changedBlockId);
        _OnNeighborChange(gx, gy - 1, gz, changedBlockId);
        _OnNeighborChange(gx, gy + 1, gz, changedBlockId);
        _OnNeighborChange(gx, gy, gz - 1, changedBlockId);
        _OnNeighborChange(gx, gy, gz + 1, changedBlockId);
    }

    /// <summary>
    /// Called when a block is placed/changed. Schedules the block's own initial tick.
    /// Mirrors MC's Block.onBlockAdded (e.g. BlockSand, BlockFlowing, BlockFire).
    /// </summary>
    public void OnBlockAdded(int gx, int gy, int gz, byte blockId)
    {
        if (!_initialized) return;
        switch (blockId)
        {
            case B_SAND:
            case B_GRAVEL:
                ScheduleBlockUpdate(gx, gy, gz, blockId, 3);
                break;
            case B_WATER_FLOWING:
                ScheduleBlockUpdate(gx, gy, gz, blockId, 5);
                break;
            case B_LAVA_FLOWING:
                // MC BlockFlowing.onBlockAdded: BlockFluid.onBlockAdded (checkForHarden) runs
                // FIRST — lava that just flowed into a cell touching water hardens IMMEDIATELY
                // (source->obsidian, meta<=4->cobblestone; this is the cobblestone generator) —
                // and the tick is scheduled only if the cell is still flowing lava afterwards.
                _CheckForHarden(gx, gy, gz);
                if (world.GetBlock(gx, gy, gz) == B_LAVA_FLOWING)
                    ScheduleBlockUpdate(gx, gy, gz, blockId, 30);
                break;
            case B_LAVA_STILL:
                // MC BlockStationary inherits BlockFluid.onBlockAdded: a lava SOURCE placed
                // beside/under water hardens to obsidian on placement. Still lava schedules
                // no tick of its own.
                _CheckForHarden(gx, gy, gz);
                break;
            case B_FIRE:
                ScheduleBlockUpdate(gx, gy, gz, blockId, 40);
                break;
        }
    }

    private void _OnNeighborChange(int gx, int gy, int gz, byte changedBlockId)
    {
        byte blockId = _FlowCacheGetBlock(gx, gy, gz);
        if (blockId == B_AIR) return;

        switch (blockId)
        {
            case B_SAND:
            case B_GRAVEL:
                ScheduleBlockUpdate(gx, gy, gz, blockId, 3);
                break;
            case B_WATER_STILL:
                _OnNeighborChange_StationaryFluid(gx, gy, gz, true);
                break;
            case B_LAVA_STILL:
                // MC BlockStationary.onNeighborBlockChange calls super (checkForHarden) FIRST:
                // water arriving beside/on top of a lava SOURCE makes OBSIDIAN. Only if the
                // cell survived as lava does it convert to flowing and schedule (func_30004_j
                // is gated on getBlockId == blockID in the Java).
                _CheckForHarden(gx, gy, gz);
                if (world.GetBlock(gx, gy, gz) == B_LAVA_STILL)
                    _OnNeighborChange_StationaryFluid(gx, gy, gz, false);
                break;
            // Strict b1.7.3 parity: BlockFlowing does NOT override onNeighborBlockChange — the
            // inherited BlockFluid.onNeighborBlockChange only runs checkForHarden, with NO
            // reschedule. VRCM used to reschedule flowing fluid here, which made recession ~13x
            // faster than vanilla (not 1:1). Removed: recession now relies on the flowing cell's
            // OWN reschedule while it is actively changing, the still-fluid notify cascade (still
            // cases above, which MC does have), and random ticks (now at MC density) — exactly like
            // vanilla. checkForHarden kept for lava (no-op for water).
            case B_WATER_FLOWING:
                break;
            case B_LAVA_FLOWING:
                _CheckForHarden(gx, gy, gz);
                break;
            case B_FIRE:
                _OnNeighborChange_Fire(gx, gy, gz);
                break;
            case B_CACTUS:
                _OnNeighborChange_Cactus(gx, gy, gz);
                break;
            case B_FLOWER_DANDELION:
            case B_FLOWER_ROSE:
            case B_MUSHROOM_BROWN:
            case B_MUSHROOM_RED:
            case B_TALLGRASS:
            case B_DEADBUSH:
                _OnNeighborChange_Flower(gx, gy, gz);
                break;
            case B_REED:
                _OnNeighborChange_Reed(gx, gy, gz);
                break;
            case B_TORCH:
            case B_REDSTONE_TORCH_OFF:
            case B_REDSTONE_TORCH_ON:
                world.DropTorchIfUnsupported(gx, gy, gz);
                break;
        }
    }

    // --- LCG Random Helper ---
    private int _NextRandom()
    {
        _randomTickSeed = _randomTickSeed * 1103515245 + 12345;
        return (_randomTickSeed >> 16) & 0x7FFF;
    }

    // =====================================================================
    // BLOCK BEHAVIORS — ported 1:1 from Beta 1.7.3 deobfuscated source
    // =====================================================================

    // --- SAND / GRAVEL (BlockSand.java) ---

    private void _UpdateTick_Sand(int gx, int gy, int gz, byte blockId)
    {
        if (!_CanFallBelow(gx, gy - 1, gz) || gy < 1) return;

        if (fallingBlockPool != null && fallingBlockPool.Length > 0)
        {
            world.SetBlock(gx, gy, gz, B_AIR);
            _SpawnFallingBlock(gx, gy, gz, blockId);
        }
        else
        {
            // No pool available — fall instantly (MC fallInstantly path)
            world.SetBlock(gx, gy, gz, B_AIR);
            int targetY = gy - 1;
            while (targetY > 0 && _CanFallBelow(gx, targetY - 1, gz))
                targetY--;
            if (targetY > 0)
                world.SetBlock(gx, targetY, gz, blockId);
        }
    }

    private bool _CanFallBelow(int gx, int gy, int gz)
    {
        byte below = world.GetBlock(gx, gy, gz);
        if (below == B_AIR) return true;
        if (below == B_FIRE) return true;
        if (below == B_WATER_FLOWING || below == B_WATER_STILL) return true;
        if (below == B_LAVA_FLOWING || below == B_LAVA_STILL) return true;
        return false;
    }

    // --- FLOWING FLUID (BlockFlowing.java) ---

    private void _UpdateTick_FlowingFluid(int gx, int gy, int gz, bool isWater)
    {
        byte selfId = isWater ? B_WATER_FLOWING : B_LAVA_FLOWING;
        byte stillId = isWater ? B_WATER_STILL : B_LAVA_STILL;
        int flowDecay = _GetFlowDecay(gx, gy, gz, isWater);
        int decayRate = 1;
        if (!isWater) decayRate = 2; // lava flows slower in overworld

        bool settled = true;

        if (flowDecay > 0)
        {
            int smallest = -100;
            _numAdjacentSources = 0;

            int s1 = _GetSmallestFlowDecay(gx - 1, gy, gz, isWater, smallest);
            int s2 = _GetSmallestFlowDecay(gx + 1, gy, gz, isWater, s1);
            int s3 = _GetSmallestFlowDecay(gx, gy, gz - 1, isWater, s2);
            int s4 = _GetSmallestFlowDecay(gx, gy, gz + 1, isWater, s3);
            int newDecay = s4 + decayRate;

            if (newDecay >= 8 || s4 < 0) newDecay = -1;

            int aboveDecay = _GetFlowDecay(gx, gy + 1, gz, isWater);
            if (aboveDecay >= 0)
            {
                newDecay = aboveDecay >= 8 ? aboveDecay : aboveDecay + 8;
            }

            // Water source duplication: 2+ adjacent sources on solid ground
            if (isWater && _numAdjacentSources >= 2)
            {
                byte belowBlock = _FlowCacheGetBlock(gx, gy - 1, gz);
                bool belowSolid = belowBlock != B_AIR && blockTypeManager.GetBlockIsSolid(belowBlock);
                if (belowSolid)
                    newDecay = 0;
                else if (_IsFluidBlock(gx, gy - 1, gz, isWater) && _FlowCacheGetMeta(gx, gy, gz) == 0)
                    newDecay = 0;
            }

            // Lava sticky randomness (BlockFlowing.java:59): 75% of the time lava refuses to
            // weaken, stays FLOWING, and does NOT reschedule itself. In MC the stalled cell is
            // eventually re-tried by a RANDOM tick (Beta: 80 picks per 16x16x128 chunk per tick
            // = one per block every ~410 ticks / ~20s). Udon cannot afford that density — the
            // budgeted random-tick scan covers ~1-2% of chunks per tick, so a stalled cell would
            // wait ~an hour for its retry and lava effectively never receded once its source was
            // removed (water has no sticky, which is why water always dried fine).
            // PARITY SHIM: schedule the retry explicitly with the SAME expected interval and
            // randomized spread MC's random ticks would give (mean ~410 ticks). Same 25% accept
            // odds per attempt, same expected recession rate, no random-tick dependence.
            if (!isWater && flowDecay < 8 && newDecay < 8 && newDecay > flowDecay && (_NextRandom() % 4) != 0)
            {
                newDecay = flowDecay;
                settled = false;
                ScheduleBlockUpdate(gx, gy, gz, selfId, LAVA_STICKY_RETRY_MIN_TICKS + (_NextRandom() % LAVA_STICKY_RETRY_SPREAD_TICKS));
            }

            if (newDecay != flowDecay)
            {
                flowDecay = newDecay;
                if (newDecay < 0)
                {
                    world.SetBlock(gx, gy, gz, B_AIR);
                }
                else
                {
                    world.SetBlockMetadata(gx, gy, gz, (byte)newDecay);
                    ScheduleBlockUpdate(gx, gy, gz, selfId, isWater ? 5 : 30);
                    world.NotifyNeighborsOfBlockChange(gx, gy, gz, selfId);
                }
            }
            else if (settled)
            {
                // Convert to stationary
                _ConvertToStationary(gx, gy, gz, isWater);
            }
        }
        else
        {
            // Source block (flowDecay == 0), convert to stationary
            _ConvertToStationary(gx, gy, gz, isWater);
        }

        // Flow downward — only into a LOADED below-cell. A not-ready chunk reads as AIR and would be
        // a phantom drop into the void / unstreamed terrain (this crosses a chunk-Y border every 16
        // blocks, and gy-1<0 is also not-ready, which conveniently blocks flow below the world).
        if (world.IsCellDataReady(gx, gy - 1, gz) && _LiquidCanDisplace(gx, gy - 1, gz, isWater))
        {
            int downMeta = flowDecay >= 8 ? flowDecay : flowDecay + 8;
            world.SetBlockAndMetadata(gx, gy - 1, gz, selfId, (byte)downMeta);
            ScheduleBlockUpdate(gx, gy - 1, gz, selfId, isWater ? 5 : 30);
        }
        else if (flowDecay >= 0 && (flowDecay == 0 || _BlockBlocksFlow(gx, gy - 1, gz)))
        {
            // Flow horizontally
            int hDecay = flowDecay + decayRate;
            if (flowDecay >= 8) hDecay = 1;
            if (hDecay >= 8) return;

            bool[] optimal = _GetOptimalFlowDirections(gx, gy, gz, isWater);
            if (debugFluidFlow)
            {
                // _flowCostResult holds the per-direction flood-fill cost just computed above.
                Debug.Log("[FluidFlow] " + (isWater ? "W" : "L") + " (" + gx + "," + gy + "," + gz + ") decay=" + flowDecay + " hDecay=" + hDecay
                    + "  -X n=" + world.GetBlock(gx - 1, gy, gz) + " below=" + world.GetBlock(gx - 1, gy - 1, gz) + " cost=" + _flowCostResult[0] + " opt=" + optimal[0]
                    + " | +X n=" + world.GetBlock(gx + 1, gy, gz) + " below=" + world.GetBlock(gx + 1, gy - 1, gz) + " cost=" + _flowCostResult[1] + " opt=" + optimal[1]
                    + " | -Z n=" + world.GetBlock(gx, gy, gz - 1) + " below=" + world.GetBlock(gx, gy - 1, gz - 1) + " cost=" + _flowCostResult[2] + " opt=" + optimal[2]
                    + " | +Z n=" + world.GetBlock(gx, gy, gz + 1) + " below=" + world.GetBlock(gx, gy - 1, gz + 1) + " cost=" + _flowCostResult[3] + " opt=" + optimal[3]);

                // ANOMALY PROBE: an all-1000 result means the flood fill found no drop within
                // range. When that happens, dump the 7x7 neighborhood at gy and gy-1 twice —
                // once through the ticker's frame cache (what the scan actually read) and once
                // through direct world reads (ground truth) — plus per-cell readiness. Any
                // disagreement pinpoints WHICH layer lied to the flood fill and where.
                if (_flowCostResult[0] == 1000 && _flowCostResult[1] == 1000
                    && _flowCostResult[2] == 1000 && _flowCostResult[3] == 1000)
                {
                    string cacheRow = "";
                    string worldRow = "";
                    string cacheBelow = "";
                    string worldBelow = "";
                    string readyRow = "";
                    for (int dz = -3; dz <= 3; dz++)
                    {
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            int px = gx + dx;
                            int pz = gz + dz;
                            byte cb = _FlowCacheGetBlock(px, gy, pz);
                            byte cm = _FlowCacheGetMeta(px, gy, pz);
                            byte cbb = _FlowCacheGetBlock(px, gy - 1, pz);
                            byte cbm = _FlowCacheGetMeta(px, gy - 1, pz);
                            byte wb = world.GetBlockAndMeta(px, gy, pz);
                            byte wm = world.lastMetaResult;
                            byte wbb = world.GetBlockAndMeta(px, gy - 1, pz);
                            byte wbm = world.lastMetaResult;
                            cacheRow += cb + ":" + cm + " ";
                            worldRow += wb + ":" + wm + " ";
                            cacheBelow += cbb + ":" + cbm + " ";
                            worldBelow += wbb + ":" + wbm + " ";
                            readyRow += (world.IsCellDataReady(px, gy, pz) ? "1" : "0");
                            readyRow += (world.IsCellDataReady(px, gy - 1, pz) ? "1" : "0");
                            readyRow += " ";
                        }
                        cacheRow += "| ";
                        worldRow += "| ";
                        cacheBelow += "| ";
                        worldBelow += "| ";
                        readyRow += "| ";
                    }
                    Debug.Log("[FluidFlood1000] (" + gx + "," + gy + "," + gz + ") 7x7 rows z-3..z+3, cols x-3..x+3, cell=id:meta"
                        + "\nCACHE  y=" + gy + " : " + cacheRow
                        + "\nWORLD  y=" + gy + " : " + worldRow
                        + "\nCACHE y-1=" + (gy - 1) + ": " + cacheBelow
                        + "\nWORLD y-1=" + (gy - 1) + ": " + worldBelow
                        + "\nREADY (y,y-1) : " + readyRow);
                }
            }
            if (optimal[0]) _FlowIntoBlock(gx - 1, gy, gz, hDecay, isWater);
            if (optimal[1]) _FlowIntoBlock(gx + 1, gy, gz, hDecay, isWater);
            if (optimal[2]) _FlowIntoBlock(gx, gy, gz - 1, hDecay, isWater);
            if (optimal[3]) _FlowIntoBlock(gx, gy, gz + 1, hDecay, isWater);
        }
    }

    private void _ConvertToStationary(int gx, int gy, int gz, bool isWater)
    {
        byte meta = _FlowCacheGetMeta(gx, gy, gz);
        byte stillId = isWater ? B_WATER_STILL : B_LAVA_STILL;
        // MC func_30003_j: setBlockAndMetadata (NO notify) + markDirty
        world.SetBlockAndMetadataSilent(gx, gy, gz, stillId, meta);
    }

    private int _GetFlowDecay(int gx, int gy, int gz, bool isWater)
    {
        byte b = _FlowCacheGetBlock(gx, gy, gz);
        if (isWater)
        {
            if (b != B_WATER_FLOWING && b != B_WATER_STILL) return -1;
        }
        else
        {
            if (b != B_LAVA_FLOWING && b != B_LAVA_STILL) return -1;
        }
        return _FlowCacheGetMeta(gx, gy, gz);
    }

    private int _GetSmallestFlowDecay(int gx, int gy, int gz, bool isWater, int currentSmallest)
    {
        int decay = _GetFlowDecay(gx, gy, gz, isWater);
        if (decay < 0) return currentSmallest;
        if (decay == 0) _numAdjacentSources++;
        if (decay >= 8) decay = 0;
        return (currentSmallest >= 0 && decay >= currentSmallest) ? currentSmallest : decay;
    }

    private bool _LiquidCanDisplace(int gx, int gy, int gz, bool isWater)
    {
        byte b = _FlowCacheGetBlock(gx, gy, gz);
        if (isWater)
        {
            if (b == B_WATER_FLOWING || b == B_WATER_STILL) return false;
        }
        else
        {
            if (b == B_LAVA_FLOWING || b == B_LAVA_STILL) return false;
        }
        if (b == B_LAVA_FLOWING || b == B_LAVA_STILL) return false;
        if (b == B_DOOR_WOOD || b == B_DOOR_IRON || b == B_SIGN || b == B_LADDER || b == B_REED) return false;
        if (b == B_AIR) return true;
        return !blockTypeManager.GetBlockIsSolid(b);
    }

    private bool _BlockBlocksFlow(int gx, int gy, int gz)
    {
        byte b = _FlowCacheGetBlock(gx, gy, gz);
        if (b == B_DOOR_WOOD || b == B_DOOR_IRON || b == B_SIGN || b == B_LADDER || b == B_REED) return true;
        if (b == B_AIR) return false;
        return blockTypeManager.GetBlockIsSolid(b);
    }

    private bool _IsFluidBlock(int gx, int gy, int gz, bool isWater)
    {
        byte b = _FlowCacheGetBlock(gx, gy, gz);
        if (isWater) return b == B_WATER_FLOWING || b == B_WATER_STILL;
        return b == B_LAVA_FLOWING || b == B_LAVA_STILL;
    }

    private void _FlowIntoBlock(int gx, int gy, int gz, int decay, bool isWater)
    {
        if (!_LiquidCanDisplace(gx, gy, gz, isWater)) return;
        byte selfId = isWater ? B_WATER_FLOWING : B_LAVA_FLOWING;
        world.SetBlockAndMetadata(gx, gy, gz, selfId, (byte)decay);
        ScheduleBlockUpdate(gx, gy, gz, selfId, isWater ? 5 : 30);
    }

    // Reusable arrays for flow direction calculation (UdonSharp can't do stack arrays)
    private bool[] _flowDirResult;
    private int[] _flowCostResult;
    private int[] _flowOffX;
    private int[] _flowOffZ;
    private int[] _flowOpposites;

    // Frame-wide block cache using modular position hashing.
    // Survives across ticks within a frame so adjacent water ticks and
    // notification reads share cached data. 16×4×16 = 1024 entries.
    private const int BC_BITS_XZ = 4;
    private const int BC_SIZE_XZ = 1 << BC_BITS_XZ; // 16
    private const int BC_MASK_XZ = BC_SIZE_XZ - 1;  // 0xF
    private const int BC_BITS_Y = 2;
    private const int BC_SIZE_Y = 1 << BC_BITS_Y;   // 4
    private const int BC_MASK_Y = BC_SIZE_Y - 1;     // 0x3
    private const int BC_TOTAL = BC_SIZE_XZ * BC_SIZE_XZ * BC_SIZE_Y; // 1024

    private bool[] _bcValid;
    private int[] _bcPosX;
    private int[] _bcPosY;
    private int[] _bcPosZ;
    private byte[] _bcBlock;
    private byte[] _bcMeta;

    private int _BCIdx(int gx, int gy, int gz)
    {
        return (gx & BC_MASK_XZ) + ((gz & BC_MASK_XZ) << BC_BITS_XZ) + ((gy & BC_MASK_Y) << (BC_BITS_XZ + BC_BITS_XZ));
    }

    private byte _FlowCacheGetBlock(int gx, int gy, int gz)
    {
        int idx = _BCIdx(gx, gy, gz);
        if (_bcValid[idx] && _bcPosX[idx] == gx && _bcPosY[idx] == gy && _bcPosZ[idx] == gz)
            return _bcBlock[idx];
        byte b = world.GetBlockAndMeta(gx, gy, gz);
        _bcValid[idx] = true;
        _bcPosX[idx] = gx;
        _bcPosY[idx] = gy;
        _bcPosZ[idx] = gz;
        _bcBlock[idx] = b;
        _bcMeta[idx] = world.lastMetaResult;
        return b;
    }

    private byte _FlowCacheGetMeta(int gx, int gy, int gz)
    {
        int idx = _BCIdx(gx, gy, gz);
        if (_bcValid[idx] && _bcPosX[idx] == gx && _bcPosY[idx] == gy && _bcPosZ[idx] == gz)
            return _bcMeta[idx];
        byte b = world.GetBlockAndMeta(gx, gy, gz);
        _bcValid[idx] = true;
        _bcPosX[idx] = gx;
        _bcPosY[idx] = gy;
        _bcPosZ[idx] = gz;
        _bcBlock[idx] = b;
        _bcMeta[idx] = world.lastMetaResult;
        return world.lastMetaResult;
    }

    private void _BlockCacheClearAll()
    {
        System.Array.Clear(_bcValid, 0, BC_TOTAL);
    }

    private void _BlockCacheInvalidate(int gx, int gy, int gz)
    {
        int idx = _BCIdx(gx, gy, gz);
        if (_bcValid[idx] && _bcPosX[idx] == gx && _bcPosY[idx] == gy && _bcPosZ[idx] == gz)
            _bcValid[idx] = false;
    }

    private bool[] _GetOptimalFlowDirections(int gx, int gy, int gz, bool isWater)
    {

        for (int d = 0; d < 4; d++)
        {
            _flowCostResult[d] = 1000;
            int nx = gx + _flowOffX[d];
            int nz = gz + _flowOffZ[d];

            // A not-yet-loaded neighbour chunk reads as AIR (GetBlock returns 0), which fabricates a
            // phantom open path AND (via its below-cell) a phantom cost-0 drop, pulling fluid toward
            // the streaming frontier instead of the real ledge. MC loads chunks synchronously during
            // fluid ticks so it never sees this; treat not-ready as flow-BLOCKING (leave cost 1000).
            if (!world.IsCellDataReady(nx, gy, nz)) continue;

            // Inline _BlockBlocksFlow + _IsFluidBlock: single GetBlock instead of 2-3
            byte nb = _FlowCacheGetBlock(nx, gy, nz);
            if (nb == B_DOOR_WOOD || nb == B_DOOR_IRON || nb == B_SIGN || nb == B_LADDER || nb == B_REED) continue;
            if (nb != B_AIR && blockTypeManager.GetBlockIsSolid(nb)) continue;
            bool isFluid = isWater ? (nb == B_WATER_FLOWING || nb == B_WATER_STILL) : (nb == B_LAVA_FLOWING || nb == B_LAVA_STILL);
            if (isFluid && _FlowCacheGetMeta(nx, gy, nz) == 0) continue;

            // Inline _BlockBlocksFlow for below
            byte belowNb = _FlowCacheGetBlock(nx, gy - 1, nz);
            if (belowNb == B_DOOR_WOOD || belowNb == B_DOOR_IRON || belowNb == B_SIGN || belowNb == B_LADDER || belowNb == B_REED)
            { /* blocks flow */ }
            else if ((belowNb == B_AIR || !blockTypeManager.GetBlockIsSolid(belowNb)) && world.IsCellDataReady(nx, gy - 1, nz))
            {
                // Real drop only if the below-cell's chunk is loaded; a not-ready below reads AIR and
                // would be a phantom drop. If not ready, fall through to recurse (no phantom cost-0).
                _flowCostResult[d] = 0;
                continue;
            }
            _flowCostResult[d] = _CalculateFlowCost(nx, gy, nz, 1, d, isWater);
        }

        int minCost = _flowCostResult[0];
        for (int d = 1; d < 4; d++)
            if (_flowCostResult[d] < minCost) minCost = _flowCostResult[d];

        for (int d = 0; d < 4; d++)
            _flowDirResult[d] = _flowCostResult[d] == minCost;

        return _flowDirResult;
    }

    // ROOT CAUSE of the lava/water "wrong path" divergence (proven by the [FluidFlood1000]
    // probe: correct cache==world inputs, impossible cost-1000 outputs): this method calls
    // ITSELF, and UdonSharp does NOT preserve a method's locals across a recursive call
    // unless it is marked [RecursiveMethod]. Without it, the inner call clobbers the outer
    // call's d/nx/nz/best/depth, so after the first recursion returns, the remaining
    // directions of the outer loop are scanned with corrupted state — deterministically
    // missing real drops (cost 1000) and producing single-path / sideways-shelf flow.
    // Direct drops (cost 0, no recursion) were always correct, which is why only the
    // recursive cases diverged from the b1.7.3 reference.
    [RecursiveMethod]
    private int _CalculateFlowCost(int gx, int gy, int gz, int depth, int fromDir, bool isWater)
    {
        int best = 1000;

        for (int d = 0; d < 4; d++)
        {
            if (d == _flowOpposites[fromDir]) continue;

            int nx = gx + _flowOffX[d];
            int nz = gz + _flowOffZ[d];

            // Not-ready neighbour chunk reads as AIR -> phantom path/drop; treat as flow-blocking.
            if (!world.IsCellDataReady(nx, gy, nz)) continue;

            // Inline _BlockBlocksFlow + _IsFluidBlock: single GetBlock instead of 2-3
            byte nb = _FlowCacheGetBlock(nx, gy, nz);
            if (nb == B_DOOR_WOOD || nb == B_DOOR_IRON || nb == B_SIGN || nb == B_LADDER || nb == B_REED) continue;
            if (nb != B_AIR && blockTypeManager.GetBlockIsSolid(nb)) continue;
            bool isFluid = isWater ? (nb == B_WATER_FLOWING || nb == B_WATER_STILL) : (nb == B_LAVA_FLOWING || nb == B_LAVA_STILL);
            if (isFluid && _FlowCacheGetMeta(nx, gy, nz) == 0) continue;

            // Inline _BlockBlocksFlow for below
            byte belowNb = _FlowCacheGetBlock(nx, gy - 1, nz);
            if (belowNb == B_DOOR_WOOD || belowNb == B_DOOR_IRON || belowNb == B_SIGN || belowNb == B_LADDER || belowNb == B_REED)
            { /* blocks flow, fall through to recurse */ }
            else if ((belowNb == B_AIR || !blockTypeManager.GetBlockIsSolid(belowNb)) && world.IsCellDataReady(nx, gy - 1, nz))
                return depth; // real drop only if the below chunk is loaded (else recurse, no phantom)

            if (depth < 4)
            {
                int cost = _CalculateFlowCost(nx, gy, nz, depth + 1, d, isWater);
                // MC calculateFlowCost (BlockFlowing.java:162-164) scans ALL non-back directions and
                // keeps the min with NO early out. A later direction can be a DIRECT drop (returns
                // `depth`, cheaper than this recursion's depth+1); bailing at depth+1 inflated the cost
                // and broke multi-drop ties -> dropped an optimal flow direction ("only one path goes
                // down a block instead of multiple"). Match MC exactly: keep scanning.
                if (cost < best) best = cost;
            }
        }
        return best;
    }

    private void _OnNeighborChange_StationaryFluid(int gx, int gy, int gz, bool isWater)
    {
        byte meta = world.GetBlockMetadata(gx, gy, gz);
        byte flowingId = isWater ? B_WATER_FLOWING : B_LAVA_FLOWING;
        // MC func_30004_j: setBlockAndMetadata (NO notify) + scheduleBlockUpdate
        world.SetBlockAndMetadataSilent(gx, gy, gz, flowingId, meta);
        ScheduleBlockUpdate(gx, gy, gz, flowingId, isWater ? 5 : 30);
    }

    // --- LAVA/WATER HARDENING (BlockFluid.checkForHarden) ---

    private void _CheckForHarden(int gx, int gy, int gz)
    {
        byte b = world.GetBlock(gx, gy, gz);
        bool isLava = (b == B_LAVA_FLOWING || b == B_LAVA_STILL);
        if (!isLava) return;

        bool touchesWater =
            _IsWaterAt(gx, gy, gz - 1) || _IsWaterAt(gx, gy, gz + 1) ||
            _IsWaterAt(gx - 1, gy, gz) || _IsWaterAt(gx + 1, gy, gz) ||
            _IsWaterAt(gx, gy + 1, gz);

        if (!touchesWater) return;

        byte meta = world.GetBlockMetadata(gx, gy, gz);
        if (meta == 0)
            world.SetBlock(gx, gy, gz, B_OBSIDIAN);
        else if (meta <= 4)
            world.SetBlock(gx, gy, gz, B_COBBLESTONE);
    }

    private bool _IsWaterAt(int gx, int gy, int gz)
    {
        byte b = world.GetBlock(gx, gy, gz);
        return b == B_WATER_FLOWING || b == B_WATER_STILL;
    }

    // --- STATIONARY LAVA (BlockStationary.updateTick — fire ignition) ---

    private void _UpdateTick_StationaryLava(int gx, int gy, int gz)
    {
        int attempts = _NextRandom() % 3;
        int cx = gx, cy = gy, cz = gz;
        for (int i = 0; i < attempts; i++)
        {
            cx += (_NextRandom() % 3) - 1;
            cy++;
            cz += (_NextRandom() % 3) - 1;
            byte above = world.GetBlock(cx, cy, cz);
            if (above == B_AIR)
            {
                if (_IsBurnable(cx - 1, cy, cz) || _IsBurnable(cx + 1, cy, cz) ||
                    _IsBurnable(cx, cy, cz - 1) || _IsBurnable(cx, cy, cz + 1) ||
                    _IsBurnable(cx, cy - 1, cz) || _IsBurnable(cx, cy + 1, cz))
                {
                    world.SetBlock(cx, cy, cz, B_FIRE);
                    return;
                }
            }
            else if (above != B_AIR && blockTypeManager.GetBlockIsSolid(above))
            {
                return;
            }
        }
    }

    private bool _IsBurnable(int gx, int gy, int gz)
    {
        byte b = world.GetBlock(gx, gy, gz);
        // MC checks blockMaterial.getBurning() — wood/planks/leaves/wool/etc
        return _chanceToEncourageFire[b] > 0;
    }

    // --- FIRE (BlockFire.java) ---

    private void _UpdateTick_Fire(int gx, int gy, int gz)
    {
        bool onNetherrack = world.GetBlock(gx, gy - 1, gz) == B_NETHERRACK;

        // Check if fire can stay
        byte belowBlock = world.GetBlock(gx, gy - 1, gz);
        bool belowNormal = belowBlock != B_AIR && blockTypeManager.GetBlockIsSolid(belowBlock);
        if (!belowNormal && !_HasAdjacentFlammable(gx, gy, gz))
        {
            world.SetBlock(gx, gy, gz, B_AIR);
            return;
        }

        byte meta = world.GetBlockMetadata(gx, gy, gz);

        // Age the fire
        if (meta < 15)
        {
            int newMeta = meta + (_NextRandom() % 3 == 0 ? 1 : 0);
            if (newMeta > 15) newMeta = 15;
            world.SetBlockMetadata(gx, gy, gz, (byte)newMeta);
        }

        // Schedule next tick
        ScheduleBlockUpdate(gx, gy, gz, B_FIRE, 40);

        if (!onNetherrack && !_HasAdjacentFlammable(gx, gy, gz))
        {
            if (!belowNormal || meta > 3)
            {
                world.SetBlock(gx, gy, gz, B_AIR);
                return;
            }
        }

        if (!onNetherrack && !_CanBlockCatchFire(gx, gy - 1, gz) && meta == 15 && (_NextRandom() % 4) == 0)
        {
            world.SetBlock(gx, gy, gz, B_AIR);
            return;
        }

        // Try to burn adjacent blocks
        _TryBurnBlock(gx + 1, gy, gz, 300, meta);
        _TryBurnBlock(gx - 1, gy, gz, 300, meta);
        _TryBurnBlock(gx, gy - 1, gz, 250, meta);
        _TryBurnBlock(gx, gy + 1, gz, 250, meta);
        _TryBurnBlock(gx, gy, gz - 1, 300, meta);
        _TryBurnBlock(gx, gy, gz + 1, 300, meta);

        // Spread fire to nearby air blocks
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 4; dy++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;
                    int nx = gx + dx, ny = gy + dy, nz = gz + dz;
                    int chance = _GetEncouragementForPos(nx, ny, nz);
                    if (chance <= 0) continue;

                    int divisor = 100;
                    if (dy > 1) divisor += (dy - 1) * 100;

                    int spreadChance = (chance + 40) / (meta + 30);
                    if (spreadChance > 0 && (_NextRandom() % divisor) <= spreadChance)
                    {
                        int newAge = meta + (_NextRandom() % 5) / 4;
                        if (newAge > 15) newAge = 15;
                        world.SetBlockAndMetadata(nx, ny, nz, B_FIRE, (byte)newAge);
                    }
                }
            }
        }
    }

    private void _TryBurnBlock(int gx, int gy, int gz, int chance, int fireMeta)
    {
        byte b = world.GetBlock(gx, gy, gz);
        int flammability = _abilityToCatchFire[b];
        if ((_NextRandom() % chance) >= flammability) return;

        bool isTnt = (b == B_TNT);

        if ((_NextRandom() % (fireMeta + 10)) < 5)
        {
            int newAge = fireMeta + (_NextRandom() % 5) / 4;
            if (newAge > 15) newAge = 15;
            world.SetBlockAndMetadata(gx, gy, gz, B_FIRE, (byte)newAge);
        }
        else
        {
            world.SetBlock(gx, gy, gz, B_AIR);
        }
    }

    private void _OnNeighborChange_Fire(int gx, int gy, int gz)
    {
        byte below = world.GetBlock(gx, gy - 1, gz);
        bool belowNormal = below != B_AIR && blockTypeManager.GetBlockIsSolid(below);
        if (!belowNormal && !_HasAdjacentFlammable(gx, gy, gz))
            world.SetBlock(gx, gy, gz, B_AIR);
    }

    private bool _HasAdjacentFlammable(int gx, int gy, int gz)
    {
        return _CanBlockCatchFire(gx + 1, gy, gz) || _CanBlockCatchFire(gx - 1, gy, gz) ||
               _CanBlockCatchFire(gx, gy - 1, gz) || _CanBlockCatchFire(gx, gy + 1, gz) ||
               _CanBlockCatchFire(gx, gy, gz - 1) || _CanBlockCatchFire(gx, gy, gz + 1);
    }

    private bool _CanBlockCatchFire(int gx, int gy, int gz)
    {
        byte b = world.GetBlock(gx, gy, gz);
        return _chanceToEncourageFire[b] > 0;
    }

    private int _GetEncouragementForPos(int gx, int gy, int gz)
    {
        if (world.GetBlock(gx, gy, gz) != B_AIR) return 0;
        int best = 0;
        byte b;
        b = world.GetBlock(gx + 1, gy, gz); if (_chanceToEncourageFire[b] > best) best = _chanceToEncourageFire[b];
        b = world.GetBlock(gx - 1, gy, gz); if (_chanceToEncourageFire[b] > best) best = _chanceToEncourageFire[b];
        b = world.GetBlock(gx, gy - 1, gz); if (_chanceToEncourageFire[b] > best) best = _chanceToEncourageFire[b];
        b = world.GetBlock(gx, gy + 1, gz); if (_chanceToEncourageFire[b] > best) best = _chanceToEncourageFire[b];
        b = world.GetBlock(gx, gy, gz - 1); if (_chanceToEncourageFire[b] > best) best = _chanceToEncourageFire[b];
        b = world.GetBlock(gx, gy, gz + 1); if (_chanceToEncourageFire[b] > best) best = _chanceToEncourageFire[b];
        return best;
    }

    // --- GRASS (BlockGrass.java) ---

    private void _UpdateTick_Grass(int gx, int gy, int gz)
    {
        // PARITY: BlockGrass.updateTick (Beta):
        //  - If full block light at (x,y+1,z) < 4 AND opacity of block above > 2 -> revert to dirt
        //  - Else if full block light at (x,y+1,z) >= 9 -> try to spread to a random dirt block
        //    in a 3x5x3 box around (x,y,z), and the target must also have light>=4 + opacity<=2 above.
        int lightAbove = world.GetFullBlockLightValue(gx, gy + 1, gz);
        byte above = world.GetBlock(gx, gy + 1, gz);
        int aboveOpacity = (above != B_AIR) ? blockTypeManager.GetBlockLightOpacity(above) : 0;

        if (lightAbove < 4 && aboveOpacity > 2)
        {
            world.SetBlock(gx, gy, gz, B_DIRT);
        }
        else if (lightAbove >= 9)
        {
            int tx = gx + (_NextRandom() % 3) - 1;
            int ty = gy + (_NextRandom() % 5) - 3;
            int tz = gz + (_NextRandom() % 3) - 1;
            if (world.GetBlock(tx, ty, tz) == B_DIRT)
            {
                byte aboveTarget = world.GetBlock(tx, ty + 1, tz);
                int targetOpacity = (aboveTarget != B_AIR) ? blockTypeManager.GetBlockLightOpacity(aboveTarget) : 0;
                int targetLight = world.GetFullBlockLightValue(tx, ty + 1, tz);
                if (targetOpacity <= 2 && targetLight >= 4)
                    world.SetBlock(tx, ty, tz, B_GRASS);
            }
        }
    }

    // --- MUSHROOM (BlockMushroom.java) ---
    // Spread to nearby air on random tick (1/100 chance) with light < 13 requirement.
    private void _UpdateTick_Mushroom(int gx, int gy, int gz, byte selfBlockId)
    {
        if ((_NextRandom() % 100) != 0) return;
        int light = world.GetFullBlockLightValue(gx, gy + 1, gz);
        if (light >= 13) return;

        // Try to spread to a random offset block
        int tx = gx + (_NextRandom() % 3) - 1;
        int ty = gy + (_NextRandom() % 2) - (_NextRandom() % 2);
        int tz = gz + (_NextRandom() % 3) - 1;
        if (world.GetBlock(tx, ty, tz) != B_AIR) return;
        // Must be on opaque block with light < 13
        byte below = world.GetBlock(tx, ty - 1, tz);
        if (below == B_AIR) return;
        if (blockTypeManager.GetBlockLightOpacity(below) < 13) return;
        int targetLight = world.GetFullBlockLightValue(tx, ty + 1, tz);
        if (targetLight >= 13) return;
        world.SetBlock(tx, ty, tz, selfBlockId);
    }

    // --- LEAVES (BlockLeaves.java) ---

    private void _UpdateTick_Leaves(int gx, int gy, int gz)
    {
        byte meta = world.GetBlockMetadata(gx, gy, gz);
        // Bit 3 (0x08) = needs decay check (set when nearby log removed)
        if ((meta & 8) == 0) return;

        // BFS: search for a log within 4 blocks
        // Simplified: scan a 9x9x9 cube for any log block
        bool foundLog = false;
        for (int dx = -4; dx <= 4 && !foundLog; dx++)
        {
            for (int dy = -4; dy <= 4 && !foundLog; dy++)
            {
                for (int dz = -4; dz <= 4 && !foundLog; dz++)
                {
                    if (world.GetBlock(gx + dx, gy + dy, gz + dz) == B_LOG)
                        foundLog = true;
                }
            }
        }

        if (foundLog)
        {
            // Clear the decay flag
            world.SetBlockMetadata(gx, gy, gz, (byte)(meta & ~8));
        }
        else
        {
            // Decay: remove the leaf
            world.SetBlock(gx, gy, gz, B_AIR);
        }
    }

    // --- CACTUS (BlockCactus.java) ---

    private void _UpdateTick_Cactus(int gx, int gy, int gz)
    {
        if (world.GetBlock(gx, gy + 1, gz) != B_AIR) return;

        int height = 1;
        while (world.GetBlock(gx, gy - height, gz) == B_CACTUS) height++;
        if (height >= 3) return;

        byte meta = world.GetBlockMetadata(gx, gy, gz);
        if (meta >= 15)
        {
            world.SetBlock(gx, gy + 1, gz, B_CACTUS);
            world.SetBlockMetadata(gx, gy, gz, 0);
        }
        else
        {
            world.SetBlockMetadata(gx, gy, gz, (byte)(meta + 1));
        }
    }

    private void _OnNeighborChange_Cactus(int gx, int gy, int gz)
    {
        if (!_CanCactusStay(gx, gy, gz))
            world.SetBlock(gx, gy, gz, B_AIR);
    }

    private bool _CanCactusStay(int gx, int gy, int gz)
    {
        // No solid blocks adjacent horizontally
        byte nx = world.GetBlock(gx - 1, gy, gz);
        byte px = world.GetBlock(gx + 1, gy, gz);
        byte nz = world.GetBlock(gx, gy, gz - 1);
        byte pz = world.GetBlock(gx, gy, gz + 1);
        if (nx != B_AIR && blockTypeManager.GetBlockIsSolid(nx)) return false;
        if (px != B_AIR && blockTypeManager.GetBlockIsSolid(px)) return false;
        if (nz != B_AIR && blockTypeManager.GetBlockIsSolid(nz)) return false;
        if (pz != B_AIR && blockTypeManager.GetBlockIsSolid(pz)) return false;
        byte below = world.GetBlock(gx, gy - 1, gz);
        return below == B_CACTUS || below == B_SAND;
    }

    // --- REED / SUGARCANE (BlockReed.java) ---

    private void _UpdateTick_Reed(int gx, int gy, int gz)
    {
        if (world.GetBlock(gx, gy + 1, gz) != B_AIR) return;

        int height = 1;
        while (world.GetBlock(gx, gy - height, gz) == B_REED) height++;
        if (height >= 3) return;

        byte meta = world.GetBlockMetadata(gx, gy, gz);
        if (meta >= 15)
        {
            world.SetBlock(gx, gy + 1, gz, B_REED);
            world.SetBlockMetadata(gx, gy, gz, 0);
        }
        else
        {
            world.SetBlockMetadata(gx, gy, gz, (byte)(meta + 1));
        }
    }

    private void _OnNeighborChange_Reed(int gx, int gy, int gz)
    {
        byte below = world.GetBlock(gx, gy - 1, gz);
        // PARITY: BlockReed.canBlockStay (Beta) — reed survives if:
        //   (a) block below is reed, OR
        //   (b) block below is grass/dirt AND there is water (any kind) in one of the four
        //       horizontal neighbors of the BELOW block.
        // Previous version omitted the water-adjacency check, so reed on bare dirt survived forever.
        if (below == B_REED) return;
        if (below == B_GRASS || below == B_DIRT)
        {
            int by = gy - 1;
            byte n1 = world.GetBlock(gx - 1, by, gz);
            byte n2 = world.GetBlock(gx + 1, by, gz);
            byte n3 = world.GetBlock(gx, by, gz - 1);
            byte n4 = world.GetBlock(gx, by, gz + 1);
            bool waterNearby =
                n1 == B_WATER_FLOWING || n1 == B_WATER_STILL ||
                n2 == B_WATER_FLOWING || n2 == B_WATER_STILL ||
                n3 == B_WATER_FLOWING || n3 == B_WATER_STILL ||
                n4 == B_WATER_FLOWING || n4 == B_WATER_STILL;
            if (waterNearby) return;
        }
        world.SetBlock(gx, gy, gz, B_AIR);
    }

    // --- FLOWER / MUSHROOM / TALLGRASS (BlockFlower.java) ---

    private void _OnNeighborChange_Flower(int gx, int gy, int gz)
    {
        // PARITY: BlockFlower.canBlockStay (Beta) — requires
        //   (getFullBlockLightValue(x,y+1,z) >= 8 || canBlockSeeTheSky(x,y+1,z))
        //   AND canThisPlantGrowOnThisBlockID(below).
        // Mushrooms (BlockMushroom override): require light < 13 AND opaque cube below.
        // Dead bush (BlockDeadBush override): requires SAND below.
        byte self = world.GetBlock(gx, gy, gz);
        byte below = world.GetBlock(gx, gy - 1, gz);

        if (self == B_DEADBUSH)
        {
            if (below != B_SAND) world.SetBlock(gx, gy, gz, B_AIR);
            return;
        }

        if (self == B_MUSHROOM_BROWN || self == B_MUSHROOM_RED)
        {
            // Mushroom: opaque below + light < 13
            int lightHere = world.GetFullBlockLightValue(gx, gy + 1, gz);
            if (lightHere >= 13 || below == B_AIR || blockTypeManager.GetBlockLightOpacity(below) < 13)
                world.SetBlock(gx, gy, gz, B_AIR);
            return;
        }

        // Flower / tallgrass: light >= 8 or can see sky, AND grass/dirt/farmland below
        bool soilOK = (below == B_GRASS || below == B_DIRT || below == B_FARMLAND);
        if (!soilOK) { world.SetBlock(gx, gy, gz, B_AIR); return; }

        int light = world.GetFullBlockLightValue(gx, gy + 1, gz);
        if (light < 8 && !world.CanBlockSeeTheSky(gx, gy, gz))
            world.SetBlock(gx, gy, gz, B_AIR);
    }

    // --- SAPLING (BlockSapling.java) ---

    private void _UpdateTick_Sapling(int gx, int gy, int gz)
    {
        // PARITY: BlockSapling.updateTick (Beta):
        //  - Requires `getBlockLightValue(x, y+1, z) >= 9` to even attempt growth.
        //  - Growth chance is `nextInt(30) == 0`, not `nextInt(7) != 0`.
        // The previous `% 7 != 0` form skipped 6/7 = 86% of ticks (effectively 1/7 growth);
        // Java's `nextInt(30) == 0` is 1/30. The old form grew saplings ~4x too fast.
        int lightAbove = world.GetFullBlockLightValue(gx, gy + 1, gz);
        if (lightAbove < 9) return;
        if ((_NextRandom() % 30) != 0) return;

        byte meta = world.GetBlockMetadata(gx, gy, gz);
        if ((meta & 8) == 0)
        {
            // Set stage flag
            world.SetBlockMetadata(gx, gy, gz, (byte)(meta | 8));
        }
        else
        {
            // Grow a simple tree (trunk + canopy)
            _GrowSimpleTree(gx, gy, gz);
        }
    }

    private void _GrowSimpleTree(int gx, int gy, int gz)
    {
        int trunkHeight = 4 + (_NextRandom() % 3);

        // Check space
        for (int h = 1; h <= trunkHeight + 1; h++)
        {
            if (world.GetBlock(gx, gy + h, gz) != B_AIR) return;
        }

        // Remove sapling
        world.SetBlock(gx, gy, gz, B_AIR);

        // Place trunk
        for (int h = 0; h < trunkHeight; h++)
            world.SetBlock(gx, gy + h, gz, B_LOG);

        // Place canopy (simple sphere-ish)
        int canopyBase = gy + trunkHeight - 2;
        for (int dy = 0; dy <= 2; dy++)
        {
            int radius = (dy < 2) ? 2 : 1;
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    if (dx == 0 && dz == 0 && dy < 2) continue; // trunk occupies center
                    if (Mathf.Abs(dx) == radius && Mathf.Abs(dz) == radius && dy < 2 && (_NextRandom() % 2) == 0) continue;
                    int lx = gx + dx, ly = canopyBase + dy, lz = gz + dz;
                    if (world.GetBlock(lx, ly, lz) == B_AIR)
                        world.SetBlock(lx, ly, lz, B_LEAVES);
                }
            }
        }
    }

    // --- FARMLAND (BlockFarmland.java) ---

    private void _UpdateTick_Farmland(int gx, int gy, int gz)
    {
        // PARITY: Java BlockFarmland.updateTick wraps the entire body in
        // `if (rand.nextInt(5) == 0)`. Without it, farmland updated 5x too fast.
        if ((_NextRandom() % 5) != 0) return;

        // Check for water within 4 blocks horizontally and 1 block vertically
        bool hydrated = false;
        for (int dx = -4; dx <= 4 && !hydrated; dx++)
        {
            for (int dz = -4; dz <= 4 && !hydrated; dz++)
            {
                byte b = world.GetBlock(gx + dx, gy, gz + dz);
                if (b == B_WATER_FLOWING || b == B_WATER_STILL) hydrated = true;
                b = world.GetBlock(gx + dx, gy + 1, gz + dz);
                if (b == B_WATER_FLOWING || b == B_WATER_STILL) hydrated = true;
            }
        }

        byte meta = world.GetBlockMetadata(gx, gy, gz);
        if (hydrated)
        {
            world.SetBlockMetadata(gx, gy, gz, 7);
        }
        else if (meta > 0)
        {
            world.SetBlockMetadata(gx, gy, gz, (byte)(meta - 1));
        }
        else
        {
            // Not hydrated and meta is 0 — revert to dirt if nothing planted above
            byte above = world.GetBlock(gx, gy + 1, gz);
            if (above != B_CROPS)
                world.SetBlock(gx, gy, gz, B_DIRT);
        }
    }

    // --- ICE (BlockIce.java) ---

    private void _UpdateTick_Ice(int gx, int gy, int gz)
    {
        // PARITY: BlockIce.updateTick (Beta) melts if BLOCK-emitted light at this position > 11.
        // It does NOT melt from sky exposure alone. The previous implementation inverted this:
        // it melted under open sky and never melted under torches — opposite of Beta.
        int blockLight = world.GetBlockLightValue(gx, gy, gz);
        if (blockLight > 11 - blockTypeManager.GetBlockLightOpacity(B_ICE))
        {
            world.SetBlock(gx, gy, gz, B_WATER_STILL);
        }
    }

    // --- SNOW LAYER (BlockSnow.java) ---

    private void _UpdateTick_Snow(int gx, int gy, int gz)
    {
        // PARITY: BlockSnow.updateTick (Beta) melts when BLOCK-emitted light > 11 — guaranteed,
        // no probability gate. Previous impl used sky-exposure + 25% probability.
        int blockLight = world.GetBlockLightValue(gx, gy, gz);
        if (blockLight > 11)
        {
            world.SetBlock(gx, gy, gz, B_AIR);
        }
    }

    // --- CROPS (BlockCrops.java) ---

    private void _UpdateTick_Crops(int gx, int gy, int gz)
    {
        // PARITY: BlockCrops.updateTick (Beta):
        //  - Requires light at (x, y+1, z) >= 9. (Missing entirely before.)
        //  - Growth chance is `nextInt(max(1, 100/growthRate)) == 0` where growthRate depends
        //    on neighboring farmland hydration and crop layout. We approximate growthRate
        //    here by sampling: hydrated farmland below = base rate; dry farmland = slower.
        byte meta = world.GetBlockMetadata(gx, gy, gz);
        if (meta >= 7) return; // Fully grown

        int lightAbove = world.GetFullBlockLightValue(gx, gy + 1, gz);
        if (lightAbove < 9) return;

        // Approximate growth rate from underlying farmland metadata.
        // farmland meta 7 = hydrated, 0 = bone dry. Bias growthRate by hydration level.
        byte belowFarmlandMeta = world.GetBlockMetadata(gx, gy - 1, gz);
        byte belowBlock = world.GetBlock(gx, gy - 1, gz);
        // baseRate = 1 (dry farmland), up to ~4 (hydrated). Java's full formula is more nuanced
        // but this captures the dominant hydration term within a parity tolerance.
        int growthRate = 1;
        if (belowBlock == B_FARMLAND)
        {
            growthRate = (belowFarmlandMeta > 0) ? 4 : 2;
        }
        int chance = 100 / growthRate;
        if (chance < 1) chance = 1;
        if ((_NextRandom() % chance) == 0)
        {
            world.SetBlockMetadata(gx, gy, gz, (byte)(meta + 1));
        }
    }

    // =====================================================================
    // FALLING BLOCK ENTITIES (EntityFallingSand.java)
    // =====================================================================

    private void _SpawnFallingBlock(int gx, int gy, int gz, byte blockId)
    {
        if (fallingBlockPool == null) return;

        int slot = -1;
        for (int i = 0; i < FALL_POOL_MAX && i < fallingBlockPool.Length; i++)
        {
            if (!_fallActive[i])
            {
                slot = i;
                break;
            }
        }

        if (slot < 0)
        {
#if LOGGING
            stats_fallingBlockPoolExhausted++;
#endif
            // Pool exhausted — fall instantly
            int targetY = gy - 1;
            while (targetY > 0 && _CanFallBelow(gx, targetY - 1, gz))
                targetY--;
            if (targetY > 0)
                world.SetBlock(gx, targetY, gz, blockId);
            return;
        }

        // MC: EntityFallingSand spawns at block center (x+0.5, y+0.5, z+0.5)
        _fallPosX[slot] = gx + 0.5f;
        _fallPosY[slot] = gy + 0.5f;
        _fallPosZ[slot] = gz + 0.5f;
        _fallVelY[slot] = 0f;
        _fallBlockId[slot] = blockId;
        _fallActive[slot] = true;
        _fallTicks[slot] = 0;

        // PARITY: Java clears the origin cell EXACTLY ONCE at entity spawn time, not on every
        // physics tick. (`worldObj.setBlockWithNotify(floor(posX), floor(posY), floor(posZ), 0);`
        // in EntityFallingSand's constructor.) Doing it per-tick would erase a freshly placed
        // landing block when the visual position lands inside the target cell.
        if (world.GetBlock(gx, gy, gz) == blockId)
        {
            world.SetBlock(gx, gy, gz, B_AIR);
        }
#if LOGGING
        stats_fallingBlockSpawns++;
#endif

        if (fallingBlockPool[slot] != null)
        {
            fallingBlockPool[slot].position = new Vector3(_fallPosX[slot], _fallPosY[slot], _fallPosZ[slot]);
            fallingBlockPool[slot].localScale = new Vector3(0.98f, 0.98f, 0.98f);
            fallingBlockPool[slot].gameObject.SetActive(true);

            // Set texture slice on the material if renderer is available
            if (fallingBlockRenderers != null && slot < fallingBlockRenderers.Length && fallingBlockRenderers[slot] != null)
            {
                int texSlice = blockTypeManager.GetBlockTextureSlice_AllFaces(blockId);
                fallingBlockRenderers[slot].material.SetFloat("_TexIndex", texSlice);
            }
        }
    }

    private void _UpdateFallingBlocks()
    {
        if (fallingBlockPool == null) return;
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // MC runs entity physics at 20 TPS. Scale forces by tick ratio.
        float tickRatio = dt / 0.05f;

        for (int i = 0; i < FALL_POOL_MAX && i < fallingBlockPool.Length; i++)
        {
            if (!_fallActive[i]) continue;

            _fallTicks[i]++;

            // MC: motionY -= 0.04 per tick, then motionY *= 0.98
            _fallVelY[i] -= 0.04f * tickRatio;
            _fallVelY[i] *= Mathf.Pow(0.98f, tickRatio);

            _fallPosY[i] += _fallVelY[i] * tickRatio;

            // Update visual
            if (fallingBlockPool[i] != null)
            {
                fallingBlockPool[i].position = new Vector3(_fallPosX[i], _fallPosY[i], _fallPosZ[i]);
            }

            // Check landing: block below feet position
            int bx = Mathf.FloorToInt(_fallPosX[i]);
            int by = Mathf.FloorToInt(_fallPosY[i] - 0.5f);
            int bz = Mathf.FloorToInt(_fallPosZ[i]);

            // PARITY: Java EntityFallingSand clears its ORIGIN block (the cell it spawned in)
            // ONCE at spawn time, not on every physics tick. Re-clearing every tick at the
            // current Y could wipe a freshly placed landing block when the entity's visual
            // position lands inside the cell containing its target. The block-id check makes
            // it less catastrophic but still wrong when sand falls on sand. The spawn-time
            // clear is handled in _SpawnFallingBlock; here we just do collision/landing.

            bool landed = !_CanFallBelow(bx, by, bz) && _fallVelY[i] <= 0f;
            bool timedOut = _fallTicks[i] > 600;
            bool belowWorld = _fallPosY[i] < -64f;

            if (landed || timedOut || belowWorld)
            {
                _fallActive[i] = false;
                if (fallingBlockPool[i] != null)
                    fallingBlockPool[i].gameObject.SetActive(false);

                if (landed)
                {
#if LOGGING
                    stats_fallingBlockLandings++;
#endif
                    int placeY = by + 1;
                    byte existing = world.GetBlock(bx, placeY, bz);
                    if (existing == B_AIR || existing == B_FIRE ||
                        existing == B_WATER_FLOWING || existing == B_WATER_STILL ||
                        existing == B_LAVA_FLOWING || existing == B_LAVA_STILL)
                    {
                        world.SetBlock(bx, placeY, bz, _fallBlockId[i]);
                    }
                }
            }
        }
    }

#if LOGGING
    public void AppendAggregatePerformanceStats(StringBuilder sb)
    {
        if (sb == null || stats_tickFrames <= 0) return;

        float avgScheduledMs = stats_scheduledTickTimeMs / stats_tickFrames;
        float avgFlushMs = stats_meshFlushTimeMs / stats_tickFrames;
        float avgRandomMs = stats_randomTickTimeMs / stats_tickFrames;
        float avgFallingMs = stats_fallingBlockTimeMs / stats_tickFrames;
        float totalMs = stats_scheduledTickTimeMs + stats_meshFlushTimeMs + stats_randomTickTimeMs + stats_fallingBlockTimeMs;
        float avgTotalMs = totalMs / stats_tickFrames;

        sb.AppendLine("Block Ticker:");
        sb.AppendFormat("  Frames: {0}, avg {1:F3}ms/frame (scheduled {2:F3}ms, mesh flush {3:F3}ms, random {4:F3}ms, falling {5:F3}ms)\n",
            stats_tickFrames, avgTotalMs, avgScheduledMs, avgFlushMs, avgRandomMs, avgFallingMs);
        sb.AppendFormat("  Scheduled ticks: {0} processed, queue peak {1}/{2}\n",
            stats_scheduledTicksProcessed, stats_queuePeakCount, TICK_QUEUE_CAPACITY);
        if (stats_queueOverflows > 0)
            sb.AppendFormat("  WARNING: {0} tick queue overflows\n", stats_queueOverflows);
        sb.AppendFormat("  Random ticks: {0} dispatches across {1} chunk scans\n",
            stats_randomTicksProcessed, stats_randomTickChunksScanned);

        int totalDispatches = stats_dispatchWaterFlowing + stats_dispatchLavaFlowing +
            stats_dispatchLavaStill + stats_dispatchFire + stats_dispatchSand +
            stats_dispatchGrass + stats_dispatchLeaves + stats_dispatchCactus +
            stats_dispatchReed + stats_dispatchSapling + stats_dispatchFarmland +
            stats_dispatchIce + stats_dispatchSnow + stats_dispatchCrops + stats_dispatchOther;

        if (totalDispatches > 0)
        {
            sb.AppendFormat("  Dispatch breakdown ({0} total):\n", totalDispatches);
            if (stats_dispatchWaterFlowing > 0) sb.AppendFormat("    water_flowing: {0}\n", stats_dispatchWaterFlowing);
            if (stats_dispatchLavaFlowing > 0) sb.AppendFormat("    lava_flowing: {0}\n", stats_dispatchLavaFlowing);
            if (stats_dispatchLavaStill > 0) sb.AppendFormat("    lava_still(ignition): {0}\n", stats_dispatchLavaStill);
            if (stats_dispatchFire > 0) sb.AppendFormat("    fire: {0}\n", stats_dispatchFire);
            if (stats_dispatchSand > 0) sb.AppendFormat("    sand/gravel: {0}\n", stats_dispatchSand);
            if (stats_dispatchGrass > 0) sb.AppendFormat("    grass: {0}\n", stats_dispatchGrass);
            if (stats_dispatchLeaves > 0) sb.AppendFormat("    leaves: {0}\n", stats_dispatchLeaves);
            if (stats_dispatchCactus > 0) sb.AppendFormat("    cactus: {0}\n", stats_dispatchCactus);
            if (stats_dispatchReed > 0) sb.AppendFormat("    reed: {0}\n", stats_dispatchReed);
            if (stats_dispatchSapling > 0) sb.AppendFormat("    sapling: {0}\n", stats_dispatchSapling);
            if (stats_dispatchFarmland > 0) sb.AppendFormat("    farmland: {0}\n", stats_dispatchFarmland);
            if (stats_dispatchIce > 0) sb.AppendFormat("    ice: {0}\n", stats_dispatchIce);
            if (stats_dispatchSnow > 0) sb.AppendFormat("    snow: {0}\n", stats_dispatchSnow);
            if (stats_dispatchCrops > 0) sb.AppendFormat("    crops: {0}\n", stats_dispatchCrops);
            if (stats_dispatchOther > 0) sb.AppendFormat("    other: {0}\n", stats_dispatchOther);
        }

        if (stats_fallingBlockSpawns > 0 || stats_fallingBlockLandings > 0 || stats_fallingBlockPoolExhausted > 0)
        {
            sb.AppendFormat("  Falling blocks: {0} spawned, {1} landed, {2} pool exhausted, peak active {3}/{4}\n",
                stats_fallingBlockSpawns, stats_fallingBlockLandings, stats_fallingBlockPoolExhausted,
                stats_activeFallingPeak, FALL_POOL_MAX);
        }
    }

    public void ResetAggregatePerformanceStats()
    {
        stats_scheduledTickTimeMs = 0f;
        stats_meshFlushTimeMs = 0f;
        stats_randomTickTimeMs = 0f;
        stats_fallingBlockTimeMs = 0f;
        stats_scheduledTicksProcessed = 0;

        stats_randomTicksProcessed = 0;
        stats_randomTickChunksScanned = 0;
        stats_tickFrames = 0;
        stats_queuePeakCount = 0;
        stats_queueOverflows = 0;
        stats_fallingBlockSpawns = 0;
        stats_fallingBlockLandings = 0;
        stats_fallingBlockPoolExhausted = 0;
        stats_activeFallingPeak = 0;
        stats_dispatchWaterFlowing = 0;
        stats_dispatchLavaFlowing = 0;
        stats_dispatchLavaStill = 0;
        stats_dispatchFire = 0;
        stats_dispatchSand = 0;
        stats_dispatchGrass = 0;
        stats_dispatchLeaves = 0;
        stats_dispatchCactus = 0;
        stats_dispatchReed = 0;
        stats_dispatchSapling = 0;
        stats_dispatchFarmland = 0;
        stats_dispatchIce = 0;
        stats_dispatchSnow = 0;
        stats_dispatchCrops = 0;
        stats_dispatchOther = 0;
    }
#endif
}
