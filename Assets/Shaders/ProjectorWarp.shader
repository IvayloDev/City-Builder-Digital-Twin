Shader "CityTwin/ProjectorWarp"
{
    // Draws the table render texture onto a projector's warp mesh (built by ProjectorOutput).
    // The mesh does the geometry (corner pin + mesh warp); this shader only samples the table
    // image and applies the per-projector seam cut and optional circle mask, both in table uv
    // so they line up across projectors automatically once geometry is calibrated.
    // No edge blending: the seam sits in the black gap between the game wedges.
    // Seam test mirrors ProjectorCalibration.KeepsTablePoint - keep them in sync.
    // SRPDefaultUnlit pass so it renders through the project's URP 2D renderer without lights.
    Properties
    {
        _MainTex ("Table Texture", 2D) = "black" {}
        _Seam ("Seam (normal x, normal y, offset, enabled)", Vector) = (-0.7071068, 0.7071068, 0, 1)
        _CircleMask ("Circle Mask (cx, cy, radius, feather)", Vector) = (0.5, 0.5, 0, 0.004)
        _PatternMode ("Test Pattern (0 off, 1 overlay, 2 pattern only, 3 white, 4 black)", Float) = 0
        _GridSpacing ("Grid Spacing (table uv)", Float) = 0.0833333
        _CentralRadius ("Central Piece Radius (table uv)", Float) = 0.1416667
        _Tint ("Projector Tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Unlit"
            Tags { "LightMode"="SRPDefaultUnlit" }
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Seam;
                float4 _CircleMask;
                float _PatternMode;
                float _GridSpacing;
                float _CentralRadius;
                float4 _Tint;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            // Anti-aliased line coverage for a signed distance field, ~widthPx screen pixels wide.
            float LineMask(float dist, float widthPx)
            {
                return 1.0 - saturate(abs(dist) / max(fwidth(dist), 1e-6) - widthPx * 0.5);
            }

            // Calibration test pattern in table uv: 25 cm grid, table rim, central piece, centre cross,
            // seam line (dashed). Returns coverage 0..1 per element as (grid, strong, seam).
            float3 TestPattern(float2 uv)
            {
                float2 g = uv / _GridSpacing;
                float2 gd = abs(frac(g - 0.5) - 0.5) * _GridSpacing; // distance to nearest grid line, table uv
                float grid = max(LineMask(gd.x, 1.5), LineMask(gd.y, 1.5));
                // Both diagonals: the gaps between the four game wedges, where the seam belongs.
                grid = max(grid, max(LineMask((uv.x - uv.y) * 0.7071068, 2.0), LineMask((uv.x + uv.y - 1.0) * 0.7071068, 2.0)));

                float r = distance(uv, float2(0.5, 0.5));
                float strong = max(LineMask(r - 0.5, 4.0), LineMask(r - _CentralRadius, 3.0));
                strong = max(strong, max(LineMask(uv.x - 0.5, 3.0), LineMask(uv.y - 0.5, 3.0)));

                float dash = step(0.5, frac(dot(uv, float2(_Seam.y, -_Seam.x)) * 40.0));
                float seam = _Seam.w > 0.5 ? LineMask(dot(uv, _Seam.xy) - _Seam.z, 3.0) * dash : 0.0;
                return float3(grid, strong, seam);
            }

            half4 frag(Varyings i) : SV_Target
            {
                half3 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rgb;

                int mode = (int)round(_PatternMode);
                if (mode == 2 || mode == 4) col = 0;
                else if (mode == 3) col = 1;
                if (mode == 1 || mode == 2)
                {
                    float3 p = TestPattern(i.uv);
                    col = lerp(col, half3(0.45, 0.45, 0.45), p.x);
                    col = lerp(col, half3(1.0, 0.85, 0.1), p.z);
                    col = lerp(col, half3(1.0, 1.0, 1.0), p.y);
                }
                if (mode != 0) col *= _Tint.rgb;

                float w = 1.0;
                if (_Seam.w > 0.5)
                {
                    // Hard cut, anti-aliased over one output pixel.
                    float d = dot(i.uv, _Seam.xy) - _Seam.z;
                    w = saturate(d / max(fwidth(d), 1e-6) + 0.5);
                }

                if (_CircleMask.z > 0.0)
                {
                    float r = distance(i.uv, _CircleMask.xy);
                    w *= 1.0 - smoothstep(_CircleMask.z - _CircleMask.w, _CircleMask.z, r);
                }

                return half4(col * w, 1.0);
            }
            ENDHLSL
        }
    }
}
