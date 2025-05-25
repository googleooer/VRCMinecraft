// Made with Amplify Shader Editor
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "Minecraft Sky (With Stars)"
{
    Properties
    {
        [Header(Sky)]
        _SkyColorDay ("Sky Color Day", Color) = (0.466, 0.686, 1, 1)
        _SkyColorNight ("Sky Color Night", Color) = (0.1, 0.1, 0.2, 1)
        _WorldBottomColor ("World Bottom Color", Color) = (0.137, 0.168, 0.698, 1)
        _WorldBottomColorNight ("World Bottom Color Night", Color) = (0.137, 0.168, 0.698, 1)
        _FogColorDay ("Fog Color Day", Color) = (0.137, 0.168, 0.698, 1)
        _FogColorNight ("Fog Color Night", Color) = (0.137, 0.168, 0.698, 1)
        [Header(Gradient)]
        _SkyRatio ("Sky Ratio", Range(0, 1)) = 0.5
        _FogRatio ("Fog Ratio", Range(0, 1)) = 0.3
        _BottomRatio ("Bottom Ratio", Range(0, 1)) = 0.2
        [Header(Sun and Moon)]
        _SunTex ("Sun", 2D) = "white" {}
        _MoonTex ("Sun", 2D) = "white" {}
        _SunMoonSize ("Sun and Moon Size", Range(0.01, 1)) = 0.2
        [Header(Stars)]
        _StarCount ("Star Count", Int) = 1500
        _StarSize ("Star Size", Range(0.001, 0.01)) = 0.005
        _StarBrightness ("Star Brightness", Range(0.1, 1.0)) = 0.5
        _StarFadeRange ("Star Fade Range", Range(0, 1)) = 0.2
        [Header(Internal)]
        _DayProgress("Day Progress", Range(0,1)) = 0
    }
    SubShader
    {
        Tags {"Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox"}
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 texcoord : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _SunTex, _MoonTex;
            half _SunMoonSize;
            half4 _SkyColorDay;
            half4 _SkyColorNight;
            half4 _WorldBottomColor;
            half4 _WorldBottomColorNight;
            half4 _FogColorDay, _FogColorNight;
            float _SkyRatio;
            float _FogRatio;
            float _BottomRatio;
            int _StarCount;
            half _StarSize;
            half _StarBrightness;
            half _StarFadeRange;
            half _DayProgress;

            float3 RotateAroundZInDegrees(float3 vertex, float degrees)
            {
                float alpha = degrees * UNITY_PI / 180.0;
                float sina, cosa;
                sincos(alpha, sina, cosa);
                float2x2 m = float2x2(cosa, -sina, sina, cosa);
                return float3(mul(m, vertex.yx), vertex.z).xyz;
            }

            float3 RotateAroundYInDegrees (float3 vertex, float degrees)
            {
                float alpha = degrees * UNITY_PI / 180.0;
                float sina, cosa;
                sincos(alpha, sina, cosa);
                float2x2 m = float2x2(cosa, -sina, sina, cosa);
                return float3(mul(m, vertex.xz), vertex.y).xzy;
            }

            v2f vert (appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.vertex.xyz;
                return o;
            }

            float3 hash3(float3 p)
            {
                p = float3(dot(p,float3(127.1,311.7,74.7)),
                           dot(p,float3(269.5,183.3,246.1)),
                           dot(p,float3(113.5,271.9,124.6)));
                return frac(sin(p)*43758.5453123);
            }

            float2x2 rot2D(float angle)
            {
                float s = sin(angle), c = cos(angle);
                return float2x2(c, -s, s, c);
            }

            float4 frag (v2f i) : SV_Target
            {
                float3 dir = normalize(i.texcoord);
                
                // Vertical gradient calculation
                float t = dir.y * 0.5 + 0.5;

                float totalRatio = _SkyRatio + _FogRatio + _BottomRatio;
                _SkyRatio /= totalRatio;
                _FogRatio /= totalRatio;
                _BottomRatio /= totalRatio;

                half4 gradientColor;
                if (t < _BottomRatio) {
                    gradientColor = lerp(_WorldBottomColor, _FogColorDay, t / _BottomRatio);
                } else if (t < _BottomRatio + _FogRatio) {
                    float tAdjusted = (t - _BottomRatio) / _FogRatio;
                    gradientColor = lerp(_FogColorDay, _SkyColorDay, tAdjusted);
                } else {
                    gradientColor = _SkyColorDay;
                }

                half4 gradientColorNight;
                if (t < _BottomRatio) {
                    gradientColorNight = lerp(_WorldBottomColorNight, _FogColorNight, t / _BottomRatio);
                } else if (t < _BottomRatio + _FogRatio) {
                    float tAdjusted = (t - _BottomRatio) / _FogRatio;
                    gradientColorNight = lerp(_FogColorNight, _SkyColorNight, tAdjusted);
                } else {
                    gradientColorNight = _SkyColorNight;
                }

                // Day/Night sky color transition
                half dayNightTransition;
                if (_DayProgress < 0.0417) { // 0 to 1000 ticks
                    dayNightTransition = _DayProgress / 0.0417;
                } else if (_DayProgress > 0.5 && _DayProgress < 0.5417) { // 12000 to 13000 ticks
                    dayNightTransition = 1 - ((_DayProgress - 0.5) / 0.0417);
                } else if (_DayProgress <= 0.5) {
                    dayNightTransition = 1;
                } else {
                    dayNightTransition = 0;
                }
                
                half4 col = lerp(gradientColorNight, gradientColor, dayNightTransition);
                
                // Star visibility calculation
                float starVisibility = 0;
                if (_DayProgress < 0.0177) { // 425 ticks
                    starVisibility = 1 - (_DayProgress / 0.0177);
                } else if (_DayProgress > 0.4823) { // 11576 ticks
                    starVisibility = (_DayProgress - 0.4823) / (1 - 0.4823);
                }
                starVisibility = saturate(starVisibility);
                
                // Rotate the direction for star placement
                float3 rotatedDir = RotateAroundZInDegrees(dir, _DayProgress * 360);
                
                // Star rendering
                /*for(int j = 0; j < _StarCount; j++)
                {
                    float3 starSeed = float3(j, j * 1.5, j * 2.3);
                    float3 starPos = normalize(hash3(starSeed) * 2.0 - 1.0);
                    
                    // Skip stars below the equator
                    //if (starPos.y < 0) continue;
                    
                    float3 starDir = rotatedDir - starPos;
                    
                    float starVariation = hash3(starSeed).x;
                    float starSizeVar = _StarSize * (0.5 + 0.5 * starVariation);
                    float starBrightnessVar = _StarBrightness * (0.5 + 0.5 * starVariation) * starVisibility;
                    
                    // Apply fade-out near the equator
                    float equatorFade = saturate(((RotateAroundZInDegrees(dir,0) - _StarFadeRange) / (1 - _StarFadeRange))+0.5);
                    starBrightnessVar *= equatorFade;
                    
                    // Random rotation
                    float starAngle = hash3(starSeed).y * 6.283185; // 2 * PI
                    float2x2 starRotation = rot2D(starAngle);
                    
                    // Project starDir onto the plane perpendicular to starPos
                    float3 u = normalize(cross(starPos, float3(0,1,0)));
                    float3 v = cross(starPos, u);
                    float2 starPlaneDir = float2(dot(starDir, u), dot(starDir, v));
                    
                    // Apply rotation
                    starPlaneDir = mul(starRotation, starPlaneDir);
                    
                    // Check if inside square
                    if(abs(starPlaneDir.x) < starSizeVar && abs(starPlaneDir.y) < starSizeVar)
                    {
                        col.rgb += float3(starBrightnessVar, starBrightnessVar, starBrightnessVar);
                    }
                }*/
                //Disabled star rendering until I can optimize it.


                
                float3 rotatedDirSunMoon = RotateAroundZInDegrees(dir, (_DayProgress+0.25) * (360));

                // Sun and Moon rendering
                float3 sunDir = float3(0, 0, -1);
                float3 moonDir = float3(0, 0, 1);
                
                // Rotate sun and moon
                sunDir = RotateAroundYInDegrees(sunDir, 90);
                moonDir = RotateAroundZInDegrees(moonDir, -90);

                float3 rotatedRotatedDir = RotateAroundYInDegrees(rotatedDirSunMoon,90);
                
                float sunDot = dot(90, sunDir);
                float moonDot = dot(90, moonDir);

                float equatorFadeSunMoon = saturate(((rotatedDirSunMoon - _StarFadeRange) / (1 - _StarFadeRange))+0.5);
                
                if (sunDot > 0) 
                {
                    float2 sunUV = (rotatedRotatedDir.xy / rotatedRotatedDir.z * 0.5 / _SunMoonSize) + 0.5;
                    col += tex2D(_SunTex, sunUV) * (1 - equatorFadeSunMoon);
                }

                if (moonDot > 0)
                {
                
                    float2 moonUV = (rotatedRotatedDir.xy / rotatedRotatedDir.z * 0.5 / _SunMoonSize) + 0.5;
                    col += tex2D(_MoonTex, moonUV) * equatorFadeSunMoon;
                }
                
                return saturate(col);
            }
            ENDCG
        }
    }
}