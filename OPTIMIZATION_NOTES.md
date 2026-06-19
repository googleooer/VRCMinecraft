# VRCMinecraft — World/Chunk Gen + Render Optimization Notes

_Profiled and worked on the `gpu-optimizations` branch (scene `Assets/VRCMinecraft/scenes/Minecraft.unity`, benchmark world `worldDimension 32×8×32 = 8192 chunks`, chunk size 16×16×16). Unity 2022.3.22f1, ClientSim._

## 🟢 SESSION 2026-06-18 — GPU meshing (step 3): remove the mesh path's CPU-neighbour dependency

**Why this is THE durable fix:** the near-region target needs *meshed* chunks, and meshing currently reads neighbour block data from the CPU, so a GPU-resident chunk (no CPU mirror) can't mesh — which is why the readback can't be eliminated for the near region. Make the mesh path read neighbours from the **GPU atlas** (which already holds every resident chunk) and GPU-resident chunks mesh with no readback. That's what makes ClientSim and Quest agree.

**Huge discovery — most of step 3's shaders already exist, just UNWIRED:**
- `GpuSentinelBorderCopy.shader` — already does cross-slot neighbour reads from `gpuBlockAtlas` via the slot-lookup texture (the proven pattern: `LookupSlot` + `SampleAtlasBlock`).
- `GpuAOBake.shader` — computes per-vertex AO GPU-side from a sentinel RT. **Dead stub** — `_GpuBakeChunkAO` (McWorld.cs ~12277) is never called.
- `GpuVoxelFaceExtract.shader` already *contains* `LookupNeighborSlot` + `SampleBlockId` (atlas cross-slot reads) — it just used the CPU-packed `_BorderTex` for cross-chunk lookups instead.

**The two CPU-neighbour dependencies (audited):**
- **A. Face visibility:** `_GpuPackBorderData` (McWorld.cs ~1553) reads neighbour CPU blocks (`_GetBlockLocal`) into `gpuBorderTextures`; the face-extract shader samples `_BorderTex` for cross-chunk neighbours. **GPU-resident neighbours break this** (`_GetBlockLocal` returns air/rehydrate).
- **B. Ambient occlusion:** `_StartGpuBuildMeshFromFaceData` (McWorld.cs 2389-2398) calls `_DecompressNeighborsOnce` when `ambientOcclusion` is on — CPU neighbour blocks for AO corners. (Brightness is already gated behind `_UsesGpuTerrainLightSampling()`.)

**LANDED this session — Dependency A, flag-gated default-OFF (shader only, zero blast radius):** added `_UseCrossSlotNeighbors` to `GpuVoxelFaceExtract.shader`. When set, `GetNeighborBlockId` reads the cross-chunk neighbour straight from its atlas slot (`LookupNeighborSlot(chunkX±1,…)` + `SampleBlockId`) instead of `_BorderTex`; missing/non-resident neighbour → air → draw face (matches the old border semantics). `chunkX/Y/Z` come from `slotMeta` = **array coords**, which is exactly what the slot-lookup table is indexed by — verified against `_GpuGetLookupPixelIndex`. Shader edits do NOT trigger the UdonSharp/C# recompile, so this is lower crash-risk than the C# work. Toggle at runtime via `gpuFaceExtractMaterial.SetFloat("_UseCrossSlotNeighbors", 1)` — no C# change needed to validate.

**Validation plan for A (mesh has no terrain fingerprint):** with normal CPU-mirrored chunks, cross-slot must produce IDENTICAL face output to the border path (atlas was synced from the same CPU data). A/B it: mesh with the flag off vs on and compare per-chunk mesh vertex counts (or the face-mask readback). Then exercise it with an actual GPU-resident neighbour — where the border path returns air but cross-slot reads the atlas correctly. Validate visually in the editor scene view (it renders the world).

**REMAINING for step 3:**
- Wire A into C#: a flag that sets `_UseCrossSlotNeighbors` + skips `_GpuPackBorderData` when on (saves the CPU border pack entirely).
- **Dependency B (AO):** wire the existing `GpuSentinelBorderCopy` → `GpuAOBake` → AO RT path (`_GpuBakeChunkAO`), and make the CPU mesh build read AO from the RT instead of `_DecompressNeighborsOnce`. Sentinel RT is the shared neighbour-aware local copy both face-extract and AO can read.
- Then mark near chunks GPU-resident and confirm they mesh with no readback. Validate fingerprint + visual.

