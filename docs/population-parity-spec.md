# Population parity spec — feature generation 1:1 with b1.7.3

Status: researched against the deobfuscated Java (2026-07-20); implementation not started.
Scope: everything vanilla `ChunkProviderGenerate.populate()` places that we do not:
ores + dirt/gravel/clay blobs, water/lava lakes, dungeons, cacti, reeds, pumpkins,
mushrooms, dead bushes, springs, snow cover, and the missing tree species
(big oak, birch, both spruces). Trees/tall grass/flowers exist today but are **not**
save-accurate (see "Existing divergences").

## The one rule that shapes everything

Vanilla population is **one `java.util.Random` stream per chunk**, consumed strictly
sequentially by every pass in a fixed order. Any pass's outcome depends on the exact
number of draws made by every earlier pass — including passes that roll coordinates
and then veto themselves. There is no per-feature reseeding (except big trees, which
deliberately fork: one `nextLong()` from the shared stream seeds a private Random, so
a big tree always costs exactly 2 LCG states regardless of shape).

Seed setup, exact:

```
rand.setSeed(worldSeed);
long a = rand.nextLong() / 2L * 2L + 1L;   // Java division truncates toward zero;
long b = rand.nextLong() / 2L * 2L + 1L;   // this is NOT (x | 1) for negatives
rand.setSeed((long)chunkX * a + (long)chunkZ * b ^ worldSeed);
```

`a`/`b` are world-constant. Biome for the whole pass is sampled once at
`(x0 + 16, z0 + 16)`. Most features roll `x0 + nextInt(16) + 8` (the +8 population
window straddling 4 chunks); clay and ores roll without +8 but their generators add
+8 internally — same window.

Pass order (attempt counts and y-formulas in the research; each is load-bearing):
1 water lake (1/4) · 2 lava lake (1/8, veto rolls still consumed) · 3 dungeons ×8 ·
4 clay ×10 · 5 dirt ×20 · 6 gravel ×10 · 7 coal ×20 · 8 iron ×20 · 9 gold ×2 ·
10 redstone ×8 · 11 diamond ×1 · 12 lapis ×1 · 13 trees (noise+rand count, biome
species table) · 14 yellow flowers · 15 tall grass · 16 dead bush · 17 rose (1/2) ·
18 brown mushroom (1/4) · 19 red mushroom (1/8) · 20 reeds ×10 · 21 pumpkin (1/32) ·
22 cactus ×10 · 23 water springs ×50 · 24 lava springs ×20 · 25 snow (no RNG).

Consequence: **1:1 trees require implementing (or exactly stream-simulating) every
pass before trees.** Lakes' RNG consumption is derivable from the stream alone, but
dungeons consume world-dependent rolls (`nextInt(4)` per *solid* floor cell, chest
and loot rolls on success), so the stream state after pass 3 cannot be known without
real world data. There is no shortcut around having the world bytes at populate time.

## Existing divergences to fix first (Phase 0)

- The GPU collect path seeds decoration chunks with **hardcoded multipliers**
  (`cx*1364927 + cz*7420851 ^ worldSeed`) while vanilla (and our own CPU fallback)
  derives `a`/`b` from the world seed. Current tree/grass/flower positions are
  self-consistent but cannot match a real save.
- Tree leaf corners use 8 pre-rolled bits reused modulo 8; vanilla rolls
  `nextInt(2)` per corner cell in stream order.
- Current pass order (trees → flowers → grass only) ignores passes 1–12 entirely.

## Architecture decision: where does populate run?

**Recommended: CPU populate after a terrain readback**, replacing the GPU
decoration blits. Rationale:

- Vanilla populate reads and writes the *real* post-cave world; dungeons, lakes,
  reeds (water adjacency), cactus (neighbor solidity) and tree ground checks are all
  world-dependent. The CPU path gets this for free from chunk arrays; the GPU path
  needs an anchor texture per neighbor chunk and still can't see cave air under a
  border trunk (a known approximation today).
