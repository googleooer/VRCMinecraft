# Voxel Rethink — Phase 0 + Phase 1 (Baseline + VM Merge) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the McCoordinator↔McWorld cross-VM extern tax (~100–200 cross-VM calls/frame, ~44 ms/cycle coordinator cost) by folding McCoordinator's worker/scheduler logic into McWorld as a single in-VM loop, with no behavior change.

**Architecture:** McCoordinator and McWorld are two `UdonSharpBehaviour`s with independent `Update()` loops that both touch the shared `McWorld.chunks_1D` array; coupling is one-way (Coordinator→World). We move the worker state machine, the data-gen picker, and AssignWork into McWorld, replace every `world.X()` call with a direct in-VM call, drive it all from `McWorld.Update()`, then delete McCoordinator. Phase 0 first locks a measurable baseline so Phase 1's win is provable.

**Tech Stack:** Unity + VRChat SDK3 (Worlds) + UdonSharp; ClientSim for in-editor runtime; UnityMCP for compile/console/playmode control. Spec: `docs/superpowers/specs/2026-06-19-voxel-engine-merge-soa-rethink-design.md`.

---

## Verification Methodology (read first — replaces standard TDD)

There is **no automated runtime test harness** for Udon code. Every task is gated by:

1. **Compile gate.** After edits, trigger a Unity asset refresh and read the console for UdonSharp compile errors:
   - UnityMCP: `refresh_unity`, then `read_console` (filter type = Error). Or poll `editor_state.isCompiling` until false, then `read_console`.
   - Expected: zero compile errors, zero new warnings from touched files.
2. **Behavior/perf gate.** Enter ClientSim play mode, let the world stream for ~30 s standing still, capture the engine's `=== Performance Summary ===` block from the console, and compare against the recorded baseline (Task 0). "Behavior-preserving" means: same chunk-load progress curve, same final loaded set, no new errors, and the targeted cost metric moved in the expected direction.
   - UnityMCP: `manage_editor` to enter/exit play mode; `read_console` to capture the summary.

**Commit after every task.** Each task is independently revertable. Work happens on branch `gpu-optimizations` (already active). Do NOT commit the unrelated `Assets/VRCMinecraft/scenes/Minecraft.unity` working-tree change unless a task explicitly says so — stage only the files named in that task.

**UdonSharp constraints to respect everywhere** (from CLAUDE.md): no local method declarations, no multidim arrays (use jagged), no `try/catch`, no `gameObject.AddComponent<>()`, no nested type declarations; never use post-increment/decrement on an object field as an expression (read then assign on separate lines). Check `udon_blacklisted.txt` / `udon_whitelisted*.txt` before relying on any API.

---

## File Structure

- `Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs` — gains the scheduler/worker logic. If U# partial classes are confirmed (Task 1), the moved logic goes in a new partial file; otherwise into a `#region Scheduler (merged from McCoordinator)` in this file.
- `Assets/VRCMinecraft/Code/VoxelEngine/McWorld.Scheduler.cs` — **(created only if partial classes work)** holds the merged worker state machine, picker, AssignWork, and worker-pool fields as `public partial class McWorld`.
- `Assets/VRCMinecraft/Code/VoxelEngine/McCoordinator.cs` — emptied of logic, then deleted in Task 9.
- `Assets/VRCMinecraft/scenes/Minecraft.unity` — scene wiring updated when McCoordinator is removed (Task 9). Treated carefully; may require in-editor inspector work by the user.
- `docs/superpowers/baselines/2026-06-19-baseline.md` — **(created)** recorded baseline metrics (Task 0).

---

## Task 0: Lock the baseline (Phase 0)

**Files:**
- Create: `docs/superpowers/baselines/2026-06-19-baseline.md`
- Modify (optional, if a per-frame cross-VM counter is not already derivable): `Assets/VRCMinecraft/Code/VoxelEngine/McCoordinator.cs` (add a counter — see Step 3)

- [ ] **Step 1: Capture a baseline Performance Summary**

Enter ClientSim play mode, stand still, let it stream ~30 s, then capture the full `=== Performance Summary ===` block.
- UnityMCP: `manage_editor` (action: enter play mode) → wait ~30 s → `read_console` (capture the summary) → `manage_editor` (exit play mode).
Expected: a summary block including `Update: avg … ms`, `Coordinator: Avg cycle … ms, update workers …, assign work …`, `Assign breakdown … pick … startGen … siblingStep …`, and `Progress: N/8192`.

