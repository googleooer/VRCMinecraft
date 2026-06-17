// GPU OFFLOAD #12: SetBlock chain — single-pixel atlas write + dirty mark.
//
// When the player places or breaks a block, we want to update everything in one Blit chain:
//   pass 0: write block ID to atlas at (slotIndex, lx, ly, lz)
//   pass 1: write metadata = 0 to metadata atlas at same coord
//   pass 2: mark neighbor chunks' "needs-mesh-rebuild" bitmask
//
// Combined with GpuLightPoke (offload #6) — kicked separately by C# right after these passes
// run — the full per-SetBlock CPU work drops to ~5 material.SetInt calls + 3 Blits.
//
// The atlas layout matches the existing GpuBlockAtlas in the project (one atlas tile per
// chunk slot, tiles arranged in a grid). The CPU sends the slot index per call.
Shader "VRCM/GpuSetBlockChain"
{
    Properties
    {
        _MainTex   ("Atlas (R/W)", 2D) = "black" {}
        _AtlasTilesX ("Atlas Tiles per Row", Int) = 32
        _ChunkSizeXZ ("Chunk Size XZ", Int) = 16
        _ChunkSizeY  ("Chunk Size Y",  Int) = 16
        _SlotIndex ("Slot Index", Int) = 0
        _LocalX ("Local X", Int) = 0
        _LocalY ("Local Y", Int) = 0
        _LocalZ ("Local Z", Int) = 0
        _NewBlockId ("New Block ID", Int) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        CGINCLUDE
        #include "UnityCG.cginc"
        struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
        struct v2f     { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };
        sampler2D _MainTex;
        int _AtlasTilesX;
        int _ChunkSizeXZ;
        int _ChunkSizeY;
        int _SlotIndex;
        int _LocalX, _LocalY, _LocalZ;
        int _NewBlockId;
        v2f vert(appdata v) { v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }

        bool IsTargetPixel(float2 vpos, out int slotX, out int slotY, out int lx, out int ly, out int lz)
        {
            int px = (int)floor(vpos.x);
            int py = (int)floor(vpos.y);
            int tileX = px / _ChunkSizeXZ;
            int tileY = py / (_ChunkSizeY * _ChunkSizeXZ);
            slotX = tileX; slotY = tileY;
            lx = px - tileX * _ChunkSizeXZ;
            int innerY = py - tileY * (_ChunkSizeY * _ChunkSizeXZ);
            ly = innerY / _ChunkSizeXZ;
            lz = innerY - ly * _ChunkSizeXZ;
            int slot = tileY * _AtlasTilesX + tileX;
            return slot == _SlotIndex && lx == _LocalX && ly == _LocalY && lz == _LocalZ;
        }
        ENDCG

        // Pass 0: write block ID
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int sx, sy, lx, ly, lz;
                if (IsTargetPixel(i.vertex.xy, sx, sy, lx, ly, lz))
                    return float4((float)_NewBlockId / 255.0, 0, 0, 1);
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
        // Pass 1: clear metadata at the target cell (set to 0)
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int sx, sy, lx, ly, lz;
                if (IsTargetPixel(i.vertex.xy, sx, sy, lx, ly, lz))
                    return 0;
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
    }
}
