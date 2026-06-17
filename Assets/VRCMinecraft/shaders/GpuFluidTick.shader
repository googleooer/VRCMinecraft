// GPU OFFLOAD #7: Fluid (water/lava) cellular automaton tick.
//
// Beta's water/lava flow algorithm is purely a function of the current chunk's block IDs +
// metadata (flow level) — making it a perfect Blit-friendly CA. Each tick:
//   - Source blocks (meta=0) try to flow down into the cell below if empty
//   - Flowing blocks decrement meta as they spread sideways
//   - Adjacent same-source blocks at meta=0 promote a meta=1 cell to meta=0 (Beta source-rule)
//   - Lava has slower rate + sticky chance for retreating
//
// This shader runs ONCE per fluid tick per chunk that has any scheduled fluid ticks.
// Output is a new (blockId, meta) RT pair that the chunk swaps in.
//
// Parity note: Beta's "rand.nextInt(4) != 0" sticky-flow gate for lava uses a
// deterministic Wang hash of (chunkX, chunkZ, x, y, z, tickCounter) here, matching Java's
// per-tick RNG advance count (close enough for visual parity; exact match requires CPU sync).
Shader "VRCM/GpuFluidTick"
{
    Properties
    {
        _BlockTex  ("Chunk Block IDs",  2D) = "black" {}
        _MetaTex   ("Chunk Metadata",   2D) = "black" {}
        _SentinelTex ("Sentinel Block IDs (with border)", 2D) = "black" {}
        _ChunkSizeXZ ("Chunk Size XZ", Int) = 16
        _ChunkSizeY  ("Chunk Size Y",  Int) = 16
        _IsLava    ("Is Lava (0=water,1=lava)", Int) = 0
        _TickCounter ("Tick Counter (for sticky-flow RNG)", Int) = 0
        _ChunkX ("Chunk X (for hash)", Int) = 0
        _ChunkZ ("Chunk Z (for hash)", Int) = 0
        // Block ID constants — caller sets these once at material init.
        _BWaterFlow ("Water Flowing ID", Int) = 8
        _BWaterStill ("Water Still ID", Int) = 9
        _BLavaFlow ("Lava Flowing ID", Int) = 10
        _BLavaStill ("Lava Still ID", Int) = 11
        _BAir ("Air ID", Int) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        CGINCLUDE
        #include "UnityCG.cginc"

        struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
        struct v2f     { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

        sampler2D _BlockTex;
        sampler2D _MetaTex;
        sampler2D _SentinelTex;
        int _ChunkSizeXZ;
        int _ChunkSizeY;
        int _IsLava;
        int _TickCounter;
        int _ChunkX, _ChunkZ;
        int _BWaterFlow, _BWaterStill, _BLavaFlow, _BLavaStill, _BAir;

        v2f vert(appdata v)
        {
            v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o;
        }

        void DecodePixel(float2 vpos, out int lx, out int ly, out int lz)
        {
            int px = clamp((int)floor(vpos.x), 0, _ChunkSizeXZ - 1);
            int py = clamp((int)floor(vpos.y), 0, _ChunkSizeY * _ChunkSizeXZ - 1);
            lx = px;
            ly = py / _ChunkSizeXZ;
            lz = py - ly * _ChunkSizeXZ;
        }

        // Sample block ID at sentinel coords (with 1-voxel border from neighbors).
        int SampleSentinelBlock(int sx, int sy, int sz)
        {
            int sxz = _ChunkSizeXZ + 2;
            int syt = _ChunkSizeY + 2;
            int csx = sx + 1, csy = sy + 1, csz = sz + 1;
            if (csx < 0 || csy < 0 || csz < 0 || csx >= sxz || csy >= syt || csz >= sxz) return _BAir;
            float u = ((float)csx + 0.5) / (float)sxz;
            float v = ((float)(csy * sxz + csz) + 0.5) / (float)(sxz * syt);
            return (int)round(tex2Dlod(_SentinelTex, float4(u, v, 0, 0)).r * 255.0);
        }

        int SampleBlock(int lx, int ly, int lz)
        {
            if (lx < 0 || ly < 0 || lz < 0 || lx >= _ChunkSizeXZ || ly >= _ChunkSizeY || lz >= _ChunkSizeXZ)
                return SampleSentinelBlock(lx, ly, lz);
            float u = ((float)lx + 0.5) / (float)_ChunkSizeXZ;
            float v = ((float)(ly * _ChunkSizeXZ + lz) + 0.5) / (float)(_ChunkSizeY * _ChunkSizeXZ);
            return (int)round(tex2Dlod(_BlockTex, float4(u, v, 0, 0)).r * 255.0);
        }

        int SampleMeta(int lx, int ly, int lz)
        {
            if (lx < 0 || ly < 0 || lz < 0 || lx >= _ChunkSizeXZ || ly >= _ChunkSizeY || lz >= _ChunkSizeXZ) return 0;
            float u = ((float)lx + 0.5) / (float)_ChunkSizeXZ;
            float v = ((float)(ly * _ChunkSizeXZ + lz) + 0.5) / (float)(_ChunkSizeY * _ChunkSizeXZ);
            return (int)round(tex2Dlod(_MetaTex, float4(u, v, 0, 0)).r * 15.0);
        }

        // Wang-hash style RNG for sticky-flow gate.
        uint WangHash(uint x)
        {
            x = (x ^ 61u) ^ (x >> 16);
            x *= 9u;
            x = x ^ (x >> 4);
            x *= 0x27d4eb2du;
            x = x ^ (x >> 15);
            return x;
        }

        bool IsFluidBlock(int blockId, int isLava)
        {
            if (isLava == 1) return blockId == _BLavaFlow || blockId == _BLavaStill;
            return blockId == _BWaterFlow || blockId == _BWaterStill;
        }

        bool IsSourceBlock(int blockId, int meta, int isLava)
        {
            int still = isLava == 1 ? _BLavaStill : _BWaterStill;
            return blockId == still && meta == 0;
        }

        bool IsPassable(int blockId, int isLava)
        {
            if (blockId == _BAir) return true;
            // Same-fluid blocks are "displaceable" by lower-meta of same fluid.
            return IsFluidBlock(blockId, isLava);
        }
        ENDCG

        // Pass 0: emit new block ID
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int lx, ly, lz; DecodePixel(i.vertex.xy, lx, ly, lz);
                int self = SampleBlock(lx, ly, lz);
                int selfMeta = SampleMeta(lx, ly, lz);

                int flowingId = _IsLava == 1 ? _BLavaFlow : _BWaterFlow;
                int stillId   = _IsLava == 1 ? _BLavaStill : _BWaterStill;

                // If we're air, check if a fluid neighbor would flow into us.
                if (self == _BAir)
                {
                    // Above source flows down?
                    int above = SampleBlock(lx, ly + 1, lz);
                    if (IsFluidBlock(above, _IsLava))
                    {
                        return float4(flowingId / 255.0, 0, 0, 1);
                    }

                    // Horizontal: any flow-decay-<7 fluid neighbor spreads to us?
                    // Beta uses level 7 as max-decay; lava skips by 2 outside Hell.
                    int decayStep = _IsLava == 1 ? 2 : 1;
                    int minNeighborDecay = 8;
                    int n = SampleBlock(lx - 1, ly, lz); int nm = SampleMeta(lx - 1, ly, lz);
                    if (IsFluidBlock(n, _IsLava) && nm < 7) minNeighborDecay = min(minNeighborDecay, nm + decayStep);
                    n = SampleBlock(lx + 1, ly, lz); nm = SampleMeta(lx + 1, ly, lz);
                    if (IsFluidBlock(n, _IsLava) && nm < 7) minNeighborDecay = min(minNeighborDecay, nm + decayStep);
                    n = SampleBlock(lx, ly, lz - 1); nm = SampleMeta(lx, ly, lz - 1);
                    if (IsFluidBlock(n, _IsLava) && nm < 7) minNeighborDecay = min(minNeighborDecay, nm + decayStep);
                    n = SampleBlock(lx, ly, lz + 1); nm = SampleMeta(lx, ly, lz + 1);
                    if (IsFluidBlock(n, _IsLava) && nm < 7) minNeighborDecay = min(minNeighborDecay, nm + decayStep);

                    if (minNeighborDecay < 8)
                    {
                        // Sticky-flow gate for lava: 75% chance to skip the spread.
                        if (_IsLava == 1)
                        {
                            uint h = WangHash((uint)(lx * 73856093 ^ ly * 19349663 ^ lz * 83492791
                                                    ^ _ChunkX * 2147483647 ^ _ChunkZ * 1664525
                                                    ^ _TickCounter * 1013904223));
                            if ((h & 3u) != 0u) return float4(_BAir / 255.0, 0, 0, 1);
                        }
                        return float4(flowingId / 255.0, 0, 0, 1);
                    }

                    return float4(_BAir / 255.0, 0, 0, 1);
                }

                // If we're a fluid: check if 2+ adjacent source-blocks promote us to still
                // (Beta's "source generation" rule for water).
                if (IsFluidBlock(self, _IsLava) && _IsLava == 0)
                {
                    int sourceCount = 0;
                    if (IsSourceBlock(SampleBlock(lx - 1, ly, lz), SampleMeta(lx - 1, ly, lz), _IsLava)) sourceCount++;
                    if (IsSourceBlock(SampleBlock(lx + 1, ly, lz), SampleMeta(lx + 1, ly, lz), _IsLava)) sourceCount++;
                    if (IsSourceBlock(SampleBlock(lx, ly, lz - 1), SampleMeta(lx, ly, lz - 1), _IsLava)) sourceCount++;
                    if (IsSourceBlock(SampleBlock(lx, ly, lz + 1), SampleMeta(lx, ly, lz + 1), _IsLava)) sourceCount++;
                    if (sourceCount >= 2) return float4(stillId / 255.0, 0, 0, 1);
                }

                return float4((float)self / 255.0, 0, 0, 1);
            }
            ENDCG
        }

        // Pass 1: emit new metadata
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int lx, ly, lz; DecodePixel(i.vertex.xy, lx, ly, lz);
                int self = SampleBlock(lx, ly, lz);

                // Air → meta 0
                if (self == _BAir)
                {
                    int above = SampleBlock(lx, ly + 1, lz);
                    if (IsFluidBlock(above, _IsLava)) return 0; // falling from above
                    int decayStep = _IsLava == 1 ? 2 : 1;
                    int minD = 8;
                    int n;
                    n = SampleMeta(lx - 1, ly, lz); if (IsFluidBlock(SampleBlock(lx - 1, ly, lz), _IsLava) && n < 7) minD = min(minD, n + decayStep);
                    n = SampleMeta(lx + 1, ly, lz); if (IsFluidBlock(SampleBlock(lx + 1, ly, lz), _IsLava) && n < 7) minD = min(minD, n + decayStep);
                    n = SampleMeta(lx, ly, lz - 1); if (IsFluidBlock(SampleBlock(lx, ly, lz - 1), _IsLava) && n < 7) minD = min(minD, n + decayStep);
                    n = SampleMeta(lx, ly, lz + 1); if (IsFluidBlock(SampleBlock(lx, ly, lz + 1), _IsLava) && n < 7) minD = min(minD, n + decayStep);
                    if (minD < 8) return float4((float)minD / 15.0, 0, 0, 1);
                    return 0;
                }

                return float4((float)SampleMeta(lx, ly, lz) / 15.0, 0, 0, 1);
            }
            ENDCG
        }
    }
}
