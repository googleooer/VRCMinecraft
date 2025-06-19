Shader "Unlit/McBlockOutlineShader"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (1,1,1,1)
        _TerrainTex ("Terrain Atlas", 2DArray) = "black" {}
        _Progress ("Progress", Integer) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent-1"}//this helps the user make materials that are not effected
        //anything with a renderqueue 2999 or below will be effected by darkmode.

		
        ZWrite Off 
		Blend SrcAlpha OneMinusSrcAlpha
        Offset -0.05, -0.05

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // make fog work
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                fixed3 uv : TEXCOORD0;
            };

            struct v2f
            {
                UNITY_FOG_COORDS(1)
                float4 vertex : SV_POSITION;
                fixed3 uv : TEXCOORD0;
            };

            fixed4 _OutlineColor;
            fixed _Progress;

            UNITY_DECLARE_TEX2DARRAY(_TerrainTex);


            fixed GetSliceFromProgress(fixed progress)
            {
                // The first break frame is x0 y15 (240)
                // The last break frame is x9 y15 (249)
                return 240 + floor(min((_Progress/10.0),9));
            }

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv.xy = v.uv.xy;
                o.uv.z = GetSliceFromProgress(_Progress);
                UNITY_TRANSFER_FOG(o,o.vertex);
                return o;
            }

            

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = UNITY_SAMPLE_TEX2DARRAY(_TerrainTex, i.uv);
                if(_Progress < 1.0f)
                {
                    col.a = 0.0f;
                }
                fixed4 outline = _OutlineColor;
                //Make it blink in a sine wave pattern based on _Time. Fluctuate between 0.0 alpha and 0.3 alpha.
                outline.a = lerp(0.01f,0.4f,saturate(sin(_Time.x * 100)));
                
                
                col = lerp(outline, col, clamp(col.a*100.0f,0.0,1.0));

                // apply fog
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
}
