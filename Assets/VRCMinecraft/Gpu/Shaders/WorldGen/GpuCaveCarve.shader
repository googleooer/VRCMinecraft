Shader "VRCM/GpuCaveCarve"
{
    Properties
    {
        _MainTex ("Base Fill Texture", 2D) = "black" {}
        _WorldHeight ("World Height", Int) = 128
        _ChunkSizeXZ ("Chunk Size XZ", Int) = 16
        _ChunkX ("Chunk X", Int) = 0
        _ChunkZ ("Chunk Z", Int) = 0
        _WorldSeedHi ("World Seed Hi32", Int) = 0
        _WorldSeedLo ("World Seed Lo32", Int) = 0
        _HashAHi ("Hash A Hi32", Int) = 0
        _HashALo ("Hash A Lo32", Int) = 0
        _HashBHi ("Hash B Hi32", Int) = 0
        _HashBLo ("Hash B Lo32", Int) = 0
        _FlipXAxis ("Flip X Axis", Int) = 1
        _StoneBlockId ("Stone Block ID", Int) = 1
        _DirtBlockId ("Dirt Block ID", Int) = 3
        _GrassBlockId ("Grass Block ID", Int) = 2
        _WaterBlockId ("Water Block ID", Int) = 9
        _StationaryWaterBlockId ("Stationary Water Block ID", Int) = 9
        _LavaBlockId ("Lava Block ID", Int) = 11
        _GenerateCaves ("Generate Caves", Int) = 1
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
            #pragma target 5.0

            #include "UnityCG.cginc"
            #include "JavaRandomGPU.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

            sampler2D _MainTex;
            int _WorldHeight, _ChunkSizeXZ;
            int _ChunkX, _ChunkZ;
            int _WorldSeedHi, _WorldSeedLo;
            int _HashAHi, _HashALo, _HashBHi, _HashBLo;
            int _FlipXAxis;
            int _StoneBlockId, _DirtBlockId, _GrassBlockId;
            int _WaterBlockId, _StationaryWaterBlockId, _LavaBlockId;
            int _GenerateCaves;

            static const int CAVE_RADIUS = 8;
            static const float MC_PI = 3.14159265;
            static const int MAX_WORM_STEPS = 200;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            int readBlock(int outX, int y, int z)
            {
                int packedRow = y * _ChunkSizeXZ + z;
                float2 uv = float2(
                    ((float)outX + 0.5) / (float)_ChunkSizeXZ,
                    ((float)packedRow + 0.5) / (float)(_WorldHeight * _ChunkSizeXZ)
                );
                return (int)(tex2Dlod(_MainTex, float4(uv, 0, 0)).r * 255.0 + 0.5);
            }

            // Simulate one releaseEntitySkin call for a single block.
            // Returns true if this block is carved by this worm.
            // chunkRng is passed by REFERENCE so it's advanced by the nextLong() consumption.
            bool walkWorm(inout uint2 chunkRng, int3 blockPos,
                          float3 startPos, float wormSize, float startYaw, float startPitch,
                          int startStep, int maxSteps, float vertScale, bool isRoom)
            {
                // releaseEntitySkin line 15: Random var23 = new Random(this.rand.nextLong())
                uint2 wormRng = jrSetSeedFromNextLong(chunkRng);

                float chunkCenterX = (float)(_ChunkX * 16 + 8);
                float chunkCenterZ = (float)(_ChunkZ * 16 + 8);

                if (maxSteps <= 0)
                {
                    int maxBlockRadius = CAVE_RADIUS * 16 - 16;
                    maxSteps = maxBlockRadius - jrNextIntBound(wormRng, maxBlockRadius / 4);
                }

                if (startStep == -1)
                {
                    startStep = maxSteps / 2;
                    // isRoom = true; (caller already sets this)
                }

                int splitDist = jrNextIntBound(wormRng, maxSteps / 2) + maxSteps / 4;
                bool allowSteep = (jrNextIntBound(wormRng, 6) == 0);

                float curX = startPos.x, curY = startPos.y, curZ = startPos.z;
                float yaw = startYaw, pitch = startPitch;
                float hChange = 0.0, vChange = 0.0;

                for (int step = startStep; step < maxSteps && step < startStep + MAX_WORM_STEPS; step++)
                {
                    float radiusH = 1.5 + sin((float)step * MC_PI / (float)maxSteps) * wormSize;
                    float radiusV = radiusH * vertScale;

                    float cosP = cos(pitch);
                    curX += cos(yaw) * cosP;
                    curY += sin(pitch);
                    curZ += sin(yaw) * cosP;

                    pitch = allowSteep ? pitch * 0.92 : pitch * 0.7;
                    pitch += vChange * 0.1;
                    yaw += hChange * 0.1;
                    vChange *= 0.9;
                    hChange *= 0.75;
                    vChange += (jrNextFloat(wormRng) - jrNextFloat(wormRng)) * jrNextFloat(wormRng) * 2.0;
                    hChange += (jrNextFloat(wormRng) - jrNextFloat(wormRng)) * jrNextFloat(wormRng) * 4.0;

                    // Fork check (only for non-room branches with size > 1)
                    if (!isRoom && step == splitDist && wormSize > 1.0)
                    {
                        // Fork consumes from wormRng (var23) for the fork sizes
                        float forkSizeA = jrNextFloat(wormRng) * 0.5 + 0.5;
                        float forkSizeB = jrNextFloat(wormRng) * 0.5 + 0.5;

                        // Fork A: recursive releaseEntitySkin call
                        // Each child creates its RNG from this.rand (= chunkRng), NOT from wormRng
                        uint2 forkARng = jrSetSeedFromNextLong(chunkRng);
                        // Consume wormRng state that the child's constructor would use
                        int fAMax = maxSteps;
                        // The child starts at 'step' with the fork params, walks to fAMax
                        int fAUnused0 = jrNextIntBound(forkARng, 1); // maxSteps reuse — no, maxSteps passed from parent
                        // Actually the child receives maxSteps from parent (var14).
                        // The child computes: if(var14 <= 0) compute new maxSteps. Since var14=maxSteps > 0, skip.
                        // if(var13 == -1) — var13 = step, not -1. So noSplitBranch stays false.
                        int fASplitDist = jrNextIntBound(forkARng, fAMax / 2) + fAMax / 4;
                        bool fASteep = (jrNextIntBound(forkARng, 6) == 0);
                        float fX = curX, fY = curY, fZ = curZ;
                        float fYaw = yaw - MC_PI * 0.5;
                        float fPitch = pitch / 3.0;
                        float fH = 0.0, fV = 0.0;

                        for (int fs = step; fs < fAMax && fs < step + MAX_WORM_STEPS; fs++)
                        {
                            float fRH = 1.5 + sin((float)fs * MC_PI / (float)fAMax) * forkSizeA;
                            float fRV = fRH * 1.0; // vertScale = 1.0 for forks
                            float fCosP = cos(fPitch);
                            fX += cos(fYaw) * fCosP;
                            fY += sin(fPitch);
                            fZ += sin(fYaw) * fCosP;
                            fPitch = fASteep ? fPitch * 0.92 : fPitch * 0.7;
                            fPitch += fV * 0.1;
                            fYaw += fH * 0.1;
                            fV *= 0.9; fH *= 0.75;
                            fV += (jrNextFloat(forkARng) - jrNextFloat(forkARng)) * jrNextFloat(forkARng) * 2.0;
                            fH += (jrNextFloat(forkARng) - jrNextFloat(forkARng)) * jrNextFloat(forkARng) * 4.0;

                            // Forked children don't re-fork (their wormSize < 1.0)
                            // Skip check: 25% skip
                            if (jrNextIntBound(forkARng, 4) == 0) continue;

                            float fdcx = fX - chunkCenterX;
                            float fdcz = fZ - chunkCenterZ;
                            float fRemain = (float)(fAMax - fs);
                            float fBuf = forkSizeA + 18.0;
                            if (fdcx*fdcx + fdcz*fdcz - fRemain*fRemain > fBuf*fBuf) break;
                            if (fX < chunkCenterX - 16.0 - fRH * 2.0 ||
                                fZ < chunkCenterZ - 16.0 - fRH * 2.0 ||
                                fX > chunkCenterX + 16.0 + fRH * 2.0 ||
                                fZ > chunkCenterZ + 16.0 + fRH * 2.0) continue;

                            float dx = ((float)blockPos.x + 0.5 - fX) / fRH;
                            float dz = ((float)blockPos.z + 0.5 - fZ) / fRH;
                            if (dx*dx + dz*dz < 1.0)
                            {
                                float dy = ((float)blockPos.y + 0.5 - fY) / fRV;
                                if (dy > -0.7 && dx*dx + dy*dy + dz*dz < 1.0)
                                    return true;
                            }
                        }

                        // Fork B
                        uint2 forkBRng = jrSetSeedFromNextLong(chunkRng);
                        int fBSplitDist = jrNextIntBound(forkBRng, maxSteps / 2) + maxSteps / 4;
                        bool fBSteep = (jrNextIntBound(forkBRng, 6) == 0);
                        fX = curX; fY = curY; fZ = curZ;
                        float fBYaw = yaw + MC_PI * 0.5;
                        float fBPitch = pitch / 3.0;
                        fH = 0.0; fV = 0.0;

                        for (int fs = step; fs < maxSteps && fs < step + MAX_WORM_STEPS; fs++)
                        {
                            float fRH = 1.5 + sin((float)fs * MC_PI / (float)maxSteps) * forkSizeB;
                            float fRV = fRH * 1.0;
                            float fCosP = cos(fBPitch);
                            fX += cos(fBYaw) * fCosP;
                            fY += sin(fBPitch);
                            fZ += sin(fBYaw) * fCosP;
                            fBPitch = fBSteep ? fBPitch * 0.92 : fBPitch * 0.7;
                            fBPitch += fV * 0.1;
                            fBYaw += fH * 0.1;
                            fV *= 0.9; fH *= 0.75;
                            fV += (jrNextFloat(forkBRng) - jrNextFloat(forkBRng)) * jrNextFloat(forkBRng) * 2.0;
                            fH += (jrNextFloat(forkBRng) - jrNextFloat(forkBRng)) * jrNextFloat(forkBRng) * 4.0;

                            if (jrNextIntBound(forkBRng, 4) == 0) continue;

                            float fdcx = fX - chunkCenterX;
                            float fdcz = fZ - chunkCenterZ;
                            float fRemain = (float)(maxSteps - fs);
                            float fBuf = forkSizeB + 18.0;
                            if (fdcx*fdcx + fdcz*fdcz - fRemain*fRemain > fBuf*fBuf) break;
                            if (fX < chunkCenterX - 16.0 - fRH * 2.0 ||
                                fZ < chunkCenterZ - 16.0 - fRH * 2.0 ||
                                fX > chunkCenterX + 16.0 + fRH * 2.0 ||
                                fZ > chunkCenterZ + 16.0 + fRH * 2.0) continue;

                            float dx = ((float)blockPos.x + 0.5 - fX) / fRH;
                            float dz = ((float)blockPos.z + 0.5 - fZ) / fRH;
                            if (dx*dx + dz*dz < 1.0)
                            {
                                float dy = ((float)blockPos.y + 0.5 - fY) / fRV;
                                if (dy > -0.7 && dx*dx + dy*dy + dz*dz < 1.0)
                                    return true;
                            }
                        }

                        return false; // parent returns after forking
                    }

                    // Non-room: 25% skip
                    if (!isRoom && jrNextIntBound(wormRng, 4) == 0) continue;

                    // Distance cull
                    if (!isRoom)
                    {
                        float dcx = curX - chunkCenterX;
                        float dcz = curZ - chunkCenterZ;
                        float remain = (float)(maxSteps - step);
                        float buf = wormSize + 18.0;
                        if (dcx*dcx + dcz*dcz - remain*remain > buf*buf) return false;
                    }

                    // Bounds check — rooms continue walking if out of bounds
                    if (curX < chunkCenterX - 16.0 - radiusH * 2.0 ||
                        curZ < chunkCenterZ - 16.0 - radiusH * 2.0 ||
                        curX > chunkCenterX + 16.0 + radiusH * 2.0 ||
                        curZ > chunkCenterZ + 16.0 + radiusH * 2.0)
                    {
                        continue;
                    }

                    // Ellipsoid carving test
                    float dx = ((float)blockPos.x + 0.5 - curX) / radiusH;
                    float dz = ((float)blockPos.z + 0.5 - curZ) / radiusH;
                    if (dx * dx + dz * dz < 1.0)
                    {
                        float dy = ((float)blockPos.y + 0.5 - curY) / radiusV;
                        if (dy > -0.7 && dx * dx + dy * dy + dz * dz < 1.0)
                            return true;
                    }

                    // Room: Java breaks out of the for-loop after first in-bounds carve attempt
                    // (regardless of whether the block was actually inside the ellipsoid)
                    if (isRoom) return false;
                }

                return false;
            }

            // Check if block at (blockX, blockY, blockZ) in world coords is carved
            bool isBlockCarved(int3 blockPos)
            {
                int2 hashA = int2(_HashAHi, _HashALo);
                int2 hashB = int2(_HashBHi, _HashBLo);

                for (int ncx = _ChunkX - CAVE_RADIUS; ncx <= _ChunkX + CAVE_RADIUS; ncx++)
                {
                    for (int ncz = _ChunkZ - CAVE_RADIUS; ncz <= _ChunkZ + CAVE_RADIUS; ncz++)
                    {
                        // MapGenBase: rand.setSeed((long)ncx * hashA + (long)ncz * hashB ^ worldSeed)
                        int2 seedVal = _jr64Add(
                            _jr64Mul(int2(ncx < 0 ? -1 : 0, ncx), hashA),
                            _jr64Mul(int2(ncz < 0 ? -1 : 0, ncz), hashB)
                        );
                        seedVal = _jr64Xor(seedVal, int2(_WorldSeedHi, _WorldSeedLo));
                        uint2 chunkRng = jrSetSeed(seedVal.x, (uint)seedVal.y);

                        // func_868_a: worm count
                        int wormCount = jrNextIntBound(chunkRng,
                                        jrNextIntBound(chunkRng,
                                        jrNextIntBound(chunkRng, 40) + 1) + 1);
                        if (jrNextIntBound(chunkRng, 15) != 0)
                            wormCount = 0;

                        for (int wi = 0; wi < wormCount; wi++)
                        {
                            float wormX = (float)(ncx * 16 + jrNextIntBound(chunkRng, 16));
                            float wormY = (float)jrNextIntBound(chunkRng,
                                          jrNextIntBound(chunkRng, 120) + 8);
                            float wormZ = (float)(ncz * 16 + jrNextIntBound(chunkRng, 16));

                            int branchCount = 1;
                            bool hasRoom = (jrNextIntBound(chunkRng, 4) == 0);

                            if (hasRoom)
                            {
                                // func_870_a: room branch
                                float roomSize = 1.0 + jrNextFloat(chunkRng) * 6.0;
                                if (walkWorm(chunkRng, blockPos,
                                             float3(wormX, wormY, wormZ),
                                             roomSize, 0.0, 0.0, -1, -1, 0.5, true))
                                    return true;
                                branchCount += jrNextIntBound(chunkRng, 4);
                            }

                            for (int bi = 0; bi < branchCount; bi++)
                            {
                                float bYaw = jrNextFloat(chunkRng) * MC_PI * 2.0;
                                float bPitch = (jrNextFloat(chunkRng) - 0.5) * 2.0 / 8.0;
                                float bSize = jrNextFloat(chunkRng) * 2.0 + jrNextFloat(chunkRng);
                                if (walkWorm(chunkRng, blockPos,
                                             float3(wormX, wormY, wormZ),
                                             bSize, bYaw, bPitch, 0, 0, 1.0, false))
                                    return true;
                            }
                        }
                    }
                }

                return false;
            }

            float4 frag(v2f i) : SV_Target
            {
                int packedHeight = _WorldHeight * _ChunkSizeXZ;
                int xOut = clamp((int)floor(i.vertex.x), 0, _ChunkSizeXZ - 1);
                int packedRow = clamp((int)floor(i.vertex.y), 0, packedHeight - 1);
                int localZ = packedRow % _ChunkSizeXZ;
                int y = packedRow / _ChunkSizeXZ;
                int localX = _FlipXAxis == 1 ? (_ChunkSizeXZ - 1 - xOut) : xOut;

                int blockId = readBlock(xOut, y, localZ);

                if (_GenerateCaves == 0 || blockId == 0 || y < 1 || y > 120)
                    return float4(blockId / 255.0, 0, 0, 1);

                bool carveable = (blockId == _StoneBlockId || blockId == _DirtBlockId || blockId == _GrassBlockId);
                if (!carveable)
                    return float4(blockId / 255.0, 0, 0, 1);

                int worldX = localX + _ChunkX * 16;
                int worldZ = localZ + _ChunkZ * 16;

                if (isBlockCarved(int3(worldX, y, worldZ)))
                {
                    // Water proximity check — scan the bounding face of the carving volume
                    // Simplified: check 3x3x3 neighborhood for water
                    bool waterNearby = false;
                    [unroll] for (int wy = -1; wy <= 1 && !waterNearby; wy++)
                    [unroll] for (int wx = -1; wx <= 1 && !waterNearby; wx++)
                    [unroll] for (int wz = -1; wz <= 1 && !waterNearby; wz++)
                    {
                        int cx = localX + wx;
                        int cy = y + wy;
                        int cz = localZ + wz;
                        if (cx >= 0 && cx < _ChunkSizeXZ && cz >= 0 && cz < _ChunkSizeXZ &&
                            cy >= 0 && cy < _WorldHeight)
                        {
                            int outCx = _FlipXAxis == 1 ? (_ChunkSizeXZ - 1 - cx) : cx;
                            int adj = readBlock(outCx, cy, cz);
                            if (adj == _WaterBlockId || adj == _StationaryWaterBlockId)
                                waterNearby = true;
                        }
                    }

                    if (!waterNearby)
                    {
                        if (y < 10)
                            blockId = _LavaBlockId;
                        else
                            blockId = 0;
                    }
                }

                return float4(blockId / 255.0, 0, 0, 1);
            }
            ENDCG
        }
    }
}
