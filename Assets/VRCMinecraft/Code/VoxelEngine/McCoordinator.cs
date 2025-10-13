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
    [Tooltip("How many chunks can be processed (data-gen or meshing) concurrently.")]
    public int maxConcurrentWorkers = 4;
    [Tooltip("Time budget per Update() call in milliseconds to prevent frame drops.")]
    public float updateTimeBudgetMs = 8.0f;
    [Tooltip("Skip N state checks per worker to reduce overhead (higher = less responsive but faster)")]
    public int skipCheckCycles = 1;

    [Header("Workload Per Step")]
    [Tooltip("How many Z-columns of voxels to generate per step inside a chunk. Higher values generate chunks faster but may cause lag spikes.")]
    public int columnsPerDataGenStep = 2;
    [Tooltip("How many voxels to check for meshing per step inside a chunk. Higher values build meshes faster but may cause lag spikes.")]
    public int voxelsPerMeshStep = 1024;
    [Tooltip("How many voxels to process when generating data.")]
    public int voxelsPerTerrainStep = 1024;


    // --- Worker Pool State ---
    private int[] worker_targetChunkIndex;
    private int[] worker_state;
    private int[] worker_skipCheckCounter; // Skip state checks for N cycles to reduce overhead
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
    private bool isGeneratorBusy = false;
    
    // --- Player-Initiated Rebuild Queue ---
    private int[] chunkRebuildQueue;
    private int chunkRebuildQueue_head = 0;
    private int chunkRebuildQueue_tail = 0;
    private int chunkRebuildQueue_count = 0;
    private const int MAX_REBUILD_QUEUE_SIZE = 256;

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
    private int rebuilds_Processed;
    private int worldChunks_Assigned;
#endif

    public void InitializeAndStartProcessing(McWorld worldInstance, int[] generatedRadialOrder, int worldTotalChunks)
    {
        this.world = worldInstance;
        this.radialChunkOrder = generatedRadialOrder;
        this.totalWorldChunks = worldTotalChunks;
        this.nextChunkIndexToAssign = 0;
        this.chunksCompletedCount = 0;
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
        worker_skipCheckCounter = new int[maxConcurrentWorkers];
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            worker_targetChunkIndex[i] = -1; // -1 indicates no chunk
            worker_state[i] = STATE_IDLE;
            worker_skipCheckCounter[i] = 0;
        }

        chunkRebuildQueue = new int[MAX_REBUILD_QUEUE_SIZE];

        // No longer using SendCustomEventDelayedSeconds - Update() will handle processing
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
        float cycleBudget = updateTimeBudgetMs * 0.001f;

#if LOGGING
        System.DateTime cycleStart = System.DateTime.MinValue;
        System.DateTime stepStart = System.DateTime.MinValue;
        if (enableDetailedTimings)
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
                worker_skipCheckCounter[i] = 0;
                continue;
            }

            // OPTIMIZATION: Skip state checks for a few cycles to reduce overhead
            int skipCount = worker_skipCheckCounter[i];
            if (skipCount > 0)
            {
                worker_skipCheckCounter[i] = skipCount - 1;
                continue;
            }

            // Direct chunk access (already validated index)
            ChunkData chunk = chunks[chunkIndex];
            if (chunk == null)
            {
                worker_state[i] = STATE_IDLE;
                worker_targetChunkIndex[i] = -1;
                continue;
            }

            // OPTIMIZATION: Use if-else instead of switch for better performance in UdonSharp
            if (state == STATE_DATA_GEN)
            {
                // Check if data generation is complete
                if (!chunk.isGeneratingData)
                {
                    // Data generation is complete, move to lighting
                    worker_state[i] = STATE_LIGHTING;
                    worker_skipCheckCounter[i] = 0; // Start lighting immediately
                    isGeneratorBusy = false;
                    
                    // FIXED: Start incremental lighting processing
                    world.StartChunkLighting(chunkIndex);
#if LOGGING
                    if (enableDetailedTimings) workers_DataGenCompleted++;
#endif
                }
                else
                {
                    // Data gen takes multiple cycles, skip checking for a bit
                    worker_skipCheckCounter[i] = skipCheckCycles;
                }
            }
            else if (state == STATE_LIGHTING)
            {
                // FIXED: Step through lighting incrementally
                world.StepChunkLighting(chunkIndex);
                
                // Check if lighting is complete
                if (!chunk.isProcessingLighting)
                {
                    // Lighting is complete, move to waiting for mesh
                    worker_state[i] = STATE_WAITING_FOR_MESH;
                    worker_skipCheckCounter[i] = 0; // Check immediately for neighbors
                }
                else
                {
                    // Continue processing lighting (don't skip, process every frame)
                    worker_skipCheckCounter[i] = 0;
                }
            }
            else if (state == STATE_WAITING_FOR_MESH)
            {
                // OPTIMIZATION: Only check if not already building mesh
                if (!chunk.isBuildingMesh)
                {
                    // Check if neighbors are ready (expensive, but only when needed)
                    if (world.AreAllNeighborsReady(chunkIndex))
                    {
                        world.BuildChunkMesh(chunkIndex); 
                        worker_state[i] = STATE_MESHING;
                        worker_skipCheckCounter[i] = 1; // Skip 1 cycle for mesh to start
                    }
                    else
                    {
                        // Neighbors not ready, check again in a few cycles
                        worker_skipCheckCounter[i] = skipCheckCycles * 2; // Wait longer for neighbors
                    }
                }
            }
            else if (state == STATE_MESHING)
            {
                // Check if mesh building is complete
                if (!chunk.isBuildingMesh)
                {
                    // Mesh building is complete
                    worker_targetChunkIndex[i] = -1;
                    worker_state[i] = STATE_IDLE;
                    worker_skipCheckCounter[i] = 0;
                    
                    if(chunksCompletedCount < totalWorldChunks)
                    {
                       chunksCompletedCount++;
                    }
#if LOGGING
                    if (enableDetailedTimings) workers_MeshCompleted++;
#endif
                }
                else
                {
                    // Meshing takes multiple cycles, skip checking for a bit
                    worker_skipCheckCounter[i] = skipCheckCycles;
                }
            }
        }

