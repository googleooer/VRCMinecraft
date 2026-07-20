#ifndef MCVOXEL_INSTANCED_CGINC
#define MCVOXEL_INSTANCED_CGINC

// Shared body for the GPU chunk-mesh voxel render (opaque/cutout/transparent passes).
// Define PASS_CLASS before including:
//   0 = OPAQUE      (opaque cubes; Cull Back, ZWrite On)
//   1 = CUTOUT      (cutout cubes e.g. leaves; alpha-clip, Cull Off)
//   2 = TRANSPARENT (transparent cubes e.g. water/ice/glass; alpha-blend, Cull Off)
// Block class is read from _BlockFaceSliceTex.a, encoded by McWorld._GpuBuildBlockFaceSliceTexture:
//   1.0 = opaque cube, 0.5 = cutout cube, 0.25 = transparent cube, 0 = skip (air / cross / non-cube).
#ifndef PASS_CLASS
#define PASS_CLASS 0
#endif

#include "UnityCG.cginc"

// --- atlas globals (published by McWorld; FRAGMENT-stage only) ---
sampler2D _UdonVRCM_GpuBlockAtlas;
sampler2D _UdonVRCM_GpuLightAtlas;
sampler2D _UdonVRCM_GpuSlotLookup;
sampler2D _UdonVRCM_GpuBlockProps;
float4 _UdonVRCM_GpuAtlasInfo;   // (atlasW, atlasH, slotsX, slotsY)
float4 _UdonVRCM_GpuWorldInfo;   // (numChunksX, numChunksY, numChunksZ, numY*numZ)
float4 _UdonVRCM_GpuChunkInfo;   // (chunkSizeXZ, chunkSizeY, offX, offZ)
float4 _UdonVRCM_GpuVoxelOffset; // (offX, offY, offZ, slotCapacity)
float _UdonVRCM_GpuEnabled;
float _UdonVRCM_SkylightSub; // DAY/NIGHT: 0-11, subtracted from SKY light at sample time

#define _UseGpuExactAo 1.0
#include "MCTerrainGpuExactAo.cginc"

UNITY_DECLARE_TEX2DARRAY(_MainTex);
UNITY_DECLARE_TEX2DARRAY(_TintMask);
sampler2D _ShouldDrawTex;
sampler2D _BlockFaceSliceTex;
sampler2D _BiomeColorRT;
// vertex-readable material-bound copies of the atlas + slot-lookup (globals don't bind in vertex)
sampler2D _InstBlockAtlas;
sampler2D _InstSlotLookup;

fixed4 _FogColor;
float _FogStart, _FogEnd;
// DAY/NIGHT FOG: driven by McWorld._PublishFogState at ~10Hz alongside the terrain.mat
// family — mode 0 = linear (land, start/end), 1 = exp (water 0.1 / lava 2.0 densities),
// 2 = exp2, matching MCTerrain's calcMinecraftFog exactly.
float _FogMode;
float _FogDensity;

struct appdata
{
    float4 vertex : POSITION;   // CHUNK-LOCAL cube corner (0..chunkSize)
    float3 normal : NORMAL;     // face normal
    float3 uv : TEXCOORD0;      // xy = 0/1 face corner, z = voxel local Y (ly)
    float2 uv2 : TEXCOORD1;     // (lx, lz) voxel column within the chunk
};

struct v2f
{
    float4 pos : SV_POSITION;
    float3 uvw : TEXCOORD0;     // xy in-slice uv, z = texture-array slice
    float3 worldPos : TEXCOORD1;
    float3 normal : TEXCOORD2;
    float2 biomeUv : TEXCOORD3; // per-chunk biome RT uv (sampled in frag — MPB tex)
};

// vertex-stage atlas slot lookup -> atlasUv for the voxel; -1 if not loaded.
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
    float4 slotData = tex2Dlod(_InstSlotLookup, float4(lookupUv, 0, 0));
    if (slotData.a < 0.5) return -1.0;
    float slot = floor(slotData.r * 255.0 + 0.5) + floor(slotData.g * 255.0 + 0.5) * 256.0;
    float tileX = fmod(slot, _UdonVRCM_GpuAtlasInfo.z);
    float tileY = floor(slot / _UdonVRCM_GpuAtlasInfo.z);
    atlasUv = float2((tileX * _UdonVRCM_GpuChunkInfo.x + lX + 0.5) / _UdonVRCM_GpuAtlasInfo.x,
                     (tileY * (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x) + lY * _UdonVRCM_GpuChunkInfo.x + lZ + 0.5) / _UdonVRCM_GpuAtlasInfo.y);
    return slot;
}

float vBlockId(float3 worldVoxel)
{
    float2 uv;
    if (vLookupSlot(worldVoxel, uv) < 0.0) return -1.0;
    return floor(tex2Dlod(_InstBlockAtlas, float4(uv, 0, 0)).r * 255.0 + 0.5);
}

