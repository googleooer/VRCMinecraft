using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;
using VRRefAssist;

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ModifyTerrain : UdonSharpBehaviour
{
    [SerializeField] private MinecraftGame minecraftGame;
    [SerializeField] private McWorld world;
    Vector3 cameraPos;
    Quaternion cameraRot;
    Vector3 cameraForward;
    [SerializeField] private Transform blockOutline;
    public float blockPlacementRange = 5;

    void Start()
    {
    }

    //int n = 0;

    public override void InputUse(bool value, UdonInputEventArgs args)
    {
        if(minecraftGame.debugMode) Debug.Log($"InputUse state is {value}");
        if(value){
            ReplaceBlockCenter(blockPlacementRange,0);
            //SetBlockAt(Networking.LocalPlayer.GetPosition()+new Vector3(0,0.5f,0), 0);
        }
    }

    public override void InputDrop(bool value, UdonInputEventArgs args)
    {
        if(minecraftGame.debugMode) Debug.Log($"InputDrop state is {value}");
        if(value){
            AddBlockCenter(blockPlacementRange,1);
            //SetBlockAt(Networking.LocalPlayer.GetPosition()+new Vector3(0,0.5f,0), 1);
        }
    }

    void Update()
    {
        Ray ray = new Ray(getCamPos(), getCamForward());
        RaycastHit hit;
        if(Physics.Raycast(ray, out hit))
        {
            if(hit.distance<blockPlacementRange)
            {
                Vector3 position = hit.point;
                position+= (hit.normal * -0.5f);

                blockOutline.position = Vector3Int.RoundToInt(position);
            } else {
                blockOutline.position = new Vector3(0,-1000,0);
            }
        }
        Debug.DrawLine(ray.origin,ray.origin+( ray.direction*hit.distance),Color.green,2);
        Debug.DrawLine(hit.point,hit.point+( hit.normal*2),Color.red,2);

        
        /*if(Input.GetMouseButton(0)){
            ReplaceBlockCenter(5,0);
            //SetBlockAt(Networking.LocalPlayer.GetPosition(), 0);
        }

        //SetBlockAt(4,n,4,2);
        //n++;

        if(Input.GetMouseButton(1)){
            AddBlockCenter(5,1);
            //SetBlockAt(Networking.LocalPlayer.GetPosition(), 1);
        }*/
    }

    Vector3 getCamPos()
    {
        return Networking.LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
    }

    Quaternion getCamRot()
    {
        return Networking.LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).rotation;
    }

    Vector3 getCamForward()
    {
        return getCamRot() * Vector3.forward;
    }

    public void ReplaceBlockCenter(float range, byte block){
        //Replaces the block directly in front of the player
        Ray ray = new Ray(getCamPos(), getCamForward());
        RaycastHit hit;

        if(Physics.Raycast(ray, out hit))
        {
            if(hit.distance<range)
            {
                ReplaceBlockAt(hit, block);
            }
        }
        Debug.DrawLine(ray.origin,ray.origin+( ray.direction*hit.distance),Color.green,2);
        Debug.DrawLine(hit.point,hit.point+( hit.normal*2),Color.red,2);

    }

    public void AddBlockCenter(float range, byte block){
        //Adds the block specified directly in front of the player
        Ray ray = new Ray(getCamPos(), getCamForward());
        RaycastHit hit;

        if(Physics.Raycast(ray, out hit))
        {
            if(hit.distance<range)
            {
                AddBlockAt(hit, block);
            }
        }
        Debug.DrawLine(ray.origin,ray.origin+( ray.direction*hit.distance),Color.green,2);
        Debug.DrawLine(hit.point,hit.point+( hit.normal*2),Color.red,2);
        
    }

    /*public void ReplaceBlockCursor(byte block){
        //Replaces the block specified where the mouse cursor is pointing

        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        RaycastHit hit;

        if(Physics.Raycast(ray, out hit))
        {
            ReplaceBlockAt(hit, block);
        }
    }*/

    /*public void AddBlockCursor( byte block){
        //Adds the block specified where the mouse cursor is pointing
        
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        RaycastHit hit;

        if(Physics.Raycast(ray, out hit))
        {
            AddBlockAt(hit, block);
        }
    }*/

    public void ReplaceBlockAt(RaycastHit hit, byte block) {
        //removes a block at these impact coordinates, you can raycast against the terrain and call this with the hit.point
        Vector3 position = hit.point;
        position+= (hit.normal * -0.5f);

        SetBlockAt(position+new Vector3(0,1,0), block);
    }

    public void AddBlockAt(RaycastHit hit, byte block) {
        //adds the specified block at these impact coordinates, you can raycast against the terrain and call this with the hit.point
        Vector3 position = hit.point;
        position+= (hit.normal * 0.5f);

        SetBlockAt(position+new Vector3(0,1,0), block);
    }

    public void SetBlockAt(Vector3 position, byte block) {
        //sets the specified block at these coordinates
        int x = Mathf.RoundToInt(position.x);
        int y = Mathf.RoundToInt(position.y);
        int z = Mathf.RoundToInt(position.z);

        SetBlockAt(x, y, z, block);
    }

    public void SetBlockAt(int x, int y, int z, byte block) {
        //adds the specified block at these coordinates
        if(minecraftGame.debugMode) Debug.Log($"Adding {x}, {y}, {z}");

        if (!isBlockValid(x,y,z)) return;

        world.data[x][y][z] = block;
        UpdateChunkAt(x,y,z);
    }

    public void UpdateChunkAt(int x, int y, int z){
        //Updates the chunk containing this block
        int updateX = Mathf.FloorToInt( x / world.chunkSizeXZ);
        int updateY = Mathf.FloorToInt( y / world.chunkSizeY);
        int updateZ = Mathf.FloorToInt( z / world.chunkSizeXZ);

        if(minecraftGame.debugMode) Debug.Log($"Updating Chunk: {updateX}, {updateY}, {updateZ}");
        if(world.isChunkValid(new Vector3Int(updateX, updateY, updateZ)) != ChunkValidityState.LOADED) return;

        world.chunks[updateX][updateY][updateZ].update = true;

        // Check and update adjacent chunks if the block is on a border
        int localX = x - (world.chunkSizeXZ * updateX);
        int localY = y - (world.chunkSizeY * updateY);
        int localZ = z - (world.chunkSizeXZ * updateZ);

        // Check X-axis boundaries
        if(localX == 0 && updateX > 0 && world.isChunkValid(new Vector3Int(updateX - 1, updateY, updateZ)) == ChunkValidityState.LOADED){
            world.chunks[updateX-1][updateY][updateZ].update=true;
        }
        if(localX == world.chunkSizeXZ - 1 && updateX < world.chunks.Length - 1 && world.isChunkValid(new Vector3Int(updateX + 1, updateY, updateZ)) == ChunkValidityState.LOADED){
            world.chunks[updateX+1][updateY][updateZ].update=true; 
        }

        // Check Y-axis boundaries
        if(localY == 0 && updateY > 0 && world.isChunkValid(new Vector3Int(updateX, updateY - 1, updateZ)) == ChunkValidityState.LOADED){
            world.chunks[updateX][updateY-1][updateZ].update=true;
        }
        if(localY == world.chunkSizeY - 1 && updateY < world.chunks[updateX].Length - 1 && world.isChunkValid(new Vector3Int(updateX, updateY + 1, updateZ)) == ChunkValidityState.LOADED){
            world.chunks[updateX][updateY+1][updateZ].update=true;
        }
        
        // Check Z-axis boundaries
        if(localZ == 0 && updateZ > 0 && world.isChunkValid(new Vector3Int(updateX, updateY, updateZ - 1)) == ChunkValidityState.LOADED){
            world.chunks[updateX][updateY][updateZ-1].update=true;
        }
        if(localZ == world.chunkSizeXZ - 1 && updateZ < world.chunks[updateX][updateY].Length - 1 && world.isChunkValid(new Vector3Int(updateX, updateY, updateZ + 1)) == ChunkValidityState.LOADED){
            world.chunks[updateX][updateY][updateZ+1].update=true;
        }
    }

    bool isBlockValid(int x, int y, int z)
    {
        if(world == null || world.data == null) return false;
        //Checks if the block at these coordinates is valid for placement
        return x >= 0 && x < world.data.Length && y >= 0 && y < world.data[0].Length && z >= 0 && z < world.data[0][0].Length;
    }
}
