Shader "Unlit/MCTerrain (Transparent)"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _BiomeColor ("Biome Color", Color) = (1,1,1,0)
        _SkyLight ("Sky Light", Integer) = 16
        _DayProgress("Day Progress", Range(0,1)) = 0
        _LavaTex ("Lava Strip Texture", 2D) = "white" {}

        // MINECRAFT-STYLE RADIAL FOG PROPERTIES
        _FogColor ("Fog Color", Color) = (0.5, 0.6, 0.7, 1.0)
        _FogDensity ("Fog Density", Range(0.0, 0.1)) = 0.02
        _FogStart ("Fog Start Distance", Float) = 32.0
        _FogEnd ("Fog End Distance", Float) = 128.0
        _FogMode ("Fog Mode", Range(0, 2)) = 0
        [HideInInspector] _UseGpuExactAo ("Use GPU Exact AO", Float) = 0
    }
    SubShader
    {
        Tags {"RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100

        Cull Off

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : NORMAL;
                float4 color : COLOR;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                UNITY_FOG_COORDS(2)
                float4 vertex : SV_POSITION;
                float3 normal: TEXCOORD1;
                fixed4 color : COLOR;
                float3 worldPos : TEXCOORD3;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            int _SkyLight;
            half _DayProgress;

            fixed4 _BiomeColor;

            // MINECRAFT-STYLE RADIAL FOG
            fixed4 _FogColor;
            half _FogDensity;
            half _FogStart;
            half _FogEnd;
            half _FogMode;
            float _UseGpuExactAo;
            sampler2D _LavaTex;

            // GPU LIGHT ATLAS GLOBALS
            sampler2D _UdonVRCM_GpuLightAtlas;
            sampler2D _UdonVRCM_GpuBlockAtlas;
            sampler2D _UdonVRCM_GpuSlotLookup;
            sampler2D _UdonVRCM_GpuBlockProps;
            float4 _UdonVRCM_GpuAtlasInfo;
            float4 _UdonVRCM_GpuWorldInfo;
            float4 _UdonVRCM_GpuChunkInfo;
            float4 _UdonVRCM_GpuVoxelOffset;
            float _UdonVRCM_GpuEnabled;
            float _UdonVRCM_SkylightSub; // DAY/NIGHT: 0-11, subtracted from SKY light at sample time

            #include "MCTerrainGpuExactAo.cginc"

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.normal = v.normal;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                UNITY_TRANSFER_FOG(o,o.vertex);
                return o;
            }

            float2 round(float2 uv)
            {
                return float2(  (uv.x / 0.0625) * 0.0625, (uv.y / 0.0625) * 0.0625);
            }

            fixed calcBrightness(fixed3 normal)
            {
                fixed brightness;
                if (normal.y > 0.5)
                {
                    brightness = 1.0;
                }
                else if (normal.y < -0.5)
                {
                    brightness = 0.5;
                }
                else if (normal.x > 0.5 || normal.x < -0.5)
                {
                    brightness = 0.6;
                }
                else if (normal.z > 0.5 || normal.z < -0.5)
                {
                    brightness = 0.8;
                }
                else
                {
                    brightness = 1.0;
                }

                return brightness;
            }

            half calcMinecraftFog(float3 worldPos)
            {
                float3 viewDir = worldPos - _WorldSpaceCameraPos;
                half distance = length(viewDir);
                half fogFactor = 1.0;
                if (_FogMode < 0.5)
                {
                    fogFactor = saturate((_FogEnd - distance) / (_FogEnd - _FogStart));
                }
                else if (_FogMode < 1.5)
                {
                    fogFactor = exp(-_FogDensity * distance);
                }
                else
                {
                    fogFactor = exp(-_FogDensity * _FogDensity * distance * distance);
                }
                return saturate(fogFactor);
            }

            half calcBetaLightBrightnessFromLevel(float lightLevel)
            {
                float darkness = 1.0 - saturate(lightLevel / 15.0);
                return (1.0 - darkness) / (darkness * 3.0 + 1.0) * 0.95 + 0.05;
            }

            half sampleGpuLightBrightnessAtPosition(float3 samplePos)
            {
                float coordinateBias = 0.0001;
                float globalX = floor(samplePos.x + coordinateBias) + _UdonVRCM_GpuVoxelOffset.x;
                float globalY = floor(samplePos.y + coordinateBias) + _UdonVRCM_GpuVoxelOffset.y;
                float globalZ = floor(samplePos.z + coordinateBias) + _UdonVRCM_GpuVoxelOffset.z;

                float maxWorldX = _UdonVRCM_GpuWorldInfo.x * _UdonVRCM_GpuChunkInfo.x;
                float maxWorldY = _UdonVRCM_GpuWorldInfo.y * _UdonVRCM_GpuChunkInfo.y;
                float maxWorldZ = _UdonVRCM_GpuWorldInfo.z * _UdonVRCM_GpuChunkInfo.x;
                if (globalX < 0.0 || globalY < 0.0 || globalZ < 0.0) return -1.0;
                if (globalX >= maxWorldX || globalY >= maxWorldY || globalZ >= maxWorldZ) return -1.0;

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
                float4 slotData = tex2D(_UdonVRCM_GpuSlotLookup, lookupUv);
                if (slotData.a < 0.5) return -1.0;

                float slotLow = floor(slotData.r * 255.0 + 0.5);
                float slotHigh = floor(slotData.g * 255.0 + 0.5);
                float slotIndex = slotLow + slotHigh * 256.0;
                float tileX = fmod(slotIndex, _UdonVRCM_GpuAtlasInfo.z);
                float tileY = floor(slotIndex / _UdonVRCM_GpuAtlasInfo.z);
                float atlasU = (tileX * _UdonVRCM_GpuChunkInfo.x + localX + 0.5) / _UdonVRCM_GpuAtlasInfo.x;
                float atlasV = (tileY * (_UdonVRCM_GpuChunkInfo.y * _UdonVRCM_GpuChunkInfo.x) + localY * _UdonVRCM_GpuChunkInfo.x + localZ + 0.5) / _UdonVRCM_GpuAtlasInfo.y;
                float4 lightSample = tex2D(_UdonVRCM_GpuLightAtlas, float2(atlasU, atlasV));
                if (lightSample.a < 0.5) return -1.0;
                float lightLevel = floor(max(lightSample.r, lightSample.g) * 15.0 + 0.5);
                return calcBetaLightBrightnessFromLevel(lightLevel);
            }

            half sampleGpuLightBrightness(float3 worldPos, float3 faceNormal)
            {
                if (_UdonVRCM_GpuEnabled < 0.5) return -1.0;
                if (max(max(abs(faceNormal.x), abs(faceNormal.y)), abs(faceNormal.z)) < 0.9)
                {
                    return sampleGpuLightBrightnessAtPosition(worldPos);
                }

                return sampleGpuLightBrightnessAtPosition(worldPos + normalize(faceNormal) * 0.501);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                bool isWaterOrLava = (i.color.b < 0.01);
                bool isLava = (i.color.g < 0.01 && isWaterOrLava);
                fixed4 col;

                if (isLava)
                {
                    // Lava: sample from animated strip, 20fps, 32 frames
                    float frameIndex = floor(fmod(_Time.y * 20.0, 32.0));
                    float frameOffset = frameIndex / 32.0;
                    float2 lavaUV = float2(i.uv.x, frac(i.uv.y) / 32.0 + frameOffset);
                    col = tex2D(_LavaTex, lavaUV);
                    // Fallback: if texture not assigned (near-white from default), use MC lava orange
                    if (col.r > 0.99 && col.g > 0.99 && col.b > 0.99)
                        col = fixed4(1.0, 0.42, 0.0, 1.0);
                    col.a = 1.0;

                    // Lava emits its own light (lightValue=15 in MC), so it's always fully bright
                    half lavaLight = max(0.02, i.color.a);
                    half lavaFace = 1.0;
                    half lavaBrightness = GammaToLinearSpace((lavaLight * lavaFace).xxx).x;
                    col.rgb *= lavaBrightness;

                    half fogFactor = calcMinecraftFog(i.worldPos);
                    col.rgb = lerp(_FogColor.rgb, col.rgb, fogFactor);
                    UNITY_APPLY_FOG(i.fogCoord, col);
                    return col;
                }

                if (isWaterOrLava)
                {
                    // Water path
                    col = tex2D(_MainTex, round(i.uv)) * half4(_BiomeColor.rgb, 1);

                    half lightBrightness = max(0.02, i.color.a);
                    // MC uses 1.0 for all water faces regardless of normal direction.
                    // The per-face multipliers (0.5 bottom, 0.8 N/S, 0.6 E/W) are baked
                    // into the CPU brightness via _GetWaterBlockBrightness sampling the neighbor.
                    half combinedBrightness = GammaToLinearSpace(lightBrightness.xxx).x;
                    col.rgb *= combinedBrightness;

                    half fogFactor = calcMinecraftFog(i.worldPos);
                    col.rgb = lerp(_FogColor.rgb, col.rgb, fogFactor);
                    UNITY_APPLY_FOG(i.fogCoord, col);
                    return col;
                }

                // Non-fluid transparent block (e.g. glass, ice)
                col = tex2D(_MainTex, round(i.uv)) * half4(_BiomeColor.rgb, 1);

                half minLightLevel = 0.02;
                half lightBrightness;
                if (_UseGpuExactAo > 0.5)
                {
                    half aoBrightness = gpuVoxelComputeExactAoBrightness(i.worldPos, i.normal);
                    lightBrightness = max(minLightLevel, aoBrightness >= 0.0 ? aoBrightness : i.color.a);
                }
                else
                {
                    half gpuLightBrightness = gpuVoxelSampleLightBrightness(i.worldPos, i.normal);
                    if (gpuLightBrightness >= 0.0)
                    {
                        lightBrightness = max(minLightLevel, gpuLightBrightness);
                    }
                    else
                    {
                        lightBrightness = max(minLightLevel, i.color.a);
                    }
                }

                half faceBrightness = calcBrightness(i.normal);
                half combinedBrightness = lightBrightness * faceBrightness;
                combinedBrightness = GammaToLinearSpace(combinedBrightness.xxx).x;
                col.rgb *= combinedBrightness;

                half fogFactor = calcMinecraftFog(i.worldPos);
                col.rgb = lerp(_FogColor.rgb, col.rgb, fogFactor);

                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
}
