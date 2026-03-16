Shader "VRCM/NoiseOctaveFixed"
{
    Properties
    {
        _PermTex ("Permutation Texture", 2D) = "white" {}
        _GradTex ("Gradient Texture", 2D) = "white" {}
        _AccumulationTex ("Accumulation Texture", 2D) = "black" {}
        _ClimatePermTex0 ("Climate Perm 0", 2D) = "white" {}
        _ClimatePermTex1 ("Climate Perm 1", 2D) = "white" {}
        _ClimatePermTex2 ("Climate Perm 2", 2D) = "white" {}
        _ClimateOffsetTex0 ("Climate Offset 0", 2D) = "white" {}
        _ClimateOffsetTex1 ("Climate Offset 1", 2D) = "white" {}
        _ClimateOffsetTex2 ("Climate Offset 2", 2D) = "white" {}
        _ClimateBiomeLookupTex ("Climate Biome Lookup", 2D) = "white" {}
        _Amplitude ("Amplitude", Float) = 1.0
        _XSize ("X Size", Int) = 5
        _YSize ("Y Size", Int) = 33
        _ZSize ("Z Size", Int) = 5
        _Is2D ("Is 2D", Int) = 0
        _OctaveCount ("Octave Count", Int) = 16
        _OctaveRow ("Octave Row", Int) = 0
        _ChunkX ("Chunk X", Int) = 0
        _ChunkZ ("Chunk Z", Int) = 0
        _ClimateOctaveCount0 ("Climate Octave Count 0", Int) = 4
        _ClimateOctaveCount1 ("Climate Octave Count 1", Int) = 4
        _ClimateOctaveCount2 ("Climate Octave Count 2", Int) = 2
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
            sampler2D _CoordXTex;
            sampler2D _CoordYTex;
            sampler2D _CoordZTex;
            int _XSize;
            int _YSize;
            int _ZSize;
            int _Is2D;
            int _OctaveCount;

            float samplePerm(int octave, int index)
            {
                // NOTE: index can be 0-510 (the standard Perlin double-table).
                // We store 512 entries per octave; do NOT clamp with & 255 here.
                float rowV = ((float)octave + 0.5) / 16.0; // 16 = GPU_MAX_OCTAVES
                return round(tex2Dlod(_PermTex, float4(((float)index + 0.5) / 512.0, rowV, 0, 0)).r * 255.0);
            }

            float4 sampleCoordX(int octave, int index)
            {
                float rowV = ((float)octave + 0.5) / 16.0;
                return tex2Dlod(_CoordXTex, float4(((float)index + 0.5) / 16.0, rowV, 0, 0));
            }

            float4 sampleCoordY(int octave, int index)
            {
                float rowV = ((float)octave + 0.5) / 16.0;
                return tex2Dlod(_CoordYTex, float4(((float)index + 0.5) / 65.0, rowV, 0, 0));
            }

            float4 sampleCoordZ(int octave, int index)
            {
                float rowV = ((float)octave + 0.5) / 16.0;
                return tex2Dlod(_CoordZTex, float4(((float)index + 0.5) / 16.0, rowV, 0, 0));
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
                if (_Is2D == 1)
                {
                    // 2D noise (for noise6 and noise7)
                    int xIndex = clamp((int)floor(i.vertex.x), 0, _XSize - 1);
                    int zIndex = clamp((int)floor(i.vertex.y), 0, _ZSize - 1);
                    float value = 0.0;
                    [loop]
                    for (int octave = 0; octave < 16; octave++)
                    {
                        if (octave >= _OctaveCount) break;

                        float4 coordX = sampleCoordX(octave, xIndex);
                        float4 coordZ = sampleCoordZ(octave, zIndex);

                        int p1 = (int)round(coordX.r * 255.0);
                        float relX = coordX.g;
                        float fx = fade(relX);

                        int p3 = (int)round(coordZ.r * 255.0);
                        float relZ = coordZ.g;
                        float fz = fade(relZ);

                        int a1 = (int)samplePerm(octave, p1);
                        int a2 = (int)samplePerm(octave, a1) + p3;

                        int b1 = (int)samplePerm(octave, p1 + 1);
                        int b2 = (int)samplePerm(octave, b1) + p3;

                        int hash0 = (int)samplePerm(octave, a2);
                        int hash1 = (int)samplePerm(octave, b2);
                        int hash2 = (int)samplePerm(octave, a2 + 1);
                        int hash3 = (int)samplePerm(octave, b2 + 1);
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

                        float octaveAmplitude = exp2((float)octave);
                        value += (val1 + fz * (val2 - val1)) * octaveAmplitude;
                    }

                    return float4(value, 0, 0, 1);
                }
                else
                {
                    // 3D noise (for noise1, noise2, noise3)
                    int totalXZ = _XSize * _ZSize;
                    int flatIndex = clamp((int)floor(i.vertex.x), 0, totalXZ - 1);
                    int zIndex = flatIndex / _XSize;
                    int xIndex = flatIndex - zIndex * _XSize;
                    int yIndex = clamp((int)floor(i.vertex.y), 0, _YSize - 1);
                    float value = 0.0;
                    [loop]
                    for (int octave = 0; octave < 16; octave++)
                    {
                        if (octave >= _OctaveCount) break;

                        float4 coordX = sampleCoordX(octave, xIndex);
                        float4 coordY = sampleCoordY(octave, yIndex);
                        float4 coordZ = sampleCoordZ(octave, zIndex);

                        int p1 = (int)round(coordX.r * 255.0);
                        float relX = coordX.g;
                        float fx = fade(relX);

                        int p2 = (int)round(coordY.r * 255.0);
                        float gradRelY = coordY.b;
                        float relY = coordY.g;
                        float fy = fade(relY);

                        int p3 = (int)round(coordZ.r * 255.0);
                        float relZ = coordZ.g;
                        float fz = fade(relZ);

                        int a1 = (int)samplePerm(octave, p1) + p2;
                        int a2 = (int)samplePerm(octave, a1) + p3;
                        int a3 = (int)samplePerm(octave, a1 + 1) + p3;
                        int b1 = (int)samplePerm(octave, p1 + 1) + p2;
                        int b2 = (int)samplePerm(octave, b1) + p3;
                        int b3 = (int)samplePerm(octave, b1 + 1) + p3;

                        int hash0 = (int)samplePerm(octave, a2);
                        int hash1 = (int)samplePerm(octave, b2);
                        int hash2 = (int)samplePerm(octave, a3);
                        int hash3 = (int)samplePerm(octave, b3);
                        int hash4 = (int)samplePerm(octave, a2 + 1);
                        int hash5 = (int)samplePerm(octave, b2 + 1);
                        int hash6 = (int)samplePerm(octave, a3 + 1);
                        int hash7 = (int)samplePerm(octave, b3 + 1);
                        float3 grad0 = sampleGrad(hash0);
                        float3 grad1 = sampleGrad(hash1);
                        float3 grad2 = sampleGrad(hash2);
                        float3 grad3 = sampleGrad(hash3);
                        float3 grad4 = sampleGrad(hash4);
                        float3 grad5 = sampleGrad(hash5);
                        float3 grad6 = sampleGrad(hash6);
                        float3 grad7 = sampleGrad(hash7);

                        float g0 = dot(grad0, float3(relX, gradRelY, relZ));
                        float g1 = dot(grad1, float3(relX - 1.0, gradRelY, relZ));
                        float d13 = g0 + fx * (g1 - g0);

                        float g2 = dot(grad2, float3(relX, gradRelY - 1.0, relZ));
                        float g3 = dot(grad3, float3(relX - 1.0, gradRelY - 1.0, relZ));
                        float d15 = g2 + fx * (g3 - g2);

                        float g4 = dot(grad4, float3(relX, gradRelY, relZ - 1.0));
                        float g5 = dot(grad5, float3(relX - 1.0, gradRelY, relZ - 1.0));
                        float d16 = g4 + fx * (g5 - g4);

                        float g6 = dot(grad6, float3(relX, gradRelY - 1.0, relZ - 1.0));
                        float g7 = dot(grad7, float3(relX - 1.0, gradRelY - 1.0, relZ - 1.0));
                        float d18 = g6 + fx * (g7 - g6);

                        float octaveAmplitude = exp2((float)octave);
                        value += (d13 + fy * (d15 - d13) + fz * ((d16 + fy * (d18 - d16)) - (d13 + fy * (d15 - d13)))) * octaveAmplitude;
                    }

                    return float4(value, 0, 0, 1);
                }
            }
            ENDCG
        }

        Pass
        {
            Name "CLIMATE"
            ZTest Always
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragClimate
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

            sampler2D _ClimatePermTex0;
            sampler2D _ClimatePermTex1;
            sampler2D _ClimatePermTex2;
            sampler2D _ClimateOffsetTex0;
            sampler2D _ClimateOffsetTex1;
            sampler2D _ClimateOffsetTex2;
            sampler2D _ClimateBiomeLookupTex;
            int _ChunkX;
            int _ChunkZ;
            int _XSize;
            int _ZSize;
            int _ClimateOctaveCount0;
            int _ClimateOctaveCount1;
            int _ClimateOctaveCount2;

            static const float SIMPLEX_F2 = 0.3660254037844386;
            static const float SIMPLEX_G2 = 0.2113248654051871;

            static const float2 SIMPLEX_GRADS[12] = {
                float2(1, 1), float2(-1, 1), float2(1, -1), float2(-1, -1),
                float2(1, 0), float2(-1, 0), float2(1, 0), float2(-1, 0),
                float2(0, 1), float2(0, -1), float2(0, 1), float2(0, -1)
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            int sampleClimatePerm(sampler2D tex, int octave, int index)
            {
                float rowV = ((float)octave + 0.5) / 4.0;
                return (int)round(tex2Dlod(tex, float4(((float)index + 0.5) / 512.0, rowV, 0, 0)).r * 255.0);
            }

            float2 sampleClimateOffset(sampler2D tex, int octave)
            {
                return tex2Dlod(tex, float4(((float)octave + 0.5) / 4.0, 0.5, 0, 0)).rg;
            }

            float simplexContribution(float2 grad, float x, float y)
            {
                float t = 0.5 - x * x - y * y;
                if (t < 0.0) return 0.0;
                t *= t;
                return t * t * dot(grad, float2(x, y));
            }

            int wrapLikeCpu(float v)
            {
                int truncated = (int)v;
                return v > 0.0 ? truncated : (truncated - 1);
            }

            float simplexNoise2D(sampler2D permTex, sampler2D offsetTex, int octave, float worldX, float worldZ, float gridX, float gridZ)
            {
                float2 randomOffset = sampleClimateOffset(offsetTex, octave);
                float cx = worldX * gridX + randomOffset.x;
                float cz = worldZ * gridZ + randomOffset.y;

                float skew = (cx + cz) * SIMPLEX_F2;
                int cellX = wrapLikeCpu(cx + skew);
                int cellZ = wrapLikeCpu(cz + skew);
                float unskew = (cellX + cellZ) * SIMPLEX_G2;
                float originX = cellX - unskew;
                float originZ = cellZ - unskew;
                float relX = cx - originX;
                float relZ = cz - originZ;

                int stepX = relX > relZ ? 1 : 0;
                int stepZ = relX > relZ ? 0 : 1;

                float relX1 = relX - stepX + SIMPLEX_G2;
                float relZ1 = relZ - stepZ + SIMPLEX_G2;
                float relX2 = relX - 1.0 + 2.0 * SIMPLEX_G2;
                float relZ2 = relZ - 1.0 + 2.0 * SIMPLEX_G2;

                int permX = cellX & 255;
                int permZ = cellZ & 255;
                int grad0 = sampleClimatePerm(permTex, octave, permX + sampleClimatePerm(permTex, octave, permZ)) % 12;
                int grad1 = sampleClimatePerm(permTex, octave, permX + stepX + sampleClimatePerm(permTex, octave, permZ + stepZ)) % 12;
                int grad2 = sampleClimatePerm(permTex, octave, permX + 1 + sampleClimatePerm(permTex, octave, permZ + 1)) % 12;

                float n0 = simplexContribution(SIMPLEX_GRADS[grad0], relX, relZ);
                float n1 = simplexContribution(SIMPLEX_GRADS[grad1], relX1, relZ1);
                float n2 = simplexContribution(SIMPLEX_GRADS[grad2], relX2, relZ2);
                return 70.0 * (n0 + n1 + n2);
            }

            float generateClimateField(sampler2D permTex, sampler2D offsetTex, int octaveCount, float worldX, float worldZ, float baseGridX, float baseGridZ, float fq, float persistence)
            {
                float gridXDiv = baseGridX / 1.5;
                float gridZDiv = baseGridZ / 1.5;
                float frequency = 1.0;
                float amplitude = 1.0;
                float value = 0.0;

                [loop]
                for (int octave = 0; octave < 4; octave++)
                {
                    if (octave >= octaveCount) break;
                    float contrib = simplexNoise2D(permTex, offsetTex, octave, worldX, worldZ, gridXDiv * frequency, gridZDiv * frequency);
                    value += contrib * (0.55 / amplitude);
                    frequency *= fq;
                    amplitude *= persistence;
                }

                return value;
            }

            float4 fragClimate(v2f i) : SV_Target
            {
                int x = clamp((int)floor(i.vertex.x), 0, _XSize - 1);
                int z = clamp((int)floor(i.vertex.y), 0, _ZSize - 1);
                float worldX = (float)(_ChunkX + x);
                float worldZ = (float)(_ChunkZ + z);

                float tempNoise = generateClimateField(_ClimatePermTex0, _ClimateOffsetTex0, _ClimateOctaveCount0, worldX, worldZ, 0.02500000037252903, 0.02500000037252903, 0.25, 0.5);
                float rainNoise = generateClimateField(_ClimatePermTex1, _ClimateOffsetTex1, _ClimateOctaveCount1, worldX, worldZ, 0.05000000074505806, 0.05000000074505806, 0.33333333333333331, 0.5);
                float modifierNoise = generateClimateField(_ClimatePermTex2, _ClimateOffsetTex2, _ClimateOctaveCount2, worldX, worldZ, 0.25, 0.25, 0.58823529411764708, 0.5);

                float modifier = modifierNoise * 1.1 + 0.5;
                float finalTemp = (tempNoise * 0.15 + 0.7) * 0.99 + modifier * 0.01;
                float finalRain = (rainNoise * 0.15 + 0.5) * 0.998 + modifier * 0.002;
                finalTemp = 1.0 - (1.0 - finalTemp) * (1.0 - finalTemp);
                finalTemp = clamp(finalTemp, 0.0, 1.0);
                finalRain = clamp(finalRain, 0.0, 1.0);

                int biomeTempIndex = clamp((int)(finalTemp * 63.0), 0, 63);
                int biomeRainIndex = clamp((int)(finalRain * 63.0), 0, 63);
                float2 biomeLookupUv = float2(((float)biomeTempIndex + 0.5) / 64.0, ((float)biomeRainIndex + 0.5) / 64.0);
                int biomeId = (int)round(tex2Dlod(_ClimateBiomeLookupTex, float4(biomeLookupUv, 0, 0)).r * 255.0);
                return float4(finalTemp, finalRain, biomeId / 255.0, 1.0);
            }
            ENDCG
        }
    }
}
