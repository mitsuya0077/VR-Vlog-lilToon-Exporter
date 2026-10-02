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
            float _Mip, _Face, _Normal, _Cube, _Resample, _Color, _ManualSrgb;
            float4 _SourceSize, _TargetSize;
            float4 SampleSource(float2 position)
            {
                float2 uv = position * 2 - 1;
                float3 direction = _Face == 0 ? float3(1,-uv.y,-uv.x) : _Face == 1 ? float3(-1,-uv.y,uv.x) :
                    _Face == 2 ? float3(uv.x,1,uv.y) : _Face == 3 ? float3(uv.x,-1,-uv.y) :
                    _Face == 4 ? float3(uv.x,-uv.y,1) : float3(-uv.x,-uv.y,-1);
                float4 value = _Cube ? texCUBElod(_CubeTex, float4(direction,_Mip)) : tex2Dlod(_MainTex,float4(position,0,_Mip));
                if (_Normal) value = float4(UnpackNormal(value) * .5 + .5, 1);
                return value;
            }
            float4 frag(v2f_img input) : SV_Target
            {
                if (!_Resample) return SampleSource(input.uv);
                float2 pixel = min(floor(input.uv * _TargetSize.xy), _TargetSize.xy - 1);
                float2 minimum = pixel * _SourceSize.xy / _TargetSize.xy;
                float2 maximum = (pixel + 1) * _SourceSize.xy / _TargetSize.xy;
                int2 first = (int2)floor(minimum);
                int2 last = min((int2)ceil(maximum), (int2)_SourceSize.xy);
                float4 sum = 0;
                float4 scale = 1;
                float2 area = maximum - minimum;
                float total = area.x * area.y;
                // Texel-center sampling averages all covered source pixels even
                // for Point filtering. Fractional NPOT edges use area weights.
                [loop] for (int y = first.y; y < last.y; y++)
                [loop] for (int x = first.x; x < last.x; x++)
                {
                    float2 coverage = max(0, min(float2(x + 1, y + 1), maximum) - max(float2(x, y), minimum));
                    float weight = coverage.x * coverage.y / total;
                    float4 value = SampleSource((float2(x, y) + .5) / _SourceSize.xy);
                    if (_ManualSrgb)
                        value.rgb = float3(GammaToLinearSpaceExact(value.r), GammaToLinearSpaceExact(value.g), GammaToLinearSpaceExact(value.b));
                    if (_Color) value.rgb *= value.a;
                    // Keep the reciprocal normal on GPUs that flush subnormal
                    // numbers. Dividing by float.MaxValue otherwise becomes
                    // multiplication by a reciprocal flushed to zero.
                    float4 nextScale = max(scale, min(abs(value), 1e19));
                    sum = sum * (scale / nextScale) + (value / nextScale) * weight;
                    scale = nextScale;
                }
                // Rounding a convex average at the finite float boundary can
                // overflow by one ULP. Bound that result to the format range.
                sum = clamp(sum * scale, -3.402823466e38, 3.402823466e38);
                if (_Color) sum.rgb = sum.a > 0 ? sum.rgb / sum.a : 0;
                if (_ManualSrgb)
                    sum.rgb = float3(LinearToGammaSpaceExact(sum.r), LinearToGammaSpaceExact(sum.g), LinearToGammaSpaceExact(sum.b));
                return sum;
            }
            ENDCG
        }
    }
    Fallback Off
}