### User-reported rendering issues — diagnosed + culling fixed

User reported (1) fragmented rendering (chunks missing while farther ones render) and (2) chunk-boundary face culling failing sometimes. **Reproduced both with ALL my changes off (`dataGenLookaheadWindow=1`, `crossSlotFlag=0`) — neither is caused by the look-ahead/cross-slot.** Both trace to **meshing being far slower than data-gen** (`renderedChunks` ~232 vs `nearReady` ~835, a 3-4× gap).

- **Culling "fails sometimes" — FIXED (`requireAllNeighborsForMesh`, default ON, commit 4d562e2).** `AreAllNeighborsReady` deliberately treated *unallocated* (null) in-world neighbours as ready, so a frontier chunk meshed an air-boundary *wall* toward a not-yet-generated neighbour + set `_borderMissingMask` for a heal re-mesh; under load that heal queue backed up so the walls lingered. Fix: wait for all 6 neighbours data-ready (strict). The original lenient behaviour avoided a coordinator deadlock (all workers stuck in `STATE_WAITING_FOR_MESH`, none free to generate the neighbour) — solved instead by **defer-on-wait**: when neighbours aren't ready, defer the chunk and FREE the worker (→ data-gen); the deferred-mesh scan re-queues it when ready. Validated: heal queue `chunkRebuildQueue` 14 → ~1, no deadlock (workers `wait` 16 → 1-4, ~13 idle), terrain renders cleanly. Companion: `deferredInteriorMeshRequestsPerFrame` 1 → 8 + scan 64 → 128 so the now-deferred frontier chunks don't render-throttle (esp. at higher FPS).

- **Fragmentation — it's FPS-bound, the project's core perf wall.** Profiled the load: **~3-9 FPS during heavy gen+mesh** (~330 ms frames early). At that FPS the per-frame caps barely bind — raising `gpuMeshDecodeFrameBudgetMs` 4 → 20 moved the mesh rate only 1.14 → 1.21 chunks/s, and meshing had 9 idle workers (NOT worker- or budget-bound). The world fills in slowly because each frame is heavy (GPU blits + async-readback callbacks + CPU mesh build at ~3 FPS). **No per-frame tuning fixes this** — the durable fix is cutting per-chunk cost so frames get cheaper: GPU meshing (cross-slot face extract + GPU AO, removing the CPU neighbour decompress + the face readback round-trip) and the instanced-draw render path. That's exactly step 3 / #1 above. The culling fix trades a thin frontier ring rendering slightly later for permanently-correct boundaries.

---

## 🧪 SESSION 2026-06-17 — meshing-starvation hypothesis TESTED & DISPROVEN; data-gen is readback-bound (confirmed)

**Two real fixes landed (validated):**
1. **Console spam removed** — ClientSim's `ClientSimPlayerController.Teleport` logs `"Moving player to … (fromPlaySpace=…)"` every frame because NUMovement teleports the player each frame as its locomotion model. Commented out that one `this.Log(...)` (`Packages/com.vrchat.worlds/Integrations/ClientSim/Runtime/Player/ClientSimPlayerController.cs:242`). Editor-only (ClientSim never ships to the build); **gitignored package, so not committable — re-apply if the package is reinstalled.**
2. **Generator-duplicate bug** — the scene wired `terrainGenerator` (primary, slot 0) and `extraGenerators[6]` to the **same** `McTerrainGenerator` instance → 8 routing slots mapped to only **7 unique generators**, and slots 0 & 7 sharing one single-column generator is a latent terrain-corruption risk under concurrency. Rewired the duplicate slot to the orphaned 8th generator → **8 unique, fingerprint still `2213895521`** (byte-identical). ⚠️ **Fragile:** the proxy↔Udon sync during recompiles re-introduces the duplicate (the Udon heap's old refs overwrite the C# proxy on `CopyUdonToProxy`). After any recompile, re-verify uniqueness and `CopyProxyToUdon` + save. A robust fix would auto-collect+dedupe all scene `McTerrainGenerator`s at init instead of trusting the serialized array.

**Hypothesis tested:** "meshing starves data-gen" — data-gen + meshing share one worker pool (`maxConcurrentWorkers=16`), each worker carries a chunk through its *whole* lifecycle (data-gen→lighting→mesh). Observed all 16 workers in `STATE_MESHING` while `genSlotBusy=[00000000]`. The rule in `_ShouldPrioritizeChunkMesh` `if (neighbor==null || !neighbor.isDataReady) return true;` makes nearly every freshly-generated frontier chunk mesh eagerly during bulk gen. Added a flag-gated change (`deferFrontierMeshDuringLoad`) to defer frontier meshing during the load phase.

**Result: DISPROVEN — reverted.** Deferral *worked* (chunks deferred, `deferredMeshQueue` filled) but **did not speed up data-gen**, and caused visible **chunk fragmentation** (deferred meshes only fully drain after the *entire* 8192-chunk world finishes — far too slow). After reverting to stock behavior the decisive measurement: **12 / 16 workers IDLE, 0 in data-gen, `deferredMeshQueue=0`, `genSlotBusy=0`, and data-gen still ~4–8 chunks/s.** Spare worker capacity + idle generators + slow data-gen ⇒ **the worker pool was never the constraint.** This *confirms* the original diagnosis: **data-gen is async-readback-latency-bound with pipeline STALLS** — gaps where a readback has completed but the coordinator hasn't fed the next column to the generator, so everything sits idle.

**Sharper lever than "add more generators":** `genSlotBusy=0` (generators idle, not saturated) means adding generators won't help until the **assignment/pipeline gaps** are closed — keep all 8 generators continuously fed (prefetch the next column's GPU passes while the current readback is in flight) — or the readback is eliminated (the GPU-resident migration below). Adding generators only helps *after* generators are actually saturated.

