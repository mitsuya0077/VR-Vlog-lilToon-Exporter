using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal enum BlinkExportMode { Auto, Manual, None }

    [Serializable]
    internal sealed class BlinkShapeBinding
    {
        public SkinnedMeshRenderer Renderer;
        public string Shape = "";
        public float Weight = 100f;
        internal BlinkShapeBinding Copy() => new BlinkShapeBinding { Renderer = Renderer, Shape = Shape, Weight = Weight };
    }

    [Serializable]
    internal sealed class BlinkExportOptions
    {
        public BlinkExportMode Mode;
        public List<BlinkShapeBinding> Both = new List<BlinkShapeBinding>();
        public List<BlinkShapeBinding> Left = new List<BlinkShapeBinding>();
        public List<BlinkShapeBinding> Right = new List<BlinkShapeBinding>();
        internal BlinkExportOptions Copy() => new BlinkExportOptions {
            Mode = Mode, Both = Both.Select(b => b.Copy()).ToList(),
            Left = Left.Select(b => b.Copy()).ToList(), Right = Right.Select(b => b.Copy()).ToList()
        };
    }
}