#if LOGGING
        if (enableDetailedTimings)
        {
            time_UpdateWorkers += (float)(System.DateTime.UtcNow - stepStart).TotalMilliseconds;
            stepStart = System.DateTime.UtcNow;
        }
#endif

        // --- 2. Assign new work to idle workers ---
        // OPTIMIZED: Allow multiple workers to be assigned in one cycle
        int assignedThisCycle = 0;
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            if (Time.realtimeSinceStartup - cycleStartTime > cycleBudget) break; // Don't exceed budget
            
            if (worker_state[i] != STATE_IDLE) continue;

            bool assigned = false;

            // Priority 1: Process player-initiated rebuilds
            if (chunkRebuildQueue_count > 0)
            {
                int chunkIndexToRebuild = chunkRebuildQueue[chunkRebuildQueue_head];
                chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE;
                chunkRebuildQueue_count--;

                if (chunkIndexToRebuild != -1)
                {
                    worker_targetChunkIndex[i] = chunkIndexToRebuild;
                    worker_state[i] = STATE_WAITING_FOR_MESH;
                    assigned = true;
                    assignedThisCycle++;
#if LOGGING
                    if (enableDetailedTimings) rebuilds_Processed++;
#endif
                }
            }
            // Priority 2: Initial world generation (only one data gen at a time)
            else if (nextChunkIndexToAssign < totalWorldChunks && !isGeneratorBusy)
            {
                int chunk1DIndex = radialChunkOrder[nextChunkIndexToAssign];
                nextChunkIndexToAssign++;

                world.Chunk1DToArrrayCoords(chunk1DIndex, out int array_cx, out int array_cy, out int array_cz);

                int newChunkIndex = world.InstantiateAndConfigureChunk(array_cx, array_cy, array_cz);

                if (newChunkIndex != -1)
                {
                    worker_targetChunkIndex[i] = newChunkIndex;
                    worker_state[i] = STATE_DATA_GEN;
                    world.StartChunkDataGeneration(newChunkIndex);
                    isGeneratorBusy = true;
                    assigned = true;
                    assignedThisCycle++;
#if LOGGING
                    if (enableDetailedTimings) worldChunks_Assigned++;
#endif
                }
                else
                {
                    chunksCompletedCount++; // already existed
                }
            }
            
            // Stop if we've assigned enough work this cycle (prevent overwhelming the system)
            if (assignedThisCycle >= 2) break;
        }

