#define LOGGING

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRRefAssist;
using System.Text;

/// <summary>
/// Manages world generation and meshing using a fixed-size worker pool
/// and a single, perpetual processing loop.
/// 1.  PERPETUAL LOOP: The coordinator now runs a single `ProcessWorkers` loop that
///     never stops. It handles initial world generation and seamlessly transitions
///     to processing player-initiated mesh updates (e.g., from SetBlock) without
///     entering a separate, faulty "idle" state.
/// 2.  ROBUST STATE HANDLING: The loop continuously checks worker states and the
///     rebuild queue, ensuring all generation and meshing tasks are completed.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
[Singleton]
public class McCoordinator : UdonSharpBehaviour
{
    private McWorld world;

    [Header("Performance")]
    [Tooltip("How many chunks can be processed (data-gen or meshing) concurrently. On the GPU path a high count overlapped GPU readback latency (each mesh worker held one of 48 in-flight readbacks). On the CPU-only path there is NO latency to overlap — all workers share the single Udon thread, so too many just fragments the per-frame budget across more partially-done chunks and adds per-cycle overhead. 16 concentrates the budget (each chunk finishes sooner) while still allowing gen+mesh overlap (~8 generators + ~8 mesh).")]
    public int maxConcurrentWorkers = 16;
    [Tooltip("During the ONE-TIME bulk load, cap how many workers may be MESHING at once so the rest stay free to feed the generators. Meshing otherwise expands to fill the whole worker pool and starves data generation (which is the slow, readback-bound phase on Quest). Only chunks whose neighbours are all data-ready ever reach the cap, so the deferred ones drain safely (shell chunks wake eagerly) and the visible world does not fragment. Lifts automatically once data-gen completes. Default OFF — enable and headset-benchmark before relying on it.")]
    public bool reserveWorkersForDataGenDuringLoad = false;
    [Tooltip("Max workers allowed to mesh concurrently during the bulk load when reserveWorkersForDataGenDuringLoad is on. The remaining (maxConcurrentWorkers - this) feed data generation. ~8 leaves one worker per generator.")]
    public int loadPhaseMeshWorkerCap = 8;
    [Tooltip("DEBUG/validation only. After a generator finishes a column, keep its slot 'busy' for this many extra frames to SIMULATE Quest's GPU-readback occupancy inside ClientSim (where readback is near-instant). Use it to verify on desktop that the look-ahead dispatch actually keeps all generators busy concurrently. 0 = off (no effect on real runs). Leave at 0 for shipping.")]
    public int debugGenSlotHoldFrames = 0;
    private int[] genSlotReleaseDelay; // per-slot countdown for debugGenSlotHoldFrames
    [Tooltip("Time budget per Update() call in milliseconds to prevent frame drops.")]
    public float updateTimeBudgetMs = 16.0f;
    [Tooltip("While the ONE-TIME initial world generation is running, use this larger per-cycle budget so terrain data-gen completes faster (frame rate during the load matters far less than total load time). Reverts to updateTimeBudgetMs automatically once gen completes. Set <= updateTimeBudgetMs to disable.")]
    public float loadPhaseUpdateBudgetMs = 60.0f;
    [Tooltip("Skip N state checks per worker to reduce overhead (higher = less responsive but faster)")]
    public int skipCheckCycles = 0;
    [Tooltip("When the main rebuild queue is this small or smaller, allow a few deferred interior wakes to drain in the same cycle.")]
    public int deferredMeshWakeQueueThreshold = 32;
    [Tooltip("Maximum deferred interior wake assignments per coordinator cycle while the main rebuild queue still has work.")]
    public int deferredMeshWakeBurstPerCycle = 1;
    [Tooltip("How many brand-new chunks may be instantiated per coordinator cycle. Larger values let an idle column drain into multiple workers in one frame after a readback completes (worldgen throughput multiplier).")]
    public int maxChunkInstantiationsPerCycle = 16;

    [Header("Workload Per Step")]
    [Tooltip("How many Z-columns of voxels to generate per step inside a chunk. Higher values generate chunks faster but may cause lag spikes.")]
    public int columnsPerDataGenStep = 4;
    [Tooltip("How many voxels to check for meshing per step inside a chunk. Higher values build meshes faster but may cause lag spikes.")]
    public int voxelsPerMeshStep = 2048;
    [Tooltip("How many voxels to process when generating data.")]
    public int voxelsPerTerrainStep = 2048;


    // --- Worker Pool State ---
    private int[] worker_targetChunkIndex;
    private int[] worker_state;
    private bool[] worker_usesExclusiveGenerator;
    private bool[] worker_isDeferredMeshWake;
    private int[] worker_skipCheckCounter; // Skip state checks for N cycles to reduce overhead
    private int[] worker_meshFrames;       // WATCHDOG: frames a worker has been in STATE_MESHING
    // A mesh build that never completes wedges a worker (and its mesh-pool slot) forever. After this
    // many frames in STATE_MESHING the coordinator force-releases the build so workers never hang.
    // Generous: a normal build finishes in well under this even at low fps; only a true hang trips it.
    private const int MESH_WATCHDOG_FRAMES = 300;
    private const int STATE_IDLE = 0;
    private const int STATE_DATA_GEN = 1;
    private const int STATE_LIGHTING = 2; // FIXED: New lighting state
    private const int STATE_WAITING_FOR_MESH = 3;
    private const int STATE_MESHING = 4;

    // --- World Generation State ---
    private int[] radialChunkOrder;
    private int nextChunkIndexToAssign = 0;
    private int totalWorldChunks;
    private int chunksCompletedCount = 0;
    // Concurrent column dispatch. Tracks which radialChunkOrder positions have been handed to a
    // worker, so the assignment can scan PAST the in-flight column (whose sibling Y-chunks are
    // still waiting on the GPU readback) to start OTHER columns on otherwise-idle generators.
    // Without this look-ahead the strictly-sequential cursor stalled all generators down to one
    // column at a time (~concurrency 1). The scan always runs forward from the low-water-mark, so
    // a column's cached siblings (earlier positions) are always drained before that generator is
    // reused for a later column — preserving the single-column cache. Terrain data is
    // dispatch-order-independent (fingerprint-validated: sum=2213895521).
    private bool[] _positionAssigned;
    private int _lastPickedDataGenPos = -1;
    [Tooltip("How many positions ahead of the assignment low-water-mark to scan for a chunk that can start now (free generator for a new column, or a sibling whose column cache is ready). Larger = more concurrent columns in flight, up to the generator count. ~12 columns (96) covers 8 generators.")]
    public int dataGenLookaheadWindow = 96;
    // Per-generator-slot busy flags (size = world.GetGeneratorCount()). Replaces the old single
    // isGeneratorBusy so up to N columns can generate concurrently (one per generator instance).
    private bool[] genSlotBusy;
    private int[] worker_generatorSlot; // generator slot each worker holds (-1 = none)
    private float benchmarkStartTime = 0f;

