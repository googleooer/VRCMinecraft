// Dedicated fluid (water + lava) shader for the per-chunk transparent mesh (terrain_trans.mat).
// b1.7.3 renders the translucent pass with depth-writes OFF and the water faces PRE-SORTED
// back-to-front per chunk (RenderGlobal.sortAndRender re-sorts on movement) so they alpha-blend
// in the correct far->near order. We can't cheaply re-sort mesh triangles per frame in Unity, so
// the order-INDEPENDENT equivalent is a DEPTH PRIME: prime depth to the nearest water surface,
// then blend it exactly once (no double-blend of overlapping faces, which is what made the single
// ZWrite pass look bad). LAVA has no transparency in Minecraft -> a fully OPAQUE pass (ZWrite on)
// so it sorts and occludes correctly. Split by the vertex-color type flag (water 1,1,0 /
// still-lava 0,0,0 / flowing-lava .5,0,0). Kept isolated from MCTerrain.shader (shared by the
// live opaque+cutout terrain) so opaque/cutout are unaffected. Property names match what McWorld
// pushes by name (fog, water slices, lava strip, light-source, _UdonVRCM_* globals).
Shader "VRCM/MCFluid"
{
    Properties
    {
        _MainTex ("Texture Array", 2DArray) = "white" {}
        _TintMask ("Tint Mask Array", 2DArray) = "black" {}
        [HideInInspector] _WaterStillTex ("Water Still", 2D) = "white" {}
        [HideInInspector] _WaterFlowTex ("Water Flow", 2D) = "white" {}
        [HideInInspector] _WaterStillSlice ("Water Still Slice", Float) = -1
        [HideInInspector] _WaterFlowSlice ("Water Flow Slice", Float) = -1
        _LavaTex ("Lava Strip", 2D) = "white" {}
        _UseVertexLight ("Use Vertex Light", Float) = 1
        [HideInInspector] _UseGpuExactAo ("Use GPU Exact AO", Float) = 0
        [HideInInspector] _UseBakedAo ("Use Baked AO", Float) = 0
        _AoFullDistance ("AO Full Distance", Float) = 128
        _FogColor ("Fog Color", Color) = (0.5,0.6,0.7,1)
        _FogDensity ("Fog Density", Range(0,0.1)) = 0.02
        _FogStart ("Fog Start", Float) = 32
        _FogEnd ("Fog End", Float) = 128
        _FogMode ("Fog Mode", Range(0,2)) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100

        CGINCLUDE
        #pragma target 3.0
        #include "UnityCG.cginc"

        struct appdata { float4 vertex : POSITION; float3 uvw : TEXCOORD0; float3 normal : NORMAL; float4 color : COLOR; };
        struct v2f { float3 uvw : TEXCOORD0; float4 vertex : SV_POSITION; float3 normal : TEXCOORD1; fixed4 color : COLOR; float3 worldPos : TEXCOORD3; };

        UNITY_DECLARE_TEX2DARRAY(_MainTex);
        float4 _MainTex_ST;
        UNITY_DECLARE_TEX2DARRAY(_TintMask);
        sampler2D _WaterStillTex, _WaterFlowTex;
        float _WaterStillSlice, _WaterFlowSlice;
        sampler2D _LavaTex;
        float _UseVertexLight, _UseGpuExactAo, _UseBakedAo, _AoFullDistance;
        fixed4 _FogColor;
        half _FogDensity, _FogStart, _FogEnd, _FogMode;
        // GPU light atlas globals (declared before the include, which references but does not declare them).
        sampler2D _UdonVRCM_GpuLightAtlas;
        sampler2D _UdonVRCM_GpuBlockAtlas;
        sampler2D _UdonVRCM_GpuSlotLookup;
        sampler2D _UdonVRCM_GpuBlockProps;
        float4 _UdonVRCM_GpuAtlasInfo, _UdonVRCM_GpuWorldInfo, _UdonVRCM_GpuChunkInfo, _UdonVRCM_GpuVoxelOffset;
        float _UdonVRCM_GpuEnabled, _UdonVRCM_SkylightSub;

        #include "MCTerrainGpuExactAo.cginc"

        v2f vert(appdata v)
        {
            v2f o;
            o.vertex = UnityObjectToClipPos(v.vertex);
            o.normal = v.normal;
            o.uvw.xy = TRANSFORM_TEX(v.uvw.xy, _MainTex);
            o.uvw.z = v.uvw.z;
            o.color = v.color;
            o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
            return o;
        }

        bool fluidIsLava(v2f i) { return (i.color.g < 0.01 && i.color.b < 0.01); }

        half calcMinecraftFog(float3 worldPos)
        {
            half d = length(worldPos - _WorldSpaceCameraPos);
            half f = 1.0;
            if (_FogMode < 0.5)      f = saturate((_FogEnd - d) / max(0.001, _FogEnd - _FogStart));
            else if (_FogMode < 1.5) f = saturate(exp(-_FogDensity * d));
            else                     f = saturate(exp(-_FogDensity * _FogDensity * d * d));
            return f;
        }

        fixed calcFaceBrightness(fixed3 n)
        {
            fixed m = max(max(abs(n.x), abs(n.y)), abs(n.z));
            if (m < 0.9) return 1.0;
            if (n.y > 0.5) return 1.0;
            if (n.y < -0.5) return 0.5;
            if (abs(n.x) > abs(n.z)) return 0.6;
            return 0.8;
        }

        // Full b1.7.3 fluid fragment color (water + lava), ported from MCTerrain.shader:190-335.
        fixed4 fluidColor(v2f i)
        {
            fixed4 col;
            bool isLava = (i.color.g < 0.01 && i.color.b < 0.01);
            bool isWater = (!isLava && i.color.b < 0.01);

            if (isLava)
            {
                bool isFlowingLava = (i.color.r > 0.25);
                float2 lavaUV;
                if (isFlowingLava)
                {
                    // MC TextureLavaFlowFX scrolls the tiling pattern DOWN by a whole 16px ROW every
                    // 3 ticks — a DISCRETE PER-PIXEL step, NOT a smooth slide (line 57:
                    // field[var2 - tickCounter/3*16 & 255], where /3*16 = one row per 3 ticks). Each
                    // baked lava_strip frame tiles vertically (CA wraps &15), so stepping the sample
                    // by whole rows within one frame loops seamlessly. Quantize to 1/16 (one row)
                    // increments, stepping every 3 ticks = 0.15s @20 TPS.
                    float rows = floor(_Time.y / 0.15);   // 1 row per 3 ticks (20 TPS)
                    float vOffset = fmod(rows, 16.0) / 16.0; // wrap every 16 rows (keeps precision)
                    lavaUV = float2(frac(i.uvw.x), frac(i.uvw.y - vOffset) / 64.0);
                }
                else
                {
                    // Still lava: bubble by cycling the 64 CA frames (TextureLavaFX), no scroll.
                    float frameIndex = floor(fmod(_Time.y * 10.0, 64.0));
                    lavaUV = float2(frac(i.uvw.x), frac(i.uvw.y) / 64.0 + frameIndex / 64.0);
                }
                col = tex2D(_LavaTex, lavaUV);
                if (col.r > 0.99 && col.g > 0.99 && col.b > 0.99) col = fixed4(1.0, 0.42, 0.0, 1.0);
                col.a = 1.0;
            }
            else
            {
                col = UNITY_SAMPLE_TEX2DARRAY(_MainTex, i.uvw).rgba;
            }

            fixed4 tintInput = fixed4(0,0,0,0);
            fixed waterWeight = 0;
            if (!isLava)
            {
                tintInput = UNITY_SAMPLE_TEX2DARRAY(_TintMask, i.uvw);
                fixed4 waterStill = tex2D(_WaterStillTex, i.uvw.xy).rgba;
                fixed4 waterFlow = tex2D(_WaterFlowTex, i.uvw.xy).rgba;
                fixed stillWeight = saturate(1.0 - abs(i.uvw.z - _WaterStillSlice) * 4.0);
                fixed flowWeight = saturate(1.0 - abs(i.uvw.z - _WaterFlowSlice) * 4.0) * (1.0 - stillWeight);
                waterWeight = saturate(stillWeight + flowWeight);
                fixed4 waterCol = lerp(waterFlow, waterStill, stillWeight);
                col = lerp(col, waterCol, waterWeight);
                fixed3 tintedColor = col.rgb * i.color.rgb;
                col.rgb = lerp(col.rgb, tintedColor, tintInput.a * (1.0 - waterWeight));
            }

            half minLightLevel = 0.02;
            half lightBrightness;
            if (isWater || isLava)
            {
                // MC max(lightAt(self), lightAt(above)) — avoids fluid self-darkening from opacity.
                half selfLight = gpuVoxelSampleLightBrightnessAtPosition(i.worldPos);
                half aboveLight = gpuVoxelSampleLightBrightnessAtPosition(i.worldPos + float3(0, 1, 0));
                half fluidLight = max(selfLight, aboveLight);
                if (fluidLight < 0.0) fluidLight = _UdonVRCM_GpuEnabled >= 0.5 ? (isLava ? 1.0 : gpuVoxelAtlasMissBrightness()) : i.color.a;
                lightBrightness = max(minLightLevel, fluidLight);
            }
            else
            {
                // Generic transparent cube (glass/ice) — only reachable on the CPU fallback mesher.
                half g = gpuVoxelSampleLightBrightness(i.worldPos, i.normal);
                lightBrightness = max(minLightLevel, g >= 0.0 ? g : (_UdonVRCM_GpuEnabled >= 0.5 ? gpuVoxelAtlasMissBrightness() : i.color.a));
            }

            // b1.7.3 renderBlockFluids applies the SAME directional face shading to fluids as to
            // solid blocks: top 1.0, bottom 0.5, N/S (Z) 0.8, E/W (X) 0.6 (var14-17, lines 1485-88 /
            // 1601-05). The old "fluids = 1.0" override made water/lava sides read too bright.
            half faceBrightness = calcFaceBrightness(i.normal);
            half combined = lightBrightness * faceBrightness;
            combined = GammaToLinearSpace(combined.xxx).x;
            col.rgb *= combined;

            half fogFactor = calcMinecraftFog(i.worldPos);
            col.rgb = lerp(_FogColor.rgb, col.rgb, fogFactor);
            return col;
        }
        ENDCG

        // Pass 1 — LAVA, fully OPAQUE (ZWrite on, no blend). Cull Off: lava is opaque, so ZWrite +
        // ZTest LEqual already depth-occlude the back faces of each block by its own front faces
        // (nearer depth always wins under LEqual regardless of draw order) — no manual cull needed.
        // The previous `dot(normal, viewDir) < 0` reject was a screen/grazing-angle hack that wrongly
        // discarded top faces near the horizon (upper screen), so lava surfaces vanished when looked
        // up at. Removed.
        Pass
        {
            Cull Off ZWrite On ZTest LEqual
            Offset -0.5, -0.5
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragLava
            fixed4 fragLava(v2f i) : SV_Target
            {
                if (!fluidIsLava(i)) discard;
                fixed4 c = fluidColor(i);
                c.a = 1.0;
                return c;
            }
            ENDCG
        }

        // Pass 2 — WATER DEPTH PRIME: write only depth (nearest water/transparent surface).
        Pass
        {
            Cull Off ZWrite On ZTest LEqual ColorMask 0
            Offset -0.5, -0.5
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragPrime
            fixed4 fragPrime(v2f i) : SV_Target
            {
                if (fluidIsLava(i)) discard; // lava is opaque in pass 1, not part of the water prime
                return 0;
            }
            ENDCG
        }

        // Pass 3 — WATER COLOR: blend the front-most surface once (ZTest LEqual matches the prime).
        Pass
        {
            Cull Off ZWrite Off ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha
            Offset -0.5, -0.5
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragWater
            fixed4 fragWater(v2f i) : SV_Target
            {
                if (fluidIsLava(i)) discard;
                return fluidColor(i);
            }
            ENDCG
        }
    }
}
