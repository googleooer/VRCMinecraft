#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Text; // Added for StringBuilder

[CustomEditor(typeof(McStructureTemplate))]
public class McStructureTemplateEditor : Editor
{
    private StringBuilder logBuilder = new StringBuilder(256); // Initialize once

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector(); 

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("Use the button below to bake the voxel data from this prefab's children. " +
                                "Each child GameObject that represents a voxel MUST have the 'McStructureVoxelData' script attached, " +
                                "with its 'Block ID' correctly set.", MessageType.Info);

        McStructureTemplate template = (McStructureTemplate)target;

        if (GUILayout.Button("Bake Structure From Children To Data Arrays"))
        {
            // Editor-time profiling can use EditorApplication.timeSinceStartup
            double editorStartTime = EditorApplication.timeSinceStartup;
            BakeStructureData(template);
            double duration = (EditorApplication.timeSinceStartup - editorStartTime) * 1000.0;
            logBuilder.Clear();
            logBuilder.AppendFormat("[McStructureTemplateEditor.BakeStructureData] Operation took {0:F2} ms for '{1}'.", duration, template.structureName);
#if ENABLE_LOGGING
            Debug.Log(logBuilder.ToString());
#endif
        }
        
        if (template.bakedVoxelPositions != null && template.bakedVoxelBlockIDs != null && template.bakedVoxelPositions.Length > 0)
        {
            EditorGUILayout.LabelField("Baked Data Status:", EditorStyles.boldLabel);
            logBuilder.Clear(); // Using StringBuilder for consistency, though simple here
            logBuilder.AppendFormat("  - Voxel Count: {0}", template.bakedVoxelPositions.Length);
            EditorGUILayout.LabelField(logBuilder.ToString());

            if (template.bakedVoxelPositions.Length != template.bakedVoxelBlockIDs.Length) {
                EditorGUILayout.HelpBox("Warning: Baked positions and block IDs array lengths do not match! Re-bake.", MessageType.Error);
            }
        } else {
            EditorGUILayout.HelpBox("This structure has not been baked yet or contains no voxel data. " +
                                   "Ensure child GameObjects have 'McStructureVoxelData' and then click 'Bake'.", MessageType.Warning);
        }
    }

    private void BakeStructureData(McStructureTemplate template)
    {
        Transform templateRoot = template.transform;
        if (templateRoot.childCount == 0 && template.GetComponentsInChildren<McStructureVoxelData>(true).Length == 0)
        {
            logBuilder.Clear();
            logBuilder.AppendFormat("[McStructureTemplateEditor.BakeStructureData] No child objects with McStructureVoxelData found in '{0}'. Nothing to bake.", template.structureName);
#if ENABLE_LOGGING
            Debug.LogWarning(logBuilder.ToString(), template.gameObject);
#endif
            template.bakedVoxelPositions = new Vector3Int[0]; 
            template.bakedVoxelBlockIDs = new byte[0];
            EditorUtility.SetDirty(template);
            return;
        }

        Undo.RecordObject(template, "Bake Voxel Structure Data");

        List<Vector3Int> positions = new List<Vector3Int>();
        List<byte> blockIDs = new List<byte>();
        McStructureVoxelData[] childVoxels = template.GetComponentsInChildren<McStructureVoxelData>(true);

        if (childVoxels.Length == 0) {
            logBuilder.Clear();
            logBuilder.AppendFormat("[McStructureTemplateEditor.BakeStructureData] No 'McStructureVoxelData' components found in children of '{0}'.", template.structureName);
#if ENABLE_LOGGING
            Debug.LogWarning(logBuilder.ToString(), template.gameObject);
#endif
            template.bakedVoxelPositions = new Vector3Int[0];
            template.bakedVoxelBlockIDs = new byte[0];
            EditorUtility.SetDirty(template);
            return;
        }

        logBuilder.Clear();
        logBuilder.AppendFormat("[McStructureTemplateEditor.BakeStructureData] Found {0} McStructureVoxelData components in children of '{1}'. Baking now...", childVoxels.Length, template.structureName);
#if ENABLE_LOGGING
        Debug.Log(logBuilder.ToString());
#endif

        for (int i = 0; i < childVoxels.Length; i++)
        {
            McStructureVoxelData voxelDataScript = childVoxels[i];
            Transform childTransform = voxelDataScript.transform;
            Vector3 relativePos = templateRoot.InverseTransformPoint(childTransform.position);
            Vector3Int voxelPos = new Vector3Int(
                Mathf.RoundToInt(relativePos.x),
                Mathf.RoundToInt(relativePos.y),
                Mathf.RoundToInt(relativePos.z)
            );
            positions.Add(voxelPos);
            blockIDs.Add(voxelDataScript.blockID);
        }

        template.bakedVoxelPositions = positions.ToArray();
        template.bakedVoxelBlockIDs = blockIDs.ToArray();
        EditorUtility.SetDirty(template);
        // Prefab saving logic remains the same

        logBuilder.Clear();
        logBuilder.AppendFormat("[McStructureTemplateEditor.BakeStructureData] Successfully baked {0} voxels for '{1}'. Apply to Prefab if needed.", positions.Count, template.structureName);
#if ENABLE_LOGGING
        Debug.Log(logBuilder.ToString());
#endif
    }
}
#endif
