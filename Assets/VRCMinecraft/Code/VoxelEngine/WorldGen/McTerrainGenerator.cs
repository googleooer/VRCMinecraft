using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRRefAssist;
using System.Text;

[Singleton]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class McTerrainGenerator : UdonSharpBehaviour
{
    [Header("Biome & Surface Settings")]
    [Tooltip("The Y-level at or below which water will be placed in open areas.")]
    public int seaLevel = 62;
    [Tooltip("The depth, in blocks, that grass/dirt will form on surfaces.")]
    public int surfaceDepth = 4;


    [Header("Terrain Composition")]
    public byte airBlockID = 0;
    public byte grassBlockID = 2;
    public byte stoneBlockID = 1;
    public byte dirtBlockID = 3;
    public byte waterBlockID = 9;
    public byte bedrockBlockID = 7;
    public byte sandBlockID = 12;
    public byte sandStoneBlockID = 24;
    // --- END: Terrain Settings ---


    
    [Header("Structure & Feature Templates")]
    public McStructureTemplate[] structureTemplates;

    [SerializeField, FindObjectOfType(true)]
    private McWorld world;

    [SerializeField, FindObjectOfType(true)]
    private McBlockTypeManager blockTypeManager;

    private bool isInitialized = false;
    private int _worldActualSeed;

    private uint _placementRandState;

#if UNITY_EDITOR
    [HideInInspector] public bool enableVerboseLogging = true;
#endif
    private StringBuilder logBuilder;

    public void InitializeGenerator(int seed)
    {
        float startTime = Time.realtimeSinceStartup;
        if (isInitialized) return;

        logBuilder = new StringBuilder(256);
        _worldActualSeed = seed;

        isInitialized = true;

#if UNITY_EDITOR
        if (enableVerboseLogging) {
            logBuilder.Clear();
            logBuilder.AppendFormat("[McTerrainGenerator.InitializeGenerator] Complete. Seed: {0}. Time: {1:F2} ms.", seed, (Time.realtimeSinceStartup - startTime) * 1000f);
            Debug.Log(logBuilder.ToString());
        }
#endif
    }
}
