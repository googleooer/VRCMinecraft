// Made with Amplify Shader Editor
// Available at the Unity Asset Store - http://u3d.as/y3X 
Shader "Minecraft Sky (With Stars)"
{
    Properties
    {
        [Header(Sun and Moon)]
        _SunTex ("Sun", 2D) = "white" {}
        _MoonTex ("Moon", 2D) = "white" {}
        _SunSize ("Sun Size (tan half-angle, vanilla 0.3)", Range(0.01, 1)) = 0.3
        _MoonSize ("Moon Size (tan half-angle, vanilla 0.2)", Range(0.01, 1)) = 0.2
        [Header(Stars)]
        _StarTex ("Star Field (baked b1.7.3 equirect)", 2D) = "black" {}
        [Header(Script driven   b1.7.3 renderSky)]
        // The sky dome is drawn at _McSkyColor (func_4079_a) and linear-fogged toward
        // _McHorizonColor (== the terrain fog) with the sky-pass fog end = _McFarPlane * 0.8.
        _McSkyColor("Sky Color (func_4079_a)", Color) = (0.47, 0.65, 1.0, 1)
        _McHorizonColor("Horizon Fog Color", Color) = (0.75, 0.82, 1.0, 1)
        _McFarPlane("Far Plane Distance", Float) = 128
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
            half4 _McSkyColor;
            half4 _McHorizonColor;
            float _McFarPlane;
            sampler2D _StarTex;
            half _StarBrightness;
            half4 _SunriseColor;
            half4 _FluidFogColor;
            half _FluidFogDensity;
            // float, not half: fp16 quantizes the *360 rotation into ~0.18-degree steps on
            // Quest — a third of a baked star's width, making the night sky lurch visibly.
            float _CelestialAngle;

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

                // SKY GRADIENT — b1.7.3 renderSky, exact. The sky is a FLAT plane dome at
                // y=+16 (void plane at y=-16), drawn at the SKY color (_McSkyColor =
                // World.func_4079_a) and LINEAR-fogged toward the composited fog color
                // (_McHorizonColor = the terrain fog, so horizon and terrain stay in sync).
                // The sky pass uses setupFog(-1): start 0, end farPlane*0.8, EYE-RADIAL. The
                // radial distance to the flat plane in view direction dir is 16/|dir.y|, so
                // the whole day/night/sunset gradient falls out of the two computed colors —
                // no hand-tuned bands. Below the horizon the void plane is darkened
                // (WorldProvider.func_28112_c == true for the overworld).
                float fogEnd = _McFarPlane * 0.8;
                float dist = 16.0 / max(abs(dir.y), 0.0001);
                float fogFactor = saturate((fogEnd - dist) / max(0.001, fogEnd)); // 1 = body color, 0 = fog

                half4 col;
                if (dir.y >= 0.0)
                {
                    col = half4(lerp(_McHorizonColor.rgb, _McSkyColor.rgb, fogFactor), 1.0);
                }
                else
                {
                    half3 voidCol = _McSkyColor.rgb * half3(0.2, 0.2, 0.6) + half3(0.04, 0.04, 0.1);
                    col = half4(lerp(_McHorizonColor.rgb, voidCol, fogFactor), 1.0);
                }

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
                // Vanilla (renderSky:635-668): a fan drawn in the sky-local frame — center
                // C=(0,100,0) alpha=a, rim R(phi)=(120 sin, 120 cos, -40a cos) alpha=0 — then
                // transformed glRotatef(90,X) (and, for celestialAngle>0.5, glRotatef(180,Z)
                // FIRST since GL post-multiplies). After Rx(90): C=(0,0,100), rim=(120 sin,
                // 40a cos, 120 cos). So the fan center sits at +Z — the SAME horizon the sun
                // sets on (both from the +Y quad rotated about X). Sunrise (angle>0.5) is the
                // same fan mirrored to -Z (negate x,z). Solving t*d=(1-rho)C+rho*R(phi):
                // rho = 100K/(dz + 100K - 3dy/a), K = sqrt((dx/120)^2 + (dy/(40a))^2), on the
                // UNIT view dir (homogeneous in the sky units); alpha = a*(1-rho).
                if (_SunriseColor.a > 0.0001)
                {
                    float3 fanDir = (_CelestialAngle > 0.5) ? float3(-dir.x, dir.y, -dir.z) : dir;
                    float fa = _SunriseColor.a;
                    float kx = fanDir.x / 120.0;
                    float ky = fanDir.y / (40.0 * fa);
                    float K = sqrt(kx * kx + ky * ky);
                    float denom = fanDir.z + 100.0 * K - 3.0 * fanDir.y / fa;
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


                // SUN / MOON / STARS — b1.7.3 renderSky, exact axis. Vanilla draws the sun quad
                // at local (0,+100,0) and the moon at (0,-100,0), then rotates BOTH (and the
                // star list) by glRotatef(celestialAngle*360, 1,0,0) — about the X axis. So the
                // bodies travel the Y-Z plane: overhead at noon, setting on +Z, rising from -Z.
                // (The previous code rotated about Z, sending the sun to +/-X — the reported bug.)
                // We bring the view direction into the sky-local frame with the INVERSE rotation
                // Rx(-theta), where the sun sits at local +Y and the moon at -Y, then intersect
                // the flat quads (y=+/-100) and sample with vanilla's exact UVs.
                float th = _CelestialAngle * 6.28318530718; // celestialAngle * 360 deg, radians
                float sinT, cosT; sincos(th, sinT, cosT);
                float3 localDir = float3(dir.x,
                                         dir.y * cosT + dir.z * sinT,     // Rx(-theta)
                                         -dir.y * sinT + dir.z * cosT);
                float s = localDir.y; // sun axis: sun when s>0 (local +Y), moon when s<0 (-Y)

                if (s > 0.001)
                {
                    // Sun quad y=+100, x,z in [-30,30], U=x/60+0.5, V=z/60+0.5 (renderSky:687).
                    // _SunSize = 30/100 = 0.3 tan half-angle; ray hits y=100 at local x=100*lx/s.
                    float2 sunUV = float2(localDir.x, localDir.z) / s * (0.5 / _SunSize) + 0.5;
                    if (sunUV.x >= 0.0 && sunUV.x <= 1.0 && sunUV.y >= 0.0 && sunUV.y <= 1.0)
                    {
                        half4 sunTex = tex2D(_SunTex, sunUV);
                        col.rgb += sunTex.rgb * sunTex.a;
                    }
                }
                else if (s < -0.001)
                {
                    // Moon quad y=-100, size 20 (_MoonSize 0.2); vanilla UVs (renderSky:695) are
                    // U mirrored... = 0.5 + 0.5*(lx/s)/size, V = 0.5 - 0.5*(lz/s)/size (the moon's
                    // 180-degree UV vs the sun). s<0 makes lx/s, lz/s carry the sign correctly.
                    float2 moonUV = float2(localDir.x, -localDir.z) / s * (0.5 / _MoonSize) + 0.5;
                    if (moonUV.x >= 0.0 && moonUV.x <= 1.0 && moonUV.y >= 0.0 && moonUV.y <= 1.0)
                    {
                        half4 moonTex = tex2D(_MoonTex, moonUV);
                        col.rgb += moonTex.rgb * moonTex.a;
                    }
                }

                // STARS — the baked equirect coverage (StarFieldBaker ports renderStars exactly:
                // Random(10842L), 1500 candidates). Vanilla draws the list under the SAME Rx
                // rotation as the sun, glColor4f(b,b,b,b), GL_SRC_ALPHA/GL_ONE -> adds coverage*b^2.
                // The bake's model space IS the sky-local frame, so sample directly with localDir
                // via the baker's mapping u = atan2(x,-z)/2pi+0.5, v = asin(y)/pi+0.5.
                if (_StarBrightness > 0.001)
                {
                    float starLon = atan2(localDir.x, -localDir.z);
                    float starLat = asin(clamp(localDir.y, -1.0, 1.0));
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