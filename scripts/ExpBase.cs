using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_base — the REFERENCE for the whole DSP experiment series.
//
// Pure ExpPoolScene: nothing added, every experiment uniform at its no-op default, so this is
// the plain full sim in scene 24's pool. Every other scene in the series is this plus exactly
// one variable, which is what makes the comparisons mean anything.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_base.tscn 10 1280x900
public partial class ExpBase : ExpPoolScene
{
    protected override (string Title, string Hint) SceneInfo => (
        "24_base · reference",
        "The frozen baseline every other experiment scene is judged against: scene 24's pool, "
        + "ball and camera, full sim (no MNA stamp), one drive source, no obstacles, and every "
        + "experiment uniform at its no-op default. Raytracing is OFF by default so the fps is "
        + "the solver's, not the renderer's. LEFT-CLICK to drive.");
}
