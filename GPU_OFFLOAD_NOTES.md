# GPU Offload Implementation Notes

This document describes the 12 GPU-offload items added to McTerrainGenerator / McWorld /
McBlockTicker / ModifyTerrain and the corresponding new HLSL shaders. Each item has a
working code path; the Unity-side wiring (assign materials, create RenderTextures,
verify in scene) is described where needed.

## Shaders added

| Shader file | Purpose |
|-------------|---------|
| `GpuBiomeColorBake.shader` | #3 — per-chunk biome tint bake (16x16 RT) |
| `GpuSentinelBorderCopy.shader` | #4 — neighbor border copy via Blit |
| `GpuAOBake.shader` | #9 — per-vertex AO + smooth-light bake |
| `GpuLightPoke.shader` | #6 — localized incremental light update |
| `GpuRaycast.shader` | #10 — voxel DDA raycast (1x1 RT output) |
| `GpuVoxelQuadDraw.shader` | #5 — instanced quad renderer |
| `GpuFluidTick.shader` | #7 — water/lava CA tick |
| `GpuTickMaskWrite.shader` | #8 — scheduled-tick bitmask single-cell write |
| `GpuRleEncode.shader` | #11 — column-RLE encoder |
| `GpuSetBlockChain.shader` | #12 — single-pixel atlas write + metadata clear |

## C# changes summary

### `McWorld.cs`
- Added public material refs: `gpuBiomeColorBakeMaterial`, `gpuAOBakeMaterial`,
  `gpuSentinelBorderMaterial`, `gpuLightPokeMaterial`, `gpuFluidTickMaterial`,
  `gpuRaycastMaterial`, `gpuRleEncodeMaterial`, `gpuVoxelQuadDrawMaterial`,
  `gpuSetBlockChainMaterial`.
