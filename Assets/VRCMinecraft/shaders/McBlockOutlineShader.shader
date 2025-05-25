Shader "Unlit/McBlockOutlineShader"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (1,1,1,1)
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
            };

            struct v2f
            {
                UNITY_FOG_COORDS(1)
                float4 vertex : SV_POSITION;
            };

            fixed4 _OutlineColor;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                UNITY_TRANSFER_FOG(o,o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = _OutlineColor;
                //Make it blink in a sine wave pattern based on _Time. Fluctuate between 0.0 alpha and 0.3 alpha.
                col.a = lerp(0.01f,0.4f,saturate(sin(_Time.x * 100)));
                
                


                // apply fog
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
}
