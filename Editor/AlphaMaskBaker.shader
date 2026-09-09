// lilToon 2.3.4 alpha-mask equations (MIT, lilxyzw); see ThirdPartyNotices/lilToon-LICENSE.txt.
Shader "Hidden/VRVlog/AlphaMaskBaker"
{
    Properties
    {
        _MainTex ("Base", 2D) = "white" {}
        _AlphaMask ("Mask", 2D) = "white" {}
        _AlphaMaskMode ("Mode", Float) = 0
        _AlphaMaskScale ("Scale", Float) = 1
        _AlphaMaskValue ("Offset", Float) = 0
        _ColorAlpha ("Color alpha", Float) = 1
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            UNITY_DECLARE_TEX2D(_MainTex);
            UNITY_DECLARE_TEX2D_NOSAMPLER(_AlphaMask);
            float4 _AlphaMask_ST;
            float _AlphaMaskMode, _AlphaMaskScale, _AlphaMaskValue, _ColorAlpha;
            float4 frag(v2f_img input) : SV_Target
            {
                float4 color = UNITY_SAMPLE_TEX2D(_MainTex, input.uv);
                color.a *= _ColorAlpha;
                float mask = saturate(UNITY_SAMPLE_TEX2D_SAMPLER(_AlphaMask, _MainTex, input.uv * _AlphaMask_ST.xy + _AlphaMask_ST.zw).r * _AlphaMaskScale + _AlphaMaskValue);
                if (_AlphaMaskMode == 1) color.a = mask;
                if (_AlphaMaskMode == 2) color.a *= mask;
                if (_AlphaMaskMode == 3) color.a = saturate(color.a + mask);
                if (_AlphaMaskMode == 4) color.a = saturate(color.a - mask);
                return color;
            }
            ENDCG
        }
    }
}
