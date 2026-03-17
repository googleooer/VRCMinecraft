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
    public int skipCheckCycles = 0;

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
    private bool[] worker_usesExclusiveGenerator;
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
    private float benchmarkStartTime = 0f;
    
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
    private int workers_MeshDeferred;
    private int rebuilds_Processed;
    private int worldChunks_Assigned;
    private int peak_ActiveWorkers;
    private int peak_DataGenWorkers;
    private int peak_MeshingWorkers;
    private int peak_RebuildQueue;
#endif

    public void InitializeAndStartProcessing(McWorld worldInstance, int[] generatedRadialOrder, int worldTotalChunks)
    {
        this.world = worldInstance;
        this.radialChunkOrder = generatedRadialOrder;
        this.totalWorldChunks = worldTotalChunks;
        this.nextChunkIndexToAssign = 0;
        this.chunksCompletedCount = 0;
        this.benchmarkStartTime = Time.realtimeSinceStartup;
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
        worker_skipCheckCounter = new int[maxConcurrentWorkers];
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            worker_targetChunkIndex[i] = -1; // -1 indicates no chunk
            worker_state[i] = STATE_IDLE;
            worker_usesExclusiveGenerator[i] = false;
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
                worker_usesExclusiveGenerator[i] = false;
                worker_skipCheckCounter[i] = 0;
                continue;
            }

            // Direct chunk access (already validated index)
            ChunkData chunk = chunks[chunkIndex];
            if (chunk == null)
            {
                worker_state[i] = STATE_IDLE;
                worker_targetChunkIndex[i] = -1;
                worker_usesExclusiveGenerator[i] = false;
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
                            isGeneratorBusy = false;
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
                        // Check if neighbors are ready (expensive, but only when needed)
                        if (world.AreAllNeighborsReady(chunkIndex))
                        {
                            if (world.ShouldDeferChunkMesh(chunkIndex))
                            {
                                world.MarkChunkMeshDeferred(chunkIndex);
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
                                continue;
                            }
                            if (!world.HasAvailableGpuMeshReadbackSlot())
                            {
                                continue;
                            }
                            world.BuildChunkMesh(chunkIndex); 
                            worker_state[i] = STATE_MESHING;
                            worker_skipCheckCounter[i] = 0;
                            // Don't recheck — meshing takes multiple frames
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
                    if (enableDetailedTimings || enableAggregateLogging) rebuilds_Processed++;
#endif
                }
            }
            // Priority 2: Initial world generation (only one data gen at a time)
            else if (nextChunkIndexToAssign < totalWorldChunks && (!isGeneratorBusy || world.CanStartChunkDataGenerationWithoutExclusiveGenerator(radialChunkOrder[nextChunkIndexToAssign])))
            {
                int chunk1DIndex = radialChunkOrder[nextChunkIndexToAssign];
                nextChunkIndexToAssign++;

                world.Chunk1DToArrrayCoords(chunk1DIndex, out int array_cx, out int array_cy, out int array_cz);

                int newChunkIndex = world.InstantiateAndConfigureChunk(array_cx, array_cy, array_cz);

                if (newChunkIndex != -1)
                {
                    worker_targetChunkIndex[i] = newChunkIndex;
                    worker_state[i] = STATE_DATA_GEN;
                    worker_usesExclusiveGenerator[i] = world.StartChunkDataGeneration(newChunkIndex);
                    if (worker_usesExclusiveGenerator[i]) isGeneratorBusy = true;
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
        sb.AppendFormat("  Worker completions: data {0}, mesh {1}, deferred {2}, rebuilds {3}, world assigns {4}\n",
            workers_DataGenCompleted, workers_MeshCompleted, workers_MeshDeferred, rebuilds_Processed, worldChunks_Assigned);
        sb.AppendFormat("  Peaks: active {0}/{1}, data {2}, meshing {3}, rebuild queue {4}/{5}\n",
            peak_ActiveWorkers, maxConcurrentWorkers, peak_DataGenWorkers, peak_MeshingWorkers, peak_RebuildQueue, MAX_REBUILD_QUEUE_SIZE);
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
        worldChunks_Assigned = 0;
        peak_ActiveWorkers = 0;
        peak_DataGenWorkers = 0;
        peak_MeshingWorkers = 0;
        peak_RebuildQueue = 0;
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
