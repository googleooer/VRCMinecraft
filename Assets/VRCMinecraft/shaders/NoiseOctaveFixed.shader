Shader "VRCM/NoiseOctaveFixed"
{
    Properties
    {
        _PermTex ("Permutation Texture", 2D) = "white" {}
        _GradTex ("Gradient Texture", 2D) = "white" {}
        _AccumulationTex ("Accumulation Texture", 2D) = "black" {}
        _Amplitude ("Amplitude", Float) = 1.0
        _XSize ("X Size", Int) = 5
        _YSize ("Y Size", Int) = 33
        _ZSize ("Z Size", Int) = 5
        _Is2D ("Is 2D", Int) = 0
        _OctaveRow ("Octave Row", Int) = 0
    }
    
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        
        Pass
        {
            Name "CLEAR"
            ZTest Always
            ZWrite Off
            Cull Off
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
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            
            float4 frag (v2f i) : SV_Target
            {
                // Clear to black (0,0,0,1)
                return float4(0, 0, 0, 1);
            }
            ENDCG
        }
        
        Pass
        {
            Name "OCTAVE"
            ZTest Always
            ZWrite Off
            Cull Off
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
            
            sampler2D _AccumulationTex;
            sampler2D _PermTex;
            float _Amplitude;
            int _XSize;
            int _YSize;
            int _ZSize;
            int _Is2D;
            int _OctaveRow;

            // CPU-precomputed coordinate data for double-precision noise.
            // PermIdx = floor(worldCoord) & 0xFF, computed in double on CPU.
            // Frac    = worldCoord - floor(worldCoord), computed in double on CPU.
            // This eliminates all float precision issues in coordinate computation.
            #define MAX_XZSIZE 5
            #define MAX_YSIZE 65
            float _PermIdxX[MAX_XZSIZE];
            float _FracX[MAX_XZSIZE];
            float _PermIdxY[MAX_YSIZE];
            float _FracY[MAX_YSIZE];
            // GradFracY: the relY of the FIRST sample in each Perlin cell.
            // Minecraft Beta 1.7.3 caches gradient dot products when the Y
            // permutation index hasn't changed, so gradient computation uses
            // the "stale" relY from the first entry in the cell, while fade()
            // uses the actual relY.  This array replicates that behaviour.
            float _GradFracY[MAX_YSIZE];
            float _PermIdxZ[MAX_XZSIZE];
            float _FracZ[MAX_XZSIZE];

            float samplePerm(int index)
            {
                int wrapped = index & 255;
                // Read from the correct row of the 256xN multi-octave permutation texture.
                // _OctaveRow selects which octave's permutation table to read.
                float rowV = ((float)_OctaveRow + 0.5) / 16.0; // 16 = GPU_MAX_OCTAVES
                return round(tex2Dlod(_PermTex, float4(((float)wrapped + 0.5) / 256.0, rowV, 0, 0)).r * 255.0);
            }

            static const float3 GRADIENTS[16] = {
                float3(1, 1, 0), float3(-1, 1, 0), float3(1, -1, 0), float3(-1, -1, 0),
                float3(1, 0, 1), float3(-1, 0, 1), float3(1, 0, -1), float3(-1, 0, -1),
                float3(0, 1, 1), float3(0, -1, 1), float3(0, 1, -1), float3(0, -1, -1),
                float3(1, 1, 0), float3(0, -1, 1), float3(-1, 1, 0), float3(0, -1, -1)
            };

            float3 sampleGrad(int hash)
            {
                int h = hash & 15;
                return GRADIENTS[h];
            }

            float fade(float t)
            {
                return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
            }
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            
            float4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
                
                if (_Is2D == 1)
                {
                    // 2D noise (for noise6 and noise7)
                    int xIndex = clamp((int)floor(uv.x * _XSize), 0, _XSize - 1);
                    int zIndex = clamp((int)floor(uv.y * _ZSize), 0, _ZSize - 1);
                    
                    // Use CPU-precomputed permutation indices and fractional parts (double-precise)
                    int p1 = (int)_PermIdxX[xIndex];
                    float relX = _FracX[xIndex];
                    float fx = fade(relX);
                    
                    int p3 = (int)_PermIdxZ[zIndex];
                    float relZ = _FracZ[zIndex];
                    float fz = fade(relZ);
                    
                    int a1 = (int)samplePerm(p1);
                    int a2 = (int)samplePerm(a1) + p3;
                    
                    int b1 = (int)samplePerm(p1 + 1);
                    int b2 = (int)samplePerm(b1) + p3;
                    
                    int hash0 = (int)samplePerm(a2);
                    int hash1 = (int)samplePerm(b2);
                    int hash2 = (int)samplePerm(a2 + 1);
                    int hash3 = (int)samplePerm(b2 + 1);
                    float3 grad0 = sampleGrad(hash0);
                    float3 grad1 = sampleGrad(hash1);
                    float3 grad2 = sampleGrad(hash2);
                    float3 grad3 = sampleGrad(hash3);
                    
                    float g0 = grad0.r * relX + grad0.b * relZ;
                    float g1 = grad1.r * (relX - 1.0) + grad1.b * relZ;
                    float val1 = g0 + fx * (g1 - g0);
                    
                    float g2 = grad2.r * relX + grad2.b * (relZ - 1.0);
                    float g3 = grad3.r * (relX - 1.0) + grad3.b * (relZ - 1.0);
                    float val2 = g2 + fx * (g3 - g2);
                    
                    float value = val1 + fz * (val2 - val1);
                    value *= _Amplitude;
                    
                    float prevValue = tex2Dlod(_AccumulationTex, float4(uv, 0, 0)).r;
                    value = prevValue + value;
                    
                    return float4(value, 0, 0, 1);
                }
                else
                {
                    // 3D noise (for noise1, noise2, noise3)
                    float xStride = (float)_XSize;
                    float zStride = (float)_ZSize;
                    float totalXZ = xStride * zStride;
                    int flatIndex = clamp((int)floor(uv.x * totalXZ), 0, (int)totalXZ - 1);
                    int zIndex = flatIndex / _XSize;
                    int xIndex = flatIndex - zIndex * _XSize;
                    int yIndex = clamp((int)floor(uv.y * _YSize), 0, _YSize - 1);
                    
                    // Use CPU-precomputed permutation indices and fractional parts (double-precise)
                    int p1 = (int)_PermIdxX[xIndex];
                    float relX = _FracX[xIndex];
                    float fx = fade(relX);
                    
                    int p2 = (int)_PermIdxY[yIndex];
                    // Minecraft Beta 1.7.3 Y-level caching emulation.
                    // Gradient dot products use _GradFracY (the relY of the FIRST
                    // sample in this Perlin cell), while fade() uses the actual relY.
                    float gradRelY = _GradFracY[yIndex];
                    float relY = _FracY[yIndex];
                    float fy = fade(relY);
                    
                    int p3 = (int)_PermIdxZ[zIndex];
                    float relZ = _FracZ[zIndex];
                    float fz = fade(relZ);
                    
                    int a1 = (int)samplePerm(p1) + p2;
                    int a2 = (int)samplePerm(a1) + p3;
                    int a3 = (int)samplePerm(a1 + 1) + p3;
                    int b1 = (int)samplePerm(p1 + 1) + p2;
                    int b2 = (int)samplePerm(b1) + p3;
                    int b3 = (int)samplePerm(b1 + 1) + p3;
                    
                    int hash0 = (int)samplePerm(a2);
                    int hash1 = (int)samplePerm(b2);
                    int hash2 = (int)samplePerm(a3);
                    int hash3 = (int)samplePerm(b3);
                    int hash4 = (int)samplePerm(a2 + 1);
                    int hash5 = (int)samplePerm(b2 + 1);
                    int hash6 = (int)samplePerm(a3 + 1);
                    int hash7 = (int)samplePerm(b3 + 1);
                    float3 grad0 = sampleGrad(hash0);
                    float3 grad1 = sampleGrad(hash1);
                    float3 grad2 = sampleGrad(hash2);
                    float3 grad3 = sampleGrad(hash3);
                    float3 grad4 = sampleGrad(hash4);
                    float3 grad5 = sampleGrad(hash5);
                    float3 grad6 = sampleGrad(hash6);
                    float3 grad7 = sampleGrad(hash7);
                    
                    // Use gradRelY (cached "base" relY) for gradient dot products
                    float g0 = dot(grad0, float3(relX, gradRelY, relZ));
                    float g1 = dot(grad1, float3(relX - 1.0, gradRelY, relZ));
                    float d13 = g0 + fx * (g1 - g0);

                    // DIAGNOSTIC OUTPUT
                    if (_OctaveRow == 0) {
                        return float4(hash0, g0, fx, 1);
                    }
                    
                    float g2 = dot(grad2, float3(relX, gradRelY - 1.0, relZ));
                    float g3 = dot(grad3, float3(relX - 1.0, gradRelY - 1.0, relZ));
                    float d15 = g2 + fx * (g3 - g2);
                    
                    float g4 = dot(grad4, float3(relX, gradRelY, relZ - 1.0));
                    float g5 = dot(grad5, float3(relX - 1.0, gradRelY, relZ - 1.0));
                    float d16 = g4 + fx * (g5 - g4);
                    
                    float g6 = dot(grad6, float3(relX, gradRelY - 1.0, relZ - 1.0));
                    float g7 = dot(grad7, float3(relX - 1.0, gradRelY - 1.0, relZ - 1.0));
                    float d18 = g6 + fx * (g7 - g6);
                    
                    // Use actual fy (fade of real relY) for final interpolation
                    float val1 = d13 + fy * (d15 - d13);
                    float val2 = d16 + fy * (d18 - d16);
                    float value = val1 + fz * (val2 - val1);
                    value *= _Amplitude;
                    
                    float prevValue = tex2Dlod(_AccumulationTex, float4(uv, 0, 0)).r;
                    value = prevValue + value;
                    
                    return float4(value, 0, 0, 1);
                }
            }
            ENDCG
        }
    }
}
