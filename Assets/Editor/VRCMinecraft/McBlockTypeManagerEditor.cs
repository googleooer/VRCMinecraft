#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditorInternal; // Required for ReorderableList
using System.Collections.Generic;

[CustomEditor(typeof(McBlockTypeManager))]
public class McBlockTypeManagerEditor : Editor
{
    private McBlockTypeManager manager;
    private SerializedObject soTarget;

    // --- Serialized Properties ---
    private SerializedProperty previewTextureArrayProp;
    private SerializedProperty finalDataArrayProp;
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

    // --- Editor State ---
    private ReorderableList blockTypesList;
    private bool[] foldoutStates; 
    private Texture2D singleSlicePreviewTexture; 
    private const int ATLAS_GRID_WIDTH = 16; 
    private const float PICKER_SLICE_SIZE = 40f; 
    private const float PICKER_SLICE_PADDING = 2f;

    // --- Inner class for the Texture Slice Picker Popup (Unchanged) ---
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
            this.tempSliceTexture = null;

            if (textureArray != null && textureArray.width > 0 && textureArray.height > 0)
            {
                try {
                    tempSliceTexture = new Texture2D(textureArray.width, textureArray.height, TextureFormat.RGBA32, false);
                    tempSliceTexture.filterMode = FilterMode.Point;
                } catch (System.Exception ex) { 
                    Debug.LogError($"[TextureSlicePickerPopup] Error creating temp Texture2D: {ex.Message}");
                    tempSliceTexture = null;
                }
            } else {
                 if(managerInstance != null && managerInstance.enableVerboseLogging) Debug.LogWarning("[TextureSlicePickerPopup] TextureArray is null or has zero dimensions.");
                 tempSliceTexture = null;
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
            if (tempSliceTexture == null) {
                EditorGUILayout.LabelField("Error: Could not initialize temp texture for picker.");
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
                        try {
                             Graphics.CopyTexture(textureArray, sliceIndex, 0, tempSliceTexture, 0, 0);
                        } catch (System.Exception e) {
                            Color[] colors = new Color[tempSliceTexture.width * tempSliceTexture.height];
                            for(int c=0; c < colors.Length; c++) colors[c] = Color.gray;
                            tempSliceTexture.SetPixels(colors);
                            tempSliceTexture.Apply();
                            if(managerInstance != null && managerInstance.enableVerboseLogging) Debug.LogWarning($"[TextureSlicePicker] Error copying texture slice {sliceIndex}: {e.Message}.");
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
        soTarget = new SerializedObject(manager); 

        // --- Find All Properties ---
        previewTextureArrayProp = soTarget.FindProperty("previewTextureArray");
        finalDataArrayProp = soTarget.FindProperty("finalDataArray");
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

        // --- Initialize ReorderableList ---
        blockTypesList = new ReorderableList(soTarget, blockNamesProp, true, true, true, true);
        
        blockTypesList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Block Properties (per ID)");
        blockTypesList.drawElementCallback = DrawListElement;
        blockTypesList.onAddCallback = AddNewBlockType;
        blockTypesList.onRemoveCallback = RemoveBlockType;
        blockTypesList.onReorderCallbackWithDetails = ReorderBlockTypes;
        blockTypesList.elementHeightCallback = GetElementHeight;
        
        // --- Sync Foldout States ---
        SyncFoldoutStates();
        InitializeSingleSlicePreviewTexture();
    }
    
    private void SyncFoldoutStates() {
        int currentNum = blockNamesProp.arraySize;
        if (foldoutStates == null || foldoutStates.Length != currentNum)
        {
            bool[] newStates = new bool[currentNum];
            if (foldoutStates != null)
            {
                System.Array.Copy(foldoutStates, newStates, Mathf.Min(foldoutStates.Length, newStates.Length));
            }
            foldoutStates = newStates;
        }
    }

    private void InitializeSingleSlicePreviewTexture() {
        if (singleSlicePreviewTexture != null) Object.DestroyImmediate(singleSlicePreviewTexture);
        singleSlicePreviewTexture = null; 

        if (manager != null && manager.previewTextureArray != null) {
            if (manager.previewTextureArray.width > 0 && manager.previewTextureArray.height > 0) {
                try {
                    singleSlicePreviewTexture = new Texture2D(manager.previewTextureArray.width, manager.previewTextureArray.height, TextureFormat.RGBA32, false);
                    singleSlicePreviewTexture.filterMode = FilterMode.Point;
                } catch (System.Exception ex) { 
                    Debug.LogWarning($"[McBlockTypeManagerEditor] Error creating singleSlicePreviewTexture: {ex.Message}");
                    singleSlicePreviewTexture = null;
                }
            } else {
                if(manager != null && manager.enableVerboseLogging) Debug.LogWarning("[McBlockTypeManagerEditor] Preview Texture Array has zero width or height.");
            }
        }
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

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Block Type Definitions Manager", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Configure per-block properties using the list below. Drag to reorder, then press 'Bake' to generate optimized runtime data.", MessageType.Info);
        EditorGUILayout.Space();

        // --- Management Buttons ---
        EditorGUILayout.BeginHorizontal();
        GUI.backgroundColor = new Color(0.7f, 1f, 0.8f); // Light green
        if (GUILayout.Button(new GUIContent("Bake Properties", "Processes the Editor-Only Source arrays into the optimized 'finalDataArray' for use in builds."), GUILayout.Height(30)))
        {
            if (EditorUtility.DisplayDialog("Confirm Bake", "This will overwrite the existing 'finalDataArray' with newly packed data from the source arrays. Are you sure?", "Yes, Bake Data", "Cancel"))
            {
                manager.EncodeDataForBuild();
                EditorUtility.SetDirty(manager);
                soTarget.Update();
            }
        }
        
        GUI.backgroundColor = new Color(1f, 0.8f, 0.8f); // Light red
        if (GUILayout.Button(new GUIContent("Force Sync Arrays", "Synchronizes the sizes of all data arrays. Use this to fix 'Array size mismatch' errors."), GUILayout.Height(30)))
        {
            if (EditorUtility.DisplayDialog("Confirm Sync", "This will force all data arrays to match the size of the 'Block Names' list. This can fix errors but may result in data loss if an array has shrunk. Are you sure?", "Yes, Sync Arrays", "Cancel"))
            {
                ForceSyncAllArrays();
            }
        }
        EditorGUILayout.EndHorizontal();
        GUI.backgroundColor = Color.white;
        EditorGUILayout.Space(10);

        // --- Runtime Data Display ---
        EditorGUILayout.LabelField("Runtime Data", EditorStyles.boldLabel);
        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.PropertyField(finalDataArrayProp, true);
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.Space(10);

        // --- Editor-Only Source Data ---
        EditorGUILayout.LabelField("Editor-Only Source Data", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(previewTextureArrayProp, new GUIContent("Preview Texture Array"));
        if (EditorGUI.EndChangeCheck()) {
            soTarget.ApplyModifiedProperties(); 
            InitializeSingleSlicePreviewTexture(); 
        }
        EditorGUILayout.Space();
        
        EditorGUILayout.LabelField("Fallback Audio", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(fallbackBreakSoundsProp, true);
        EditorGUILayout.PropertyField(fallbackPlaceSoundsProp, true);
        EditorGUILayout.PropertyField(fallbackFootstepSoundsProp, true);
        EditorGUILayout.Space();
        
        // --- Reorderable List ---
        if (CheckOverallMismatch(blockNamesProp.arraySize)) {
            EditorGUILayout.HelpBox("Source array sizes mismatch! Use the 'Force Sync Arrays' button to fix this.", MessageType.Error);
        }
        blockTypesList.DoLayoutList();
        
        if (GUI.changed) EditorUtility.SetDirty(manager);
        soTarget.ApplyModifiedProperties(); 
    }

    #region ReorderableList Callbacks
    private float GetElementHeight(int index)
    {
        if (foldoutStates == null || index >= foldoutStates.Length || !foldoutStates[index])
        {
            return EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
        }
        else
        {
            float height = EditorGUIUtility.singleLineHeight; // Foldout header
            // Add heights for all properties shown when unfolded
            height += (EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing) * 11; // General + Particles + Section Labels
            
            // Texture Mapping
            var mappingTypeProp = textureMappingTypeDataProp.GetArrayElementAtIndex(index);
            McBlockTextureMappingType currentMappingType = (McBlockTextureMappingType)mappingTypeProp.intValue;
            if (currentMappingType == McBlockTextureMappingType.AllFacesSame) height += 42f;
            else if (currentMappingType == McBlockTextureMappingType.TopBottomSides) height += 42f * 3;

            // Audio
            height += EditorGUIUtility.singleLineHeight; // "Audio" label
            for(int i = 0; i < 3; i++) {
                AudioClip[][] audioArray = (i == 0) ? manager.breakSounds : (i == 1) ? manager.placeSounds : manager.footstepSounds;
                height += EditorGUIUtility.singleLineHeight * 2; // "Size" field + label
                if (audioArray != null && index < audioArray.Length && audioArray[index] != null) {
                    height += (EditorGUIUtility.singleLineHeight) * audioArray[index].Length;
                }
            }
            height += EditorGUIUtility.standardVerticalSpacing * 15;
            return height;
        }
    }

    private void DrawListElement(Rect rect, int index, bool isActive, bool isFocused)
    {
        if (index >= blockNamesProp.arraySize) return;
        SyncFoldoutStates();

        string foldoutLabel;
        if (blockNamesProp != null && index < blockNamesProp.arraySize)
        {
            foldoutLabel = $"{blockNamesProp.GetArrayElementAtIndex(index).stringValue} (ID: {index})";
        }
        else
        {
            foldoutLabel = $"Block ID: {index} (Data Array Mismatch!)";
        }
        
        Rect foldoutRect = new Rect(rect.x, rect.y, rect.width, EditorGUIUtility.singleLineHeight);
        foldoutStates[index] = EditorGUI.Foldout(foldoutRect, foldoutStates[index], foldoutLabel, true, EditorStyles.foldoutHeader);

        if (foldoutStates[index])
        {
            Rect propertyRect = new Rect(rect.x, rect.y + EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing, rect.width, EditorGUIUtility.singleLineHeight);
            
            if (CheckCanDisplayProperties(index))
            {
                EditorGUI.indentLevel++;
                
                DrawLabel(ref propertyRect, "General");
                DrawProperty(ref propertyRect, blockNamesProp.GetArrayElementAtIndex(index), "Name");
                DrawProperty(ref propertyRect, isSolidDataProp.GetArrayElementAtIndex(index), "Is Solid");
                DrawEnumPopup<BlockVisibilityType>(ref propertyRect, blockVisibilityTypeDataProp.GetArrayElementAtIndex(index), "Visibility Type");
                DrawEnumPopup<McBlockShapeType>(ref propertyRect, blockShapeTypeDataProp.GetArrayElementAtIndex(index), "Shape Type");
                
                DrawLabel(ref propertyRect, "Texturing");
                DrawEnumPopup<McBlockTextureMappingType>(ref propertyRect, textureMappingTypeDataProp.GetArrayElementAtIndex(index), "Texture Mapping");

                var mappingTypeProp = textureMappingTypeDataProp.GetArrayElementAtIndex(index);
                McBlockTextureMappingType currentMappingType = (McBlockTextureMappingType)mappingTypeProp.intValue;
                if (currentMappingType == McBlockTextureMappingType.AllFacesSame)
                {
                    DrawAtlasSlicePicker(ref propertyRect, uv_allFacesDataProp.GetArrayElementAtIndex(index), "All Faces");
                }
                else if (currentMappingType == McBlockTextureMappingType.TopBottomSides)
                {
                    DrawAtlasSlicePicker(ref propertyRect, uv_topFaceDataProp.GetArrayElementAtIndex(index), "Top Face");
                    DrawAtlasSlicePicker(ref propertyRect, uv_bottomFaceDataProp.GetArrayElementAtIndex(index), "Bottom Face");
                    DrawAtlasSlicePicker(ref propertyRect, uv_sideFacesDataProp.GetArrayElementAtIndex(index), "Side Faces");
                }
                
                DrawLabel(ref propertyRect, "Audio");
                manager.breakSounds[index] = DrawAudioClipArray(ref propertyRect, manager.breakSounds[index], "Break Sounds");
                manager.placeSounds[index] = DrawAudioClipArray(ref propertyRect, manager.placeSounds[index], "Place Sounds");
                manager.footstepSounds[index] = DrawAudioClipArray(ref propertyRect, manager.footstepSounds[index], "Footstep Sounds");

                DrawLabel(ref propertyRect, "Particles");
                DrawProperty(ref propertyRect, breakParticlesPrefabDataProp.GetArrayElementAtIndex(index), "Break Particles");
                DrawProperty(ref propertyRect, placeParticlesPrefabDataProp.GetArrayElementAtIndex(index), "Place Particles");

                EditorGUI.indentLevel--;
            }
            else
            {
                EditorGUI.HelpBox(new Rect(rect.x, propertyRect.y, rect.width, 40), "Properties for this block ID cannot be displayed. Array mismatch.", MessageType.Error);
            }
        }
    }

    private void AddNewBlockType(ReorderableList list)
    {
        int index = list.serializedProperty.arraySize;
        list.serializedProperty.InsertArrayElementAtIndex(index);

        blockNamesProp.GetArrayElementAtIndex(index).stringValue = $"Block_{index}";

        isSolidDataProp.arraySize = blockNamesProp.arraySize;
        isSolidDataProp.GetArrayElementAtIndex(index).boolValue = true;

        blockVisibilityTypeDataProp.arraySize = blockNamesProp.arraySize;
        blockVisibilityTypeDataProp.GetArrayElementAtIndex(index).intValue = (int)BlockVisibilityType.Opaque;

        blockShapeTypeDataProp.arraySize = blockNamesProp.arraySize;
        blockShapeTypeDataProp.GetArrayElementAtIndex(index).intValue = (int)McBlockShapeType.Cube;

        textureMappingTypeDataProp.arraySize = blockNamesProp.arraySize;
        textureMappingTypeDataProp.GetArrayElementAtIndex(index).intValue = (int)McBlockTextureMappingType.AllFacesSame;

        uv_allFacesDataProp.arraySize = blockNamesProp.arraySize;
        uv_allFacesDataProp.GetArrayElementAtIndex(index).intValue = 0;
        uv_topFaceDataProp.arraySize = blockNamesProp.arraySize;
        uv_topFaceDataProp.GetArrayElementAtIndex(index).intValue = 0;
        uv_bottomFaceDataProp.arraySize = blockNamesProp.arraySize;
        uv_bottomFaceDataProp.GetArrayElementAtIndex(index).intValue = 0;
        uv_sideFacesDataProp.arraySize = blockNamesProp.arraySize;
        uv_sideFacesDataProp.GetArrayElementAtIndex(index).intValue = 0;

        breakParticlesPrefabDataProp.arraySize = blockNamesProp.arraySize;
        breakParticlesPrefabDataProp.GetArrayElementAtIndex(index).objectReferenceValue = null;
        placeParticlesPrefabDataProp.arraySize = blockNamesProp.arraySize;
        placeParticlesPrefabDataProp.GetArrayElementAtIndex(index).objectReferenceValue = null;

        manager.breakSounds = ResizeJaggedArrayManual(manager.breakSounds, blockNamesProp.arraySize);
        manager.placeSounds = ResizeJaggedArrayManual(manager.placeSounds, blockNamesProp.arraySize);
        manager.footstepSounds = ResizeJaggedArrayManual(manager.footstepSounds, blockNamesProp.arraySize);

        numberOfBlockTypesProp.intValue = blockNamesProp.arraySize;
        SyncFoldoutStates();
        if (foldoutStates.Length > index)
        {
            foldoutStates[index] = true;
        }

        soTarget.ApplyModifiedProperties();

        ForceSyncAllArrays();
    }

    private void RemoveBlockType(ReorderableList list)
    {
        if (EditorUtility.DisplayDialog("Confirm Deletion", $"Are you sure you want to delete block ID {list.index} ({blockNamesProp.GetArrayElementAtIndex(list.index).stringValue})?", "Delete", "Cancel"))
        {
            RemoveElementFromAllArrays(list.index);
            SyncFoldoutStates();
        }
    }

    private void ReorderBlockTypes(ReorderableList list, int oldIndex, int newIndex)
    {
        // Reordering is handled automatically for the master property (blockNames)
        // We just need to move the elements in our other parallel arrays.
        isSolidDataProp.MoveArrayElement(oldIndex, newIndex);
        blockVisibilityTypeDataProp.MoveArrayElement(oldIndex, newIndex);
        blockShapeTypeDataProp.MoveArrayElement(oldIndex, newIndex);
        textureMappingTypeDataProp.MoveArrayElement(oldIndex, newIndex);
        uv_allFacesDataProp.MoveArrayElement(oldIndex, newIndex);
        uv_topFaceDataProp.MoveArrayElement(oldIndex, newIndex);
        uv_bottomFaceDataProp.MoveArrayElement(oldIndex, newIndex);
        uv_sideFacesDataProp.MoveArrayElement(oldIndex, newIndex);
        breakParticlesPrefabDataProp.MoveArrayElement(oldIndex, newIndex);
        placeParticlesPrefabDataProp.MoveArrayElement(oldIndex, newIndex);

        manager.breakSounds = ReorderJaggedArray(manager.breakSounds, oldIndex, newIndex);
        manager.placeSounds = ReorderJaggedArray(manager.placeSounds, oldIndex, newIndex);
        manager.footstepSounds = ReorderJaggedArray(manager.footstepSounds, oldIndex, newIndex);
        
        if (foldoutStates != null)
        {
            List<bool> foldoutList = new List<bool>(foldoutStates);
            bool item = foldoutList[oldIndex];
            foldoutList.RemoveAt(oldIndex);
            foldoutList.Insert(newIndex, item);
            foldoutStates = foldoutList.ToArray();
        }
        
        EditorUtility.SetDirty(manager);
    }
    #endregion
    
    #region Array Management
    private void ForceSyncAllArrays()
    {
        Undo.RecordObject(manager, "Force Sync Block Type Arrays");
        int masterSize = blockNamesProp.arraySize;
        
        ResizeSerializedArray(isSolidDataProp, masterSize, () => true);
        ResizeSerializedArray(blockVisibilityTypeDataProp, masterSize, () => (int)BlockVisibilityType.Opaque);
        ResizeSerializedArray(blockShapeTypeDataProp, masterSize, () => (int)McBlockShapeType.Cube);
        ResizeSerializedArray(textureMappingTypeDataProp, masterSize, () => (int)McBlockTextureMappingType.AllFacesSame);
        ResizeSerializedArray(uv_allFacesDataProp, masterSize, () => 0);
        ResizeSerializedArray(uv_topFaceDataProp, masterSize, () => 0);
        ResizeSerializedArray(uv_bottomFaceDataProp, masterSize, () => 0);
        ResizeSerializedArray(uv_sideFacesDataProp, masterSize, () => 0);
        ResizeSerializedArray<ParticleSystem>(breakParticlesPrefabDataProp, masterSize, () => null);
        ResizeSerializedArray<ParticleSystem>(placeParticlesPrefabDataProp, masterSize, () => null);
        
        manager.breakSounds = ResizeJaggedArrayManual(manager.breakSounds, masterSize);
        manager.placeSounds = ResizeJaggedArrayManual(manager.placeSounds, masterSize);
        manager.footstepSounds = ResizeJaggedArrayManual(manager.footstepSounds, masterSize);

        numberOfBlockTypesProp.intValue = masterSize;
        SyncFoldoutStates();
        soTarget.ApplyModifiedProperties();
        EditorUtility.SetDirty(manager);
        Debug.Log($"[McBlockTypeManagerEditor] All data arrays force-synchronized to size {masterSize}.");
    }

    private void ResizeSerializedArray<T>(SerializedProperty arrayProperty, int newSize, System.Func<T> defaultValueFactory)
    {
        int oldSize = arrayProperty.arraySize;
        if (oldSize == newSize) return;

        arrayProperty.arraySize = newSize;
        if (newSize > oldSize)
        {
            for (int i = oldSize; i < newSize; i++)
            {
                var element = arrayProperty.GetArrayElementAtIndex(i);
                object value = defaultValueFactory();
                if (element.propertyType == SerializedPropertyType.String) element.stringValue = (string)value;
                else if (element.propertyType == SerializedPropertyType.Boolean) element.boolValue = (bool)value;
                else if (element.propertyType == SerializedPropertyType.Integer) element.intValue = (int)value;
                else if (element.propertyType == SerializedPropertyType.ObjectReference) element.objectReferenceValue = (Object)value;
            }
        }
    }
    
    private bool CheckOverallMismatch(int count) {
        if (count < 0) count = 0;
        return blockNamesProp.arraySize != count || isSolidDataProp.arraySize != count ||
            blockVisibilityTypeDataProp.arraySize != count || blockShapeTypeDataProp.arraySize != count ||
            uv_allFacesDataProp.arraySize != count || uv_topFaceDataProp.arraySize != count ||
            uv_bottomFaceDataProp.arraySize != count || uv_sideFacesDataProp.arraySize != count ||
            textureMappingTypeDataProp.arraySize != count || (manager.breakSounds != null && manager.breakSounds.Length != count) ||
            (manager.placeSounds != null && manager.placeSounds.Length != count) || (manager.footstepSounds != null && manager.footstepSounds.Length != count) ||
            breakParticlesPrefabDataProp.arraySize != count || placeParticlesPrefabDataProp.arraySize != count;
    }

    private bool CheckCanDisplayProperties(int index) {
        int count = blockNamesProp.arraySize;
        if (index >= count) return false;
        return !(index >= blockNamesProp.arraySize || index >= isSolidDataProp.arraySize ||
            index >= blockVisibilityTypeDataProp.arraySize || index >= blockShapeTypeDataProp.arraySize ||
            index >= uv_allFacesDataProp.arraySize || index >= uv_topFaceDataProp.arraySize ||
            index >= uv_bottomFaceDataProp.arraySize || index >= uv_sideFacesDataProp.arraySize ||
            index >= textureMappingTypeDataProp.arraySize || (manager.breakSounds != null && index >= manager.breakSounds.Length) ||
            (manager.placeSounds != null && index >= manager.placeSounds.Length) || (manager.footstepSounds != null && index >= manager.footstepSounds.Length) ||
            index >= breakParticlesPrefabDataProp.arraySize || index >= placeParticlesPrefabDataProp.arraySize);
    }

    private void RemoveElementFromAllArrays(int index) {
        blockNamesProp.DeleteArrayElementAtIndex(index);
        isSolidDataProp.DeleteArrayElementAtIndex(index);
        blockVisibilityTypeDataProp.DeleteArrayElementAtIndex(index);
        blockShapeTypeDataProp.DeleteArrayElementAtIndex(index);
        textureMappingTypeDataProp.DeleteArrayElementAtIndex(index);
        uv_allFacesDataProp.DeleteArrayElementAtIndex(index);
        uv_topFaceDataProp.DeleteArrayElementAtIndex(index);
        uv_bottomFaceDataProp.DeleteArrayElementAtIndex(index);
        uv_sideFacesDataProp.DeleteArrayElementAtIndex(index);
        breakParticlesPrefabDataProp.DeleteArrayElementAtIndex(index);
        placeParticlesPrefabDataProp.DeleteArrayElementAtIndex(index);
        
        manager.breakSounds = RemoveFromJaggedArray(manager.breakSounds, index);
        manager.placeSounds = RemoveFromJaggedArray(manager.placeSounds, index);
        manager.footstepSounds = RemoveFromJaggedArray(manager.footstepSounds, index);
        
        numberOfBlockTypesProp.intValue = blockNamesProp.arraySize;
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

    private AudioClip[][] ReorderJaggedArray(AudioClip[][] source, int oldIndex, int newIndex)
    {
        if (source == null || oldIndex == newIndex || oldIndex < 0 || newIndex < 0 || oldIndex >= source.Length || newIndex >= source.Length) return source;
        AudioClip[] item = source[oldIndex];
        List<AudioClip[]> list = new List<AudioClip[]>(source);
        list.RemoveAt(oldIndex);
        list.Insert(newIndex, item);
        return list.ToArray();
    }

    private AudioClip[][] RemoveFromJaggedArray(AudioClip[][] source, int index)
    {
        if (source == null || index < 0 || index >= source.Length) return source;
        List<AudioClip[]> list = new List<AudioClip[]>(source);
        list.RemoveAt(index);
        return list.ToArray();
    }

    #endregion

    #region Drawing Helpers

    private void DrawLabel(ref Rect rect, string label) {
        EditorGUI.LabelField(rect, label, EditorStyles.boldLabel);
        rect.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
    }
    
    private void DrawProperty(ref Rect rect, SerializedProperty prop, string label) {
        EditorGUI.PropertyField(rect, prop, new GUIContent(label));
        rect.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
    }

    private void DrawEnumPopup<T>(ref Rect rect, SerializedProperty prop, string label) where T : System.Enum {
        prop.intValue = System.Convert.ToInt32(EditorGUI.EnumPopup(rect, new GUIContent(label), (T)System.Enum.ToObject(typeof(T), prop.intValue)));
        rect.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
    }

    private AudioClip[] DrawAudioClipArray(ref Rect rect, AudioClip[] clips, string label)
    {
        EditorGUI.LabelField(rect, label);
        rect.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

        EditorGUI.indentLevel++;
        Rect sizeRect = new Rect(rect.x, rect.y, rect.width, EditorGUIUtility.singleLineHeight);
        
        if (clips == null) clips = new AudioClip[0];
        int newSize = EditorGUI.IntField(sizeRect, "Size", clips.Length);
        if (newSize < 0) newSize = 0;
        rect.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

        if (newSize != clips.Length)
        {
            AudioClip[] resizedClips = new AudioClip[newSize];
            System.Array.Copy(clips, resizedClips, Mathf.Min(newSize, clips.Length));
            clips = resizedClips; 
        }

        for (int j = 0; j < clips.Length; j++)
        {
            Rect clipRect = new Rect(rect.x, rect.y, rect.width, EditorGUIUtility.singleLineHeight);
            clips[j] = (AudioClip)EditorGUI.ObjectField(clipRect, $"Element {j}", clips[j], typeof(AudioClip), false);
            rect.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
        }
        EditorGUI.indentLevel--;
        return clips; 
    }

    private void DrawAtlasSlicePicker(ref Rect rect, SerializedProperty sliceIndexProp, string baseLabel = "Atlas Coords")
    {
        Rect lineRect = new Rect(rect.x, rect.y, rect.width, 36);
        int currentSliceIndex = sliceIndexProp.intValue;
        
        Rect labelRect = new Rect(lineRect.x, lineRect.y + 8, EditorGUIUtility.labelWidth - 50, lineRect.height);
        EditorGUI.LabelField(labelRect, new GUIContent(baseLabel, $"Raw Index: {currentSliceIndex}"), new GUIContent($"XY: ({currentSliceIndex % ATLAS_GRID_WIDTH}, {currentSliceIndex / ATLAS_GRID_WIDTH})"));

        Rect buttonRect = new Rect(labelRect.xMax, lineRect.y + 4, 60, 28);
        if (GUI.Button(buttonRect, "Pick"))
        {
            if (manager.previewTextureArray != null)
            {
                PopupWindow.Show(buttonRect, new TextureSlicePickerPopup(manager, sliceIndexProp));
            }
            else
            {
                Debug.LogWarning("[McBlockTypeManagerEditor] Cannot open picker: Preview Texture Array is not assigned.");
            }
        }
        
        Rect previewRect = new Rect(buttonRect.xMax + 10, lineRect.y, 36, 36);
        if (manager.previewTextureArray != null && singleSlicePreviewTexture != null && currentSliceIndex >= 0 && currentSliceIndex < manager.previewTextureArray.depth)
        {
            try {
                if (singleSlicePreviewTexture.width != manager.previewTextureArray.width || singleSlicePreviewTexture.height != manager.previewTextureArray.height) {
                    InitializeSingleSlicePreviewTexture();
                }

                if (singleSlicePreviewTexture != null) { 
                     Graphics.CopyTexture(manager.previewTextureArray, currentSliceIndex, 0, singleSlicePreviewTexture, 0, 0);
                     EditorGUI.DrawPreviewTexture(previewRect, singleSlicePreviewTexture, null, ScaleMode.StretchToFill); 
                }
            } catch {
                EditorGUI.DrawPreviewTexture(previewRect, Texture2D.grayTexture, null, ScaleMode.StretchToFill);
            }
        }
        else
        {
            EditorGUI.DrawPreviewTexture(previewRect, Texture2D.grayTexture, null, ScaleMode.StretchToFill);
        }
        
        rect.y += 42;
    }
    #endregion
}
#endif