**ROOT CAUSE FOUND — head-of-line blocking in the dispatcher (FIX LANDED, Quest-targeted):** instrumented the live VM and caught it directly — data-gen runs at **concurrency 1** (`genBusy ∈ {0,1}`, `DATAGEN_workers ∈ {0,1}`, `nextChunkIndexToAssign == _nearChunksReady` in lockstep = zero pipeline depth), never the 8 it should. Cause: `McCoordinator.Update` Priority-2 assignment only ever looked at `radialChunkOrder[nextChunkIndexToAssign]`. A column's 8 Y-chunks are consecutive in that order; while the column's first chunk is mid-readback its 7 siblings can't start (cache not ready) and the cursor **doesn't advance**, so the coordinator stalls on the in-flight column instead of starting other columns on the 7 free generators — serializing 8 generators down to 1.

**Fix (committed):** added a look-ahead scan `_TryPickDataGenPosition()` + `_positionAssigned[]` low-water-mark in `McCoordinator`. When the head column's siblings are still waiting on their readback, it scans forward (window `dataGenLookaheadWindow=96`) for the first chunk that can start NOW — a free generator (new column) or a sibling whose column cache is ready — and starts that. Forward-from-low-water-mark order guarantees a column's cached siblings (earlier positions) are always picked before any later column that reuses the same generator, so the single-column cache is never evicted under un-drained siblings. **Worst-case failure mode is a redundant column re-gen (identical deterministic data), never corruption** — fingerprint re-validated **`sum=2213895521`** after the change.

**Why ClientSim can't show the win (and why it's still right):** in ClientSim the readback runs on a fast desktop GPU, so columns complete almost immediately — there's no ~244 ms latency to overlap, so the look-ahead rarely needs to skip ahead and the measured rate is unchanged (~8 ch/s). On **Quest**, where the readback genuinely costs ~244 ms, the old code capped data-gen at 1 column in flight; the fix lets multiple overlap → higher data-gen throughput. The GPU passes are per-frame-budget-throttled (`gpuWorldgenStepBudgetMs`/`gpuWorldgenStepsPerFrame`), so concurrent columns overlap their *readback waits* without spiking the GPU. **MUST benchmark on the headset** to confirm — and ideally re-check the fingerprint on-device, since ClientSim validates correctness only under its non-concurrent timing (the order-based coherency argument is platform-independent, but on-device concurrency is unexercised here).

**VALIDATED in ClientSim via a latency proxy (no code change):** slowed each column's GPU passes at runtime (`gpuWorldgenStepsPerFrame=1`, `gpuWorldgenStepBudgetMs=0.4`) so generators are held many frames — the same head-of-line condition that readback latency creates on Quest. Then A/B-toggled the fix live via `dataGenLookaheadWindow` (it's a runtime knob: **1 = original strictly-sequential behavior, 96 = look-ahead**). Measured pipeline depth as "holes" = `count(_positionAssigned) - nextChunkIndexToAssign` (positions started *ahead* of the blocked low-water-mark — something only the look-ahead can do):
- **window=1 → 0 holes** (sequential; stalls on the in-flight column — reproduces the bug).
- **window=96 → sustained ~10 holes** (starts ~10 chunks past the blocked column the old code would stall on).

