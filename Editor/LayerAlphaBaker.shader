// Adapted from lilToon 2.3.4 (MIT, Copyright (c) 2020-2024 lilxyzw).
// See ThirdPartyNotices/lilToon-LICENSE.txt and lilToon.md.
Shader "Hidden/VRVlog/LayerAlphaBaker"
{
    Properties
    {
        //----------------------------------------------------------------------------------------------------------------------
        // Main
        [lilHDR]        _Color                      ("Color", Color) = (1,1,1,1)
        [MainTexture]   _MainTex                    ("Texture", 2D) = "white" {}
        [lilUVAnim]     _MainTex_ScrollRotate       ("Angle|UV Animation|Scroll|Rotate", Vector) = (0,0,0,0)
        [lilHSVG]       _MainTexHSVG                ("Hue|Saturation|Value|Gamma", Vector) = (0,1,1,1)
                        _MainGradationStrength      ("Gradation Strength", Range(0, 1)) = 0
        [NoScaleOffset] _MainGradationTex           ("Gradation Map", 2D) = "white" {}
        [NoScaleOffset] _MainColorAdjustMask        ("Adjust Mask", 2D) = "white" {}

        //----------------------------------------------------------------------------------------------------------------------
        // Main2nd
        [lilToggleLeft] _UseMain2ndTex              ("Use Main 2nd", Int) = 0
                        _Color2nd                   ("Color", Color) = (1,1,1,1)
                        _Main2ndTex                 ("Texture", 2D) = "white" {}
        [lilAngle]      _Main2ndTexAngle            ("Angle", Float) = 0
        [lilDecalAnim]  _Main2ndTexDecalAnimation   ("sDecalAnimations", Vector) = (1,1,1,30)
        [lilDecalSub]   _Main2ndTexDecalSubParam    ("sDecalSubParams", Vector) = (1,1,0,1)
        [lilToggle]     _Main2ndTexIsDecal          ("As Decal", Int) = 0
        [lilToggle]     _Main2ndTexIsLeftOnly       ("Left Only", Int) = 0
        [lilToggle]     _Main2ndTexIsRightOnly      ("Right Only", Int) = 0
        [lilToggle]     _Main2ndTexShouldCopy       ("Copy", Int) = 0
        [lilToggle]     _Main2ndTexShouldFlipMirror ("Flip Mirror", Int) = 0
        [lilToggle]     _Main2ndTexShouldFlipCopy   ("Flip Copy", Int) = 0
        [lilToggle]     _Main2ndTexIsMSDF           ("As MSDF", Int) = 0
        [NoScaleOffset] _Main2ndBlendMask           ("Mask", 2D) = "white" {}
        [lilEnum]       _Main2ndTexBlendMode        ("Blend Mode|Normal|Add|Screen|Multiply", Int) = 0

        //----------------------------------------------------------------------------------------------------------------------
        // Main3rd
        [lilToggleLeft] _UseMain3rdTex              ("Use Main 3rd", Int) = 0
                        _Color3rd                   ("Color", Color) = (1,1,1,1)
                        _Main3rdTex                 ("Texture", 2D) = "white" {}
        [lilAngle]      _Main3rdTexAngle            ("Angle", Float) = 0
        [lilDecalAnim]  _Main3rdTexDecalAnimation   ("sDecalAnimations", Vector) = (1,1,1,30)
        [lilDecalSub]   _Main3rdTexDecalSubParam    ("sDecalSubParams", Vector) = (1,1,0,1)
        [lilToggle]     _Main3rdTexIsDecal          ("As Decal", Int) = 0
        [lilToggle]     _Main3rdTexIsLeftOnly       ("Left Only", Int) = 0
        [lilToggle]     _Main3rdTexIsRightOnly      ("Right Only", Int) = 0
        [lilToggle]     _Main3rdTexShouldCopy       ("Copy", Int) = 0
        [lilToggle]     _Main3rdTexShouldFlipMirror ("Flip Mirror", Int) = 0
        [lilToggle]     _Main3rdTexShouldFlipCopy   ("Flip Copy", Int) = 0
        [lilToggle]     _Main3rdTexIsMSDF           ("As MSDF", Int) = 0
        [NoScaleOffset] _Main3rdBlendMask           ("Mask", 2D) = "white" {}
        [lilEnum]       _Main3rdTexBlendMode        ("Blend Mode|Normal|Add|Screen|Multiply", Int) = 0

        _Main2ndTexAlphaMode ("2nd alpha mode", Int) = 0
        _Main3rdTexAlphaMode ("3rd alpha mode", Int) = 0
        _VrvMainST ("Source main UV transform", Vector) = (1,1,0,0)
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #define LIL_BAKER
            #define LIL_WITHOUT_ANIMATION
            #define LIL_FEATURE_MAIN_TONE_CORRECTION
            #define LIL_FEATURE_MAIN_GRADATION_MAP
            #define LIL_FEATURE_MAIN2ND
            #define LIL_FEATURE_MAIN3RD
            #define LIL_FEATURE_DECAL
            #define LIL_FEATURE_ANIMATE_DECAL
            #define LIL_FEATURE_MainGradationTex
            #define LIL_FEATURE_MainColorAdjustMask
            #define LIL_FEATURE_Main2ndTex
            #define LIL_FEATURE_Main2ndBlendMask
            #define LIL_FEATURE_Main3rdTex
            #define LIL_FEATURE_Main3rdBlendMask
            #include "Packages/jp.lilxyzw.liltoon/Shader/Includes/lil_pipeline_brp.hlsl"
            #include "Packages/jp.lilxyzw.liltoon/Shader/Includes/lil_common.hlsl"
            #include "Packages/jp.lilxyzw.liltoon/Shader/Includes/lil_common_appdata.hlsl"

            uint _Main2ndTexAlphaMode, _Main3rdTexAlphaMode;
            float4 _VrvMainST;
            struct v2f
            {
                float4 positionCS : SV_POSITION;
                float2 uv0 : TEXCOORD0;
            };
            v2f vert(appdata input)
            {
                v2f o;
                LIL_VERTEX_POSITION_INPUTS(input.positionOS, vertexInput);
                o.positionCS = vertexInput.positionCS;
                o.uv0 = input.uv0;
                return o;
            }
            void BlendLayer(inout float4 color, float4 layer, uint alphaMode, uint blendMode)
            {
                // lilGetMain2nd/3rd: coverage changes surface alpha first;
                // an active alpha mode then uses FULL strength for RGB.
                if (alphaMode != 0)
                {
                    if (alphaMode == 1) color.a = layer.a;
                    if (alphaMode == 2) color.a *= layer.a;
                    if (alphaMode == 3) color.a = saturate(color.a + layer.a);
                    if (alphaMode == 4) color.a = saturate(color.a - layer.a);
                    layer.a = 1;
                }
                color.rgb = lilBlendColor(color.rgb, layer.rgb, layer.a, blendMode);
            }
            float4 frag(v2f input) : SV_Target
            {
                float4 color = LIL_SAMPLE_2D(_MainTex, sampler_MainTex, input.uv0);
                float3 baseColor = color.rgb;
                float adjustMask = LIL_SAMPLE_2D(_MainColorAdjustMask, sampler_MainTex, input.uv0).r;
                color.rgb = lilToneCorrection(color.rgb, _MainTexHSVG);
                color.rgb = lilGradationMap(color.rgb, _MainGradationTex, _MainGradationStrength);
                color.rgb = lerp(baseColor, color.rgb, adjustMask);
                color *= _Color;

                // Pixels represent uvMain. Layers use the original mesh UV0,
                // while their blend masks use uvMain and the MAIN sampler.
                float2 layerUv = (input.uv0 - _VrvMainST.zw) / _VrvMainST.xy;
                bool isRightHand = false; // orientation-dependent layers are rejected
                if (_UseMain2ndTex)
                {
                    float4 layer = _Color2nd * LIL_GET_SUBTEX(_Main2ndTex, layerUv);
                    layer.a *= LIL_SAMPLE_2D(_Main2ndBlendMask, sampler_MainTex, input.uv0).r;
                    BlendLayer(color, layer, _Main2ndTexAlphaMode, _Main2ndTexBlendMode);
                }
                if (_UseMain3rdTex)
                {
                    float4 layer = _Color3rd * LIL_GET_SUBTEX(_Main3rdTex, layerUv);
                    layer.a *= LIL_SAMPLE_2D(_Main3rdBlendMask, sampler_MainTex, input.uv0).r;
                    BlendLayer(color, layer, _Main3rdTexAlphaMode, _Main3rdTexBlendMode);
                }
                // AlphaMaskBaker runs AFTER this pass, matching lilToon.
                return color;
            }
            ENDHLSL
        }
    }
}