    // NEAR-REGION RENDER benchmark: time until every chunk within nearRegionRadius (XZ) has its
    // render mesh built. Deduped per chunkIndex so deferred/re-queued chunks count once.
    private bool[] _nearMeshCounted;
    private int _nearMeshDone = 0;
    private bool _nearMeshLogged = false;
    
    // --- Player-Initiated Rebuild Queue ---
    private int[] chunkRebuildQueue;
    private int chunkRebuildQueue_head = 0;
    private int chunkRebuildQueue_tail = 0;
    private int chunkRebuildQueue_count = 0;
    private const int MAX_REBUILD_QUEUE_SIZE = 256;
    private int[] deferredMeshQueue;
    private int deferredMeshQueue_head = 0;
    private int deferredMeshQueue_tail = 0;
    private int deferredMeshQueue_count = 0;
    private const int MAX_DEFERRED_MESH_QUEUE_SIZE = 256;
    private int borderHealWorkerCursor = 0;
    private readonly int[] _healDx = { 1, -1, 0,  0, 0,  0 };
    private readonly int[] _healDy = { 0,  0, 1, -1, 0,  0 };
    private readonly int[] _healDz = { 0,  0, 0,  0, 1, -1 };

#if LOGGING
    [Header("Debug")] 
    public bool enableVerboseLogging = true;
    
    [Header("Performance Profiling")]
    public bool enableDetailedTimings = false;
    public bool enableCounters = true;
    public bool enableAggregateLogging = true;
    public int aggregateLogInterval = 300; // frames
    
    private int lastLoggedPercent = -1;
    
    // Detailed timing metrics
    private float time_UpdateWorkers;
    private float time_AssignWork;
    private float time_RebuildQueue;
    private float time_WorldGen;
    private float time_TotalCycle;
    private int cycles_Processed;
    private int workers_DataGenCompleted;
    private int workers_MeshCompleted;
    private int workers_MeshDeferred;
    private int rebuilds_Processed;
    private int deferredMeshWakeAssignments;
    private int worldChunks_Assigned;
    private int peak_ActiveWorkers;
    private int peak_DataGenWorkers;
    private int peak_MeshingWorkers;
    private int peak_RebuildQueue;
    private int peak_DeferredMeshQueue;
#endif

    public void InitializeAndStartProcessing(McWorld worldInstance, int[] generatedRadialOrder, int worldTotalChunks)
    {
        this.world = worldInstance;
        this.radialChunkOrder = generatedRadialOrder;
        this.totalWorldChunks = worldTotalChunks;
        this.nextChunkIndexToAssign = 0;
        this.chunksCompletedCount = 0;
        this.benchmarkStartTime = Time.realtimeSinceStartup;
        this._nearMeshCounted = new bool[worldTotalChunks];
        this._nearMeshDone = 0;
        this._nearMeshLogged = false;
#if LOGGING
        this.lastLoggedPercent = -1;
#endif

        if (world == null)
        {
            Debug.LogError("[McCoordinator] McWorld instance is null! Aborting initialization.");
            this.enabled = false;
            return;
        }
        
        worker_targetChunkIndex = new int[maxConcurrentWorkers];
        worker_state = new int[maxConcurrentWorkers];
        worker_usesExclusiveGenerator = new bool[maxConcurrentWorkers];
        worker_isDeferredMeshWake = new bool[maxConcurrentWorkers];
        worker_skipCheckCounter = new int[maxConcurrentWorkers];
        worker_meshFrames = new int[maxConcurrentWorkers];
        genSlotBusy = new bool[Mathf.Max(1, world != null ? world.GetGeneratorCount() : 1)];
        genSlotReleaseDelay = new int[genSlotBusy.Length];
        worker_generatorSlot = new int[maxConcurrentWorkers];
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            worker_targetChunkIndex[i] = -1; // -1 indicates no chunk
            worker_state[i] = STATE_IDLE;
            worker_usesExclusiveGenerator[i] = false;
            worker_isDeferredMeshWake[i] = false;
            worker_skipCheckCounter[i] = 0;
            worker_generatorSlot[i] = -1;
        }

        chunkRebuildQueue = new int[MAX_REBUILD_QUEUE_SIZE];
        deferredMeshQueue = new int[MAX_DEFERRED_MESH_QUEUE_SIZE];
        _positionAssigned = new bool[totalWorldChunks];

