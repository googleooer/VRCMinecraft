#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic; // For List<AudioClip>

[CustomEditor(typeof(McBlockTypeManager))]
public class McBlockTypeManagerEditor : Editor
{
    private McBlockTypeManager manager;
    private SerializedObject soTarget;

    private SerializedProperty previewTextureArrayProp;

    private SerializedProperty numberOfBlockTypesProp;
    private SerializedProperty blockNamesProp;
    private SerializedProperty isSolidDataProp;
    private SerializedProperty blockVisibilityTypeDataProp; 
    private SerializedProperty blockShapeTypeDataProp; 
    
    private SerializedProperty uv_allFacesDataProp;
    private SerializedProperty uv_topFaceDataProp;
    private SerializedProperty uv_bottomFaceDataProp;
    private SerializedProperty uv_sideFacesDataProp;
    private SerializedProperty textureMappingTypeDataProp; 

    private SerializedProperty breakParticlesPrefabDataProp;
    private SerializedProperty placeParticlesPrefabDataProp;

    private SerializedProperty fallbackBreakSoundsProp;
    private SerializedProperty fallbackPlaceSoundsProp;
    private SerializedProperty fallbackFootstepSoundsProp;

    private bool[] foldoutStates; 
    private Texture2D singleSlicePreviewTexture; 
    private const int ATLAS_GRID_WIDTH = 16; 
    private const float PICKER_SLICE_SIZE = 40f; 
    private const float PICKER_SLICE_PADDING = 2f;


    // --- Inner class for the Texture Slice Picker Popup ---
    private class TextureSlicePickerPopup : PopupWindowContent
    {
        private McBlockTypeManager managerInstance;
        private SerializedProperty targetSliceIndexProperty;
        private Texture2DArray textureArray;
        private Texture2D tempSliceTexture; 
        private Vector2 scrollPosition;
        private int initialSliceIndex;

        public TextureSlicePickerPopup(McBlockTypeManager manager, SerializedProperty sliceIndexProp)
        {
            this.managerInstance = manager;
            this.targetSliceIndexProperty = sliceIndexProp;
            this.textureArray = manager.previewTextureArray; 
            this.initialSliceIndex = sliceIndexProp.intValue;
            this.tempSliceTexture = null; // Initialize as null

            if (textureArray != null && textureArray.width > 0 && textureArray.height > 0)
            {
                // Always use RGBA32 for the temporary destination texture for Graphics.CopyTexture.
                // This is a known good, uncompressed format, generally safe as a destination.
                try {
                    tempSliceTexture = new Texture2D(textureArray.width, textureArray.height, TextureFormat.RGBA32, false); // false for mipChain
                    tempSliceTexture.filterMode = FilterMode.Point;
                } catch (System.Exception ex) { 
                    Debug.LogError($"[TextureSlicePickerPopup] Error creating temp Texture2D with RGBA32: {ex.Message}");
                    tempSliceTexture = null; // Ensure it's null if creation fails
                }
            } else {
                 if(managerInstance != null && managerInstance.enableVerboseLogging) Debug.LogWarning("[TextureSlicePickerPopup] TextureArray is null or has zero dimensions. Cannot create tempSliceTexture.");
                 tempSliceTexture = null; // Ensure it's null
            }
        }

        public override Vector2 GetWindowSize()
        {
            float width = ATLAS_GRID_WIDTH * (PICKER_SLICE_SIZE + PICKER_SLICE_PADDING) + EditorGUIUtility.standardVerticalSpacing * 2 + 30f; 
            float height = Mathf.Min(ATLAS_GRID_WIDTH * (PICKER_SLICE_SIZE + PICKER_SLICE_PADDING) + EditorGUIUtility.standardVerticalSpacing * 4 + EditorGUIUtility.singleLineHeight * 2, 450f); 
            return new Vector2(width, height);
        }

