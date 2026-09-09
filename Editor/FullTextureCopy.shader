Shader "Hidden/VRVlog/FullTextureCopy"
{
    Properties { _MainTex ("Source", 2D) = "white" {} _CubeTex ("Cube", Cube) = "" {} }
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
            sampler2D _MainTex; samplerCUBE _CubeTex;
            float _Mip, _Face, _Normal, _Cube;
            float4 frag(v2f_img input) : SV_Target
            {
                float2 uv = input.uv * 2 - 1;
                float3 direction = _Face == 0 ? float3(1,-uv.y,-uv.x) : _Face == 1 ? float3(-1,-uv.y,uv.x) :
                    _Face == 2 ? float3(uv.x,1,uv.y) : _Face == 3 ? float3(uv.x,-1,-uv.y) :
                    _Face == 4 ? float3(uv.x,-uv.y,1) : float3(-uv.x,-uv.y,-1);
                float4 value = _Cube ? texCUBElod(_CubeTex, float4(direction,_Mip)) : tex2Dlod(_MainTex,float4(input.uv,0,_Mip));
                if (_Normal) value = float4(UnpackNormal(value) * .5 + .5, 1);
                return value;
            }
            ENDCG
        }
    }
    Fallback Off
}
