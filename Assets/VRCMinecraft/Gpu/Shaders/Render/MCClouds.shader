// b1.7.3 clouds — a REAL 3D mesh of cloud boxes (12 wide x 4 tall x 12 deep) baked from
// clouds.png (CloudMeshBaker): one box per opaque texture cell, interior faces culled, each
// face carrying its vanilla shade in the vertex color (top 1.0, bottom 0.7, X-sides 0.9,
// Z-sides 0.8). Two-pass DEPTH PRIME so overlapping translucent faces blend exactly ONCE — this
// is vanilla renderCloudsFancy's trick (pass 1 writes only depth with the color mask off, pass 2
// draws the front-most surface). Without it, the far faces of a box show through the near faces
// at (1-alpha) and read as a grid. Cull Off — vanilla does glDisable(GL_CULL_FACE). The mesh is
// one texture period (256 cells) wide in X; two copies offset by a period scroll seamlessly
// (McWorld positions them). Cloud color (getCloudColour) + fog are pushed live by McWorld.
Shader "VRCM/MCClouds"
{
    Properties
    {
        _CloudColor ("Cloud Color (script: getCloudColour)", Color) = (1,1,1,1)
        _CloudAlpha ("Cloud Alpha (b1.7.3 = 0.8)", Float) = 0.8
        [Header(Fog   world pass  same as terrain)]
        _FogColor ("Fog Color", Color) = (0.5,0.6,0.7,1)
        _FogStart ("Fog Start", Float) = 32
        _FogEnd ("Fog End", Float) = 128
        _FogMode ("Fog Mode", Float) = 0
        _FogDensity ("Fog Density", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }

        // Pass 1 — DEPTH PRIME: write only depth (nearest cloud surface), no color. ZTest LEqual
        // (default) so terrain / mountains above y=108 still occlude the clouds.
        Pass
        {
            Cull Off
            ZWrite On
            ColorMask 0
            CGPROGRAM
            #pragma vertex vertD
            #pragma fragment fragD
            #pragma target 2.0
            #include "UnityCG.cginc"
            struct appdataD { float4 vertex : POSITION; };
            float4 vertD(appdataD v) : SV_POSITION { return UnityObjectToClipPos(v.vertex); }
            fixed4 fragD() : SV_Target { return 0; }
            ENDCG
        }

        // Pass 2 — COLOR: blend the front-most surface once (ZTest LEqual matches the primed
        // depth), no depth write.
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            float4 _CloudColor;
            float _CloudAlpha;
            fixed4 _FogColor;
            float _FogStart, _FogEnd, _FogMode, _FogDensity;

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float3 worldPos : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;                                  // per-face vanilla shade
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            half calcFog(float dist) // b1.7.3 setupFog world pass, == terrain
            {
                half f;
                if (_FogMode < 0.5)      f = saturate((_FogEnd - dist) / max(0.001, _FogEnd - _FogStart));
                else if (_FogMode < 1.5) f = saturate(exp(-_FogDensity * dist));
                else                     f = saturate(exp(-_FogDensity * _FogDensity * dist * dist));
                return f;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed3 rgb = _CloudColor.rgb * i.color.rgb;         // cloud color x face shade
                float dist = distance(i.worldPos, _WorldSpaceCameraPos);
                half fog = calcFog(dist);
                rgb = lerp(_FogColor.rgb, rgb, fog);                // dissolve into the horizon
                return fixed4(rgb, _CloudAlpha);
            }
            ENDCG
        }
    }
}