So the mechanism is confirmed: on Quest those ~10 become concurrent in-flight readbacks instead of 1.

**SECOND limiter found (next lever): meshing starves data-gen of WORKERS.** Even with the look-ahead engaged, most of the 16 coordinator workers sit in `STATE_MESHING` (near-player shell + frontier), so data-gen only gets a few workers and realized concurrency stays ~2–3, not the full 8.

- **Raising `maxConcurrentWorkers` does NOT fix it — tried 16→24, reverted.** Meshing is *not* capped by the 16-buffer face-readback FIFO the way I assumed; it **expands to fill whatever workers exist** (saw all 24 in `STATE_MESHING`, `genBusy=0`), so the extra workers went to meshing and data-gen got *less* (holes dropped 10→2). Net regression for data-gen + more memory. Reverted to 16. (Fingerprint stayed `2213895521` at 24 workers — the regression was throughput, not correctness.)
- **The real fix is an explicit worker RESERVATION for data-gen** (cap concurrent meshing during the load phase) — **IMPLEMENTED, flag-gated, default OFF** (`reserveWorkersForDataGenDuringLoad` / `loadPhaseMeshWorkerCap=8` on McCoordinator). When a worker would start meshing but `_CountMeshingWorkers() >= cap` during the load, it `MarkChunkMeshDeferred` + frees the worker (→ data-gen). The subtlety the failed deferred-frontier attempt missed: the cap only fires for meshes that already *passed* `AreAllNeighborsReady` (coordinator line ~311), so the deferred ones have ready neighbours and the scan + Priority-3 wake them (shell chunks eagerly). Frontier chunks (neighbours not ready) never reach the cap, so they're untouched. `interactionMeshPriority` (player edits) bypass.
  - **Validated in ClientSim (flag ON):** meshing capped exactly at 8 (was 15–16) → **7 workers freed**; fingerprint preserved **`2213895521`**; `deferredMeshQueue` bounded (≤256). So it does what it says — reserves the pool for data-gen.
  - **Tradeoff (why default OFF):** with only 8 mesh workers, the visible world meshes slower than data-gen produces during the load, so the deferred queue backs up (visible chunks lag, then catch up once data-gen finishes and the cap lifts). Acceptable when the goal is data-ready-ASAP; not free.
  - **Quest benefit is unprovable in ClientSim:** the freed workers showed *idle* (`DG=1`), because ClientSim's fast desktop-GPU readback lets data-gen keep up with ~1 worker — there's no latency-bound backlog for the freed workers to chew through. The step-throttle latency proxy used for the look-ahead does NOT hold the generators here (the readback, not the GPU passes, dominates column time). On Quest (~244 ms readback) the freed workers + the look-ahead should give up to 8 concurrent columns. **Enable + headset-benchmark to confirm.**
  - **Net:** look-ahead (default ON, no downside, fixes concurrency-1) + reservation (default OFF, frees workers, load-phase visible-meshing tradeoff) together are the in-place path to the near-region target on Quest. The durable fix is still the GPU-resident migration (eliminate the readback).

**ClientSim measurement caveats (important, cost hours this session):**
- **Proxy vs VM heap:** in play mode the C# `McWorld` MonoBehaviour (proxy) fields read STALE/default (`chunks_1D=null`). Read live state from the Udon VM: `UdonSharpEditor.UdonSharpEditorUtility.GetBackingUdonBehaviour(world).GetProgramVariable("name")`. `ChunkData` is stored as `object[]` in the VM (field-name reflection fails on VM chunk objects).
- A Unity **crash** mid-session left UdonSharp's incremental compiler with a **stale program** (new public field missing from the symbol table → `GetProgramVariable` returns null + "Field for System.Boolean does not exist" on `CopyProxyToUdon`). Fix: force `UdonSharp.Compiler.UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions())` (heavy; blocks Unity's main thread so the MCP bridge times out for ~20–40s — wait it out, Unity recovers). Verify via `programAsset.SerializedProgramAsset.RetrieveProgram().SymbolTable.GetSymbols()`.
- Set `Application.runInBackground=true` and DON'T touch `Time.fixedDeltaTime` before the world inits (throttling it early stalls ClientSim's player bootstrap). Absolute ClientSim timings may not represent real-hardware/Quest; **benchmark gen on the headset before trusting numbers.**

