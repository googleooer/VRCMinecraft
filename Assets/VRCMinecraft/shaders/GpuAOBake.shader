// GPU OFFLOAD #9: Per-vertex ambient occlusion baking.
//
// Beta-style smooth lighting AO at each block-corner vertex is determined by 3
// neighbor checks:
//   side1, side2 (the two block-edge neighbors of the corner)
//   corner       (the diagonal neighbor)
// AO level = (side1 && side2) ? 0 : 3 - (isSolid(side1) + isSolid(side2) + isSolid(corner)).
//
// We produce a "vertex AO" texture: for each (face, voxel, corner) the 2-bit AO value.
// The mesh shader samples this when emitting vertices. Output layout:
//   width:  chunkSizeXZ * 6  (one block-row, 6 faces)
//   height: chunkSizeY * chunkSizeXZ * 4  (Y-major then Z, 4 corners per voxel)
//   each pixel: 2-bit AO in R, plus per-corner packed sky+block light in G/B
//
// Sources from the sentinel RT built by GpuSentinelBorderCopy so neighbor-chunk
// lookups are O(1) lookups inside the same texture (no atlas walking per vertex).
Shader "VRCM/GpuAOBake"
{
    Properties
    {
        _SentinelTex ("Sentinel Block Tex", 2D) = "black" {}
        _LightTex    ("Sentinel Light Tex", 2D) = "black" {}
        _BlockPropsTex ("Block Properties (canBlockGrass etc.)", 2D) = "black" {}
        _ChunkSizeXZ ("Chunk Size XZ", Int) = 16
        _ChunkSizeY  ("Chunk Size Y",  Int) = 16
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            ZTest Always Cull Off ZWrite Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

            sampler2D _SentinelTex;
            sampler2D _LightTex;
            sampler2D _BlockPropsTex;
            int _ChunkSizeXZ;
            int _ChunkSizeY;

            v2f vert(appdata v)
            {
                v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o;
            }

            // Sample block ID at sentinel coords [-1..N] (we use +1 offset to write into the
            // sentinel buffer that spans 0..N+1).
            float SampleSentinelBlock(int sx, int sy, int sz)
            {
                int sxz = _ChunkSizeXZ + 2;
                int syt = _ChunkSizeY + 2;
                float u = ((float)sx + 0.5) / (float)sxz;
                float v = ((float)(sy * sxz + sz) + 0.5) / (float)(sxz * syt);
                return tex2Dlod(_SentinelTex, float4(u, v, 0, 0)).r * 255.0;
            }

            // Block-property lookup. _BlockPropsTex is a 256-pixel-wide table where
            // R = opacity (0..15) / 255.  G = canBlockGrass (0 or 1).
            bool IsSolidOpaque(float blockId)
            {
                if (blockId < 0.5) return false; // air
                float2 propUv = float2((blockId + 0.5) / 256.0, 0.5);
                float opacity = tex2Dlod(_BlockPropsTex, float4(propUv, 0, 0)).r * 15.0;
                return opacity > 13.5; // matches Beta's canBlockGrass / opacity-15 check
            }

            float SampleLight(int sx, int sy, int sz)
            {
                int sxz = _ChunkSizeXZ + 2;
                int syt = _ChunkSizeY + 2;
                float u = ((float)sx + 0.5) / (float)sxz;
                float v = ((float)(sy * sxz + sz) + 0.5) / (float)(sxz * syt);
                return tex2Dlod(_LightTex, float4(u, v, 0, 0)).r;
            }

            // AO formula matches CPU side: 3 - (count of solid neighbors), capped at 0 if
            // both side neighbors are solid. Range: 0..3 (3 = no occlusion).
            int AOValue(bool s1, bool s2, bool c)
            {
                if (s1 && s2) return 0;
                int n = (s1 ? 1 : 0) + (s2 ? 1 : 0) + (c ? 1 : 0);
                return 3 - n;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Decode (face, voxel, corner) from output pixel position.
                int px = clamp((int)floor(i.vertex.x), 0, _ChunkSizeXZ * 6 - 1);
                int py = clamp((int)floor(i.vertex.y), 0, _ChunkSizeY * _ChunkSizeXZ * 4 - 1);
                int face   = px / _ChunkSizeXZ;
                int lx     = px - face * _ChunkSizeXZ;
                int row    = py / 4;
                int corner = py - row * 4;
                int ly     = row / _ChunkSizeXZ;
                int lz     = row - ly * _ChunkSizeXZ;

                // Sentinel coords (block we are computing AO for, offset by +1).
                int sx = lx + 1, sy = ly + 1, sz = lz + 1;

                // Face normal directions: 0:+X 1:-X 2:+Y 3:-Y 4:+Z 5:-Z
                int nx = 0, ny = 0, nz = 0;
                if      (face == 0) nx =  1;
                else if (face == 1) nx = -1;
                else if (face == 2) ny =  1;
                else if (face == 3) ny = -1;
                else if (face == 4) nz =  1;
                else                nz = -1;

                // The 3 neighbor block coords for AO at this corner.
                // Tangent axes for each face: side1, side2 directions (in voxel space).
                int t1x = 0, t1y = 0, t1z = 0;
                int t2x = 0, t2y = 0, t2z = 0;
                if (face == 0 || face == 1) { t1y = 1; t2z = 1; }
                else if (face == 2 || face == 3) { t1x = 1; t2z = 1; }
                else { t1x = 1; t2y = 1; }

                // Corner sign: 0:(- -), 1:(+ -), 2:(- +), 3:(+ +).
                int s1 = ((corner & 1) == 0) ? -1 : 1;  // along tangent1
                int s2 = ((corner & 2) == 0) ? -1 : 1;  // along tangent2

                int side1X = sx + nx + s1 * t1x;
                int side1Y = sy + ny + s1 * t1y;
                int side1Z = sz + nz + s1 * t1z;
                int side2X = sx + nx + s2 * t2x;
                int side2Y = sy + ny + s2 * t2y;
                int side2Z = sz + nz + s2 * t2z;
                int cornerX = sx + nx + s1 * t1x + s2 * t2x;
                int cornerY = sy + ny + s1 * t1y + s2 * t2y;
                int cornerZ = sz + nz + s1 * t1z + s2 * t2z;

                bool side1Solid = IsSolidOpaque(SampleSentinelBlock(side1X, side1Y, side1Z));
                bool side2Solid = IsSolidOpaque(SampleSentinelBlock(side2X, side2Y, side2Z));
                bool cornerSolid = IsSolidOpaque(SampleSentinelBlock(cornerX, cornerY, cornerZ));

                int ao = AOValue(side1Solid, side2Solid, cornerSolid);

                // Average the lights of the 3 neighbor cells + the air cell in front (Beta
                // smooth lighting). This gives the vertex its final light without a CPU sample.
                float lFace = SampleLight(sx + nx, sy + ny, sz + nz);
                float lSide1 = side1Solid ? lFace : SampleLight(side1X, side1Y, side1Z);
                float lSide2 = side2Solid ? lFace : SampleLight(side2X, side2Y, side2Z);
                float lCorner = (side1Solid && side2Solid) ? lFace
                              : (cornerSolid ? lFace : SampleLight(cornerX, cornerY, cornerZ));
                float vertLight = (lFace + lSide1 + lSide2 + lCorner) * 0.25;

                // Pack: R = ao / 3 (2-bit), G = vertLight (8-bit), B/A unused.
                return float4(ao / 3.0, vertLight, 0, 1);
            }
            ENDCG
        }
    }
}
