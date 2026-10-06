Shader "Hidden/VRVlogTests/AppearanceColorProbe"
{
    Properties
    {
        _Ordinary ("Ordinary", Color) = (1,1,1,1)
        [HDR] _Hdr ("HDR", Color) = (1,1,1,1)
        [Gamma] _Gamma ("Gamma", Color) = (1,1,1,1)
        _Which ("Property", Float) = 0
    }
    SubShader
    {
        Pass
        {
            Cull Off ZTest Always ZWrite Off Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Ordinary, _Hdr, _Gamma;
            float _Which;
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            float4 frag() : SV_Target { return _Which < .5 ? _Ordinary : _Which < 1.5 ? _Hdr : _Gamma; }
            ENDCG
        }
    }
}