- Population is **sparse**: ~82 veins ≈ 1–2k block writes, a tree ≈ 35, a lake ≈ 600.
  This is candidate-collection-sized CPU work (already budget-sliced today), not
  per-voxel work — the reason terrain went GPU does not apply.
- The CPU fallback decorator (DecoratingTerrain steps 1–7) already exists and is
  sliced; this extends it into the primary path and lets the whole anchor system
  (expensive misses, 45–77 ms renders, corner-bit approximation) be deleted.
- Cost: the readback moves earlier (post-cave instead of post-decoration) — same
  count of readbacks, plus populate writes go through normal CPU chunk arrays and
  the existing remesh path.
- Trade-off: the dormant GPU-resident mode (no CPU mirror) would need a GPU populate
  later. Accepted: resident mode is staged future work, and candidates-in-texture
  remains available as its eventual shape.

Population ordering across chunks follows vanilla: chunk (cx,cz) populates when its
+x/+z neighbors exist; with our fixed 32×32 world, that is a deterministic sweep
during load (populate chunk (cx,cz) once (cx+1,cz+1) terrain has been read back).

## Phases

**Phase 0 — population director skeleton.** One CPU routine per chunk that walks the
full vanilla pass list with a bit-exact JavaRandom, deriving `a`/`b` from the world
seed. Every pass either executes or (until implemented) burns the documented RNG
draws. From day one, trees/grass/flowers move into it at their correct stream
positions — this alone changes their placement to save-accurate (verify against
Glacier before proceeding).

**Phase 1 — WorldGenMinable family** (dirt, gravel, coal, iron, gold, redstone,
diamond, lapis, clay). Fixed RNG cost per call (1 float + 2 nextInt(3) + size+1
doubles), replaces only stone; clay only replaces sand and only when rolled inside
water. Straight port, ~80 veins/chunk.

**Phase 2 — small plants**: cactus (neighbor-solidity + sand), reeds (water-adjacent
grass/dirt, fixed y), dead bush, pumpkin (facing roll only on success), brown/red
mushrooms. Mushroom light rule (`light < 13`) is evaluated against block light at
populate time; approximate as "not sky-visible" initially and document it.

**Phase 3 — tree species**: birch (oak logic, height 5–7, meta 2), taiga1/taiga2
(new leaf shapes, spruce meta 1, taiga biome roll 1/3), big oak (1/10 default roll;
private forked Random; cluster/branch geometry). Forest biome consumes 1 *or* 2
selection rolls (birch short-circuit) — director must model that.

**Phase 4 — lakes and dungeons.** Lakes: blob flags are pure-RNG; viability abort
reads the world but consumes nothing; lava lakes consume `nextInt(2)` per border
cell with localY ≥ 4 regardless of solidity. Dungeons: cobble/mossy shell, spawner
and chest as blocks first (loot tables and tile entities are game-layer work, later).

**Phase 5 — springs and snow.** Vanilla runs the fluid cascade *during* populate
with the shared rand — but springs are the last RNG consumers (only no-RNG snow
follows), so their internal flow rolls cannot shift any other feature. We place the
spring block and let our proven-1:1 CPU fluid sim settle it over the first seconds:
deviation in mechanism, parity in end state. Snow cover: no RNG, temperature minus
`(y-64)/64*0.3`, top solid block, not on ice.

## Documented tolerances (accepted, revisit only if visibly wrong)

- Spring fluids settle via the runtime tick instead of instantaneous in-populate flow.
- Mushroom light gate approximated by sky-occlusion until block light exists at
  populate time.
- Dungeon loot/spawner behavior deferred to the game layer (blocks placed correctly).

## Verification

Per phase, compare against the Glacier save with the established coordinate mapping
(`x_mc = -x_vrcm - 1`, `y` equal, `z` equal): ore-vein cell sets in a handful of
chunks, tree positions/species across the spawn area, lake shells, dungeon boxes.
The save is ground truth; any mismatch is a stream-order bug until proven otherwise.
