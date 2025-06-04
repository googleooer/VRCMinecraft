#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering; // Required for BlendMode, CullMode, RenderQueue enums

public class MCTerrainCombinedShaderGUI : ShaderGUI
{
    private MaterialProperty surfaceTypeProp;
    private MaterialProperty mainTexProp;
    private MaterialProperty tintMaskProp;
    private MaterialProperty biomeColorProp;
    private MaterialProperty skyLightProp;
    private MaterialProperty dayProgressProp;
    private MaterialProperty cutoffProp;

    // Properties for render states (we'll set these based on SurfaceType)
    private MaterialProperty srcBlendProp;
    private MaterialProperty dstBlendProp;
    private MaterialProperty zWriteProp;
    private MaterialProperty cullProp;

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] props)
    {
        // Find properties 
        surfaceTypeProp = FindProperty("_SurfaceType", props);
        mainTexProp = FindProperty("_MainTex", props);
        tintMaskProp = FindProperty("_TintMask", props);
        biomeColorProp = FindProperty("_BiomeColor", props);
        skyLightProp = FindProperty("_SkyLight", props);
        dayProgressProp = FindProperty("_DayProgress", props);
        cutoffProp = FindProperty("_Cutoff", props);

        srcBlendProp = FindProperty("_SrcBlend", props);
        dstBlendProp = FindProperty("_DstBlend", props);
        zWriteProp = FindProperty("_ZWrite", props);
        cullProp = FindProperty("_Cull", props);

        // Cast materialEditor.target to Material
        Material material = materialEditor.target as Material;

        // --- Surface Type and State Management ---
        EditorGUI.BeginChangeCheck();
        materialEditor.ShaderProperty(surfaceTypeProp, "Surface Type");
        bool surfaceTypeChanged = EditorGUI.EndChangeCheck();

        if (surfaceTypeChanged)
        {
            SetupMaterialWithSurfaceType(material, (SurfaceType)surfaceTypeProp.floatValue);
        }
        
        // Update render states if they were changed by an undo/redo or by direct material modification
        // (though direct modification of _SrcBlend etc. is unlikely as they are HideInInspector)
        // This ensures the keywords match the actual state.
        // We primarily drive state from the SurfaceType enum.
        SetShaderKeywords(material, (SurfaceType)surfaceTypeProp.floatValue);


        // --- Display other properties ---
        EditorGUILayout.Space();
        materialEditor.TexturePropertySingleLine(new GUIContent("Main Texture Array", "Base Texture Array (RGB), Alpha for Opacity/Cutout"), mainTexProp);
        materialEditor.TexturePropertySingleLine(new GUIContent("Tint Mask Array", "Tint Mask (RGBA)"), tintMaskProp);
        materialEditor.ColorProperty(biomeColorProp, "Biome Color");
        materialEditor.RangeProperty(dayProgressProp, "Day Progress");
        materialEditor.ShaderProperty(skyLightProp, "Sky Light");


        SurfaceType currentSurfaceType = (SurfaceType)surfaceTypeProp.floatValue;
        if (currentSurfaceType == SurfaceType.Cutout)
        {
            materialEditor.RangeProperty(cutoffProp, "Alpha Cutoff");
        }
        
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Advanced Render State (Controlled by Surface Type)", EditorStyles.boldLabel);
        EditorGUI.BeginDisabledGroup(true); // Disable manual editing of these
        EditorGUILayout.EnumPopup("Src Blend", (BlendMode)material.GetInt("_SrcBlend"));
        EditorGUILayout.EnumPopup("Dst Blend", (BlendMode)material.GetInt("_DstBlend"));
        EditorGUILayout.Toggle("ZWrite", material.GetInt("_ZWrite") > 0);
        EditorGUILayout.EnumPopup("Cull Mode", (CullMode)material.GetInt("_Cull"));
        EditorGUILayout.IntField("Render Queue", material.renderQueue);
        EditorGUI.EndDisabledGroup();


        // Apply changes to the material
        // base.OnGUI(materialEditor, props); // We are handling property drawing, so don't call base unless we want default drawers for unhandled props.
    }

    // Enum to match the [KeywordEnum(Opaque, Cutout, Transparent)] in the shader
    private enum SurfaceType
    {
        Opaque,
        Cutout,
        Transparent
    }

    private void SetupMaterialWithSurfaceType(Material material, SurfaceType surfaceType)
    {
        SetShaderKeywords(material, surfaceType);

        switch (surfaceType)
        {
            case SurfaceType.Opaque:
                material.SetOverrideTag("RenderType", "Opaque");
                material.SetInt("_SrcBlend", (int)BlendMode.One);
                material.SetInt("_DstBlend", (int)BlendMode.Zero);
                material.SetInt("_ZWrite", 1); // ZWrite On
                material.SetInt("_Cull", (int)CullMode.Back);
                material.renderQueue = (int)RenderQueue.Geometry; 
                break;
 
            case SurfaceType.Cutout:
                material.SetOverrideTag("RenderType", "TransparentCutout"); // Or "TransparentCutout" if preferred and handled by engine
                material.SetOverrideTag("IgnoreProjector", "True");
                material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); // Alpha test doesn't need blending
                material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 1); // ZWrite On
                material.SetInt("_Cull", (int)CullMode.Off); // Could be Off if two-sided cutout needed
                material.renderQueue = (int)RenderQueue.AlphaTest; // Standard queue for cutouts
                break;

            case SurfaceType.Transparent:
                material.SetOverrideTag("RenderType", "Transparent");
                material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0); // ZWrite Off
                material.SetInt("_Cull", (int)CullMode.Off);    // Often Off for transparent things like glass
                material.renderQueue = (int)RenderQueue.Transparent;
                break;
        }
        EditorUtility.SetDirty(material); // Ensure changes are saved
    }
    
    private void SetShaderKeywords(Material material, SurfaceType surfaceType)
    {
        // Manage keywords for shader_feature_local
        if (surfaceType == SurfaceType.Opaque)
        {
            material.EnableKeyword("_SURFACETYPE_OPAQUE");
            material.DisableKeyword("_SURFACETYPE_CUTOUT");
            material.DisableKeyword("_SURFACETYPE_TRANSPARENT");
        }
        else if (surfaceType == SurfaceType.Cutout)
        {
            material.DisableKeyword("_SURFACETYPE_OPAQUE");
            material.EnableKeyword("_SURFACETYPE_CUTOUT");
            material.DisableKeyword("_SURFACETYPE_TRANSPARENT");
        }
        else // Transparent
        {
            material.DisableKeyword("_SURFACETYPE_OPAQUE");
            material.DisableKeyword("_SURFACETYPE_CUTOUT");
            material.EnableKeyword("_SURFACETYPE_TRANSPARENT");
        }
    }
}
#endif 