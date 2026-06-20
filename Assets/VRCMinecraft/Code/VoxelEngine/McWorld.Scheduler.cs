#define LOGGING

using UdonSharp;
using UnityEngine;

// Partial of McWorld holding the in-VM scheduler merged from McCoordinator.
// (Phase 1 of the McCoordinator merge — see docs/superpowers/plans/2026-06-19-voxel-rethink-p0-p1-merge.md)
//
// Task 2: worker-pool and scheduler STATE FIELDS only.
// No logic moved yet; McCoordinator still owns and drives the scheduler.
// updateTimeBudgetMs and loadPhaseUpdateBudgetMs are NOT redeclared here — McWorld already owns them.
public partial class McWorld
{
    // -------------------------------------------------------------------------
    // Worker-pool state constants
    // -------------------------------------------------------------------------
    private const int SCH_STATE_IDLE             = 0;
    private const int SCH_STATE_DATA_GEN         = 1;
    private const int SCH_STATE_LIGHTING         = 2;
    private const int SCH_STATE_WAITING_FOR_MESH = 3;
    private const int SCH_STATE_MESHING          = 4;
    private const int SCH_MESH_WATCHDOG_FRAMES   = 300;

    // -------------------------------------------------------------------------
    // Serialized tuning fields (mirrors McCoordinator inspector fields)
    // -------------------------------------------------------------------------
    [Header("Scheduler: Performance")]
    public int maxConcurrentWorkers = 16;
    public int maxConcurrentWorldgenColumns = 4;
    public bool reserveWorkersForDataGenDuringLoad = false;
    public int loadPhaseMeshWorkerCap = 8;
    public int debugGenSlotHoldFrames = 0;
    public int deferredMeshWakeQueueThreshold = 32;
    public int deferredMeshWakeBurstPerCycle = 1;
    public int maxChunkInstantiationsPerCycle = 16;
    public int dataGenLookaheadWindow = 96;

    // -------------------------------------------------------------------------
    // Worker-pool runtime arrays (allocated in scheduler init)
    // -------------------------------------------------------------------------
    private int[]  worker_targetChunkIndex;
    private int[]  worker_state;
    private bool[] worker_usesExclusiveGenerator;
    private bool[] worker_isDeferredMeshWake;
    private int[]  worker_skipCheckCounter;
    private int[]  worker_meshFrames;
    private int[]  worker_generatorSlot;

    // -------------------------------------------------------------------------
    // Generator-slot tracking
    // -------------------------------------------------------------------------
    private bool[] genSlotBusy;
    private int[]  genSlotReleaseDelay;

    // -------------------------------------------------------------------------
    // World-generation / picker state
    // -------------------------------------------------------------------------
    private int[]  radialChunkOrder;
    private int    nextChunkIndexToAssign  = 0;
    // totalWorldChunks: already declared in McWorld.cs (line 281) — reused, not redeclared.
    private int    chunksCompletedCount    = 0;
    private bool[] _positionAssigned;
    private int    _lastPickedDataGenPos   = -1;
    private int[]  _genSlotCache;

    // -------------------------------------------------------------------------
    // Initial-load plateau detection
    // -------------------------------------------------------------------------
    private bool  _initialBulkLoadDone          = false;
    private int   _lastProgressCompletedCount   = -1;
    private float _lastGenProgressTime          = -1f;

    // -------------------------------------------------------------------------
    // Rebuild / deferred-mesh queues
    // -------------------------------------------------------------------------
    private int[] chunkRebuildQueue;
    private int   chunkRebuildQueue_head  = 0;
    private int   chunkRebuildQueue_tail  = 0;
    private int   chunkRebuildQueue_count = 0;
    private const int MAX_REBUILD_QUEUE_SIZE = 256;

    private int[] deferredMeshQueue;
    private int   deferredMeshQueue_head  = 0;
    private int   deferredMeshQueue_tail  = 0;
    private int   deferredMeshQueue_count = 0;
    private const int MAX_DEFERRED_MESH_QUEUE_SIZE = 256;

