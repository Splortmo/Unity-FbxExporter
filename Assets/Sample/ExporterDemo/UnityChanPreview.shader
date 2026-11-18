Shader "FBX Exporter Sample/UnityChan"
{
    Properties
    {
        [MainTexture] _MainTex ("Diffuse", 2D) = "white" {}
        [MainColor] _Color ("Tint", Color) = (1, 1, 1, 1)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Source Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Destination Blend", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Toggle] _ZWrite ("Depth Write", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Cull [_Cull]
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]

            CGPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            half4 _Color;

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                half3 normal : TEXCOORD1;
            };

            Varyings Vertex(appdata_base input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.texcoord, _MainTex);
                output.normal = UnityObjectToWorldNormal(input.normal);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                half4 color = tex2D(_MainTex, input.uv) * _Color;
                // A fixed key and fill keep the model readable without a scene light.
                half key = saturate(dot(normalize(input.normal), half3(-0.30h, 0.58h, 0.76h)));
                color.rgb *= lerp(0.68h, 1.0h, smoothstep(0.10h, 0.72h, key));
                return color;
            }
            ENDCG
        }
    }
}
