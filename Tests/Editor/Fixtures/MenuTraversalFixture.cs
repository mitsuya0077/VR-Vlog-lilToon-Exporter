using System;
using System.Collections.Generic;
using System.Linq;

namespace VRVlog.LilToonExporter.Tests
{
    // SDK-shaped public fields exercise the production reflection/traversal code.
    // No SDK installation, controller execution or source avatar is simulated.
    internal static class MenuTraversalFixture
    {
        internal static void Run(Action<bool, string> check)
        {
            var sub = new Menu();
            sub.controls.Add(new Control { name = "笑顔", type = "Toggle", parameter = new Parameter { name = "Face" }, value = 3 });
            sub.controls.Add(new Control { name = "強さ", type = "RadialPuppet" });
            sub.controls.Add(new Control { name = "未設定", type = "Button" });
            var menu = new Menu();
            menu.controls.Add(new Control { name = "表情", type = "SubMenu", parameter = new Parameter { name = "OpenFace" }, value = 1, subMenu = sub });
            sub.controls.Add(new Control { name = "循環", type = "SubMenu", subMenu = menu });
            var source = Read(menu);
            check(source.Entries.Count == 3, "Menus produce control entries, not individual morph names.");
            check(source.Entries[0].Name == "表情 / 笑顔" && source.Entries[0].Error == null, "Composed expression retains its menu/submenu name.");
            check(source.Entries[0].Parameters["Face"] == 3 && source.Entries[0].Parameters["OpenFace"] == 1,
                "Child selection carries its own parameter and the ancestor submenu gate.");
            check(source.Entries[1].Error.Contains("Puppet") && source.Entries[2].Error != null, "Puppets and missing parameters have explicit per-item errors.");
            check(source.Messages.Single().Contains("循環"), "A menu cycle terminates and is reported.");
            var previousId = source.Entries[0].Id;
            sub.controls[0].value = 2;
            check(Read(menu).Entries[0].Id != previousId, "Changed menu controls cannot inherit another control's stale exclusion.");
            menu.controls.Add(new Control { name = "別経路", type = "SubMenu", subMenu = sub });
            source = Read(menu);
            check(source.Entries.Count == 6 && source.Entries[3].Name == "別経路 / 笑顔", "Shared submenu assets can be used through multiple independent paths.");
            check(!source.Entries[3].Parameters.ContainsKey("OpenFace"), "Ancestor parameters do not leak between menu branches.");
            sub.controls[0].value = float.NaN;
            bool rejected = false;
            try { Read(menu); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Non-finite SDK values are rejected before Animator evaluation.");

            var boundary = new Menu();
            for (var i = 0; i < 256; i++) boundary.controls.Add(new Control { name = "face " + i, type = "Toggle", parameter = new Parameter { name = "Face" }, value = i });
            check(Read(boundary).Entries.Count == 256, "The documented 256 leaf budget remains usable in full.");
            boundary.controls.Add(new Control { name = "Empty", type = "SubMenu", subMenu = new Menu() });
            check(Read(boundary).Entries.Count == 256, "An empty submenu following 256 leaves does not consume a candidate slot.");
            boundary.controls.Add(new Control { name = "One more", type = "Button", parameter = new Parameter { name = "Face" }, value = 1 });
            VrChatMenuImportLimitException limit = null;
            try { Read(boundary); } catch (VrChatMenuImportLimitException error) { limit = error; }
            check(limit != null && limit.Summary.CandidateCount == 257 && limit.Summary.IsComplete,
                "An oversized menu has a typed, complete 257-candidate diagnostic instead of an opaque failure.");
            var skip = new VrChatMenuImportPolicy { ExpectedRoot = boundary, SkipAll = true };
            check(VrChatExpressionMenu.ReadMenu(boundary, skip).Entries.Count == 0,
                "An explicitly chosen whole-menu omission does not truncate or mutate the source menu.");
            check(boundary.controls.Count == 258, "Omission leaves the original controls and empty submenu intact.");
        }

        private static VrChatExpressionMenu.Source Read(Menu menu)
        {
            return VrChatExpressionMenu.ReadMenu(menu);
        }
        private sealed class Menu { public List<Control> controls = new List<Control>(); }
        private sealed class Parameter { public string name; }
        private sealed class Control
        {
            public string name, type;
            public Parameter parameter;
            public float value;
            public Menu subMenu;
        }
    }
}
