Shader "Unlit/MCTerrain (Transparent)"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _BiomeColor ("Biome Color", Color) = (1,1,1,0)
        _SkyLight ("Sky Light", Integer) = 16
        _DayProgress("Day Progress", Range(0,1)) = 0
    }
    SubShader
    {
        Tags {"RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100

        Cull Off
        
        

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // make fog work
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                UNITY_FOG_COORDS(2)
                float4 vertex : SV_POSITION;
                float3 normal: TEXCOORD1;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            int _SkyLight;
            half _DayProgress;

            fixed4 _BiomeColor;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.normal = v.normal;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
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
                if (normal.y > 0.5) // Top face
                {
                    brightness = 1.0;
                }
                else if (normal.y < -0.5) // Bottom face
                {
                    brightness = 0.2;
                }
                else if (normal.x > 0.5 || normal.x < -0.5) // Left and right faces
                {
                    brightness = 0.6;
                }
                else if (normal.z > 0.5 || normal.z < -0.5) // Front and back faces
                {
                    brightness = 0.4;
                }

                return brightness;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Snap the UVs to a 0.0625 grid
                //float2 snappedUV = floor(i.uv / 0.0625) * 0.0625;
                // sample the texture
                //fixed4 col = tex2D(_MainTex, snappedUV) * _BiomeColor;
                fixed4 col = tex2D(_MainTex, round(i.uv)) * half4(_BiomeColor.rgb,1);
                half minLightLevel = 0.02;
                float dayNightTransition;
                if (_DayProgress < 0.0417) { // 0 to 1000 ticks
                    dayNightTransition = (_DayProgress / 0.0417);
                } else if (_DayProgress > 0.5 && _DayProgress < 0.5417) { // 12000 to 13000 ticks
                    dayNightTransition = (1 - ((_DayProgress - 0.5) / 0.0417));
                } else if (_DayProgress <= 0.5) {
                    dayNightTransition = 1;
                } else {
                    dayNightTransition = 0;
                }
                dayNightTransition = dayNightTransition;
                col.rgb *= max(minLightLevel,(float)((_SkyLight+1)*dayNightTransition)/16);
                col.rgb *= calcBrightness(i.normal);
                // apply fog
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
}
