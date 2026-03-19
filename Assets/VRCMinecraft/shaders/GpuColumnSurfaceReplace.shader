Shader "VRCM/GpuColumnSurfaceReplace"
{
    Properties
    {
        _BaseColumnTex ("Base Column Texture", 2D) = "black" {}
        _SurfaceInfoTex ("Surface Info Texture", 2D) = "black" {}
        _SurfaceParamsTexA ("Surface Params A", 2D) = "black" {}
        _SurfaceParamsTexB ("Surface Params B", 2D) = "black" {}
        _BedrockMaskTex ("Bedrock Mask", 2D) = "black" {}
        _SandNoiseTex ("Sand Noise Texture", 2D) = "black" {}
        _GravelNoiseTex ("Gravel Noise Texture", 2D) = "black" {}
        _StoneNoiseTex ("Stone Noise Texture", 2D) = "black" {}
        _WorldHeight ("World Height", Int) = 256
        _ChunkSizeXZ ("Chunk Size XZ", Int) = 16
        _StoneBlockId ("Stone Block ID", Int) = 1
        _BedrockBlockId ("Bedrock Block ID", Int) = 7
        _SandBlockId ("Sand Block ID", Int) = 12
        _GravelBlockId ("Gravel Block ID", Int) = 13
        _WaterBlockId ("Water Block ID", Int) = 9
        _SandstoneBlockId ("Sandstone Block ID", Int) = 24
        _FlipXAxis ("Flip X Axis", Int) = 0
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
            sampler2D _SurfaceInfoTex;
            sampler2D _SurfaceParamsTexA;
            sampler2D _SurfaceParamsTexB;
            sampler2D _BedrockMaskTex;
            sampler2D _SandNoiseTex;
            sampler2D _GravelNoiseTex;
            sampler2D _StoneNoiseTex;
            int _WorldHeight;
            int _ChunkSizeXZ;
            int _StoneBlockId;
            int _BedrockBlockId;
            int _SandBlockId;
            int _GravelBlockId;
            int _WaterBlockId;
            int _SandstoneBlockId;
            int _FlipXAxis;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            int readBaseBlockId(int x, int y, int z)
            {
                int packedHeight = _WorldHeight * _ChunkSizeXZ;
                int packedRow = y * _ChunkSizeXZ + z;
                float2 uv = float2((x + 0.5) / (float)_ChunkSizeXZ, (packedRow + 0.5) / (float)packedHeight);
                return (int)round(tex2Dlod(_BaseColumnTex, float4(uv, 0, 0)).r * 255.0);
            }

            float4 readSurfaceParamsA(int x, int z)
            {
                float2 uv = float2((x + 0.5) / (float)_ChunkSizeXZ, (z + 0.5) / (float)_ChunkSizeXZ);
                return tex2Dlod(_SurfaceParamsTexA, float4(uv, 0, 0));
            }

            float4 readSurfaceParamsB(int x, int z)
            {
                float2 uv = float2((x + 0.5) / (float)_ChunkSizeXZ, (z + 0.5) / (float)_ChunkSizeXZ);
                return tex2Dlod(_SurfaceParamsTexB, float4(uv, 0, 0));
            }

            float4 readSurfaceInfo(int x, int z)
            {
                float2 uv = float2((x + 0.5) / (float)_ChunkSizeXZ, (z + 0.5) / (float)_ChunkSizeXZ);
                return tex2Dlod(_SurfaceInfoTex, float4(uv, 0, 0));
            }

            float readNoiseValue(sampler2D noiseTex, int x, int z)
            {
                int noiseX = _FlipXAxis == 1 ? (_ChunkSizeXZ - 1 - x) : x;
                float2 uv = float2((noiseX + 0.5) / (float)_ChunkSizeXZ, (z + 0.5) / (float)_ChunkSizeXZ);
                return tex2Dlod(noiseTex, float4(uv, 0, 0)).r;
            }

            int readBedrockMask(int x, int y, int z)
            {
                int packedHeight = 5 * _ChunkSizeXZ;
                int packedRow = y * _ChunkSizeXZ + z;
                float2 uv = float2((x + 0.5) / (float)_ChunkSizeXZ, (packedRow + 0.5) / (float)packedHeight);
                return tex2Dlod(_BedrockMaskTex, float4(uv, 0, 0)).r > 0.5 ? 1 : 0;
            }

            float4 frag(v2f i) : SV_Target
            {
                int packedHeight = _WorldHeight * _ChunkSizeXZ;
                int x = clamp((int)floor(i.vertex.x), 0, _ChunkSizeXZ - 1);
                int packedRow = clamp((int)floor(i.vertex.y), 0, packedHeight - 1);
                int z = packedRow % _ChunkSizeXZ;
                int y = packedRow / _ChunkSizeXZ;

                if (y < 5 && readBedrockMask(x, y, z) == 1)
                {
                    return float4(_BedrockBlockId / 255.0, 0, 0, 1);
                }

                int baseBlockId = readBaseBlockId(x, y, z);
                if (baseBlockId != _StoneBlockId)
                {
                    return float4(baseBlockId / 255.0, 0, 0, 1);
                }

                float4 surfaceInfo = readSurfaceInfo(x, z);
                float4 paramsA = readSurfaceParamsA(x, z);
                float4 paramsB = readSurfaceParamsB(x, z);
                int hasStone = surfaceInfo.a > 0.5 ? 1 : 0;
                if (hasStone == 0)
                {
                    return float4(baseBlockId / 255.0, 0, 0, 1);
                }

                int surfaceY = (int)round(surfaceInfo.r * 255.0);
                int topBlockId = (int)round(paramsA.r * 255.0);
                int fillerBlockId = (int)round(paramsA.g * 255.0);
                float sandRand = paramsB.r;
                float gravelRand = paramsB.g;
                float depthRand = paramsB.b;
                int sandstoneDepth = (int)round(paramsB.a * 255.0);
                bool sand = readNoiseValue(_SandNoiseTex, x, z) + sandRand * 0.2 > 0.0;
                bool gravel = readNoiseValue(_GravelNoiseTex, x, z) + gravelRand * 0.2 > 3.0;
                int depth = (int)(readNoiseValue(_StoneNoiseTex, x, z) * 0.33333333 + 3.0 + depthRand * 0.25);

                if (depth <= 0)
                {
                    topBlockId = 0;
                    fillerBlockId = _StoneBlockId;
                }
                else if (surfaceY >= 60 && surfaceY <= 65)
                {
                    if (gravel)
                    {
                        topBlockId = 0;
                        fillerBlockId = _GravelBlockId;
                    }
                    if (sand)
                    {
                        topBlockId = _SandBlockId;
                        fillerBlockId = _SandBlockId;
                    }
                }

                if (surfaceY < 64 && topBlockId == 0)
                {
                    topBlockId = _WaterBlockId;
                }

                if (y > surfaceY)
                {
                    return float4(baseBlockId / 255.0, 0, 0, 1);
                }

                int delta = surfaceY - y;
                int finalBlockId = baseBlockId;
                if (delta == 0)
                {
                    finalBlockId = surfaceY >= 63 ? topBlockId : fillerBlockId;
                }
                else if (delta <= depth)
                {
                    finalBlockId = fillerBlockId;
                }
                else if (fillerBlockId == _SandBlockId && sandstoneDepth > 0 && delta <= depth + sandstoneDepth)
                {
                    finalBlockId = _SandstoneBlockId;
                }

                return float4(finalBlockId / 255.0, 0, 0, 1);
            }
            ENDCG
        }
    }
}