        // No longer using SendCustomEventDelayedSeconds - Update() will handle processing
    }

    // Releases the generator slot a worker was holding (clears genSlotBusy for that slot).
    private void _ReleaseWorkerGeneratorSlot(int i)
    {
        int slot = worker_generatorSlot[i];
        if (slot >= 0 && genSlotBusy != null && slot < genSlotBusy.Length)
        {
            // DEBUG: simulate Quest readback occupancy by holding the slot busy a few more frames.
            if (debugGenSlotHoldFrames > 0 && genSlotReleaseDelay != null && slot < genSlotReleaseDelay.Length)
                genSlotReleaseDelay[slot] = debugGenSlotHoldFrames;
            else
                genSlotBusy[slot] = false;
        }
        worker_generatorSlot[i] = -1;
    }

    // Count of workers currently in STATE_MESHING — used by the data-gen worker reservation
    // to cap concurrent meshing during the bulk load.
    private int _CountMeshingWorkers()
    {
        int c = 0;
        for (int i = 0; i < maxConcurrentWorkers; i++) if (worker_state[i] == STATE_MESHING) c++;
        return c;
    }

    // Count of workers currently in STATE_WAITING_FOR_MESH — used by the strict-neighbour mesh
    // gate to detect a potential deadlock (all workers waiting, none free to instantiate the
    // missing neighbour) and fall back to lenient (air-boundary) meshing to break it.
    private int _CountWaitingWorkers()
    {
        int c = 0;
        for (int i = 0; i < maxConcurrentWorkers; i++) if (worker_state[i] == STATE_WAITING_FOR_MESH) c++;
        return c;
    }

    // Scans forward from the assignment low-water-mark for the first chunk that can start data-gen
    // RIGHT NOW: either its generator slot is free (a new column) or its column cache is already
    // populated (a sibling Y-chunk of an in-flight/completed column). Returns true and sets
    // _lastPickedDataGenPos on success. This keeps every generator busy instead of serializing on
    // the in-flight column. Forward order guarantees a column's cached siblings (earlier positions)
    // are picked before any later column that would reuse the same generator, so the single-column
    // cache is never evicted out from under un-drained siblings.
    private bool _TryPickDataGenPosition()
    {
        if (radialChunkOrder == null || _positionAssigned == null) return false;
        int scanEnd = nextChunkIndexToAssign + dataGenLookaheadWindow;
        if (scanEnd > totalWorldChunks) scanEnd = totalWorldChunks;
        for (int p = nextChunkIndexToAssign; p < scanEnd; p++)
        {
            if (_positionAssigned[p]) continue;
            int ci = radialChunkOrder[p];
            if (!genSlotBusy[world.GeneratorSlotForChunkIndex(ci)] || world.CanStartChunkDataGenerationWithoutExclusiveGenerator(ci))
            {
                _lastPickedDataGenPos = p;
                return true;
            }
        }

        // FALLBACK (anti-stall): the windowed scan can come up empty when the next
        // dataGenLookaheadWindow positions are all already-assigned or map to the few busy
        // generator slots, while the pickable (unassigned + FREE-slot) positions sit further ahead.
        // That froze data-gen with idle workers and unassigned chunks remaining (observed: idle
        // workers, free slots, completed stuck). Scan the whole remaining order for an unassigned
        // position whose generator slot is FREE. Using a free slot evicts no in-use column cache,
        // so the close-sibling-first invariant the window protects is preserved.
        for (int p = scanEnd; p < totalWorldChunks; p++)
        {
            if (_positionAssigned[p]) continue;
            int ci = radialChunkOrder[p];
            if (!genSlotBusy[world.GeneratorSlotForChunkIndex(ci)])
            {
                _lastPickedDataGenPos = p;
                return true;
            }
        }
        return false;
    }
    
    /// <summary>
    /// Called every frame by Unity. Processes worker states and assigns new work.
    /// OPTIMIZED: Using Update() instead of SendCustomEventDelayedSeconds eliminates 50-100ms overhead per cycle!
    /// </summary>
    void Update()
    {
        // Safety check: Don't process if not initialized
        if (world == null || worker_state == null || radialChunkOrder == null) return;
        
        float cycleStartTime = Time.realtimeSinceStartup;
        // Load-phase budget boost: terrain data-gen drains faster at a larger budget during
        // the one-time initial generation; revert to the normal budget once complete so
        // steady-state frame rate is protected. Accuracy is unaffected.
        float effectiveBudgetMs = (chunksCompletedCount < totalWorldChunks && loadPhaseUpdateBudgetMs > updateTimeBudgetMs)
            ? loadPhaseUpdateBudgetMs : updateTimeBudgetMs;
        float cycleBudget = effectiveBudgetMs * 0.001f;

        // DEBUG (debugGenSlotHoldFrames): release generator slots whose simulated readback-occupancy
        // hold has elapsed. No-op when the knob is 0.
        if (debugGenSlotHoldFrames > 0 && genSlotReleaseDelay != null && genSlotBusy != null)
        {
            for (int s = 0; s < genSlotReleaseDelay.Length; s++)
            {
                if (genSlotReleaseDelay[s] > 0)
                {
                    genSlotReleaseDelay[s] = genSlotReleaseDelay[s] - 1;
                    if (genSlotReleaseDelay[s] == 0) genSlotBusy[s] = false;
                }
            }
        }

#if LOGGING
        System.DateTime cycleStart = System.DateTime.MinValue;
        System.DateTime stepStart = System.DateTime.MinValue;
        if (enableDetailedTimings || enableAggregateLogging)
        {
            cycleStart = System.DateTime.UtcNow;
            stepStart = cycleStart;
        }
#endif

        // --- 1. Update state of existing workers (HEAVILY OPTIMIZED) ---
        // Cache chunks_1D array locally to avoid repeated field access
        ChunkData[] chunks = world.chunks_1D;
        int chunksLen = chunks != null ? chunks.Length : 0;
        
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            if (Time.realtimeSinceStartup - cycleStartTime > cycleBudget) break; // Don't exceed budget
            
            int state = worker_state[i];
            if (state == STATE_IDLE) continue;

            int chunkIndex = worker_targetChunkIndex[i];
            if (chunkIndex == -1 || chunkIndex >= chunksLen) {
                worker_state[i] = STATE_IDLE;
                _ReleaseWorkerGeneratorSlot(i);
                worker_usesExclusiveGenerator[i] = false;
                worker_isDeferredMeshWake[i] = false;
                worker_skipCheckCounter[i] = 0;
                continue;
            }

            // Direct chunk access (already validated index)
            ChunkData chunk = chunks[chunkIndex];
            if (chunk == null)
            {
                worker_state[i] = STATE_IDLE;
                worker_targetChunkIndex[i] = -1;
                _ReleaseWorkerGeneratorSlot(i);
                worker_usesExclusiveGenerator[i] = false;
                worker_isDeferredMeshWake[i] = false;
                continue;
            }

            // OPTIMIZATION: Re-evaluation loop — cascade state transitions within a single frame.
            // When a worker finishes data gen, it can immediately check neighbors, start meshing, etc.
            // without waiting for the next Update() call. Critical for cached Y-chunks that complete in <1ms.
            bool recheck = true;
            while (recheck)
            {
                recheck = false;
                if (Time.realtimeSinceStartup - cycleStartTime > cycleBudget) break;
                
                state = worker_state[i];

                if (state == STATE_DATA_GEN)
                {
                    // OPTIMIZATION: Actively drive data gen forward instead of passively waiting.
                    // This eliminates the cross-Update handoff delay between McWorld and coordinator.
                    if (chunk.isGeneratingData)
                    {
                        world.StepChunkDataGeneration(chunk);
                    }

                    // Check if data generation is complete
                    if (!chunk.isGeneratingData)
                    {
                        if (worker_usesExclusiveGenerator[i])
                        {
                            _ReleaseWorkerGeneratorSlot(i);
                            worker_usesExclusiveGenerator[i] = false;
                        }

                        if (world.UsesGpuLightingBackend() && !world.RequiresCpuLightingForAmbientOcclusion())
                        {
                            worker_state[i] = STATE_WAITING_FOR_MESH;
                            worker_skipCheckCounter[i] = 0;
                            // Trigger neighbor re-meshing so already-meshed neighbors
                            // update their boundary faces with this chunk's data
                            world.HandleChunkPostDataGpuLighting(chunk);
                        }
                        else
                        {
                            // Data generation is complete, move to lighting
                            worker_state[i] = STATE_LIGHTING;
                            worker_skipCheckCounter[i] = 0; // Start lighting immediately
                            world.StartChunkLighting(chunkIndex);
                        }
                        recheck = true; // Re-evaluate the new state immediately
#if LOGGING
                        if (enableDetailedTimings || enableAggregateLogging) workers_DataGenCompleted++;
#endif
                    }
                }
                else if (state == STATE_LIGHTING)
                {
                    if (world.UsesGpuLightingBackend() && !world.RequiresCpuLightingForAmbientOcclusion())
                    {
                        worker_state[i] = STATE_WAITING_FOR_MESH;
                        worker_skipCheckCounter[i] = 0;
                        recheck = true;
                        continue;
                    }

                    // FIXED: Step through lighting incrementally
                    world.StepChunkLighting(chunkIndex);
                    
                    // Check if lighting is complete
                    if (!chunk.isProcessingLighting)
                    {
                        // Lighting is complete, move to waiting for mesh
                        worker_state[i] = STATE_WAITING_FOR_MESH;
                        worker_skipCheckCounter[i] = 0; // Check immediately for neighbors
                        recheck = true;
                    }
                }
                else if (state == STATE_WAITING_FOR_MESH)
                {
                    // OPTIMIZATION: Only check if not already building mesh
                    if (!chunk.isBuildingMesh)
                    {
                        // Check if neighbors are ready. AreAllNeighborsReady is strict when
                        // requireAllNeighborsForMesh is on (waits for unallocated in-world
                        // neighbours so boundary faces cull correctly). If EVERY worker is stuck
                        // waiting (none free to instantiate the missing neighbour) fall back to
                        // the lenient air-boundary check to break the deadlock.
                        bool neighborsReadyForMesh = (_CountWaitingWorkers() >= maxConcurrentWorkers)
                            ? world.AreAllNeighborsReadyLenient(chunkIndex)
                            : world.AreAllNeighborsReady(chunkIndex);
                        if (neighborsReadyForMesh)
                        {
                            // Only defer mesh builds during initial worldgen, not rebuilds.
                            // A chunk with _borderMissingMask != 0 needs re-meshing to fix
                            // boundary artifacts — re-deferring it loops forever.
                            // A chunk whose opaque mesh already has vertices was already
                            // meshed once; this is a rebuild request, not a first build.
                            bool isRebuild = chunk._borderMissingMask != 0
                                || (chunk.opaqueMeshFilter != null && chunk.opaqueMeshFilter.sharedMesh != null && chunk.opaqueMeshFilter.sharedMesh.vertexCount > 0);
                            if (!isRebuild && !worker_isDeferredMeshWake[i] && world.ShouldDeferChunkMesh(chunkIndex))
                            {
                                world.MarkChunkMeshDeferred(chunkIndex);
                                worker_targetChunkIndex[i] = -1;
                                worker_state[i] = STATE_IDLE;
                                worker_isDeferredMeshWake[i] = false;
                                worker_skipCheckCounter[i] = 0;

                                if (chunksCompletedCount < totalWorldChunks)
                                {
                                    chunksCompletedCount++;
                                }
#if LOGGING
                                if (enableDetailedTimings || enableAggregateLogging) workers_MeshDeferred++;
#endif
                                continue;
                            }
                            // Data-gen worker reservation: during the bulk load, cap total mesh
                            // OCCUPANCY (actively meshing + parked waiting-for-mesh) so the rest of
                            // the pool stays free to feed the generators. The old check counted only
                            // STATE_MESHING, so workers piled up in STATE_WAITING_FOR_MESH (e.g. 6
                            // meshing + 10 waiting) and starved data-gen to ZERO -> the world hard-
                            // stalled incomplete. This chunk already passed the neighbour check, so
                            // deferring is fragmentation-safe (deferred scan + Priority 3 re-wake it).
                            // Player edits (interactionMeshPriority) bypass. Checked BEFORE the
                            // readback-slot gate so a capped worker frees instead of getting stuck.
                            if (reserveWorkersForDataGenDuringLoad && chunksCompletedCount < totalWorldChunks
                                && !chunk.interactionMeshPriority
                                && (_CountMeshingWorkers() + _CountWaitingWorkers()) > loadPhaseMeshWorkerCap)
                            {
                                world.MarkChunkMeshDeferred(chunkIndex);
                                worker_targetChunkIndex[i] = -1;
                                worker_state[i] = STATE_IDLE;
                                worker_isDeferredMeshWake[i] = false;
                                worker_skipCheckCounter[i] = 0;
                                if (chunksCompletedCount < totalWorldChunks) chunksCompletedCount++;
                                continue;
                            }
                            if (!world.HasAvailableGpuMeshReadbackSlot())
                            {
                                // No readback buffer free. Do NOT spin in STATE_WAITING_FOR_MESH —
                                // that parks the worker indefinitely and (with many such workers)
                                // starves data-gen. Free it; the deferred-mesh scan re-wakes the
                                // chunk once a slot frees. Keep isMeshDeferred so it isn't lost.
                                world.MarkChunkMeshDeferred(chunkIndex);
                                worker_targetChunkIndex[i] = -1;
                                worker_state[i] = STATE_IDLE;
                                worker_isDeferredMeshWake[i] = false;
                                worker_skipCheckCounter[i] = 0;
                                continue;
                            }
                            worker_isDeferredMeshWake[i] = false;
                            world.BuildChunkMesh(chunkIndex);
                            worker_state[i] = STATE_MESHING;
                            worker_skipCheckCounter[i] = 0;
                            // Don't recheck — meshing takes multiple frames
                        }
                        else
                        {
                            // Neighbours not ready (strict requireAllNeighborsForMesh). Do NOT hold
                            // the worker in STATE_WAITING_FOR_MESH — that starves data-gen of the
                            // very workers needed to GENERATE those neighbours (near-deadlock: all
                            // workers wait, nothing generates). Defer the chunk and free the worker;
                            // the deferred-mesh scan re-queues it once its neighbours are data-ready,
                            // so it meshes cleanly (boundary faces culled). Not counted complete here
                            // — it counts when it actually meshes.
                            world.MarkChunkMeshDeferred(chunkIndex);
                            worker_targetChunkIndex[i] = -1;
                            worker_state[i] = STATE_IDLE;
                            worker_isDeferredMeshWake[i] = false;
                            worker_skipCheckCounter[i] = 0;
                        }
                    }
                }
                else if (state == STATE_MESHING)
                {
                    // Check if mesh building is complete
                    if (!chunk.isBuildingMesh)
                    {
                        worker_meshFrames[i] = 0;
                        if (chunk.pendingChunkMeshRebuild)
                        {
                            chunk.pendingChunkMeshRebuild = false;
                            worker_state[i] = STATE_WAITING_FOR_MESH;
                            worker_isDeferredMeshWake[i] = false;
                            worker_skipCheckCounter[i] = 0;
                            recheck = true;
                            continue;
                        }

                        // Mesh building is complete
                        worker_targetChunkIndex[i] = -1;
                        worker_state[i] = STATE_IDLE;
                        worker_isDeferredMeshWake[i] = false;
                        worker_skipCheckCounter[i] = 0;
                        
                        if(chunksCompletedCount < totalWorldChunks)
                        {
                           chunksCompletedCount++;
                        }

                        // NEAR-REGION RENDER benchmark: this chunk just had its render mesh built.
                        // Count it once if it's inside the near radius; log when the whole near
                        // region (all Y of every column within nearRegionRadius) is meshed.
                        if (!_nearMeshLogged && _nearMeshCounted != null && chunkIndex >= 0 && chunkIndex < _nearMeshCounted.Length
                            && !_nearMeshCounted[chunkIndex]
                            && chunk.chunkX_world >= -world.nearRegionRadius && chunk.chunkX_world <= world.nearRegionRadius
                            && chunk.chunkZ_world >= -world.nearRegionRadius && chunk.chunkZ_world <= world.nearRegionRadius)
                        {
                            _nearMeshCounted[chunkIndex] = true;
                            _nearMeshDone++;
                            int nearRenderTotal = (world.nearRegionRadius * 2 + 1) * (world.nearRegionRadius * 2 + 1) * world.worldDimensionY;
                            if (_nearMeshDone >= nearRenderTotal)
                            {
                                _nearMeshLogged = true;
                                float nearElapsed = Time.realtimeSinceStartup - benchmarkStartTime;
                                Debug.Log("[NEAR_RENDER] " + world.nearRegionRadius + "-radius region (" + nearRenderTotal + " chunks) GEN+RENDERED in " + nearElapsed.ToString("F2") + "s");
                            }
                        }

                        // Benchmark: log time at 50% and 100% completion
                        if (chunksCompletedCount == (totalWorldChunks + 1) / 2)
                        {
                            float elapsed = Time.realtimeSinceStartup - benchmarkStartTime;
                            Debug.Log($"[BENCHMARK] 50% world gen at {elapsed:F3}s — chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) #{chunksCompletedCount}/{totalWorldChunks}");
                        }
                        else if (chunksCompletedCount == totalWorldChunks)
                        {
                            float elapsed = Time.realtimeSinceStartup - benchmarkStartTime;
                            Debug.Log($"[BENCHMARK] 100% world gen at {elapsed:F3}s — last chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) #{chunksCompletedCount}/{totalWorldChunks}");
                        }
#if LOGGING
                        if (enableDetailedTimings || enableAggregateLogging) workers_MeshCompleted++;
#endif
                        // Don't recheck — worker is now IDLE
                    }
                    else
                    {
                        // WATCHDOG: still building. A build that never completes must never wedge
                        // the worker (and the mesh-pool slot it may hold) forever — force-release
                        // after a frame cap so workers can never hang and the pool can't deadlock.
                        worker_meshFrames[i]++;
                        if (worker_meshFrames[i] > MESH_WATCHDOG_FRAMES)
                        {
                            int stuckIndex = worker_targetChunkIndex[i];
                            world.ForceCompleteStuckMesh(stuckIndex);
                            // CRITICAL: do NOT count this complete — the chunk has NO mesh. Re-queue
                            // it so it is never lost (was a permanent hole + miscount before). The
                            // deferred-mesh scan re-meshes it once budget frees up; with the meshing
                            // budget guarantee in ProcessActiveChunks this should rarely fire at all.
                            if (stuckIndex >= 0) world.MarkChunkMeshDeferred(stuckIndex);
                            worker_meshFrames[i] = 0;
                            worker_targetChunkIndex[i] = -1;
                            worker_state[i] = STATE_IDLE;
                            worker_isDeferredMeshWake[i] = false;
                            worker_skipCheckCounter[i] = 0;
                        }
                    }
                }
            }
        }

