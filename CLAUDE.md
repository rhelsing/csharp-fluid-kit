# Project — godot-csharp-experiments (Godot 4.6.2 **.NET**, Jolt physics)

A lab of small, focused **C#** Godot experiments — sibling to `../water-kit`
(which is GDScript). Same philosophy: each technique isolated in one small,
self-contained, self-validating scene. This repo exists for the things C# is the
right tool for — big stateful CPU-side systems, zero-GC value types (`struct`
passed by `ref`/`in`), compiler-checked interfaces/generics, and tight numeric
loops. Long-term goal: port the heavier water-kit techniques (ripple sim,
buoyancy on many bodies, underwater compute) into a real C# architecture.

## ⚠️ This project needs the .NET build of Godot — NOT the standard one

- **Godot binary (C#):** `/Applications/Godot-mono.app/Contents/MacOS/Godot`  (4.6.2 .NET)
- The standard `/Applications/Godot.app` used by water-kit **cannot compile or run this project.** Always use `Godot-mono.app` here.
- **.NET SDK:** 8.0.x **arm64**, installed user-local at `~/.dotnet` (added to `~/.zshrc`).
  Verify with `~/.dotnet/dotnet --version`. C# targets `net8.0` (see `.csproj`).
- **C# editor:** VS Code + the C# Dev Kit extension.

### ⚠️ Architecture: launch Godot as arm64 (this machine is Apple Silicon)

The Godot binary is a **universal** (x86_64 + arm64) build and the .NET SDK is
**arm64-only**. If Godot launches in x86_64 mode it looks for an x86_64
`libhostfxr.dylib` that doesn't exist and dies with *"incompatible architecture …
need 'x86_64'"*. The kernel picks a universal binary's arch to **match the parent
process**, so the trap is launching Godot through an **x86_64 wrapper** — e.g.
`/usr/local/bin/timeout` (Intel Homebrew) flips it to x86_64.

- Launch Godot **directly** from the arm64 shell, or force it: `arch -arm64 <godot> …`.
- Do **not** wrap the launch in `/usr/local/bin/timeout`. Use `--quit-after N`
  (Godot self-terminates) instead of an external timeout.

## The build step (the big difference from GDScript)

C# must **compile before the scene runs.** The editor builds on play; from the
CLI:

```bash
~/.dotnet/dotnet build                      # or let the editor's Build button do it
```

- First build after opening / after `.godot` is cleared is **slow** (restores
  NuGet, compiles). Later builds are fast/incremental.
- Build output lands in `bin/` and `obj/` (git-ignored). If C# behaves stale,
  delete `bin/ obj/ .godot/mono/` and rebuild.
- A red "Build" error in the editor means the C# didn't compile — fix that before
  chasing runtime bugs.

## Architecture / conventions (mirror water-kit)

- Numbered pair per scenario: `scenes/NN_<name>.tscn` (thin wrapper) +
  `scripts/<PascalCaseName>.cs` (builds camera/light/content in code, so the
  whole scene is one reviewable file). Root namespace: `GodotCsharpExperiments`.
- `public partial class Foo : Node3D` — Godot C# classes are **`partial`** (the
  source generator adds the other half). Forgetting `partial` is the #1 first
  error.
- Shared C# in `scripts/lib/` under a `GodotCsharpExperiments.Lib` sub-namespace.
- Shaders live in `shaders/*.gdshader` — these are **portable verbatim** from
  water-kit or tutorials; cite the source URL in the script/README.
- Expose tunables as live sliders so a scene is self-explaining.
- New scenario → add a numbered row to the README "Scenarios" table.
- Godot 4.6.2 · .NET 8 · Jolt · Forward+.

## Visual verification — render, don't run blind

Same rule as water-kit: a shader/particle bug looks fine in numbers and wrong on
screen, so **screenshot it**. The `tools/shoot.tscn` harness is ported and working
(it's GDScript on purpose — tooling, no build step; renders C# scenes fine). It
loads a scene, runs it N seconds with its own camera/lights, saves
`tools/shot_<scene>.png`, and quits. Must render (never `--headless`) and must run
**arm64** (see the architecture note above):

```bash
arch -arm64 /Applications/Godot-mono.app/Contents/MacOS/Godot \
  --path . res://tools/shoot.tscn -- res://scenes/<scene>.tscn 3
# → tools/shot_<scene>.png  — then Read that PNG. Add a 3rd arg "1600x1200" if a panel covers content.
```

## RenderingDevice compute / new `.glsl` — the verified recipe

`scripts/ComputeSmoke.cs` (scene 02) is the **working, screenshot-verified RD-compute +
`Texture2Drd` template** — copy it for anything GPU-compute (the stamp solver builds on it).

**One root gotcha, one fix.** A spawned/GUI Godot-mono doesn't read `~/.zshrc`, so it can't
find `~/.dotnet` → *"dotnet: command not found" / hostfxr missing* (and, in headless, a crash
in the C# editor init — **same root cause**). Fix it with env, once: launch everything through
**`tools/godot-mono.sh`** (sets `DOTNET_ROOT` + `PATH` + `arch -arm64`):

```bash
tools/godot-mono.sh --headless --import --path .                                # import new .glsl
tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/NN.tscn 3   # shoot, then Read PNG
```

**Verified:** with PATH set, `--headless --import` imports cleanly (~6 s, exit 0, no crash)
— the earlier "headless is broken for .NET" was purely the missing PATH, *not* an engine
limit (isolating the one variable disproved it). Build with `~/.dotnet/dotnet build`. Never
wrap a Godot launch in Intel `/usr/local/bin/timeout` — it flips the universal binary to
x86_64 and can't load the arm64 hostfxr (a look-alike of the same error); use `--quit-after N`.

C# RD API notes (differ from GDScript): `GD.Load<RDShaderFile>(...).GetSpirV()`,
`RenderingServer.GetRenderingDevice()`, all rd work inside
`RenderingServer.CallOnRenderThread(Callable.From(...))`, push-constant bytes via
`Buffer.BlockCopy(float[]→byte[])`, display via `Texture2Drd.TextureRdRid`, free
order sets → textures → shader.

## Debugging — prove it, don't guess (see global CLAUDE.md)

Visual bug → screenshot (the rendered frame is truth). Logic/number bug → deduped
state-change logs (`GD.Print`). A one-line guard beats a 50-line rewrite; if the
fix is big, the diagnosis is probably wrong.
