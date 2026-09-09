Shader "VRVlogTests/UnsupportedHiddenFallback"
{
    Properties { _Color("Color", Color) = (0,0,0,1) }
    SubShader
    {
        Tags { "VRCFallback" = "Hidden" }
        Pass { Color (0,0,0,1) }
    }
}
