using System.Collections.Generic;
using System.Linq;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRRefAssist;
using System;



public enum ChunkValidityState{
    LOADED,
    UNLOADED,
    INVALID
}


[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McWorld : UdonSharpBehaviour
{
    [SerializeField] private MinecraftGame minecraftGame;

    [Header("Chunks")]
    public byte[][][] data;
    
    public string worldSeed;

    public int realWorldSeed;
    public int worldX = 16;
    public int worldY = 16;
    public int worldZ = 16;

    public McChunk chunk;
    public McChunk[][][] chunks;
    public int chunkSizeXZ= 32;
    public int chunkSizeY = 32;


    [Header("Daylight Cycle")]
    public int currentTick = 0;

    /// <summary>
    /// 24 must be divisible by this number!
    /// </summary>
    public int timeStep = 1;
    int concurrentTickCheckCount = 0;
    public Material skyShader;
    public Material terrainShader;

    public Material terrainCutShader;
    public Material terrainTransShader;

    public Color dayFogColor;
    public Color nightFogColor;


    [Header("User Settings")]

    public int renderDistanceXZ = 4;
    public int renderDistanceY = 4;

    // This array contains the pre-instanced chunks, so that they can simply be re-used when a chunk is loaded/unloaded
    // so we don't have to instance new chunks all the time, since it causes lag spikes.
    // When we want to increase the render distance, we simply call setRenderDistance, which resets the chunkCache array by deleting all the chunks,
    // and then regenerates it with the new number of chunks based on the new render distance.
    public McChunk[] chunkBuffer = new McChunk[0];
    int chunkBufferIndex = 0;
    // To iterate over all the 3d chunk area with only ONE int counter, do the following pseudocode:
    // if(arrayX < counter)
    //    doStuff
    // else if(arrayY < RoundToInt(counter/2))
    //    doOtherStuff

    public int chunkGenerationIndexX, chunkGenerationIndexY, chunkGenerationIndexZ;

    private Vector3Int[] chunksToLoadQueue = new Vector3Int[0];
    private int chunksToLoadQueueCount = 0;
    private const int MAX_CHUNKS_PER_FRAME_LOAD = 1; // Load one chunk per ProcessChunks call to spread the load
    private Vector3Int lastPlayerChunkPos = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
    private List<McChunk> activeChunksForUnloading = new List<McChunk>(); // Udon doesn't support List<T>
    // We need an array for active chunks if we want to manage unloading more carefully.
    // For now, the chunkBuffer itself acts as the pool of active McChunk instances.


    public void SetRenderDistance(int newRenderDistXZ, int newRenderDistY)
    {
        renderDistanceXZ = newRenderDistXZ;
        renderDistanceY = newRenderDistY;

        // Calculate the total number of chunks needed for the new render distance
        int newBufferLength = (2 * renderDistanceXZ + 1) * (2 * renderDistanceXZ + 1) * (2 * renderDistanceY + 1);

        McChunk[] newChunkBuffer = new McChunk[newBufferLength];
        int newBufferPopulatedCount = 0;

        // Try to preserve existing, correctly positioned chunks from old buffer
        // And destroy chunks that are no longer needed or are outside the new view range (or simply all old ones for simplicity now)

        if(chunkBuffer.Length != 0)
        {
            for(int i = 0; i < chunkBuffer.Length; i++)
            {
                if(chunkBuffer[i] != null && chunkBuffer[i].gameObject != null) // Check if GO is valid
                { 
                    // For simplicity, we destroy all old chunk GameObjects from the buffer.
                    // A more advanced approach would be to check if they are still needed and reposition them.
                    // However, since GenerateChunkAt handles re-purposing, we just need to ensure they are cleaned up.
                    
                    // We need to remove their McChunk script from the world.chunks array if they were active.
                    // The existing logic in GenerateChunkAt handles nullifying chunks[x][y][z] when a buffer McChunk is reused.
                    // So, just destroying the GOs from the old buffer should be okay IF the new buffer is entirely new instances.
                    // BUT, we want to reuse McChunk components if possible to avoid GC and instantiation cost.

                    // Let's try to move existing McChunk components to the new buffer if the new buffer is larger.
                    // If smaller, destroy excess.
                    if (newBufferPopulatedCount < newBufferLength) {
                        newChunkBuffer[newBufferPopulatedCount++] = chunkBuffer[i];
                        // Don't destroy its GameObject yet, it might be reused.
                    } else {
                        // New buffer is full, or old buffer had more than new capacity. Destroy this one.
                        int x = Mathf.FloorToInt(chunkBuffer[i].gameObject.transform.position.x / chunkSizeXZ);
                        int y = Mathf.FloorToInt(chunkBuffer[i].gameObject.transform.position.y / chunkSizeY);
                        int z = Mathf.FloorToInt(chunkBuffer[i].gameObject.transform.position.z / chunkSizeXZ);
                        if(isChunkValid(new Vector3Int(x,y,z)) == ChunkValidityState.LOADED && chunks[x][y][z] == chunkBuffer[i]) {
                             chunks[x][y][z] = null; // Unlink from active world state
                        }
                        Destroy(chunkBuffer[i].gameObject);
                    }
                }
            }
        }

        // Populate remaining new buffer slots with new instances if needed
        for (int i = newBufferPopulatedCount; i < newBufferLength; i++)
        {   
            GameObject instantiatedChunkGO = Instantiate(chunk.gameObject, new Vector3(0, -chunkSizeY*10,0), Quaternion.identity); // Position far away
            newChunkBuffer[i] = instantiatedChunkGO.GetComponent<McChunk>();
            newChunkBuffer[i].template = true; // Mark as template initially until configured by GenerateChunkAt
            newChunkBuffer[i].worldGO = this.gameObject; // Pre-assign
        }
        
        chunkBuffer = newChunkBuffer;
        chunkBufferIndex = 0; // Reset index for the new buffer

        // After changing render distance, force a re-evaluation of chunks
        lastPlayerChunkPos = new Vector3Int(int.MinValue, int.MinValue, int.MinValue); 
        chunksToLoadQueueCount = 0; // Clear loading queue

        // Also, explicitly unload chunks that are now outside the new render distance.
        // This requires iterating through world.chunks.
        if (chunks != null) {
            for (int x = 0; x < chunks.Length; x++) {
                if (chunks[x] == null) continue;
                for (int y = 0; y < chunks[x].Length; y++) {
                    if (chunks[x][y] == null) continue;
                    for (int z = 0; z < chunks[x][y].Length; z++) {
                        if (chunks[x][y][z] != null) {
                            Vector3 playerChunkCurrent = new Vector3( 
                                Mathf.FloorToInt(Networking.LocalPlayer.GetPosition().x / chunkSizeXZ),
                                Mathf.FloorToInt(Networking.LocalPlayer.GetPosition().y / chunkSizeY),
                                Mathf.FloorToInt(Networking.LocalPlayer.GetPosition().z / chunkSizeXZ)
                            );
                            bool isOutsideNewDistance = 
                                Mathf.Abs(x - playerChunkCurrent.x) > renderDistanceXZ ||
                                Mathf.Abs(y - playerChunkCurrent.y) > renderDistanceY ||
                                Mathf.Abs(z - playerChunkCurrent.z) > renderDistanceXZ;

                            if (isOutsideNewDistance) {
                                McChunk c_to_unload = chunks[x][y][z];
                                c_to_unload.gameObject.transform.position = new Vector3(0, -chunkSizeY*10,0); // Move away
                                c_to_unload.template = true; // Mark as available for buffer
                                // Mesh clearing happens in GenerateMesh or if chunk is empty.
                                chunks[x][y][z] = null;
                                // This McChunk object is already in chunkBuffer, it will be reused.
                            }
                        }
                    }
                }
            }
        }
    }


    public string TimeSet(int ticks)
    {
        currentTick = ticks;
        return $"Set time to {ticks} ticks.";
    }

    public string TimeSet(string time)
    {
        switch(time.ToLower())
        {
            default:
                return $"<color=FF5555> Error: \"{time}\" is not a valid time.";
            case "day":
                currentTick = 1000;
                return "Set time to day.";
            case "noon":
                currentTick = 6000;
                return "Set time to noon.";
            case "night":
                currentTick = 13000;
                return "Set time to night.";
            case "midnight":
                currentTick = 18000;
                return "Set time to midnight.";
        }

    }

    public void TickWorld()
    {
        if(concurrentTickCheckCount!=0) return;
        concurrentTickCheckCount++;
        currentTick = currentTick+timeStep >= 24000 ? 0 : currentTick+timeStep;

        float dayProgress = (float)currentTick / (float)24000;
        RenderSettings.skybox.SetFloat("_DayProgress", dayProgress);
        terrainShader.SetFloat("_DayProgress", dayProgress);
        terrainCutShader.SetFloat("_DayProgress", dayProgress);
        terrainTransShader.SetFloat("_DayProgress", dayProgress);
        RenderSettings.fogColor = CalcFogColor(dayProgress);
        //Debug.Log($"Current Tick: {currentTick}, DayProgress: {dayProgress.ToString("#.000000")}");

        if(currentTick == 1000 || currentTick == 6000 || currentTick == 13000 || currentTick == 18000)
        {
            minecraftGame.musicManager.playRandomTrack(minecraftGame.currentGamemode);
        }
        
        concurrentTickCheckCount--; 
        SendCustomEventDelayedSeconds(nameof(TickWorld), 0.05f);
    }

    public void ProcessChunks()
    {
        Vector3 playerPos = Networking.LocalPlayer.GetPosition();
        int playerChunkX = Mathf.FloorToInt(playerPos.x / chunkSizeXZ);
        int playerChunkY = Mathf.FloorToInt(playerPos.y / chunkSizeY);
        int playerChunkZ = Mathf.FloorToInt(playerPos.z / chunkSizeXZ);
        Vector3Int currentPlayerChunkPos = new Vector3Int(playerChunkX, playerChunkY, playerChunkZ);

        // If player moved to a new chunk, re-evaluate chunks to load/unload
        if (currentPlayerChunkPos != lastPlayerChunkPos)
        {
            // Clear previous queue (optional, or could intelligently update)
            chunksToLoadQueueCount = 0; 
            // Potentially add unloading logic here too for chunks far from player.
            // For now, focus on loading.

            for (int yOffset = -renderDistanceY; yOffset <= renderDistanceY; yOffset++)
            {
                for (int xOffset = -renderDistanceXZ; xOffset <= renderDistanceXZ; xOffset++)
                {
                    for (int zOffset = -renderDistanceXZ; zOffset <= renderDistanceXZ; zOffset++)
                    {
                        // Optional: Prioritize closer chunks or use a distance check for spherical loading
                        // if (new Vector3(xOffset, yOffset, zOffset).magnitude > renderDistanceXZ) continue;

                        Vector3Int chunkToEvaluate = new Vector3Int(playerChunkX + xOffset, playerChunkY + yOffset, playerChunkZ + zOffset);
                        
                        if (isChunkValid(chunkToEvaluate) == ChunkValidityState.UNLOADED)
                        {
                            // Add to queue if not already there (simple check, could be more robust)
                            bool alreadyInQueue = false;
                            for(int i=0; i < chunksToLoadQueueCount; i++)
                            {
                                if(chunksToLoadQueue[i] == chunkToEvaluate)
                                {
                                    alreadyInQueue = true;
                                    break;
                                }
                            }
                            if (!alreadyInQueue)
                            {
                                if(chunksToLoadQueueCount >= chunksToLoadQueue.Length)
                                {
                                    // Resize queue if needed (Udon doesn't have List.Add directly)
                                    Vector3Int[] newQueue = new Vector3Int[chunksToLoadQueue.Length + 10]; // Increase by a fixed amount
                                    Array.Copy(chunksToLoadQueue, newQueue, chunksToLoadQueue.Length);
                                    chunksToLoadQueue = newQueue;
                                }
                                chunksToLoadQueue[chunksToLoadQueueCount++] = chunkToEvaluate;
                            }
                        }
                    }
                }
            }
            lastPlayerChunkPos = currentPlayerChunkPos;
        }

        // Process some chunks from the queue
        int chunksProcessedThisFrame = 0;
        while(chunksToLoadQueueCount > 0 && chunksProcessedThisFrame < MAX_CHUNKS_PER_FRAME_LOAD)
        {
            // Dequeue (simplified: take from end, then decrement count)
            chunksToLoadQueueCount--;
            Vector3Int chunkToLoad = chunksToLoadQueue[chunksToLoadQueueCount];
            
            // Double check validity before loading, in case state changed
            if (isChunkValid(chunkToLoad) == ChunkValidityState.UNLOADED)
            {
                GenerateChunkAt(chunkToLoad);
            }
            chunksProcessedThisFrame++;
        }

        SendCustomEventDelayedSeconds(nameof(ProcessChunks), 0.1f);
    }

    public ChunkValidityState isChunkValid(Vector3Int chunkPosition)
    {
        if(minecraftGame.debugMode){
        }
        
        if(chunkPosition.x >= 0 && chunkPosition.x  < chunks.Length)
        {
            if(chunkPosition.y >= 0 && chunkPosition.y < chunks[chunkPosition.x].Length)
            {
                if(chunkPosition.z >= 0 && chunkPosition.z < chunks[chunkPosition.x][chunkPosition.y].Length)  
                {
                    if(chunks[chunkPosition.x][chunkPosition.y][chunkPosition.z] == null) {
                        return ChunkValidityState.UNLOADED;
                    } else {
                        return ChunkValidityState.LOADED;
                    }
                }
            }
        }

        return ChunkValidityState.INVALID;
    }

    public Color CalcFogColor(float _dayProgress)
    {
        float dayNightTransition;
        if (_dayProgress < 0.0417) { // 0 to 1000 ticks
            dayNightTransition = (float)(_dayProgress / 0.0417);
        } else if (_dayProgress > 0.5 && _dayProgress < 0.5417) { // 12000 to 13000 ticks
            dayNightTransition = (float)(1 - ((_dayProgress - 0.5) / 0.0417));
        } else if (_dayProgress <= 0.5) {
            dayNightTransition = 1;
        } else {
            dayNightTransition = 0;
        }
        
        return Color.Lerp(nightFogColor, dayFogColor, dayNightTransition);
    }

    void SetWorldSeed(string seed)
    {
        /// If the seed contains characters other than numbers or is greater than or equal to 20 characters in length,
        /// the Java String.hashCode() function is used to generate a number seed. This restricts Minecraft to a subset of the possible worlds to 232 (or 4,294,967,296), 
        /// due to the int datatype used. Number seeds or a default world seed must be used to access the full set of possible worlds (264, or 18,446,744,073,709,551,616). 
        /// There are 248 meaningful seeds because Java's Random uses 48 bits of the seed; seeds are equivalent to one another modulo 248.
        /// 
        /// We have to use an Unity C# alternative that works with Udon here, since we're not in Java.
        /// 
    
        bool nonNumericSeed = seed.Length >= 20 || !IsDigitsOnly(seed);
        if (nonNumericSeed)
        {
            realWorldSeed = seed.GetHashCode();
        } else {
            realWorldSeed = int.Parse(seed);
        }
    }

    bool IsDigitsOnly(string str)
    {
        foreach (char c in str)
        {
            if (!char.IsDigit(c)) return false;
        }

        return true;
    }

    void Start()
    {
        TimeSet(0);
        SetWorldSeed(worldSeed);
        SetRenderDistance(renderDistanceXZ,renderDistanceY);

        data = new byte[worldX][][];
        for (int x = 0; x < worldX; x++)
        {
            data[x] = new byte[worldY][];
            for (int y = 0; y < worldY; y++)
            {
                data[x][y] = new byte[worldZ];
            }
        }

        /*for (int x = 0; x < worldX; x++)
        {
            for (int y = 0; y < worldY; y++)
            {
                for (int z = 0; z < worldZ; z++)
                {
                    if (y <= 8)
                    {
                        data[x][y][z] = 1;
                    }
                }
            }
        }*/

        //  Instead of rendering the whole world at once,
        //  We could have a render distance.
        //  Convert the player position to chunk-space coordinates,
        //  And based on that, check if the nearby chunks already exist.
        //  If not, create a new chunk at said position.


        for(int x = 0; x<worldX; x++)
        {
            for(int z = 0; z<worldZ; z++)
            {
                if(z > 2) realWorldSeed = 143;
                int stone=PerlinNoise(x,0,z,10,3,1.2f);
                stone+= PerlinNoise(x,300,z,20,4,0)+10;
                int dirt=PerlinNoise(x,100,z,50,2,0) +1; //Added +1 to make sure minimum grass height is 1

                for(int y = 0; y<worldY; y++)
                {
                    if(y<= stone){
                        data[x][y][z]=1;
                    } else if (y<=dirt+stone)
                    {
                        data[x][y][z]=2;
                    }
                }
            }
        }


        int chunkXCount = Mathf.FloorToInt(worldX / chunkSizeXZ);
        int chunkYCount = Mathf.FloorToInt(worldY / chunkSizeY);
        int chunkZCount = Mathf.FloorToInt(worldZ / chunkSizeXZ);

        chunks = new McChunk[chunkXCount][][];
        for (int x = 0; x < chunkXCount; x++)
        {
            chunks[x] = new McChunk[chunkYCount][];
            for (int y = 0; y < chunkYCount; y++)
            {
                chunks[x][y] = new McChunk[chunkZCount];
            }
        }
        //SendCustomEventDelayedSeconds(nameof(GenerateAnotherChunk), 0.5f);

        ProcessChunks();

        TickWorld();
    }


    public void GenerateChunkAt(Vector3Int chunkPosition)
    {
        int x = chunkPosition.x;
        int y = chunkPosition.y;
        int z = chunkPosition.z;

        int chunkXCount = Mathf.FloorToInt(worldX / chunkSizeXZ);
        int chunkYCount = Mathf.FloorToInt(worldY / chunkSizeY);
        int chunkZCount = Mathf.FloorToInt(worldZ / chunkSizeXZ);

        if(x < 0 || y < 0 || z < 0) return;
        if(!(x < chunkXCount || !(y < chunkYCount) || !(z < chunkZCount))) return;

        //GameObject newChunk = Instantiate(chunk.gameObject, new Vector3(transform.position.x + x * chunkSizeXZ-0.5f, transform.position.y + y * chunkSizeY-0.5f, transform.position.z + z * chunkSizeXZ-0.5f), Quaternion.identity).gameObject;
        GameObject newChunk;
        if(chunkBufferIndex < chunkBuffer.Length)
        {
            if(chunkBuffer[chunkBufferIndex] != null)
            {
                int xx = Mathf.FloorToInt(chunkBuffer[chunkBufferIndex].gameObject.transform.position.x / chunkSizeXZ);
                int yy = Mathf.FloorToInt(chunkBuffer[chunkBufferIndex].gameObject.transform.position.y / chunkSizeY);
                int zz = Mathf.FloorToInt(chunkBuffer[chunkBufferIndex].gameObject.transform.position.z / chunkSizeXZ);
                if(isChunkValid(new Vector3Int(xx,yy,zz)) != ChunkValidityState.INVALID) chunks[xx][yy][zz] = null;
            }
            newChunk = chunkBuffer[chunkBufferIndex].gameObject;
            chunkBufferIndex++;
        } else {
            chunkBufferIndex = 0;
            if(chunkBuffer[chunkBufferIndex] != null)
            {
                int xx = Mathf.FloorToInt(chunkBuffer[chunkBufferIndex].gameObject.transform.position.x / chunkSizeXZ);
                int yy = Mathf.FloorToInt(chunkBuffer[chunkBufferIndex].gameObject.transform.position.y / chunkSizeY);
                int zz = Mathf.FloorToInt(chunkBuffer[chunkBufferIndex].gameObject.transform.position.z / chunkSizeXZ);
                if(isChunkValid(new Vector3Int(xx,yy,zz)) != ChunkValidityState.INVALID) chunks[xx][yy][zz] = null;
            }
            newChunk = chunkBuffer[chunkBufferIndex].gameObject;
        }

        
 
        if(minecraftGame.debugMode) Debug.Log($"Setting Chunk X{x} Y{y} Z{z}");
        newChunk.name = $"Chunk X{x} Y{y} Z{z}"; 
        newChunk.transform.position = new Vector3(transform.position.x + x * chunkSizeXZ-0.5f, transform.position.y + y * chunkSizeY-0.5f, transform.position.z + z * chunkSizeXZ-0.5f);
        chunks[x][y][z] = newChunk.GetComponent<McChunk>();
        chunks[x][y][z].worldGO = this.gameObject;
        chunks[x][y][z].template = false;
        chunks[x][y][z].chunkSizeXZ = chunkSizeXZ;
        chunks[x][y][z].chunkSizeY = chunkSizeY;
        chunks[x][y][z].chunkX = x * chunkSizeXZ;
        chunks[x][y][z].chunkY = y * chunkSizeY;
        chunks[x][y][z].chunkZ = z * chunkSizeXZ;
        chunks[x][y][z].OnInstance();
    }

    public void GenerateAnotherChunk()
    {
        int chunkXCount = Mathf.FloorToInt(worldX / chunkSizeXZ)-1;
        int chunkYCount = Mathf.FloorToInt(worldY / chunkSizeY)-1;
        int chunkZCount = Mathf.FloorToInt(worldZ / chunkSizeXZ)-1;

        if(chunkGenerationIndexX < chunkXCount)
        {
            chunkGenerationIndexX++;
            if (chunkGenerationIndexY < chunkYCount)
            {
                chunkGenerationIndexY++;
                if (chunkGenerationIndexZ < chunkZCount)
                {
                    chunkGenerationIndexZ++;
                }
            }
            else
            {
                if (chunkGenerationIndexZ < chunkZCount)
                {
                    chunkGenerationIndexZ++;
                }
            }
        }
        else
        {
            if (chunkGenerationIndexY < chunkYCount)
            {
                chunkGenerationIndexY++;
                if (chunkGenerationIndexZ < chunkZCount)
                {
                    chunkGenerationIndexZ++;
                }
            }
            else
            {
                if (chunkGenerationIndexZ < chunkZCount)
                {
                    chunkGenerationIndexZ++;
                }
            }
        }

        int x = chunkGenerationIndexX;
        int y = chunkGenerationIndexY;
        int z = chunkGenerationIndexZ;

        if(chunkGenerationIndexX >= chunkXCount && chunkGenerationIndexY >= chunkYCount && chunkGenerationIndexZ >= chunkZCount) return;
        


        GameObject newChunk = Instantiate(chunk.gameObject, new Vector3(transform.position.x + x * chunkSizeXZ-0.5f, transform.position.y + y * chunkSizeY-0.5f, transform.position.z + z * chunkSizeXZ-0.5f), Quaternion.identity).gameObject;
        
        if(minecraftGame.debugMode) Debug.Log($"Setting Chunk X{x} Y{y} Z{z}");
        if(minecraftGame.debugMode) newChunk.name = $"Chunk X{x} Y{y} Z{z}"; 
        chunks[x][y][z] = newChunk.GetComponent<McChunk>();
        chunks[x][y][z].worldGO = this.gameObject;
        chunks[x][y][z].template = false;
        chunks[x][y][z].chunkSizeXZ = chunkSizeXZ;
        chunks[x][y][z].chunkSizeY = chunkSizeY;
        chunks[x][y][z].chunkX = x * chunkSizeXZ;
        chunks[x][y][z].chunkY = y * chunkSizeY;
        chunks[x][y][z].chunkZ = z * chunkSizeXZ;

        SendCustomEventDelayedSeconds(nameof(GenerateAnotherChunk), 0.5f);
            
        
    }


    int PerlinNoise(int x,int y, int z, float scale, float height, float power)
    {
        
        float rValue;
        //rValue= noise.GetNoise (((double)x) / scale, ((double)y)/ scale, ((double)z) / scale);
        rValue = Mathf.PerlinNoise(x / scale + realWorldSeed, z/ scale + realWorldSeed);
        rValue*=height;
        
            if(power!=0){
            //rValue=Mathf.Pow( rValue, power+1); 
            }
        
        return (int) rValue;
    }

    public byte Block(int x, int y, int z)
    {
        if (x >= worldX || x < 0 || y >= worldY || y < 0 || z >= worldZ || z < 0)
        {
            return (byte)1;
        }

        return data[x][y][z];
    }
}