    private int borderHealWorkerCursor = 0;
    private readonly int[] _healDx = {  1, -1, 0,  0, 0,  0 };
    private readonly int[] _healDy = {  0,  0, 1, -1, 0,  0 };
    private readonly int[] _healDz = {  0,  0, 0,  0, 1, -1 };

    // -------------------------------------------------------------------------
    // Benchmark / near-region tracking
    // -------------------------------------------------------------------------
    private float  benchmarkStartTime  = 0f;
    private bool[] _nearMeshCounted;
    private int    _nearMeshDone       = 0;
    private bool   _nearMeshLogged     = false;

    // =========================================================================
    // Picker methods (moved from McCoordinator — McCoordinator still drives;
    // these are dormant copies for compile verification. Task 5/6 will wire them.)
    // =========================================================================

    // Scans forward from the assignment low-water-mark for the first chunk that
    // can start data-gen RIGHT NOW: either its generator slot is free (a new
    // column) or its column cache is already populated (a sibling Y-chunk of an
    // in-flight/completed column). Returns true and sets _lastPickedDataGenPos on
    // success. Forward order guarantees a column's cached siblings (earlier
    // positions) are picked before any later column that would reuse the same
    // generator, so the single-column cache is never evicted out from under
    // un-drained siblings.
    private bool _TryPickDataGenPosition()
    {
#if LOGGING
        if (!sch_enableDetailedTimings && !sch_enableAggregateLogging) return _TryPickDataGenPositionImpl();
        System.DateTime _pickT = System.DateTime.UtcNow;
        bool _pickR = _TryPickDataGenPositionImpl();
        sch_time_AssignPick += (float)(System.DateTime.UtcNow - _pickT).TotalMilliseconds;
        sch_assign_PickCalls++;
        return _pickR;
#else
        return _TryPickDataGenPositionImpl();
#endif
    }

    // Memoized GeneratorSlotForChunkIndex — avoids a cross-VM call per scanned
    // position each cycle.
    private int _GenSlotForChunk(int ci)
    {
        if (_genSlotCache == null || ci < 0 || ci >= _genSlotCache.Length) return GeneratorSlotForChunkIndex(ci);
        int s = _genSlotCache[ci];
        if (s < 0) { s = GeneratorSlotForChunkIndex(ci); _genSlotCache[ci] = s; }
        return s;
    }

    private bool _TryPickDataGenPositionImpl()
    {
        if (radialChunkOrder == null || _positionAssigned == null) return false;
        // THROTTLE: how many NEW worldgen columns may be in flight concurrently. Each
        // new column holds a generator + an in-flight GPU base-readback; capping below
        // the generator count leaves GPU readback bandwidth for mesh face-readbacks
        // (which were being starved during load). Sibling chunks copied from an
        // already-generated column cache add NO readback, so they bypass the cap.
        bool canStartNewColumn = _BusyGeneratorCount() < maxConcurrentWorldgenColumns;
        int scanEnd = nextChunkIndexToAssign + dataGenLookaheadWindow;
        if (scanEnd > totalWorldChunks) scanEnd = totalWorldChunks;
        for (int p = nextChunkIndexToAssign; p < scanEnd; p++)
        {
            if (_positionAssigned[p]) continue;
            int ci = radialChunkOrder[p];
            if (!ShouldGenerateChunkData(ci)) continue; // DATA-GEN STREAMING: defer chunks beyond render distance (+margin)
            if (!genSlotBusy[_GenSlotForChunk(ci)])
            {
                if (canStartNewColumn) { _lastPickedDataGenPos = p; return true; } // new column (concurrency-capped)
            }
            else if (CanStartChunkDataGenerationWithoutExclusiveGenerator(ci))
            {
                _lastPickedDataGenPos = p; return true; // sibling from column cache — no new readback, bypasses the cap
            }
        }

        // FALLBACK (anti-stall): the windowed scan can come up empty when the next
        // dataGenLookaheadWindow positions are all already-assigned or map to the few
        // busy generator slots, while the pickable (unassigned + FREE-slot) positions
        // sit further ahead. Scan the whole remaining order for an unassigned position
        // whose generator slot is FREE.
        for (int p = scanEnd; p < totalWorldChunks; p++)
        {
            if (_positionAssigned[p]) continue;
            int ci = radialChunkOrder[p];
            if (!ShouldGenerateChunkData(ci)) continue; // DATA-GEN STREAMING: defer chunks beyond render distance (+margin)
            if (!genSlotBusy[_GenSlotForChunk(ci)] && canStartNewColumn)
            {
                _lastPickedDataGenPos = p;
                return true;
            }
        }
        return false;
    }

