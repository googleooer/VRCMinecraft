Shader "VRCM/GpuColumnDecoration"
{
    Properties
    {
        _BaseColumnTex ("Finalized Column Texture", 2D) = "black" {}
        _CandidateTex ("Candidate Positions", 2D) = "black" {}
        _CandidateCount ("Number of candidates", Int) = 0
        _CandidateTexWidth ("Candidate texture width", Int) = 256
        _CandidateTexHeight ("Candidate texture height", Int) = 32
        _WorldHeight ("World Height in blocks", Int) = 128
        _ChunkSizeXZ ("Chunk Size XZ", Int) = 16
        _AirBlockId ("Air Block ID", Int) = 0
        _GrassBlockId ("Grass Block ID", Int) = 2
        _DirtBlockId ("Dirt Block ID", Int) = 3
        _LeavesBlockId ("Leaves Block ID", Int) = 18
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

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

            sampler2D _BaseColumnTex;
            sampler2D _CandidateTex;
            int _CandidateCount;
            int _CandidateTexWidth;
            int _CandidateTexHeight;
            int _WorldHeight;
            int _ChunkSizeXZ;
            int _AirBlockId;
            int _GrassBlockId;
            int _DirtBlockId;
            int _LeavesBlockId;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            int readBlock(int x, int y, int z)
            {
                if (x < 0 || x >= _ChunkSizeXZ || z < 0 || z >= _ChunkSizeXZ || y < 0 || y >= _WorldHeight)
                    return _AirBlockId;
                int packedRow = y * _ChunkSizeXZ + z;
                float2 uv = float2((x + 0.5) / (float)_ChunkSizeXZ, (packedRow + 0.5) / (float)(_WorldHeight * _ChunkSizeXZ));
                return (int)round(tex2Dlod(_BaseColumnTex, float4(uv, 0, 0)).r * 255.0);
            }

            float4 readCandidate(int index)
            {
                int cx = index % _CandidateTexWidth;
                int cy = index / _CandidateTexWidth;
                float2 uv = float2((cx + 0.5) / (float)_CandidateTexWidth, (cy + 0.5) / (float)_CandidateTexHeight);
                return tex2Dlod(_CandidateTex, float4(uv, 0, 0));
            }

            int getHighestSolidBlock(int x, int z)
            {
                for (int sy = _WorldHeight - 1; sy >= 0; sy--)
                {
                    if (readBlock(x, sy, z) != _AirBlockId) return sy;
                }
                return -1;
            }

            float4 frag(v2f i) : SV_Target
            {
                int x = clamp((int)floor(i.vertex.x), 0, _ChunkSizeXZ - 1);
                int packedRow = clamp((int)floor(i.vertex.y), 0, _WorldHeight * _ChunkSizeXZ - 1);
                int z = packedRow % _ChunkSizeXZ;
                int y = packedRow / _ChunkSizeXZ;

                int currentBlock = readBlock(x, y, z);

                // Pre-compute surface Y once per fragment
                int surfaceY = getHighestSolidBlock(x, z);

                for (int ci = 0; ci < _CandidateCount; ci++)
                {
                    float4 cand = readCandidate(ci);
                    int packedXZ = (int)round(cand.r * 255.0);
                    int candX = packedXZ >> 4;
                    int candZ = packedXZ & 15;
                    int candBlock = (int)round(cand.b * 255.0);
                    int candMode = (int)round(cand.a * 255.0);

                    if (candMode == 0)
                    {
                        // Grass: surface-relative with dY scatter offset
                        // G channel = (dY + 128) / 255
                        if (candX == x && candZ == z && surfaceY >= 0)
                        {
                            int dY = (int)round(cand.g * 255.0) - 128;
                            int targetY = surfaceY + dY + 1;
                            if (y == targetY && currentBlock == _AirBlockId)
                            {
                                int blockBelow = readBlock(x, y - 1, z);
                                if (blockBelow == _GrassBlockId || blockBelow == _DirtBlockId)
                                {
                                    currentBlock = candBlock;
                                }
                            }
                        }
                    }
                    else if (candMode == 1)
                    {
                        // Flower: absolute Y matching (vanilla doesn't walk Y down)
                        int candY = (int)round(cand.g * 255.0);
                        if (candX == x && candY == y && candZ == z)
                        {
                            if (currentBlock == _AirBlockId && y > 0)
                            {
                                int blockBelow = readBlock(x, y - 1, z);
                                if (blockBelow == _GrassBlockId || blockBelow == _DirtBlockId)
                                {
                                    currentBlock = candBlock;
                                }
                            }
                        }
                    }
                }

                return float4(currentBlock / 255.0, 0, 0, 1);
            }
            ENDCG
        }
    }
}