        public override void OnGUI(Rect rect)
        {
            if (textureArray == null) {
                EditorGUILayout.LabelField("Texture Array not assigned in Manager.");
                return;
            }
            // More robust check for tempSliceTexture validity
            if (tempSliceTexture == null || tempSliceTexture.GetNativeTexturePtr() == System.IntPtr.Zero || tempSliceTexture.width == 0 || tempSliceTexture.height == 0) 
            {
                EditorGUILayout.LabelField("Error: Could not initialize temporary texture for picker (null, native error, or zero dimensions).");
                return;
            }

            EditorGUILayout.LabelField("Select a Texture Slice", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox($"Current Selection: XY({initialSliceIndex % ATLAS_GRID_WIDTH}, {initialSliceIndex / ATLAS_GRID_WIDTH}) Index: {initialSliceIndex}", MessageType.Info);
            EditorGUILayout.Space();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            
            for (int y = 0; y < ATLAS_GRID_WIDTH; y++)
            {
                EditorGUILayout.BeginHorizontal();
                for (int x = 0; x < ATLAS_GRID_WIDTH; x++)
                {
                    int sliceIndex = y * ATLAS_GRID_WIDTH + x;
                    if (sliceIndex < textureArray.depth)
                    {
                        // Ensure tempSliceTexture is still valid before copying
                        if (tempSliceTexture == null || tempSliceTexture.GetNativeTexturePtr() == System.IntPtr.Zero) {
                            Debug.LogError("[TextureSlicePicker] tempSliceTexture became invalid before Graphics.CopyTexture.");
                            EditorGUILayout.LabelField("Error drawing slice.", GUILayout.Width(PICKER_SLICE_SIZE), GUILayout.Height(PICKER_SLICE_SIZE));
                            GUILayout.Space(PICKER_SLICE_PADDING);
                            continue;
                        }

                        try {
                             Graphics.CopyTexture(textureArray, sliceIndex, 0, tempSliceTexture, 0, 0);
                        } catch (System.Exception e) { // Catch System.Exception for broader error capture
                            Color[] colors = new Color[tempSliceTexture.width * tempSliceTexture.height];
                            for(int c=0; c < colors.Length; c++) colors[c] = Color.gray;
                            tempSliceTexture.SetPixels(colors);
                            tempSliceTexture.Apply();
                            if(managerInstance != null && managerInstance.enableVerboseLogging) Debug.LogWarning($"[TextureSlicePicker] Error copying texture slice {sliceIndex} for preview: {e.Message}. Ensure Texture Array has Read/Write enabled if necessary, or formats are compatible for Graphics.CopyTexture.");
                        }

                        GUIStyle buttonStyle = new GUIStyle(GUI.skin.button);
                        bool isSelected = (sliceIndex == initialSliceIndex);
                        
                        Color oldBg = GUI.backgroundColor;
                        if (isSelected) GUI.backgroundColor = Color.yellow;
                        
                        if (GUILayout.Button(new GUIContent(tempSliceTexture, $"Pick XY({x},{y}) Index: {sliceIndex}"), buttonStyle, GUILayout.Width(PICKER_SLICE_SIZE), GUILayout.Height(PICKER_SLICE_SIZE)))
                        {
                             targetSliceIndexProperty.intValue = sliceIndex;
                             targetSliceIndexProperty.serializedObject.ApplyModifiedProperties();
                             if (managerInstance != null) EditorUtility.SetDirty(managerInstance);
                             editorWindow.Close(); 
                        }
                        if(isSelected) GUI.backgroundColor = oldBg;
                    }
                    else
                    {
                        GUI.enabled = false;
                        GUILayout.Button("", GUILayout.Width(PICKER_SLICE_SIZE), GUILayout.Height(PICKER_SLICE_SIZE));
                        GUI.enabled = true;
                    }
                     GUILayout.Space(PICKER_SLICE_PADDING);
                }
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(PICKER_SLICE_PADDING);
            }
            EditorGUILayout.EndScrollView();
        }

        public override void OnClose()
        {
            if (tempSliceTexture != null)
            {
                Object.DestroyImmediate(tempSliceTexture);
                tempSliceTexture = null; 
            }
        }
    }
    // --- End of Popup Inner Class ---