---

## ⏳ ACTIVE: GPU-resident pipeline migration (#2 + #1) — eliminate the readback

**Root cause (profiled):** worldgen is ~100% **async-GPU-readback-latency bound** — per column ~12 ms GPU compute, ~1 ms CPU, **~244 ms waiting on `VRCAsyncGPUReadback`**. The data is generated on the GPU, dragged to CPU (the whole cost), then re-uploaded to the GPU atlas. Heavy *compute* is already GPU; the readback is data-transfer. Scaling generators / scheduler tweaks only *overlap* the readback (band-aid). The real fix is to not read back at all for chunks the CPU doesn't touch.

**Why it's a migration, not a toggle:** the readback also feeds `_RefreshChunkDerivedData` (cross/torch positions, column bounds, flags) that *meshing* needs, plus 5 CPU read-paths. Nulling `_chunkData` blindly = invisible chunks (the team's prior failure).

**Audit results — what reads block data on CPU (file:line):**
- SAFE to go GPU-resident: meshing (reads `gpuBlockAtlas`, not `_chunkData`), collision (mesh-based), derived caches (computed once at gen-completion).
- BREAKS if `_chunkData` null (must gate/rehydrate): raycast/targeting `ModifyTerrain.cs:264`, `SetBlock` `McWorld.cs:~9090`, ticking `McBlockTicker.cs:543/777/1118+`, particles `McParticleManager.cs:277/358/804/936`, CPU lighting fallback.
- Scaffolding exists but is UNWIRED stubs: `_isGpuResident`, rehydrate queue (`_GpuRequestRehydrate`/`GpuMaintainRehydrationQueue`/`_GpuRehydrateInline` — never called, no real readback), `cpuMirrorRadiusXZ/Y`, `ChunkNeedsCpuMirror`.

**Ordered migration plan (each step harness-validated `sum=2213895521`):**
1. **Read-path safety net (flag-gated, default off):** wire `GpuMaintainRehydrationQueue` into Update; implement real atlas-slot→CPU readback in `_GpuRehydrateInline`; make `_GetBlockLocal`/SetBlock/tick/particle paths rehydrate-or-skip on `_isGpuResident`; gate mutators to `cpuMirrorRadius`. Inert when off.
2. **Defer derived caches + readback to mesh-time:** far chunks skip `_RefreshChunkDerivedData` + the column readback at gen-time; GPU column data goes GPU→GPU into the atlas (Blit/repack shader). `_chunkData` stays null until the chunk is meshed/approached.
3. **GPU-compute derived data (cross/torch/water + bounds)** so the GPU mesh path needs no CPU bytes — pairs with #1 (GpuVoxelQuadDraw instanced render). Near chunks then mesh GPU-side without a readback.
4. **Result:** bulk gen becomes GPU-compute-bound (~12 ms/col); near-region target met once near chunks also mesh GPU-side. Collision lazy-rehydrates as the player moves.

---

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

### #6 Parallelize terrain generation — ✅ IMPLEMENTED & VALIDATED (two-generator concurrency)
**Status: DONE.** Phase A (column routing) + Phase B1 (per-slot coordinator) + Phase B2 (2nd generator instance wired) are committed. Validated byte-identical terrain (fingerprint `2213895521`) with TWO real concurrent generators, and a measured throughput improvement (~758 vs ~634 chunks at the same point). `[Singleton]` was removed from McTerrainGenerator so VRRefAssist stops auto-injecting `terrainGenerator2`. Scalable: add `terrainGenerator3/4` + route on `cx%N` for more concurrency. Get a clean before/after gen-time on the headset; tune generator count vs. VRAM (each generator allocates its own column RTs).

_Original analysis (kept for reference):_

### #6 Parallelize terrain generation — ⭐ CONFIRMED the dominant gen-throughput limiter
- **Status:** `isGeneratorBusy` in `McCoordinator` serializes terrain data-gen to one column at a time; the GPU is idle waiting. Empirically confirmed: during gen, frames sit ~22 ms (well under budget) — the system is stalling on this serialization + readback latency, not on per-frame compute. This is the single highest-value gen speedup. `McTerrainGenerator` (5,347 lines) has a single working-buffer set, so true multi-column concurrency needs multiple generator states — a real refactor.
- **Risk:** determinism-sensitive; **cannot be cleanly benchmarked in ClientSim** (NRE flood). Needs careful staging + hardware/clean-bench validation. The load-phase budget boost (landed) is the lower-risk lever that partially addresses the same goal.

