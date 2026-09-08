using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class AlphaMaskBaker
    {
        internal static bool NeedsBake(Material material) => material.HasProperty("_AlphaMaskMode") && material.GetFloat("_AlphaMaskMode") != 0 &&
            (material.shader.name.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 ||
             material.shader.name.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
             material.HasProperty("_TransparentMode") && material.GetFloat("_TransparentMode") != 0);

        internal static void CheckUvRange(Renderer renderer, int slot, Material material, ICollection<string> warnings)
        {
            if (!NeedsBake(material)) return;
            var texture = material.GetTexture("_MainTex") ?? Texture2D.whiteTexture;
            var maskScale = material.GetTextureScale("_AlphaMask");
            // The source mask shares the main sampler. Integer tiling with
            // repeat sampling remains periodic after baking a single tile.
            if (texture.wrapModeU == TextureWrapMode.Repeat && texture.wrapModeV == TextureWrapMode.Repeat &&
                maskScale.x == Mathf.Round(maskScale.x) && maskScale.y == Mathf.Round(maskScale.y)) return;
            var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            var scale = material.GetTextureScale("_MainTex"); var offset = material.GetTextureOffset("_MainTex");
            var contained = mesh != null && mesh.subMeshCount > 0;
            if (contained)
            {
                var uv = mesh.uv;
                foreach (var i in mesh.GetIndices(Mathf.Min(slot, mesh.subMeshCount - 1)))
                {
                    if (i >= uv.Length) { contained = false; break; }
                    var u = uv[i].x * scale.x + offset.x; var v = uv[i].y * scale.y + offset.y;
                    if (u < 0 || u > 1 || v < 0 || v > 1) { contained = false; break; }
                }
            }
            if (!contained) warnings?.Add(renderer.name + " / " + material.name +
                ": 主画像の1枚分を超えるアルファマスクを近似しました。繰り返し部分の透明度に差が残る場合があります。");
        }

        internal static void Bake(Material material, List<Texture2D> owned, ICollection<string> warnings)
        {
            if (!NeedsBake(material)) return;
            var mode = material.GetFloat("_AlphaMaskMode");
            if (mode < 1 || mode > 4 || mode != Mathf.Round(mode)) throw new InvalidOperationException(material.name + ": アルファマスクの合成方法が不正です。");
            var shader = Shader.Find("Hidden/VRVlog/AlphaMaskBaker");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("アルファマスクのベイク用シェーダーを利用できません。");
            var source = material.GetTexture("_MainTex") ?? Texture2D.whiteTexture;
            var mask = material.GetTexture("_AlphaMask") ?? Texture2D.whiteTexture;
            var size = TextureResizePolicy.Size(Math.Max(source.width, mask.width), Math.Max(source.height, mask.height));
            if (size.Item1 < source.width || size.Item2 < source.height || size.Item1 < mask.width || size.Item2 < mask.height)
                warnings?.Add(material.name + ": アルファマスクを含む画像を " + size.Item1 + "×" + size.Item2 + " へ縮小しました。");
            var baker = new Material(shader);
            var previous = RenderTexture.active;
            var previousSrgb = GL.sRGBWrite;
            RenderTexture target = null;
            Texture2D result = null;
            try
            {
                baker.SetTexture("_AlphaMask", mask);
                baker.SetTextureScale("_AlphaMask", material.GetTextureScale("_AlphaMask"));
                baker.SetTextureOffset("_AlphaMask", material.GetTextureOffset("_AlphaMask"));
                baker.SetFloat("_AlphaMaskMode", mode);
                baker.SetFloat("_AlphaMaskScale", material.GetFloat("_AlphaMaskScale"));
                baker.SetFloat("_AlphaMaskValue", material.GetFloat("_AlphaMaskValue"));
                var color = material.GetColor("_Color");
                baker.SetFloat("_ColorAlpha", color.a);
                target = RenderTexture.GetTemporary(size.Item1, size.Item2, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(source, target, baker);
                RenderTexture.active = target;
                result = new Texture2D(size.Item1, size.Item2, TextureFormat.RGBA32, false, false)
                {
                    name = material.name + "__VRVlog_Alpha_" + Guid.NewGuid().ToString("N"),
                    wrapModeU = source.wrapModeU, wrapModeV = source.wrapModeV, filterMode = source.filterMode
                };
                result.ReadPixels(new Rect(0, 0, size.Item1, size.Item2), 0, 0, false);
                result.Apply(false, false);
                owned.Add(result);
                material.SetTexture("_MainTex", result);
                material.SetColor("_Color", new Color(color.r, color.g, color.b, 1));
                material.SetFloat("_AlphaMaskMode", 0);
                warnings?.Add(material.name + ": アルファマスクを出力用画像へ焼き込みました。");
                result = null;
            }
            finally
            {
                GL.sRGBWrite = previousSrgb;
                RenderTexture.active = previous;
                if (result != null) UnityEngine.Object.DestroyImmediate(result);
                if (target != null) RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(baker);
            }
        }
    }
}