#if LOGGING
        if (enableDetailedTimings || enableAggregateLogging)
        {
            time_UpdateWorkers += (float)(System.DateTime.UtcNow - stepStart).TotalMilliseconds;
            stepStart = System.DateTime.UtcNow;
        }
#endif

        // GENERATOR-SLOT WATCHDOG: release any generator slot marked busy that NO worker actually
        // owns. An orphaned busy slot permanently blocks _TryPickDataGenPosition's look-ahead window
        // (it only picks positions whose generator slot is free) -> data-gen can never be assigned
        // -> workers starve in mesh-wait -> the world stalls incomplete (observed: 0 STATE_DATA_GEN
        // workers, nextChunkIndexToAssign frozen, completed stuck). This self-heals so gen can never
        // wedge. Safe: a slot a worker is actively generating with is "owned" and never released here.
        if (genSlotBusy != null)
        {
            for (int s = 0; s < genSlotBusy.Length; s++)
            {
                if (!genSlotBusy[s]) continue;
                if (genSlotReleaseDelay != null && s < genSlotReleaseDelay.Length && genSlotReleaseDelay[s] > 0) continue; // debug hold
                bool owned = false;
                for (int w = 0; w < maxConcurrentWorkers; w++)
                {
                    if (worker_state[w] == STATE_DATA_GEN && worker_generatorSlot[w] == s) { owned = true; break; }
                }
                if (!owned) genSlotBusy[s] = false;
            }
        }

        // --- 2. Assign new work to idle workers ---
        // OPTIMIZED: Allow multiple workers to be assigned in one cycle
        int assignedThisCycle = 0;
        int rebuildAssignmentsThisCycle = 0;
        int deferredWakeAssignmentsThisCycle = 0;
        int chunkInstantiationsThisFrame = 0;
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            if (Time.realtimeSinceStartup - cycleStartTime > cycleBudget) break; // Don't exceed budget
            
            if (worker_state[i] != STATE_IDLE) continue;

            bool assigned = false;
            bool canInterleaveDeferredWake =
                deferredMeshQueue_count > 0
                && chunkRebuildQueue_count > 0
                && chunkRebuildQueue_count <= deferredMeshWakeQueueThreshold
                && deferredWakeAssignmentsThisCycle < deferredMeshWakeBurstPerCycle
                && rebuildAssignmentsThisCycle > deferredWakeAssignmentsThisCycle;

            if (canInterleaveDeferredWake)
            {
                assigned = _TryAssignDeferredMeshWake(i, chunks, chunksLen);
                if (assigned)
                {
                    assignedThisCycle++;
                    deferredWakeAssignmentsThisCycle++;
                }
            }

            // Priority 1: Process player-initiated rebuilds
            if (!assigned && chunkRebuildQueue_count > 0)
            {
                int chunkIndexToRebuild = chunkRebuildQueue[chunkRebuildQueue_head];
                chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE;
                chunkRebuildQueue_count--;

                if (chunkIndexToRebuild != -1)
                {
                    ChunkData rebuildChunk = chunks != null && chunkIndexToRebuild >= 0 && chunkIndexToRebuild < chunksLen ? chunks[chunkIndexToRebuild] : null;
                    if (rebuildChunk != null) rebuildChunk.isQueuedForMeshRebuild = false;
                    worker_targetChunkIndex[i] = chunkIndexToRebuild;
                    worker_state[i] = STATE_WAITING_FOR_MESH;
                    worker_isDeferredMeshWake[i] = rebuildChunk != null && rebuildChunk.isMeshDeferred;
                    if (rebuildChunk != null && rebuildChunk.isMeshDeferred)
                    {
                        rebuildChunk.isMeshDeferred = false;
                        rebuildChunk.pendingNeighborMeshRebuild = true;
                    }
                    assigned = true;
                    assignedThisCycle++;
                    rebuildAssignmentsThisCycle++;
#if LOGGING
                    if (enableDetailedTimings || enableAggregateLogging) rebuilds_Processed++;
#endif
                }
            }
            // Priority 1.5: Heal chunks with stale border data (bypass rebuild queue)
            // Only after all chunks have been assigned for generation.
            else if (!assigned && nextChunkIndexToAssign >= totalWorldChunks)
            {
                int scanLen = chunks != null ? chunksLen : 0;
                for (int s = 0; s < 64 && scanLen > 0; s++)
                {
                    borderHealWorkerCursor++;
                    if (borderHealWorkerCursor >= scanLen) borderHealWorkerCursor = 0;
                    ChunkData hc = chunks[borderHealWorkerCursor];
                    if (hc == null || !hc.isDataReady || hc.isBuildingMesh || hc.isMeshDeferred) continue;
                    if (hc._borderMissingMask == 0) continue;
                    byte hm = hc._borderMissingMask;
                    bool ready = true;
                    for (int d = 0; d < 6; d++)
                    {
                        if ((hm & (1 << d)) == 0) continue;
                        ChunkData nc = world.GetChunkAt(
                            hc.chunkX_world + _healDx[d],
                            hc.chunkY_world + _healDy[d],
                            hc.chunkZ_world + _healDz[d]);
                        if (nc == null || !nc.isDataReady) { ready = false; break; }
                    }
                    if (ready)
                    {
                        worker_targetChunkIndex[i] = borderHealWorkerCursor;
                        worker_state[i] = STATE_WAITING_FOR_MESH;
                        assigned = true;
                        assignedThisCycle++;
                        break;
                    }
                }

                // CRITICAL: once worldgen assignment is complete this border-heal branch is an
                // else-if that captures every idle worker, so the chain never reaches Priority 3
                // (deferred-mesh wake) below — leaving the deferred queue stranded at its cap and
                // the world permanently incomplete. Drain it here when heal found nothing. (During
                // load this branch isn't entered, so Priority 3 still drains the queue normally.)
                if (!assigned && deferredMeshQueue_count > 0)
                {
                    assigned = _TryAssignDeferredMeshWake(i, chunks, chunksLen);
                    if (assigned) assignedThisCycle++;
                }
            }
            // Priority 2: Initial world generation
            // Old limit was 1 instantiation per frame — that capped worldgen at framerate.
            // With column caching (radial order visits all Y per (x,z) consecutively), the
            // first chunk in a column waits for the GPU readback (~80ms), then 7 more
            // sibling chunks can all be allocated to free workers immediately. Allowing
            // a burst here removes the gap between readback completion and chunks actually
            // entering the data-gen state.
            else if (chunkInstantiationsThisFrame < maxChunkInstantiationsPerCycle && nextChunkIndexToAssign < totalWorldChunks && _TryPickDataGenPosition())
            {
                int pickedPos = _lastPickedDataGenPos;
                _positionAssigned[pickedPos] = true;
                int chunk1DIndex = radialChunkOrder[pickedPos];
                // Advance the low-water-mark past any now-consumed leading positions.
                while (nextChunkIndexToAssign < totalWorldChunks && _positionAssigned[nextChunkIndexToAssign]) nextChunkIndexToAssign++;

                world.Chunk1DToArrrayCoords(chunk1DIndex, out int array_cx, out int array_cy, out int array_cz);

                int newChunkIndex = world.InstantiateAndConfigureChunk(array_cx, array_cy, array_cz);
                chunkInstantiationsThisFrame++;

                if (newChunkIndex != -1)
                {
                    worker_targetChunkIndex[i] = newChunkIndex;
                    worker_state[i] = STATE_DATA_GEN;
                    worker_usesExclusiveGenerator[i] = world.StartChunkDataGeneration(newChunkIndex);
                    worker_isDeferredMeshWake[i] = false;
                    if (worker_usesExclusiveGenerator[i])
                    {
                        int genSlot = world.GeneratorSlotForChunkIndex(newChunkIndex);
                        worker_generatorSlot[i] = genSlot;
                        if (genSlot >= 0 && genSlot < genSlotBusy.Length) genSlotBusy[genSlot] = true;
                    }
                    assigned = true;
                    assignedThisCycle++;
#if LOGGING
                    if (enableDetailedTimings || enableAggregateLogging) worldChunks_Assigned++;
#endif

                    // Cached lower-Y GPU slices can complete immediately without holding the
                    // exclusive generator. Collapse that handoff here so workers are freed
                    // in the same coordinator cycle when possible.
                    if (!worker_usesExclusiveGenerator[i])
                    {
                        ChunkData assignedChunk = chunks != null && newChunkIndex >= 0 && newChunkIndex < chunksLen ? chunks[newChunkIndex] : null;
                        if (assignedChunk != null && assignedChunk.isGeneratingData)
                        {
                            world.StepChunkDataGeneration(assignedChunk);

                            if (!assignedChunk.isGeneratingData)
                            {
                                if (world.UsesGpuLightingBackend() && !world.RequiresCpuLightingForAmbientOcclusion())
                                {
                                    worker_state[i] = STATE_WAITING_FOR_MESH;
                                    worker_skipCheckCounter[i] = 0;
                                    world.HandleChunkPostDataGpuLighting(assignedChunk);

                                    if (world.AreAllNeighborsReady(newChunkIndex) && world.ShouldDeferChunkMesh(newChunkIndex))
                                    {
                                        world.MarkChunkMeshDeferred(newChunkIndex);
                                        worker_targetChunkIndex[i] = -1;
                                        worker_state[i] = STATE_IDLE;
                                        worker_skipCheckCounter[i] = 0;

                                        if (chunksCompletedCount < totalWorldChunks)
                                        {
                                            chunksCompletedCount++;
                                        }
#if LOGGING
                                        if (enableDetailedTimings || enableAggregateLogging) workers_MeshDeferred++;
#endif
                                    }
                                }
                                else
                                {
                                    worker_state[i] = STATE_LIGHTING;
                                    worker_skipCheckCounter[i] = 0;
                                    world.StartChunkLighting(newChunkIndex);
                                }

#if LOGGING
                                if (enableDetailedTimings || enableAggregateLogging) workers_DataGenCompleted++;
#endif
                            }
                        }
                    }
                }
                else
                {
                    chunksCompletedCount++; // already existed
                }
            }
            // Priority 3: Wake deferred interior meshes on their own queue
            else if (!assigned && deferredMeshQueue_count > 0)
            {
                assigned = _TryAssignDeferredMeshWake(i, chunks, chunksLen);
                if (assigned)
                {
                    assignedThisCycle++;
                }
            }
            
            // Keep filling idle workers until the frame budget says stop.
            if (assignedThisCycle >= maxConcurrentWorkers) break;
        }

