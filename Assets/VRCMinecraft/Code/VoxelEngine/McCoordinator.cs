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
    [Tooltip("How often, in seconds, the coordinator checks on its workers.")]
    public float workerProcessingInterval = 0.05f;

    [Header("Workload Per Step")]
    [Tooltip("How many Z-columns of voxels to generate per step inside a chunk. Higher values generate chunks faster but may cause lag spikes.")]
    public int columnsPerDataGenStep = 2;
    [Tooltip("How many voxels to check for meshing per step inside a chunk. Higher values build meshes faster but may cause lag spikes.")]
    public int voxelsPerMeshStep = 1024;
    [Tooltip("How many voxels to process when generating data.")]
    public int voxelsPerTerrainStep = 1024;


    // --- Worker Pool State ---
    private McChunk[] worker_targetChunk;
    private int[] worker_state;
    private const int STATE_IDLE = 0;
    private const int STATE_DATA_GEN = 1;
    private const int STATE_WAITING_FOR_MESH = 2;
    private const int STATE_MESHING = 3;

    // --- World Generation State ---
    private int[] radialChunkOrder;
    private int nextChunkIndexToAssign = 0;
    private int totalWorldChunks;
    private int chunksCompletedCount = 0;
    private bool isGeneratorBusy = false;
    
    // --- Player-Initiated Rebuild Queue ---
    private McChunk[] chunkRebuildQueue;
    private int chunkRebuildQueue_head = 0;
    private int chunkRebuildQueue_tail = 0;
    private int chunkRebuildQueue_count = 0;
    private const int MAX_REBUILD_QUEUE_SIZE = 256;


    [Header("Debug")]
    #if UNITY_EDITOR
    public bool enableVerboseLogging = true;
    #endif
    private int lastLoggedPercent = -1;
    private StringBuilder logBuilder_McCoordinator;

    public void InitializeAndStartProcessing(McWorld worldInstance, int[] generatedRadialOrder, int worldTotalChunks)
    {
        this.world = worldInstance;
        this.radialChunkOrder = generatedRadialOrder;
        this.totalWorldChunks = worldTotalChunks;
        this.nextChunkIndexToAssign = 0;
        this.chunksCompletedCount = 0;
        this.lastLoggedPercent = -1;

        if (world == null)
        {
            Debug.LogError("[McCoordinator] McWorld instance is null! Aborting initialization.");
            this.enabled = false;
            return;
        }
        
        #if UNITY_EDITOR
        logBuilder_McCoordinator = new StringBuilder(512);
        #endif

        worker_targetChunk = new McChunk[maxConcurrentWorkers];
        worker_state = new int[maxConcurrentWorkers];
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            worker_state[i] = STATE_IDLE;
        }

        chunkRebuildQueue = new McChunk[MAX_REBUILD_QUEUE_SIZE];

        // Start the single, perpetual processing loop.
        SendCustomEventDelayedSeconds(nameof(ProcessWorkers), workerProcessingInterval);
    }
    
    /// <summary>
    /// The main, perpetual loop. It processes worker states and assigns new work from
    /// either the initial world generation list or the player-initiated rebuild queue.
    /// </summary>
    public void ProcessWorkers()
    {
        // --- 1. Update state of existing workers ---
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            if (worker_state[i] == STATE_IDLE) continue;

            McChunk chunk = worker_targetChunk[i];
            if (chunk == null) {
                worker_state[i] = STATE_IDLE;
                continue;
            }

            switch (worker_state[i])
            {
                case STATE_DATA_GEN:
                    // Check if chunk is generating data using the new time-sliced system
                    if (chunk.IsGeneratingData())
                    {
                        // Step the data generation
                        bool dataComplete = chunk.StepDataGeneration();
                        
                        if (dataComplete)
                        {
                            // Data generation is complete, move to waiting for mesh
                            worker_state[i] = STATE_WAITING_FOR_MESH;
                            isGeneratorBusy = false; // Generator is now free
                            
                            #if UNITY_EDITOR
                            if (enableVerboseLogging) {
                                Debug.Log($"[McCoordinator] Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) completed data generation");
                            }
                            #endif
                        }
                    }
                    else if (chunk.isDataReady)
                    {
                        // Fallback for chunks that complete instantly
                        worker_state[i] = STATE_WAITING_FOR_MESH;
                        isGeneratorBusy = false; // Generator is now free
                    }
                    break; 
                    
                case STATE_WAITING_FOR_MESH:
                    if (!chunk.isBuildingMesh && world.AreAllNeighborsReady(chunk))
                    {
                        chunk.BuildMesh(); 
                        worker_state[i] = STATE_MESHING;
                    }
                    break;

                case STATE_MESHING:
                    // The chunk handles its own time-sliced mesh building via BuildMeshStep()
                    // We just need to check if it's done
                    if (!chunk.isBuildingMesh)
                    {
                        // Mesh building is complete
                        worker_targetChunk[i] = null;
                        worker_state[i] = STATE_IDLE;
                        
                        // Only increment the initial build count if we were actually building the world
                        if(chunksCompletedCount < totalWorldChunks)
                        {
                           chunksCompletedCount++;
                        }
                        
                        #if UNITY_EDITOR
                        if (enableVerboseLogging) {
                            Debug.Log($"[McCoordinator] Chunk ({chunk.chunkX_world},{chunk.chunkY_world},{chunk.chunkZ_world}) completed mesh building");
                        }
                        #endif
                    }
                    // If still building, just let it continue on its own
                    break;
            }
        }

        // --- 2. Assign new work to idle workers ---
        for (int i = 0; i < maxConcurrentWorkers; i++)
        {
            if (worker_state[i] == STATE_IDLE)
            {
                // Priority 1: Process player-initiated rebuilds
                if (chunkRebuildQueue_count > 0)
                {
                    McChunk chunkToRebuild = chunkRebuildQueue[chunkRebuildQueue_head];
                    chunkRebuildQueue_head = (chunkRebuildQueue_head + 1) % MAX_REBUILD_QUEUE_SIZE;
                    chunkRebuildQueue_count--;

                    if (chunkToRebuild != null)
                    {
                        worker_targetChunk[i] = chunkToRebuild;
                        worker_state[i] = STATE_WAITING_FOR_MESH;
                    }
                    continue; 
                }
                
                // Priority 2: Process initial world generation
                if (nextChunkIndexToAssign < totalWorldChunks && !isGeneratorBusy)
                {
                    int chunk1DIndex = radialChunkOrder[nextChunkIndexToAssign];
                    nextChunkIndexToAssign++;

                    world.Chunk1DToArrrayCoords(chunk1DIndex, out int array_cx, out int array_cy, out int array_cz);

                    McChunk newChunk = world.InstantiateAndConfigureChunk(array_cx, array_cy, array_cz, columnsPerDataGenStep, voxelsPerMeshStep, voxelsPerTerrainStep);

                    if (newChunk != null) {
                        worker_targetChunk[i] = newChunk;
                        worker_state[i] = STATE_DATA_GEN;
                        isGeneratorBusy = true; // Generator is now busy
                        
                        #if UNITY_EDITOR
                        if (enableVerboseLogging) {
                            Debug.Log($"[McCoordinator] Started data generation for chunk ({newChunk.chunkX_world},{newChunk.chunkY_world},{newChunk.chunkZ_world})");
                        }
                        #endif

                        // Only assign ONE new data generation task per ProcessWorkers call 
                        // to prevent race conditions and to allow the rebuild queue to be checked next frame.
                        break;
                    } else {
                        // If chunk already existed, we still need to count it as "completed" to not stall the progress counter.
                        chunksCompletedCount++;
                    }
                }
            }
        }
        
        // --- 3. Logging & Perpetual Scheduling ---
        
        // Log progress during initial generation
        if (chunksCompletedCount < totalWorldChunks)
        {
            int currentPercent = (chunksCompletedCount * 100) / totalWorldChunks;
            if (currentPercent / 10 > lastLoggedPercent / 10) {
                #if UNITY_EDITOR
                if (enableVerboseLogging) Debug.Log($"[McCoordinator] World Processing: ~{currentPercent}% complete ({chunksCompletedCount}/{totalWorldChunks} chunks finalized).");
                #endif
                lastLoggedPercent = currentPercent;
            }
        }
        else
        {
            // Log completion of the initial build just once
            if (lastLoggedPercent != 100)
            {
                lastLoggedPercent = 100;
                #if UNITY_EDITOR
                if (enableVerboseLogging) Debug.Log($"[McCoordinator] Initial world generation complete. Now processing player edits.");
                #endif
            }
        }

        // Always reschedule the loop to keep the coordinator alive.
        SendCustomEventDelayedSeconds(nameof(ProcessWorkers), workerProcessingInterval);
    }

    /// <summary>
    /// Queues a chunk for a mesh rebuild. The perpetual ProcessWorkers loop will pick this up.
    /// </summary>
    public void RequestChunkMeshUpdate(McChunk chunkToUpdate)
    {
        if (chunkToUpdate == null || chunkRebuildQueue_count >= MAX_REBUILD_QUEUE_SIZE) return;
        
        // Avoid adding if it's already being processed by a worker
        for(int i = 0; i < maxConcurrentWorkers; i++)
        {
            if (worker_targetChunk[i] == chunkToUpdate) return;
        }

        // Avoid adding duplicates to the queue
        for (int i = 0; i < chunkRebuildQueue_count; i++) {
            int index = (chunkRebuildQueue_head + i) % MAX_REBUILD_QUEUE_SIZE;
            if (chunkRebuildQueue[index] == chunkToUpdate) return;
        }

        chunkRebuildQueue[chunkRebuildQueue_tail] = chunkToUpdate;
        chunkRebuildQueue_tail = (chunkRebuildQueue_tail + 1) % MAX_REBUILD_QUEUE_SIZE;
        chunkRebuildQueue_count++;
    }
}