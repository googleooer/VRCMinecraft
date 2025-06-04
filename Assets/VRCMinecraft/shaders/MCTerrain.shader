Shader "Unlit/MCTerrain_Combined" // MODIFIED: Renamed shader
{
    Properties
    {
        [KeywordEnum(Opaque, Cutout, Transparent)] _SurfaceType ("Surface Type", Float) = 0 // ADDED: Surface type dropdown
        _MainTex ("Texture Array", 2DArray) = "white" {} // MODIFIED: Changed to 2DArray
        _TintMask ("Tint Mask Array", 2DArray) = "black" {} // MODIFIED: Changed to 2DArray
        _BiomeColor ("Biome Color", Color) = (1,1,1,0)
        _SkyLight ("Sky Light", Integer) = 16
        _DayProgress("Day Progress", Range(0,1)) = 0
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5 // ADDED: Alpha cutoff for Cutout mode

        // ADDED: Properties for render states (intended to be controlled by script/custom editor)
        [HideInInspector] _SrcBlend ("SrcBlend Mode", Int) = 1 // MODIFIED: Float to Int. Default: UnityEngine.Rendering.BlendMode.One
        [HideInInspector] _DstBlend ("DstBlend Mode", Int) = 0 // MODIFIED: Float to Int. Default: UnityEngine.Rendering.BlendMode.Zero
        [HideInInspector] _ZWrite ("ZWrite", Int) = 1       // MODIFIED: Float to Int. Default: On (1)
        [HideInInspector] _Cull ("Cull Mode", Int) = 2        // MODIFIED: Float to Int. Default: UnityEngine.Rendering.CullMode.Back (2)
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

            // Default states for Opaque/Cutout: ZWrite On, Cull Back, Blend Off
            // For Transparent mode, these need to be changed manually on the material:
            // Blend SrcAlpha OneMinusSrcAlpha, ZWrite Off, Cull Off (optional)

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag 
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
            };

            UNITY_DECLARE_TEX2DARRAY(_MainTex); // MODIFIED: Declared as Texture2DArray
            float4 _MainTex_ST;

            UNITY_DECLARE_TEX2DARRAY(_TintMask); // MODIFIED: Declared as Texture2DArray

            #if defined(_SURFACETYPE_CUTOUT) // ADDED: Conditional declaration
            float _Cutoff; // ADDED: Declaration for Alpha Cutoff
            #endif // ADDED: Conditional declaration

            int _SkyLight;
            half _DayProgress;

            fixed4 _BiomeColor;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.normal = v.normal;
                o.uvw.xy = TRANSFORM_TEX(v.uvw.xy, _MainTex); // MODIFIED: Transform only xy
                o.uvw.z = v.uvw.z; // MODIFIED: Pass z (slice index) through
                o.color = v.color; // ADDED: Pass vertex color to fragment shader
                UNITY_TRANSFER_FOG(o,o.vertex);
                return o;
            }

            fixed calcBrightness(fixed3 normal)
            {
                fixed brightness;
                if (normal.y > 0.5) // Top face
                {
                    brightness = 1.0;
                }
                else if (normal.y < -0.5) // Bottom face
                {
                    brightness = 0.2;
                }
                else if (normal.x > 0.5 || normal.x < -0.5) // Left and right faces
                {
                    brightness = 0.6;
                }
                else if (normal.z > 0.5 || normal.z < -0.5) // Front and back faces
                {
                    brightness = 0.4;
                }
                else // Should not happen with axis-aligned cubes, but as a fallback
                {
                    brightness = 0.5;
                }

                return brightness;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = UNITY_SAMPLE_TEX2DARRAY(_MainTex, i.uvw).rgba;
                fixed4 tintInput = UNITY_SAMPLE_TEX2DARRAY(_TintMask, i.uvw); // Sample tint mask

                // Apply tint to RGB
                // The tint color is (_TintMask.rgb * _BiomeColor.rgb)
                // The amount of tint applied is based on _TintMask.a
                col.rgb = lerp(col.rgb, tintInput.rgb * _BiomeColor.rgb, tintInput.a);
                
                half minLightLevel = 0.02;
                float dayNightTransition;

                if (_DayProgress < 0.0417) { // 0 to 1000 ticks (approx 1/24th of a day if 24000 ticks = 1 day)
                    dayNightTransition = (_DayProgress / 0.0417);
                } else if (_DayProgress > 0.5 && _DayProgress < 0.5417) { // 12000 to 13000 ticks
                    dayNightTransition = (1 - ((_DayProgress - 0.5) / 0.0417));
                } else if (_DayProgress <= 0.5) { // Daytime
                    dayNightTransition = 1;
                } else { // Nighttime
                    dayNightTransition = 0;
                }
                
                // Apply sky light and day/night transition
                col.rgb *= max(minLightLevel,(float)((_SkyLight+1)*dayNightTransition)/16);
                
                // Apply normal-based brightness (face lighting)
                col.rgb *= calcBrightness(i.normal);

                // ADDED: Apply Vertex AO
                // The vertex color's RGB channels store the AO value (e.g., grayscale).
                // We multiply the current color by the AO color.
                col.rgb *= i.color.rgb;

                #if defined(_SURFACETYPE_CUTOUT) // ADDED: Conditional clip for Cutout mode
                    clip(col.a - _Cutoff);
                #elif defined(_SURFACETYPE_TRANSPARENT)
                    // For Transparent mode, modulate the main texture's alpha by _BiomeColor.a
                    col.a *= _BiomeColor.a;
                #endif

                // apply fog
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
    CustomEditor "MCTerrainCombinedShaderGUI" // ADDED: Link to custom shader GUI
}