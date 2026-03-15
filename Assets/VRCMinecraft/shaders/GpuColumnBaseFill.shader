Shader "VRCM/GpuColumnBaseFill"
{
    Properties
    {
        _DensityTex ("Density Texture", 2D) = "black" {}
        _TemperatureTex ("Temperature Texture", 2D) = "white" {}
        _WorldHeight ("World Height", Int) = 128
        _ChunkSizeXZ ("Chunk Size XZ", Int) = 16
        _OceanHeight ("Ocean Height", Int) = 64
        _FlipXAxis ("Flip X Axis", Int) = 1
        _StoneBlockId ("Stone Block ID", Int) = 1
        _WaterBlockId ("Water Block ID", Int) = 9
        _IceBlockId ("Ice Block ID", Int) = 79
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

            sampler2D _DensityTex;
            sampler2D _TemperatureTex;
            int _WorldHeight;
            int _ChunkSizeXZ;
            int _OceanHeight;
            int _FlipXAxis;
            int _StoneBlockId;
            int _WaterBlockId;
            int _IceBlockId;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float sampleDensityPoint(int gridX, int gridY, int gridZ)
            {
                int xSize = 5;
                int zSize = 5;
                int ySize = _WorldHeight / 8 + 1;
                int xzIndex = gridX + gridZ * xSize;
                float2 uv = float2((xzIndex + 0.5) / (float)(xSize * zSize), (gridY + 0.5) / (float)ySize);
                return tex2Dlod(_DensityTex, float4(uv, 0, 0)).r;
            }

            float sampleTemperature(int localX, int localZ)
            {
                float2 uv = float2((localX + 0.5) / (float)_ChunkSizeXZ, (localZ + 0.5) / (float)_ChunkSizeXZ);
                return tex2Dlod(_TemperatureTex, float4(uv, 0, 0)).r;
            }

            float4 frag(v2f i) : SV_Target
            {
                int packedHeight = _WorldHeight * _ChunkSizeXZ;
                int xOut = clamp((int)floor(i.uv.x * _ChunkSizeXZ), 0, _ChunkSizeXZ - 1);
                int packedRow = clamp((int)floor(i.uv.y * packedHeight), 0, packedHeight - 1);
                int z = packedRow % _ChunkSizeXZ;
                int y = packedRow / _ChunkSizeXZ;
                int x = _FlipXAxis == 1 ? (_ChunkSizeXZ - 1 - xOut) : xOut;

                int xPiece = x / 4;
                int zPiece = z / 4;
                int yPiece = y / 8;
                int xSub = x - xPiece * 4;
                int zSub = z - zPiece * 4;
                int ySub = y - yPiece * 8;

                float yLerp = ySub * 0.125;
                float xLerp = xSub * 0.25;
                float zLerp = zSub * 0.25;

                float d00 = lerp(sampleDensityPoint(xPiece, yPiece, zPiece), sampleDensityPoint(xPiece, yPiece + 1, zPiece), yLerp);
                float d01 = lerp(sampleDensityPoint(xPiece, yPiece, zPiece + 1), sampleDensityPoint(xPiece, yPiece + 1, zPiece + 1), yLerp);
                float d10 = lerp(sampleDensityPoint(xPiece + 1, yPiece, zPiece), sampleDensityPoint(xPiece + 1, yPiece + 1, zPiece), yLerp);
                float d11 = lerp(sampleDensityPoint(xPiece + 1, yPiece, zPiece + 1), sampleDensityPoint(xPiece + 1, yPiece + 1, zPiece + 1), yLerp);

                float dx0 = lerp(d00, d10, xLerp);
                float dx1 = lerp(d01, d11, xLerp);
                float density = lerp(dx0, dx1, zLerp);
                float temp = sampleTemperature(x, z);

                float blockId = 0.0;
                if (y < _OceanHeight)
                {
                    blockId = (temp < 0.5 && y >= (_OceanHeight - 1)) ? _IceBlockId : _WaterBlockId;
                }
                if (density > 0.0)
                {
                    blockId = _StoneBlockId;
                }

                return float4(blockId / 255.0, 0, 0, 1);
            }
            ENDCG
        }
    }
}
