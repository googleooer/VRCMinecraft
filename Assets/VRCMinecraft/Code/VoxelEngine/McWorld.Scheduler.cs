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