    void OnEnable()
    {
        manager = (McBlockTypeManager)target;
        soTarget = serializedObject; 

        previewTextureArrayProp = soTarget.FindProperty("previewTextureArray");
        numberOfBlockTypesProp = soTarget.FindProperty("numberOfBlockTypes");
        blockNamesProp = soTarget.FindProperty("blockNames");
        isSolidDataProp = soTarget.FindProperty("isSolidData");
        blockVisibilityTypeDataProp = soTarget.FindProperty("blockVisibilityTypeData");
        blockShapeTypeDataProp = soTarget.FindProperty("blockShapeTypeData"); 
        
        uv_allFacesDataProp = soTarget.FindProperty("uv_allFacesData"); 
        uv_topFaceDataProp = soTarget.FindProperty("uv_topFaceData"); 
        uv_bottomFaceDataProp = soTarget.FindProperty("uv_bottomFaceData"); 
        uv_sideFacesDataProp = soTarget.FindProperty("uv_sideFacesData"); 
        textureMappingTypeDataProp = soTarget.FindProperty("textureMappingTypeData");

        breakParticlesPrefabDataProp = soTarget.FindProperty("breakParticlesPrefabData");
        placeParticlesPrefabDataProp = soTarget.FindProperty("placeParticlesPrefabData");

        fallbackBreakSoundsProp = soTarget.FindProperty("fallbackBreakSounds");
        fallbackPlaceSoundsProp = soTarget.FindProperty("fallbackPlaceSounds");
        fallbackFootstepSoundsProp = soTarget.FindProperty("fallbackFootstepSounds");

        ValidateSerializedProperties(); 

        int currentNumberOfBlockTypes = 0;
        if (numberOfBlockTypesProp != null) 
        {
            currentNumberOfBlockTypes = numberOfBlockTypesProp.intValue;
        }
        if (currentNumberOfBlockTypes < 0) currentNumberOfBlockTypes = 0;


        if (foldoutStates == null || foldoutStates.Length != currentNumberOfBlockTypes)
        {
            foldoutStates = new bool[currentNumberOfBlockTypes];
        }
        
        InitializeSingleSlicePreviewTexture();
    }

    private void InitializeSingleSlicePreviewTexture() {
        if (singleSlicePreviewTexture != null) Object.DestroyImmediate(singleSlicePreviewTexture);
        singleSlicePreviewTexture = null; 

        if (manager != null && manager.previewTextureArray != null) {
            if (manager.previewTextureArray.width > 0 && manager.previewTextureArray.height > 0) {
                // Use RGBA32 for singleSlicePreviewTexture as well for consistency and safety as a CopyTexture destination
                try {
                    singleSlicePreviewTexture = new Texture2D(manager.previewTextureArray.width, manager.previewTextureArray.height, TextureFormat.RGBA32, false);
                    singleSlicePreviewTexture.filterMode = FilterMode.Point;
                } catch (System.Exception ex) { 
                    Debug.LogWarning($"[McBlockTypeManagerEditor] Error creating singleSlicePreviewTexture with RGBA32: {ex.Message}. Using fallback.");
                    try {
                        singleSlicePreviewTexture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
                        singleSlicePreviewTexture.filterMode = FilterMode.Point;
                    } catch (System.Exception innerEx) { 
                         Debug.LogError($"[McBlockTypeManagerEditor] Fallback singleSlicePreviewTexture creation also failed: {innerEx.Message}");
                         singleSlicePreviewTexture = null; 
                    }
                }
            } else {
                if(manager != null && manager.enableVerboseLogging) Debug.LogWarning("[McBlockTypeManagerEditor] Preview Texture Array has zero width or height. Cannot create single slice preview texture.");
            }
        }
    }