    // Number of generator slots currently mid-column (each = one in-flight GPU
    // base-readback).
    private int _BusyGeneratorCount()
    {
        if (genSlotBusy == null) return 0;
        int n = 0;
        for (int i = 0; i < genSlotBusy.Length; i++) if (genSlotBusy[i]) n++;
        return n;
    }

    // =========================================================================
    // Worker helper methods (pulled from McCoordinator — same rename rules)
    // =========================================================================

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

    // Count of workers currently in SCH_STATE_MESHING — used by the data-gen worker reservation
    // to cap concurrent meshing during the bulk load.
    private int _CountMeshingWorkers()
    {
        int c = 0;
        for (int i = 0; i < maxConcurrentWorkers; i++) if (worker_state[i] == SCH_STATE_MESHING) c++;
        return c;
    }

    // Count of workers currently in SCH_STATE_WAITING_FOR_MESH — used by the strict-neighbour mesh
    // gate to detect a potential deadlock (all workers waiting, none free to instantiate the
    // missing neighbour) and fall back to lenient (air-boundary) meshing to break it.
    private int _CountWaitingWorkers()
    {
        int c = 0;
        for (int i = 0; i < maxConcurrentWorkers; i++) if (worker_state[i] == SCH_STATE_WAITING_FOR_MESH) c++;
        return c;
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
            worker_state[workerIndex] = SCH_STATE_WAITING_FOR_MESH;
            worker_isDeferredMeshWake[workerIndex] = true;
#if LOGGING
            if (sch_enableDetailedTimings || sch_enableAggregateLogging) sch_deferredMeshWakeAssignments++;
#endif
            return true;
        }

