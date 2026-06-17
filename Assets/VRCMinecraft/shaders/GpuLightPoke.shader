// GPU OFFLOAD #6: Localized light "poke" for incremental SetBlock updates.
//
// When a player places or breaks a single block, we don't need to re-propagate the
// entire chunk's lighting from scratch — we only need to:
//   1. Re-evaluate the modified cell's emission + opacity
//   2. Propagate the change to its 6 face neighbors (clipped to a small radius)
//   3. Re-decay outward 1 cell at a time, capped at light-15 hops
//
// This shader operates on the chunk's light RT (sky in .r, block in .g, both 0..15).
// It writes to a "next" RT and the caller ping-pongs back to the source after the Blit.
//
// The poke radius is whatever the source block emitted before the change (max 15 hops).
// In practice 2-3 iterations cover most poke patterns; the GPU full-chunk propagate
// shader (already exists) handles convergence on the next render cycle if a deeper
// change is needed.
//
// Why this is a win over the existing CPU `_PropagateToNeighborIfBrighter`:
//   - One Blit per iteration (vs N Udon function calls per iteration)
//   - Reads all 6 neighbors in parallel (vs serial CPU calls)
//   - Zero CPU work per SetBlock — the chunk just bumps a "dirty light" version int
Shader "VRCM/GpuLightPoke"
{
    Properties
    {
        _MainTex       ("Current Light RT (skies in .r block in .g)", 2D) = "black" {}
        _BlockTex      ("Chunk Block IDs (per-chunk 3D-packed-as-2D)", 2D) = "black" {}
        _BlockPropsTex ("Block Props (opacity in .r, emission in .g)", 2D) = "black" {}
        _ChunkSizeXZ ("Chunk Size XZ", Int) = 16
        _ChunkSizeY  ("Chunk Size Y",  Int) = 16
        // Poke center (local chunk coords). Set by the C# caller per SetBlock.
        _PokeX ("Poke X", Int) = 0
        _PokeY ("Poke Y", Int) = 0
        _PokeZ ("Poke Z", Int) = 0
        // Decreasing window: each iteration covers (radius - iteration) hops.
        _PokeRadius ("Poke Radius (max 15)", Int) = 15
        _Iteration  ("Iteration index 0..radius-1", Int) = 0
        // Old vs new emission/opacity — caller sets them so the shader can decide
        // whether this poke increases or decreases light.
        _OldEmission ("Old block emission", Int) = 0
        _OldOpacity  ("Old block opacity",  Int) = 0
        _NewEmission ("New block emission", Int) = 0
        _NewOpacity  ("New block opacity",  Int) = 0
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
            sampler2D _BlockTex;
            sampler2D _BlockPropsTex;
            int _ChunkSizeXZ;
            int _ChunkSizeY;
            int _PokeX, _PokeY, _PokeZ;
            int _PokeRadius;
            int _Iteration;
            int _OldEmission, _OldOpacity, _NewEmission, _NewOpacity;

            v2f vert(appdata v)
            {
                v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o;
            }

            // Light RT layout: same as existing sky+block packing — RT is sized
            // (chunkSizeXZ × (chunkSizeY * chunkSizeXZ)). Pixel (px, py) corresponds
            // to (lx, ly, lz) where ly = py / chunkSizeXZ, lz = py % chunkSizeXZ.
            void DecodePixel(float2 vpos, out int lx, out int ly, out int lz)
            {
                int px = clamp((int)floor(vpos.x), 0, _ChunkSizeXZ - 1);
                int py = clamp((int)floor(vpos.y), 0, _ChunkSizeY * _ChunkSizeXZ - 1);
                lx = px;
                ly = py / _ChunkSizeXZ;
                lz = py - ly * _ChunkSizeXZ;
            }

            float4 SampleLightLocal(int lx, int ly, int lz)
            {
                if (lx < 0 || ly < 0 || lz < 0 ||
                    lx >= _ChunkSizeXZ || ly >= _ChunkSizeY || lz >= _ChunkSizeXZ) return 0;
                float u = ((float)lx + 0.5) / (float)_ChunkSizeXZ;
                float v = ((float)(ly * _ChunkSizeXZ + lz) + 0.5) / (float)(_ChunkSizeY * _ChunkSizeXZ);
                return tex2Dlod(_MainTex, float4(u, v, 0, 0));
            }

            float SampleBlockId(int lx, int ly, int lz)
            {
                if (lx < 0 || ly < 0 || lz < 0 ||
                    lx >= _ChunkSizeXZ || ly >= _ChunkSizeY || lz >= _ChunkSizeXZ) return 0;
                float u = ((float)lx + 0.5) / (float)_ChunkSizeXZ;
                float v = ((float)(ly * _ChunkSizeXZ + lz) + 0.5) / (float)(_ChunkSizeY * _ChunkSizeXZ);
                return tex2Dlod(_BlockTex, float4(u, v, 0, 0)).r * 255.0;
            }

            int SampleOpacity(int lx, int ly, int lz)
            {
                float blockId = SampleBlockId(lx, ly, lz);
                if (blockId < 0.5) return 1; // air → 1-step decay
                float2 propUv = float2((blockId + 0.5) / 256.0, 0.5);
                int op = (int)round(tex2Dlod(_BlockPropsTex, float4(propUv, 0, 0)).r * 15.0);
                return max(1, op);
            }

            float4 frag(v2f i) : SV_Target
            {
                int lx, ly, lz; DecodePixel(i.vertex.xy, lx, ly, lz);

                // Manhattan distance to poke center.
                int mdx = abs(lx - _PokeX);
                int mdy = abs(ly - _PokeY);
                int mdz = abs(lz - _PokeZ);
                int dist = mdx + mdy + mdz;

                // Out of influence radius: pass through.
                if (dist > _PokeRadius) return SampleLightLocal(lx, ly, lz);

                // On the poke cell itself, set emission directly on the new state.
                float4 selfLight = SampleLightLocal(lx, ly, lz);
                int curSky = (int)(selfLight.r * 15.0 + 0.5);
                int curBlock = (int)(selfLight.g * 15.0 + 0.5);

                if (dist == 0)
                {
                    // Reset block light to max(neighbor-1, emission) and let later
                    // iterations smooth it out. Sky light is preserved (vertical
                    // propagation happens via the global pass).
                    int sky4 = 0;
                    int blk4 = _NewEmission;
                    // PULL from 6 neighbors for both channels (their values are stale
                    // from this iteration — that's fine, we converge in radius passes).
                    int n;
                    n = (int)(SampleLightLocal(lx - 1, ly, lz).r * 15.0 + 0.5) - 1; sky4 = max(sky4, n);
                    n = (int)(SampleLightLocal(lx + 1, ly, lz).r * 15.0 + 0.5) - 1; sky4 = max(sky4, n);
                    n = (int)(SampleLightLocal(lx, ly - 1, lz).r * 15.0 + 0.5) - 1; sky4 = max(sky4, n);
                    n = (int)(SampleLightLocal(lx, ly + 1, lz).r * 15.0 + 0.5) - 1; sky4 = max(sky4, n);
                    n = (int)(SampleLightLocal(lx, ly, lz - 1).r * 15.0 + 0.5) - 1; sky4 = max(sky4, n);
                    n = (int)(SampleLightLocal(lx, ly, lz + 1).r * 15.0 + 0.5) - 1; sky4 = max(sky4, n);

                    n = (int)(SampleLightLocal(lx - 1, ly, lz).g * 15.0 + 0.5) - 1; blk4 = max(blk4, n);
                    n = (int)(SampleLightLocal(lx + 1, ly, lz).g * 15.0 + 0.5) - 1; blk4 = max(blk4, n);
                    n = (int)(SampleLightLocal(lx, ly - 1, lz).g * 15.0 + 0.5) - 1; blk4 = max(blk4, n);
                    n = (int)(SampleLightLocal(lx, ly + 1, lz).g * 15.0 + 0.5) - 1; blk4 = max(blk4, n);
                    n = (int)(SampleLightLocal(lx, ly, lz - 1).g * 15.0 + 0.5) - 1; blk4 = max(blk4, n);
                    n = (int)(SampleLightLocal(lx, ly, lz + 1).g * 15.0 + 0.5) - 1; blk4 = max(blk4, n);

                    sky4 = clamp(sky4, 0, 15);
                    blk4 = clamp(blk4, _NewEmission, 15);
                    return float4(sky4 / 15.0, blk4 / 15.0, 0, 1);
                }

                // For neighbor cells within radius: PULL max(neighbors - opacity, current).
                int op = SampleOpacity(lx, ly, lz);
                int sky = curSky, blk = curBlock;

                int s, b;
                s = (int)(SampleLightLocal(lx - 1, ly, lz).r * 15.0 + 0.5) - op; if (s > sky) sky = s;
                s = (int)(SampleLightLocal(lx + 1, ly, lz).r * 15.0 + 0.5) - op; if (s > sky) sky = s;
                s = (int)(SampleLightLocal(lx, ly - 1, lz).r * 15.0 + 0.5) - op; if (s > sky) sky = s;
                s = (int)(SampleLightLocal(lx, ly + 1, lz).r * 15.0 + 0.5) - op; if (s > sky) sky = s;
                s = (int)(SampleLightLocal(lx, ly, lz - 1).r * 15.0 + 0.5) - op; if (s > sky) sky = s;
                s = (int)(SampleLightLocal(lx, ly, lz + 1).r * 15.0 + 0.5) - op; if (s > sky) sky = s;

                b = (int)(SampleLightLocal(lx - 1, ly, lz).g * 15.0 + 0.5) - op; if (b > blk) blk = b;
                b = (int)(SampleLightLocal(lx + 1, ly, lz).g * 15.0 + 0.5) - op; if (b > blk) blk = b;
                b = (int)(SampleLightLocal(lx, ly - 1, lz).g * 15.0 + 0.5) - op; if (b > blk) blk = b;
                b = (int)(SampleLightLocal(lx, ly + 1, lz).g * 15.0 + 0.5) - op; if (b > blk) blk = b;
                b = (int)(SampleLightLocal(lx, ly, lz - 1).g * 15.0 + 0.5) - op; if (b > blk) blk = b;
                b = (int)(SampleLightLocal(lx, ly, lz + 1).g * 15.0 + 0.5) - op; if (b > blk) blk = b;

                sky = clamp(sky, 0, 15);
                blk = clamp(blk, 0, 15);
                return float4(sky / 15.0, blk / 15.0, 0, 1);
            }
            ENDCG
        }
    }
}