v2f vert(appdata v)
{
    v2f o;
    float cs = _UdonVRCM_GpuChunkInfo.x;
    int lx = (int)(v.uv2.x + 0.5);
    int lz = (int)(v.uv2.y + 0.5);
    float3 center = mul(unity_ObjectToWorld, float4(v.uv2.x + 0.5, v.uv.z + 0.5, v.uv2.y + 0.5, 1.0)).xyz;
    float3 n = v.normal;
    float4 dead = float4(0, 0, -10, 0);

    float self = vBlockId(center);
    if (self <= 0.0) { o.pos = dead; o.uvw = 0; o.worldPos = 0; o.normal = 0; o.biomeUv = 0; return o; }

    float4 lut = tex2Dlod(_BlockFaceSliceTex, float4((self + 0.5) / 256.0, 0.5, 0, 0));
    // block-class gate: each pass renders only its own class.
#if PASS_CLASS == 0
    if (lut.a < 0.75) { o.pos = dead; o.uvw = 0; o.worldPos = 0; o.normal = 0; o.biomeUv = 0; return o; }
#elif PASS_CLASS == 1
    if (lut.a < 0.375 || lut.a >= 0.75) { o.pos = dead; o.uvw = 0; o.worldPos = 0; o.normal = 0; o.biomeUv = 0; return o; }
#else
    if (lut.a < 0.125 || lut.a >= 0.375) { o.pos = dead; o.uvw = 0; o.worldPos = 0; o.normal = 0; o.biomeUv = 0; return o; }
#endif

    float nb = vBlockId(center + n);
    if (nb < 0.0) nb = 0.0;
    float draw = tex2Dlod(_ShouldDrawTex, float4((nb + 0.5) / 256.0, (self + 0.5) / 256.0, 0, 0)).r;
    if (draw < 0.5) { o.pos = dead; o.uvw = 0; o.worldPos = 0; o.normal = 0; o.biomeUv = 0; return o; }

    float3 wp = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz;
    o.worldPos = wp;
    o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1.0));
    o.normal = n;

    float slice = lut.b * 255.0;
    if (n.y > 0.5) slice = lut.r * 255.0;
    else if (n.y < -0.5) slice = lut.g * 255.0;
    o.uvw = float3(v.uv.xy, floor(slice + 0.5));
    o.biomeUv = float2((lx + 0.5) / cs, (lz + 0.5) / cs);
    return o;
}

fixed4 frag(v2f i) : SV_Target
{
    fixed4 col = UNITY_SAMPLE_TEX2DARRAY(_MainTex, i.uvw);
#if PASS_CLASS == 1
    clip(col.a - 0.1); // cutout alpha test (leaves etc.)
#endif

    // biome tint, gated by the per-slice tint-mask alpha (matches MCTerrain.shader)
    fixed3 biome = tex2D(_BiomeColorRT, i.biomeUv).rgb;
    fixed tintA = UNITY_SAMPLE_TEX2DARRAY(_TintMask, i.uvw).a;
    col.rgb = lerp(col.rgb, col.rgb * biome, saturate(tintA));

    // GPU-exact AO / light
    half minLight = 0.02;
    half aoBrightness = gpuVoxelComputeExactAoBrightness(i.worldPos, i.normal);
    half lightBrightness = max(minLight, aoBrightness >= 0.0 ? aoBrightness : 1.0);

    // directional face shading
    half faceBrightness = 1.0;
    float3 an = abs(i.normal);
    if (an.y > 0.5) faceBrightness = i.normal.y > 0.0 ? 1.0 : 0.5;
    else if (an.z > 0.5) faceBrightness = 0.8;
    else faceBrightness = 0.6;

    half b = lightBrightness * faceBrightness;
    b = GammaToLinearSpace(b.xxx).x;
    col.rgb *= b;

    // Minecraft distance fog — same modes as MCTerrain.calcMinecraftFog (b1.7.3 setupFog):
    // linear for land, exp for water/lava immersion, exp2 spare.
    float d = distance(i.worldPos, _WorldSpaceCameraPos);
    float fog;
    if (_FogMode < 0.5)      fog = saturate((_FogEnd - d) / max(0.001, _FogEnd - _FogStart));
    else if (_FogMode < 1.5) fog = saturate(exp(-_FogDensity * d));
    else                     fog = saturate(exp(-_FogDensity * _FogDensity * d * d));
    col.rgb = lerp(_FogColor.rgb, col.rgb, fog);

#if PASS_CLASS == 2
    col.a = max(col.a, 0.7); // transparent: ensure water/ice/glass blend visibly see-through
#else
    col.a = 1.0;
#endif
    return col;
}

#endif // MCVOXEL_INSTANCED_CGINC
