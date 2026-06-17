// GPU OFFLOAD #10: Voxel DDA raycast for block-break/place targeting.
//
// Replaces the CPU `Physics.Raycast` in ModifyTerrain._UpdateInteractionRaycast that goes
// through Unity's broadphase. Instead, we march a ray through the GPU block atlas using
// Amanatides-Woo DDA and output the first-hit info to a 1×1 RGBA32 RT:
//   .r = hit blockId
//   .g = packed face hit (0..5)
//   .b = (hitDistance * 16) low-8 bits  — useful for outline distance
//   .a = 1 if hit, 0 if missed
//
// Position output is the encoded block coord (3D → 24-bit packed) in a second 1×1 RT
// using pass 1 — emitted as RGB byte triple plus alpha.
//
// One CPU readback per frame on a 1×1 RT is dirt-cheap (single pixel async readback);
// the latency is ~2 frames which is fine for VR block interaction.
Shader "VRCM/GpuRaycast"
{
    Properties
    {
        _BlockAtlas    ("Block Atlas",   2D) = "black" {}
        _SlotLookupTex ("Slot Lookup",   2D) = "black" {}
        _RayOrigin     ("Ray Origin (world)",    Vector) = (0, 0, 0, 0)
        _RayDirection  ("Ray Direction (world)", Vector) = (0, 0, 1, 0)
        _MaxDistance   ("Max Distance (blocks)", Float) = 4.5
        _ChunkSizeXZ   ("Chunk Size XZ", Int) = 16
        _ChunkSizeY    ("Chunk Size Y",  Int) = 16
        _WorldOffsetX  ("World Offset X (blocks)", Int) = 0
        _WorldOffsetY  ("World Offset Y (blocks)", Int) = 0
        _WorldOffsetZ  ("World Offset Z (blocks)", Int) = 0
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
        float4 _RayOrigin;
        float4 _RayDirection;
        float _MaxDistance;
        int _ChunkSizeXZ;
        int _ChunkSizeY;
        int _WorldOffsetX, _WorldOffsetY, _WorldOffsetZ;
        float4 _UdonVRCM_GpuAtlasInfo;
        float4 _UdonVRCM_GpuChunkInfo;
        float4 _UdonVRCM_GpuWorldInfo;

        v2f vert(appdata v)
        {
            v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o;
        }

        int LookupSlot(int cx, int cy, int cz)
        {
            if (cx < 0 || cy < 0 || cz < 0) return -1;
            if (cx >= (int)_UdonVRCM_GpuWorldInfo.x || cy >= (int)_UdonVRCM_GpuWorldInfo.y || cz >= (int)_UdonVRCM_GpuWorldInfo.z) return -1;
            float lookupRow = (float)cy * _UdonVRCM_GpuWorldInfo.z + (float)cz;
            float2 uv = float2(
                ((float)cx + 0.5) / _UdonVRCM_GpuWorldInfo.x,
                (lookupRow + 0.5) / (_UdonVRCM_GpuWorldInfo.y * _UdonVRCM_GpuWorldInfo.z)
            );
            float4 slotData = tex2Dlod(_SlotLookupTex, float4(uv, 0, 0));
            if (slotData.a < 0.5) return -1;
            int slotLow = (int)floor(slotData.r * 255.0 + 0.5);
            int slotHigh = (int)floor(slotData.g * 255.0 + 0.5);
            return slotLow + slotHigh * 256;
        }

        float SampleAtlasBlock(int slot, int lx, int ly, int lz)
        {
            float tilesX = _UdonVRCM_GpuAtlasInfo.z;
            float tileX = fmod((float)slot, tilesX);
            float tileY = floor((float)slot / tilesX);
            float u = (tileX * _UdonVRCM_GpuChunkInfo.x + (float)lx + 0.5) / _UdonVRCM_GpuAtlasInfo.x;
            float v = (tileY * (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x) + (float)ly * _UdonVRCM_GpuChunkInfo.x + (float)lz + 0.5) / _UdonVRCM_GpuAtlasInfo.y;
            return tex2Dlod(_BlockAtlas, float4(u, v, 0, 0)).r * 255.0;
        }

        int GetBlockAtWorld(int wx, int wy, int wz)
        {
            int cx = (wx - _WorldOffsetX) >> 4;
            int cy = (wy - _WorldOffsetY) >> 4;
            int cz = (wz - _WorldOffsetZ) >> 4;
            int lx = (wx - _WorldOffsetX) & 15;
            int ly = (wy - _WorldOffsetY) & 15;
            int lz = (wz - _WorldOffsetZ) & 15;
            int slot = LookupSlot(cx, cy, cz);
            if (slot < 0) return 0;
            return (int)round(SampleAtlasBlock(slot, lx, ly, lz));
        }

        // Marches the ray through voxels, returns (blockId, face, distance, hit).
        // face: 0=-X, 1=+X, 2=-Y, 3=+Y, 4=-Z, 5=+Z
        float4 MarchRay(out int hitX, out int hitY, out int hitZ)
        {
            hitX = hitY = hitZ = 0;
            float3 ro = _RayOrigin.xyz;
            float3 rd = normalize(_RayDirection.xyz);

            int x = (int)floor(ro.x);
            int y = (int)floor(ro.y);
            int z = (int)floor(ro.z);

            int stepX = rd.x > 0 ? 1 : (rd.x < 0 ? -1 : 0);
            int stepY = rd.y > 0 ? 1 : (rd.y < 0 ? -1 : 0);
            int stepZ = rd.z > 0 ? 1 : (rd.z < 0 ? -1 : 0);

            float nextBoundaryX = (stepX > 0) ? (float)(x + 1) : (float)x;
            float nextBoundaryY = (stepY > 0) ? (float)(y + 1) : (float)y;
            float nextBoundaryZ = (stepZ > 0) ? (float)(z + 1) : (float)z;

            float tMaxX = (rd.x != 0) ? (nextBoundaryX - ro.x) / rd.x : 1e30;
            float tMaxY = (rd.y != 0) ? (nextBoundaryY - ro.y) / rd.y : 1e30;
            float tMaxZ = (rd.z != 0) ? (nextBoundaryZ - ro.z) / rd.z : 1e30;

            float tDeltaX = (rd.x != 0) ? abs(1.0 / rd.x) : 1e30;
            float tDeltaY = (rd.y != 0) ? abs(1.0 / rd.y) : 1e30;
            float tDeltaZ = (rd.z != 0) ? abs(1.0 / rd.z) : 1e30;

            int faceHit = 0;
            float distance = 0;
            // Cap iterations at 32 — covers 5.66-block diagonal reach, well above Beta's 4.5.
            [loop] for (int i = 0; i < 32; i++)
            {
                int blockId = GetBlockAtWorld(x, y, z);
                if (blockId != 0 && i > 0)
                {
                    hitX = x; hitY = y; hitZ = z;
                    return float4((float)blockId / 255.0, (float)faceHit / 255.0, frac(distance * 16.0), 1.0);
                }

                // Step to next voxel boundary.
                if (tMaxX < tMaxY && tMaxX < tMaxZ)
                {
                    distance = tMaxX;
                    tMaxX += tDeltaX;
                    x += stepX;
                    faceHit = (stepX > 0) ? 0 /*-X face of new block*/ : 1 /*+X*/;
                }
                else if (tMaxY < tMaxZ)
                {
                    distance = tMaxY;
                    tMaxY += tDeltaY;
                    y += stepY;
                    faceHit = (stepY > 0) ? 2 : 3;
                }
                else
                {
                    distance = tMaxZ;
                    tMaxZ += tDeltaZ;
                    z += stepZ;
                    faceHit = (stepZ > 0) ? 4 : 5;
                }

                if (distance > _MaxDistance) break;
            }
            return float4(0, 0, 0, 0); // miss
        }
        ENDCG

        // Pass 0: hit info (blockId, face, fractional distance, hit flag).
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int hx, hy, hz;
                return MarchRay(hx, hy, hz);
            }
            ENDCG
        }

        // Pass 1: hit world position, encoded as (x_low, x_high, y_low, z_low) with another
        // pass writing the high bytes if needed. For our usage range (±32K blocks) the low
        // 16 bits per axis are sufficient with sign extension on the CPU side.
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                int hx, hy, hz;
                float4 hit = MarchRay(hx, hy, hz);
                if (hit.a < 0.5) return 0;
                // Encode hx mod 256 in .r, (hx >> 8) mod 256 in .g, hy mod 256 in .b, hz mod 256 in .a.
                int xl = ((hx % 256) + 256) % 256;
                int xh = (((hx >> 8) % 256) + 256) % 256;
                int yl = ((hy % 256) + 256) % 256;
                int zl = ((hz % 256) + 256) % 256;
                return float4(xl / 255.0, xh / 255.0, yl / 255.0, zl / 255.0);
            }
            ENDCG
        }
    }
}
