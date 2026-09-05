Shader "Hidden/VRVlogTests/lilToon"
{
    Properties
    {
        _MainTex ("Main", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _UseShadow ("Shadow", Float) = 0
        _ShadowColor ("Shade", Color) = (0.7,0.7,0.7,1)
        _ShadowColorTex ("Shade image", 2D) = "white" {}
        _UseEmission ("Emission", Float) = 1
        [HDR] _EmissionColor ("Emission color", Color) = (1.4,1.4,1.4,1)
        _EmissionBlend ("Emission blend", Float) = 1
        _EmissionMap ("Emission image", 2D) = "white" {}
        _UseOutline ("Outline", Float) = 1
        _OutlineWidth ("Width", Float) = 0.14
        _OutlineTex ("Outline color image", 2D) = "white" {}
    }
    SubShader { Pass {} }
}