- **Root cause (precise):** `McTerrainGenerator` is single-column throughout. `StartChunkGeneration` (~3502) sets `currentChunkX/Y/Z`, `currentState` (GenerationState enum), `cacheCoordX/Z`; the GPU pipeline writes `gpuColumnBaseTexture → gpuColumnSurfaceInfoTexture → gpuColumnFinalTexture`, then `VRCAsyncGPUReadback.Request` (~2107) fills the single `gpuColumnReadbackBlocks`. The radial chunk order visits all 8 Y-chunks of a column consecutively, so only the FIRST chunk of each column pays the GPU+readback cost; siblings drain from the column cache (`canUseGpuCachedColumn`). The serial cost ≈ `(GPU passes + readback latency) × ~1024 columns`, and the GPU sits idle during each readback wait.

- **Design to fix (K-deep column pipeline, recommend K=2–3):** turn the single-column state into a small ring of K "column slots", so column N+1's GPU passes run while column N's readback is in flight. Each slot needs its OWN copy of: `gpuColumnBase/SurfaceInfo/Final` RTs, the readback buffer + `gpuColumnReadback{Pending,Ready}` + `gpuPendingColumnX/Z`, `currentState`, `currentChunkX/Y/Z`, `cacheCoordX/Z` + cached column data, `workingChunkData`, AND the determinism-critical per-column state: the noise caches, `initRand`/RNG state, `columnDepthCache`, `columnFillerCache`, `highestStoneYColumn`, biome inputs. The coordinator's `isGeneratorBusy` (McCoordinator) becomes a count (`< K`), and `StartChunkDataGeneration`/`StepChunkDataGeneration`/`TryCopyCachedGpuChunkSlice` take a slot index.
- **Why NOT done blind:** sharing any per-column state across slots silently corrupts terrain (wrong blocks/biomes in some columns) — a subtle bug that screenshots may not catch. **Must validate** same-seed output is byte-identical to pre-refactor for a sample of chunks. This is multi-day, determinism-critical work; do it incrementally with per-stage validation, not in one pass.

#### ✅ Safety net is now built: `debugTerrainChecksum` harness (committed `1aa8a75`)
Enable `McWorld.debugTerrainChecksum`, let gen run, then `SendCustomEvent("DebugLogTerrainFingerprint")` → logs `[TERRAIN_FP] sum=<n> done=<k>/<N>`. **Reference for seed "Glacier": `sum=2213895521`, `done=32/32`.** Any generator change that keeps this sum identical preserves terrain exactly. This converts the refactor from "unsafe blind rewrite" into "validated incremental change."

#### Recommended implementation path — TWO generator instances (safer than internal K-deep)
Instead of making one `McTerrainGenerator` internally K-deep (rewriting its whole single-column core + the `wcm` biome manager), **run 2 `McTerrainGenerator` instances and route columns between them** (round-robin by column, all 8 Y of a column → same instance to keep its column cache). Each instance is already self-contained and deterministic.
- **Failure mode is PERFORMANCE, not corruption** — a routing bug just causes a generator to regenerate a column (cache miss), so the harness fingerprint stays correct. Far safer than the internal refactor whose failure mode is corruption.
- **Gotcha:** `McTerrainGenerator` is `[Singleton]` (line 55) — VRRefAssist will conflict on a 2nd instance. Either remove `[Singleton]` (verify the only reference is `McWorld.terrainGenerator`, a serialized field — grep confirms it is) or exclude the 2nd instance from injection.
- **Steps:** (1) add `McWorld.terrainGenerator2` (null ⇒ behaves exactly as today — fingerprint must stay `2213895521`); (2) route the 4 generator calls (`StartChunkDataGeneration`, `StepChunkDataGeneration`, `CanCopyCachedGpuChunkSlice`, `TryCopyCachedGpuChunkSlice`) through a `_GeneratorForColumn(cx,cz)` helper; (3) make `McCoordinator.isGeneratorBusy` per-generator (allow 2 exclusive columns in flight); (4) wire a 2nd generator in the scene + init with the same seed; (5) validate fingerprint unchanged, then measure the gen-time drop. Scale to 3–4 instances if 2 helps.

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
