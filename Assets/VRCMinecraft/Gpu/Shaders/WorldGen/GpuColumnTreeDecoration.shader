Shader "VRCM/GpuColumnTreeDecoration"
{
    Properties
    {
        _BaseColumnTex ("Column Texture", 2D) = "black" {}
        _TreeInfoTex ("Tree Info (1 pixel per tree)", 2D) = "black" {}
        _TreeChunkTex0 ("Tree Chunk NW", 2D) = "black" {}
        _TreeChunkTex1 ("Tree Chunk N", 2D) = "black" {}
        _TreeChunkTex2 ("Tree Chunk NE", 2D) = "black" {}
        _TreeChunkTex3 ("Tree Chunk W", 2D) = "black" {}
        _TreeChunkTex4 ("Tree Chunk C", 2D) = "black" {}
        _TreeChunkTex5 ("Tree Chunk E", 2D) = "black" {}
        _TreeChunkTex6 ("Tree Chunk SW", 2D) = "black" {}
        _TreeChunkTex7 ("Tree Chunk S", 2D) = "black" {}
        _TreeChunkTex8 ("Tree Chunk SE", 2D) = "black" {}
        _TreeCount ("Number of trees", Int) = 0
        _WorldHeight ("World Height in blocks", Int) = 128
        _ChunkSizeXZ ("Chunk Size XZ", Int) = 16
        _AirBlockId ("Air Block ID", Int) = 0
        _GrassBlockId ("Grass Block ID", Int) = 2
        _DirtBlockId ("Dirt Block ID", Int) = 3
        _LogBlockId ("Log Block ID", Int) = 17
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
            sampler2D _TreeInfoTex;
            sampler2D _TreeChunkTex0;
            sampler2D _TreeChunkTex1;
            sampler2D _TreeChunkTex2;
            sampler2D _TreeChunkTex3;
            sampler2D _TreeChunkTex4;
            sampler2D _TreeChunkTex5;
            sampler2D _TreeChunkTex6;
            sampler2D _TreeChunkTex7;
            sampler2D _TreeChunkTex8;
            int _TreeCount;
            int _WorldHeight;
            int _ChunkSizeXZ;
            int _AirBlockId;
            int _GrassBlockId;
            int _DirtBlockId;
            int _LogBlockId;
            int _LeavesBlockId;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            int readBlockFromTex(sampler2D columnTex, int x, int y, int z)
            {
                if (x < 0 || x >= _ChunkSizeXZ || z < 0 || z >= _ChunkSizeXZ || y < 0 || y >= _WorldHeight)
                    return _AirBlockId;
                int packedHeight = _WorldHeight * _ChunkSizeXZ;
                int packedRow = y * _ChunkSizeXZ + z;
                float2 uv = float2((x + 0.5) / (float)_ChunkSizeXZ, (packedRow + 0.5) / (float)packedHeight);
                return (int)round(tex2Dlod(columnTex, float4(uv, 0, 0)).r * 255.0);
            }

            int readBlock(int x, int y, int z)
            {
                return readBlockFromTex(_BaseColumnTex, x, y, z);
            }

            float4 readTreeInfo(int index)
            {
                float2 uv = float2((index + 0.5) / 64.0, 0.5);
                return tex2Dlod(_TreeInfoTex, float4(uv, 0, 0));
            }

            int readTreeChunkBlock(int chunkOffsetX, int chunkOffsetZ, int x, int y, int z)
            {
                if (chunkOffsetZ < 0)
                {
                    if (chunkOffsetX < 0) return readBlockFromTex(_TreeChunkTex0, x, y, z);
                    if (chunkOffsetX > 0) return readBlockFromTex(_TreeChunkTex2, x, y, z);
                    return readBlockFromTex(_TreeChunkTex1, x, y, z);
                }
                if (chunkOffsetZ > 0)
                {
                    if (chunkOffsetX < 0) return readBlockFromTex(_TreeChunkTex6, x, y, z);
                    if (chunkOffsetX > 0) return readBlockFromTex(_TreeChunkTex8, x, y, z);
                    return readBlockFromTex(_TreeChunkTex7, x, y, z);
                }
                if (chunkOffsetX < 0) return readBlockFromTex(_TreeChunkTex3, x, y, z);
                if (chunkOffsetX > 0) return readBlockFromTex(_TreeChunkTex5, x, y, z);
                return readBlockFromTex(_TreeChunkTex4, x, y, z);
            }

            int getHighestSolidBlockInTreeChunk(int chunkOffsetX, int chunkOffsetZ, int x, int z)
            {
                for (int y = _WorldHeight - 1; y >= 0; y--)
                {
                    if (readTreeChunkBlock(chunkOffsetX, chunkOffsetZ, x, y, z) != _AirBlockId) return y;
                }
                return -1;
            }

            bool treeSpaceCheck(int treeX, int treeY, int treeZ, int treeHeight)
            {
                for (int checkY = treeY; checkY <= treeY + 1 + treeHeight; checkY++)
                {
                    int radius = 1;
                    if (checkY == treeY) radius = 0;
                    if (checkY >= treeY + 1 + treeHeight - 2) radius = 2;

                    for (int checkX = treeX - radius; checkX <= treeX + radius; checkX++)
                    {
                        for (int checkZ = treeZ - radius; checkZ <= treeZ + radius; checkZ++)
                        {
                            if (checkY < 0 || checkY >= _WorldHeight) return false;
                            // Out-of-column blocks are assumed air (can't read, so optimistic)
                            if (checkX < 0 || checkX >= _ChunkSizeXZ || checkZ < 0 || checkZ >= _ChunkSizeXZ) continue;
                            int blk = readBlock(checkX, checkY, checkZ);
                            if (blk != _AirBlockId && blk != _LeavesBlockId) return false;
                        }
                    }
                }
                return true;
            }

            float4 frag(v2f i) : SV_Target
            {
                int packedHeight = _WorldHeight * _ChunkSizeXZ;
                int x = clamp((int)floor(i.vertex.x), 0, _ChunkSizeXZ - 1);
                int packedRow = clamp((int)floor(i.vertex.y), 0, packedHeight - 1);
                int z = packedRow % _ChunkSizeXZ;
                int y = packedRow / _ChunkSizeXZ;

                int currentBlock = readBlock(x, y, z);

                for (int ti = 0; ti < _TreeCount; ti++)
                {
                    float4 info = readTreeInfo(ti);
                    int treeLocalX = (int)round(info.r * 255.0) - 128;
                    int treeLocalZ = (int)round(info.g * 255.0) - 128;
                    int treeHeight = (int)round(info.b * 255.0);
                    int cornerBits = (int)round(info.a * 255.0);

                    bool isBorderTree = (treeLocalX < 0 || treeLocalX >= _ChunkSizeXZ ||
                                         treeLocalZ < 0 || treeLocalZ >= _ChunkSizeXZ);

                    // Quick reject: if pixel is too far from tree trunk, skip
                    int dx = x - treeLocalX;
                    int dz = z - treeLocalZ;
                    if (dx < -2 || dx > 2 || dz < -2 || dz > 2) continue;

                    int trunkChunkOffsetX = treeLocalX < 0 ? -1 : (treeLocalX >= _ChunkSizeXZ ? 1 : 0);
                    int trunkChunkOffsetZ = treeLocalZ < 0 ? -1 : (treeLocalZ >= _ChunkSizeXZ ? 1 : 0);
                    int trunkLocalX = treeLocalX - trunkChunkOffsetX * _ChunkSizeXZ;
                    int trunkLocalZ = treeLocalZ - trunkChunkOffsetZ * _ChunkSizeXZ;
                    int surfaceY = getHighestSolidBlockInTreeChunk(trunkChunkOffsetX, trunkChunkOffsetZ, trunkLocalX, trunkLocalZ);
                    if (surfaceY < 0) continue;
                    int treeY = surfaceY + 1;
                    if (treeY < 1 || treeY + treeHeight + 1 >= _WorldHeight) continue;

                    // Ground must be grass or dirt
                    int blockBelow = readTreeChunkBlock(trunkChunkOffsetX, trunkChunkOffsetZ, trunkLocalX, surfaceY, trunkLocalZ);
                    if (blockBelow != _GrassBlockId && blockBelow != _DirtBlockId) continue;

                    if (!isBorderTree)
                    {
                        if (!treeSpaceCheck(treeLocalX, treeY, treeLocalZ, treeHeight)) continue;

                        // Dirt below trunk
                        if (x == treeLocalX && z == treeLocalZ && y == surfaceY)
                            currentBlock = _DirtBlockId;

                        // Trunk
                        if (x == treeLocalX && z == treeLocalZ && y >= treeY && y < treeY + treeHeight)
                        {
                            if (currentBlock == _AirBlockId || currentBlock == _LeavesBlockId)
                                currentBlock = _LogBlockId;
                        }
                    }

                    // Leaves (both border and in-column trees)
                    int cornerBitIndex = 0;
                    for (int leafY = treeY - 3 + treeHeight; leafY <= treeY + treeHeight; leafY++)
                    {
                        int yOffset = leafY - (treeY + treeHeight);
                        int leafRadius = 1 - yOffset / 2;
                        for (int lx = treeLocalX - leafRadius; lx <= treeLocalX + leafRadius; lx++)
                        {
                            for (int lz = treeLocalZ - leafRadius; lz <= treeLocalZ + leafRadius; lz++)
                            {
                                bool isCorner = (abs(lx - treeLocalX) == leafRadius && abs(lz - treeLocalZ) == leafRadius);
                                if (isCorner)
                                {
                                    bool skipCorner = ((cornerBits >> (cornerBitIndex & 7)) & 1) != 0;
                                    cornerBitIndex++;
                                    // Vanilla: top-layer corners are ALWAYS removed,
                                    // non-top-layer corners are removed 50% (when skipCorner)
                                    if (yOffset == 0 || skipCorner) continue;
                                }

                                if (lx == x && leafY == y && lz == z)
                                {
                                    if (currentBlock == _AirBlockId || currentBlock == _LeavesBlockId)
                                    {
                                        currentBlock = _LeavesBlockId;
                                    }
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
