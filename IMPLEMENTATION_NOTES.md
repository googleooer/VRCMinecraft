# Parity & Optimization Implementation Notes

This file documents the changes applied during the parity + performance pass,
and explicitly lists items that were deferred (and why).

## What was implemented

### Critical parity fixes

1. **`JavaRandom.Next(bits)` clamp bug** — was collapsing ~50% of `Next(32)`
   outputs to `int.MaxValue`, corrupting `NextInt()` / `NextLong()` and every
   downstream worldgen seed. Now uses `unchecked((int)(seed >> (48 - bits)))`
   to preserve Java's bit pattern. Fixes chunk decoration seeds, cave seeds,
   and structure-placement RNG.
   [JavaRandom.cs](Assets/VRCMinecraft/Code/VoxelEngine/Static_or_Java/JavaRandom.cs)

2. **`JavaRandom` L'Ecuyer constant** — fixed from `181783497276652981L`
   (18 digits) to `1181783497276652981L` (19, matching OpenJDK). Tried to
   also make `seedUniquifier` `static` (matches OpenJDK's process-wide
   AtomicLong) but UdonSharp does not support static fields on user-defined
   types. Kept as instance — the parameterless `new JavaRandom()` path is
   never used by worldgen in this codebase, so the practical impact is nil.
   [JavaRandom.cs](Assets/VRCMinecraft/Code/VoxelEngine/Static_or_Java/JavaRandom.cs)