#if LOGGING
        if (enableDetailedTimings || enableAggregateLogging)
        {
            time_AssignWork += (float)(System.DateTime.UtcNow - stepStart).TotalMilliseconds;
        }
#endif
        
        // --- 3. Logging & Perpetual Scheduling ---
#if LOGGING
        // Log progress during initial generation
        if (chunksCompletedCount < totalWorldChunks)
        {
            int currentPercent = (chunksCompletedCount * 100) / totalWorldChunks;
            if (currentPercent / 10 > lastLoggedPercent / 10) {
                if (enableVerboseLogging) Debug.Log($"[McCoordinator] World Processing: ~{currentPercent}% complete ({chunksCompletedCount}/{totalWorldChunks} chunks finalized).");
                lastLoggedPercent = currentPercent;
            }
        }
        else
        {
            // Log completion of the initial build just once
            if (lastLoggedPercent != 100)
            {
                lastLoggedPercent = 100;
                if (enableVerboseLogging) Debug.Log($"[McCoordinator] Initial world generation complete. Now processing player edits.");
            }
        }
#endif

#if LOGGING
        if (enableDetailedTimings || enableAggregateLogging)
        {
            time_TotalCycle += (float)(System.DateTime.UtcNow - cycleStart).TotalMilliseconds;
            cycles_Processed++;

            int activeWorkers = 0;
            int dataGenWorkers = 0;
            int meshingWorkers = 0;
            for (int i = 0; i < maxConcurrentWorkers; i++)
            {
                if (worker_state[i] != STATE_IDLE) activeWorkers++;
                if (worker_state[i] == STATE_DATA_GEN) dataGenWorkers++;
                if (worker_state[i] == STATE_MESHING) meshingWorkers++;
            }

            if (activeWorkers > peak_ActiveWorkers) peak_ActiveWorkers = activeWorkers;
            if (dataGenWorkers > peak_DataGenWorkers) peak_DataGenWorkers = dataGenWorkers;
            if (meshingWorkers > peak_MeshingWorkers) peak_MeshingWorkers = meshingWorkers;
            if (chunkRebuildQueue_count > peak_RebuildQueue) peak_RebuildQueue = chunkRebuildQueue_count;
            if (deferredMeshQueue_count > peak_DeferredMeshQueue) peak_DeferredMeshQueue = deferredMeshQueue_count;
        }
