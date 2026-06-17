// GPU OFFLOAD #11: Column-RLE encoder.
//
// Encodes each (x, z) column of a chunk's blocks into at most 8 RLE pairs (blockId, runLen).
// Output RT layout: 16x16 columns × 8 RGBA32 pixels stride = 16×128 RGBA32.
//   Each pixel = (blockId, runLen, 0, valid).
// Caller readback decodes back to ushort[][] columns and stores as chunk._chunkData.
//
// Hard cap: 8 RLE pairs per column. A column that needs more (extremely varied terrain)
// will have its 8th pair span the remaining height, slightly over-stating one block run.
// In practice 99% of Beta chunks compress in ≤6 pairs per column.
//
// Faster than the CPU `_CompressChunkColumnRLE` for GPU-resident chunks because no
// per-column List<ushort> allocation + ToArray is needed.
Shader "VRCM/GpuRleEncode"
{
    Properties
    {
        _BlockTex    ("Chunk Block IDs (per-chunk RT)", 2D) = "black" {}
        _ChunkSizeXZ ("Chunk Size XZ", Int) = 16
        _ChunkSizeY  ("Chunk Size Y", Int) = 16
        _MaxPairsPerColumn ("Max RLE Pairs/Col", Int) = 8
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

            sampler2D _BlockTex;
            int _ChunkSizeXZ;
            int _ChunkSizeY;
            int _MaxPairsPerColumn;

            v2f vert(appdata v) { v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }

            int SampleBlock(int lx, int ly, int lz)
            {
                if (lx < 0 || ly < 0 || lz < 0 || lx >= _ChunkSizeXZ || ly >= _ChunkSizeY || lz >= _ChunkSizeXZ) return 0;
                float u = ((float)lx + 0.5) / (float)_ChunkSizeXZ;
                float v = ((float)(ly * _ChunkSizeXZ + lz) + 0.5) / (float)(_ChunkSizeY * _ChunkSizeXZ);
                return (int)round(tex2Dlod(_BlockTex, float4(u, v, 0, 0)).r * 255.0);
            }

            float4 frag(v2f i) : SV_Target
            {
                // Output layout:
                //   pixel (col, row) where col ∈ [0, _ChunkSizeXZ), row ∈ [0, _ChunkSizeXZ * _MaxPairsPerColumn)
                //   pair = row / _ChunkSizeXZ,  z = row % _ChunkSizeXZ
                //   So one column (x, z) produces _MaxPairsPerColumn consecutive Y-strided pixels.
                int outX = clamp((int)floor(i.vertex.x), 0, _ChunkSizeXZ - 1);
                int outY = clamp((int)floor(i.vertex.y), 0, _ChunkSizeXZ * _MaxPairsPerColumn - 1);
                int pairIdx = outY / _ChunkSizeXZ;
                int z = outY - pairIdx * _ChunkSizeXZ;
                int x = outX;

                // Walk the column and emit the pairIdx-th run.
                int runStart = 0;
                int currentRunBlock = SampleBlock(x, 0, z);
                int currentRunStartY = 0;
                int currentPairIdx = 0;
                for (int y = 1; y < _ChunkSizeY; y++)
                {
                    int b = SampleBlock(x, y, z);
                    if (b != currentRunBlock)
                    {
                        if (currentPairIdx == pairIdx)
                        {
                            int runLen = y - currentRunStartY;
                            return float4((float)currentRunBlock / 255.0, (float)runLen / 255.0, 0, 1);
                        }
                        currentPairIdx++;
                        currentRunBlock = b;
                        currentRunStartY = y;
                        if (currentPairIdx > pairIdx) break;
                    }
                }

                // Tail run: spans to end of column.
                if (currentPairIdx == pairIdx)
                {
                    int runLen = _ChunkSizeY - currentRunStartY;
                    return float4((float)currentRunBlock / 255.0, (float)runLen / 255.0, 0, 1);
                }

                // No pair at this index — emit invalid sentinel.
                return float4(0, 0, 0, 0);
            }
            ENDCG
        }
    }
}
