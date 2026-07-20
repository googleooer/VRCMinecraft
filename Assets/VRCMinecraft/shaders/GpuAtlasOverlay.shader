Shader "VRCM/GpuAtlasOverlay"
{
    Properties
    {
        _MainTex ("Source Atlas", 2D) = "black" {}
        _OverlayTex ("Overlay", 2D) = "black" {}
        _OverlayRect ("Overlay Rect", Vector) = (0,0,1,1)
        _OverlayPackedWidth ("Packed Width (px)", Float) = 0
        _OverlayColumnRepack ("Column Repack Mode", Float) = 0
        _OverlayColumnY ("Column Y Chunk Index", Float) = 0
        _OverlayNumYChunks ("Num Y Chunks (column height / chunk height)", Float) = 8
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            ZTest Always
            Cull Off
            ZWrite Off

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

            sampler2D _MainTex;
            sampler2D _OverlayTex;
            float4 _OverlayRect;
            float _OverlayPackedWidth;
            float _OverlayColumnRepack;  // 1 = read the source as a generator column final texture
            float _OverlayColumnY;       // this chunk's Y index within the column
            float _OverlayNumYChunks;    // column height (px) / chunk height (px) = worldDimensionY

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 minUv = _OverlayRect.xy;
                float2 maxUv = _OverlayRect.xy + _OverlayRect.zw;
                if (i.uv.x < minUv.x || i.uv.x >= maxUv.x || i.uv.y < minUv.y || i.uv.y >= maxUv.y)
                    return tex2D(_MainTex, i.uv);

                // Local UV within the overlay rect (0..1)
                float2 localUv = (i.uv - minUv) / _OverlayRect.zw;

                if (_OverlayColumnRepack > 0.5)
                {
                    // GPU->GPU repack: copy one chunk's Y-slice out of a generator COLUMN final
                    // texture (16 x numYChunks*256, block id in R) straight into this atlas slot —
                    // no GPU->CPU readback. Slot pixel (x, row_local) <- column pixel
                    // (x, columnY*256 + row_local). Since slot and column-slice are the same 256-row
                    // resolution, the X/row map 1:1 and only Y is offset by the chunk index.
                    float colU = localUv.x;
                    float colV = (_OverlayColumnY + localUv.y) / _OverlayNumYChunks;
                    float colBlock = tex2D(_OverlayTex, float2(colU, colV)).r;
                    return fixed4(colBlock, 0.0, 0.0, 1.0);
                }

                if (_OverlayPackedWidth > 0.5)
                {
                    // Packed block upload: 4 block IDs per RGBA pixel.
                    // localUv.x spans the full tile width (e.g. 16 voxels).
                    // The packed texture is 1/4 that width.
                    float fullWidth = _OverlayPackedWidth * 4.0;
                    float pixelX = floor(localUv.x * fullWidth);
                    float packedX = floor(pixelX * 0.25);
                    float channel = pixelX - packedX * 4.0;

                    float2 packedUv = float2(
                        (packedX + 0.5) / _OverlayPackedWidth,
                        localUv.y
                    );
                    fixed4 packed = tex2D(_OverlayTex, packedUv);

                    float blockId = 0.0;
                    if (channel < 0.5) blockId = packed.r;
                    else if (channel < 1.5) blockId = packed.g;
                    else if (channel < 2.5) blockId = packed.b;
                    else blockId = packed.a;

                    return fixed4(blockId, 0.0, 0.0, 1.0);
                }

                // Non-packed path (used for other overlays like the clear texture)
                return tex2D(_OverlayTex, localUv);
            }
            ENDCG
        }
    }
}