#if LOGGING
        if (enableDetailedTimings)
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
        if (enableDetailedTimings)
        {
            time_TotalCycle += (float)(System.DateTime.UtcNow - cycleStart).TotalMilliseconds;
            cycles_Processed++;
            
            // Log detailed timing stats every 100 cycles
            if (cycles_Processed % 100 == 0)
            {
                float avgCycle = time_TotalCycle / cycles_Processed;
                float avgUpdate = time_UpdateWorkers / cycles_Processed;
                float avgAssign = time_AssignWork / cycles_Processed;
                
                System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
                sb.AppendLine("--- Coordinator Performance (Last 100 cycles) ---");
                sb.AppendFormat("Avg Cycle Time: {0:F3} ms\n", avgCycle);
                sb.AppendFormat("  1. Update Workers: {0:F3} ms\n", avgUpdate);
                sb.AppendFormat("  2. Assign Work: {0:F3} ms\n", avgAssign);
                sb.AppendLine("--- Worker Activity ---");
                sb.AppendFormat("  Data Gen Completed: {0}\n", workers_DataGenCompleted);
                sb.AppendFormat("  Mesh Completed: {0}\n", workers_MeshCompleted);
                sb.AppendFormat("  Rebuilds Processed: {0}\n", rebuilds_Processed);
                sb.AppendFormat("  World Chunks Assigned: {0}\n", worldChunks_Assigned);
                sb.AppendLine("--- Current State ---");
                
                int activeWorkers = 0;
                int dataGenWorkers = 0;
                int meshingWorkers = 0;
                for (int i = 0; i < maxConcurrentWorkers; i++)
                {
                    if (worker_state[i] != STATE_IDLE) activeWorkers++;
                    if (worker_state[i] == STATE_DATA_GEN) dataGenWorkers++;
                    if (worker_state[i] == STATE_MESHING) meshingWorkers++;
                }
                
                sb.AppendFormat("  Active Workers: {0}/{1}\n", activeWorkers, maxConcurrentWorkers);
                sb.AppendFormat("  Data Gen: {0}, Meshing: {1}\n", dataGenWorkers, meshingWorkers);
                sb.AppendFormat("  Rebuild Queue: {0}/{1}\n", chunkRebuildQueue_count, MAX_REBUILD_QUEUE_SIZE);
                sb.AppendFormat("  Progress: {0}/{1} chunks ({2:F1}%)\n", 
                    chunksCompletedCount, totalWorldChunks, 
                    totalWorldChunks > 0 ? (chunksCompletedCount * 100f / totalWorldChunks) : 0f);
                
                Debug.Log(sb.ToString());
                
                // Reset counters for next period
                time_UpdateWorkers = 0f;
                time_AssignWork = 0f;
                time_TotalCycle = 0f;
                cycles_Processed = 0;
                workers_DataGenCompleted = 0;
                workers_MeshCompleted = 0;
                rebuilds_Processed = 0;
                worldChunks_Assigned = 0;
            }
        }
#endif

        // No longer need to reschedule - Update() runs every frame automatically!
    }

    private bool AreAnyWorkersActive()
    {
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            if (worker_state[i] != STATE_IDLE) return true;
        }
        return false;
    }

    /// <summary>
    /// Queues a chunk for a mesh rebuild. The perpetual ProcessWorkers loop will pick this up.
    /// OPTIMIZED: Reduced duplicate checking overhead
    /// </summary>
    public void RequestChunkMeshUpdate(int chunkIndexToUpdate)
    {
        if (chunkIndexToUpdate == -1 || chunkRebuildQueue_count >= MAX_REBUILD_QUEUE_SIZE) return;
        
        // Quick check: Avoid adding if it's already being processed by a worker
        // Only check first few workers since most of the time only 1-2 are active
        int checkLimit = maxConcurrentWorkers < 4 ? maxConcurrentWorkers : 4;
        for(int i = 0; i < checkLimit; i++)
        {
            if (worker_targetChunkIndex[i] == chunkIndexToUpdate) return;
        }

        // OPTIMIZED: Only check last few items in queue (most likely duplicates are recent)
        int checkCount = chunkRebuildQueue_count < 8 ? chunkRebuildQueue_count : 8;
        for (int i = 0; i < checkCount; i++) {
            int index = (chunkRebuildQueue_tail - 1 - i + MAX_REBUILD_QUEUE_SIZE) % MAX_REBUILD_QUEUE_SIZE;
            if (chunkRebuildQueue[index] == chunkIndexToUpdate) return;
        }

        chunkRebuildQueue[chunkRebuildQueue_tail] = chunkIndexToUpdate;
        chunkRebuildQueue_tail = (chunkRebuildQueue_tail + 1) % MAX_REBUILD_QUEUE_SIZE;
        chunkRebuildQueue_count++;
    }
}