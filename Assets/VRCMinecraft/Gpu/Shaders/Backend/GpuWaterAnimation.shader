Shader "VRCM/GpuWaterAnimation"
{
    Properties
    {
        _MainTex ("State (R=height G=accel B=splash)", 2D) = "black" {}
        _Time2 ("Time", Float) = 0
        _SplashChance ("Splash Chance", Float) = 0.05
        _SplashDecay ("Splash Decay", Float) = 0.1
        _DivisorInv ("1 / Average Divisor", Float) = 0.30303
        _FlowScroll ("Flow Scroll (px)", Float) = 0
        _IsFlowMode ("Flow Mode (0=still, 1=flow)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        // Pass 0: Evolve simulation state
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
            float4 _MainTex_TexelSize;
            float _Time2;
            float _SplashChance;
            float _SplashDecay;
            float _DivisorInv;
            float _IsFlowMode;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // Hash-based pseudo-random noise, range [0,1)
            float hashNoise(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 texel = _MainTex_TexelSize.xy;
                float4 self = tex2D(_MainTex, i.uv);
                float height = self.r;
                float accel = self.g;
                float splash = self.b;

                // Neighbor height averaging
                float neighborSum;
                if (_IsFlowMode > 0.5)
                {
                    // Flow: average 3 vertical neighbors (y-2 to y), matching CPU code
                    float h0 = tex2D(_MainTex, i.uv + float2(0, -2.0 * texel.y)).r;
                    float h1 = tex2D(_MainTex, i.uv + float2(0, -1.0 * texel.y)).r;
                    neighborSum = (h0 + h1 + height) * _DivisorInv;
                }
                else
                {
                    // Still: average 3 horizontal neighbors (x-1, x, x+1), matching CPU code
                    float hW = tex2D(_MainTex, i.uv + float2(-texel.x, 0)).r;
                    float hE = tex2D(_MainTex, i.uv + float2(texel.x, 0)).r;
                    neighborSum = (hW + height + hE) * _DivisorInv;
                }

                float newHeight = neighborSum + accel * 0.8;

                // Update acceleration
                accel += splash * 0.05;
                accel = max(accel, 0.0);

                // Update splash (decay + random injection)
                splash -= _SplashDecay;
                float noise = hashNoise(i.uv * 256.0 + _Time2);
                if (noise < _SplashChance)
                {
                    splash = 0.5;
                }

                return float4(newHeight, accel, splash, 1.0);
            }
            ENDCG
        }

        // Pass 1: Convert state → Minecraft Beta water color
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
            float _FlowScroll;
            float _IsFlowMode;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 sampleUv = i.uv;

                // Flow mode applies vertical scroll
                if (_IsFlowMode > 0.5)
                {
                    sampleUv.y = frac(sampleUv.y - _FlowScroll);
                }

                float height = tex2D(_MainTex, sampleUv).r;
                height = saturate(height);
                float heightSq = height * height;

                // Minecraft Beta 1.7.3 water color formula (byte values)
                float r = (32.0 + heightSq * 32.0) / 255.0;
                float g = (50.0 + heightSq * 64.0) / 255.0;
                float b = 1.0;
                float a = (146.0 + heightSq * 50.0) / 255.0;

                // The old CPU code wrote raw bytes to an sRGB Texture2D.
                // Unity's linear pipeline gamma-decodes sRGB textures on sample,
                // producing much darker values. Apply the same decode to RGB only.
                // Alpha is transparency, not color — pass through as raw byte fraction.
                r = GammaToLinearSpaceExact(r);
                g = GammaToLinearSpaceExact(g);
                b = GammaToLinearSpaceExact(b);

                return fixed4(r, g, b, a);
            }
            ENDCG
        }
    }
}
