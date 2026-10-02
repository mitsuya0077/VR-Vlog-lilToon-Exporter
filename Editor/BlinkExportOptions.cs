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
        internal void SelectMode(BlinkExportMode mode, BlinkExportSession resolved = null)
        {
            if (mode == BlinkExportMode.Manual && Both.Count == 0 && Left.Count == 0 && Right.Count == 0)
            {
                if (resolved != null)
                {
                    Both.AddRange(resolved.Slots[0].Select(b => b.Copy()));
                    Left.AddRange(resolved.Slots[1].Select(b => b.Copy()));
                    Right.AddRange(resolved.Slots[2].Select(b => b.Copy()));
                }
                // Failed automatic inference must offer an editable row without
                // guessing a renderer, shape or one of the ambiguous aliases.
                if (Both.Count == 0 && Left.Count == 0 && Right.Count == 0)
                    Both.Add(new BlinkShapeBinding());
            }
            Mode = mode;
        }
        internal BlinkExportOptions Copy() => new BlinkExportOptions {
            Mode = Mode, Both = Both.Select(b => b.Copy()).ToList(),
            Left = Left.Select(b => b.Copy()).ToList(), Right = Right.Select(b => b.Copy()).ToList()
        };
    }
}
