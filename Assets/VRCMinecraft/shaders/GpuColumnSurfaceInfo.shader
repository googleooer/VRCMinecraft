Shader "VRCM/GpuColumnSurfaceInfo"
{
    Properties
    {
        _BaseColumnTex ("Base Column Texture", 2D) = "black" {}
        _WorldHeight ("World Height", Int) = 256
        _ChunkSizeXZ ("Chunk Size XZ", Int) = 16
        _StoneBlockId ("Stone Block ID", Int) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _BaseColumnTex;
            int _WorldHeight;
            int _ChunkSizeXZ;
            int _StoneBlockId;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            int readBlockId(int x, int y, int z)
            {
                int packedHeight = _WorldHeight * _ChunkSizeXZ;
                int packedRow = y * _ChunkSizeXZ + z;
                float2 uv = float2((x + 0.5) / (float)_ChunkSizeXZ, (packedRow + 0.5) / (float)packedHeight);
                return (int)round(tex2Dlod(_BaseColumnTex, float4(uv, 0, 0)).r * 255.0);
            }

            float4 frag(v2f i) : SV_Target
            {
                int x = clamp((int)floor(i.vertex.x), 0, _ChunkSizeXZ - 1);
                int z = clamp((int)floor(i.vertex.y), 0, _ChunkSizeXZ - 1);

                int surfaceY = 0;
                int hasStone = 0;
                for (int y = _WorldHeight - 1; y >= 0; y--)
                {
                    if (readBlockId(x, y, z) == _StoneBlockId)
                    {
                        surfaceY = y;
                        hasStone = 1;
                        break;
                    }
                }

                return float4(surfaceY / 255.0, 0, 0, hasStone);
            }
            ENDCG
        }
    }
}