- [ ] **Step 2: Record the baseline numbers**

Create `docs/superpowers/baselines/2026-06-19-baseline.md` with the captured summary verbatim plus a one-line extraction of the key metrics we will beat:

```markdown
# Baseline — 2026-06-19 (pre-merge)

Captured: ClientSim, standing still, ~30s stream.

- Coordinator avg cycle: <X> ms  (update workers <A> ms + assign work <B> ms)
- Assign breakdown: pick <P> ms, siblingStep <S> ms
- McWorld Update avg: <U> ms
- Load throughput: <C> chunks/s  (Progress delta / elapsed)
- Unity profiler ManagedUpdate self (if captured): <M> ms

<full Performance Summary block pasted here>
```

- [ ] **Step 3: Add an explicit cross-VM call counter (the Phase 1 success metric)**

In `McCoordinator.cs`, add a per-frame counter incremented at each call site that invokes a `world.*` method, and log it in the existing aggregate log. This is the metric that must reach ≈0 after the merge. Add near the other `#if LOGGING` aggregate fields:

```csharp
#if LOGGING
    private int dbg_crossVmCallsThisCycle = 0;
    private int dbg_crossVmCallsAccum = 0;
    private int dbg_crossVmCycles = 0;
#endif
```

Increment it (guarded by `#if LOGGING`) immediately before each `world.<Method>(...)` call in `Update()`/`UpdateWorkers()`/`AssignWork()`/the picker. In the aggregate log emit:

```csharp
#if LOGGING
    Debug.Log($"[McCoordinator] cross-VM calls/cycle avg {(dbg_crossVmCycles>0 ? (float)dbg_crossVmCallsAccum/dbg_crossVmCycles : 0f):F1}");
    dbg_crossVmCallsAccum = 0; dbg_crossVmCycles = 0;
#endif
```

(If this is too invasive to thread through every call site, instead wrap the cross-VM surface — see Task 7 — and count there. Either way, record the baseline cross-VM/cycle number into the baseline doc.)

- [ ] **Step 4: Compile gate**

Run: UnityMCP `refresh_unity` then `read_console` (Error filter).
Expected: zero errors.

- [ ] **Step 5: Capture baseline cross-VM number, then commit**

Re-run Step 1 briefly to capture `cross-VM calls/cycle avg <V>`; add `<V>` to the baseline doc.

```bash
git add docs/superpowers/baselines/2026-06-19-baseline.md Assets/VRCMinecraft/Code/VoxelEngine/McCoordinator.cs
git commit -m "chore(voxel): lock pre-merge baseline + cross-VM call counter"
```

---

## Task 1: Confirm UdonSharp partial-class support (spike)

**Files:**
- Create (temporary): `Assets/VRCMinecraft/Code/VoxelEngine/McWorld.Scheduler.cs`

- [ ] **Step 1: Create a trivial partial file**

```csharp
using UdonSharp;
using UnityEngine;

public partial class McWorld
{
    // Partial-class support spike. If this compiles, the merged scheduler lives here.
    private void _PartialClassSpikeNoop() { }
}
```

Also confirm `McWorld`'s primary declaration is `public partial class McWorld : UdonSharpBehaviour` — temporarily add `partial` to the `class McWorld` declaration in `McWorld.cs`.

- [ ] **Step 2: Compile gate**

