// SONA Quest port: additive glow for the sweep visualization and the laser in the dark world.
// Shape 0 (Dot): soft round dot for particle billboards. Shape 1 (Line): soft edges across a
// LineRenderer's width. Colour comes from the vertex colour (particles, line colours) times _Tint.
// Lives in Resources so it ships in builds even when no scene material references it.
Shader "SONA/Glow"
{
    Properties
    {
        [Enum(Dot, 0, Line, 1)] _Shape("Shape", Float) = 0
        _Tint("Tint", Color) = (1, 1, 1, 1)
        _Core("Hot core", Range(0, 2)) = 0.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "Glow"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Shape;
                float4 _Tint;
                float _Core;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                output.uv = input.uv;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 p = input.uv * 2.0 - 1.0;
                float r = _Shape < 0.5 ? length(p) : abs(p.y);
                float edge = saturate(1.0 - r);
                float glow = edge * edge + _Core * pow(saturate(1.0 - r * 2.2), 3.0);
                float3 rgb = input.color.rgb * input.color.a * _Tint.rgb * _Tint.a * glow;
                return float4(rgb, 0.0);
            }
            ENDHLSL
        }
    }
}
