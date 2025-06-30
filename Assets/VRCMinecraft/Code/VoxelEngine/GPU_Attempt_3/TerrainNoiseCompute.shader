Shader "VRCMinecraft/TerrainNoiseCompute"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        // Pass 0: Generate height map
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
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

            // Shader parameters
            int _ChunkX;
            int _ChunkY;
            int _ChunkZ;
            int _ChunkSizeXZ;
            int _ChunkSizeY;
            int _Seed;
            float _SeaLevel;
            float _TerrainMultiplier;
            
            sampler2D _MainTex;
            float4 _MainTex_ST;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }
            
            // Improved hash function for better randomness
            float hash(float3 p)
            {
                p = frac(p * float3(443.897, 441.423, 437.195));
                p += dot(p, p.yzx + 19.19);
                return frac((p.x + p.y) * p.z);
            }

            // 3D Simplex noise implementation
            float3 mod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float4 mod289(float4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float4 permute(float4 x) { return mod289(((x*34.0)+1.0)*x); }
            float4 taylorInvSqrt(float4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

            float simplexNoise3D(float3 v)
            {
                const float2 C = float2(1.0/6.0, 1.0/3.0);
                const float4 D = float4(0.0, 0.5, 1.0, 2.0);

                // First corner
                float3 i = floor(v + dot(v, C.yyy));
                float3 x0 = v - i + dot(i, C.xxx);

                // Other corners
                float3 g = step(x0.yzx, x0.xyz);
                float3 l = 1.0 - g;
                float3 i1 = min(g.xyz, l.zxy);
                float3 i2 = max(g.xyz, l.zxy);

                float3 x1 = x0 - i1 + C.xxx;
                float3 x2 = x0 - i2 + C.yyy;
                float3 x3 = x0 - D.yyy;

                // Permutations
                i = mod289(i);
                float4 p = permute(permute(permute(
                    i.z + float4(0.0, i1.z, i2.z, 1.0))
                    + i.y + float4(0.0, i1.y, i2.y, 1.0))
                    + i.x + float4(0.0, i1.x, i2.x, 1.0));

                // Gradients
                float n_ = 0.142857142857; // 1.0/7.0
                float3 ns = n_ * D.wyz - D.xzx;

                float4 j = p - 49.0 * floor(p * ns.z * ns.z);

                float4 x_ = floor(j * ns.z);
                float4 y_ = floor(j - 7.0 * x_);

                float4 x = x_ *ns.x + ns.yyyy;
                float4 y = y_ *ns.x + ns.yyyy;
                float4 h = 1.0 - abs(x) - abs(y);

                float4 b0 = float4(x.xy, y.xy);
                float4 b1 = float4(x.zw, y.zw);

                float4 s0 = floor(b0)*2.0 + 1.0;
                float4 s1 = floor(b1)*2.0 + 1.0;
                float4 sh = -step(h, float4(0.0, 0.0, 0.0, 0.0));

                float4 a0 = b0.xzyw + s0.xzyw*sh.xxyy;
                float4 a1 = b1.xzyw + s1.xzyw*sh.zzww;

                float3 p0 = float3(a0.xy, h.x);
                float3 p1 = float3(a0.zw, h.y);
                float3 p2 = float3(a1.xy, h.z);
                float3 p3 = float3(a1.zw, h.w);

                // Normalize gradients
                float4 norm = taylorInvSqrt(float4(dot(p0,p0), dot(p1,p1), dot(p2,p2), dot(p3,p3)));
                p0 *= norm.x;
                p1 *= norm.y;
                p2 *= norm.z;
                p3 *= norm.w;

                // Mix final noise value
                float4 m = max(0.6 - float4(dot(x0,x0), dot(x1,x1), dot(x2,x2), dot(x3,x3)), 0.0);
                m = m * m;
                return 42.0 * dot(m*m, float4(dot(p0,x0), dot(p1,x1), dot(p2,x2), dot(p3,x3)));
            }

            // Multi-octave noise (2D version)
            float octaveNoise2D(float2 pos, int octaves, float persistence, float scale)
            {
                float total = 0.0;
                float frequency = scale;
                float amplitude = 1.0;
                float maxValue = 0.0;
                
                for (int i = 0; i < octaves; i++)
                {
                    total += simplexNoise3D(float3(pos.x * frequency, 0, pos.y * frequency)) * amplitude;
                    maxValue += amplitude;
                    amplitude *= persistence;
                    frequency *= 2.0;
                }
                
                return total / maxValue;
            }

            // Multi-octave noise (3D version)
            float octaveNoise(float3 pos, int octaves, float persistence, float scale)
            {
                float total = 0.0;
                float frequency = scale;
                float amplitude = 1.0;
                float maxValue = 0.0;
                
                for (int i = 0; i < octaves; i++)
                {
                    total += simplexNoise3D(pos * frequency) * amplitude;
                    maxValue += amplitude;
                    amplitude *= persistence;
                    frequency *= 2.0;
                }
                
                return total / maxValue;
            }

            // Beta 1.7.3 style terrain generation
            float generateTerrainHeight(float3 worldPos)
            {
                // Add world seed offset
                float warpX = sin(_Seed * 0.1) * 100.0;
                float warpZ = cos(_Seed * 0.1) * 100.0;
                float2 warpedPos = float2(worldPos.x + warpX, worldPos.z + warpZ);

                // Base height from multiple noise octaves
                float baseNoise = octaveNoise2D(warpedPos / 684.412, 8, 0.55, 1.0);
                float detailNoise = octaveNoise2D(warpedPos / 171.103, 6, 0.5, 1.0);
                
                // Combine noises with different weights
                float height = baseNoise * 64.0 + detailNoise * 16.0;
                
                // Add base terrain level
                height += _SeaLevel + 8.0;
                
                // Mountain peaks
                float mountainNoise = octaveNoise2D(warpedPos / 1368.824, 4, 0.5, 1.0);
                if (mountainNoise > 0.4)
                {
                    float mountainHeight = (mountainNoise - 0.4) * 200.0;
                    height += mountainHeight * _TerrainMultiplier;
                }
                
                // Valleys and lowlands
                float valleyNoise = octaveNoise2D(warpedPos / 512.0, 4, 0.6, 1.0);
                if (valleyNoise < -0.3)
                {
                    height += valleyNoise * 32.0;
                }
                
                return clamp(height, 0.0, 255.0);
            }

            // Biome calculation
            float2 calculateBiome(float3 worldPos)
            {
                float biomeOffset = _Seed * 0.1;
                
                // Temperature (using 2D noise)
                float temp = simplexNoise3D(float3(worldPos.x * 0.025 + biomeOffset, 0, worldPos.z * 0.025 + biomeOffset));
                temp = (temp + 1.0) * 0.5; // Normalize to 0-1
                
                // Humidity (using 2D noise)
                float humidity = simplexNoise3D(float3(worldPos.x * 0.05 + 1000.0 + biomeOffset * 2.0, 0, worldPos.z * 0.05 + 1000.0 + biomeOffset * 2.0));
                humidity = (humidity + 1.0) * 0.5; // Normalize to 0-1
                humidity *= temp; // Humidity affected by temperature
                
                return float2(temp, humidity);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Calculate world position for this pixel, mapping UVs directly to chunk coords
                int x = (int)(i.uv.x * _ChunkSizeXZ);
                int z = (int)(i.uv.y * _ChunkSizeXZ);
                
                // Only process pixels that fall within the chunk's boundaries on the texture
                if (x >= _ChunkSizeXZ || z >= _ChunkSizeXZ)
                    return fixed4(0, 0, 0, 0);

                float3 worldPos = float3(
                    _ChunkX * _ChunkSizeXZ + x,
                    _ChunkY * _ChunkSizeY,
                    _ChunkZ * _ChunkSizeXZ + z
                );
                
                // Generate height
                float height = generateTerrainHeight(worldPos);
                float normalizedHeight = height / 255.0; // Normalize to 0-1
                
                // Generate biome data
                float2 biome = calculateBiome(worldPos);
                
                // Determine surface material hint (for later use)
                float materialHint = 0.0;
                if (height < _SeaLevel - 1) materialHint = 0.2; // Sand
                else if (biome.x > 0.95 && biome.y < 0.2) materialHint = 0.2; // Desert
                else if (biome.x < 0.2 && height > _SeaLevel + 20) materialHint = 0.8; // Snow/tundra
                else materialHint = 0.5; // Grass
                
                // Pack data into RGBA
                // R: Normalized height
                // G: Temperature
                // B: Humidity
                // A: Material hint
                return fixed4(normalizedHeight, biome.x, biome.y, materialHint);
            }
            ENDCG
        }
        
        // Pass 1: Process biome data (optional refinement pass)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
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
            float4 _MainTex_ST;
            int _ChunkSizeXZ;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Sample the height map texture, which now contains all generated data
                fixed4 generatedData = tex2D(_MainTex, i.uv);
                
                // This pass can be used for biome post-processing in the future.
                // For now, it just passes the data through.
                return generatedData;
            }
            ENDCG
        }
    }
}