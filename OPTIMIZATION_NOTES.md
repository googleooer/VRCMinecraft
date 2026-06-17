# VRCMinecraft — World/Chunk Gen + Render Optimization Notes

_Profiled and worked on the `gpu-optimizations` branch (scene `Assets/VRCMinecraft/scenes/Minecraft.unity`, benchmark world `worldDimension 32×8×32 = 8192 chunks`, chunk size 16×16×16). Unity 2022.3.22f1, ClientSim._

## TL;DR — the real bottleneck

Despite 9 GPU offloads all reporting **GPU-READY**, world/chunk generation is **CPU-main-thread bound inside the Udon VM, with the GPU ~92% idle.**

Measured frame timing **during generation** (Unity FrameTimingManager, sampled repeatedly):

| | Baseline | After landed changes |
|---|---|---|
| CPU main thread | **~50 ms/frame** | ~16–25 ms typical (spikes to ~68 ms) |
| GPU | **~4 ms/frame** | rose to ~13 ms (more work flowing to GPU) |
| Idle (post-gen) | 8 ms CPU / 1 ms GPU | — |

**Implication:** the wins come from *cutting/parallelizing main-thread Udon work* and *feeding the idle GPU more* — NOT from optimizing shaders. The single largest remaining main-thread cost is the **CPU mesh build/apply**, because the GPU instanced-draw path (below) is a dead stub, so every rendered chunk is meshed on the CPU.

## Architecture facts worth knowing

