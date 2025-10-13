Shader "VRCM/NoiseOctaveFixed"
{
    Properties
    {
        _PermTex ("Permutation Texture", 2D) = "white" {}
        _GradTex ("Gradient Texture", 2D) = "white" {}
        _AccumulationTex ("Accumulation Texture", 2D) = "black" {}
        _Octave ("Octave", Int) = 0
        _Frequency ("Frequency", Float) = 1.0
        _Amplitude ("Amplitude", Float) = 1.0
        _XCoord ("X Coordinate", Float) = 0.0
        _YCoord ("Y Coordinate", Float) = 0.0
        _ZCoord ("Z Coordinate", Float) = 0.0
        _XPos ("X Position", Float) = 0.0
        _YPos ("Y Position", Float) = 0.0
        _ZPos ("Z Position", Float) = 0.0
        _GridX ("Grid X", Float) = 1.0
        _GridY ("Grid Y", Float) = 1.0
        _GridZ ("Grid Z", Float) = 1.0
        _XSize ("X Size", Int) = 5
        _YSize ("Y Size", Int) = 33
        _ZSize ("Z Size", Int) = 5
        _Is2D ("Is 2D", Int) = 0
    }
    
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        
        Pass
        {
            Name "CLEAR"
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
            
            fixed4 frag (v2f i) : SV_Target
            {
                // Clear to black (0,0,0,1) in fixed-point format
                return float4(0, 0, 0, 1);
            }
            ENDCG
        }
        
        Pass
        {
            Name "OCTAVE"
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
            sampler2D _GradTex;
            int _Octave;
            float _Frequency;
            float _Amplitude;
            float _XCoord;
            float _YCoord;
            float _ZCoord;
            float _XPos;
            float _YPos;
            float _ZPos;
            float _GridX;
            float _GridY;
            float _GridZ;
            int _XSize;
            int _YSize;
            int _ZSize;
            int _Is2D;

            // Perlin noise gradient function
            float grad(int hash, float x, float y, float z)
            {
                int h = hash & 15;
                float4 gradVec = tex2Dlod(_GradTex, float4((float)h / 16.0, 0.5, 0, 0));
                return gradVec.r * x + gradVec.g * y + gradVec.b * z;
            }
            
            float grad2d(int hash, float x, float z)
            {
                int h = hash & 15;
                float4 gradVec = tex2Dlod(_GradTex, float4((float)h / 16.0, 0.5, 0, 0));
                return gradVec.r * x + gradVec.b * z;
            }
            
            // Minecraft fade function: 6t^5 - 15t^4 + 10t^3
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
            
            fixed4 frag (v2f i) : SV_Target
            {
                // Calculate 3D coordinates from UV
                float2 uv = i.uv;
                
                if (_Is2D == 1)
                {
                    // 2D noise (for noise6 and noise7)
                    float xIndex = floor(uv.x * _XSize);
                    float zIndex = floor(uv.y * _ZSize);
                    
                    // Apply grid scaling and coordinate offset
                    // CPU: generateNoiseArray(array, xPos, yPos, zPos, xSize, ySize, zSize, gridX, gridY, gridZ)
                    // where worldX = (xPos + dx) * gridX + xCoord
                    float worldX = (_XPos + xIndex) * _GridX + _XCoord;
                    float worldZ = (_ZPos + zIndex) * _GridZ + _ZCoord;
                    
                    // Floor to get integer coordinates
                    int intX = (int)floor(worldX);
                    int intZ = (int)floor(worldZ);
                    
                    // Get permutation indices
                    int p1 = intX & 255;
                    int p3 = intZ & 255;
                    
                    // Get relative coordinates within the cell
                    float relX = worldX - intX;
                    float relZ = worldZ - intZ;
                    
                    // Apply fade function
                    float fx = fade(relX);
                    float fz = fade(relZ);
                    
                    // Sample permutation texture
                    float4 perm1 = tex2D(_PermTex, float2((float)p1 / 256.0, 0.5));
                    float4 perm3 = tex2D(_PermTex, float2((float)p3 / 256.0, 0.5));
                    
                    int a1 = (int)(perm1.r * 255.0);
                    int a2 = (int)(tex2D(_PermTex, float2((float)a1 / 256.0, 0.5)).r * 255.0) + p3;
                    
                    int b1 = (int)(tex2D(_PermTex, float2((float)(p1 + 1) / 256.0, 0.5)).r * 255.0);
                    int b2 = (int)(tex2D(_PermTex, float2((float)b1 / 256.0, 0.5)).r * 255.0) + p3;
                    
                    // Sample gradient texture for hash values
                    float4 grad0 = tex2D(_GradTex, float2((float)(a2 & 15) / 16.0, 0.5));
                    float4 grad1 = tex2D(_GradTex, float2((float)(b2 & 15) / 16.0, 0.5));
                    float4 grad2 = tex2D(_GradTex, float2((float)((a2 + 1) & 15) / 16.0, 0.5));
                    float4 grad3 = tex2D(_GradTex, float2((float)((b2 + 1) & 15) / 16.0, 0.5));
                    
                    // Calculate gradients (gradient texture format: R=X, G=Y, B=Z, A=unused)
                    float g0 = grad0.r * relX + grad0.b * relZ;
                    float g1 = grad1.r * (relX - 1.0) + grad1.b * relZ;
                    float val1 = g0 + fx * (g1 - g0);
                    
                    float g2 = grad2.r * relX + grad2.b * (relZ - 1.0);
                    float g3 = grad3.r * (relX - 1.0) + grad3.b * (relZ - 1.0);
                    float val2 = g2 + fx * (g3 - g2);
                    
                    float value = val1 + fz * (val2 - val1);
                    value *= _Amplitude;
                    
                    // Add current octave
                    float prevValue = tex2Dlod(_AccumulationTex, float4(uv, 0, 0)).r;
                    value = prevValue + value;
                    
                    return float4(value, 0, 0, 1);
                }
                else
                {
                    // 3D noise (for noise1, noise2, noise3)
                    // For 3D noise, we need to map UV coordinates to 3D space
                    // Assuming the render texture is laid out as (xSize*zSize) x ySize
                    float xStride = (float)_XSize;
                    float zStride = (float)_ZSize;
                    float totalXZ = xStride * zStride;
                    float flatIndex = floor(uv.x * totalXZ);
                    float zIndex = floor(flatIndex / xStride);
                    float xIndex = flatIndex - zIndex * xStride;
                    float yIndex = floor(uv.y * _YSize);
                    
                    // Apply grid scaling and coordinate offset
                    // CPU: generateNoiseArray(array, xPos, yPos, zPos, xSize, ySize, zSize, gridX, gridY, gridZ)
                    // where worldX = (xPos + dx) * gridX + xCoord
                    float worldX = (_XPos + xIndex) * _GridX + _XCoord;
                    float worldY = (_YPos + yIndex) * _GridY + _YCoord;
                    float worldZ = (_ZPos + zIndex) * _GridZ + _ZCoord;
                    
                    // Floor to get integer coordinates
                    int intX = (int)floor(worldX);
                    int intY = (int)floor(worldY);
                    int intZ = (int)floor(worldZ);
                    
                    // Get permutation indices
                    int p1 = intX & 255;
                    int p2 = intY & 255;
                    int p3 = intZ & 255;
                    
                    // Get relative coordinates within the cell
                    float relX = worldX - intX;
                    float relY = worldY - intY;
                    float relZ = worldZ - intZ;
                    
                    // Apply fade function
                    float fx = fade(relX);
                    float fy = fade(relY);
                    float fz = fade(relZ);
                    
                    // Sample permutation texture
                    float4 perm1 = tex2D(_PermTex, float2((float)p1 / 256.0, 0.5));
                    float4 perm2 = tex2D(_PermTex, float2((float)p2 / 256.0, 0.5));
                    float4 perm3 = tex2D(_PermTex, float2((float)p3 / 256.0, 0.5));
                    
                    int a1 = (int)(perm1.r * 255.0) + p2;
                    int a2 = (int)(tex2D(_PermTex, float2((float)a1 / 256.0, 0.5)).r * 255.0) + p3;
                    int a3 = (int)(tex2D(_PermTex, float2((float)(a1 + 1) / 256.0, 0.5)).r * 255.0) + p3;
                    int b1 = (int)(tex2D(_PermTex, float2((float)(p1 + 1) / 256.0, 0.5)).r * 255.0) + p2;
                    int b2 = (int)(tex2D(_PermTex, float2((float)b1 / 256.0, 0.5)).r * 255.0) + p3;
                    int b3 = (int)(tex2D(_PermTex, float2((float)(b1 + 1) / 256.0, 0.5)).r * 255.0) + p3;
                    
                    // Sample gradient texture for hash values
                    float4 grad0 = tex2D(_GradTex, float2((float)(a2 & 15) / 16.0, 0.5));
                    float4 grad1 = tex2D(_GradTex, float2((float)(b2 & 15) / 16.0, 0.5));
                    float4 grad2 = tex2D(_GradTex, float2((float)(a3 & 15) / 16.0, 0.5));
                    float4 grad3 = tex2D(_GradTex, float2((float)(b3 & 15) / 16.0, 0.5));
                    float4 grad4 = tex2D(_GradTex, float2((float)((a2 + 1) & 15) / 16.0, 0.5));
                    float4 grad5 = tex2D(_GradTex, float2((float)((b2 + 1) & 15) / 16.0, 0.5));
                    float4 grad6 = tex2D(_GradTex, float2((float)((a3 + 1) & 15) / 16.0, 0.5));
                    float4 grad7 = tex2D(_GradTex, float2((float)((b3 + 1) & 15) / 16.0, 0.5));
                    
                    // Calculate gradients (gradient texture format: R=X, G=Y, B=Z, A=unused)
                    float g0 = grad0.r * relX + grad0.g * relY + grad0.b * relZ;
                    float g1 = grad1.r * (relX - 1.0) + grad1.g * relY + grad1.b * relZ;
                    float d13 = g0 + fx * (g1 - g0);
                    
                    float g2 = grad2.r * relX + grad2.g * (relY - 1.0) + grad2.b * relZ;
                    float g3 = grad3.r * (relX - 1.0) + grad3.g * (relY - 1.0) + grad3.b * relZ;
                    float d15 = g2 + fx * (g3 - g2);
                    
                    float g4 = grad4.r * relX + grad4.g * relY + grad4.b * (relZ - 1.0);
                    float g5 = grad5.r * (relX - 1.0) + grad5.g * relY + grad5.b * (relZ - 1.0);
                    float d16 = g4 + fx * (g5 - g4);
                    
                    float g6 = grad6.r * relX + grad6.g * (relY - 1.0) + grad6.b * (relZ - 1.0);
                    float g7 = grad7.r * (relX - 1.0) + grad7.g * (relY - 1.0) + grad7.b * (relZ - 1.0);
                    float d18 = g6 + fx * (g7 - g6);
                    
                    // Final interpolation
                    float val1 = d13 + fy * (d15 - d13);
                    float val2 = d16 + fy * (d18 - d16);
                    float value = val1 + fz * (val2 - val1);
                    value *= _Amplitude;
                    
                    // Add current octave
                    float prevValue = tex2Dlod(_AccumulationTex, float4(uv, 0, 0)).r;
                    value = prevValue + value;
                    
                    return float4(value, 0, 0, 1);
                }
            }
            ENDCG
        }
    }
}
