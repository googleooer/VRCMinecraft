Shader "Unlit/McBlockOutlineShader"
{
    Properties
    {
        _OutlineColor ("Outline Color", Color) = (0,0,0,0.4)
        _TerrainTex ("Terrain Atlas", 2DArray) = "black" {}
        _Progress ("Progress", Range(0,100)) = 0
        _OutlineThickness ("Outline Thickness (pixels)", Range(0,6)) = 2
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent-1"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
        }

        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Offset -0.05, -0.05

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_FOG_COORDS(1)
            };

            UNITY_DECLARE_TEX2DARRAY(_TerrainTex);

            float4 _OutlineColor;
            float _Progress;
            float _OutlineThickness;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv.xy;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            float ComputeOutlineMask(float2 uv)
            {
                const float epsilon = 1e-6f;
                float thicknessPixels = max(_OutlineThickness, 0.0f);

                float uvPerPixelU = max(length(float2(ddx(uv.x), ddy(uv.x))), epsilon);
                float uvPerPixelV = max(length(float2(ddx(uv.y), ddy(uv.y))), epsilon);

                float edgeWidthU = thicknessPixels * uvPerPixelU;
                float edgeWidthV = thicknessPixels * uvPerPixelV;

                float distU = min(uv.x, 1.0f - uv.x);
                float distV = min(uv.y, 1.0f - uv.y);

                float maskU = 1.0f - step(edgeWidthU, distU);
                float maskV = 1.0f - step(edgeWidthV, distV);

                return saturate(maskU + maskV);
            }

            float3 ComputeCrackColor(float2 uv, float progress, out float crackAlpha)
            {
                crackAlpha = 0.0f;

                if (progress <= 0.0f)
                {
                    return float3(0.0f, 0.0f, 0.0f);
                }

                float stage = floor(progress / 10.0f);
                stage = clamp(stage, 0.0f, 9.0f);
                float slice = 240.0f + stage;

                float3 sampleCoord = float3(uv, slice);
                float4 crackSample = UNITY_SAMPLE_TEX2DARRAY(_TerrainTex, sampleCoord);

                crackAlpha = crackSample.a;
                return crackSample.rgb;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 faceUV = clamp(i.uv, 0.0f, 1.0f);

                float outlineMask = ComputeOutlineMask(faceUV);

                float outlineAlpha = _OutlineColor.a * outlineMask;
                float3 outlineRGB = _OutlineColor.rgb;

                float crackAlpha;
                float3 crackRGB = ComputeCrackColor(faceUV, _Progress, crackAlpha);

                float crackContribution = (1.0f - outlineMask) * crackAlpha;
                float preMultOutline = outlineAlpha;
                float preMultCrack = crackContribution;

                float finalAlpha = preMultOutline + preMultCrack;
                float3 finalRGB = float3(0.0f, 0.0f, 0.0f);

                if (finalAlpha > 0.0f)
                {
                    float3 accum = outlineRGB * preMultOutline + crackRGB * preMultCrack;
                    finalRGB = accum / finalAlpha;
                }

                fixed4 col = fixed4(finalRGB, finalAlpha);
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
}
