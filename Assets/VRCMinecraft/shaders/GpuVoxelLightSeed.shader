Shader "VRCM/GpuVoxelLightSeed"
{
    Properties
    {
        _MainTex ("Current Light Atlas", 2D) = "black" {}
        _BlockAtlas ("Block Atlas", 2D) = "black" {}
        _BlockPropsTex ("Block Props", 2D) = "black" {}
        _SlotLookupTex ("Slot Lookup", 2D) = "black" {}
        _SlotMetaTex ("Slot Meta", 2D) = "black" {}
        _TopSkyLight ("Top Sky Light", Float) = 15
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            ZTest Always
            Cull Off
            ZWrite Off

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

            sampler2D _MainTex;
            sampler2D _BlockAtlas;
            sampler2D _BlockPropsTex;
            sampler2D _SlotLookupTex;
            sampler2D _SlotMetaTex;
            float4 _UdonVRCM_GpuAtlasInfo;
            float4 _UdonVRCM_GpuWorldInfo;
            float4 _UdonVRCM_GpuChunkInfo;
            float4 _UdonVRCM_GpuVoxelOffset;
            float _TopSkyLight;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 SampleAtlas(float slotIndex, float localX, float localY, float localZ, sampler2D atlasTex)
            {
                float tileX = fmod(slotIndex, _UdonVRCM_GpuAtlasInfo.z);
                float tileY = floor(slotIndex / _UdonVRCM_GpuAtlasInfo.z);
                float atlasU = (tileX * _UdonVRCM_GpuChunkInfo.x + localX + 0.5) / _UdonVRCM_GpuAtlasInfo.x;
                float atlasV = (tileY * (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x) + localY * _UdonVRCM_GpuChunkInfo.x + localZ + 0.5) / _UdonVRCM_GpuAtlasInfo.y;
                return tex2D(atlasTex, float2(atlasU, atlasV));
            }

            float LookupNeighborSlot(float chunkX, float chunkY, float chunkZ, out float valid)
            {
                valid = 0.0;
                if (chunkX < 0.0 || chunkY < 0.0 || chunkZ < 0.0) return 0.0;
                if (chunkX >= _UdonVRCM_GpuWorldInfo.x || chunkY >= _UdonVRCM_GpuWorldInfo.y || chunkZ >= _UdonVRCM_GpuWorldInfo.z) return 0.0;

                float lookupRow = chunkY * _UdonVRCM_GpuWorldInfo.z + chunkZ;
                float2 lookupUv = float2(
                    (chunkX + 0.5) / _UdonVRCM_GpuWorldInfo.x,
                    (lookupRow + 0.5) / (_UdonVRCM_GpuWorldInfo.y * _UdonVRCM_GpuWorldInfo.z)
                );
                float4 slotData = tex2D(_SlotLookupTex, lookupUv);
                if (slotData.a < 0.5) return 0.0;
                valid = 1.0;
                float slotLow = floor(slotData.r * 255.0 + 0.5);
                float slotHigh = floor(slotData.g * 255.0 + 0.5);
                return slotLow + slotHigh * 256.0;
            }

            float GetBlockOpacity(float slotIndex, float localX, float localY, float localZ)
            {
                float blockId = floor(SampleAtlas(slotIndex, localX, localY, localZ, _BlockAtlas).r * 255.0 + 0.5);
                float2 propUv = float2((blockId + 0.5) / 256.0, 0.5);
                float4 blockProps = tex2D(_BlockPropsTex, propUv);
                return floor(blockProps.r * 15.0 + 0.5);
            }

            float4 frag(v2f i) : SV_Target
            {
                float pixelX = floor(i.uv.x * _UdonVRCM_GpuAtlasInfo.x);
                float pixelY = floor(i.uv.y * _UdonVRCM_GpuAtlasInfo.y);
                float tileX = floor(pixelX / _UdonVRCM_GpuChunkInfo.x);
                float tileY = floor(pixelY / (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x));
                float slotIndex = tileY * _UdonVRCM_GpuAtlasInfo.z + tileX;
                if (slotIndex < 0.0 || slotIndex >= _UdonVRCM_GpuVoxelOffset.w) return 0;

                float2 slotMetaUv = float2((slotIndex + 0.5) / _UdonVRCM_GpuVoxelOffset.w, 0.5);
                float4 slotMeta = tex2D(_SlotMetaTex, slotMetaUv);
                if (slotMeta.a < 0.5) return 0;

                float localX = pixelX - tileX * _UdonVRCM_GpuChunkInfo.x;
                float packedYZ = pixelY - tileY * (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x);
                float localY = floor(packedYZ / _UdonVRCM_GpuChunkInfo.x);
                float localZ = packedYZ - localY * _UdonVRCM_GpuChunkInfo.x;
                float atlasUvX = (pixelX + 0.5) / _UdonVRCM_GpuAtlasInfo.x;
                float atlasUvY = (pixelY + 0.5) / _UdonVRCM_GpuAtlasInfo.y;
                float blockId = floor(tex2D(_BlockAtlas, float2(atlasUvX, atlasUvY)).r * 255.0 + 0.5);
                float2 propUv = float2((blockId + 0.5) / 256.0, 0.5);
                float4 blockProps = tex2D(_BlockPropsTex, propUv);
                float opacity = floor(blockProps.r * 15.0 + 0.5);
                float emission = floor(blockProps.g * 15.0 + 0.5);

                float chunkX = floor(slotMeta.r * 255.0 + 0.5);
                float chunkY = floor(slotMeta.g * 255.0 + 0.5);
                float chunkZ = floor(slotMeta.b * 255.0 + 0.5);
                int worldYInt = (int)(chunkY * _UdonVRCM_GpuChunkInfo.y + localY);
                int worldTopYInt = (int)(_UdonVRCM_GpuWorldInfo.y * _UdonVRCM_GpuChunkInfo.y) - 1;

                float runningSkyLight = _TopSkyLight;
                float skyLight = 0.0;
                for (int scanOffset = 0; scanOffset < 256; scanOffset++)
                {
                    int scanWorldYInt = worldTopYInt - scanOffset;
                    if (scanWorldYInt < worldYInt || scanWorldYInt < 0)
                    {
                        break;
                    }

                    float scanChunkY = floor(scanWorldYInt / _UdonVRCM_GpuChunkInfo.y);
                    float scanLocalY = scanWorldYInt - scanChunkY * _UdonVRCM_GpuChunkInfo.y;
                    float validScanSlot = 0.0;
                    float scanSlotIndex = LookupNeighborSlot(chunkX, scanChunkY, chunkZ, validScanSlot);
                    float opacityAtScan = 0.0;
                    if (validScanSlot >= 0.5)
                    {
                        opacityAtScan = GetBlockOpacity(scanSlotIndex, localX, scanLocalY, localZ);
                    }

                    float nextSkyLight = max(0.0, runningSkyLight - opacityAtScan);
                    if (scanWorldYInt == worldYInt)
                    {
                        skyLight = nextSkyLight;
                        break;
                    }

                    runningSkyLight = nextSkyLight;
                }

                float blockLight = emission;
                if (opacity > 0.0 && opacity < 15.0 && runningSkyLight > skyLight && skyLight > blockLight)
                {
                    blockLight = skyLight;
                }

                // Per-slot dirty tracking via alpha channel:
                // Cleared slots (alpha < 0.5) get fresh column-scan seed values.
                // Already-seeded slots (alpha >= 0.5) pass through unchanged,
                // preserving horizontally-propagated light from the BFS shader.
                float4 existing = tex2D(_MainTex, float2(atlasUvX, atlasUvY));
                if (existing.a >= 0.5)
                {
                    return existing;
                }

                float combined = max(skyLight, blockLight);
                return float4(skyLight / 15.0, blockLight / 15.0, combined / 15.0, 1.0);
            }
            ENDCG
        }
    }
}
