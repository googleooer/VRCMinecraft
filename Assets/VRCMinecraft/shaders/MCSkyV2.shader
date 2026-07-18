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
        _FogStart("Fog Start", Range(0, 1)) = 0.2
		_FogSize("Fog Size", Range(0, 1)) = 0.1
		_TransitionSize("Transition Size", Range(0, 1)) = 0.1
        [Header(Sun and Moon)]
        _SunTex ("Sun", 2D) = "white" {}
        _MoonTex ("Moon", 2D) = "white" {}
        _SunSize ("Sun Size (tan half-angle, vanilla 0.3)", Range(0.01, 1)) = 0.3
        _MoonSize ("Moon Size (tan half-angle, vanilla 0.2)", Range(0.01, 1)) = 0.2
        [Header(Stars)]
        _StarTex ("Star Field (baked b1.7.3 equirect)", 2D) = "black" {}
        [Header(Internal)]
        _DayProgress("Day Progress", Range(0,1)) = 0
        _CelestialAngle("Celestial Angle (b1.7.3 eased)", Range(0,1)) = 0
        _StarBrightness("Star Brightness (script-driven)", Range(0,1)) = 0
        _SunriseColor("Sunrise Fan RGBA (script-driven)", Color) = (0,0,0,0)
        _FluidFogColor("Fluid Fog Color (script-driven)", Color) = (0,0,0,1)
        _FluidFogDensity("Fluid Fog Density (script-driven)", Float) = 0
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
            half _SunSize;
            half _MoonSize;
            half4 _SkyColorDay;
            half4 _SkyColorNight;
            half4 _WorldBottomColor;
            half4 _WorldBottomColorNight;
            half4 _FogColorDay, _FogColorNight;
            sampler2D _StarTex;
            half _StarBrightness;
            half4 _SunriseColor;
            half4 _FluidFogColor;
            half _FluidFogDensity;
            half _DayProgress;
            // float, not half: fp16 quantizes the *360 rotation into ~0.18-degree steps on
            // Quest — a third of a baked star's width, making the night sky lurch visibly.
            float _CelestialAngle;
			float _FogStart;
			float _FogSize;
			float _TransitionSize;

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

            float4 frag (v2f i) : SV_Target
            {
                float3 dir = normalize(i.texcoord);
                
                // Vertical gradient calculation
                float t = dir.y * 0.5 + 0.5;

				float bottom_to_fog_end = _FogStart;
				float pure_fog_end = _FogStart + _FogSize;
				float fog_to_sky_end = pure_fog_end + _TransitionSize;

				half4 gradientColor;
				if (t < bottom_to_fog_end) {
					float tAdjusted = smoothstep(bottom_to_fog_end - _TransitionSize, bottom_to_fog_end, t);
					gradientColor = lerp(_WorldBottomColor, _FogColorDay, tAdjusted);
				} else if (t < pure_fog_end) {
					gradientColor = _FogColorDay;
				} else if (t < fog_to_sky_end) {
					float tAdjusted = smoothstep(pure_fog_end, fog_to_sky_end, t);
					gradientColor = lerp(_FogColorDay, _SkyColorDay, tAdjusted);
				} else {
					gradientColor = _SkyColorDay;
				}

				half4 gradientColorNight;
				if (t < bottom_to_fog_end) {
					float tAdjusted = smoothstep(bottom_to_fog_end - _TransitionSize, bottom_to_fog_end, t);
					gradientColorNight = lerp(_WorldBottomColorNight, _FogColorNight, tAdjusted);
				} else if (t < pure_fog_end) {
					gradientColorNight = _FogColorNight;
				} else if (t < fog_to_sky_end) {
					float tAdjusted = smoothstep(pure_fog_end, fog_to_sky_end, t);
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

                // FLUID FOG (b1.7.3 renderSky draws the dome with GL_FOG enabled; underwater/
                // in-lava that fog is EXP per EntityRenderer.setupFog, so the sky itself drowns
                // in fog color). Approximate the dome-vertex distance vanilla fogs against: the
                // sky plane sits 16 above the eye and spans +-384, so distance = 16/|dir.y|,
                // capped near the horizon. Zero density (not in a fluid) is a no-op. The
                // sunrise fan, sun, moon and stars are drawn with GL_FOG *disabled* in vanilla,
                // so they composite AFTER this, unfogged.
                if (_FluidFogDensity > 0.0001)
                {
                    float domeDist = 16.0 / max(abs(dir.y), 0.04);
                    float fluidFogFactor = exp(-_FluidFogDensity * domeDist);
                    col.rgb = lerp(_FluidFogColor.rgb, col.rgb, fluidFogFactor);
                }

                // SUNRISE/SUNSET FAN — b1.7.3 renderSky triangle fan, solved analytically.
                // Vanilla geometry (after glRotatef(90,X) and the angle>0.5 flip about Z):
                // center C=(0,0,100) alpha=a, rim R(theta)=(120 sin, 40a cos, 120 cos) alpha=0,
                // in the FIXED world frame (the fan hugs the horizon at the sunset/sunrise
                // point; it does NOT rotate with sun elevation). Solving t*d = (1-rho)C +
                // rho*R(theta) for a view ray d gives rho = 100K/(dz + 100K - 3dy/a) with
                // K = sqrt((dx/120)^2 + (dy/(40a))^2); interpolated alpha = a*(1-rho), blended
                // SRC_ALPHA/ONE_MINUS_SRC_ALPHA. Script publishes _SunriseColor.a = 0 when
                // vanilla calcSunriseSunsetColors returns null (fan inactive).
                if (_SunriseColor.a > 0.0001)
                {
                    float3 q0 = RotateAroundYInDegrees(RotateAroundZInDegrees(dir, 180.0), 90.0);
                    // Fixed world frame, MC axes. The -q0.z restores mcw.y = +up: the raw
                    // swizzle chain yields mcw.y = -dir.y, and the -3dy/a term is the fan's
                    // only up/down asymmetry — with the sign wrong the glow paints the
                    // ZENITH instead of hugging the horizon (review-confirmed, 6/6 votes).
                    float3 mcw = float3(q0.x, -q0.z, q0.y);
                    float fz = mcw.z * (_CelestialAngle > 0.5 ? -1.0 : 1.0); // sunset +Z, sunrise -Z
                    float fa = _SunriseColor.a;
                    float kx = mcw.x / 120.0;
                    float ky = mcw.y / (40.0 * fa);
                    float K = sqrt(kx * kx + ky * ky);
                    float denom = fz + 100.0 * K - 3.0 * mcw.y / fa;
                    if (denom > 0.0001)
                    {
                        float rho = 100.0 * K / denom;
                        if (rho < 1.0)
                        {
                            float fanAlpha = fa * (1.0 - rho);
                            col.rgb = lerp(col.rgb, _SunriseColor.rgb, fanAlpha);
                        }
                    }
                }


                // DAY/NIGHT: sun/moon as PROPER BOUNDED QUADS, b1.7.3 renderSky style —
                // both rotate about the celestial axis by the EASED celestial angle
                // (WorldProvider.calculateCelestialAngle; 0 = noon), sun and moon on
                // OPPOSITE sides, composited additively with the texture's own alpha
                // (GL_SRC_ALPHA, GL_ONE). No +0.5 here: adding half a turn is a 180-degree
                // flip about the celestial axis that puts the SUN at midnight-zenith
                // (verified with editor sky probes). The old code projected the textures
                // for EVERY sky direction with clamped UVs, so edge texels streaked to
                // the horizon as infinite lines.
                float3 celDir = RotateAroundZInDegrees(dir, _CelestialAngle * 360.0);
                float3 projDir = RotateAroundYInDegrees(celDir, 90);

                // Sun on the +Z side of the rotated frame, moon on the -Z side (opposite,
                // like the y=+100 / y=-100 quads in RenderGlobal.renderSky). Only render a
                // body when the view direction is inside its quad's angular footprint.
                if (projDir.z > 0.001)
                {
                    // b1.7.3: sun quad half-size 30 at plane distance 100 (tan 0.3);
                    // the moon below is 20/100 (tan 0.2) — the sun is 1.5x the moon.
                    float2 sunUV = (projDir.xy / projDir.z * 0.5 / _SunSize) + 0.5;
                    if (sunUV.x >= 0.0 && sunUV.x <= 1.0 && sunUV.y >= 0.0 && sunUV.y <= 1.0)
                    {
                        half4 sunTex = tex2D(_SunTex, sunUV);
                        col.rgb += sunTex.rgb * sunTex.a;
                    }
                }
                else if (projDir.z < -0.001)
                {
                    // Moon UVs mirror through the opposite projection — b1.7.3 draws the
                    // moon quad with UVs rotated 180 degrees relative to the sun's.
                    float2 moonUV = (projDir.xy / projDir.z * 0.5 / _MoonSize) + 0.5;
                    if (moonUV.x >= 0.0 && moonUV.x <= 1.0 && moonUV.y >= 0.0 && moonUV.y <= 1.0)
                    {
                        half4 moonTex = tex2D(_MoonTex, moonUV);
                        col.rgb += moonTex.rgb * moonTex.a;
                    }
                }

                // STARS — b1.7.3 star display list, baked to an equirect coverage texture
                // (StarFieldBaker ports RenderGlobal.renderStars exactly: Random(10842L),
                // 1500 candidates, tangent quads at radius 100). Vanilla draws the list under
                // the SAME celestial rotation as the sun with glColor4f(b,b,b,b) and
                // GL_SRC_ALPHA/GL_ONE — i.e. adds coverage * b^2. projDir is already the
                // celestial-rotated frame (sun at +Z); map it to the bake's model space
                // (sun at +Y) and sample with the mapping the baker wrote:
                // u = atan2(x,-z)/2pi + 0.5, v = asin(y)/pi + 0.5. Texture has no mips, so
                // the computed-UV longitude seam is artifact-free.
                if (_StarBrightness > 0.001)
                {
                    float3 mcCel = float3(projDir.x, projDir.z, projDir.y);
                    float starLon = atan2(mcCel.x, -mcCel.z);
                    float starLat = asin(clamp(mcCel.y, -1.0, 1.0));
                    float2 starUV = float2(starLon / 6.28318530718 + 0.5, starLat / 3.14159265359 + 0.5);
                    float starCov = tex2D(_StarTex, starUV).r;
                    col.rgb += starCov * (_StarBrightness * _StarBrightness);
                }

                return saturate(col);
            }
            ENDCG
        }
    }
}