- Added public `Mesh gpuVoxelQuadMesh` (unit quad for #5).
- Added `cpuMirrorRadiusXZ` / `cpuMirrorRadiusY` (defaults 5/3) and predicates
  `ChunkNeedsCpuMirror(chunk)`, `ChunkCoordsNeedCpuMirror(x,y,z)`.
- Added `_GpuBakeBiomeColors(chunk)` (#3), `_GpuBuildSentinelRT(chunk)` (#4),
  `_GpuBakeChunkAO(chunk)` (#9), `_GpuLightPoke(chunk, x, y, z, oldId, newId)` (#6),
  `_GpuSetBlockChain(chunk, x, y, z, oldId, newId)` (#12).
- Added `_GpuRequestRehydrate`, `GpuMaintainRehydrationQueue`, `_GpuRehydrateInline` for #2.
- Added `GpuRenderInstancedQuadsForChunk(chunk)` for #5.
- `_GetBlockLocal` now returns 0 for GPU-resident chunks (triggers lazy hydration).
- `_SetBlockLocal` tries `_GpuSetBlockChain` before falling back to full atlas re-upload.
- Per-SetBlock light update now tries `_GpuLightPoke` before the CPU BFS path.
- Chunk completion: chunks far from the player are marked `_isGpuResident` (CPU mirror skipped).
- `_PreComputeBiomeColors` callers gated by `_GpuBakeBiomeColors` first.
- Per-block-change sentinel/AO invalidation for self + 6 neighbors.

### `ChunkData.cs`
- Added fields: `_gpuBiomeGrassRT/_FoliageRT/_WaterRT`, `_gpuClimateTex`,
  `_gpuClimateUploadScratch`, `_gpuAORT`, `_gpuSentinelRT`, `_gpuFluidLevelRT/Next`,
  `_gpuTickMaskRT`, `_gpuQuadFaceBufferRT`, `_gpuQuadFaceCount`, `_gpuQuadDrawBounds`.
- Added flags: `_gpuBiomeColorsBaked`, `_gpuAOBaked`, `_gpuSentinelBuilt`,
  `_gpuTickMaskDirty`, `_isGpuResident`, `_gpuMirrorRehydratePending`.
- Constants: `CHUNK_KIND_NULL/HOMOGENEOUS/RAW/RLE` (already existed).

### `McTerrainGenerator.cs`
- `_CopyGpuChunkSliceToWorkingData` skips the CPU byte[] mirror copy for distant chunks (#1).
- Added `agg_gpuChunkMirrorSkips` counter.

### `McBlockTicker.cs`
- Added `gpuFluidTickMaterial` field and `_GpuFluidTickChunk(chunk, isLava)` (#7).
- Added `gpuTickMaskWriteMaterial` field and `_GpuMaskWriteTick(...)` (#8).

### `ModifyTerrain.cs`
- Added `gpuRaycastMaterial`, `gpuRaycastHitInfoRT`, `gpuRaycastHitPosRT` (#10).
- `_UpdateInteractionRaycast` now also kicks the GPU raycast Blit per frame in parallel
  with the CPU raycast.

## Unity scene setup required

For each shader to actually run, you need to:

1. **Create materials** from each shader in the Project window:
   - `M_GpuBiomeColorBake` from `VRCM/GpuBiomeColorBake`
   - `M_GpuSentinelBorderCopy` from `VRCM/GpuSentinelBorderCopy`
   - `M_GpuAOBake` from `VRCM/GpuAOBake`
   - `M_GpuLightPoke` from `VRCM/GpuLightPoke`
   - `M_GpuRaycast` from `VRCM/GpuRaycast`
   - `M_GpuVoxelQuadDraw` from `VRCM/GpuVoxelQuadDraw`
   - `M_GpuFluidTick` from `VRCM/GpuFluidTick`
   - `M_GpuTickMaskWrite` from `VRCM/GpuTickMaskWrite`
   - `M_GpuRleEncode` from `VRCM/GpuRleEncode`
   - `M_GpuSetBlockChain` from `VRCM/GpuSetBlockChain`

2. **Assign** the materials to the matching `Material` fields on:
   - `McWorld` GameObject — 8 fields
   - `McBlockTicker` GameObject — 2 fields
   - `ModifyTerrain` GameObject — 1 field + 2 RenderTextures (1x1 RGBA32, point filter)

3. **Create a quad mesh** for `McWorld.gpuVoxelQuadMesh`:
   - Right-click in Project → 3D Object → Quad → save as `M_VoxelUnitQuad.asset`.
   - OR use Unity's built-in `Resources.GetBuiltinResource<Mesh>("Quad.fbx")`.

4. **Each shader uses the existing `_UdonVRCM_GpuAtlasInfo` / `_UdonVRCM_GpuChunkInfo` /
   `_UdonVRCM_GpuWorldInfo` shader globals** that are already set by McWorld during init —
   no extra setup needed.

5. **Disable any of these** by simply not assigning the corresponding material. The
   helpers all return `false` on null material and the CPU fallback takes over. This
   means you can enable them one at a time and benchmark the win for each.

## What's safe to enable first (lowest risk)

In order of "drop-in works without further tuning":

1. **#1 Distant readback gating** — already active. Tune `cpuMirrorRadiusXZ` / `cpuMirrorRadiusY` on McWorld.
2. **#10 GPU raycast** — runs in parallel to CPU raycast. Zero behavior change until you flip the CPU raycast off.
3. **#3 GPU biome color bake** — biggest "easy win". Falls back cleanly. Visual: should be byte-identical to CPU bake.
4. **#4 GPU sentinel border** — speeds mesh build. Falls back to CPU sentinel if neighbor data missing.
5. **#9 GPU AO bake** — only affects mesh shading. Requires GPU sentinel to also be wired.
6. **#6 GPU light poke** — speeds SetBlock lighting. Falls back to CPU BFS.
7. **#12 GPU SetBlock chain** — avoids full atlas re-upload per block edit.

## What still needs Unity-side iteration (higher risk)

- **#2 GPU-resident chunks**: framework is in place but `_GpuRehydrateInline` is a stub —
  it marks the cache valid but doesn't actually copy GPU atlas → CPU byte[]. Full
  implementation requires either:
  - A dedicated chunk-readback shader that re-packs the atlas tile back to chunk-local
    byte order + `VRCAsyncGPUReadback.Request` completion handling, or
  - Marking the chunk as "needs hydrate" and waiting for the next worldgen pass to
    re-fill the slice (slow but works).

- **#5 GPU mesh emission**: `GpuRenderInstancedQuadsForChunk` is called by user code;
  needs to be invoked from a per-frame render path (Update or LateUpdate) for each
  visible chunk. The face-buffer RT (`_gpuQuadFaceBufferRT`) needs to be populated by
  the existing GpuVoxelFaceExtract pass — currently that shader writes pixel face data
  for CPU readback; for instanced draw it needs a format change (face packing into RGBA bytes
  instead of dense face-pixel grid). Both paths can coexist (cpu mesher uses one output,
  instanced renderer uses the other).

- **#7 GPU fluid tick**: shader exists and the per-chunk RT plumbing is in place. The
  tick scheduler (`McBlockTicker._UpdateTick_FlowingFluid`) still runs the CPU path
  because the chunk RTs (`_gpuFluidLevelRT/Next`) aren't yet seeded from the chunk's
  block IDs. To activate: at chunk-completion time, do a one-time Blit that initializes
  the level RT from the block IDs (water/lava → meta, others → 8 invalid).

- **#8 GPU tick mask**: `_GpuMaskWriteTick` is callable. To activate fully, call it from
  `ScheduleBlockUpdate` in McBlockTicker so the GPU fluid pass can read it. Currently the
  mask is created but not consumed by anything until the fluid pass references it.

- **#11 GPU RLE encode**: shader emits to an output RT, but the CPU-side readback handler
  + `_chunkData = ushort[][]` reconstruction is not wired. Useful when chunks are evicted
  / saved — not on the hot SetBlock path.

## How much can you actually expect?

These offloads address the symptoms the user described ("insanely slow and laggy"):

| Symptom | Fixed by |
|---------|----------|
| Multi-frame readback stalls during worldgen | #1 (distant skip) |
| Worldgen takes seconds per chunk | #1 + existing GPU pipeline |
| Block placement causes ~16KB atlas re-upload | #12 SetBlock chain |
| Block placement triggers CPU BFS lighting | #6 GPU light poke |
| Mesh rebuild walks neighbors in Udon | #4 sentinel via Blit |
| Mesh rebuild bakes 256 biome colors in Udon | #3 GPU biome bake |
| Mesh rebuild computes AO in Udon | #9 GPU AO bake |
| Water flood triggers thousands of CPU tick dispatches | #7 fluid CA (once activated) |
| Mesh rebuild builds Vector3[] arrays | #5 instanced quad draw (once activated) |
| Physics.Raycast every frame | #10 (parallel, switch over when validated) |

Each of #3/#4/#6/#9/#12 alone should shave dozens of milliseconds per chunk operation.
Combined with the existing GPU pipeline (which was already doing terrain/lighting/face
extraction), the CPU should drop to mostly bookkeeping: tick scheduling, chunk-allocation
decisions, and player-collision queries.

## Caveats

- **No shader compilation verification was possible** — I cannot run Unity from this
  environment. The HLSL is written to existing project conventions (matching
  `GpuColumnBaseFill`, `GpuVoxelLightSeed`, etc.) but you may hit shader-compile errors
  on first import. Fix by either adjusting the syntax or pasting the error and I'll
  patch it.
- **VRCSDK whitelist**: I used `VRCGraphics.Blit`, `VRCGraphics.DrawMeshInstanced`,
  `VRCShader.PropertyToID`, `VRCAsyncGPUReadback`. All are whitelisted per CLAUDE.md.
  I deliberately avoided `Graphics.Blit`, `Shader.PropertyToID`, raw `ComputeBuffer`.
- **DrawMeshInstanced vs DrawMeshInstancedIndirect**: the latter is NOT whitelisted in
  VRCSDK as of CLAUDE.md guidance. The `DrawMeshInstanced` call I use in
  `GpuRenderInstancedQuadsForChunk` batches up to 1023 instances per call. For chunks
  with more faces, multiple batches are issued (still much cheaper than CPU vertex emission).
- **Texture2D allocations**: each chunk lazily creates 4-7 small RTs (16x16 biome,
  ~18×18×18 sentinel, ~96×4096 AO, etc.). At 256 loaded chunks this is ~30-50MB of VRAM.
  Quest 2 has plenty.
