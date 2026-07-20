#ifndef VRCM_MC_TERRAIN_GPU_EXACT_AO_INCLUDED
#define VRCM_MC_TERRAIN_GPU_EXACT_AO_INCLUDED

float4 gpuVoxelSamplePoint(sampler2D tex, float2 uv)
{
    return tex2D(tex, uv);
}

half gpuVoxelCalcBetaLightBrightnessFromLevel(float lightLevel)
{
    float darkness = 1.0 - saturate(lightLevel / 15.0);
    return (1.0 - darkness) / (darkness * 3.0 + 1.0) * 0.95 + 0.05;
}

// ATLAS-MISS POLICY: a sample that lands in a chunk with no atlas slot (just streamed in /
// evicted from the lit set / beyond the world) or an unseeded light slot is treated as FULLY
// SKYLIT with the day/night subtraction applied — bright by day, correctly dim at night.
// Vanilla chunks always arrive pre-lit; any CONSTANT fallback here reads as a noon-bright
// chunk at night (the old i.color.a/1.0 paths) or a black hole by day (the old level-0 paths).
float gpuVoxelAtlasMissLightLevel()
{
    float skyLevel = 15.0 - _UdonVRCM_SkylightSub;
    return skyLevel < 0.0 ? 0.0 : skyLevel;
}

half gpuVoxelAtlasMissBrightness()
{
    return gpuVoxelCalcBetaLightBrightnessFromLevel(gpuVoxelAtlasMissLightLevel());
}

float gpuVoxelTryLookupAtlasUv(float3 samplePos, out float2 atlasUv)
{
    atlasUv = float2(0.0, 0.0);

    float coordinateBias = 0.0001;
    float globalX = floor(samplePos.x + coordinateBias) + _UdonVRCM_GpuVoxelOffset.x;
    float globalY = floor(samplePos.y + coordinateBias) + _UdonVRCM_GpuVoxelOffset.y;
    float globalZ = floor(samplePos.z + coordinateBias) + _UdonVRCM_GpuVoxelOffset.z;

    float maxWorldX = _UdonVRCM_GpuWorldInfo.x * _UdonVRCM_GpuChunkInfo.x;
    float maxWorldY = _UdonVRCM_GpuWorldInfo.y * _UdonVRCM_GpuChunkInfo.y;
    float maxWorldZ = _UdonVRCM_GpuWorldInfo.z * _UdonVRCM_GpuChunkInfo.x;
    if (globalX < 0.0 || globalY < 0.0 || globalZ < 0.0) return 0.0;
    if (globalX >= maxWorldX || globalY >= maxWorldY || globalZ >= maxWorldZ) return 0.0;

    float chunkX = floor(globalX / _UdonVRCM_GpuChunkInfo.x);
    float chunkY = floor(globalY / _UdonVRCM_GpuChunkInfo.y);
    float chunkZ = floor(globalZ / _UdonVRCM_GpuChunkInfo.x);
    float localX = globalX - chunkX * _UdonVRCM_GpuChunkInfo.x;
    float localY = globalY - chunkY * _UdonVRCM_GpuChunkInfo.y;
    float localZ = globalZ - chunkZ * _UdonVRCM_GpuChunkInfo.x;

    float lookupRow = chunkY * _UdonVRCM_GpuWorldInfo.z + chunkZ;
    float2 lookupUv = float2(
        (chunkX + 0.5) / _UdonVRCM_GpuWorldInfo.x,
        (lookupRow + 0.5) / (_UdonVRCM_GpuWorldInfo.y * _UdonVRCM_GpuWorldInfo.z)
    );
    float4 slotData = gpuVoxelSamplePoint(_UdonVRCM_GpuSlotLookup, lookupUv);
    if (slotData.a < 0.5) return 0.0;

    float slotLow = floor(slotData.r * 255.0 + 0.5);
    float slotHigh = floor(slotData.g * 255.0 + 0.5);
    float slotIndex = slotLow + slotHigh * 256.0;
    float tileX = fmod(slotIndex, _UdonVRCM_GpuAtlasInfo.z);
    float tileY = floor(slotIndex / _UdonVRCM_GpuAtlasInfo.z);
    atlasUv = float2(
        (tileX * _UdonVRCM_GpuChunkInfo.x + localX + 0.5) / _UdonVRCM_GpuAtlasInfo.x,
        (tileY * (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x) + localY * _UdonVRCM_GpuChunkInfo.x + localZ + 0.5) / _UdonVRCM_GpuAtlasInfo.y
    );
    return 1.0;
}

