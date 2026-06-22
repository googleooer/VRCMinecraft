// (c) GPU chunk-mesh voxel render — TRANSPARENT pass (water/ice/glass). Shared in MCVoxelInstanced.cginc.
// Renders a chunk's transparent cubes (block-class 0.25 in _BlockFaceSliceTex.a) alpha-blended, on the
// chunk's TRANSPARENT MeshRenderer. Cull Off, ZWrite On (matches MCTerrain_Transparent).
Shader "Unlit/MCVoxelInstanced_Transparent"
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
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
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
            #define PASS_CLASS 2
            #include "MCVoxelInstanced.cginc"
            ENDCG
        }
    }
}
