Shader "VRCM/GpuVoxelLightPropagate"
{
    Properties
    {
        _MainTex ("Current Light Atlas", 2D) = "black" {}
        _BlockAtlas ("Block Atlas", 2D) = "black" {}
        _BlockPropsTex ("Block Props", 2D) = "black" {}
        _SlotLookupTex ("Slot Lookup", 2D) = "black" {}
        _SlotMetaTex ("Slot Meta", 2D) = "black" {}
        _TopSkyLight ("Top Sky Light", Float) = 15
        _FrameJitter ("Frame Jitter", Int) = 0
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
            int _FrameJitter;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 GetSlotMeta(float slotIndex)
            {
                return tex2D(_SlotMetaTex, float2((slotIndex + 0.5) / _UdonVRCM_GpuVoxelOffset.w, 0.5));
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

            float4 SampleNeighborLight(float chunkX, float chunkY, float chunkZ, float localX, float localY, float localZ)
            {
                float valid = 0.0;
                float slotIndex = LookupNeighborSlot(chunkX, chunkY, chunkZ, valid);
                if (valid < 0.5) return 0.0;
                return SampleAtlas(slotIndex, localX, localY, localZ, _MainTex);
            }

            float4 frag(v2f i) : SV_Target
            {
                float pixelX = floor(i.uv.x * _UdonVRCM_GpuAtlasInfo.x);
                float pixelY = floor(i.uv.y * _UdonVRCM_GpuAtlasInfo.y);
                float tileX = floor(pixelX / _UdonVRCM_GpuChunkInfo.x);
                float tileY = floor(pixelY / (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x));
                float slotIndex = tileY * _UdonVRCM_GpuAtlasInfo.z + tileX;
                if (slotIndex < 0.0 || slotIndex >= _UdonVRCM_GpuVoxelOffset.w) return 0;

                float4 slotMeta = GetSlotMeta(slotIndex);
                if (slotMeta.a < 0.5) return 0;

                float localX = pixelX - tileX * _UdonVRCM_GpuChunkInfo.x;
                float packedYZ = pixelY - tileY * (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x);
                float localY = floor(packedYZ / _UdonVRCM_GpuChunkInfo.x);
                float localZ = packedYZ - localY * _UdonVRCM_GpuChunkInfo.x;

                float atlasUvX = (pixelX + 0.5) / _UdonVRCM_GpuAtlasInfo.x;
                float atlasUvY = (pixelY + 0.5) / _UdonVRCM_GpuAtlasInfo.y;
                float blockId = floor(tex2D(_BlockAtlas, float2(atlasUvX, atlasUvY)).r * 255.0 + 0.5);
                float4 blockProps = tex2D(_BlockPropsTex, float2((blockId + 0.5) / 256.0, 0.5));
                float opacity = floor(blockProps.r * 15.0 + 0.5);
                float emission = floor(blockProps.g * 15.0 + 0.5);
                float spreadAttenuation = max(1.0, opacity);

                float chunkX = floor(slotMeta.r * 255.0 + 0.5);
                float chunkY = floor(slotMeta.g * 255.0 + 0.5);
                float chunkZ = floor(slotMeta.b * 255.0 + 0.5);

                float worldY = chunkY * _UdonVRCM_GpuChunkInfo.y + localY;
                float topWorldY = _UdonVRCM_GpuWorldInfo.y * _UdonVRCM_GpuChunkInfo.y - 1.0;

                float skyLight = 0.0;
                float blockLight = emission;

                if (opacity >= 15.0 && emission <= 0.0)
                {
                    return float4(0.0, 0.0, 0.0, 1.0);
                }

                if (worldY >= topWorldY && opacity < 15.0)
                {
                    float topSkyLight = max(0.0, _TopSkyLight - opacity);
                    skyLight = max(skyLight, topSkyLight);
                    if (opacity > 0.0 && topSkyLight > blockLight)
                    {
                        blockLight = topSkyLight;
                    }
                }

                float4 neighborLight;
                float neighborSkyLight;
                float propagatedSkyLight;
                float neighborBlockLight;

                // Preserve full vertical skylight through transparent columns.
                // This mirrors the CPU path's top-down sky fill, where air does not
                // lose brightness simply because it is farther from the top of the world.
                float py = localY + 1.0;
                float pChunkY = chunkY;
                if (py >= _UdonVRCM_GpuChunkInfo.y) { py -= _UdonVRCM_GpuChunkInfo.y; pChunkY += 1.0; }
                neighborLight = SampleNeighborLight(chunkX, pChunkY, chunkZ, localX, py, localZ);
                neighborSkyLight = floor(neighborLight.r * 15.0 + 0.5);
                propagatedSkyLight = max(0.0, neighborSkyLight - opacity);
                skyLight = max(skyLight, propagatedSkyLight);
                neighborBlockLight = floor(neighborLight.g * 15.0 + 0.5);
                blockLight = max(blockLight, max(0.0, neighborBlockLight - spreadAttenuation));
                if (opacity > 0.0 && opacity < 15.0 && neighborSkyLight > propagatedSkyLight && propagatedSkyLight > 0.0)
                {
                    blockLight = max(blockLight, propagatedSkyLight);
                }

                float nx = localX - 1.0;
                float nChunkX = chunkX;
                if (nx < 0.0) { nx += _UdonVRCM_GpuChunkInfo.x; nChunkX -= 1.0; }
                neighborLight = SampleNeighborLight(nChunkX, chunkY, chunkZ, nx, localY, localZ);
                neighborSkyLight = floor(neighborLight.r * 15.0 + 0.5);
                propagatedSkyLight = max(0.0, neighborSkyLight - spreadAttenuation);
                skyLight = max(skyLight, propagatedSkyLight);
                neighborBlockLight = floor(neighborLight.g * 15.0 + 0.5);
                blockLight = max(blockLight, max(0.0, neighborBlockLight - spreadAttenuation));
                if (opacity > 1.0 && opacity < 15.0 && neighborSkyLight > propagatedSkyLight && propagatedSkyLight > 0.0)
                {
                    blockLight = max(blockLight, propagatedSkyLight);
                }

                float px = localX + 1.0;
                float pChunkX = chunkX;
                if (px >= _UdonVRCM_GpuChunkInfo.x) { px -= _UdonVRCM_GpuChunkInfo.x; pChunkX += 1.0; }
                neighborLight = SampleNeighborLight(pChunkX, chunkY, chunkZ, px, localY, localZ);
                neighborSkyLight = floor(neighborLight.r * 15.0 + 0.5);
                propagatedSkyLight = max(0.0, neighborSkyLight - spreadAttenuation);
                skyLight = max(skyLight, propagatedSkyLight);
                neighborBlockLight = floor(neighborLight.g * 15.0 + 0.5);
                blockLight = max(blockLight, max(0.0, neighborBlockLight - spreadAttenuation));
                if (opacity > 1.0 && opacity < 15.0 && neighborSkyLight > propagatedSkyLight && propagatedSkyLight > 0.0)
                {
                    blockLight = max(blockLight, propagatedSkyLight);
                }

                float ny = localY - 1.0;
                float nChunkY = chunkY;
                if (ny < 0.0) { ny += _UdonVRCM_GpuChunkInfo.y; nChunkY -= 1.0; }
                neighborLight = SampleNeighborLight(chunkX, nChunkY, chunkZ, localX, ny, localZ);
                neighborSkyLight = floor(neighborLight.r * 15.0 + 0.5);
                propagatedSkyLight = max(0.0, neighborSkyLight - spreadAttenuation);
                skyLight = max(skyLight, propagatedSkyLight);
                neighborBlockLight = floor(neighborLight.g * 15.0 + 0.5);
                blockLight = max(blockLight, max(0.0, neighborBlockLight - spreadAttenuation));
                if (opacity > 1.0 && opacity < 15.0 && neighborSkyLight > propagatedSkyLight && propagatedSkyLight > 0.0)
                {
                    blockLight = max(blockLight, propagatedSkyLight);
                }

                float nz = localZ - 1.0;
                float nChunkZ = chunkZ;
                if (nz < 0.0) { nz += _UdonVRCM_GpuChunkInfo.x; nChunkZ -= 1.0; }
                neighborLight = SampleNeighborLight(chunkX, chunkY, nChunkZ, localX, localY, nz);
                neighborSkyLight = floor(neighborLight.r * 15.0 + 0.5);
                propagatedSkyLight = max(0.0, neighborSkyLight - spreadAttenuation);
                skyLight = max(skyLight, propagatedSkyLight);
                neighborBlockLight = floor(neighborLight.g * 15.0 + 0.5);
                blockLight = max(blockLight, max(0.0, neighborBlockLight - spreadAttenuation));
                if (opacity > 1.0 && opacity < 15.0 && neighborSkyLight > propagatedSkyLight && propagatedSkyLight > 0.0)
                {
                    blockLight = max(blockLight, propagatedSkyLight);
                }

                float pz = localZ + 1.0;
                float pChunkZ = chunkZ;
                if (pz >= _UdonVRCM_GpuChunkInfo.x) { pz -= _UdonVRCM_GpuChunkInfo.x; pChunkZ += 1.0; }
                neighborLight = SampleNeighborLight(chunkX, chunkY, pChunkZ, localX, localY, pz);
                neighborSkyLight = floor(neighborLight.r * 15.0 + 0.5);
                propagatedSkyLight = max(0.0, neighborSkyLight - spreadAttenuation);
                skyLight = max(skyLight, propagatedSkyLight);
                neighborBlockLight = floor(neighborLight.g * 15.0 + 0.5);
                blockLight = max(blockLight, max(0.0, neighborBlockLight - spreadAttenuation));
                if (opacity > 1.0 && opacity < 15.0 && neighborSkyLight > propagatedSkyLight && propagatedSkyLight > 0.0)
                {
                    blockLight = max(blockLight, propagatedSkyLight);
                }

                float combined = max(skyLight, blockLight);
                return float4(skyLight / 15.0, blockLight / 15.0, combined / 15.0, 1.0);
            }
            ENDCG
        }
    }
}
