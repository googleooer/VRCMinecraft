#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

// Ensure McBlockTextureMappingType enum is accessible. 
// If it's in McBlockTypeManager.cs (a non-Editor script), it should be.
// Otherwise, you might need to define/reference it appropriately here.

// Ensure BlockVisibilityType enum is accessible
// It MUST be defined outside the McBlockTypeManager class in its .cs file, or in its own .cs file.

[CustomEditor(typeof(McBlockTypeManager))]
public class McBlockTypeManagerEditor : Editor
{
    private McBlockTypeManager manager;
    private SerializedObject soTarget;

    // Global Atlas Settings
    private SerializedProperty textureAtlasTUnitProp;
    private SerializedProperty textureAtlasUVPaddingProp;

    // Parallel Array Properties
    private SerializedProperty numberOfBlockTypesProp;
    private SerializedProperty blockNamesProp;
    private SerializedProperty isSolidDataProp;
    // private SerializedProperty isTransparentDataProp; // Removed
    private SerializedProperty blockVisibilityTypeDataProp; // New: for BlockVisibilityType
    
    // UV Atlas Coordinate Properties
    private SerializedProperty uv_allFacesDataProp;
    private SerializedProperty uv_topFaceDataProp;
    private SerializedProperty uv_bottomFaceDataProp;
    private SerializedProperty uv_sideFacesDataProp;
    private SerializedProperty textureMappingTypeDataProp; // Stores McBlockTextureMappingType as int

    // Sound and Particle Properties
    private SerializedProperty breakSoundsProp;
    private SerializedProperty placeSoundsProp;
    private SerializedProperty footstepSoundsProp;
    private SerializedProperty breakParticlesPrefabDataProp;
    private SerializedProperty placeParticlesPrefabDataProp;

    private bool[] foldoutStates; 

    void OnEnable()
    {
        manager = (McBlockTypeManager)target;
        soTarget = serializedObject; 

        textureAtlasTUnitProp = soTarget.FindProperty("textureAtlasTUnit");
        textureAtlasUVPaddingProp = soTarget.FindProperty("textureAtlasUVPadding");

        numberOfBlockTypesProp = soTarget.FindProperty("numberOfBlockTypes");
        blockNamesProp = soTarget.FindProperty("blockNames");
        isSolidDataProp = soTarget.FindProperty("isSolidData");
        // isTransparentDataProp = soTarget.FindProperty("isTransparentData"); // Removed
        blockVisibilityTypeDataProp = soTarget.FindProperty("blockVisibilityTypeData"); // New
        
        uv_allFacesDataProp = soTarget.FindProperty("uv_allFacesData");
        uv_topFaceDataProp = soTarget.FindProperty("uv_topFaceData");
        uv_bottomFaceDataProp = soTarget.FindProperty("uv_bottomFaceData");
        uv_sideFacesDataProp = soTarget.FindProperty("uv_sideFacesData");
        textureMappingTypeDataProp = soTarget.FindProperty("textureMappingTypeData");

        breakSoundsProp = soTarget.FindProperty("breakSounds");
        placeSoundsProp = soTarget.FindProperty("placeSounds");
        footstepSoundsProp = soTarget.FindProperty("footstepSounds");
        breakParticlesPrefabDataProp = soTarget.FindProperty("breakParticlesPrefabData");
        placeParticlesPrefabDataProp = soTarget.FindProperty("placeParticlesPrefabData");

        if (manager != null && (foldoutStates == null || foldoutStates.Length != manager.numberOfBlockTypes))
        {
            if (manager.numberOfBlockTypes >= 0)
            {
                foldoutStates = new bool[manager.numberOfBlockTypes];
            }
            else
            {
                 foldoutStates = new bool[0]; // Handle invalid initial size
            }
        }
    }

