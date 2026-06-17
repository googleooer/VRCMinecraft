// GPU OFFLOAD #3: GPU biome color baking.
//
// Replaces the CPU per-chunk loop in McWorld._PreComputeBiomeColors that samples
// the grass color texture for each of 256 (x,z) columns. One Blit per chunk:
//   input:  per-chunk 16x16 temperature texture + 16x16 rainfall texture
//           + 256x256 grass color LUT (and optionally foliage + water)
//   output: 16x16 RGBA color texture with the grass-tint per column
//
// The Beta tint formula (BetaBiome.GetGrassColor) is:
//   temperature  = clamp(temp, 0..1)
//   adjustedTemp = temp - rainfall * 0.5  (rough; see BetaBiome.cs for exact)
//   then sample grassColorTex at (1 - temp, 1 - adjustedTemp).
// We replicate that lookup in HLSL so the output is bit-equivalent (within the
// LUT texture's sampling precision).
Shader "VRCM/GpuBiomeColorBake"
{
    Properties
    {
        // Combined climate texture: temperature in .r, rainfall in .g (matches
        // gpuClimateTexture layout used everywhere else in the GPU pipeline).
        _ClimateTex  ("Climate Tex (16x16)",     2D) = "white" {}
        _GrassLUT    ("Grass Color LUT",         2D) = "white" {}
        _FoliageLUT  ("Foliage Color LUT",       2D) = "white" {}
        _WaterLUT    ("Water Color LUT",         2D) = "white" {}
        _ChunkSizeXZ ("Chunk Size XZ",           Int) = 16
        // 0 = grass tint, 1 = foliage tint, 2 = water tint.
        // Most chunks bake grass only; we run additional passes if foliage/water are
        // needed for the mesh build path.
        _TintMode    ("Tint Mode (0=grass,1=foliage,2=water)", Int) = 0
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

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

            sampler2D _ClimateTex;
            sampler2D _GrassLUT;
            sampler2D _FoliageLUT;
            sampler2D _WaterLUT;
            int _ChunkSizeXZ;
            int _TintMode;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Output is a chunkSizeXZ × chunkSizeXZ RT. Each pixel is one column.
                int x = clamp((int)floor(i.vertex.x), 0, _ChunkSizeXZ - 1);
                int z = clamp((int)floor(i.vertex.y), 0, _ChunkSizeXZ - 1);

                float2 colUV = float2((x + 0.5) / (float)_ChunkSizeXZ, (z + 0.5) / (float)_ChunkSizeXZ);
                float4 climate = tex2Dlod(_ClimateTex, float4(colUV, 0, 0));
                float temperature = climate.r;
                float rainfall    = climate.g;

                // Clamp inputs to [0,1] like the CPU side does.
                temperature = saturate(temperature);
                rainfall    = saturate(rainfall);
                rainfall   *= temperature;        // matches CPU formula in BetaBiome

                // Look up grass color: u = 1 - temperature, v = 1 - rainfall.
                // (See BetaBiome.GetGrassColor in C#.)
                float2 lutUV = float2(1.0 - temperature, 1.0 - rainfall);
                float4 tint;
                if (_TintMode == 0)       tint = tex2Dlod(_GrassLUT,   float4(lutUV, 0, 0));
                else if (_TintMode == 1)  tint = tex2Dlod(_FoliageLUT, float4(lutUV, 0, 0));
                else                      tint = tex2Dlod(_WaterLUT,   float4(lutUV, 0, 0));

                tint.a = 1.0;
                return tint;
            }
            ENDCG
        }
    }
}
