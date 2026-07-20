// (c) GPU chunk-mesh voxel render — OPAQUE pass. Shared logic in MCVoxelInstanced.cginc.
// Renders a chunk's opaque cubes straight from the GPU block atlas (vertex-stage per-voxel culling),
// through the chunk's opaque MeshRenderer. Cutout (leaves) + transparent (water/ice) are the sibling
// shaders MCVoxelInstanced_Cutout / MCVoxelInstanced_Transparent on the chunk's cutout/transparent
// MeshRenderers. Bindings (set by McWorld): _MainTex, _TintMask, _ShouldDrawTex, _BlockFaceSliceTex,
// _BiomeColorRT (per-chunk MPB), _InstBlockAtlas, _InstSlotLookup, + the _UdonVRCM_Gpu* globals.
Shader "Unlit/MCVoxelInstanced"
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
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100
        Cull Back
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #define PASS_CLASS 0
            #include "MCVoxelInstanced.cginc"
            ENDCG
        }
    }
}
