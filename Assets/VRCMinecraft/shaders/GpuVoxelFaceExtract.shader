Shader "VRCM/GpuVoxelFaceExtract"
{
    Properties
    {
        _MainTex ("Block Atlas", 2D) = "black" {}
        _BlockPropsTex ("Block Props", 2D) = "black" {}
        _SlotLookupTex ("Slot Lookup", 2D) = "black" {}
        _SlotMetaTex ("Slot Meta", 2D) = "black" {}
        _DrawTableTex ("Draw Table 256x256", 2D) = "black" {}
        _Mode ("Mode", Float) = 0
        _ReadSlotIndex ("Read Slot Index", Float) = 0
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

            sampler2D _MainTex;       // Block Atlas
            sampler2D _BlockPropsTex;
            sampler2D _SlotLookupTex;
            sampler2D _SlotMetaTex;
            sampler2D _DrawTableTex;  // 256x256 shouldDraw lookup
            float _Mode;
            float _ReadSlotIndex;
            float4 _UdonVRCM_GpuAtlasInfo;   // (atlasWidth, atlasHeight, atlasSlotsX, atlasSlotsY)
            float4 _UdonVRCM_GpuWorldInfo;    // (worldDimX, worldDimY, worldDimZ, worldDimY*worldDimZ)
            float4 _UdonVRCM_GpuChunkInfo;    // (chunkSizeXZ, chunkSizeY, chunkOffsetX, chunkOffsetZ)
            float4 _UdonVRCM_GpuVoxelOffset;  // (globalVoxelOffsetX, Y, Z, chunkSlotCapacity)

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // Decode a slot index from the slot lookup texture
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

            // Sample a block ID from an atlas slot at local coordinates
            float SampleBlockId(float slotIndex, float localX, float localY, float localZ)
            {
                float tileX = fmod(slotIndex, _UdonVRCM_GpuAtlasInfo.z);
                float tileY = floor(slotIndex / _UdonVRCM_GpuAtlasInfo.z);
                float atlasU = (tileX * _UdonVRCM_GpuChunkInfo.x + localX + 0.5) / _UdonVRCM_GpuAtlasInfo.x;
                float atlasV = (tileY * (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x) + localY * _UdonVRCM_GpuChunkInfo.x + localZ + 0.5) / _UdonVRCM_GpuAtlasInfo.y;
                return floor(tex2D(_MainTex, float2(atlasU, atlasV)).r * 255.0 + 0.5);
            }

            // Get the neighbor block ID, handling cross-chunk boundaries
            float GetNeighborBlockId(float slotIndex, float chunkX, float chunkY, float chunkZ,
                                     float localX, float localY, float localZ,
                                     float dx, float dy, float dz)
            {
                float nx = localX + dx;
                float ny = localY + dy;
                float nz = localZ + dz;
                float nChunkX = chunkX;
                float nChunkY = chunkY;
                float nChunkZ = chunkZ;

                // Handle chunk boundary crossing
                if (nx < 0.0) { nx += _UdonVRCM_GpuChunkInfo.x; nChunkX -= 1.0; }
                else if (nx >= _UdonVRCM_GpuChunkInfo.x) { nx -= _UdonVRCM_GpuChunkInfo.x; nChunkX += 1.0; }

                if (ny < 0.0) { ny += _UdonVRCM_GpuChunkInfo.y; nChunkY -= 1.0; }
                else if (ny >= _UdonVRCM_GpuChunkInfo.y) { ny -= _UdonVRCM_GpuChunkInfo.y; nChunkY += 1.0; }

                if (nz < 0.0) { nz += _UdonVRCM_GpuChunkInfo.x; nChunkZ -= 1.0; }
                else if (nz >= _UdonVRCM_GpuChunkInfo.x) { nz -= _UdonVRCM_GpuChunkInfo.x; nChunkZ += 1.0; }

                // Same chunk — use provided slotIndex directly
                if (nChunkX == chunkX && nChunkY == chunkY && nChunkZ == chunkZ)
                {
                    return SampleBlockId(slotIndex, nx, ny, nz);
                }

                // Different chunk — look up its slot
                float valid = 0.0;
                float neighborSlot = LookupNeighborSlot(nChunkX, nChunkY, nChunkZ, valid);
                if (valid < 0.5) return 0.0; // treat unloaded chunk as air (draw face)
                return SampleBlockId(neighborSlot, nx, ny, nz);
            }

            // Look up the shouldDraw table: selfId on X axis, neighborId on Y axis
            float ShouldDrawFace(float selfId, float neighborId)
            {
                float2 drawUv = float2(
                    (neighborId + 0.5) / 256.0,
                    (selfId + 0.5) / 256.0
                );
                return tex2D(_DrawTableTex, drawUv).r;
            }

            float4 frag(v2f i) : SV_Target
            {
                if (_Mode > 1.5)
                {
                    float slotIndex = floor(_ReadSlotIndex + 0.5);
                    if (slotIndex < 0.0 || slotIndex >= _UdonVRCM_GpuVoxelOffset.w) return 0;

                    float2 slotMetaUv = float2((slotIndex + 0.5) / _UdonVRCM_GpuVoxelOffset.w, 0.5);
                    float4 slotMeta = tex2D(_SlotMetaTex, slotMetaUv);
                    if (slotMeta.a < 0.5) return 0;

                    float compactWidth = max(1.0, floor(_UdonVRCM_GpuChunkInfo.x * 0.5 + 0.5));
                    float compactHeight = 6.0 * _UdonVRCM_GpuChunkInfo.y;
                    float pixelX = floor(i.uv.x * compactWidth);
                    float pixelY = floor(i.uv.y * compactHeight);
                    float direction = floor(pixelY / _UdonVRCM_GpuChunkInfo.y);
                    float slice = pixelY - direction * _UdonVRCM_GpuChunkInfo.y;
                    if (direction < 0.0 || direction > 5.0) return 0;

                    float width = _UdonVRCM_GpuChunkInfo.x;
                    float height = _UdonVRCM_GpuChunkInfo.y;
                    float sliceCount = 0.0;
                    float maskWidth = 0.0;
                    float maskHeight = 0.0;

                    if (direction <= 1.0)
                    {
                        sliceCount = height;
                        maskWidth = width;
                        maskHeight = width;
                    }
                    else if (direction <= 3.0)
                    {
                        sliceCount = width;
                        maskWidth = width;
                        maskHeight = height;
                    }
                    else
                    {
                        sliceCount = width;
                        maskWidth = width;
                        maskHeight = height;
                    }

                    if (slice < 0.0 || slice >= sliceCount) return 0;

                    float chunkX = floor(slotMeta.r * 255.0 + 0.5);
                    float chunkY = floor(slotMeta.g * 255.0 + 0.5);
                    float chunkZ = floor(slotMeta.b * 255.0 + 0.5);
                    float rowMask0 = 0.0;
                    float rowMask1 = 0.0;
                    float v0 = pixelX * 2.0;
                    float v1 = v0 + 1.0;

                    [loop]
                    for (int uInt = 0; uInt < 16; uInt++)
                    {
                        float u = (float)uInt;
                        if (u >= maskWidth) break;

                        float localX = 0.0;
                        float localY = 0.0;
                        float localZ = 0.0;
                        float offsetX = 0.0;
                        float offsetY = 0.0;
                        float offsetZ = 0.0;

                        if (direction <= 1.0)
                        {
                            localX = u; localY = slice; localZ = v0;
                            offsetY = (direction < 0.5) ? 1.0 : -1.0;
                        }
                        else if (direction <= 3.0)
                        {
                            localX = u; localY = v0; localZ = slice;
                            offsetZ = (direction < 2.5) ? 1.0 : -1.0;
                        }
                        else
                        {
                            localX = slice; localY = v0; localZ = u;
                            offsetX = (direction < 4.5) ? 1.0 : -1.0;
                        }

                        if (v0 < maskHeight)
                        {
                            float selfId = SampleBlockId(slotIndex, localX, localY, localZ);
                            if (selfId >= 0.5)
                            {
                                float2 propUv = float2((selfId + 0.5) / 256.0, 0.5);
                                float4 blockProps = tex2D(_BlockPropsTex, propUv);
                                float shapeType = floor(blockProps.b * 255.0 + 0.5);
                                if (shapeType < 1.0)
                                {
                                    float neighborId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, offsetX, offsetY, offsetZ);
                                    if (ShouldDrawFace(selfId, neighborId) > 0.5) rowMask0 += exp2(u);
                                }
                            }
                        }

                        if (v1 < maskHeight)
                        {
                            if (direction <= 1.0)
                            {
                                localX = u; localY = slice; localZ = v1;
                            }
                            else if (direction <= 3.0)
                            {
                                localX = u; localY = v1; localZ = slice;
                            }
                            else
                            {
                                localX = slice; localY = v1; localZ = u;
                            }

                            float selfId = SampleBlockId(slotIndex, localX, localY, localZ);
                            if (selfId >= 0.5)
                            {
                                float2 propUv = float2((selfId + 0.5) / 256.0, 0.5);
                                float4 blockProps = tex2D(_BlockPropsTex, propUv);
                                float shapeType = floor(blockProps.b * 255.0 + 0.5);
                                if (shapeType < 1.0)
                                {
                                    float neighborId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, offsetX, offsetY, offsetZ);
                                    if (ShouldDrawFace(selfId, neighborId) > 0.5) rowMask1 += exp2(u);
                                }
                            }
                        }
                    }

                    float row0Lo = floor(fmod(rowMask0, 256.0));
                    float row0Hi = floor(rowMask0 / 256.0);
                    float row1Lo = floor(fmod(rowMask1, 256.0));
                    float row1Hi = floor(rowMask1 / 256.0);
                    return float4(row0Lo / 255.0, row0Hi / 255.0, row1Lo / 255.0, row1Hi / 255.0);
                }

                if (_Mode > 0.5)
                {
                    float slotIndex = floor(_ReadSlotIndex + 0.5);
                    if (slotIndex < 0.0 || slotIndex >= _UdonVRCM_GpuVoxelOffset.w) return 0;

                    float2 slotMetaUv = float2((slotIndex + 0.5) / _UdonVRCM_GpuVoxelOffset.w, 0.5);
                    float4 slotMeta = tex2D(_SlotMetaTex, slotMetaUv);
                    if (slotMeta.a < 0.5) return 0;

                    float localX = floor(i.uv.x * _UdonVRCM_GpuChunkInfo.x);
                    float packedYZ = floor(i.uv.y * (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x));
                    float localY = floor(packedYZ / _UdonVRCM_GpuChunkInfo.x);
                    float localZ = packedYZ - localY * _UdonVRCM_GpuChunkInfo.x;

                    float selfId = SampleBlockId(slotIndex, localX, localY, localZ);
                    if (selfId < 0.5) return 0;

                    float2 propUv = float2((selfId + 0.5) / 256.0, 0.5);
                    float4 blockProps = tex2D(_BlockPropsTex, propUv);
                    float shapeType = floor(blockProps.b * 255.0 + 0.5);
                    if (shapeType >= 1.0)
                    {
                        return float4(0.0, selfId / 255.0, shapeType / 255.0, 1.0);
                    }

                    float chunkX = floor(slotMeta.r * 255.0 + 0.5);
                    float chunkY = floor(slotMeta.g * 255.0 + 0.5);
                    float chunkZ = floor(slotMeta.b * 255.0 + 0.5);
                    float faceMask = 0.0;

                    float nId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, 0, 1, 0);
                    if (ShouldDrawFace(selfId, nId) > 0.5) faceMask += 1.0;

                    nId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, 0, -1, 0);
                    if (ShouldDrawFace(selfId, nId) > 0.5) faceMask += 2.0;

                    nId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, 0, 0, 1);
                    if (ShouldDrawFace(selfId, nId) > 0.5) faceMask += 4.0;

                    nId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, 0, 0, -1);
                    if (ShouldDrawFace(selfId, nId) > 0.5) faceMask += 8.0;

                    nId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, 1, 0, 0);
                    if (ShouldDrawFace(selfId, nId) > 0.5) faceMask += 16.0;

                    nId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, -1, 0, 0);
                    if (ShouldDrawFace(selfId, nId) > 0.5) faceMask += 32.0;

                    return float4(faceMask / 255.0, selfId / 255.0, shapeType / 255.0, 1.0);
                }

                // Decode atlas pixel position → slot + local (x, y, z)
                float pixelX = floor(i.uv.x * _UdonVRCM_GpuAtlasInfo.x);
                float pixelY = floor(i.uv.y * _UdonVRCM_GpuAtlasInfo.y);
                float tileX = floor(pixelX / _UdonVRCM_GpuChunkInfo.x);
                float tileY = floor(pixelY / (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x));
                float slotIndex = tileY * _UdonVRCM_GpuAtlasInfo.z + tileX;
                if (slotIndex < 0.0 || slotIndex >= _UdonVRCM_GpuVoxelOffset.w) return 0;

                // Check if slot is valid
                float2 slotMetaUv = float2((slotIndex + 0.5) / _UdonVRCM_GpuVoxelOffset.w, 0.5);
                float4 slotMeta = tex2D(_SlotMetaTex, slotMetaUv);
                if (slotMeta.a < 0.5) return 0;

                // Unpack local coordinates
                float localX = pixelX - tileX * _UdonVRCM_GpuChunkInfo.x;
                float packedYZ = pixelY - tileY * (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x);
                float localY = floor(packedYZ / _UdonVRCM_GpuChunkInfo.x);
                float localZ = packedYZ - localY * _UdonVRCM_GpuChunkInfo.x;

                // Read block ID at this voxel
                float atlasUvX = (pixelX + 0.5) / _UdonVRCM_GpuAtlasInfo.x;
                float atlasUvY = (pixelY + 0.5) / _UdonVRCM_GpuAtlasInfo.y;
                float selfId = floor(tex2D(_MainTex, float2(atlasUvX, atlasUvY)).r * 255.0 + 0.5);

                // Air blocks have no faces
                if (selfId < 0.5) return 0;

                // Read block properties
                float2 propUv = float2((selfId + 0.5) / 256.0, 0.5);
                float4 blockProps = tex2D(_BlockPropsTex, propUv);
                float shapeType = floor(blockProps.b * 255.0 + 0.5); // 0=cube, 1=cross

                // Cross-shaped blocks are handled separately (not via face extraction)
                // Encode them specially: faceMask=0, but blockID set + shape=1
                if (shapeType >= 1.0)
                {
                    return float4(0.0, selfId / 255.0, shapeType / 255.0, 1.0);
                }

                // Get chunk array coordinates from slot meta
                float chunkX = floor(slotMeta.r * 255.0 + 0.5);
                float chunkY = floor(slotMeta.g * 255.0 + 0.5);
                float chunkZ = floor(slotMeta.b * 255.0 + 0.5);

                // Test 6 faces: +Y, -Y, +Z, -Z, +X, -X
                // Directions: 0=+Y, 1=-Y, 2=+Z, 3=-Z, 4=+X, 5=-X
                float faceMask = 0.0;

                // +Y face (direction 0)
                float nId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, 0, 1, 0);
                if (ShouldDrawFace(selfId, nId) > 0.5) faceMask += 1.0;

                // -Y face (direction 1)
                nId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, 0, -1, 0);
                if (ShouldDrawFace(selfId, nId) > 0.5) faceMask += 2.0;

                // +Z face (direction 2)
                nId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, 0, 0, 1);
                if (ShouldDrawFace(selfId, nId) > 0.5) faceMask += 4.0;

                // -Z face (direction 3)
                nId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, 0, 0, -1);
                if (ShouldDrawFace(selfId, nId) > 0.5) faceMask += 8.0;

                // +X face (direction 4)
                nId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, 1, 0, 0);
                if (ShouldDrawFace(selfId, nId) > 0.5) faceMask += 16.0;

                // -X face (direction 5)
                nId = GetNeighborBlockId(slotIndex, chunkX, chunkY, chunkZ, localX, localY, localZ, -1, 0, 0);
                if (ShouldDrawFace(selfId, nId) > 0.5) faceMask += 32.0;

                // Output: R=faceMask/255, G=blockID/255, B=shapeType/255, A=1 (valid)
                return float4(faceMask / 255.0, selfId / 255.0, shapeType / 255.0, 1.0);
            }
            ENDCG
        }
    }
}