    private void ValidateSerializedProperties()
    {
        if (previewTextureArrayProp == null) Debug.LogError("[McBlockTypeManagerEditor] Failed to find SP: previewTextureArray");
        if (numberOfBlockTypesProp == null) Debug.LogError("[McBlockTypeManagerEditor] Failed to find SP: numberOfBlockTypes");
        // ... (other property validations)
    }


    void OnDisable() 
    {
        if (singleSlicePreviewTexture != null)
        {
            Object.DestroyImmediate(singleSlicePreviewTexture);
            singleSlicePreviewTexture = null;
        }
    }

    public override void OnInspectorGUI()
    {
        soTarget.Update();

        if (numberOfBlockTypesProp == null || blockNamesProp == null /* ... other critical SPs ... */)
        {
            EditorGUILayout.HelpBox("Core SerializedProperties missing. Check console.", MessageType.Error);
            if (GUILayout.Button("Re-initialize Editor")) OnEnable();
            return;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Block Type Definitions Manager", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Configure per-block properties.", MessageType.Info);
        EditorGUILayout.Space();

        if (previewTextureArrayProp != null)
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(previewTextureArrayProp, new GUIContent("Preview Texture Array"));
            if (EditorGUI.EndChangeCheck()) {
                soTarget.ApplyModifiedProperties(); 
                InitializeSingleSlicePreviewTexture(); 
            }
            EditorGUILayout.Space();
        }