- Generates **data for all 8192 chunks** (GPU worldgen + async readback) but only builds **render meshes for the visible shell** near the player (`prioritizeVisibleShellMeshing`). Full data-gen takes minutes.
- Each chunk is a `Chunk_(x,y,z)` GameObject with 4 child mesh filters (opaque/transparent/cutout + collider).
- **No chunk eviction** — fixed world fully loads; per-chunk RTs are never freed (not a leak, but a fixed cost).
- GPU atlas = `gpuChunkSlotCapacity = 1023` slots (≈ tuned to the resident radius).
- RenderTextures at runtime: ~249 RTs ≈ **228 MB VRAM** (heavy for Quest 2's ~4 GB).
- **ClientSim editor caveat:** `UdonManager.FixedUpdate` (and `ClientSimPlayerController.GetSpeed`) throw NullReferenceException every FixedUpdate — an editor/ClientSim artifact (the custom NUMovement controller leaves ClientSim state null). Won't occur in the real client, but it floods/evicts the console. Workaround while profiling: `Time.fixedDeltaTime = 2.0` at runtime to throttle FixedUpdate. Frame timing via the profiler is the reliable, eviction-immune metric.

---

## ✅ LANDED (safe, accuracy-preserving, compiles clean)

1. **Readback throughput cap 2 → 8** — `GPU_FACE_READBACKS_PER_FRAME` in `McWorld.cs`. The 16-buffer design intended ~8/frame; the cap was throttling GPU-meshed chunks to ~2/frame. Still bounded by frame budget + mesh-pool availability.
2. **Profiling instrumentation OFF** — disabled `enableVerboseLogging / enableGenerationTimings / enableAggregateLogging / enableDetailedTimings / enableCounters / enableMemoryTracking / enableCacheTracking` on McWorld, McCoordinator, McTerrainGenerator (scene saved). Removes per-frame/per-chunk `DateTime.UtcNow` timing, stat counters, and StringBuilder summaries from the Udon main thread. **Reversible in the inspector** — re-enable `enableAggregateLogging` if you want the Performance Summary back for dev benchmarking. (The `[BENCHMARK] 50%/100% world gen at Xs` lines are unconditional and still log.)
3. **Load-phase budget boost** — new serialized `loadPhaseUpdateBudgetMs` (default 45 ms) on **McWorld** and **McCoordinator**. While the one-time initial gen runs, the per-frame `updateTimeBudgetMs` is raised to this value so the adaptive budget system lets the decode/worldgen loops do far more per frame (they were pinned to the floor because gen frames exceed the 16 ms target). Auto-restored to the normal budget once gen completes. Frame rate during the one-time load is intentionally traded for faster loading; accuracy unaffected. Set `loadPhaseUpdateBudgetMs <= updateTimeBudgetMs` to disable.

   > **Empirical follow-up (important):** with the boost active, gen frames measured only **~22 ms — far below the 45 ms budget.** That means generation is **NOT budget-limited** — it's stalling on the **exclusive-generator serialization + async-readback latency**. So this boost (and the readback-cap bump) are safe but **marginal**; they only help the occasional heavy mesh-apply frame (~68 ms). **The dominant throughput limiter is #6 below.** Verified: terrain still generates correctly (sand/grass/water all correct), gen actively advances (~15–20 chunks/s), no regression.

---

## 🔬 NOT shipped — why, and the plan

### #1 GPU instanced draw — biggest *potential* render win, but a research-grade build
- **Status:** `GpuRenderInstancedQuadsForChunk` (`McWorld.cs:~11735`) is **never called**; `ChunkData._gpuQuadFaceBufferRT` / `_gpuQuadFaceCount` are **never populated**. `gpuVoxelQuadMesh` is mis-assigned to a Unity built-in mesh (null in Udon at runtime). The shader (`shaders/GpuVoxelQuadDraw.shader`) IS complete.
- **The hard part:** the shader wants a *dense per-face* buffer `(x|y, z|face, blockId, ao|light)`. Producing that requires **GPU-side face compaction** (variable-length output), and VRChat has **no compute shaders / append buffers** — so this is a genuine research problem.
- **Why a CPU repack is NOT the answer:** doing the compaction on the CPU re-iterates all voxels (the expensive part), so it would *not* beat the existing CPU mesh path and could regress. And per-draw-call overhead on Quest 2 may make instancing slower than CPU meshing regardless — **must be benchmarked on the headset.**
- **If pursued, staged plan:**
  1. Generate a real unit-quad `Mesh` in code at init (assign to `gpuVoxelQuadMesh`; removes the null-mesh blocker — this part is safe & cheap).
  2. Solve compaction. Best lead: a multi-pass prefix-sum / scatter scheme across RTs, or a fixed-grid "one pixel per (voxel,face)" buffer with the shader skipping empty slots (wastes instances but avoids compaction). Validate the packing format matches `GpuVoxelQuadDraw.shader` lines 77–87.
  3. Populate `_gpuQuadFaceBufferRT` + `_gpuQuadFaceCount`; verify `_AtlasUVTex` and `_MainTex` are bound (the draw helper only sets `_FaceBuffer`/`_BiomeColorRT` on the MPB — the rest must be on the material).
  4. Per-frame draw loop (LateUpdate) over visible chunks, behind a default-OFF flag; disable the CPU MeshRenderers for chunks drawn via instancing (avoid double-render).
  5. Validate visual correctness in the **editor scene view** (it renders the world — iterate via screenshots), then benchmark **on Quest** before committing to it.

### #6 Parallelize terrain generation — ⭐ CONFIRMED the dominant gen-throughput limiter
- **Status:** `isGeneratorBusy` in `McCoordinator` serializes terrain data-gen to one column at a time; the GPU is idle waiting. Empirically confirmed: during gen, frames sit ~22 ms (well under budget) — the system is stalling on this serialization + readback latency, not on per-frame compute. This is the single highest-value gen speedup. `McTerrainGenerator` (5,347 lines) has a single working-buffer set, so true multi-column concurrency needs multiple generator states — a real refactor.
- **Risk:** determinism-sensitive; **cannot be cleanly benchmarked in ClientSim** (NRE flood). Needs careful staging + hardware/clean-bench validation. The load-phase budget boost (landed) is the lower-risk lever that partially addresses the same goal.

- **Root cause (precise):** `McTerrainGenerator` is single-column throughout. `StartChunkGeneration` (~3502) sets `currentChunkX/Y/Z`, `currentState` (GenerationState enum), `cacheCoordX/Z`; the GPU pipeline writes `gpuColumnBaseTexture → gpuColumnSurfaceInfoTexture → gpuColumnFinalTexture`, then `VRCAsyncGPUReadback.Request` (~2107) fills the single `gpuColumnReadbackBlocks`. The radial chunk order visits all 8 Y-chunks of a column consecutively, so only the FIRST chunk of each column pays the GPU+readback cost; siblings drain from the column cache (`canUseGpuCachedColumn`). The serial cost ≈ `(GPU passes + readback latency) × ~1024 columns`, and the GPU sits idle during each readback wait.

- **Design to fix (K-deep column pipeline, recommend K=2–3):** turn the single-column state into a small ring of K "column slots", so column N+1's GPU passes run while column N's readback is in flight. Each slot needs its OWN copy of: `gpuColumnBase/SurfaceInfo/Final` RTs, the readback buffer + `gpuColumnReadback{Pending,Ready}` + `gpuPendingColumnX/Z`, `currentState`, `currentChunkX/Y/Z`, `cacheCoordX/Z` + cached column data, `workingChunkData`, AND the determinism-critical per-column state: the noise caches, `initRand`/RNG state, `columnDepthCache`, `columnFillerCache`, `highestStoneYColumn`, biome inputs. The coordinator's `isGeneratorBusy` (McCoordinator) becomes a count (`< K`), and `StartChunkDataGeneration`/`StepChunkDataGeneration`/`TryCopyCachedGpuChunkSlice` take a slot index.
- **Why NOT done blind:** sharing any per-column state across slots silently corrupts terrain (wrong blocks/biomes in some columns) — a subtle bug that screenshots may not catch. **Must validate** same-seed output is byte-identical to pre-refactor for a sample of chunks. This is multi-day, determinism-critical work; do it incrementally with per-stage validation, not in one pass.

### #5 Render-texture memory (~228 MB)
- **Status:** dominated by **4 global atlases (~138 MB)**: `gpuBlockAtlas` + `gpuLightAtlas` + their scratch buffers, sized by `gpuChunkSlotCapacity = 1023`.
- **Knob already exposed:** lowering `gpuChunkSlotCapacity` shrinks all four — but it's roughly tuned to the GPU-resident radius, so shrinking too far causes thrashing. Tune against `gpuResidentRadiusXZ/Y`.
- **Other options (refactors, correctness risk):** pool/share per-chunk AO (`_gpuAORT`) and sentinel (`_gpuSentinelRT`) RTs instead of one-per-chunk; smaller formats; free per-chunk RTs if/when an eviction system is added. None are safe blind edits — they need a design decision.

---

## Suggested next steps (in order)
1. Re-enable `enableAggregateLogging` temporarily and run the benchmark to quantify the landed gains (gen time + the Performance Summary's CPU breakdown).
2. Tune `loadPhaseUpdateBudgetMs` (try 45 → 80) and `gpuChunkSlotCapacity` (VRAM vs. thrash) on the headset.
3. If pursuing instanced draw (#1), do the staged plan above with editor-scene-view validation, then a Quest benchmark — it's the only path to remove the dominant CPU mesh-apply cost.

_See also the agent memory: `perf-cpu-bound-diagnosis`, `clientsim-benchmark-observability`._
