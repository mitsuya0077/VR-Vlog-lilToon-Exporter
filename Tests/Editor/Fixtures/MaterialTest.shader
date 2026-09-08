Shader "Hidden/VRVlogTests/lilToon"
{
    Properties
    {
        _MainTex ("Main", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _UseShadow ("Shadow", Float) = 0
        _ShadowColor ("Shade", Color) = (0.7,0.7,0.7,1)
        _ShadowColorTex ("Shade image", 2D) = "white" {}
        _ShadowBorder ("Shadow border", Float) = 0.5
        _ShadowBlur ("Shadow blur", Float) = 0.1
        _ShadowStrength ("Shadow strength", Float) = 1
        _AsUnlit ("Unlit", Float) = 0
        _LightMinLimit ("Light min", Float) = 0.05
        _LightMaxLimit ("Light max", Float) = 1
        _MonochromeLighting ("Monochrome", Float) = 0
        _lilDirectionalLightStrength ("Directional", Float) = 1
        _VertexLightStrength ("Vertex lighting", Float) = 0
        _LightDirectionOverride ("Light direction", Vector) = (0.001,0.002,0.001,0)
        _UseRim ("Rim", Float) = 0
        _RimColor ("Rim color", Color) = (1,1,1,1)
        _RimBorder ("Rim border", Float) = 0.5
        _RimBlur ("Rim blur", Float) = 0.65
        _RimFresnelPower ("Rim power", Float) = 1
        _RimEnableLighting ("Rim lighting", Float) = 1
        _UseEmission ("Emission", Float) = 1
        [HDR] _EmissionColor ("Emission color", Color) = (1.4,1.4,1.4,1)
        _EmissionBlend ("Emission blend", Float) = 1
        _EmissionMap ("Emission image", 2D) = "white" {}
        _UseOutline ("Outline", Float) = 1
        _OutlineWidth ("Width", Float) = 0.14
        _OutlineTex ("Outline color image", 2D) = "white" {}
        _OutlineWidthMask ("Outline width mask", 2D) = "white" {}
        _OutlineVertexR2Width ("Vertex width control", Float) = 0
    }
    SubShader { Pass {} }
}