        return false;
    }

    // =========================================================================
    // Assignment loop (ported from McCoordinator.Update, lines 707–930)
    // Assigns new work to idle workers: rebuilds (P1), border-heal (P1.5),
    // initial worldgen (P2), deferred-mesh wakes (P3).
    // =========================================================================
    private void _SchedulerAssignWork(float cycleStartTime, float cycleBudget)
    {
        ChunkData[] chunks = chunks_1D;
        int chunksLen = chunks != null ? chunks.Length : 0;

#if LOGGING
        System.DateTime _awStart = System.DateTime.MinValue;
        if (sch_enableDetailedTimings || sch_enableAggregateLogging) _awStart = System.DateTime.UtcNow;
#endif

        int assignedThisCycle = 0;
        int rebuildAssignmentsThisCycle = 0;
        int deferredWakeAssignmentsThisCycle = 0;
        int chunkInstantiationsThisFrame = 0;
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            if (Time.realtimeSinceStartup - cycleStartTime > cycleBudget) break; // Don't exceed budget

            if (worker_state[i] != SCH_STATE_IDLE) continue;

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
                    worker_state[i] = SCH_STATE_WAITING_FOR_MESH;
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
                    if (sch_enableDetailedTimings || sch_enableAggregateLogging) sch_rebuilds_Processed++;
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
                    // Udon VM quirk: never post-increment a field as expression — split read/write.
                    int healCursor = borderHealWorkerCursor;
                    borderHealWorkerCursor = healCursor + 1;
                    if (borderHealWorkerCursor >= scanLen) borderHealWorkerCursor = 0;
                    ChunkData hc = chunks[borderHealWorkerCursor];
                    if (hc == null || !hc.isDataReady || hc.isBuildingMesh || hc.isMeshDeferred) continue;
                    if (hc._borderMissingMask == 0) continue;
                    byte hm = hc._borderMissingMask;
                    bool ready = true;
                    for (int d = 0; d < 6; d++)
                    {
                        if ((hm & (1 << d)) == 0) continue;
                        ChunkData nc = GetChunkAt(
                            hc.chunkX_world + _healDx[d],
                            hc.chunkY_world + _healDy[d],
                            hc.chunkZ_world + _healDz[d]);
                        if (nc == null || !nc.isDataReady) { ready = false; break; }
                    }
                    if (ready)
                    {
                        worker_targetChunkIndex[i] = borderHealWorkerCursor;
                        worker_state[i] = SCH_STATE_WAITING_FOR_MESH;
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

                Chunk1DToArrrayCoords(chunk1DIndex, out int array_cx, out int array_cy, out int array_cz);

                int newChunkIndex = InstantiateAndConfigureChunk(array_cx, array_cy, array_cz);
                chunkInstantiationsThisFrame++;

                if (newChunkIndex != -1)
                {
                    worker_targetChunkIndex[i] = newChunkIndex;
                    worker_state[i] = SCH_STATE_DATA_GEN;
#if LOGGING
                    System.DateTime _startGenT = (sch_enableDetailedTimings || sch_enableAggregateLogging) ? System.DateTime.UtcNow : System.DateTime.MinValue;
#endif
                    worker_usesExclusiveGenerator[i] = StartChunkDataGeneration(newChunkIndex);
#if LOGGING
                    if (sch_enableDetailedTimings || sch_enableAggregateLogging) { sch_time_AssignStartGen += (float)(System.DateTime.UtcNow - _startGenT).TotalMilliseconds; sch_assign_NewColumns++; }
#endif
                    worker_isDeferredMeshWake[i] = false;
                    if (worker_usesExclusiveGenerator[i])
                    {
                        int genSlot = _GenSlotForChunk(newChunkIndex);
                        worker_generatorSlot[i] = genSlot;
                        if (genSlot >= 0 && genSlot < genSlotBusy.Length) genSlotBusy[genSlot] = true;
                    }
                    assigned = true;
                    assignedThisCycle++;
#if LOGGING
                    if (sch_enableDetailedTimings || sch_enableAggregateLogging) sch_worldChunks_Assigned++;
#endif

                    // Cached lower-Y GPU slices can complete immediately without holding the
                    // exclusive generator. Collapse that handoff here so workers are freed
                    // in the same coordinator cycle when possible.
                    if (!worker_usesExclusiveGenerator[i])
                    {
                        ChunkData assignedChunk = chunks != null && newChunkIndex >= 0 && newChunkIndex < chunksLen ? chunks[newChunkIndex] : null;
                        if (assignedChunk != null && assignedChunk.isGeneratingData)
                        {
#if LOGGING
                            System.DateTime _stepGenT = (sch_enableDetailedTimings || sch_enableAggregateLogging) ? System.DateTime.UtcNow : System.DateTime.MinValue;
#endif
                            StepChunkDataGeneration(assignedChunk);
#if LOGGING
                            if (sch_enableDetailedTimings || sch_enableAggregateLogging) { sch_time_AssignStepGen += (float)(System.DateTime.UtcNow - _stepGenT).TotalMilliseconds; sch_assign_SiblingSteps++; }
#endif

                            if (!assignedChunk.isGeneratingData)
                            {
                                if (UsesGpuLightingBackend() && !RequiresCpuLightingForAmbientOcclusion())
                                {
                                    worker_state[i] = SCH_STATE_WAITING_FOR_MESH;
                                    worker_skipCheckCounter[i] = 0;
                                    HandleChunkPostDataGpuLighting(assignedChunk);

                                    if (AreAllNeighborsReady(newChunkIndex) && ShouldDeferChunkMesh(newChunkIndex))
                                    {
                                        MarkChunkMeshDeferred(newChunkIndex);
                                        worker_targetChunkIndex[i] = -1;
                                        worker_state[i] = SCH_STATE_IDLE;
                                        worker_skipCheckCounter[i] = 0;

                                        if (chunksCompletedCount < totalWorldChunks)
                                        {
                                            chunksCompletedCount++;
                                        }
#if LOGGING
                                        if (sch_enableDetailedTimings || sch_enableAggregateLogging) sch_workers_MeshDeferred++;
#endif
                                    }
                                }
                                else
                                {
                                    worker_state[i] = SCH_STATE_LIGHTING;
                                    worker_skipCheckCounter[i] = 0;
                                    StartChunkLighting(newChunkIndex);
                                }

#if LOGGING
                                if (sch_enableDetailedTimings || sch_enableAggregateLogging) sch_workers_DataGenCompleted++;
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
        if (sch_enableDetailedTimings || sch_enableAggregateLogging)
        {
            sch_time_AssignWork += (float)(System.DateTime.UtcNow - _awStart).TotalMilliseconds;
        }
#endif
    }

    // =========================================================================
    // Worker state-machine loop (ported from McCoordinator.Update, lines 393–676)
    // Called by _RunSchedulerOnce (wired in Task 6). McCoordinator still drives.
    // =========================================================================
    private void _SchedulerUpdateWorkers(float cycleStartTime, float cycleBudget)
    {
        // Cache chunks_1D array locally to avoid repeated field access
        ChunkData[] chunks = chunks_1D;
        int chunksLen = chunks != null ? chunks.Length : 0;

#if LOGGING
        System.DateTime _swStart = System.DateTime.UtcNow;
#endif

        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            if (Time.realtimeSinceStartup - cycleStartTime > cycleBudget) break; // Don't exceed budget

            int state = worker_state[i];
            if (state == SCH_STATE_IDLE) continue;

            int chunkIndex = worker_targetChunkIndex[i];
            if (chunkIndex == -1 || chunkIndex >= chunksLen) {
                worker_state[i] = SCH_STATE_IDLE;
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
                worker_state[i] = SCH_STATE_IDLE;
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

                if (state == SCH_STATE_DATA_GEN)
                {
                    // OPTIMIZATION: Actively drive data gen forward instead of passively waiting.
                    // This eliminates the cross-Update handoff delay between McWorld and coordinator.
                    if (chunk.isGeneratingData)
                    {
                        StepChunkDataGeneration(chunk);
                    }

                    // Check if data generation is complete
                    if (!chunk.isGeneratingData)
                    {
                        if (worker_usesExclusiveGenerator[i])
                        {
                            _ReleaseWorkerGeneratorSlot(i);
                            worker_usesExclusiveGenerator[i] = false;
                        }

                        if (UsesGpuLightingBackend() && !RequiresCpuLightingForAmbientOcclusion())
                        {
                            worker_state[i] = SCH_STATE_WAITING_FOR_MESH;
                            worker_skipCheckCounter[i] = 0;
                            // Trigger neighbor re-meshing so already-meshed neighbors
                            // update their boundary faces with this chunk's data
                            HandleChunkPostDataGpuLighting(chunk);
                        }
                        else
                        {
                            // Data generation is complete, move to lighting
                            worker_state[i] = SCH_STATE_LIGHTING;
                            worker_skipCheckCounter[i] = 0; // Start lighting immediately
                            StartChunkLighting(chunkIndex);
                        }
                        recheck = true; // Re-evaluate the new state immediately
#if LOGGING
                        if (sch_enableDetailedTimings || sch_enableAggregateLogging) sch_workers_DataGenCompleted++;
#endif
                    }
                }
                else if (state == SCH_STATE_LIGHTING)
                {
                    if (UsesGpuLightingBackend() && !RequiresCpuLightingForAmbientOcclusion())
                    {
                        worker_state[i] = SCH_STATE_WAITING_FOR_MESH;
                        worker_skipCheckCounter[i] = 0;
                        recheck = true;
                        continue;
                    }

                    // FIXED: Step through lighting incrementally
                    StepChunkLighting(chunkIndex);

                    // Check if lighting is complete
                    if (!chunk.isProcessingLighting)
                    {
                        // Lighting is complete, move to waiting for mesh
                        worker_state[i] = SCH_STATE_WAITING_FOR_MESH;
                        worker_skipCheckCounter[i] = 0; // Check immediately for neighbors
                        recheck = true;
                    }
                }
                else if (state == SCH_STATE_WAITING_FOR_MESH)
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
                            ? AreAllNeighborsReadyLenient(chunkIndex)
                            : AreAllNeighborsReady(chunkIndex);
                        if (neighborsReadyForMesh)
                        {
                            // Only defer mesh builds during initial worldgen, not rebuilds.
                            // A chunk with _borderMissingMask != 0 needs re-meshing to fix
                            // boundary artifacts — re-deferring it loops forever.
                            // A chunk whose opaque mesh already has vertices was already
                            // meshed once; this is a rebuild request, not a first build.
                            bool isRebuild = chunk._borderMissingMask != 0
                                || (chunk.opaqueMeshFilter != null && chunk.opaqueMeshFilter.sharedMesh != null && chunk.opaqueMeshFilter.sharedMesh.vertexCount > 0);
                            if (!isRebuild && !worker_isDeferredMeshWake[i] && ShouldDeferChunkMesh(chunkIndex))
                            {
                                MarkChunkMeshDeferred(chunkIndex);
                                worker_targetChunkIndex[i] = -1;
                                worker_state[i] = SCH_STATE_IDLE;
                                worker_isDeferredMeshWake[i] = false;
                                worker_skipCheckCounter[i] = 0;

                                if (chunksCompletedCount < totalWorldChunks)
                                {
                                    chunksCompletedCount++;
                                }
#if LOGGING
                                if (sch_enableDetailedTimings || sch_enableAggregateLogging) sch_workers_MeshDeferred++;
#endif
                                continue;
                            }
                            // Data-gen worker reservation: during the bulk load, cap total mesh
                            // OCCUPANCY (actively meshing + parked waiting-for-mesh) so the rest of
                            // the pool stays free to feed the generators. The old check counted only
                            // SCH_STATE_MESHING, so workers piled up in SCH_STATE_WAITING_FOR_MESH (e.g. 6
                            // meshing + 10 waiting) and starved data-gen to ZERO -> the world hard-
                            // stalled incomplete. This chunk already passed the neighbour check, so
                            // deferring is fragmentation-safe (deferred scan + Priority 3 re-wake it).
                            // Player edits (interactionMeshPriority) bypass. Checked BEFORE the
                            // readback-slot gate so a capped worker frees instead of getting stuck.
                            if (reserveWorkersForDataGenDuringLoad && chunksCompletedCount < totalWorldChunks
                                && !chunk.interactionMeshPriority
                                && (_CountMeshingWorkers() + _CountWaitingWorkers()) > loadPhaseMeshWorkerCap)
                            {
                                MarkChunkMeshDeferred(chunkIndex);
                                worker_targetChunkIndex[i] = -1;
                                worker_state[i] = SCH_STATE_IDLE;
                                worker_isDeferredMeshWake[i] = false;
                                worker_skipCheckCounter[i] = 0;
                                if (chunksCompletedCount < totalWorldChunks) chunksCompletedCount++;
                                continue;
                            }
                            if (!HasAvailableGpuMeshReadbackSlot())
                            {
                                // No readback buffer free. Do NOT spin in SCH_STATE_WAITING_FOR_MESH —
                                // that parks the worker indefinitely and (with many such workers)
                                // starves data-gen. Free it; the deferred-mesh scan re-wakes the
                                // chunk once a slot frees. Keep isMeshDeferred so it isn't lost.
                                MarkChunkMeshDeferred(chunkIndex);
                                worker_targetChunkIndex[i] = -1;
                                worker_state[i] = SCH_STATE_IDLE;
                                worker_isDeferredMeshWake[i] = false;
                                worker_skipCheckCounter[i] = 0;
                                continue;
                            }
                            worker_isDeferredMeshWake[i] = false;
                            BuildChunkMesh(chunkIndex);
                            worker_state[i] = SCH_STATE_MESHING;
                            worker_skipCheckCounter[i] = 0;
                            // Don't recheck — meshing takes multiple frames
                        }
                        else
                        {
                            // Neighbours not ready (strict requireAllNeighborsForMesh). Do NOT hold
                            // the worker in SCH_STATE_WAITING_FOR_MESH — that starves data-gen of the
                            // very workers needed to GENERATE those neighbours (near-deadlock: all
                            // workers wait, nothing generates). Defer the chunk and free the worker;
                            // the deferred-mesh scan re-queues it once its neighbours are data-ready,
                            // so it meshes cleanly (boundary faces culled). Not counted complete here
                            // — it counts when it actually meshes.
                            MarkChunkMeshDeferred(chunkIndex);
                            worker_targetChunkIndex[i] = -1;
                            worker_state[i] = SCH_STATE_IDLE;
                            worker_isDeferredMeshWake[i] = false;
                            worker_skipCheckCounter[i] = 0;
                        }
                    }
                }
                else if (state == SCH_STATE_MESHING)
                {
                    // Check if mesh building is complete
                    if (!chunk.isBuildingMesh)
                    {
                        worker_meshFrames[i] = 0;
                        if (chunk.pendingChunkMeshRebuild)
                        {
                            chunk.pendingChunkMeshRebuild = false;
                            worker_state[i] = SCH_STATE_WAITING_FOR_MESH;
                            worker_isDeferredMeshWake[i] = false;
                            worker_skipCheckCounter[i] = 0;
                            recheck = true;
                            continue;
                        }

                        // Mesh building is complete
                        worker_targetChunkIndex[i] = -1;
                        worker_state[i] = SCH_STATE_IDLE;
                        worker_isDeferredMeshWake[i] = false;
                        worker_skipCheckCounter[i] = 0;

                        if (chunksCompletedCount < totalWorldChunks)
                        {
                            chunksCompletedCount++;
                        }

                        // NEAR-REGION RENDER benchmark: this chunk just had its render mesh built.
                        // Count it once if it's inside the near radius; log when the whole near
                        // region (all Y of every column within nearRegionRadius) is meshed.
                        if (!_nearMeshLogged && _nearMeshCounted != null && chunkIndex >= 0 && chunkIndex < _nearMeshCounted.Length
                            && !_nearMeshCounted[chunkIndex]
                            && chunk.chunkX_world >= -nearRegionRadius && chunk.chunkX_world <= nearRegionRadius
                            && chunk.chunkZ_world >= -nearRegionRadius && chunk.chunkZ_world <= nearRegionRadius)
                        {
                            _nearMeshCounted[chunkIndex] = true;
                            _nearMeshDone++;
                            int nearRenderTotal = (nearRegionRadius * 2 + 1) * (nearRegionRadius * 2 + 1) * worldDimensionY;
                            if (_nearMeshDone >= nearRenderTotal)
                            {
                                _nearMeshLogged = true;
                                float nearElapsed = Time.realtimeSinceStartup - benchmarkStartTime;
                                Debug.Log("[NEAR_RENDER] " + nearRegionRadius + "-radius region (" + nearRenderTotal + " chunks) GEN+RENDERED in " + nearElapsed.ToString("F2") + "s");
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
                        if (sch_enableDetailedTimings || sch_enableAggregateLogging) sch_workers_MeshCompleted++;
#endif
                        // Don't recheck — worker is now IDLE
                    }
                    else
                    {
                        // WATCHDOG: still building. A build that never completes must never wedge
                        // the worker (and the mesh-pool slot it may hold) forever — force-release
                        // after a frame cap so workers can never hang and the pool can't deadlock.
                        worker_meshFrames[i]++;
                        if (worker_meshFrames[i] > SCH_MESH_WATCHDOG_FRAMES)
                        {
                            int stuckIndex = worker_targetChunkIndex[i];
                            ForceCompleteStuckMesh(stuckIndex);
                            // CRITICAL: do NOT count this complete — the chunk has NO mesh. Re-queue
                            // it so it is never lost (was a permanent hole + miscount before). The
                            // deferred-mesh scan re-meshes it once budget frees up; with the meshing
                            // budget guarantee in ProcessActiveChunks this should rarely fire at all.
                            if (stuckIndex >= 0) MarkChunkMeshDeferred(stuckIndex);
                            worker_meshFrames[i] = 0;
                            worker_targetChunkIndex[i] = -1;
                            worker_state[i] = SCH_STATE_IDLE;
                            worker_isDeferredMeshWake[i] = false;
                            worker_skipCheckCounter[i] = 0;
                        }
                    }
                }
            }
        }

#if LOGGING
        if (sch_enableDetailedTimings || sch_enableAggregateLogging)
        {
            sch_time_UpdateWorkers += (float)(System.DateTime.UtcNow - _swStart).TotalMilliseconds;
        }
#endif
    }

    // =========================================================================
    // Generator-slot watchdog (ported from McCoordinator.Update, lines 692–705)
    // =========================================================================
    private void _SchedulerGenSlotWatchdog()
    {
        // GENERATOR-SLOT WATCHDOG: release any generator slot marked busy that NO worker actually
        // owns. An orphaned busy slot permanently blocks _TryPickDataGenPosition's look-ahead window
        // (it only picks positions whose generator slot is free) -> data-gen can never be assigned
        // -> workers starve in mesh-wait -> the world stalls incomplete (observed: 0 SCH_STATE_DATA_GEN
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
                    if (worker_state[w] == SCH_STATE_DATA_GEN && worker_generatorSlot[w] == s) { owned = true; break; }
                }
                if (!owned) genSlotBusy[s] = false;
            }
        }
    }

#if LOGGING
    // -------------------------------------------------------------------------
    // Scheduler aggregate-stat fields (#if LOGGING only)
    // -------------------------------------------------------------------------
    [Header("Scheduler: Debug")]
    public bool sch_enableVerboseLogging    = true;

    [Header("Scheduler: Performance Profiling")]
    public bool sch_enableDetailedTimings   = false;
    public bool sch_enableAggregateLogging  = true;

    private int   sch_lastLoggedPercent     = -1;

    // Detailed timing accumulators
    private float sch_time_UpdateWorkers;
    private float sch_time_AssignWork;
    private float sch_time_AssignPick;
    private float sch_time_AssignStartGen;
    private float sch_time_AssignStepGen;
    private int   sch_assign_PickCalls;
    private int   sch_assign_NewColumns;
    private int   sch_assign_SiblingSteps;
    private float sch_time_RebuildQueue;
    private float sch_time_WorldGen;
    private float sch_time_TotalCycle;
    private int   sch_cycles_Processed;
    private int   sch_workers_DataGenCompleted;
    private int   sch_workers_MeshCompleted;
    private int   sch_workers_MeshDeferred;
    private int   sch_rebuilds_Processed;
    private int   sch_deferredMeshWakeAssignments;
    private int   sch_worldChunks_Assigned;
    private int   sch_peak_ActiveWorkers;
    private int   sch_peak_DataGenWorkers;
    private int   sch_peak_MeshingWorkers;
    private int   sch_peak_RebuildQueue;
    private int   sch_peak_DeferredMeshQueue;
#endif
}