float gpuVoxelTrySampleLightLevel(float3 samplePos, out float lightLevel)
{
    lightLevel = 0.0;

    float2 atlasUv;
    if (gpuVoxelTryLookupAtlasUv(samplePos, atlasUv) < 0.5) return 0.0;

    float4 lightSample = gpuVoxelSamplePoint(_UdonVRCM_GpuLightAtlas, atlasUv);
    if (lightSample.a < 0.5) return 0.0;

    // DAY/NIGHT: the atlas stores skylight at MAX (noon) — the time-of-day darkening happens
    // here at sample time, subtracted from SKY ONLY and then maxed with block light
    // (b1.7.3 Chunk.getBlockLightValue order; max-then-subtract would darken torch-lit
    // areas at night). _UdonVRCM_SkylightSub is 0-11, set once per tick change from Udon.
    float skyLevel = floor(lightSample.r * 15.0 + 0.5) - _UdonVRCM_SkylightSub;
    if (skyLevel < 0.0) skyLevel = 0.0;
    lightLevel = max(skyLevel, floor(lightSample.g * 15.0 + 0.5));
    return 1.0;
}

half gpuVoxelSampleLightBrightnessAtPosition(float3 samplePos)
{
    float lightLevel;
    if (gpuVoxelTrySampleLightLevel(samplePos, lightLevel) < 0.5) return -1.0;
    return gpuVoxelCalcBetaLightBrightnessFromLevel(lightLevel);
}

half gpuVoxelSampleLightBrightness(float3 worldPos, float3 faceNormal)
{
    if (_UdonVRCM_GpuEnabled < 0.5) return -1.0;
    if (max(max(abs(faceNormal.x), abs(faceNormal.y)), abs(faceNormal.z)) < 0.9)
    {
        return gpuVoxelSampleLightBrightnessAtPosition(worldPos);
    }

    return gpuVoxelSampleLightBrightnessAtPosition(worldPos + normalize(faceNormal) * 0.501);
}

float gpuVoxelTrySampleBlockProps(float3 samplePos, out float4 blockProps)
{
    blockProps = 0.0;

    float2 atlasUv;
    if (gpuVoxelTryLookupAtlasUv(samplePos, atlasUv) < 0.5) return 0.0;

    float4 blockSample = gpuVoxelSamplePoint(_UdonVRCM_GpuBlockAtlas, atlasUv);
    float blockId = floor(blockSample.r * 255.0 + 0.5);
    float2 propUv = float2((blockId + 0.5) / 256.0, 0.5);
    blockProps = gpuVoxelSamplePoint(_UdonVRCM_GpuBlockProps, propUv);
    return 1.0;
}

float gpuVoxelSampleEmissionLevel(float3 samplePos)
{
    float4 blockProps;
    if (gpuVoxelTrySampleBlockProps(samplePos, blockProps) < 0.5) return 0.0;
    return floor(blockProps.g * 15.0 + 0.5);
}

float gpuVoxelCanBlockGrass(float3 samplePos)
{
    float4 blockProps;
    if (gpuVoxelTrySampleBlockProps(samplePos, blockProps) < 0.5) return 1.0;
    return blockProps.a >= 0.5 ? 1.0 : 0.0;
}

float gpuVoxelSampleLightLevelWithEmissionFloor(float3 samplePos, float emittedLight)
{
    float lightLevel;
    // Miss -> assume skylit (see gpuVoxelAtlasMissLightLevel): AO corners at the lit-set /
    // world edge used to collapse to level 0, dark-banding the far edges by day.
    if (gpuVoxelTrySampleLightLevel(samplePos, lightLevel) < 0.5)
        return max(gpuVoxelAtlasMissLightLevel(), emittedLight);
    return max(lightLevel, emittedLight);
}

