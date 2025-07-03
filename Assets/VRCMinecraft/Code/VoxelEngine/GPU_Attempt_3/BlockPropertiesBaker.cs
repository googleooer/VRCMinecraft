using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// This editor script automatically bakes the data from McBlockTypeManager 
/// into a shader include file (.cginc) to be used as a Look-Up Table (LUT).
/// It runs automatically on play mode change, before a build, and provides a manual menu item.
/// </summary>
[InitializeOnLoad]
public class BlockPropertiesBaker : IPreprocessBuildWithReport
{
    // --- Configuration ---
    // The path to your .cginc file, relative to the Assets folder.
    private const string SHADER_INCLUDE_PATH = "VRCMinecraft/Code/VoxelEngine/GPU_Attempt_3/TerrainUtils.cginc";

    // Markers within the .cginc file to identify the replacement zone.
    private const string START_MARKER = "// BLOCK_LOOKUP_TABLES_START";
    private const string END_MARKER = "// BLOCK_LOOKUP_TABLES_END";
    
    // --- Build Preprocessing ---
    
    // This property determines the order of execution for build preprocessors.
    public int callbackOrder { get { return 0; } }

    /// <summary>
    /// This function is called by Unity automatically before any build starts.
    /// </summary>
    public void OnPreprocessBuild(BuildReport report)
    {
        Debug.Log("[BlockPropertiesBaker] Pre-build step initiated. Baking block properties to shader...");
        BakeProperties();
    }

    // --- Static Constructor for Automatic Execution on Editor Load/Play ---
    static BlockPropertiesBaker()
    {
        // Subscribe to the play mode state change event to run automatically.
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        // We want to bake the data right before the scene is played.
        if (state == PlayModeStateChange.ExitingEditMode)
        {
            Debug.Log("[BlockPropertiesBaker] Play mode entered. Baking block properties to shader...");
            BakeProperties();
        }
    }

    // --- Manual Menu Item ---
    [MenuItem("Minecraft/Bake Block Properties to Shader")]
    public static void BakePropertiesMenuItem()
    {
        Debug.Log("[BlockPropertiesBaker] Manual bake requested.");
        BakeProperties();
    }