#endif

        // No longer need to reschedule - Update() runs every frame automatically!
    }

#if LOGGING
    public void AppendAggregatePerformanceStats(StringBuilder sb)
    {
        if (sb == null || (!enableDetailedTimings && !enableAggregateLogging) || cycles_Processed <= 0) return;

        float avgCycle = time_TotalCycle / cycles_Processed;
        float avgUpdate = time_UpdateWorkers / cycles_Processed;
        float avgAssign = time_AssignWork / cycles_Processed;

        sb.AppendLine("Coordinator:");
        sb.AppendFormat("  Cycles: {0}, Avg cycle {1:F3}ms, update workers {2:F3}ms, assign work {3:F3}ms\n",
            cycles_Processed, avgCycle, avgUpdate, avgAssign);
        sb.AppendFormat("  Worker completions: data {0}, mesh {1}, deferred {2}, rebuilds {3}, deferred wakes {4}, world assigns {5}\n",
            workers_DataGenCompleted, workers_MeshCompleted, workers_MeshDeferred, rebuilds_Processed, deferredMeshWakeAssignments, worldChunks_Assigned);
        sb.AppendFormat("  Peaks: active {0}/{1}, data {2}, meshing {3}, rebuild queue {4}/{5}, deferred queue {6}/{7}\n",
            peak_ActiveWorkers, maxConcurrentWorkers, peak_DataGenWorkers, peak_MeshingWorkers, peak_RebuildQueue, MAX_REBUILD_QUEUE_SIZE, peak_DeferredMeshQueue, MAX_DEFERRED_MESH_QUEUE_SIZE);
        sb.AppendFormat("  Progress: {0}/{1} chunks ({2:F1}%)\n",
            chunksCompletedCount, totalWorldChunks,
            totalWorldChunks > 0 ? (chunksCompletedCount * 100f / totalWorldChunks) : 0f);
    }

    public void ResetAggregatePerformanceStats()
    {
        time_UpdateWorkers = 0f;
        time_AssignWork = 0f;
        time_TotalCycle = 0f;
        cycles_Processed = 0;
        workers_DataGenCompleted = 0;
        workers_MeshCompleted = 0;
        workers_MeshDeferred = 0;
        rebuilds_Processed = 0;
        deferredMeshWakeAssignments = 0;
        worldChunks_Assigned = 0;
        peak_ActiveWorkers = 0;
        peak_DataGenWorkers = 0;
        peak_MeshingWorkers = 0;
        peak_RebuildQueue = 0;
        peak_DeferredMeshQueue = 0;
    }
