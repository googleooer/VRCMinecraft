// TerrainCompute.shader

// Beta 1.7.3 Terrain Generation on the GPU.

Shader "VRCMinecraft/TerrainCompute"

{

  // I added these properties myself, Gemini, I incorporated your stuff into my shader

  Properties

  {

    // Current chunk x y and z

    _Udon_ChunkX ("Chunk X", Int) = 0

    _Udon_ChunkY ("Chunk Y", Int) = 0

    _Udon_ChunkZ ("Chunk Z", Int) = 0

    // World seed int, used for noise generation

    _Udon_WorldSeed ("World Seed", Int) = 0

    // Sea level float, used for terrain generation

    _Udon_SeaLevel ("Sea Level", Float) = 64

    // Terrain multiplier float, used for terrain generation

    _Udon_TerrainMultiplier ("Terrain Multiplier", Float) = 1

    // Chunk size int, used for terrain generation

    _Udon_ChunkSizeXZ ("Chunk Size XZ", Int) = 16

    // Chunk size int, used for terrain generation

    _Udon_ChunkSizeY ("Chunk Size Y", Int) = 16

    // octaves for the min and max limit noise

    _Udon_MinMaxLimitNoise_Octaves ("Min/Max Limit Noise Octaves", Int) = 16

    // h and v scale for the min and max limit noise, vector2.

    [ShowAsVector2] _Udon_MinMaxLimitNoise_Scale ("Min/Max Limit Noise Scale", Vector) = (684.412, 684.412, 0.0, 0.0)

    // octaves for the scale noise

    _Udon_ScaleNoise_Octaves ("Scale Noise Octaves", Int) = 8

    // h and v scale for the scale noise, vector2.

    [ShowAsVector2] _Udon_ScaleNoise_Scale ("Scale Noise Scale", Vector) = (80.0, 160.0, 0.0, 0.0)

    // octaves for the main noise

    _Udon_MainNoise_Octaves ("Main Noise Octaves", Int) = 8

    // h and v scale for the main noise, vector2.

    [ShowAsVector2] _Udon_MainNoise_Scale ("Main Noise Scale", Vector) = (80.0, 160.0, 0.0, 0.0)

    // octaves for the surface noise

    _Udon_SurfaceNoise_Octaves ("Surface Noise Octaves", Int) = 4

    // h and v scale for the surface noise, vector2.

    [ShowAsVector2] _Udon_SurfaceNoise_Scale ("Surface Noise Scale", Vector) = (200.0, 200.0, 0.0, 0.0)

    // Permutation texture for noise generation (256x2 R8 texture)

    [NoScaleOffset] _Udon_PermutationTex ("Permutation Texture", 2D) = "white" {}

  }

 

  SubShader

  {

    Tags { "RenderType"="Opaque" }



    // Debug pass, this is an example that returns a water wave with a stone floor below it.

    // Uncomment when implementing the actual terrain generation.

    Pass

    {

      CGPROGRAM

      #pragma vertex vert

      #pragma fragment frag



      #include "UnityCG.cginc"

      // Include the utility file that contains the conversion functions

      #include "Assets/VRCMinecraft/Code/VoxelEngine/GPU_Attempt_3/TerrainUtils.cginc"



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



      float _Udon_ChunkSizeXZ;

      float _Udon_ChunkSizeY;

      float _Udon_ChunkX;

      float _Udon_ChunkY;

      float _Udon_ChunkZ;



      // The vertex shader is very simple. It just prepares the data

      // for the fragment shader. We are rendering a simple quad

      // that covers the entire RenderTexture.

      v2f vert (appdata v)

      {

        v2f o;

        o.vertex = UnityObjectToClipPos(v.vertex);

        o.uv = v.uv;

        return o;

      }



      // Structure to pass generation state between functions

      struct TerrainState

      {

        fixed3 block_pos;

        fixed3 world_pos;

        uint blockID;

      };

     

      // --- Beta 1.7.3 Terrain Generation ---

     

      // These properties are defined in the shader but used in the cginc file

      // so they are redeclared here for clarity.

      int _Udon_MinMaxLimitNoise_Octaves;

      float4 _Udon_MinMaxLimitNoise_Scale;

      int _Udon_MainNoise_Octaves;

      float4 _Udon_MainNoise_Scale;

      int _Udon_ScaleNoise_Octaves;

      float4 _Udon_ScaleNoise_Scale;

      int _Udon_SurfaceNoise_Octaves;

      float4 _Udon_SurfaceNoise_Scale;

      float _Udon_SeaLevel;

     

      void GenerateBeta173Terrain(inout TerrainState state)

      {

        // 1. Sample the five core noise generators, scaling the coordinates

        // according to the parameters from the research document.

        // Note: The scales are divisors.

       

        // minLimit and maxLimit use the same parameters

        float3 minMaxNoisePos = state.world_pos / float3(_Udon_MinMaxLimitNoise_Scale.x, _Udon_MinMaxLimitNoise_Scale.y, _Udon_MinMaxLimitNoise_Scale.x);

        float minLimit = mcn_generate_octave_noise(minMaxNoisePos, _Udon_MinMaxLimitNoise_Octaves);

        float maxLimit = mcn_generate_octave_noise(minMaxNoisePos, _Udon_MinMaxLimitNoise_Octaves);

       

        // mainNoise is the primary 3D sculptor

        float3 mainNoisePos = state.world_pos / float3(_Udon_MainNoise_Scale.x, _Udon_MainNoise_Scale.y, _Udon_MainNoise_Scale.x);

        float mainNoise = mcn_generate_octave_noise(mainNoisePos, _Udon_MainNoise_Octaves);

       

        // scaleNoise is 2D, controlling the blend between min and max limits

        float2 scaleNoisePos = state.world_pos.xz / _Udon_ScaleNoise_Scale.xy;

        float scaleNoise = mcn_generate_octave_noise(float3(scaleNoisePos.x, scaleNoisePos.y, 0.0), _Udon_ScaleNoise_Octaves);

       

        // surfaceNoise is 2D, adding detail to the surface height

        float2 surfaceNoisePos = state.world_pos.xz / _Udon_SurfaceNoise_Scale.xy;

        float surfaceNoise = mcn_generate_octave_noise(float3(surfaceNoisePos.x, surfaceNoisePos.y, 0.0), _Udon_SurfaceNoise_Octaves);

       

        // 2. Combine the noise values to calculate final density.

       

        // First, interpolate between min and max limits using scaleNoise.

        // The result of scaleNoise is [-1, 1], so we map it to [0, 1] to use as a lerp factor.

        float T = (scaleNoise + 1.0) / 2.0;

        float baseDensity = lerp(minLimit, maxLimit, T);

       

        // Add the main noise to the interpolated base.

        float density = baseDensity + mainNoise;



        // 3. Apply a height gradient to pull terrain towards sea level.

        // This is a simplified interpretation of the document's description.

        float heightFactor = (_Udon_SeaLevel - state.world_pos.y);

        float gradient;



        if (heightFactor > 0)

        {

          // Below sea level, slightly increase density to make things more solid.

          gradient = heightFactor * 0.1;

        }

        else

        {

          // Above sea level, strongly decrease density to carve out air.

          // The effect intensifies with height.

          gradient = heightFactor * 0.5;

        }

       

        density += gradient;



        // Also subtract the surface noise to add roughness to the ground level.

        // Dividing by 8.0 is a value from analysis of the original code.

        density -= surfaceNoise / 8.0;



        // 4. Set final block ID based on density.

        if (density > 0)

        {

          state.blockID = 1; // Stone

        }

        else

        {

          state.blockID = 0; // Air

        }



        // Finally, place water at sea level if the block is currently air.

        if (state.world_pos.y < _Udon_SeaLevel && state.blockID == 0)

        {

          state.blockID = 9; // Water

        }

      }



      // The fragment shader runs for every pixel in the RenderTexture.

      // The 'input.vertex' parameter here, thanks to the SV_POSITION

      // semantic, holds the x and y coordinates of the current pixel.

      fixed4 frag (v2f input) : SV_Target

      {

        // Initialize terrain generation state

        TerrainState state;

        state.blockID = 0; // Default to Air

       

        // --- Decode 2D Pixel Coordinate to 3D Block Coordinate ---

        state.block_pos = mcn_pixel_to_block(input.vertex.xy, _Udon_ChunkSizeXZ);



        // Get the current chunk position

        fixed3 chunk_pos = fixed3(_Udon_ChunkX, _Udon_ChunkY, _Udon_ChunkZ);

       

        // Convert block position to world position

        state.world_pos = mcn_block_to_world(state.block_pos, chunk_pos, _Udon_ChunkSizeXZ, _Udon_ChunkSizeY);



        // --- Execute Beta 1.7.3 Terrain Generation ---

        GenerateBeta173Terrain(state);

       

        // --- Encode Block ID for R8 Texture ---

        float encodedValue = (float)state.blockID / 255.0;



        // Return the final color. Only the .r (red) channel will be stored.

        return fixed4(encodedValue, 0, 0, 1);

      }

      ENDCG

    }

  }

}