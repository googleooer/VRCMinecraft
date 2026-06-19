// (c) GPU INSTANCED VOXEL RENDER — escapes the per-chunk GPU->CPU face readback.
//
// One DrawMeshInstanced call per visible chunk. Instance = one voxel (instanceID 0..voxelsPerChunk-1).
// The mesh is a unit cube of 6 faces (24 verts). The VERTEX shader reads the GPU-resident block
// atlas (the same atlas MCTerrainGpuExactAo samples) to:
//   1. resolve this instance's voxel -> block id (degenerate the whole cube if air / not opaque),
//   2. for the face this vertex belongs to, read the NEIGHBOR voxel + the should-draw table and
//      degenerate the face if it should be culled,
//   3. otherwise place the face quad in world space and compute its texture-array slice + uv.
// The FRAGMENT shader reuses the project's terrain look (2DArray sample + biome tint + GPU-exact
// AO/light from MCTerrainGpuExactAo.cginc + Minecraft fog).
//
// No VRCAsyncGPUReadback, no CPU Mesh build, no per-chunk face buffer. This is the OPAQUE pass;
// transparent/cutout/cross/water blocks are handled by their existing paths (degenerated here via
// the per-block isOpaque flag in _BlockFaceSliceTex.a).
//
// Required bindings (set by McWorld on the instanced material / MPB):
//   _MainTex            Texture2DArray  - block textures (same as MCTerrain)
//   _TintMask           Texture2DArray  - per-slice tint amount (same as MCTerrain)
//   _ShouldDrawTex      Texture2D 256x256 - should-draw table (u=neighborId, v=selfId), R=255 draw
//   _BlockFaceSliceTex  Texture2D 256x1 - per block: R=topSlice G=bottomSlice B=sideSlice A=isOpaque(255)
//   _BiomeColorRT       Texture2D 16x16 - per-chunk grass/foliage biome colour (point) [optional]
//   _ChunkOriginX/Y/Z   float           - chunk world origin = chunkXYZ_world * chunkSize
//   _ChunkSizeXZ/_ChunkSizeY int
// Plus the global _UdonVRCM_Gpu* atlas globals (already published by McWorld._GpuPublishGlobals).
Shader "Unlit/MCVoxelInstanced"
{
    Properties
    {
        _MainTex ("Texture Array", 2DArray) = "white" {}
        _TintMask ("Tint Mask Array", 2DArray) = "black" {}
        [HideInInspector] _ShouldDrawTex ("Should Draw Table", 2D) = "black" {}
        [HideInInspector] _BlockFaceSliceTex ("Block Face Slice LUT", 2D) = "black" {}
        [HideInInspector] _BiomeColorRT ("Biome Colour", 2D) = "white" {}
        _FogColor ("Fog Color", Color) = (0.5, 0.6, 0.7, 1.0)
        _FogStart ("Fog Start Distance", Float) = 32.0
        _FogEnd ("Fog End Distance", Float) = 128.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100
        // Cull Off: each generated voxel face is a single quad whose outward normal points into air
        // (the opposite face is never emitted — culled by the should-draw table), so there is no
        // double geometry / z-fight and winding is irrelevant. Cull Back is a later micro-opt.
        Cull Off
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            // --- atlas globals (published by McWorld) ---
            sampler2D _UdonVRCM_GpuBlockAtlas;
            sampler2D _UdonVRCM_GpuLightAtlas;
            sampler2D _UdonVRCM_GpuSlotLookup;
            sampler2D _UdonVRCM_GpuBlockProps;
            float4 _UdonVRCM_GpuAtlasInfo;   // (atlasW, atlasH, slotsX, slotsY)
            float4 _UdonVRCM_GpuWorldInfo;   // (numChunksX, numChunksY, numChunksZ, numY*numZ)
            float4 _UdonVRCM_GpuChunkInfo;   // (chunkSizeXZ, chunkSizeY, offX, offZ)
            float4 _UdonVRCM_GpuVoxelOffset; // (offX, offY, offZ, slotCapacity)
            float _UdonVRCM_GpuEnabled;

            // GPU-exact AO/light (fragment-stage, tex2D based) — reused as-is.
            #define _UseGpuExactAo 1.0
            #include "MCTerrainGpuExactAo.cginc"

            UNITY_DECLARE_TEX2DARRAY(_MainTex);
            UNITY_DECLARE_TEX2DARRAY(_TintMask);
            sampler2D _ShouldDrawTex;
            sampler2D _BlockFaceSliceTex;
            sampler2D _BiomeColorRT;

            float _ChunkOriginX, _ChunkOriginY, _ChunkOriginZ;
            int _ChunkSizeXZ, _ChunkSizeY;
            int _InstanceOffset;
            fixed4 _FogColor;
            float _FogStart, _FogEnd;

            struct appdata
            {
                float4 vertex : POSITION;   // column-cube corner: x,z in 0..1, y in [ly, ly+1]
                float3 normal : NORMAL;     // face normal (+-X/+-Y/+-Z)
                float3 uv : TEXCOORD0;      // xy = 0/1 face corner, z = voxel local Y (ly)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 uvw : TEXCOORD0;     // xy in-slice uv, z = texture-array slice
                float3 worldPos : TEXCOORD1;
                float3 normal : TEXCOORD2;
                fixed3 biome : TEXCOORD3;
                float tint : TEXCOORD4;
            };

            // --- vertex-stage atlas lookup (tex2Dlod; the cginc's tex2D version is fragment-only) ---
            // Returns slot index for the chunk containing worldVoxel, or -1 if not resident.
            float vLookupSlot(float3 worldVoxel, out float2 atlasUv)
            {
                atlasUv = 0;
                float gX = floor(worldVoxel.x + 0.0001) + _UdonVRCM_GpuVoxelOffset.x;
                float gY = floor(worldVoxel.y + 0.0001) + _UdonVRCM_GpuVoxelOffset.y;
                float gZ = floor(worldVoxel.z + 0.0001) + _UdonVRCM_GpuVoxelOffset.z;
                float maxX = _UdonVRCM_GpuWorldInfo.x * _UdonVRCM_GpuChunkInfo.x;
                float maxY = _UdonVRCM_GpuWorldInfo.y * _UdonVRCM_GpuChunkInfo.y;
                float maxZ = _UdonVRCM_GpuWorldInfo.z * _UdonVRCM_GpuChunkInfo.x;
                if (gX < 0 || gY < 0 || gZ < 0 || gX >= maxX || gY >= maxY || gZ >= maxZ) return -1.0;
                float cX = floor(gX / _UdonVRCM_GpuChunkInfo.x);
                float cY = floor(gY / _UdonVRCM_GpuChunkInfo.y);
                float cZ = floor(gZ / _UdonVRCM_GpuChunkInfo.x);
                float lX = gX - cX * _UdonVRCM_GpuChunkInfo.x;
                float lY = gY - cY * _UdonVRCM_GpuChunkInfo.y;
                float lZ = gZ - cZ * _UdonVRCM_GpuChunkInfo.x;
                float lookupRow = cY * _UdonVRCM_GpuWorldInfo.z + cZ;
                float2 lookupUv = float2((cX + 0.5) / _UdonVRCM_GpuWorldInfo.x,
                                         (lookupRow + 0.5) / (_UdonVRCM_GpuWorldInfo.y * _UdonVRCM_GpuWorldInfo.z));
                float4 slotData = tex2Dlod(_UdonVRCM_GpuSlotLookup, float4(lookupUv, 0, 0));
                if (slotData.a < 0.5) return -1.0;
                float slot = floor(slotData.r * 255.0 + 0.5) + floor(slotData.g * 255.0 + 0.5) * 256.0;
                float tileX = fmod(slot, _UdonVRCM_GpuAtlasInfo.z);
                float tileY = floor(slot / _UdonVRCM_GpuAtlasInfo.z);
                atlasUv = float2((tileX * _UdonVRCM_GpuChunkInfo.x + lX + 0.5) / _UdonVRCM_GpuAtlasInfo.x,
                                 (tileY * (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x) + lY * _UdonVRCM_GpuChunkInfo.x + lZ + 0.5) / _UdonVRCM_GpuAtlasInfo.y);
                return slot;
            }

            // block id at a world voxel center; -1 if outside the loaded atlas (treat as air/sky).
            float vBlockId(float3 worldVoxel)
            {
                float2 uv;
                if (vLookupSlot(worldVoxel, uv) < 0.0) return -1.0;
                return floor(tex2Dlod(_UdonVRCM_GpuBlockAtlas, float4(uv, 0, 0)).r * 255.0 + 0.5);
            }

            v2f vert(appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                v2f o;

                // Per-column instancing: id = column index (0..chunkSizeXZ^2-1) -> (lx,lz).
                // The voxel's local Y comes from the mesh (uv.z), since the column mesh stacks all
                // chunkSizeY voxels.
                int columnsPerChunk = _ChunkSizeXZ * _ChunkSizeXZ;
                int id = _InstanceOffset;
                #ifdef UNITY_INSTANCING_ENABLED
                    id += (int)unity_InstanceID;
                #endif

                // degenerate helper target (collapse to a clipped point)
                float4 dead = float4(0, 0, -10, 0);

                if (id >= columnsPerChunk) { o.pos = dead; o.uvw = 0; o.worldPos = 0; o.normal = 0; o.biome = 0; o.tint = 0; return o; }

                int lx = id % _ChunkSizeXZ;
                int lz = id / _ChunkSizeXZ;
                int ly = (int)(v.uv.z + 0.5);

                float3 voxel = float3(_ChunkOriginX + lx, _ChunkOriginY + ly, _ChunkOriginZ + lz);
                float3 center = voxel + 0.5;

                float self = vBlockId(center);
                if (self <= 0.0) { o.pos = dead; o.uvw = 0; o.worldPos = 0; o.normal = 0; o.biome = 0; o.tint = 0; return o; }

                // per-block LUT: R=topSlice G=bottomSlice B=sideSlice A=isOpaque
                float4 lut = tex2Dlod(_BlockFaceSliceTex, float4((self + 0.5) / 256.0, 0.5, 0, 0));
                if (lut.a < 0.5) { o.pos = dead; o.uvw = 0; o.worldPos = 0; o.normal = 0; o.biome = 0; o.tint = 0; return o; }

                float3 n = v.normal;
                float3 neighbor = center + n;
                float nb = vBlockId(neighbor);
                if (nb < 0.0) nb = 0.0; // outside atlas -> treat as air so the face draws

                // should-draw table: u = neighborId, v = selfId
                float draw = tex2Dlod(_ShouldDrawTex, float4((nb + 0.5) / 256.0, (self + 0.5) / 256.0, 0, 0)).r;
                if (draw < 0.5) { o.pos = dead; o.uvw = 0; o.worldPos = 0; o.normal = 0; o.biome = 0; o.tint = 0; return o; }

                // place the face quad. v.vertex: x,z in 0..1; y already includes ly (column mesh).
                float3 wp = float3(_ChunkOriginX + lx + v.vertex.x,
                                   _ChunkOriginY + v.vertex.y,
                                   _ChunkOriginZ + lz + v.vertex.z);
                o.worldPos = wp;
                // wp is already absolute world space (instance matrices are identity) — use the
                // world->clip path so the (identity) model matrix isn't applied again.
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0));
                o.normal = n;

                // slice: +Y -> top(R), -Y -> bottom(G), sides -> side(B)
                float slice = lut.b * 255.0;
                if (n.y > 0.5) slice = lut.r * 255.0;
                else if (n.y < -0.5) slice = lut.g * 255.0;
                o.uvw = float3(v.uv.xy, floor(slice + 0.5));

                // biome tint colour for this column (point-sampled per-chunk RT)
                o.biome = tex2Dlod(_BiomeColorRT, float4((lx + 0.5) / _ChunkSizeXZ, (lz + 0.5) / _ChunkSizeXZ, 0, 0)).rgb;
                o.tint = UNITY_SAMPLE_TEX2DARRAY_LOD(_TintMask, o.uvw, 0).r;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = UNITY_SAMPLE_TEX2DARRAY(_MainTex, i.uvw);

                // biome tint where the per-slice tint mask says so
                col.rgb = lerp(col.rgb, col.rgb * i.biome, saturate(i.tint));

                // GPU-exact AO / light from the atlas (cginc, fragment-stage tex2D)
                half minLight = 0.02;
                half lightBrightness;
                half aoBrightness = gpuVoxelComputeExactAoBrightness(i.worldPos, i.normal);
                lightBrightness = max(minLight, aoBrightness >= 0.0 ? aoBrightness : 1.0);

                // directional face shading (Minecraft fixed per-face brightness)
                half faceBrightness = 1.0;
                float3 an = abs(i.normal);
                if (an.y > 0.5) faceBrightness = i.normal.y > 0.0 ? 1.0 : 0.5;
                else if (an.z > 0.5) faceBrightness = 0.8;
                else faceBrightness = 0.6;

                half b = lightBrightness * faceBrightness;
                b = GammaToLinearSpace(b.xxx).x;
                col.rgb *= b;

                // Minecraft linear distance fog
                float d = distance(i.worldPos, _WorldSpaceCameraPos);
                float fog = saturate((_FogEnd - d) / max(0.001, _FogEnd - _FogStart));
                col.rgb = lerp(_FogColor.rgb, col.rgb, fog);

                return col;
            }
            ENDCG
        }
    }
}
