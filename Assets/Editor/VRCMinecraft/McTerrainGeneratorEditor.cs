#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(McTerrainGenerator))]
public class McTerrainGeneratorEditor : Editor
{
    // Terrain Noise Properties
    private SerializedProperty baseNoiseScaleProp;
    private SerializedProperty baseTerrainHeightProp;
    private SerializedProperty baseHeightVariationAmplitudeProp;
    private SerializedProperty perlinHeightOffsetProp;

    // Terrain Composition Properties
    private SerializedProperty seaLevelProp;
    private SerializedProperty grassBlockIDProp;
    private SerializedProperty stoneBlockIDProp;
    private SerializedProperty dirtBlockIDProp;
    private SerializedProperty waterBlockIDProp;

    // Structure Templates Property
    private SerializedProperty structureTemplatesProp;

    void OnEnable()
    {
        baseNoiseScaleProp = serializedObject.FindProperty("baseNoiseScale");
        baseTerrainHeightProp = serializedObject.FindProperty("baseTerrainHeight");
        baseHeightVariationAmplitudeProp = serializedObject.FindProperty("baseHeightVariationAmplitude");
        perlinHeightOffsetProp = serializedObject.FindProperty("perlinHeightOffset");

        seaLevelProp = serializedObject.FindProperty("seaLevel");
        grassBlockIDProp = serializedObject.FindProperty("grassBlockID");
        stoneBlockIDProp = serializedObject.FindProperty("stoneBlockID");
        dirtBlockIDProp = serializedObject.FindProperty("dirtBlockID");
        waterBlockIDProp = serializedObject.FindProperty("waterBlockID");
        
        structureTemplatesProp = serializedObject.FindProperty("structureTemplates");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.LabelField("Base Terrain Noise Settings", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(baseNoiseScaleProp);
        EditorGUILayout.PropertyField(baseTerrainHeightProp);
        EditorGUILayout.PropertyField(baseHeightVariationAmplitudeProp);
        EditorGUILayout.PropertyField(perlinHeightOffsetProp);
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Terrain Composition", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(seaLevelProp);
        EditorGUILayout.PropertyField(grassBlockIDProp);
        EditorGUILayout.PropertyField(stoneBlockIDProp);
        EditorGUILayout.PropertyField(dirtBlockIDProp);
        EditorGUILayout.PropertyField(waterBlockIDProp);
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Structure & Feature Templates", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Assign prefabs here. Each prefab should have the McStructureTemplate script on its root. Children of the prefab with McStructureVoxelData scripts will define the structure's blocks.", MessageType.Info);
        
        // Display the array of McStructureTemplate references
        EditorGUILayout.PropertyField(structureTemplatesProp, true); // 'true' to draw children (i.e., array elements)

        serializedObject.ApplyModifiedProperties();
    }
}
#endif