// Frosted-glass shader for UI panels (URP).
//
// Samples the camera's opaque colour texture behind the element and blurs it, giving real
// glassmorphism rather than a flat translucent fill.
//
// REQUIREMENTS - both are set automatically by Tools > Triggle > Build Play Scene:
//   1. "Opaque Texture" must be enabled on the active URP Asset, or _CameraOpaqueTexture is empty.
//   2. The Canvas must be Screen Space - CAMERA (or World Space). A Screen Space - Overlay canvas
//      is composited outside the camera render loop, so it cannot read the scene texture at all.
//
// KNOWN LIMIT: _CameraOpaqueTexture contains opaque geometry only. The board slab, pegs, lattice
// lines and sockets blur correctly; rubber bands and claimed-cell fills are in the transparent
// queue and so do not appear in the blur. That is acceptable here - panels are backed by a dark
// tint anyway - but it is why the tint is not optional.

Shader "Triggle/UI Glass"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite (alpha = panel mask)", 2D) = "white" {}
        _Color ("Vertex Tint", Color) = (1,1,1,1)

        _GlassTint ("Glass Tint", Color) = (0.08, 0.10, 0.15, 0.72)
        _TintStrength ("Tint Strength", Range(0,1)) = 0.62
        _BlurRadius ("Blur Radius (px)", Range(0,48)) = 14
        _Brightness ("Backdrop Brightness", Range(0,2)) = 0.85
        _Saturation ("Backdrop Saturation", Range(0,2)) = 0.75

        // --- UI plumbing, required so this behaves like a normal UI shader ---
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
            "RenderPipeline" = "UniversalPipeline"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "GlassUI"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
                float4 screenPos  : TEXCOORD1;
                float4 positionOS : TEXCOORD2;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            TEXTURE2D_X(_CameraOpaqueTexture);
            SAMPLER(sampler_CameraOpaqueTexture);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
                float4 _GlassTint;
                float4 _ClipRect;
                float  _TintStrength;
                float  _BlurRadius;
                float  _Brightness;
                float  _Saturation;
            CBUFFER_END

            // UnityGet2DClipping lives in UnityUI.cginc, which is built-in-pipeline only, so the
            // RectMask2D test is reimplemented here for URP.
            float TriggleGet2DClipping (float2 position, float4 clipRect)
            {
                float2 inside = step(clipRect.xy, position.xy) * step(position.xy, clipRect.zw);
                return inside.x * inside.y;
            }

            Varyings vert (Attributes input)
            {
                Varyings output = (Varyings)0;

                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.positionOS = input.positionOS;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.color = input.color * _Color;
                output.screenPos = ComputeScreenPos(positions.positionCS);

                return output;
            }

            // 12 taps on two rings plus the centre. Cheaper than a separable Gaussian for a UI panel,
            // and the rings hide banding better than a single ring of the same tap count.
            static const float2 kTaps[12] =
            {
                float2( 1.0,  0.0), float2( 0.5,  0.866), float2(-0.5,  0.866),
                float2(-1.0,  0.0), float2(-0.5, -0.866), float2( 0.5, -0.866),
                float2( 0.707,  0.707), float2(-0.707,  0.707),
                float2(-0.707, -0.707), float2( 0.707, -0.707),
                float2( 0.0,  1.0), float2( 0.0, -1.0)
            };

            float3 SampleBackdrop (float2 screenUV)
            {
                // _ScreenParams.zw is 1 + 1/width, 1 + 1/height; derive the true texel size.
                float2 texel = float2(1.0 / _ScreenParams.x, 1.0 / _ScreenParams.y);
                float2 radius = _BlurRadius * texel;

                float3 sum = SAMPLE_TEXTURE2D_X(_CameraOpaqueTexture, sampler_CameraOpaqueTexture,
                                                 screenUV).rgb;
                float weight = 1.0;

                [unroll]
                for (int i = 0; i < 12; i++)
                {
                    // Inner ring at half radius, outer at full, giving a softer falloff.
                    float scale = (i < 6) ? 0.5 : 1.0;
                    float2 offset = kTaps[i] * radius * scale;
                    float2 uv = saturate(screenUV + offset);

                    sum += SAMPLE_TEXTURE2D_X(_CameraOpaqueTexture, sampler_CameraOpaqueTexture, uv).rgb;
                    weight += 1.0;
                }

                return sum / weight;
            }

            half4 frag (Varyings input) : SV_Target
            {
                // Sprite alpha is the panel's shape (rounded corners come from the 9-sliced sprite).
                half4 sprite = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                half maskAlpha = sprite.a * input.color.a;

                float2 screenUV = input.screenPos.xy / max(input.screenPos.w, 1e-5);
                float3 backdrop = SampleBackdrop(screenUV);

                // Desaturate and dim so text stays legible over a busy board.
                float luminance = dot(backdrop, float3(0.2126, 0.7152, 0.0722));
                backdrop = lerp(luminance.xxx, backdrop, _Saturation) * _Brightness;

                // Blend the blurred backdrop toward the glass tint.
                float3 glass = lerp(backdrop, _GlassTint.rgb, _TintStrength);

                half4 result;
                result.rgb = glass * input.color.rgb;
                result.a = maskAlpha * _GlassTint.a;

                #ifdef UNITY_UI_CLIP_RECT
                    result.a *= TriggleGet2DClipping(input.positionOS.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                    clip(result.a - 0.001);
                #endif

                return result;
            }
            ENDHLSL
        }
    }

    // If the HLSL above fails to compile on your platform, this keeps panels readable (flat tint)
    // instead of rendering magenta.
    Fallback "Universal Render Pipeline/Unlit"
}