    public override void OnInspectorGUI()
    {
        soTarget.Update();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Block Type Definitions Manager (Atlas-Based)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Configure global atlas settings and per-block properties. Block ID is the index in the parallel arrays.", MessageType.Info);
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Global Texture Atlas Settings", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(textureAtlasTUnitProp, new GUIContent("Atlas TUnit (e.g., 1/16)"));
        EditorGUILayout.PropertyField(textureAtlasUVPaddingProp, new GUIContent("Atlas UV Padding"));
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Block Type Configuration", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(numberOfBlockTypesProp);
        if (EditorGUI.EndChangeCheck())
        {
            if (numberOfBlockTypesProp.intValue < 0) numberOfBlockTypesProp.intValue = 0;
        }

        if (GUILayout.Button("Apply Number of Types & Resize Arrays"))
        {
            ResizeAllArrays(numberOfBlockTypesProp.intValue);
            if (numberOfBlockTypesProp.intValue >= 0 && (foldoutStates == null || foldoutStates.Length != numberOfBlockTypesProp.intValue))
            {
                 foldoutStates = new bool[numberOfBlockTypesProp.intValue];
            }
        }
        
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Block Properties (per ID):", EditorStyles.boldLabel);

        int currentNumberOfBlockTypes = numberOfBlockTypesProp.intValue;
        if (currentNumberOfBlockTypes >= 0 && (foldoutStates == null || currentNumberOfBlockTypes != foldoutStates.Length)) {
             foldoutStates = new bool[currentNumberOfBlockTypes];
        }
        
        // Basic check to see if arrays are sized. The button is the main way to fix.
        if (blockNamesProp.arraySize != currentNumberOfBlockTypes && currentNumberOfBlockTypes > 0) {
            EditorGUILayout.HelpBox("Array sizes mismatch 'Number Of Block Types'. Click 'Apply Number of Types & Resize Arrays' to fix.", MessageType.Warning);
        } else {
            for (int i = 0; i < currentNumberOfBlockTypes; i++)
            {
                string blockName = "Unnamed Block";
                if (blockNamesProp.arraySize > i && blockNamesProp.GetArrayElementAtIndex(i) != null) {
                     blockName = blockNamesProp.GetArrayElementAtIndex(i).stringValue;
                     if (string.IsNullOrEmpty(blockName)) blockName = $"Block ID: {i} (Unnamed)";
                } else {
                    blockName = $"Block ID: {i} (Data Array Mismatch!)";
                }

                if (foldoutStates.Length <= i) { // Safety resize for foldouts if needed
                    bool[] newFoldouts = new bool[currentNumberOfBlockTypes];
                    System.Array.Copy(foldoutStates, newFoldouts, foldoutStates.Length);
                    foldoutStates = newFoldouts;
                }

                foldoutStates[i] = EditorGUILayout.Foldout(foldoutStates[i], $"{blockName} (ID: {i})", true, EditorStyles.foldoutHeader);

                if (foldoutStates[i])
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField("General", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(blockNamesProp.GetArrayElementAtIndex(i), new GUIContent("Name"));
                    EditorGUILayout.PropertyField(isSolidDataProp.GetArrayElementAtIndex(i), new GUIContent("Is Solid"));
                    
                    // New EnumPopup for BlockVisibilityType
                    SerializedProperty visibilityTypeProp = blockVisibilityTypeDataProp.GetArrayElementAtIndex(i);
                    visibilityTypeProp.intValue = (int)(BlockVisibilityType)EditorGUILayout.EnumPopup(new GUIContent("Visibility Type"), (BlockVisibilityType)visibilityTypeProp.intValue);
                    
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Texturing (Atlas Coords: Col, Row)", EditorStyles.boldLabel);
                    SerializedProperty mappingTypeProp = textureMappingTypeDataProp.GetArrayElementAtIndex(i);
                    mappingTypeProp.intValue = (int)(McBlockTextureMappingType)EditorGUILayout.EnumPopup("Texture Mapping", (McBlockTextureMappingType)mappingTypeProp.intValue);

                    McBlockTextureMappingType currentMappingType = (McBlockTextureMappingType)mappingTypeProp.intValue;
                    if (currentMappingType == McBlockTextureMappingType.AllFacesSame)
                    {
                        EditorGUILayout.PropertyField(uv_allFacesDataProp.GetArrayElementAtIndex(i), new GUIContent("UV All Faces (Col, Row)"));
                    }
                    else if (currentMappingType == McBlockTextureMappingType.TopBottomSides)
                    {
                        EditorGUILayout.PropertyField(uv_topFaceDataProp.GetArrayElementAtIndex(i), new GUIContent("UV Top Face (Col, Row)"));
                        EditorGUILayout.PropertyField(uv_bottomFaceDataProp.GetArrayElementAtIndex(i), new GUIContent("UV Bottom Face (Col, Row)"));
                        EditorGUILayout.PropertyField(uv_sideFacesDataProp.GetArrayElementAtIndex(i), new GUIContent("UV Side Faces (Col, Row)"));
                    }

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Audio & Particles", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(breakSoundsProp.GetArrayElementAtIndex(i), new GUIContent("Break Sound"));
                    EditorGUILayout.PropertyField(placeSoundsProp.GetArrayElementAtIndex(i), new GUIContent("Place Sound"));
                    EditorGUILayout.PropertyField(footstepSoundsProp.GetArrayElementAtIndex(i), new GUIContent("Footstep Sound"));
                    EditorGUILayout.PropertyField(breakParticlesPrefabDataProp.GetArrayElementAtIndex(i), new GUIContent("Break Particles"));
                    EditorGUILayout.PropertyField(placeParticlesPrefabDataProp.GetArrayElementAtIndex(i), new GUIContent("Place Particles"));
                    
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.Separator();
            }
        }
        soTarget.ApplyModifiedProperties();
    }

    private void ResizeAllArrays(int newSize)
    {
        if (newSize < 0) newSize = 0; 
        Undo.RecordObject(manager, "Resize Block Type Arrays");

        ResizeSerializedArray<string>(blockNamesProp, newSize, "");
        ResizeSerializedArrayValueType<bool>(isSolidDataProp, newSize, true);
        // ResizeSerializedArrayValueType<bool>(isTransparentDataProp, newSize, false); // Removed
        ResizeSerializedArrayValueType<int>(blockVisibilityTypeDataProp, newSize, (int)BlockVisibilityType.Opaque); // New, default to Opaque

        ResizeSerializedArrayValueType<Vector2>(uv_allFacesDataProp, newSize, Vector2.zero);
        ResizeSerializedArrayValueType<Vector2>(uv_topFaceDataProp, newSize, Vector2.zero);
        ResizeSerializedArrayValueType<Vector2>(uv_bottomFaceDataProp, newSize, Vector2.zero);
        ResizeSerializedArrayValueType<Vector2>(uv_sideFacesDataProp, newSize, Vector2.zero);
        ResizeSerializedArrayValueType<int>(textureMappingTypeDataProp, newSize, (int)McBlockTextureMappingType.AllFacesSame);
        
        ResizeSerializedArray<AudioClip>(breakSoundsProp, newSize, null); 
        ResizeSerializedArray<AudioClip>(placeSoundsProp, newSize, null);
        ResizeSerializedArray<AudioClip>(footstepSoundsProp, newSize, null);
        ResizeSerializedArray<ParticleSystem>(breakParticlesPrefabDataProp, newSize, null);
        ResizeSerializedArray<ParticleSystem>(placeParticlesPrefabDataProp, newSize, null);

        if (blockNamesProp.arraySize == newSize) {
            for (int i = 0; i < newSize; i++)
            {
                SerializedProperty nameElement = blockNamesProp.GetArrayElementAtIndex(i);
                if (nameElement.propertyType == SerializedPropertyType.String && string.IsNullOrEmpty(nameElement.stringValue))
                {
                    nameElement.stringValue = $"Block_{i}";
                }
            }
        }

        EditorUtility.SetDirty(manager);
        Debug.Log($"[McBlockTypeManagerEditor] Resized all block property arrays to size {newSize}.");
    }

    private void ResizeSerializedArray<T>(SerializedProperty arrayProperty, int newSize, T defaultValue) where T : class
    {
        int oldSize = arrayProperty.arraySize;
        arrayProperty.arraySize = newSize; 
        if (newSize > oldSize) {
            for (int i = oldSize; i < newSize; i++) {
                SerializedProperty element = arrayProperty.GetArrayElementAtIndex(i);
                if (typeof(T) == typeof(string)) { 
                    element.stringValue = defaultValue as string;
                } else { 
                    element.objectReferenceValue = defaultValue as UnityEngine.Object;
                }
            }
        }
    }

    private void ResizeSerializedArrayValueType<T>(SerializedProperty arrayProperty, int newSize, T defaultValue) where T : struct
    {
        int oldSize = arrayProperty.arraySize;
        arrayProperty.arraySize = newSize;
        if (newSize > oldSize) {
            for (int i = oldSize; i < newSize; i++) {
                SerializedProperty element = arrayProperty.GetArrayElementAtIndex(i);
                if (typeof(T) == typeof(bool)) element.boolValue = (bool)(object)defaultValue;
                else if (typeof(T) == typeof(int)) element.intValue = (int)(object)defaultValue;
                else if (typeof(T) == typeof(float)) element.floatValue = (float)(object)defaultValue;
                else if (typeof(T) == typeof(Vector2)) element.vector2Value = (Vector2)(object)defaultValue;
                else if (typeof(T) == typeof(Vector3)) element.vector3Value = (Vector3)(object)defaultValue;
                else if (typeof(T) == typeof(Color)) element.colorValue = (Color)(object)defaultValue;
                // Ensure McBlockTextureMappingType and BlockVisibilityType are handled if they are struct enums
                // For int-backed enums, the int case above should suffice.
            }
        }
    }
}
#endif
