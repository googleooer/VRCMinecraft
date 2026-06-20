# Voxel Engine Rethink — VM Merge + Event-Driven Scheduler + SoA Data Store

- **Date:** 2026-06-19
- **Status:** Design approved; ready for implementation planning
- **Scope tag:** Sub-projects ① (orchestration + worldgen stepping) + ② (data/allocation/compression) combined into one effort. Sub-project ③ (GPU-resident render) is explicitly **out of scope** here and tracked separately.
- **Branch:** `gpu-optimizations`

---

## 1. Problem & Diagnosis

The engine runs at ~17 fps "at all times," and the user confirmed it is **logic-bound, not render-bound** ("perfect framerate when logic isn't running"). Profiling (ClientSim, Unity profiler + the engine's own Performance Summary) proves the cost is per-frame Udon bytecode execution, not GPU/draw.

### Baseline numbers (the targets to beat)

Unity profiler (one captured frame):
- `UdonBehaviour.ManagedUpdate` **self = 58.6 ms (≈17 fps), 91.8% of frame**, 4 behaviour invocations.
- **10,002 `GC.Alloc` calls/frame**, ~283 KB/frame.

Engine Performance Summary (load phase, world is 8192 chunks, ~0.8–3.5% loaded):
- **Coordinator: avg cycle ~44 ms** = `update workers ~8–19 ms` + `assign work ~36 ms`.
  - Inside assign: **data-gen sibling stepping ≈ 32 ms/cycle**, picker `~3.3 ms`, startGen `~0.13 ms`.
- **McWorld Update: avg 10.6–24 ms/frame.**
- **Worldgen stepping: `actual 0.005 ms` real work but `avg step 2.157 ms` × ~32 steps/chunk** → ~99.7% overhead.
- **GPU readback latency 164–214 ms** (worldgen base readback); face readback latency similar.
- Load throughput **~10 chunks/s**; "first deferred mesh" wait **14–44 s**.
- Frame spikes to **90–244 ms** (GC).

### Root causes (proven against code)

1. **Cross-VM extern tax.** McCoordinator → McWorld is one-way and the picker scans 96+ positions/frame, each doing cross-VM `ShouldGenerateChunkData` / `GeneratorSlotForChunkIndex`; the worker state machine does per-worker cross-VM `AreAllNeighborsReady` / `ShouldDeferChunkMesh` / `BuildChunkMesh`. ~100–200 cross-VM calls/frame.
2. **Worldgen spin-polling.** `StepChunkDataGeneration` re-enters the generator across the VM every step while the generator sits in `Prepare_GpuReadback` polling a 200 ms readback. ~32 steps/chunk of mostly VM-transition overhead. (`McWorld.cs` ~5289–5529; `McTerrainGenerator.cs` GenerationState ~22–41.)
3. **Per-frame O(world=8192) scans.** Border-heal (64/cycle, up to 6 cross-VM `GetChunkAt` each), deferred-interior-wake, GO-recycle, picker fallback — all poll every frame even when idle.
4. **Data-structure bloat & boxing.** ~111 KB per materialized chunk; cross/torch position arrays sized to 4096 ints (~98% empty); `blockMetadata`/`torchMountData`/`_cachedBrightness` always allocated; `object _chunkData` boxes a `byte` for homogeneous chunks. 8192 fat `ChunkData` objects.
5. **(Out of scope — ③) Mesh-emit boxing.** The ~10k allocs/frame are `new Vector3()`/`new Color()` per vertex in the CPU greedy emit (Udon boxes value types to the heap, ~24 B each). This is intrinsic to CPU meshing and cannot be pooled away; it is the source of the GC spikes and belongs to ③ (GPU-resident render).

---

## 2. Goals & Non-Goals

### Goals
- Cut steady-state per-frame Udon cost from ~44 ms (coordinator) + ~10–24 ms (world) to a **single-digit-ms scheduler**.
- Push load throughput from ~10 chunks/s toward the GPU-readback ceiling.
- Reduce per-chunk memory ~5× and drive **streaming object-allocations/frame to ≈0**.
- Keep behavior/parity identical (Beta 1.7.3 look, lighting, water, cross blocks, interaction).

### Non-Goals (this effort)
- **Mesh-emit GC spikes** (the ~10k Vector3/Color allocs/frame). These remain until ③.
- **Render distance / GPU instanced rendering.** Tracked as ③.
- Block-ticker parity changes (it already measures ~0.067 ms/frame; leave it).
- Any change to the GPU worldgen/lighting/face-extract shaders.

---

## 3. Chosen Approach

**Full merge + event-driven scheduler + parallel-array SoA data store**, delivered in safe phases. (Selected over "in-VM scheduler keeping two VMs" and "minimal hotfixes." The user explicitly chose the most aggressive structural option and committed a safety checkpoint.)

Calibration decisions:
- **Delete McCoordinator entirely** (no thin shell) unless an external reference forces a shim.
- **Full parallel-array SoA** over the active working set (not the lighter "trim-the-ChunkData-object" alternative).

`ChunkData` is a plain C# class (8192 instances). Udon supports plain user classes but **not** user structs, so "struct-of-arrays" = **parallel primitive/reference arrays indexed by a pooled slot**.

---

## 4. Architecture

One `UdonBehaviour` with four internal subsystems (organized as `partial class McWorld` files if U# supports partial classes — to be verified in Phase 1; fallback = `#region`s in McWorld.cs):

```
McWorld (single UdonBehaviour, drives itself from Update())
 ├─ Scheduler      ← (was McCoordinator) worker state machine + picker, in-VM
 ├─ ChunkStore     ← pooled SoA buffers for the active working set (replaces ChunkData[])
 ├─ WorldgenBridge ← event-driven completion (no per-step polling)
 └─ GpuBackend / Mesher  ← unchanged this round (mesh emit = ③)
```

---

## 5. The Merge (Phase 1 detail)

- Move the **worker state machine**, the **data-gen picker**, and **AssignWork** from McCoordinator into McWorld as private methods. The picker's per-position scans and the per-worker readiness checks become in-VM array reads — **~100–200 cross-VM externs/frame → ~0**.
- Move worker-pool state in as private fields: `worker_state[]`, `worker_targetChunkIndex[]`, `worker_usesExclusiveGenerator[]`, `genSlotBusy[]`, `_positionAssigned[]`, `_genSlotCache[]`, plus picker cursors.
- **Delete McCoordinator**; fold its `Update()` body into McWorld's `Update()`. Grep for any external references first; add a thin shim only if one exists.
- **Strictly behavior-preserving** — locality change only, no logic change.

Current cross-VM surface to internalize (from audit, `McCoordinator.cs`): `GeneratorSlotForChunkIndex`, `CanStartChunkDataGenerationWithoutExclusiveGenerator`, `ShouldGenerateChunkData`, `StartChunkDataGeneration`, `StepChunkDataGeneration`, `UsesGpuLightingBackend`, `RequiresCpuLightingForAmbientOcclusion`, `HandleChunkPostDataGpuLighting`, `StartChunkLighting`, `StepChunkLighting`, `AreAllNeighborsReady(+Lenient)`, `ShouldDeferChunkMesh`, `MarkChunkMeshDeferred`, `HasAvailableGpuMeshReadbackSlot`, `BuildChunkMesh`, `ForceCompleteStuckMesh`, `InstantiateAndConfigureChunk`, `Chunk1DToArrrayCoords`, `GetChunkAt`.

---

## 6. Event-Driven Scheduler & Worldgen (Phase 2 detail)

### Scheduler: push, not poll
- **Frontier cursor** over `radialChunkOrder` for streaming — monotonic, bounded, **no whole-world fallback scan**. Sleeps when it reaches render distance.
- **Dirty queues** for work, populated by events: "neighbor became ready," "needs mesh rebuild," "player crossed a chunk boundary."
- **Neighbor readiness event-driven:** when a chunk's data finalizes, it decrements the `_borderMissingMask` of its 6 neighbors; a neighbor reaching mask==0 enqueues itself for mesh. Border-heal and deferred-interior-wake scans are **deleted**.
- **Steady state → queues empty → scheduler does ~nothing.**

### Worldgen completion event-driven
- Kick a column once; the generator's existing async-readback callback sets a "column ready" flag/queue.
- The scheduler drains ready columns and **finalizes all 8 siblings in one in-VM pass** — no per-step VM re-entry, no `IsGeneratingChunk` polling.
- Readback latency (~200 ms) is unchanged, but no CPU/VM cycles are spent babysitting it; the worker is freed meanwhile. (rvc "read back only the completion signal" pattern.)
- Lean on the existing GPU-resident sibling fast-path (`_GpuResidentCompleteChunk`, `McWorld.cs` ~5232–5310) so siblings skip CPU homogeneous-scan / 16 KB clone / derived refresh / atlas upload.

---

## 7. SoA Data Store (Phase 3 detail)

Replace **8192 fat `ChunkData` objects** with a **bounded pool of N chunk-slots** (N = max active chunks, e.g. ~2048). One `int[8192] chunkIndexToSlot` maps world-chunk → slot (−1 = not loaded). Slots recycle as chunks stream — same pattern as the existing GameObject pool, extended to all per-chunk state.

Four concrete changes:

1. **De-box block storage.** `object _chunkData` → typed parallel arrays: `byte[] slotKind`, `byte[] slotHomogeneousBlock` (single id, no boxing), pooled `byte[][] slotRawData` (non-homogeneous), pooled RLE store for evicted/compressed chunks. Eliminates boxing/unboxing on every homogeneous chunk.
2. **Sparse derived data.** `_crossBlockPackedPositions` / `_torchBlockPackedPositions` sized to actual count (from a shared pool), not 4096 ints.
3. **Lazy allocation.** `blockMetadata`, `torchMountData`, `_cachedBrightness` not allocated until a block in that chunk needs them. Most terrain chunks never do.
4. **Pooled transients.** The 6-neighbor decompress buffers (24 KB/mesh-build) and the decompressed cache come from a fixed reuse pool.

**Expected:** per-chunk footprint ~111 KB → ~10–20 KB typical; per-chunk *object* allocations (streaming-GC source) → near-zero.

**Known caveat:** in Udon a flat-array read (`slotKind[ci]`) is **not** measurably faster than an object-field read (`chunk._chunkDataKind`) — both are heap ops. The SoA win is **memory + de-boxing + streaming-GC + lazy allocation**, not raw access speed. The cost is large: every `chunk.field` access across ~13k lines becomes `field[slot]`. This is accepted scope.

Reference-type per-chunk fields (`GameObject`, `MeshFilter`×3, `MeshCollider`×2, `_cachedNeighbors`) become parallel reference arrays indexed by slot, aligned with the existing GO pool.

---

## 8. Migration Sequencing (no big-bang — always shippable)

- **Phase 0 — Baseline lock.** Confirm/extend per-subsystem instrumentation (frame-ms split, chunks/s, allocs/frame, active memory). Capture baseline so each phase's win is provable. Logs stay (standing user preference: never remove logging as a perf measure).
- **Phase 1 — Merge (behavior-preserving).** Fold McCoordinator into McWorld; delete the cross-VM surface; verify partial-class support. Verify identical behavior; measure extern savings.
- **Phase 2 — Event-driven scheduler + worldgen.** Replace O(world) scans + worldgen polling with frontier + queues + callbacks. Measure load and steady-state.
- **Phase 3 — SoA data store.** Field groups converted in compile-checked passes (block storage → derived → metadata → transients), tested between groups. Behind a build flag if practical for A/B.

Each phase is its own commit(s), independently revertable.

---

## 9. Risks & Mitigations

- **Thousands of `chunk.field` → `field[slot]` edits.** Grouped, compile-checked passes; kind/discriminator logic stays byte-identical; temporary accessor shim (`SlotOf(chunk)`) if needed.
- **Scheduler regressions.** Phase 1 strictly behavior-preserving; diff behavior via instrumentation before changing logic in Phase 2.
- **U# partial-class uncertainty.** Verify at the start of Phase 1; fallback = `#region`s in one file.
- **Hidden McCoordinator references.** Grep before delete (ModifyTerrain, McBlockTicker, scene wiring); thin shim only if an external ref exists.
- **Udon VM quirks.** Respect the post-increment-on-field quirk and all CLAUDE.md U# limitations (no multidim arrays → jagged; no try/catch; no `AddComponent`; etc.). Check `udon_blacklisted.txt` / `udon_whitelisted*.txt` before relying on any API.

---

## 10. Success Metrics (go/no-go per phase)

- **Phase 1:** cross-VM calls/frame ≈ 0; coordinator's ~44 ms/cycle folded and reduced.
- **Phase 2:** steady-state scheduler < ~2 ms/frame; load chunks/s up materially; worldgen step overhead gone (no 2.16 ms/step polling).
- **Phase 3:** per-chunk memory ↓ ~5×; streaming object-allocs/frame ≈ 0.
- **Overall:** steady-state Udon ≤ ~6–7 ms/frame (90 fps headroom). Remaining GC spikes must be attributable to mesh emit (= ③), nothing else.

---

## 11. Open Questions / Verify During Implementation

- Does UdonSharp (this project's version) support `partial class`? (Phase 1, gates code organization only.)
- Exact value of N (max active chunk slots) — derive from render distance × full-Y columns + streaming margin; make it an inspector knob.
- Whether a build flag for Phase 3 A/B is practical given the field-conversion scope (may be all-or-nothing per field group).
- Confirm no external behaviour depends on McCoordinator being a separate UdonBehaviour (scene references, SendCustomEvent targets).

---

## Appendix — Current per-chunk data (from audit, for the planning step)

`ChunkData` (plain class, `ChunkData.cs`): `object _chunkData` + `byte _chunkDataKind` (NULL/HOMOGENEOUS/RAW/RLE); `_cachedDecompressedData byte[4096]`; derived `_columnMinY/_columnMaxY byte[256]`, `_crossBlockPackedPositions/_torchBlockPackedPositions int[4096]` (sparse in practice), flags/bounds; `blockMetadata byte[4096]`, `torchMountData byte[4096]`, `_cachedBrightness byte[4096]`; biome `double[256]`×2, `Color[256]`, `int[256]`; `_sentinel byte[5832]`; GPU face-build state; GO/MeshFilter/MeshCollider refs. Materialized footprint ~111 KB/chunk; ~111 MB at ~1000 chunks.