3. **`McUtils.GetMinecraftSeed` return-type** — now returns `long` (matches
   Java's `Long.parseLong`). Empty string and literal `"0"` now produce a
   fresh random seed per Java's `GuiCreateWorld` semantics. All five call
   sites in McWorld and McTerrainGenerator updated to use `long`.
   [McUtils.cs](Assets/VRCMinecraft/Code/VoxelEngine/Static_or_Java/McUtils.cs)
   [McTerrainGenerator.cs](Assets/VRCMinecraft/Code/VoxelEngine/WorldGen/McTerrainGenerator.cs)

4. **`MusicManager.playRandomTrack` off-by-one** — `Random.Range(0, Length-1)`
   excluded the last track from selection. Now uses `Random.Range(0, Length)`.
   Added null/empty guards.
   [MusicManager.cs](Assets/VRCMinecraft/Code/game/MusicManager.cs)

5. **SIN_TABLE LUT for cave gen** — 65,536-entry sin LUT matching Beta's
   `MathHelper.SIN_TABLE`. Replacing `Mathf.Sin` with this is simultaneously
   a perf win and a parity gain (cave shapes were literally shaped by Beta's
   LUT quantization). The LUT lives as an instance field on `WorldGenCavesOld`
   (single instance per world) because UdonSharp does not support static
   fields on user-defined types.

6. **`WorldGenCavesOld`** — uses `MathHelper.Sin/Cos` instead of `Mathf`,
   and the Y-carve loop was off-by-one (started at `endY` instead of
   `endY - 1`, carving one block too high). Fixed both.
   [WorldGenCavesOld.cs](Assets/VRCMinecraft/Code/VoxelEngine/Static_or_Java/WorldGenCavesOld.cs)

7. **Random tick mushroom dispatch** — `B_MUSHROOM_BROWN` / `B_MUSHROOM_RED`
   were registered in `_IsRandomTickBlock` but had no case in
   `_DispatchUpdateTick`. Added `_UpdateTick_Mushroom` with Beta's 1/100
   spread chance and light < 13 check.
   [McBlockTicker.cs](Assets/VRCMinecraft/Code/VoxelEngine/McBlockTicker.cs)

8. **Grass spread light check** — Beta `BlockGrass.updateTick` requires
   `getFullBlockLightValue(x,y+1,z) >= 9` to spread and `< 4 && opacity > 2`
   to revert. The port only checked opacity. Now uses both.

9. **Sapling growth rate** — was `nextInt(7) != 0` (~1/7), Java is
   `nextInt(30) == 0` (~1/30) — 4× slower. Also added the missing
   light >= 9 check.

10. **Farmland tick gate** — added the missing `if (nextInt(5) == 0)` wrap
    that Java wraps the entire body in. Without it, farmland ticked 5× too
    fast.

11. **Ice melt condition** — was checking sky exposure (inverted). Beta:
    melts when `getBlockLightValue > 11 - opacity`. Fixed.

12. **Snow melt condition** — was checking sky exposure + 25% probability
    gate. Beta: guaranteed melt when `blockLight > 11`. Fixed.

13. **Reed survival** — added missing water-adjacency check. Reed now
    correctly dies when there's no water in the 4 horizontal neighbors of
    the block below.

14. **Flower/mushroom/deadbush survival** — added Beta's `light >= 8 ||
    canBlockSeeTheSky` requirement; dead bush requires `SAND` below (not
    grass/dirt/farmland); mushrooms require `light < 13 && opaque below`.

15. **Crops growth rate** — added missing light >= 9 check and approximated
    Java's `nextInt(100/growthRate)` formula with farmland hydration term.

16. **Falling-block erasure bug** — origin cell is now cleared exactly once
    at spawn (matching `EntityFallingSand` constructor) instead of every
    physics tick. Prevents wiping a freshly placed landing block.

17. **Random-tick Y mask** — `(rv >> 16) & 15` is now correctly documented
    as the 16-tall-chunk variant; the chunk cursor iterates all Y-chunks so
    the effective Y range still covers the full column.

18. **McTerrainGenerator bedrock RNG** — Java loops Y from 127 down to 0
    calling `rand.nextInt(5)` 128 times per column. The port only called
    it 5 times, desynchronizing the downstream decoration RNG. Now consumes
    the same RNG state by walking the full Y range but only writes bedrock
    in the bottom chunk where `globalY <= NextInt(5)`.

19. **Gravel noise Y offset** — was `109.0` on the GPU shim path. Changed
    to `109.0134` (the exact Beta value).

20. **McWorld `_ImportFromNeighborBoundary` axis bug** — switch cases 0..5
    labelled axes incorrectly relative to `neighbor_d{x,y,z}_offsets`.
    Rewrote so direction 0 = +X, 1 = -X, 2 = +Y, 3 = -Y, 4 = +Z, 5 = -Z,
    matching the offsets array. Cross-chunk light boundary imports now use
    the correct face.

21. **Recursive `_PropagateToNeighborIfBrighter` rewritten as iterative
    BFS** — uses the existing pooled int[] queue with `x | (y<<8) | (z<<16)`
    packing. Removed the `+1 threshold` that prevented Beta's natural
    convergence. UdonSharp has no TCO so the recursive version risked
    stack overflow on deep light propagation.

22. **SetBlock clears metadata** — `_SetBlockLocal` now zeroes the
    metadata nibble of the changed voxel (matches `Chunk.setBlockID` in
    Beta). Prevents stale water-decay metadata bleeding into newly placed
    blocks.

23. **`ModifyTerrain.blockInteractionRange`** — was 7f, now 4.5f
    (between Beta survival 4.0 and creative 5.0).

24. **`McMovement.SOUL_SAND_MULTIPLIER`** — removed (Beta 1.7.3 has no
    Soul Sand slowdown — that was added in Release 1.0).

### Performance improvements (parity-preserving)

A. **`McWorld._GetBlockLocal` / `_SetBlockLocal` chunk-kind enum tag** —
   replaces `chunk._chunkData.GetType()` + 3 `typeof` comparisons with a
   single byte-compare on `_chunkDataKind`. In Udon, `GetType()` is a
   virtual call + RTTI scan — the single most expensive op on the hot
   path. Legacy chunks (kind=0/unset) auto-tag themselves on first access.
   [ChunkData.cs](Assets/VRCMinecraft/Code/VoxelEngine/ChunkData.cs)
   [McWorld.cs](Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs)

B. **`McBlockTicker._IsRandomTickBlock` LUT** — pre-baked `bool[256]`
   array replaces a 15-case switch. Single byte-indexed load in the
   random tick inner loop.

C. **`McBlockTicker` scheduled tick dedup HashSet** — open-addressed
   hash table of packed (x,y,z,blockId) keys with linear probing.
   Replaces O(N) linear scan over up to 8192 entries — was effectively
   O(N²) during fluid floods, now O(1) per insert.

D. **`McTerrainGenerator` octave frequency LUT** — pre-baked
   `1/2^n` for n ∈ [0..15] replaces 16 `System.Math.Pow` calls per
   chunk (each is a marshaled C++ trip in Udon).

E. **`McTerrainGenerator` trilerp invariant hoist** — moved the 4 noise
   corner indices `idx00/idx01/idx10/idx11` out of the inner yPiece
   loop (they only depend on `terrain_xPiece/zPiece/l_gt/b2_gt`). Also
   hoisted `(xPieceOffset + i2) * 16` out of the inner k2 loop.

F. **`NoiseGenerator3dPerlin` gradient tables** — attempted to make
   `GRAD_X/Y/Z` `static readonly` to save ~17 KB of duplicated constants
   across the 92 noise generators built per worldgen init, but UdonSharp
   does not support static fields on user-defined types. Reverted to the
   original `instance readonly` form. The 17 KB memory cost is accepted.

### Lighting query public API

McWorld now exposes:
- `GetBlockLightValue(x, y, z)` — block-emitted light only
- `GetSavedSkyLightValue(x, y, z)` — stored sky light
- `GetFullBlockLightValue(x, y, z)` — Beta's `max(sky - skylightSubtracted, block)`
- `CanBlockSeeTheSky(x, y, z)` — Beta's `World.canBlockSeeTheSky`

McBlockTicker uses these in the tick parity fixes above.

---

## Items deferred to future sessions

These were identified in the audit but require either:
- Entirely new subsystems (item/inventory, tile entities, entity health)
- Major architectural refactors (greedy meshing, world-height layout change)
- Massive scene-file (.unity) byte-array edits

### Missing subsystems (cannot be "fixed" — must be designed)

- **Item / inventory system**: needed for block drops, tool durability,
  hotbar stack-size tracking, instamine via tools.
- **Tile entities**: chests, furnaces, signs, dispensers, jukeboxes,
  note blocks. Beta's `chunkTileEntityMap` has zero analog in the port.
- **Entity health / damage**: needed for fall damage, lava ignition,
  drowning, fire damage. `BaseEntity.cs` is currently an empty stub.
- **`blockActivated` dispatch**: right-click handlers for chest/door/lever/
  button/repeater. Currently right-click only places blocks.
- **`onBlockClicked` dispatch**: TNT ignition, note-block play, fire-start.
- **Sprint mechanic**: 1.3× movement multiplier + FoV change.
- **Sneak edge-stop / step-bump**: requires custom swept-AABB collision
  to replace Unity CharacterController.
- **Heightmap + sky-light vertical relight**: `Chunk.func_1003_g` analog.
- **Day/night cycle**: `worldTime`, `skylightSubtracted` updates, celestial
  angle for shaders.
- **Redstone**: wire, repeater, torch update logic, indirect power.
- **Music scheduler**: `ticksBeforeMusic` countdown to auto-play tracks
  every 10–20 minutes.
- **64 particles per block break** (4×4×4 grid) — requires particle
  manager redesign.

### Major refactors (need design discussion)

- **`flipXAxis` default**: Currently true to compensate for Unity's
  left-handed coord system. Making it false (Java-correct) would mirror
  the world for the player. The correct fix is to mirror the *rendering*
  not the data — major change.
- **Greedy meshing**: 70% vertex reduction but requires rewriting the
  mesh build pipeline.
- **DDA voxel raycast** (replaces `Physics.Raycast`): needed for
  per-block AABB correctness on slabs/torches/fences. Moderate scope.
- **Iterative iterative-BFS for `_PropagateChunkLightingOptimized`**:
  the smaller seed-then-propagate function was rewritten, but the
  full chunk lighting initializer still has its own logic that could
  be unified.
- **Sub-section dirty flagging** for chunk mesh rebuild: requires
  rewriting `RequestChunkMeshUpdate` to mark only the 4×4×4 mini-section
  containing the changed voxel.
- **Nibble-pack `blockMetadata`**: 2× memory saving but ripples through
  every metadata reader (water flow, leaf decay, fire age, etc.). Risky.

### Scene-file fixes (require massive byte-array edits in `Minecraft.unity`)

- Lever (69) `uv_all` 112 → 96
- Stone pressure plate (70) `uv_all` 72 → 1
- Wooden pressure plate (72) `uv_all` 97 → 4
- Portal (90) `uv_all` 193 → 14
- Cake (92) top texture 140 → 121
- Pumpkin (86) bottom 118 → 102
- Jack-O-Lantern (91) bottom 118 → 102
- Ice (79) `lightOpacity` 0 → 3
- Locked chest (95) `lightEmission` 0 → 15
- Snow_Layer (78) `canBlockGrass` true → false
- Trapdoor (96) `canBlockGrass` true → false
- Crops (59) shape Cube → Cross
- Doors/Stairs/Beds/Slabs/Snow/Cake shape Cube → special mesh

These require editing the `Minecraft.unity` scene file's serialized
byte arrays. Best done via a custom editor script (in
`Assets/Editor/VRCMinecraft/`) that modifies the `McBlockTypeManager`
ScriptableObject-style data.

### Cave generation on CPU path (`GenerateCaves` is a stub)

`McTerrainGenerator.GenerateCaves` is empty. With GPU worldgen available
the GPU shader carves caves; with GPU disabled there are no caves at all.
Wiring up `WorldGenCavesOld.Generate` requires iterating a 9-chunk
neighborhood per chunk and is a structural change to the chunk generator
state machine.

### `WorldGenCavesOld` JavaRandom reuse

The recursive `generateBranch` allocates `new JavaRandom(rand.NextLong())`
per call. Reusing via `SetSeed` requires a per-recursion-depth stack of
JavaRandom instances (since the recursion can split). Bounded but
requires care. Deferred.
