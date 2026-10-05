Shader "Hidden/VRVlog/PreviewNormalEncode"
{
    Properties { _MainTex ("Canonical normal", 2D) = "bump" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _Mip;
            float4 frag(v2f_img input) : SV_Target
            {
                float4 value = tex2Dlod(_MainTex, float4(input.uv, 0, _Mip));
                // Match the installed native lilToon normal decoder, including
                // its platform-specific RGB, AG and RG-or-AG paths.
                #if defined(UNITY_NO_DXT5nm)
                    return float4(value.rgb, 1);
                #else
                    return float4(1, value.g, value.b, value.r);
                #endif
            }
            ENDCG
        }
    }
    Fallback Off
}
