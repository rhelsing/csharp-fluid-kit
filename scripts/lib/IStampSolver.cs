using Godot;

namespace GodotCsharpExperiments.Lib;

// Common surface for the swappable GPU solvers (GpuStampSolver's Jacobi/RBGS/CG and
// the separate MgvSolver), so a scene can hold any of them behind one field and the
// solver dropdown can recreate across families.
public interface IStampSolver
{
    bool Ready { get; }
    Rid HeightRid { get; }        // current field, for display via Texture2Drd
    Rid PrevRid { get; }          // h(t-dt) — with HeightRid gives ∂h/∂t consumers a velocity
    float LastResidual { get; }   // ‖b − Ax‖ from the last measured tick
    string ModeName { get; }
    void Step(byte[] pushConstant, int iters, bool measureResidual);
    int PassesPerStep(int iters);  // dispatches per Step — the honest cost unit

    void Free();
}
