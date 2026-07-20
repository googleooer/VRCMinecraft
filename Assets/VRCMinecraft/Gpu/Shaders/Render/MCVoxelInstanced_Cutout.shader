// (c) GPU chunk-mesh voxel render — CUTOUT pass (leaves etc.). Shared logic in MCVoxelInstanced.cginc.
// Renders a chunk's cutout cubes (block-class 0.5 in _BlockFaceSliceTex.a) with alpha clip, on the
// chunk's CUTOUT MeshRenderer. Cull Off (cutout faces are see-through both sides).
Shader "Unlit/MCVoxelInstanced_Cutout"
{
    Properties
    {
        _MainTex ("Texture Array", 2DArray) = "white" {}
        _TintMask ("Tint Mask Array", 2DArray) = "black" {}
        [HideInInspector] _ShouldDrawTex ("Should Draw Table", 2D) = "black" {}
        [HideInInspector] _BlockFaceSliceTex ("Block Face Slice LUT", 2D) = "black" {}
        [HideInInspector] _BiomeColorRT ("Biome Colour", 2D) = "white" {}
        [HideInInspector] _InstBlockAtlas ("Inst Block Atlas (vertex)", 2D) = "black" {}
        [HideInInspector] _InstSlotLookup ("Inst Slot Lookup (vertex)", 2D) = "black" {}
        _FogColor ("Fog Color", Color) = (0.5, 0.6, 0.7, 1.0)
        _FogStart ("Fog Start Distance", Float) = 32.0
        _FogEnd ("Fog End Distance", Float) = 128.0
        _FogMode ("Fog Mode (0 linear, 1 exp, 2 exp2)", Float) = 0.0
        _FogDensity ("Fog Density (exp modes)", Float) = 0.0
    }
    SubShader
    {
        Tags { "Queue"="AlphaTest" "IgnoreProjector"="True" "RenderType"="TransparentCutout" }
        LOD 100
        Cull Off
        ZWrite On
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #define PASS_CLASS 1
            #include "MCVoxelInstanced.cginc"
            ENDCG
        }
    }
}
