using System;
using System.Collections.Generic;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Shared panel + slider builder for the C# demo scenes — the idiomatic-C# analog
// of water-kit's scripts/lib/demo_ui.gd. Instance-based (not a static like the
// GDScript one) so the "Copy values" getters live on the instance instead of node
// meta. Each scene does:
//     var ui = new DemoUI(this, "title", "hint");
//     ui.AddSlider(...); ui.AddToggle(...);
public sealed class DemoUI
{
    private readonly VBoxContainer _vbox;
    private readonly List<Func<string>> _getters = new();
    private readonly string _title;

    public DemoUI(Node root, string title, string hint = "")
    {
        _title = title;

        var layer = new CanvasLayer();
        root.AddChild(layer);

        var panel = new PanelContainer
        {
            AnchorLeft = 1.0f,
            AnchorRight = 1.0f,
            AnchorTop = 0.0f,
            AnchorBottom = 1.0f,
            OffsetLeft = -336.0f,
            OffsetTop = 16.0f,
            OffsetRight = -16.0f,
            OffsetBottom = -16.0f,
            GrowHorizontal = Control.GrowDirection.Begin,
        };
        layer.AddChild(panel);

        var outer = new VBoxContainer();
        panel.AddChild(outer);

        var titleLabel = new Label { Text = title };
        titleLabel.AddThemeFontSizeOverride("font_size", 18);
        outer.AddChild(titleLabel);

        if (hint != "")
        {
            outer.AddChild(new Label
            {
                Text = hint,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(296, 0),
                Modulate = new Color(0.78f, 0.86f, 0.95f),
            });
        }

        // FocusMode.None everywhere: the arrow keys DRIVE the scenes — a focused control
        // must never eat (or react to) them. Mouse interaction is unaffected.
        var copyBtn = new Button { Text = "Copy values", FocusMode = Control.FocusModeEnum.None };
        copyBtn.Pressed += () =>
        {
            var lines = new List<string> { "# " + _title };
            foreach (var g in _getters)
            {
                lines.Add(g());
            }
            DisplayServer.ClipboardSet(string.Join("\n", lines));
            copyBtn.Text = "Copied!";
            copyBtn.GetTree().CreateTimer(1.2).Timeout += () => copyBtn.Text = "Copy values";
        };
        outer.AddChild(copyBtn);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FocusMode = Control.FocusModeEnum.None,
        };
        _vbox = new VBoxContainer { CustomMinimumSize = new Vector2(296, 0) };
        scroll.AddChild(_vbox);
        outer.AddChild(scroll);

        // FPS counter — bottom-left, standalone (not in the panel); every scene gets it.
        var fps = new Label
        {
            AnchorLeft = 0f, AnchorRight = 0f, AnchorTop = 1f, AnchorBottom = 1f,
            OffsetLeft = 12f, OffsetTop = -34f, OffsetBottom = -10f,
            GrowVertical = Control.GrowDirection.Begin,
            Modulate = new Color(0.66f, 0.95f, 0.78f),
        };
        fps.AddThemeFontSizeOverride("font_size", 15);
        layer.AddChild(fps);
        var fpsTimer = new Godot.Timer { WaitTime = 0.25, Autostart = true };
        fpsTimer.Timeout += () => fps.Text = $"{Engine.GetFramesPerSecond():0} fps";
        layer.AddChild(fpsTimer);

        // Render scale + upscaler — shared perf knobs on every scene. The sim cost is
        // resolution-independent; fullscreen retina pain is per-pixel fragment work, and
        // these attack exactly that (UI/text stay full-res; only the 3D viewport scales).
        var vp = root.GetViewport();
        AddOptions("Upscaler", new[] { "Bilinear", "FSR 1", "FSR 2", "MetalFX spatial", "MetalFX temporal" },
            (int)vp.Scaling3DMode, i => vp.Scaling3DMode = (Viewport.Scaling3DModeEnum)i);
        AddSlider("Render scale", 0.33f, 1.0f, (float)vp.Scaling3DScale, v => vp.Scaling3DScale = v);
        AddOptions("MSAA 3D", new[] { "Off", "2×", "4×", "8×" }, (int)vp.Msaa3D, i => vp.Msaa3D = (Viewport.Msaa)i);
        AddToggle("TAA (skip if temporal upscaler on)", vp.UseTaa, on => vp.UseTaa = on);
        AddToggle("FXAA", vp.ScreenSpaceAA == Viewport.ScreenSpaceAAEnum.Fxaa,
            on => vp.ScreenSpaceAA = on ? Viewport.ScreenSpaceAAEnum.Fxaa : Viewport.ScreenSpaceAAEnum.Disabled);
        AddToggle("Debanding", vp.UseDebanding, on => vp.UseDebanding = on);
    }

    public HSlider AddSlider(string label, float min, float max, float val, Action<float> cb)
    {
        var lbl = new Label { Text = $"{label}:  {val:0.###}" };
        _vbox.AddChild(lbl);

        var s = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = (max - min) / 200.0,
            Value = val,
            CustomMinimumSize = new Vector2(290, 0),
            FocusMode = Control.FocusModeEnum.None,
        };
        s.ValueChanged += v =>
        {
            lbl.Text = $"{label}:  {v:0.###}";
            cb((float)v);
        };
        _vbox.AddChild(s);
        _getters.Add(() => $"{label}: {s.Value:0.####}");
        return s;
    }

    // A live-updating readout line (the caller sets .Text each frame). Not a control,
    // so it isn't registered with the copy-values getters.
    public Label AddReadout(string initial = "")
    {
        // Fixed width + autowrap so changing text can't resize the label → no
        // container re-sort loop (which overflows the message queue and aborts).
        var lbl = new Label
        {
            Text = initial,
            Modulate = new Color(0.62f, 0.95f, 0.78f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(288, 0),
        };
        lbl.AddThemeFontSizeOverride("font_size", 13);
        _vbox.AddChild(lbl);
        return lbl;
    }

    public OptionButton AddOptions(string label, string[] items, int selected, Action<int> cb)
    {
        _vbox.AddChild(new Label { Text = label });
        var ob = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        foreach (var it in items)
        {
            ob.AddItem(it);
        }
        ob.Selected = selected;
        ob.ItemSelected += idx => cb((int)idx);
        _vbox.AddChild(ob);
        _getters.Add(() => $"{label}: {ob.GetItemText(ob.Selected)}");
        return ob;
    }

    public CheckButton AddToggle(string label, bool val, Action<bool> cb)
    {
        var c = new CheckButton { Text = label, ButtonPressed = val, FocusMode = Control.FocusModeEnum.None };
        c.Toggled += pressed => cb(pressed);
        _vbox.AddChild(c);
        _getters.Add(() => $"{label}: {(c.ButtonPressed ? "on" : "off")}");
        return c;
    }

    // A section header (top spacing + tinted label + rule) to group controls.
    public void AddSection(string title)
    {
        _vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });
        var lbl = new Label { Text = title };
        lbl.AddThemeFontSizeOverride("font_size", 15);
        lbl.AddThemeColorOverride("font_color", new Color(0.62f, 0.78f, 1.0f));
        _vbox.AddChild(lbl);
        _vbox.AddChild(new HSeparator());
    }
}
