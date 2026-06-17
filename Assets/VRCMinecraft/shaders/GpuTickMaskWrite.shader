// GPU OFFLOAD #8: Scheduled-tick mask single-cell write.
//
// The fluid CA (and other tick-driven shaders) reads a per-chunk mask RT to know which
// voxels have a scheduled tick this frame. CPU keeps a small heap of pending ticks;
// each frame, the heap-pop loop calls this shader to flip the bit at (lx, ly, lz).
//
// One Blit per tick-schedule call. Output RT is sized chunkSizeXZ × (chunkSizeY * chunkSizeXZ)
// matching the block RT, R8 with 0=no-tick, 255=has-tick.
//
// (A future optimization would batch many bits into a uint texture and write 32 at a time
// with a "tick batch" buffer; for now this is simple and correct.)
Shader "VRCM/GpuTickMaskWrite"
{
    Properties
    {
        _MainTex     ("Current Mask", 2D) = "black" {}
        _ChunkSizeXZ ("Chunk Size XZ", Int) = 16
        _ChunkSizeY  ("Chunk Size Y", Int) = 16
        _WriteX ("Write X", Int) = 0
        _WriteY ("Write Y", Int) = 0
        _WriteZ ("Write Z", Int) = 0
        _WriteValue ("Write Value (0=clear, 1=set)", Int) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            ZTest Always Cull Off ZWrite Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

            sampler2D _MainTex;
            int _ChunkSizeXZ, _ChunkSizeY;
            int _WriteX, _WriteY, _WriteZ, _WriteValue;

            v2f vert(appdata v) { v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }

            float4 frag(v2f i) : SV_Target
            {
                int px = clamp((int)floor(i.vertex.x), 0, _ChunkSizeXZ - 1);
                int py = clamp((int)floor(i.vertex.y), 0, _ChunkSizeY * _ChunkSizeXZ - 1);
                int lx = px;
                int ly = py / _ChunkSizeXZ;
                int lz = py - ly * _ChunkSizeXZ;

                if (lx == _WriteX && ly == _WriteY && lz == _WriteZ)
                {
                    return float4(_WriteValue == 0 ? 0.0 : 1.0, 0, 0, 1);
                }
                // Pass-through
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
    }
}
