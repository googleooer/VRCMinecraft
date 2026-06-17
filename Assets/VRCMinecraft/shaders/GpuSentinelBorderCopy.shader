// GPU OFFLOAD #4: Sentinel-border copy.
//
// The mesh builder needs each chunk's block data plus a 1-voxel border from each of the
// 6 neighbor chunks (so it can correctly cull boundary faces). The CPU path
// (`_DecompressNeighborsOnce` + manual border copies) walks each border in Udon — slow.
//
// This shader does the work in 6 thin Blits: for each face direction, render only the
// strip of pixels corresponding to the border slab of the sentinel RT, sampling from the
// matching face of the neighbor chunk's atlas slot.
//
// The sentinel layout matches the existing CPU sentinel:
//   sentinel size = (chunkSizeXZ + 2) x (chunkSizeY + 2) x (chunkSizeXZ + 2)
//   coords [1..chunkSizeXZ, 1..chunkSizeY, 1..chunkSizeXZ] are the SELF chunk
//   coords 0 and N+1 along any axis are border slices from neighbors
//
// We use the GPU atlas as the source (since chunks may be GPU-resident with no CPU mirror).
// The fragment shader computes the world-coord this pixel represents, identifies which
// neighbor it should sample from, and looks up the corresponding atlas slot.
Shader "VRCM/GpuSentinelBorderCopy"
{
    Properties
    {
        _BlockAtlas        ("Block Atlas",         2D) = "black" {}
        _SlotLookupTex     ("Slot Lookup",         2D) = "black" {}
        _SelfBlockTex      ("Self Block Atlas",    2D) = "black" {} // optional fast-path
        _ChunkSizeXZ       ("Chunk Size XZ",       Int) = 16
        _ChunkSizeY        ("Chunk Size Y",        Int) = 16
        _SelfChunkX        ("Self Chunk X",        Int) = 0
        _SelfChunkY        ("Self Chunk Y",        Int) = 0
        _SelfChunkZ        ("Self Chunk Z",        Int) = 0
        // Pass index drives which face we're copying:
        //   0 = -X face   1 = +X face
        //   2 = -Y face   3 = +Y face
        //   4 = -Z face   5 = +Z face
        //   6 = SELF interior copy (fast path: copy entire self atlas slot into [1..N]³ of sentinel)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        CGINCLUDE
        #include "UnityCG.cginc"

        struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
        struct v2f     { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

        sampler2D _BlockAtlas;
        sampler2D _SlotLookupTex;
        sampler2D _SelfBlockTex;
        int _ChunkSizeXZ;
        int _ChunkSizeY;
        int _SelfChunkX;
        int _SelfChunkY;
        int _SelfChunkZ;
        float4 _UdonVRCM_GpuAtlasInfo;   // (atlasW, atlasH, tilesX, _)
        float4 _UdonVRCM_GpuChunkInfo;   // (chunkXZ, chunkY, _, _)
        float4 _UdonVRCM_GpuWorldInfo;   // (worldChunksX, worldChunksY, worldChunksZ, _)

        v2f vert(appdata v)
        {
            v2f o;
            o.vertex = UnityObjectToClipPos(v.vertex);
            o.uv = v.uv;
            return o;
        }

        // Sentinel pixel layout: same packing as chunk RTs except size+2 per axis.
        // The output RT is (sxz × (sxz * sy)) where sxz = chunkSizeXZ+2, sy = chunkSizeY+2.
        // pixel.x ∈ [0, sxz),  pixel.y ∈ [0, sxz*sy) → y' = floor(pixel.y / sxz), z' = pixel.y % sxz
        // Subtract 1 from each to get [-1..N] sentinel-space block coords.
        void DecodeSentinelPixel(float2 vpos, out int sx, out int sy, out int sz)
        {
            int sxz = _ChunkSizeXZ + 2;
            int syt = _ChunkSizeY + 2;
            int px = clamp((int)floor(vpos.x), 0, sxz - 1);
            int py = clamp((int)floor(vpos.y), 0, sxz * syt - 1);
            sy = py / sxz;       // 0..syt-1
            sz = py - sy * sxz;  // 0..sxz-1
            sx = px;             // 0..sxz-1
        }

        // Lookup an atlas slot for a given chunk coord; returns -1 if not loaded.
        int LookupSlot(int cx, int cy, int cz)
        {
            if (cx < 0 || cy < 0 || cz < 0) return -1;
            if (cx >= (int)_UdonVRCM_GpuWorldInfo.x || cy >= (int)_UdonVRCM_GpuWorldInfo.y || cz >= (int)_UdonVRCM_GpuWorldInfo.z) return -1;

            float lookupRow = (float)cy * _UdonVRCM_GpuWorldInfo.z + (float)cz;
            float2 lookupUv = float2(
                ((float)cx + 0.5) / _UdonVRCM_GpuWorldInfo.x,
                (lookupRow + 0.5) / (_UdonVRCM_GpuWorldInfo.y * _UdonVRCM_GpuWorldInfo.z)
            );
            float4 slotData = tex2Dlod(_SlotLookupTex, float4(lookupUv, 0, 0));
            if (slotData.a < 0.5) return -1;
            int slotLow  = (int)floor(slotData.r * 255.0 + 0.5);
            int slotHigh = (int)floor(slotData.g * 255.0 + 0.5);
            return slotLow + slotHigh * 256;
        }

        // Sample a block ID from the global atlas at (slot, localX, localY, localZ).
        float SampleAtlasBlock(int slotIndex, int lx, int ly, int lz)
        {
            float tilesX = _UdonVRCM_GpuAtlasInfo.z;
            float tileX = fmod((float)slotIndex, tilesX);
            float tileY = floor((float)slotIndex / tilesX);
            float chunkXZf = _UdonVRCM_GpuChunkInfo.x;
            float chunkYf  = _UdonVRCM_GpuChunkInfo.y;
            float u = (tileX * chunkXZf + (float)lx + 0.5) / _UdonVRCM_GpuAtlasInfo.x;
            float v = (tileY * (chunkYf * chunkXZf) + (float)ly * chunkXZf + (float)lz + 0.5) / _UdonVRCM_GpuAtlasInfo.y;
            return tex2Dlod(_BlockAtlas, float4(u, v, 0, 0)).r;
        }
        ENDCG

        // Pass 0: -X face copy. Writes sentinel column at sx=0 from neighbor (cx-1)'s lx=N-1.
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int sx, sy, sz; DecodeSentinelPixel(i.vertex.xy, sx, sy, sz);
                if (sx != 0) discard;
                int slot = LookupSlot(_SelfChunkX - 1, _SelfChunkY, _SelfChunkZ);
                if (slot < 0) return 0;
                int ly = sy - 1; int lz = sz - 1;
                if (ly < 0 || lz < 0 || ly >= _ChunkSizeY || lz >= _ChunkSizeXZ) return 0;
                return float4(SampleAtlasBlock(slot, _ChunkSizeXZ - 1, ly, lz), 0, 0, 1);
            }
            ENDCG
        }
        // Pass 1: +X face. sx = N+1, from neighbor (cx+1)'s lx=0.
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int sx, sy, sz; DecodeSentinelPixel(i.vertex.xy, sx, sy, sz);
                if (sx != _ChunkSizeXZ + 1) discard;
                int slot = LookupSlot(_SelfChunkX + 1, _SelfChunkY, _SelfChunkZ);
                if (slot < 0) return 0;
                int ly = sy - 1; int lz = sz - 1;
                if (ly < 0 || lz < 0 || ly >= _ChunkSizeY || lz >= _ChunkSizeXZ) return 0;
                return float4(SampleAtlasBlock(slot, 0, ly, lz), 0, 0, 1);
            }
            ENDCG
        }
        // Pass 2: -Y face. sy = 0, from neighbor (cy-1)'s ly = N-1.
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int sx, sy, sz; DecodeSentinelPixel(i.vertex.xy, sx, sy, sz);
                if (sy != 0) discard;
                int slot = LookupSlot(_SelfChunkX, _SelfChunkY - 1, _SelfChunkZ);
                if (slot < 0) return 0;
                int lx = sx - 1; int lz = sz - 1;
                if (lx < 0 || lz < 0 || lx >= _ChunkSizeXZ || lz >= _ChunkSizeXZ) return 0;
                return float4(SampleAtlasBlock(slot, lx, _ChunkSizeY - 1, lz), 0, 0, 1);
            }
            ENDCG
        }
        // Pass 3: +Y face. sy = N+1, from neighbor (cy+1)'s ly = 0.
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int sx, sy, sz; DecodeSentinelPixel(i.vertex.xy, sx, sy, sz);
                if (sy != _ChunkSizeY + 1) discard;
                int slot = LookupSlot(_SelfChunkX, _SelfChunkY + 1, _SelfChunkZ);
                if (slot < 0) return 0;
                int lx = sx - 1; int lz = sz - 1;
                if (lx < 0 || lz < 0 || lx >= _ChunkSizeXZ || lz >= _ChunkSizeXZ) return 0;
                return float4(SampleAtlasBlock(slot, lx, 0, lz), 0, 0, 1);
            }
            ENDCG
        }
        // Pass 4: -Z face. sz = 0, from neighbor (cz-1)'s lz = N-1.
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int sx, sy, sz; DecodeSentinelPixel(i.vertex.xy, sx, sy, sz);
                if (sz != 0) discard;
                int slot = LookupSlot(_SelfChunkX, _SelfChunkY, _SelfChunkZ - 1);
                if (slot < 0) return 0;
                int lx = sx - 1; int ly = sy - 1;
                if (lx < 0 || ly < 0 || lx >= _ChunkSizeXZ || ly >= _ChunkSizeY) return 0;
                return float4(SampleAtlasBlock(slot, lx, ly, _ChunkSizeXZ - 1), 0, 0, 1);
            }
            ENDCG
        }
        // Pass 5: +Z face. sz = N+1, from neighbor (cz+1)'s lz = 0.
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int sx, sy, sz; DecodeSentinelPixel(i.vertex.xy, sx, sy, sz);
                if (sz != _ChunkSizeXZ + 1) discard;
                int slot = LookupSlot(_SelfChunkX, _SelfChunkY, _SelfChunkZ + 1);
                if (slot < 0) return 0;
                int lx = sx - 1; int ly = sy - 1;
                if (lx < 0 || ly < 0 || lx >= _ChunkSizeXZ || ly >= _ChunkSizeY) return 0;
                return float4(SampleAtlasBlock(slot, lx, ly, 0), 0, 0, 1);
            }
            ENDCG
        }
        // Pass 6: SELF interior bulk copy. Writes [1..N]³ of sentinel from self atlas slot.
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int sx, sy, sz; DecodeSentinelPixel(i.vertex.xy, sx, sy, sz);
                if (sx == 0 || sy == 0 || sz == 0 ||
                    sx == _ChunkSizeXZ + 1 || sy == _ChunkSizeY + 1 || sz == _ChunkSizeXZ + 1) discard;
                int slot = LookupSlot(_SelfChunkX, _SelfChunkY, _SelfChunkZ);
                if (slot < 0) return 0;
                return float4(SampleAtlasBlock(slot, sx - 1, sy - 1, sz - 1), 0, 0, 1);
            }
            ENDCG
        }
    }
}
