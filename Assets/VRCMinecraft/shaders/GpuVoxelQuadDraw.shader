// GPU OFFLOAD #5: Voxel quad instanced renderer.
//
// Renders all faces of a chunk as instanced unit-quads. The face data is read from a
// "face buffer" texture produced by GpuVoxelFaceExtract (already in the project).
// Each instance unpacks 4 bytes of packed face data into a world-space quad:
//
//   pack[0]: x (4 bits) | y (4 bits) = position low bits within chunk
//   pack[1]: z (4 bits) | face (3 bits) | _ (1 bit)
//   pack[2]: blockId (8 bits)
//   pack[3]: ao (4 bits) | smoothLight (4 bits)
//
// CPU side: one DrawMeshInstancedIndirect call per chunk with a 1×1 quad mesh,
// instanceCount = visibleFaceCount, args buffer populated from the face-summary RT.
// CPU emits ZERO vertices to Mesh.SetVertices.
//
// The vertex shader builds each face's 4 corners by adding {0,0}..{1,1} to the
// base block position, then offsets along the face's normal to place the quad at the
// correct side. UVs are derived from the blockId + face direction (atlas lookup).
Shader "VRCM/GpuVoxelQuadDraw"
{
    Properties
    {
        _FaceBuffer  ("Face Buffer (packed quads)", 2D) = "black" {}
        _AtlasUVTex  ("Block UV Atlas (per-block per-face)", 2D) = "white" {}
        _MainTex     ("Terrain Atlas",   2D) = "white" {}
        _BiomeColorRT ("Biome Color RT (16x16)", 2D) = "white" {}
        _ChunkOriginX ("Chunk Origin X (world)", Float) = 0
        _ChunkOriginY ("Chunk Origin Y (world)", Float) = 0
        _ChunkOriginZ ("Chunk Origin Z (world)", Float) = 0
        _ChunkSizeXZ  ("Chunk Size XZ", Int) = 16
        _ChunkSizeY   ("Chunk Size Y",  Int) = 16
        _FaceBufferWidth ("Face Buffer Width", Int) = 4096
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "UnityInstancing.cginc"

            struct appdata
            {
                float4 vertex : POSITION;  // unit quad: {(0,0,0), (1,0,0), (0,1,0), (1,1,0)}
                float2 uv     : TEXCOORD0;
                // Read instance id directly via SV_InstanceID semantic. This is more
                // portable than relying on the UNITY_VERTEX_INPUT_INSTANCE_ID macro chain
                // (which only exposes `unity_InstanceID` after UNITY_SETUP_INSTANCE_ID and
                // requires the full instancing include path).
                uint instanceID : SV_InstanceID;
            };

            struct v2f
            {
                float4 vertex  : SV_POSITION;
                float2 uv      : TEXCOORD0;
                float3 tint    : TEXCOORD1; // ao * biomeColor
            };

            sampler2D _FaceBuffer;
            sampler2D _AtlasUVTex;
            sampler2D _MainTex;
            sampler2D _BiomeColorRT;
            float _ChunkOriginX, _ChunkOriginY, _ChunkOriginZ;
            int _ChunkSizeXZ, _ChunkSizeY;
            int _FaceBufferWidth;

            // Sample 4 packed bytes from the face buffer for a given instance.
            void GetFacePacked(uint instanceId, out int px, out int py, out int pz, out int pw)
            {
                int row = (int)instanceId / _FaceBufferWidth;
                int col = (int)instanceId - row * _FaceBufferWidth;
                float2 uv = float2(((float)col + 0.5) / (float)_FaceBufferWidth, ((float)row + 0.5) / 4096.0);
                float4 packed = tex2Dlod(_FaceBuffer, float4(uv, 0, 0));
                px = (int)round(packed.r * 255.0);
                py = (int)round(packed.g * 255.0);
                pz = (int)round(packed.b * 255.0);
                pw = (int)round(packed.a * 255.0);
            }

            // Face directions: 0=-X, 1=+X, 2=-Y, 3=+Y, 4=-Z, 5=+Z
            void GetFaceBasis(int face, out float3 normal, out float3 tangent, out float3 bitangent, out float3 offset)
            {
                if      (face == 0) { normal = float3(-1,0,0); tangent = float3(0,0,1); bitangent = float3(0,1,0); offset = float3(0,0,0); }
                else if (face == 1) { normal = float3( 1,0,0); tangent = float3(0,0,1); bitangent = float3(0,1,0); offset = float3(1,0,0); }
                else if (face == 2) { normal = float3(0,-1,0); tangent = float3(1,0,0); bitangent = float3(0,0,1); offset = float3(0,0,0); }
                else if (face == 3) { normal = float3(0, 1,0); tangent = float3(1,0,0); bitangent = float3(0,0,1); offset = float3(0,1,0); }
                else if (face == 4) { normal = float3(0,0,-1); tangent = float3(1,0,0); bitangent = float3(0,1,0); offset = float3(0,0,0); }
                else                { normal = float3(0,0, 1); tangent = float3(1,0,0); bitangent = float3(0,1,0); offset = float3(0,0,1); }
            }

            v2f vert(appdata v)
            {
                v2f o;

                int px, py, pz, pw;
                GetFacePacked(v.instanceID, px, py, pz, pw);

                int bx = px & 0x0F;
                int by = (px >> 4) & 0x0F;
                int bz = pz & 0x0F;
                int face = (pz >> 4) & 0x07;
                int blockId = py;
                int ao = pw & 0x0F;
                int smoothLight = (pw >> 4) & 0x0F;

                float3 normal, tangent, bitangent, offset;
                GetFaceBasis(face, normal, tangent, bitangent, offset);

                // Unit quad vertex.uv ∈ {(0,0),(1,0),(1,1),(0,1)} drives corner selection.
                float3 worldPos = float3(_ChunkOriginX + (float)bx, _ChunkOriginY + (float)by, _ChunkOriginZ + (float)bz)
                                + offset
                                + tangent * v.uv.x
                                + bitangent * v.uv.y;

                o.vertex = UnityObjectToClipPos(float4(worldPos, 1));

                // UV lookup: _AtlasUVTex is a 256×6 table giving (u0,v0,u1,v1) per (blockId, face).
                float2 atlasUv = float2(((float)blockId + 0.5) / 256.0, ((float)face + 0.5) / 6.0);
                float4 uvRange = tex2Dlod(_AtlasUVTex, float4(atlasUv, 0, 0));
                o.uv = lerp(uvRange.xy, uvRange.zw, v.uv);

                // Biome tint: sample the chunk's biome color RT (16×16) at (bx, bz).
                float2 biomeUv = float2(((float)bx + 0.5) / (float)_ChunkSizeXZ, ((float)bz + 0.5) / (float)_ChunkSizeXZ);
                float3 biomeTint = tex2Dlod(_BiomeColorRT, float4(biomeUv, 0, 0)).rgb;

                // Combine smooth-light + AO into a single 0..1 scalar to multiply the tint.
                float light = (float)smoothLight / 15.0;
                float aoFactor = 0.6 + 0.4 * ((float)ao / 3.0);
                o.tint = biomeTint * light * aoFactor;

                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                col.rgb *= i.tint;
                return col;
            }
            ENDCG
        }
    }
}
