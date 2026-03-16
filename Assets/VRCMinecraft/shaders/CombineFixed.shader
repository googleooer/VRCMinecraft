Shader "VRCM/CombineFixed"
{
    Properties
    {
        _Noise1Tex ("Noise1 Texture", 2D) = "black" {}
        _Noise2Tex ("Noise2 Texture", 2D) = "black" {}
        _Noise3Tex ("Noise3 Texture", 2D) = "black" {}
        _Noise6Tex ("Noise6 Texture", 2D) = "black" {}
        _Noise7Tex ("Noise7 Texture", 2D) = "black" {}
        _TemperatureTex ("Temperature Texture", 2D) = "white" {}
        _RainfallTex ("Rainfall Texture", 2D) = "white" {}
        _XSize ("X Size", Int) = 5
        _YSize ("Y Size", Int) = 33
        _ZSize ("Z Size", Int) = 5
        _ChunkX ("Chunk X", Int) = 0
        _ChunkZ ("Chunk Z", Int) = 0
        _FlipXAxis ("Flip X Axis", Int) = 1
        _BuiltinOffsetX ("Builtin Offset X", Int) = -16
        _BuiltinOffsetZ ("Builtin Offset Z", Int) = 0
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
                // Clear to black (0,0,0,1) in fixed-point format
                return float4(0, 0, 0, 1);
            }
            ENDCG
        }
        
        Pass
        {
            Name "COMBINE"
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
            
            sampler2D _Noise1Tex;
            sampler2D _Noise2Tex;
            sampler2D _Noise3Tex;
            sampler2D _Noise6Tex;
            sampler2D _Noise7Tex;
            sampler2D _TemperatureTex;
            sampler2D _RainfallTex;
            int _XSize;
            int _YSize;
            int _ZSize;
            int _ChunkX;
            int _ChunkZ;
            int _FlipXAxis;
            int _BuiltinOffsetX;
            int _BuiltinOffsetZ;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            
            float4 frag (v2f i) : SV_Target
            {
                // Decompose the packed XZ index and Y from uv:
                // Given the packed output RT: width = _XSize * _ZSize, height = _YSize
                int width = _XSize * _ZSize;
                int xzIndex = clamp((int)floor(i.vertex.x), 0, width - 1);
                int y = clamp((int)floor(i.vertex.y), 0, _YSize - 1);
                
                // Recover x and z from the packed index (NOTE: modulo by _XSize, divide by _XSize):
                int x = xzIndex % _XSize;
                int z = xzIndex / _XSize;
                
                // UVs into the packed 3D textures (noise1/2/3) use the packed xzIndex + y:
                float2 uv3D = float2((xzIndex + 0.5) / (float)width,
                                   (y + 0.5)      / (float)_YSize);
                
                // UVs into the 2D textures (noise6/7) use (x,z) coordinates:
                float2 uv2D = float2((x + 0.5) / (float)_XSize,
                                   (z + 0.5) / (float)_ZSize);
                
                // Calculate biome coordinates for temperature/rainfall lookup
                int i2 = 16 / _XSize;
                int k2 = x * i2 + i2 / 2;
                int i3 = z * i2 + i2 / 2;
                
                // Sample temperature and rainfall
                float2 biomeUV = float2(((float)k2 + 0.5) / 16.0, ((float)i3 + 0.5) / 16.0);
                float temp = tex2Dlod(_TemperatureTex, float4(biomeUV, 0, 0)).r;
                float rain = tex2Dlod(_RainfallTex, float4(biomeUV, 0, 0)).r;
                
                // Calculate biome influence (exact CPU logic)
                float d3 = rain * temp;
                float d4 = 1.0 - d3;
                d4 *= d4;
                d4 *= d4;
                d4 = 1.0 - d4;
                
                // Sample noise6 (3D-packed with ySize=1)
                float4 noise6Sample = tex2Dlod(_Noise6Tex, float4(uv2D, 0, 0));
                float noise6Val = noise6Sample.r;
                
                float d5 = (noise6Val + 256.0) / 512.0;
                d5 *= d4;
                if (d5 > 1.0) d5 = 1.0;
                
                // Sample noise7 (3D-packed with ySize=1)
                float4 noise7Sample = tex2Dlod(_Noise7Tex, float4(uv2D, 0, 0));
                float noise7Val = noise7Sample.r;
                
                float d6 = noise7Val / 8000.0;
                if (d6 < 0.0) d6 = -d6 * 0.3;
                
                d6 = d6 * 3.0 - 2.0;
                if (d6 < 0.0)
                {
                    d6 *= 0.5;
                    if (d6 < -1.0) d6 = -1.0;
                    d6 *= 0.3571428571428571; // 1/2.8
                    d5 = 0.0;
                }
                else
                {
                    if (d6 > 1.0) d6 = 1.0;
                    d6 *= 0.125;
                }
                
                if (d5 < 0.0) d5 = 0.0;
                
                d5 += 0.5;
                d6 = (d6 * _YSize) / 16.0;
                float d7 = (_YSize * 0.5) + d6 * 4.0;
                
                // Sample 3D noise fields using packed coordinates
                float4 noise1Sample = tex2Dlod(_Noise1Tex, float4(uv3D, 0, 0));
                float4 noise2Sample = tex2Dlod(_Noise2Tex, float4(uv3D, 0, 0));
                float4 noise3Sample = tex2Dlod(_Noise3Tex, float4(uv3D, 0, 0));
                
                float noise1Val = noise1Sample.r;
                float noise2Val = noise2Sample.r;
                float noise3Val = noise3Sample.r;
                
                // Calculate density (exact CPU logic)
                float d9 = ((y - d7) * 12.0) / d5;
                if (d9 < 0.0) d9 *= 4.0;
                
                float d10 = noise1Val / 512.0;
                float d11 = noise2Val / 512.0;
                float d12 = (noise3Val * 0.1 + 1.0) * 0.5;
                
                float d8;
                if (d12 < 0.0) 
                    d8 = d10;
                else if (d12 > 1.0) 
                    d8 = d11;
                else 
                    d8 = d10 + (d11 - d10) * d12;
                
                d8 -= d9;
                
                // Apply height-based scaling
                int ySize_m4 = _YSize - 4;
                if (y > ySize_m4)
                {
                    float d13 = (y - ySize_m4) * 0.33333334;
                    d8 = d8 * (1.0 - d13) - 10.0 * d13;
                }
                
                // Debug visualization: R=x, G=z, B=y in [0,1]
                #if DEBUG_VIS
                return float4(
                    (x + 0.5) / _XSize,
                    (z + 0.5) / _ZSize,
                    (y + 0.5) / _YSize,
                    1.0);
                #endif
                
                return float4(d8, 0, 0, 1);
            }
            ENDCG
        }
    }
}
