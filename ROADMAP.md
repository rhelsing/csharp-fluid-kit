# Roadmap

## Phase 0 — toolchain (done)
- [x] Install Godot 4.6.2 .NET build (`/Applications/Godot-mono.app`)
- [x] Install .NET SDK 8.0 (`~/.dotnet`, on PATH via `~/.zshrc`)
- [x] Scaffold project (`.csproj` / `.sln` / `project.godot` for .NET)
- [x] Scene 01 — `hello, C#` smoke test
- [x] Verify headless: `dotnet build` clean + `_Ready` prints in `Godot-mono` (arm64)
- [ ] **Verify in-editor:** open in `Godot-mono.app`, build, press play, see the box + log

## Phase 1 — establish the C# conventions
- [x] Port the screenshot harness (`tools/shoot.tscn`, GDScript) — verified rendering scene 01
- [ ] Build a `DemoUI` equivalent (slider/panel builder) in C# under `scripts/lib/`
- [ ] First "real" scene: a Gerstner wave field as a C# `struct`-based field
      (contrast with water-kit's `lib/gerstner_field.gd`)

## Phase 2 — the C#-justifying systems (mined from the reference archive)
- [ ] Ripple simulation propagating across a texture (CPU sim → shader)
- [ ] Buoyancy sampling many bodies per physics tick (zero-GC `WaterSample`)
- [ ] Underwater post via compute shader
- [ ] Composite water field (Gerstner + Ripple + Flow) behind one interface

> Reference: the C# `interactive_water_system` archive is the north star for
> Phase 2 architecture (strategy/composite patterns, event bus, singleton
> manager). Study it; don't copy blindly.
