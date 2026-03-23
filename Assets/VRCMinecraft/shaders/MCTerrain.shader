Shader "Unlit/MCTerrain_Combined" // MODIFIED: Renamed shader
{
    Properties
    {
        [KeywordEnum(Opaque, Cutout, Transparent)] _SurfaceType ("Surface Type", Float) = 0 // ADDED: Surface type dropdown
        _MainTex ("Texture Array", 2DArray) = "white" {} // MODIFIED: Changed to 2DArray
        _TintMask ("Tint Mask Array", 2DArray) = "black" {} // MODIFIED: Changed to 2DArray
        [HideInInspector] _WaterStillTex ("Water Still Texture", 2D) = "white" {}
        [HideInInspector] _WaterFlowTex ("Water Flow Texture", 2D) = "white" {}
        [HideInInspector] _WaterStillSlice ("Water Still Slice", Float) = -1
        [HideInInspector] _WaterFlowSlice ("Water Flow Slice", Float) = -1
        _SkyLight ("Sky Light", Integer) = 16
        _DayProgress("Day Progress", Range(0,1)) = 0
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5 // ADDED: Alpha cutoff for Cutout mode
        _UseVertexLight ("Use Vertex Light", Float) = 1
        [HideInInspector] _UseGpuExactAo ("Use GPU Exact AO", Float) = 0
        
        // MINECRAFT-STYLE RADIAL FOG PROPERTIES
        _FogColor ("Fog Color", Color) = (0.5, 0.6, 0.7, 1.0) // Default sky fog color
        _FogDensity ("Fog Density", Range(0.0, 0.1)) = 0.02 // Controls fog thickness
        _FogStart ("Fog Start Distance", Float) = 32.0 // Distance where fog begins (25% of far plane)
        _FogEnd ("Fog End Distance", Float) = 128.0 // Distance where fog is fully opaque (far plane)
        _FogMode ("Fog Mode", Range(0, 2)) = 0 // 0=Linear, 1=Exponential, 2=Exponential Squared

        // ADDED: Properties for render states (intended to be controlled by script/custom editor)
        [HideInInspector] _SrcBlend ("SrcBlend Mode", Int) = 1 // MODIFIED: Float to Int. Default: UnityEngine.Rendering.BlendMode.One
        [HideInInspector] _DstBlend ("DstBlend Mode", Int) = 0 // MODIFIED: Float to Int. Default: UnityEngine.Rendering.BlendMode.Zero
        [HideInInspector] _ZWrite ("ZWrite", Int) = 1       // MODIFIED: Float to Int. Default: On (1)
        [HideInInspector] _Cull ("Cull Mode", Int) = 2        // MODIFIED: Float to Int. Default: UnityEngine.Rendering.CullMode.Back (2)
        [HideInInspector] _OffsetFactor ("Offset Factor", Float) = 0 // ADDED: Depth offset factor
        [HideInInspector] _OffsetUnits ("Offset Units", Float) = 0 // ADDED: Depth offset units
    }
    SubShader
    {
        // IMPORTANT: For full transparency, Tags (especially "Queue") also need to change.
        // This shader change focuses on Blend/ZWrite/Cull state configurability.
        // A script/custom material editor would be needed to set these properties AND 
        // material.renderQueue / material.SetOverrideTag("RenderType", ...) based on _SurfaceType.
        Tags { "RenderType"="" "Queue"="Geometry" } // Base tags, will need to be overridden for Transparent by script/editor
        LOD 100

        Pass
        {
            // These states are now controlled by the material properties defined above.
            // A script or custom MaterialEditor should set these properties based on the _SurfaceType keyword.
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]
            Offset [_OffsetFactor], [_OffsetUnits]

            // Default states for Opaque/Cutout: ZWrite On, Cull Back, Blend Off
            // For Transparent mode, these need to be changed manually on the material:
            // Blend SrcAlpha OneMinusSrcAlpha, ZWrite Off, Cull Off (optional)

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag 
            #pragma target 3.0
            // make fog work  
            #pragma multi_compile_fog
            #pragma shader_feature_local _SURFACETYPE_OPAQUE _SURFACETYPE_CUTOUT _SURFACETYPE_TRANSPARENT // ADDED: Shader features for surface types


            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 uvw : TEXCOORD0; // MODIFIED: Changed to float3 for UV + Slice Index
                float3 normal : NORMAL;
                float4 color : COLOR; // ADDED: Vertex color input
            };

            struct v2f
            {
                float3 uvw : TEXCOORD0; // MODIFIED: Changed to float3 for UV + Slice Index
                UNITY_FOG_COORDS(2)
                float4 vertex : SV_POSITION;
                float3 normal: TEXCOORD1;
                fixed4 color : COLOR; // ADDED: Vertex color to pass to fragment shader
                float3 worldPos : TEXCOORD3; // ADDED: World position for radial fog calculation
            };

            UNITY_DECLARE_TEX2DARRAY(_MainTex); // MODIFIED: Declared as Texture2DArray
            float4 _MainTex_ST;

            UNITY_DECLARE_TEX2DARRAY(_TintMask); // MODIFIED: Declared as Texture2DArray
            sampler2D _WaterStillTex;
            sampler2D _WaterFlowTex;
            float _WaterStillSlice;
            float _WaterFlowSlice;

            #if defined(_SURFACETYPE_CUTOUT) // ADDED: Conditional declaration
            float _Cutoff; // ADDED: Declaration for Alpha Cutoff
            #endif // ADDED: Conditional declaration

            int _SkyLight;
            half _DayProgress;
            
            // MINECRAFT-STYLE RADIAL FOG VARIABLES
            fixed4 _FogColor;
            half _FogDensity;
            half _FogStart;
            half _FogEnd;
            half _FogMode;
            float _UseVertexLight;
            float _UseGpuExactAo;
            sampler2D _UdonVRCM_GpuLightAtlas;
            sampler2D _UdonVRCM_GpuBlockAtlas;
            sampler2D _UdonVRCM_GpuSlotLookup;
            sampler2D _UdonVRCM_GpuBlockProps;
            float4 _UdonVRCM_GpuAtlasInfo;
            float4 _UdonVRCM_GpuWorldInfo;
            float4 _UdonVRCM_GpuChunkInfo;
            float4 _UdonVRCM_GpuVoxelOffset;
            float _UdonVRCM_GpuEnabled;

            #include "MCTerrainGpuExactAo.cginc"

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.normal = v.normal;
                o.uvw.xy = TRANSFORM_TEX(v.uvw.xy, _MainTex); // MODIFIED: Transform only xy
                o.uvw.z = v.uvw.z; // MODIFIED: Pass z (slice index) through
                o.color = v.color; // ADDED: Pass vertex color to fragment shader
                
                // Calculate world position for radial fog (Quest-compatible, no depth buffer needed)
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                UNITY_TRANSFER_FOG(o,o.vertex);
                return o;
            }

            fixed calcBrightness(fixed3 normal)
            {
                // Cross-shaped blocks have diagonal normals where no single component > 0.9
                fixed maxComp = max(max(abs(normal.x), abs(normal.y)), abs(normal.z));
                if (maxComp < 0.9) return 1.0;

                if (normal.y > 0.5) return 1.0;
                if (normal.y < -0.5) return 0.5;
                if (abs(normal.x) > abs(normal.z)) return 0.6;
                return 0.8;
            }
            
            // MINECRAFT-STYLE RADIAL FOG CALCULATION
            // Based on Minecraft Beta 1.7.3 fog implementation
            // Supports Linear, Exponential, and Exponential Squared modes
            half calcMinecraftFog(float3 worldPos)
            {
                // Calculate radial distance from camera (Quest-compatible)
                float3 viewDir = worldPos - _WorldSpaceCameraPos;
                half distance = length(viewDir);
                
                half fogFactor = 1.0;
                
                // Minecraft fog modes (matching EntityRenderer.java setupFog method)
                if (_FogMode < 0.5) // Linear fog (default Minecraft terrain fog)
                {
                    // Linear fog: fogStart = farPlane * 0.25, fogEnd = farPlane
                    // This creates the classic Minecraft fog that starts at 25% of render distance
                    fogFactor = saturate((_FogEnd - distance) / (_FogEnd - _FogStart));
                }
                else if (_FogMode < 1.5) // Exponential fog (water, lava, clouds)
                {
                    // Exponential fog: density = 0.1 for water/clouds, 2.0 for lava
                    fogFactor = exp(-_FogDensity * distance);
                }
                else // Exponential Squared fog (alternative exponential mode)
                {
                    // Exponential Squared: more gradual falloff
                    fogFactor = exp(-_FogDensity * _FogDensity * distance * distance);
                }
                
                return saturate(fogFactor);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = UNITY_SAMPLE_TEX2DARRAY(_MainTex, i.uvw).rgba;
                fixed4 tintInput = UNITY_SAMPLE_TEX2DARRAY(_TintMask, i.uvw);
                fixed4 waterStill = tex2D(_WaterStillTex, i.uvw.xy).rgba;
                fixed4 waterFlow = tex2D(_WaterFlowTex, i.uvw.xy).rgba;
                fixed stillWeight = saturate(1.0 - abs(i.uvw.z - _WaterStillSlice) * 4.0);
                fixed flowWeight = saturate(1.0 - abs(i.uvw.z - _WaterFlowSlice) * 4.0) * (1.0 - stillWeight);
                fixed waterWeight = saturate(stillWeight + flowWeight);
                fixed4 waterCol = lerp(waterFlow, waterStill, stillWeight);
                col = lerp(col, waterCol, waterWeight);

                // Biome tinting: col.rgb (not col.r) so colored textures like flowers keep their color.
                // For grayscale textures (grass/leaves), R≈G≈B so col.rgb*biome == col.r*biome.
                fixed3 tintedColor = col.rgb * i.color.rgb;
                col.rgb = lerp(col.rgb, tintedColor, tintInput.a * (1.0 - waterWeight));
                
                half minLightLevel = 0.02;
                half lightBrightness;
                if (_UseGpuExactAo > 0.5)
                {
                    // Cross-shaped blocks have diagonal normals — AO basis doesn't apply.
                    bool isCrossNormal = max(max(abs(i.normal.x), abs(i.normal.y)), abs(i.normal.z)) < 0.9;
                    if (isCrossNormal)
                    {
                        half crossLight = gpuVoxelSampleLightBrightnessAtPosition(i.worldPos);
                        lightBrightness = max(minLightLevel, crossLight >= 0.0 ? crossLight : i.color.a);
                    }
                    else
                    {
                        half aoBrightness = gpuVoxelComputeExactAoBrightness(i.worldPos, i.normal);
                        lightBrightness = max(minLightLevel, aoBrightness >= 0.0 ? aoBrightness : i.color.a);
                    }
                }
                else if (_UseVertexLight > 0.5)
                {
                    lightBrightness = max(minLightLevel, i.color.a);
                }
                else
                {
                    half gpuLightBrightness = gpuVoxelSampleLightBrightness(i.worldPos, i.normal);
                    if (gpuLightBrightness >= 0.0)
                    {
                        lightBrightness = max(minLightLevel, gpuLightBrightness);
                    }
                    else
                    {
                        lightBrightness = max(minLightLevel, i.color.a);
                    }
                }
                
                half faceBrightness = calcBrightness(i.normal);
                half combinedBrightness = lightBrightness * faceBrightness;
                combinedBrightness = GammaToLinearSpace(combinedBrightness.xxx).x;
                
                col.rgb *= combinedBrightness;

                #if defined(_SURFACETYPE_CUTOUT)
                    clip(col.a - _Cutoff);
                #endif

                half fogFactor = calcMinecraftFog(i.worldPos);
                col.rgb = lerp(_FogColor.rgb, col.rgb, fogFactor);
                
                UNITY_APPLY_FOG(i.fogCoord, col);
                
                return col;
            }
            ENDCG
        }
    }
    CustomEditor "MCTerrainCombinedShaderGUI" // ADDED: Link to custom shader GUI
}