        EditorGUILayout.LabelField("Block Type Configuration", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(numberOfBlockTypesProp);
        if(EditorGUI.EndChangeCheck()){
            if(numberOfBlockTypesProp.intValue < 0) numberOfBlockTypesProp.intValue = 0;
            int currentNum = numberOfBlockTypesProp.intValue;
            if(foldoutStates == null || foldoutStates.Length != currentNum) {
                foldoutStates = new bool[currentNum];
            }
        }
        
        if (GUILayout.Button("Apply Number of Types & Resize Arrays"))
        {
            ResizeAllArrays(numberOfBlockTypesProp.intValue);
            foldoutStates = new bool[numberOfBlockTypesProp.intValue < 0 ? 0 : numberOfBlockTypesProp.intValue];
        }
        
        EditorGUILayout.Space(); 
        GUI.backgroundColor = new Color(1f, 0.6f, 0.6f); 
        if (GUILayout.Button("Reset All Block Types to Zero (Irreversible!)"))
        {
            if (EditorUtility.DisplayDialog("Confirm Reset", "Reset all block types to zero?", "Yes, Reset All", "Cancel"))
            {
                numberOfBlockTypesProp.intValue = 0;
                soTarget.ApplyModifiedProperties(); 
                ResizeAllArrays(0);
                foldoutStates = new bool[0];
            }
        }
        GUI.backgroundColor = Color.white; 
        EditorGUILayout.Space(); 

        EditorGUILayout.LabelField("Fallback Audio", EditorStyles.boldLabel);
        if(fallbackBreakSoundsProp != null) EditorGUILayout.PropertyField(fallbackBreakSoundsProp, true);
        if(fallbackPlaceSoundsProp != null) EditorGUILayout.PropertyField(fallbackPlaceSoundsProp, true);
        if(fallbackFootstepSoundsProp != null) EditorGUILayout.PropertyField(fallbackFootstepSoundsProp, true);
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Block Properties (per ID):", EditorStyles.boldLabel);
        int currentNumberOfBlockTypes = numberOfBlockTypesProp.intValue;
        if (currentNumberOfBlockTypes < 0) currentNumberOfBlockTypes = 0;

        if (foldoutStates == null || foldoutStates.Length != currentNumberOfBlockTypes) {
             foldoutStates = new bool[currentNumberOfBlockTypes]; 
        }
        
        bool overallMismatch = CheckOverallMismatch(currentNumberOfBlockTypes);
        if (overallMismatch) {
            EditorGUILayout.HelpBox("Array sizes mismatch 'Number Of Block Types'. Click 'Apply Number of Types & Resize Arrays'.", MessageType.Warning);
        }
        
        for (int i = 0; i < currentNumberOfBlockTypes; i++)
        {
            if (i >= foldoutStates.Length) break; 

            bool canDisplayBlockProperties = CheckCanDisplayProperties(i); 

            string foldoutLabel;
            if (canDisplayBlockProperties && blockNamesProp != null && i < blockNamesProp.arraySize)
            {
                SerializedProperty nameProp = blockNamesProp.GetArrayElementAtIndex(i);
                foldoutLabel = nameProp.stringValue;
                if (string.IsNullOrEmpty(foldoutLabel)) foldoutLabel = $"Block ID: {i} (Unnamed)";
                else foldoutLabel = $"{foldoutLabel} (ID: {i})";
            }
            else
            {
                foldoutLabel = $"Block ID: {i} (Data Array Mismatch!)";
            }

            foldoutStates[i] = EditorGUILayout.Foldout(foldoutStates[i], foldoutLabel, true, EditorStyles.foldoutHeader);

            if (foldoutStates[i])
            {
                if (canDisplayBlockProperties)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField("General", EditorStyles.boldLabel);
                    if (blockNamesProp != null && i < blockNamesProp.arraySize) EditorGUILayout.PropertyField(blockNamesProp.GetArrayElementAtIndex(i), new GUIContent("Name"));
                    if (isSolidDataProp != null && i < isSolidDataProp.arraySize) EditorGUILayout.PropertyField(isSolidDataProp.GetArrayElementAtIndex(i), new GUIContent("Is Solid"));
                    
                    if (blockVisibilityTypeDataProp != null && i < blockVisibilityTypeDataProp.arraySize) {
                        SerializedProperty visibilityTypeProp = blockVisibilityTypeDataProp.GetArrayElementAtIndex(i);
                        visibilityTypeProp.intValue = (int)(BlockVisibilityType)EditorGUILayout.EnumPopup(new GUIContent("Visibility Type"), (BlockVisibilityType)visibilityTypeProp.intValue);
                    }
                    if (blockShapeTypeDataProp != null && i < blockShapeTypeDataProp.arraySize) {
                        SerializedProperty shapeTypeProp = blockShapeTypeDataProp.GetArrayElementAtIndex(i);
                        shapeTypeProp.intValue = (int)(McBlockShapeType)EditorGUILayout.EnumPopup(new GUIContent("Shape Type"), (McBlockShapeType)shapeTypeProp.intValue);
                    }
                    
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Texturing (Atlas XY Coords)", EditorStyles.boldLabel);
                    if (textureMappingTypeDataProp!= null && i < textureMappingTypeDataProp.arraySize)
                    {
                        SerializedProperty mappingTypeProp = textureMappingTypeDataProp.GetArrayElementAtIndex(i);
                        mappingTypeProp.intValue = (int)(McBlockTextureMappingType)EditorGUILayout.EnumPopup(new GUIContent("Texture Mapping"), (McBlockTextureMappingType)mappingTypeProp.intValue);
                        
                        McBlockTextureMappingType currentMappingType = (McBlockTextureMappingType)mappingTypeProp.intValue;
                        if (currentMappingType == McBlockTextureMappingType.AllFacesSame)
                        {
                            if (uv_allFacesDataProp != null && i < uv_allFacesDataProp.arraySize) DrawAtlasSlicePicker(uv_allFacesDataProp.GetArrayElementAtIndex(i), "All Faces");
                        }
                        else if (currentMappingType == McBlockTextureMappingType.TopBottomSides)
                        {
                            if (uv_topFaceDataProp != null && i < uv_topFaceDataProp.arraySize) DrawAtlasSlicePicker(uv_topFaceDataProp.GetArrayElementAtIndex(i), "Top Face");
                            if (uv_bottomFaceDataProp != null && i < uv_bottomFaceDataProp.arraySize) DrawAtlasSlicePicker(uv_bottomFaceDataProp.GetArrayElementAtIndex(i), "Bottom Face");
                            if (uv_sideFacesDataProp != null && i < uv_sideFacesDataProp.arraySize) DrawAtlasSlicePicker(uv_sideFacesDataProp.GetArrayElementAtIndex(i), "Side Faces");
                        }
                    }
                    
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Audio (Manually Handled)", EditorStyles.boldLabel);
                    if (manager != null && manager.breakSounds != null && i < manager.breakSounds.Length) manager.breakSounds[i] = DrawAudioClipArray(manager.breakSounds[i], "Break Sounds");
                    if (manager != null && manager.placeSounds != null && i < manager.placeSounds.Length) manager.placeSounds[i] = DrawAudioClipArray(manager.placeSounds[i], "Place Sounds");
                    if (manager != null && manager.footstepSounds != null && i < manager.footstepSounds.Length) manager.footstepSounds[i] = DrawAudioClipArray(manager.footstepSounds[i], "Footstep Sounds");
                    
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Particles", EditorStyles.boldLabel);
                    if (breakParticlesPrefabDataProp != null && i < breakParticlesPrefabDataProp.arraySize) EditorGUILayout.PropertyField(breakParticlesPrefabDataProp.GetArrayElementAtIndex(i), new GUIContent("Break Particles"));
                    if (placeParticlesPrefabDataProp != null && i < placeParticlesPrefabDataProp.arraySize) EditorGUILayout.PropertyField(placeParticlesPrefabDataProp.GetArrayElementAtIndex(i), new GUIContent("Place Particles"));

                    EditorGUI.indentLevel--;
                }
                else 
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.HelpBox("Properties for this block ID cannot be displayed. Resize arrays.", MessageType.Error);
                    EditorGUI.indentLevel--;
                }
            }
            EditorGUILayout.Separator();
        }
        