#endif

    private bool AreAnyWorkersActive()
    {
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            if (worker_state[i] != STATE_IDLE) return true;
        }
        return false;
    }

    public bool IsWorldGenComplete()
    {
        return chunksCompletedCount >= totalWorldChunks && totalWorldChunks > 0;
    }

    /// <summary>
    /// Queues a chunk for a mesh rebuild. The perpetual ProcessWorkers loop will pick this up.
    /// OPTIMIZED: Reduced duplicate checking overhead
    /// </summary>
    public void RequestChunkMeshUpdate(int chunkIndexToUpdate)
    {
        if (chunkIndexToUpdate == -1) return;

        ChunkData[] chunks = world != null ? world.chunks_1D : null;
        ChunkData chunk = chunks != null && chunkIndexToUpdate >= 0 && chunkIndexToUpdate < chunks.Length ? chunks[chunkIndexToUpdate] : null;
        if (chunk == null) return;
        bool interactionPriority = chunk != null && chunk.interactionMeshPriority;
        if (chunk.isBuildingMesh)
        {
            chunk.pendingChunkMeshRebuild = true;
            return;
        }
        if (chunk.isQueuedForMeshRebuild) return;

        if (chunkRebuildQueue_count >= MAX_REBUILD_QUEUE_SIZE) return;
        
        // Quick check: Avoid adding if it's already being processed by a worker
        // Only check first few workers since most of the time only 1-2 are active
        int checkLimit = maxConcurrentWorkers < 4 ? maxConcurrentWorkers : 4;
        for(int i = 0; i < checkLimit; i++)
        {
            if (worker_targetChunkIndex[i] == chunkIndexToUpdate) return;
        }

        if (interactionPriority)
        {
            chunkRebuildQueue_head = (chunkRebuildQueue_head - 1 + MAX_REBUILD_QUEUE_SIZE) % MAX_REBUILD_QUEUE_SIZE;
            chunkRebuildQueue[chunkRebuildQueue_head] = chunkIndexToUpdate;
        }
        else
        {
            chunkRebuildQueue[chunkRebuildQueue_tail] = chunkIndexToUpdate;
            chunkRebuildQueue_tail = (chunkRebuildQueue_tail + 1) % MAX_REBUILD_QUEUE_SIZE;
        }
        chunk.isQueuedForMeshRebuild = true;
        chunkRebuildQueue_count++;
    }

    public bool RequestDeferredChunkMeshUpdate(int chunkIndexToUpdate)
    {
        if (chunkIndexToUpdate == -1 || deferredMeshQueue_count >= MAX_DEFERRED_MESH_QUEUE_SIZE) return false;

        ChunkData[] chunks = world != null ? world.chunks_1D : null;
        ChunkData chunk = chunks != null && chunkIndexToUpdate >= 0 && chunkIndexToUpdate < chunks.Length ? chunks[chunkIndexToUpdate] : null;
        if (chunk == null || !chunk.isMeshDeferred || chunk.isBuildingMesh) return false;

        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            if (worker_targetChunkIndex[i] == chunkIndexToUpdate) return false;
        }

        for (int i = 0; i < chunkRebuildQueue_count; i++)
        {
            int queueIndex = (chunkRebuildQueue_head + i) % MAX_REBUILD_QUEUE_SIZE;
            if (chunkRebuildQueue[queueIndex] == chunkIndexToUpdate) return false;
        }

        for (int i = 0; i < deferredMeshQueue_count; i++)
        {
            int queueIndex = (deferredMeshQueue_head + i) % MAX_DEFERRED_MESH_QUEUE_SIZE;
            if (deferredMeshQueue[queueIndex] == chunkIndexToUpdate) return false;
        }

        deferredMeshQueue[deferredMeshQueue_tail] = chunkIndexToUpdate;
        deferredMeshQueue_tail = (deferredMeshQueue_tail + 1) % MAX_DEFERRED_MESH_QUEUE_SIZE;
        deferredMeshQueue_count++;
        return true;
    }

    private bool _TryAssignDeferredMeshWake(int workerIndex, ChunkData[] chunks, int chunksLen)
    {
        int popLimit = deferredMeshQueue_count < 8 ? deferredMeshQueue_count : 8;
        while (popLimit-- > 0 && deferredMeshQueue_count > 0)
        {
            int deferredChunkIndex = deferredMeshQueue[deferredMeshQueue_head];
            deferredMeshQueue_head = (deferredMeshQueue_head + 1) % MAX_DEFERRED_MESH_QUEUE_SIZE;
            deferredMeshQueue_count--;

            if (deferredChunkIndex == -1 || deferredChunkIndex >= chunksLen) continue;

            ChunkData deferredChunk = chunks[deferredChunkIndex];
            if (deferredChunk == null || !deferredChunk.isDataReady || deferredChunk.isBuildingMesh || !deferredChunk.isMeshDeferred) continue;

            deferredChunk.isMeshDeferred = false;
            deferredChunk.pendingNeighborMeshRebuild = true;
            worker_targetChunkIndex[workerIndex] = deferredChunkIndex;
            worker_state[workerIndex] = STATE_WAITING_FOR_MESH;
            worker_isDeferredMeshWake[workerIndex] = true;
#if LOGGING
            if (enableDetailedTimings || enableAggregateLogging) deferredMeshWakeAssignments++;
#endif
            return true;
        }

        return false;
    }

    public bool IsChunkScheduledForMeshUpdate(int chunkIndexToCheck)
    {
        if (chunkIndexToCheck == -1) return false;

        ChunkData[] chunks = world != null ? world.chunks_1D : null;
        ChunkData chunk = chunks != null && chunkIndexToCheck >= 0 && chunkIndexToCheck < chunks.Length ? chunks[chunkIndexToCheck] : null;
        if (chunk != null && chunk.isQueuedForMeshRebuild) return true;

        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            if (worker_targetChunkIndex[i] == chunkIndexToCheck) return true;
        }

        for (int i = 0; i < chunkRebuildQueue_count; i++)
        {
            int queueIndex = (chunkRebuildQueue_head + i) % MAX_REBUILD_QUEUE_SIZE;
            if (chunkRebuildQueue[queueIndex] == chunkIndexToCheck) return true;
        }
        return false;
    }
}
