#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic; // For using List<T>

[CustomEditor(typeof(McStructureTemplate))]
public class McStructureTemplateEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector(); // Draws all public fields from McStructureTemplate

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("Use the button below to bake the voxel data from this prefab's children. " +
                                "Each child GameObject that represents a voxel MUST have the 'McStructureVoxelData' script attached, " +
                                "with its 'Block ID' correctly set.", MessageType.Info);

        McStructureTemplate template = (McStructureTemplate)target;

        if (GUILayout.Button("Bake Structure From Children To Data Arrays"))
        {
            BakeStructureData(template);
        }
        
        // Display info about the baked data
        if (template.bakedVoxelPositions != null && template.bakedVoxelBlockIDs != null && template.bakedVoxelPositions.Length > 0)
        {
            EditorGUILayout.LabelField("Baked Data Status:", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"  - Voxel Count: {template.bakedVoxelPositions.Length}");
            if (template.bakedVoxelPositions.Length != template.bakedVoxelBlockIDs.Length) {
                EditorGUILayout.HelpBox("Warning: Baked positions and block IDs array lengths do not match! Re-bake.", MessageType.Error);
            }
            // Optionally, list a few baked voxels for verification
            // for (int i = 0; i < Mathf.Min(template.bakedVoxelPositions.Length, 5); i++) {
            //    EditorGUILayout.LabelField($"    Voxel {i}: Pos={template.bakedVoxelPositions[i]}, ID={template.bakedVoxelBlockIDs[i]}");
            // }
        } else {
            EditorGUILayout.HelpBox("This structure has not been baked yet or contains no voxel data. " +
                                   "Ensure child GameObjects have 'McStructureVoxelData' and then click 'Bake'.", MessageType.Warning);
        }
    }

    private void BakeStructureData(McStructureTemplate template)
    {
        Transform templateRoot = template.transform;
        if (templateRoot.childCount == 0 && template.GetComponentsInChildren<McStructureVoxelData>(true).Length == 0) // Check deeper too
        {
            Debug.LogWarning($"[McStructureTemplateEditor] No child objects with McStructureVoxelData found in '{template.structureName}'. Nothing to bake.", template.gameObject);
            template.bakedVoxelPositions = new Vector3Int[0]; // Ensure arrays are empty if nothing found
            template.bakedVoxelBlockIDs = new byte[0];
            EditorUtility.SetDirty(template);
            return;
        }

        Undo.RecordObject(template, "Bake Voxel Structure Data");

        List<Vector3Int> positions = new List<Vector3Int>();
        List<byte> blockIDs = new List<byte>();

        // GetComponentsInChildren will find McStructureVoxelData on any child, direct or nested.
        // This is often desired for complex prefabs.
        McStructureVoxelData[] childVoxels = template.GetComponentsInChildren<McStructureVoxelData>(true); // Include inactive

        if (childVoxels.Length == 0) {
            Debug.LogWarning($"[McStructureTemplateEditor] No 'McStructureVoxelData' components found in children of '{template.structureName}'. Make sure your visual blocks have this script.", template.gameObject);
            template.bakedVoxelPositions = new Vector3Int[0];
            template.bakedVoxelBlockIDs = new byte[0];
            EditorUtility.SetDirty(template);
            return;
        }

        Debug.Log($"[McStructureTemplateEditor] Found {childVoxels.Length} McStructureVoxelData components in children of '{template.structureName}'. Baking now...");

        for (int i = 0; i < childVoxels.Length; i++)
        {
            McStructureVoxelData voxelDataScript = childVoxels[i];
            Transform childTransform = voxelDataScript.transform;

            // Calculate position relative to the root template object's transform.
            // This ensures that if the root prefab itself is moved/rotated, the relative positions remain correct.
            Vector3 relativePos = templateRoot.InverseTransformPoint(childTransform.position);

            // Round to nearest integer to get voxel grid coordinates relative to the structure's origin.
            Vector3Int voxelPos = new Vector3Int(
                Mathf.RoundToInt(relativePos.x),
                Mathf.RoundToInt(relativePos.y),
                Mathf.RoundToInt(relativePos.z)
            );
            
            positions.Add(voxelPos);
            blockIDs.Add(voxelDataScript.blockID);
            
            // Debug.Log($"  - Baked voxel {i}: RelPos={voxelPos}, BlockID={voxelDataScript.blockID}, ChildName='{childTransform.name}'");
        }

        template.bakedVoxelPositions = positions.ToArray();
        template.bakedVoxelBlockIDs = blockIDs.ToArray();

        // Mark the object (ScriptableObject or Prefab instance) as dirty to ensure changes are saved.
        // If 'template' is a component on a prefab instance in the scene, this marks the instance.
        // If 'template' is directly on a prefab asset, this marks the asset.
        EditorUtility.SetDirty(template);
        if (PrefabUtility.IsPartOfPrefabAsset(template.gameObject)) {
            // If it's a direct prefab asset, saving is handled by Unity.
        } else if (PrefabUtility.IsPartOfPrefabInstance(template.gameObject)) {
            // If it's an instance in the scene, you might need to apply overrides to the prefab asset
            // or remind the user to do so if they want the baked data saved back to the original prefab.
            // For simplicity, we assume user manages prefab applying if editing instances.
            // PrefabUtility.RecordPrefabInstancePropertyModifications(template); // More granular
        }

        Debug.Log($"[McStructureTemplateEditor] Successfully baked {positions.Count} voxels for '{template.structureName}'. Please ensure you apply these changes to your Prefab asset if you baked an instance in the scene.");
    }
}
#endif
