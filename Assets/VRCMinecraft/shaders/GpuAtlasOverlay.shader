Shader "VRCM/GpuAtlasOverlay"
{
    Properties
    {
        _MainTex ("Source Atlas", 2D) = "black" {}
        _OverlayTex ("Overlay", 2D) = "black" {}
        _OverlayRect ("Overlay Rect", Vector) = (0,0,1,1)
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
            sampler2D _OverlayTex;
            float4 _OverlayRect;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 atlasColor = tex2D(_MainTex, i.uv);
                float2 minUv = _OverlayRect.xy;
                float2 maxUv = _OverlayRect.xy + _OverlayRect.zw;
                if (i.uv.x >= minUv.x && i.uv.x < maxUv.x && i.uv.y >= minUv.y && i.uv.y < maxUv.y)
                {
                    float2 overlayUv = (i.uv - minUv) / _OverlayRect.zw;
                    return tex2D(_OverlayTex, overlayUv);
                }
                return atlasColor;
            }
            ENDCG
        }
    }
}
