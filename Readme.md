# VRCMinecraft

Minecraft-like voxel world in VRChat using UdonSharp.

## Performance Optimizations

### Terrain Generation Optimizations (Latest)

The terrain generator has been heavily optimized for UdonSharp performance:

#### 1. **Gradient Table Lookups** (Most Important)
- Replaced branching gradient calculations with pre-computed lookup tables
- Eliminated ~50,000+ conditional branches per chunk
- 3 simple array lookups + multiplications instead of 6+ conditional checks per gradient
- Expected **30-40% speedup** in NoiseGen1/2/3

#### 2. **Function Inlining**
- Inlined `lerp()` function in critical hot paths
- Reduced method call overhead in noise generation loops
- Expected **5-10% speedup** overall

#### 3. **Local Variable Caching**
- Cached frequently accessed arrays locally (permutations, noiseCache, etc.)
- Reduced field access overhead in tight loops
- Expected **5-8% speedup**

#### 4. **Mathematical Optimizations**
- Replaced divisions with multiplications (`/= 2.0` → `*= 0.5`)
- Pre-calculated constants (`1/512`, `1/8000`, etc.)
- Used `System.Array.Clear()` instead of manual loops
- Expected **3-5% speedup**

#### 5. **Index Calculation Optimization**
- Pre-computed array indices and offsets
- Reduced redundant calculations in nested loops
- Combined related calculations
- Expected **5-8% speedup**

### Expected Total Performance Impact

| Component | Before | Expected After | Improvement |
|-----------|--------|----------------|-------------|
| NoiseGen1 | ~95ms | ~55-65ms | **30-40%** |
| NoiseGen2 | ~99ms | ~60-70ms | **30-40%** |
| NoiseGen3 | ~40ms | ~27-32ms | **20-30%** |
| GetBiomes | ~30ms | ~24-27ms | **10-20%** |
| **TOTAL** | **~291ms** | **~180-210ms** | **~30-38%** |

### Key Insight for UdonSharp

UdonSharp performs best with:
- ✅ Simple array lookups
- ✅ Straight-line code without branches
- ✅ Minimal method calls in hot loops
- ❌ Complex conditional logic
- ❌ Frequent method calls

The gradient table lookup optimization is particularly effective because it replaces:
```csharp
// OLD: 6+ conditional checks + branches
int h = hash & 15;
double u = h < 8 ? x : y;
double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
```

With:
```csharp
// NEW: 1 index + 3 array lookups + 3 multiplications
int h = hash & 15;
return GRAD_X[h] * x + GRAD_Y[h] * y + GRAD_Z[h] * z;
```

## Testing

Test the optimizations by checking chunk generation times in the Unity console.