void gpuVoxelGetAoBasis(float3 faceNormal, out float3 normalAxis, out float3 tangentU, out float3 tangentV)
{
    if (faceNormal.y > 0.5)
    {
        normalAxis = float3(0.0, 1.0, 0.0);
        tangentU = float3(1.0, 0.0, 0.0);
        tangentV = float3(0.0, 0.0, 1.0);
    }
    else if (faceNormal.y < -0.5)
    {
        normalAxis = float3(0.0, -1.0, 0.0);
        tangentU = float3(1.0, 0.0, 0.0);
        tangentV = float3(0.0, 0.0, 1.0);
    }
    else if (faceNormal.z > 0.5)
    {
        normalAxis = float3(0.0, 0.0, 1.0);
        tangentU = float3(1.0, 0.0, 0.0);
        tangentV = float3(0.0, 1.0, 0.0);
    }
    else if (faceNormal.z < -0.5)
    {
        normalAxis = float3(0.0, 0.0, -1.0);
        tangentU = float3(1.0, 0.0, 0.0);
        tangentV = float3(0.0, 1.0, 0.0);
    }
    else if (faceNormal.x > 0.5)
    {
        normalAxis = float3(1.0, 0.0, 0.0);
        tangentU = float3(0.0, 0.0, 1.0);
        tangentV = float3(0.0, 1.0, 0.0);
    }
    else
    {
        normalAxis = float3(-1.0, 0.0, 0.0);
        tangentU = float3(0.0, 0.0, 1.0);
        tangentV = float3(0.0, 1.0, 0.0);
    }
}

half gpuVoxelComputeExactAoCornerBrightness(float3 blockPos, float3 normalAxis, float3 tangentU, float3 tangentV, float emittedLight, float signU, float signV)
{
    float3 faceSamplePos = blockPos + normalAxis;
    float3 sideUSamplePos = faceSamplePos + tangentU * signU;
    float3 sideVSamplePos = faceSamplePos + tangentV * signV;

    float faceLight = gpuVoxelSampleLightLevelWithEmissionFloor(faceSamplePos, emittedLight);
    float sideULight = gpuVoxelSampleLightLevelWithEmissionFloor(sideUSamplePos, emittedLight);
    float sideVLight = gpuVoxelSampleLightLevelWithEmissionFloor(sideVSamplePos, emittedLight);

    float diagonalLight = sideULight;
    if (gpuVoxelCanBlockGrass(sideUSamplePos) > 0.5 || gpuVoxelCanBlockGrass(sideVSamplePos) > 0.5)
    {
        float diagonalSample;
        if (gpuVoxelTrySampleLightLevel(faceSamplePos + tangentU * signU + tangentV * signV, diagonalSample) > 0.5)
        {
            diagonalLight = max(diagonalSample, emittedLight);
        }
    }

    return (gpuVoxelCalcBetaLightBrightnessFromLevel(faceLight) +
        gpuVoxelCalcBetaLightBrightnessFromLevel(sideULight) +
        gpuVoxelCalcBetaLightBrightnessFromLevel(sideVLight) +
        gpuVoxelCalcBetaLightBrightnessFromLevel(diagonalLight)) * 0.25;
}

half gpuVoxelComputeExactAoBrightness(float3 worldPos, float3 faceNormal)
{
    if (_UseGpuExactAo < 0.5 || _UdonVRCM_GpuEnabled < 0.5) return -1.0;

    float3 normalAxis;
    float3 tangentU;
    float3 tangentV;
    gpuVoxelGetAoBasis(faceNormal, normalAxis, tangentU, tangentV);

    float3 blockPos = floor(worldPos - normalAxis * 0.501);
    float emittedLight = gpuVoxelSampleEmissionLevel(blockPos);

    float rawU = abs(tangentU.x) > 0.5 ? worldPos.x : (abs(tangentU.y) > 0.5 ? worldPos.y : worldPos.z);
    float rawV = abs(tangentV.x) > 0.5 ? worldPos.x : (abs(tangentV.y) > 0.5 ? worldPos.y : worldPos.z);
    float fracU = clamp(frac(rawU), 0.001, 0.999);
    float fracV = clamp(frac(rawV), 0.001, 0.999);

    half corner0 = gpuVoxelComputeExactAoCornerBrightness(blockPos, normalAxis, tangentU, tangentV, emittedLight, -1.0, -1.0);
    half corner1 = gpuVoxelComputeExactAoCornerBrightness(blockPos, normalAxis, tangentU, tangentV, emittedLight, -1.0, 1.0);
    half corner2 = gpuVoxelComputeExactAoCornerBrightness(blockPos, normalAxis, tangentU, tangentV, emittedLight, 1.0, 1.0);
    half corner3 = gpuVoxelComputeExactAoCornerBrightness(blockPos, normalAxis, tangentU, tangentV, emittedLight, 1.0, -1.0);

    half bottom = lerp(corner0, corner3, fracU);
    half top = lerp(corner1, corner2, fracU);
    return lerp(bottom, top, fracV);
}

#endif