Run: UnityMCP `refresh_unity` → poll `editor_state.isCompiling` to false → `read_console` (Error filter).
- **If zero errors:** partial classes work. Keep `partial` on the McWorld declaration and keep `McWorld.Scheduler.cs`. The merge will use this file.
- **If errors** (U# rejects partial): delete `McWorld.Scheduler.cs`, remove `partial` from the declaration. The merge will instead use a `#region Scheduler (merged from McCoordinator)` inside `McWorld.cs`. Note this decision in the commit message.

- [ ] **Step 3: Commit the decision**

```bash
git add Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs Assets/VRCMinecraft/Code/VoxelEngine/McWorld.Scheduler.cs
git commit -m "chore(voxel): confirm U# partial-class support for scheduler merge"
```

(If partial is unsupported, `git add -u` the removed file and McWorld.cs and commit "chore(voxel): partial classes unsupported — scheduler merge will use regions".)

> For the remaining tasks, "the scheduler file" means `McWorld.Scheduler.cs` if partials work, else the `#region` inside `McWorld.cs`.

---

## Task 2: Move the worker-pool state into McWorld

**Files:**
- Modify: scheduler file (add fields)
- Modify: `Assets/VRCMinecraft/Code/VoxelEngine/McCoordinator.cs` (these fields will be removed from here in Task 9; for now leave them so McCoordinator still compiles/runs)

- [ ] **Step 1: Declare the worker-pool + scheduler fields on McWorld**

Copy the field declarations from McCoordinator into the scheduler file as `private`/serialized fields on McWorld. From the audit these include: the serialized tuning fields (`maxConcurrentWorkers`, `maxConcurrentWorldgenColumns`, `dataGenLookaheadWindow`, `deferredMeshWakeQueueThreshold`, `deferredMeshWakeBurstPerCycle`, `maxChunkInstantiationsPerCycle`, `updateTimeBudgetMs`, `loadPhaseUpdateBudgetMs`, etc.) and the runtime arrays (`worker_state[]`, `worker_targetChunkIndex[]`, `worker_usesExclusiveGenerator[]`, `genSlotBusy[]`, `_positionAssigned[]`, `_genSlotCache[]`, the picker cursors `nextChunkIndexToAssign`/`_lastPickedDataGenPos`/`borderHealWorkerCursor`, and the `STATE_*` consts).

Prefix any name that already exists on McWorld to avoid collisions (e.g. `updateTimeBudgetMs` already exists on McWorld per the audit — reuse McWorld's, do NOT redeclare; map McCoordinator's usage to McWorld's field). List collisions explicitly in the commit body.

- [ ] **Step 2: Compile gate**

Run: `refresh_unity` → `read_console` (Error filter).
Expected: zero errors. (McWorld now *has* the fields; McCoordinator still has its own copies — both compile. No behavior change yet.)

- [ ] **Step 3: Commit**

```bash
git add Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs Assets/VRCMinecraft/Code/VoxelEngine/McWorld.Scheduler.cs
git commit -m "refactor(voxel): add worker-pool state fields to McWorld (merge step 1/7)"
```

---

## Task 3: Move the data-gen picker into McWorld

**Files:**
- Modify: scheduler file (add picker methods)
- Reference source: `McCoordinator.cs` `_TryPickDataGenPosition` (~lines 244–310) and `_GenSlotForChunk` (~line 261)

- [ ] **Step 1: Copy the picker methods into McWorld**

Move `_TryPickDataGenPosition()` and `_GenSlotForChunk(int)` (and any private helpers they call) into the scheduler file as private methods on McWorld. Inside them, replace every cross-VM call with a direct call:
- `world.ShouldGenerateChunkData(ci)` → `ShouldGenerateChunkData(ci)`
- `world.GeneratorSlotForChunkIndex(ci)` → `GeneratorSlotForChunkIndex(ci)`
- `world.CanStartChunkDataGenerationWithoutExclusiveGenerator(ci)` → `CanStartChunkDataGenerationWithoutExclusiveGenerator(ci)`
- `world.chunks_1D` → `chunks_1D` (already a field on McWorld)

These McWorld methods already exist and are public (the cross-VM surface) — calling them as `this.` is free in-VM.

- [ ] **Step 2: Compile gate**

Run: `refresh_unity` → `read_console` (Error filter).
Expected: zero errors. (McWorld now has its own picker; McCoordinator's copy is still the one actually driving — no behavior change yet.)

- [ ] **Step 3: Commit**

```bash
git add Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs Assets/VRCMinecraft/Code/VoxelEngine/McWorld.Scheduler.cs
git commit -m "refactor(voxel): move data-gen picker into McWorld (merge step 2/7)"
```

---

## Task 4: Move the worker state machine (UpdateWorkers) into McWorld

**Files:**
- Modify: scheduler file
- Reference source: `McCoordinator.cs` `UpdateWorkers`/worker loop (~lines 393–676) incl. the generator-slot watchdog (~692–705)

- [ ] **Step 1: Copy the worker loop into McWorld as `_SchedulerUpdateWorkers()`**

Move the worker state-machine loop into the scheduler file as `private void _SchedulerUpdateWorkers()`. Replace every cross-VM `world.<Method>(...)` with `<Method>(...)`. The full call set to rewrite (from the audit): `StepChunkDataGeneration`, `StepChunkLighting`, `AreAllNeighborsReady`, `AreAllNeighborsReadyLenient`, `ShouldDeferChunkMesh`, `MarkChunkMeshDeferred`, `HasAvailableGpuMeshReadbackSlot`, `BuildChunkMesh`, `ForceCompleteStuckMesh`, `UsesGpuLightingBackend`, `RequiresCpuLightingForAmbientOcclusion`, `HandleChunkPostDataGpuLighting`, `StartChunkLighting`, `GetChunkAt`.

Respect the Udon post-increment quirk if the worker loop uses `field++` as an expression — split into read-then-assign.

- [ ] **Step 2: Compile gate**

Run: `refresh_unity` → `read_console` (Error filter).
Expected: zero errors.

- [ ] **Step 3: Commit**

```bash
git add Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs Assets/VRCMinecraft/Code/VoxelEngine/McWorld.Scheduler.cs
git commit -m "refactor(voxel): move worker state machine into McWorld (merge step 3/7)"
```

---

## Task 5: Move AssignWork into McWorld

**Files:**
- Modify: scheduler file
- Reference source: `McCoordinator.cs` `AssignWork` (~lines 713–923) incl. border-heal (~766–806) and sibling fast-path (~815–900)

- [ ] **Step 1: Copy AssignWork into McWorld as `_SchedulerAssignWork()`**

Move the assignment logic into the scheduler file as `private void _SchedulerAssignWork()`. Rewrite cross-VM calls to direct calls, including: `InstantiateAndConfigureChunk`, `Chunk1DToArrrayCoords`, `StartChunkDataGeneration`, `StepChunkDataGeneration` (the sibling step), `MarkChunkMeshDeferred`, `GetChunkAt`, plus the picker (`_TryPickDataGenPosition`, now on McWorld). Keep `genSlotBusy[...] = true` writes (the state now lives on McWorld from Task 2).

- [ ] **Step 2: Compile gate**

Run: `refresh_unity` → `read_console` (Error filter).
Expected: zero errors.

- [ ] **Step 3: Commit**

```bash
git add Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs Assets/VRCMinecraft/Code/VoxelEngine/McWorld.Scheduler.cs
git commit -m "refactor(voxel): move AssignWork into McWorld (merge step 4/7)"
```

---

## Task 6: Move init + the load-phase/budget logic; add `RunSchedulerOnce()`

**Files:**
- Modify: scheduler file
- Reference source: `McCoordinator.cs` `Update()` (~332–979) and `InitializeAndStartProcessing` and load-phase plateau detection

- [ ] **Step 1: Move coordinator init into McWorld init**

Move `InitializeAndStartProcessing()`'s body (worker array allocation, `genSlotBusy`/`_genSlotCache` init, cursor reset) into a `private void _SchedulerInit()` and call it from McWorld's existing init path (wherever McWorld first sets up `chunks_1D` / the GPU backend). Find McWorld's init by searching for where `chunks_1D = new ChunkData[...]` is assigned.

- [ ] **Step 2: Add the single per-frame entrypoint**

Add to the scheduler file:

```csharp
// Runs the merged coordinator cycle entirely in-VM (was McCoordinator.Update()).
private void _RunSchedulerOnce()
{
    // 1. load-phase vs steady budget selection (moved from McCoordinator.Update head)
    // 2. _SchedulerUpdateWorkers();
    // 3. generator-slot watchdog;
    // 4. _SchedulerAssignWork();
    // 5. #if LOGGING aggregate cycle logging
}
```

Fill the body by moving the corresponding blocks from `McCoordinator.Update()` (budget selection at the head, the workers call, the watchdog, the assign call, the aggregate logging tail). Keep the existing load-phase plateau detection logic intact.

- [ ] **Step 3: Compile gate**

Run: `refresh_unity` → `read_console` (Error filter).
Expected: zero errors. (Nothing calls `_RunSchedulerOnce()` yet — McCoordinator still drives. No behavior change.)

- [ ] **Step 4: Commit**

```bash
git add Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs Assets/VRCMinecraft/Code/VoxelEngine/McWorld.Scheduler.cs
git commit -m "refactor(voxel): add in-VM _RunSchedulerOnce + scheduler init (merge step 5/7)"
```

---

## Task 7: Switch the driver from McCoordinator to McWorld

**Files:**
- Modify: `Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs` (`Update()`)
- Modify: `Assets/VRCMinecraft/Code/VoxelEngine/McCoordinator.cs` (`Update()` becomes a no-op)

- [ ] **Step 1: Call the scheduler from McWorld.Update()**

In McWorld's `Update()`, call `_RunSchedulerOnce()` **before** the existing `ProcessActiveChunks()` block (preserving the intent that work is assigned, then processed). Locate McWorld.Update by searching for the existing `ProcessActiveChunks` call.

```csharp
void Update()
{
    _RunSchedulerOnce();   // merged from McCoordinator
    // ... existing McWorld.Update body (ProcessActiveChunks, block ticker, etc.) ...
}
```

- [ ] **Step 2: Neutralize McCoordinator.Update()**

Replace the body of `McCoordinator.Update()` with an early `return;` (leave the class otherwise intact so the scene reference still resolves until Task 9). This makes McWorld the sole driver.

- [ ] **Step 3: Compile gate**

Run: `refresh_unity` → `read_console` (Error filter).
Expected: zero errors.

- [ ] **Step 4: Behavior gate (the critical one)**

Enter ClientSim, stream ~30 s standing still, capture the Performance Summary. Compare to baseline (Task 0):
- Chunk-load progress curve should match the baseline within noise.
- No new console errors.
- `Coordinator:` cycle metrics should now show the work happening inside McWorld's Update instead (the coordinator's own log may go quiet — that's expected).
- The `cross-VM calls/cycle` counter (Task 0 Step 3) should now read **≈0** if you kept the counter on the now-dead McCoordinator path; if you moved counting into the wrapped surface, it should reflect in-VM calls.

If progress regresses or errors appear, revert this task and diagnose before continuing.

- [ ] **Step 5: Commit**

```bash
git add Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs Assets/VRCMinecraft/Code/VoxelEngine/McCoordinator.cs
git commit -m "refactor(voxel): drive scheduler from McWorld.Update; McCoordinator.Update no-op (merge step 6/7)"
```

---

## Task 8: Make the formerly-cross-VM methods private; remove dead wrappers

**Files:**
- Modify: `Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs`

- [ ] **Step 1: Audit external callers before tightening visibility**

Grep the codebase for each method that was only called by McCoordinator and confirm no other behaviour calls it cross-VM:

Run (Grep tool): search `Assets/VRCMinecraft/Code` for `\.ShouldGenerateChunkData\(`, `\.AreAllNeighborsReady`, `\.ShouldDeferChunkMesh`, `\.HasAvailableGpuMeshReadbackSlot`, `\.GeneratorSlotForChunkIndex`, `\.StartChunkDataGeneration`, `\.StepChunkDataGeneration`, `\.StartChunkLighting`, `\.StepChunkLighting`, `\.ForceCompleteStuckMesh`, `\.CanStartChunkDataGenerationWithoutExclusiveGenerator`.
Expected: matches only inside McWorld (now `this.`) and the soon-deleted McCoordinator.

- [ ] **Step 2: Keep public ONLY what genuinely needs cross-VM access**

Methods still called by other behaviours (e.g. `RequestChunkMeshUpdate`, `BuildChunkMesh`, `GetChunkAt` if ModifyTerrain/McBlockTicker use them) stay public. Everything used only by the merged scheduler becomes `private`. Do not change `chunks_1D` visibility (other code may use it).

- [ ] **Step 3: Compile gate**

Run: `refresh_unity` → `read_console` (Error filter).
Expected: zero errors. If an error shows an external caller you missed, restore that method to public and note it.

- [ ] **Step 4: Commit**

```bash
git add Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs
git commit -m "refactor(voxel): privatize internalized scheduler surface (merge step 7/7)"
```

---

## Task 9: Delete McCoordinator and fix scene wiring

**Files:**
- Delete: `Assets/VRCMinecraft/Code/VoxelEngine/McCoordinator.cs` (+ its `.meta`)
- Modify: `Assets/VRCMinecraft/scenes/Minecraft.unity` (remove the McCoordinator UdonBehaviour; migrate any inspector-set tuning values to McWorld)

- [ ] **Step 1: Migrate serialized tuning values**

Before deleting, record McCoordinator's inspector values for the serialized tuning fields (open the scene/prefab or read the `.unity` block for the McCoordinator UdonBehaviour). Ensure the matching fields on McWorld (added in Task 2) carry the same values. If they differ, set McWorld's values to match.

- [ ] **Step 2: Find every reference to McCoordinator**

Grep `Assets/VRCMinecraft/Code` for `McCoordinator` (type usages, serialized fields like `public McCoordinator coordinator;`, `SendCustomEvent` targets). For each:
- A field `McCoordinator coordinator` on another behaviour that called `coordinator.RequestChunkMeshUpdate(...)` / `coordinator.RequestDeferredChunkMeshUpdate(...)` → re-point to call the McWorld equivalent (these were moved/forwarded). If McWorld now owns these entrypoints, change the caller to use its existing `McWorld` reference.

- [ ] **Step 3: Remove the McCoordinator references in code, then delete the file**

Update callers, then delete `McCoordinator.cs` and its `.meta`.

```bash
git rm Assets/VRCMinecraft/Code/VoxelEngine/McCoordinator.cs Assets/VRCMinecraft/Code/VoxelEngine/McCoordinator.cs.meta
```

- [ ] **Step 4: Compile gate**

Run: `refresh_unity` → `read_console` (Error filter).
Expected: zero errors.

- [ ] **Step 5: Scene cleanup (may require the user)**

The McCoordinator UdonBehaviour component must be removed from the scene GameObject, and any UdonBehaviour reference fields that pointed to it cleared/re-pointed. UdonSharp reference assignment via MCP has been unreliable in this project — **flag this step for the user to do in the Unity Inspector** if MCP cannot reliably remove the component and fix references. Provide the user the exact GameObject name (the one holding the McCoordinator UdonBehaviour) found in Step 2.

- [ ] **Step 6: Behavior gate**

Enter ClientSim, stream ~30 s, capture Performance Summary. Confirm: world still loads (progress curve matches baseline), no missing-reference errors, no NREs.

- [ ] **Step 7: Commit**

```bash
git add -A Assets/VRCMinecraft/Code/VoxelEngine Assets/VRCMinecraft/scenes/Minecraft.unity
git commit -m "refactor(voxel): delete McCoordinator; McWorld is sole scheduler (Phase 1 complete)"
```

---

## Task 10: Phase 1 validation & write-up

**Files:**
- Modify: `docs/superpowers/baselines/2026-06-19-baseline.md` (append post-merge numbers)

- [ ] **Step 1: Capture post-merge metrics**

Enter ClientSim, stream ~30 s standing still, capture the Performance Summary.

- [ ] **Step 2: Compare against baseline and record**

Append a "Post-merge (Phase 1)" section to the baseline doc with the new numbers. Phase 1 go/no-go (from spec §10):
- Cross-VM calls/frame ≈ 0 — **PASS/FAIL**
- Coordinator/scheduler per-cycle cost reduced vs baseline — record delta.
- World still loads correctly, no errors — **PASS/FAIL**.

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/baselines/2026-06-19-baseline.md
git commit -m "docs(voxel): record Phase 1 (merge) results vs baseline"
```

---

## Out of scope for this plan (separate plans, written after Phase 1 lands)

- **Phase 2 — Event-driven scheduler + worldgen:** replace per-frame O(8192) scans (border-heal, deferred-interior-wake, GO-recycle, picker fallback) with a frontier cursor + dirty queues; make worldgen completion fire on the readback callback instead of per-step polling. Planned separately because the exact queue/event shape depends on the merged code from Phase 1.
- **Phase 3 — SoA data store:** convert the 8192 `ChunkData` objects to a pooled slot model with parallel arrays (de-box `_chunkData`, sparse derived data, lazy metadata, pooled transients). Planned separately; it is thousands of `chunk.field`→`field[slot]` edits done in compile-checked field-group passes.

---

## Self-Review notes

- **Spec coverage:** This plan covers spec §1 (baseline, Task 0), §5 (the merge, Tasks 1–9), and §10 Phase-1 metric (Task 10). Spec §6 (event-driven) and §7 (SoA) are explicitly deferred to their own plans (spec §8 phasing). No spec requirement for Phase 1 is unaddressed.
- **Verification adaptation:** classic TDD is replaced by compile-gate + ClientSim-instrumentation-gate, documented up top, because Udon has no runtime test harness.
- **No placeholders:** every task names exact files and concrete edits; large method bodies are referenced by name + line range (they are *moves*, not new code — pasting 200-line bodies verbatim would be noise and drift-prone).
