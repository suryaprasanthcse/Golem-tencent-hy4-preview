# GOLEM

**Every AI-generated 3D asset is a statue. GOLEM makes it move.**

GOLEM turns a static AI-generated prop (a chest, a cabinet, a laptop) into an interactive Unity asset with real mechanical joints: the lid swings on the right hinge, doors open, drawers slide, all simulated with Unity `ArticulationBody` physics.

Built for the Cambridge × Arcade AI Hackathon, Game Tech track (Tencent Cloud × Hyper3D), 3–4 October 2026.

## Design principle: the model chooses, the geometry computes

AI models only make discrete choices: what a part is, which part it hangs from, the joint type, and which candidate hinge is right. Geometry computes everything continuous: pivots, axes, masses and joint limits.

## Pipeline

| Stage | What happens |
|---|---|
| 1. Generate | Text prompt → Hyper3D Rodin Gen-2.5 through the official CLI (browser sign-in; `hyper3d_client.py`) → `.glb` |
| 2. Split | The layered segmenter (below) separates moving parts from the body |
| 3. Candidates | Oriented boxes per part → candidate hinge and slide axes |
| 4. Choose | A model labels parts and joint types; geometry and a sweep test pick the hinge |
| 5. Solve | Snap the hinge to the part/parent seam, rescale to real-world size, compute mass, colliders and limits |
| 6. Unity | glTF parts with pivots plus a joint spec → `ArticulationBody` chain, with drag interaction |

## The layered segmenter

Splitters are interchangeable modules behind one interface, so any tier can be swapped in or out with a configuration change.

| Tier | Splitter | Used for |
|---|---|---|
| 1 | **Hyper3D BANG** (cloud) | Complex, multi-part mechanical assets |
| 2 | **Headless Blender cutter** (`golem_cutter.py`, local) | Boxy shapes with planar seams (chests, flat doors): no cloud latency |
| 3 | **Tencent Hunyuan3D-Part** (P3-SAM, through its Hugging Face Space) | Tencent's segmentation model |
| Hot-swap | **Tencent HY-3D-Component** (TokenHub) | Moves to tier 1 once Tencent Cloud access clears |

Every split must pass an acceptance check before it's used: the moving part is its own piece, it touches its parent, and there are no stray fragments. A split that fails moves to the next tier.

## Compliance

All code in this repository was written after hacking began at 15:30 IST on 3 October 2026. Design notes and discussions were prepared before the event. The code was written with AI coding agents (Claude).
