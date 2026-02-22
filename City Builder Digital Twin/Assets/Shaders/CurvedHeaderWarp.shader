Shader "UI/RT_UVWarp_Header"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        // Curve strength in UV space (0.0 - ~0.15)
        _Curve ("Curve Amount (UV)", Range(-0.85,0.85)) = 0.08

        // Optional: soften edge clipping if you push UV out of bounds
        _EdgeFade ("Edge Fade", Range(0,0.1)) = 0.02
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                float4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                fixed4 color : COLOR;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float _Curve;
            float _EdgeFade;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // x in [0..1], curve y by a parabola that peaks at center
                float t = uv.x - 0.5;                  // -0.5..0.5
                float f = 1.0 - (4.0 * t * t);         // 0..1..0

                uv.y += f * _Curve;

                // Optional edge fade if uv goes out of bounds
                float fade = 1.0;
                if (_EdgeFade > 0.0001)
                {
                    float2 d = min(uv, 1.0 - uv);      // distance to edges
                    float m = min(d.x, d.y);
                    fade = saturate(m / _EdgeFade);
                }

                fixed4 c = tex2D(_MainTex, uv) * i.color;
                c.a *= fade;
                return c;
            }
            ENDCG
        }
    }
}