        if (GUI.changed) EditorUtility.SetDirty(manager);
        soTarget.ApplyModifiedProperties(); 
    }

    private bool CheckOverallMismatch(int currentNumberOfBlockTypes) {
        if (currentNumberOfBlockTypes <= 0) return false;
        if (blockNamesProp == null || blockNamesProp.arraySize != currentNumberOfBlockTypes) return true;
        // ... (other SP checks)
        if (manager == null) return true; 
        if (manager.breakSounds == null || manager.breakSounds.Length != currentNumberOfBlockTypes) return true;
        // ... (other jagged array checks)
        return false;
    }

     private bool CheckCanDisplayProperties(int index) {
        if (numberOfBlockTypesProp == null) return false; 
        int currentNumberOfBlockTypes = numberOfBlockTypesProp.intValue;
        if (index >= currentNumberOfBlockTypes) return false;

        // Check SerializedProperty arrays (ensure property itself is not null AND index is within bounds)
        if (blockNamesProp == null || index >= blockNamesProp.arraySize) return false;
        // ... (other SP checks) ...

        // Check manager's jagged arrays for outer dimension validity
        if (manager == null) return false; 
        if (manager.breakSounds == null || index >= manager.breakSounds.Length) return false;
        // ... (other jagged array checks) ...
        
        return true;
    }

    private void ResizeAllArrays(int newSize)
    {
        if (newSize < 0) newSize = 0; 
        Undo.RecordObject(manager, "Resize Block Type Arrays");

        if(blockNamesProp != null) ResizeSerializedArray<string>(blockNamesProp, newSize, "");
        // ... (resize other SP arrays with null checks) ...
        if(isSolidDataProp != null) ResizeSerializedArrayValueType<bool>(isSolidDataProp, newSize, true);
        if(blockVisibilityTypeDataProp != null) ResizeSerializedArrayValueType<int>(blockVisibilityTypeDataProp, newSize, (int)BlockVisibilityType.Opaque); 
        if(blockShapeTypeDataProp != null) ResizeSerializedArrayValueType<int>(blockShapeTypeDataProp, newSize, (int)McBlockShapeType.Cube); 
        if(uv_allFacesDataProp != null) ResizeSerializedArrayValueType<int>(uv_allFacesDataProp, newSize, 0); 
        if(uv_topFaceDataProp != null) ResizeSerializedArrayValueType<int>(uv_topFaceDataProp, newSize, 0); 
        if(uv_bottomFaceDataProp != null) ResizeSerializedArrayValueType<int>(uv_bottomFaceDataProp, newSize, 0); 
        if(uv_sideFacesDataProp != null) ResizeSerializedArrayValueType<int>(uv_sideFacesDataProp, newSize, 0); 
        if(textureMappingTypeDataProp != null) ResizeSerializedArrayValueType<int>(textureMappingTypeDataProp, newSize, (int)McBlockTextureMappingType.AllFacesSame);
        if(breakParticlesPrefabDataProp != null) ResizeSerializedArray<ParticleSystem>(breakParticlesPrefabDataProp, newSize, null);
        if(placeParticlesPrefabDataProp != null) ResizeSerializedArray<ParticleSystem>(placeParticlesPrefabDataProp, newSize, null);


        if (manager != null) 
        {
            manager.breakSounds = ResizeJaggedArrayManual(manager.breakSounds, newSize);
            manager.placeSounds = ResizeJaggedArrayManual(manager.placeSounds, newSize);
            manager.footstepSounds = ResizeJaggedArrayManual(manager.footstepSounds, newSize);
        }


        if (blockNamesProp != null && blockNamesProp.arraySize == newSize) {
            for (int i = 0; i < newSize; i++) {
                SerializedProperty nameElement = blockNamesProp.GetArrayElementAtIndex(i);
                if (nameElement != null && nameElement.propertyType == SerializedPropertyType.String && string.IsNullOrEmpty(nameElement.stringValue)) {
                    nameElement.stringValue = $"Block_{i}";
                }
            }
        }

        if (manager != null) EditorUtility.SetDirty(manager); 
        soTarget.ApplyModifiedProperties(); 
    }

     private AudioClip[][] ResizeJaggedArrayManual(AudioClip[][] currentArray, int newOuterSize)
    {
        AudioClip[][] newArray = new AudioClip[newOuterSize][];
        int oldOuterSize = (currentArray != null) ? currentArray.Length : 0;
        for (int i = 0; i < newOuterSize; i++)
        {
            if (i < oldOuterSize && currentArray[i] != null) newArray[i] = currentArray[i]; 
            else newArray[i] = new AudioClip[0]; 
        }
        return newArray;
    }

    private void ResizeSerializedArray<T>(SerializedProperty arrayProperty, int newSize, T defaultValue) where T : class
    {
        if (arrayProperty == null) return; 
        int oldSize = arrayProperty.arraySize;
        arrayProperty.arraySize = newSize; 
        if (newSize > oldSize) {
            for (int i = oldSize; i < newSize; i++) {
                SerializedProperty element = arrayProperty.GetArrayElementAtIndex(i);
                if (element == null) continue;
                if (typeof(T) == typeof(string)) element.stringValue = defaultValue as string; 
                else element.objectReferenceValue = defaultValue as UnityEngine.Object;
            }
        }
    }

    private void ResizeSerializedArrayValueType<T>(SerializedProperty arrayProperty, int newSize, T defaultValue) where T : struct
    {
        if (arrayProperty == null) return; 
        int oldSize = arrayProperty.arraySize;
        arrayProperty.arraySize = newSize;
        if (newSize > oldSize) {
            for (int i = oldSize; i < newSize; i++) {
                SerializedProperty element = arrayProperty.GetArrayElementAtIndex(i);
                if (element == null) continue;
                if (typeof(T) == typeof(bool)) element.boolValue = (bool)(object)defaultValue;
                else if (typeof(T) == typeof(int)) element.intValue = (int)(object)defaultValue;
            }
        }
    }
     private AudioClip[] DrawAudioClipArray(AudioClip[] clips, string label)
    {
        if (manager == null) return clips; 

        EditorGUI.BeginChangeCheck();

        EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
        EditorGUI.indentLevel++;

        if (clips == null) clips = new AudioClip[0]; 

        int newSize = EditorGUILayout.IntField("Size", clips.Length);
        if (newSize < 0) newSize = 0;

        if (newSize != clips.Length)
        {
            AudioClip[] resizedClips = new AudioClip[newSize];
            for (int j = 0; j < Mathf.Min(newSize, clips.Length); j++)
            {
                resizedClips[j] = clips[j];
            }
            clips = resizedClips; 
        }

        for (int j = 0; j < clips.Length; j++)
        {
            clips[j] = (AudioClip)EditorGUILayout.ObjectField($"Element {j}", clips[j], typeof(AudioClip), false);
        }
        EditorGUI.indentLevel--;

        if(EditorGUI.EndChangeCheck()) {
            EditorUtility.SetDirty(manager); 
        }
        return clips; 
    }


    private void DrawAtlasSlicePicker(SerializedProperty sliceIndexProp, string baseLabel = "Atlas Coords")
    {
        if (sliceIndexProp == null) return; 

        EditorGUILayout.BeginHorizontal();

        int currentSliceIndex = sliceIndexProp.intValue;
        int displayX = currentSliceIndex % ATLAS_GRID_WIDTH;
        int displayY = currentSliceIndex / ATLAS_GRID_WIDTH;
        
        EditorGUILayout.LabelField(new GUIContent(baseLabel, $"Current Raw Slice Index: {currentSliceIndex}"), new GUIContent($"XY: ({displayX}, {displayY})"), GUILayout.MinWidth(150));

        if (GUILayout.Button("Pick", GUILayout.Width(60)))
        {
            if (manager != null && manager.previewTextureArray != null)
            {
                Rect buttonRect = GUILayoutUtility.GetLastRect();
                buttonRect = GUIUtility.GUIToScreenRect(buttonRect); 

                PopupWindow.Show(buttonRect, new TextureSlicePickerPopup(manager, sliceIndexProp));
            }
            else
            {
                Debug.LogWarning("[McBlockTypeManagerEditor] Cannot open slice picker: Preview Texture Array is not assigned in the manager.");
            }
        }
        
        if (manager != null && manager.previewTextureArray != null && singleSlicePreviewTexture != null && 
            currentSliceIndex >= 0 && currentSliceIndex < manager.previewTextureArray.depth)
        {
            try {
                if (singleSlicePreviewTexture.width != manager.previewTextureArray.width || singleSlicePreviewTexture.height != manager.previewTextureArray.height) {
                    Object.DestroyImmediate(singleSlicePreviewTexture);
                    InitializeSingleSlicePreviewTexture(); 
                }

                if (singleSlicePreviewTexture != null) { 
                     Graphics.CopyTexture(manager.previewTextureArray, currentSliceIndex, 0, singleSlicePreviewTexture, 0, 0);
                     EditorGUI.DrawPreviewTexture(GUILayoutUtility.GetRect(32, 32, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false)), singleSlicePreviewTexture, null, ScaleMode.StretchToFill); 
                } else {
                     EditorGUI.DrawPreviewTexture(GUILayoutUtility.GetRect(32, 32, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false)), Texture2D.grayTexture, null, ScaleMode.StretchToFill);
                }
            } catch (System.Exception e) { // Catch System.Exception for broader error capture
                EditorGUI.DrawPreviewTexture(GUILayoutUtility.GetRect(32, 32, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false)), Texture2D.grayTexture, null, ScaleMode.StretchToFill);
                if(manager != null && manager.enableVerboseLogging) Debug.LogWarning($"[McBlockTypeManagerEditor] Could not copy texture for preview (Slice: {currentSliceIndex}). Error: {e.Message}.");
            }
        }
        else
        {
            EditorGUI.DrawPreviewTexture(GUILayoutUtility.GetRect(32, 32, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false)), Texture2D.grayTexture, null, ScaleMode.StretchToFill);
        }
        EditorGUILayout.EndHorizontal();
    }
}
#endif

