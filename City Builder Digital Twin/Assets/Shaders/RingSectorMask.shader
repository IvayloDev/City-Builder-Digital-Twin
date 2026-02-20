Shader "UI/RingSectorMask"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _AngleStart ("Angle Start (deg)", Range(0,360)) = 0
        _AngleEnd   ("Angle End (deg)",   Range(0,360)) = 90
        _InnerRadius ("Inner Radius", Range(0,1)) = 0.35
        _OuterRadius ("Outer Radius", Range(0,1)) = 1.0
        _Feather ("Feather", Range(0,0.1)) = 0.005
        _Rotation ("Rotation (deg)", Range(0,360)) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t { float4 vertex:POSITION; float4 color:COLOR; float2 texcoord:TEXCOORD0; };
            struct v2f { float4 vertex:SV_POSITION; fixed4 color:COLOR; float2 uv:TEXCOORD0; };

            sampler2D _MainTex;
            fixed4 _Color;
            float _AngleStart, _AngleEnd, _InnerRadius, _OuterRadius, _Feather, _Rotation;

            v2f vert(appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            float2 rot(float2 p, float r)
            {
                float s = sin(r), c = cos(r);
                return float2(c*p.x - s*p.y, s*p.x + c*p.y);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 p = (i.uv * 2.0 - 1.0);
                p = rot(p, radians(_Rotation));

                float rr = length(p);

                float a = atan2(p.y, p.x);
                if (a < 0) a += 6.28318530718;

                float a0 = radians(_AngleStart);
                float a1 = radians(_AngleEnd);

                float inAngle = (a0 <= a1) ? (step(a0,a) * step(a,a1))
                                           : (step(a0,a) + step(a,a1));

                float f = max(_Feather, 1e-6);
                float inOuter = smoothstep(_OuterRadius, _OuterRadius - f, rr);
                float outInner = smoothstep(_InnerRadius, _InnerRadius + f, rr);
                float inRing = inOuter * outInner;

                fixed4 col = tex2D(_MainTex, i.uv) * i.color;
                col.a *= (inAngle * inRing);
                return col;
            }
            ENDCG
        }
    }
}