    /// <summary>
    /// The core logic for finding the manager, generating the shader code, and writing to the file.
    /// </summary>
    private static void BakeProperties()
    {
        // 1. Find the McBlockTypeManager in the active scene.
        McBlockTypeManager manager = Object.FindObjectOfType<McBlockTypeManager>();
        if (manager == null)
        {
            // Don't show a dialog during builds as it can interrupt the process.
            Debug.LogError("[BlockPropertiesBaker] Could not find an active McBlockTypeManager in the scene. Bake failed.");
            return;
        }

        // 2. Ensure the manager's data is encoded. This is critical.
        manager.EncodeDataForBuild();
        ushort[] bakedData = manager.finalDataArray;

        if (bakedData == null || bakedData.Length == 0)
        {
            Debug.LogWarning("[BlockPropertiesBaker] McBlockTypeManager has no baked data. The shader LUT will be empty.");
        }

        // 3. Generate the HLSL/Cg code for the LUT and helper functions.
        string shaderCode = GenerateShaderCode(bakedData, manager.blockNames);

        // 4. Find and update the .cginc file.
        string fullPath = Path.Combine(Application.dataPath, SHADER_INCLUDE_PATH);
        if (!File.Exists(fullPath))
        {
            Debug.LogError($"[BlockPropertiesBaker] Shader include file not found at path: Assets/{SHADER_INCLUDE_PATH}. Bake failed.");
            return;
        }

        try
        {
            string fileContent = File.ReadAllText(fullPath);

            // Use Regex for robust replacement between markers.
            string pattern = $"{Regex.Escape(START_MARKER)}(.*?){Regex.Escape(END_MARKER)}";
            string replacement = $"{START_MARKER}\n{shaderCode}\n{END_MARKER}";

            if (!Regex.IsMatch(fileContent, pattern, RegexOptions.Singleline))
            {
                Debug.LogError($"[BlockPropertiesBaker] Could not find the start and end markers in the shader file:\n{START_MARKER}\n{END_MARKER}. Bake failed.");
                return;
            }

            string newFileContent = Regex.Replace(fileContent, pattern, replacement, RegexOptions.Singleline);

            // Only write to the file if the content has actually changed.
            if (newFileContent != fileContent)
            {
                File.WriteAllText(fullPath, newFileContent);
                // Force Unity to re-import the changed asset.
                AssetDatabase.ImportAsset($"Assets/{SHADER_INCLUDE_PATH}");
                Debug.Log($"[BlockPropertiesBaker] Successfully baked {bakedData.Length} block properties to '{SHADER_INCLUDE_PATH}'.");
            }
            else
            {
                 Debug.Log($"[BlockPropertiesBaker] Shader properties are already up-to-date. No changes made.");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[BlockPropertiesBaker] An error occurred while writing to the shader file: {e.Message}");
        }
    }

    /// <summary>
    /// Generates the HLSL/Cg code string from the baked ushort array.
    /// </summary>
    private static string GenerateShaderCode(ushort[] data, string[] blockNames)
    {
        StringBuilder sb = new StringBuilder();

        // --- Generate the main LUT array ---
        sb.AppendLine("// This code is auto-generated by BlockPropertiesBaker.cs. DO NOT EDIT MANUALLY.");
        sb.AppendFormat("static const uint BLOCK_PROPERTIES_LUT[{0}] = {{\n", data.Length > 0 ? data.Length : 1);
        if (data != null && data.Length > 0)
        {
            for (int i = 0; i < data.Length; i++)
            {
                sb.AppendFormat("    0x{0:X4}u", data[i]); // Format as 4-digit hex
                string blockName = blockNames[i];
                if (i < data.Length - 1)
                {
                    sb.Append(",");
                }
                sb.AppendFormat(" // Block ID: {0} ({1})\n", i, blockName);
            }
        }
        else
        {
            // Add a dummy entry if the array is empty to prevent shader compilation errors.
            sb.AppendLine("    0x0000u // Empty LUT");
        }
        sb.AppendLine("};");
        sb.AppendLine();

        // --- Generate the helper functions to decode the properties ---
        sb.AppendLine("// --- Helper functions to unpack data from BLOCK_PROPERTIES_LUT ---");
        sb.AppendLine("// Note: The bit shifts and masks MUST match McBlockTypeManager.EncodeDataForBuild()");
        sb.AppendLine();
        
        sb.AppendLine("bool get_is_solid(uint block_id) {");
        sb.AppendLine("    return (BLOCK_PROPERTIES_LUT[block_id] & 0x1u) != 0u; // Bit 0");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("uint get_visibility_type(uint block_id) {");
        sb.AppendLine("    return (BLOCK_PROPERTIES_LUT[block_id] >> 1) & 0x3u; // Bits 1-2. 0:Opaque, 1:Transparent, 2:Cutout, 3:Invisible");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("uint get_culling_type(uint block_id) {");
        sb.AppendLine("    return (BLOCK_PROPERTIES_LUT[block_id] >> 3) & 0x7u; // Bits 3-5. 0:NoCull, 1:CullSelf, 2:CullSelfAndOpaque, 3:CullSelfAndCutout, 4:CullSelfAndTransparent, 5:CullAll");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("uint get_shape_type(uint block_id) {");
        sb.AppendLine("    return (BLOCK_PROPERTIES_LUT[block_id] >> 6) & 0x3u; // Bits 6-7. 0:Cube, 1:Cross");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("uint get_texture_mapping_type(uint block_id) {");
        sb.AppendLine("    return (BLOCK_PROPERTIES_LUT[block_id] >> 8) & 0x3u; // Bits 8-9. 0:AllFacesSame, 1:TopBottomSides");
        sb.AppendLine("}");
        sb.AppendLine("// End of auto-generated code.");

        return sb.ToString();
    }
}
