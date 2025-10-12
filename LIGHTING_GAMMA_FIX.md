# Lighting Gamma/Linear Color Space Fix

## Problem Description

The terrain was rendering darker than Minecraft Beta 1.7.3, especially on side faces. This was caused by incorrect handling of gamma and linear color space conversions in the lighting calculations.

## Root Cause Analysis

### How Minecraft Beta 1.7.3 Handles Lighting

1. **Light Brightness Table** (`WorldProvider.java`, lines 19-25):
   ```java
   float minBrightness = 0.05F;
   for(int i = 0; i <= 15; ++i) {
       float darkness = 1.0F - (float)i / 15.0F;
       lightBrightnessTable[i] = (1.0F - darkness) / (darkness * 3.0F + 1.0F) * 0.95F + 0.05F;
   }
   ```
   - Converts light level (0-15) to brightness (0.0-1.0)
   - This formula creates a **gamma-corrected** curve for perceptual brightness
   - Values are in **gamma space** (designed for direct display on CRT monitors)

2. **Face Shading Multipliers** (`RenderBlocks.java`, lines 2220-2223):
   ```java
   float var10 = 0.5F;  // Bottom face (Y-)
   float var11 = 1.0F;  // Top face (Y+)
   float var12 = 0.8F;  // Z faces (North/South)
   float var13 = 0.6F;  // X faces (East/West)
   ```
   - Also in **gamma space**
   - Applied to simulate ambient occlusion and directional lighting

3. **Application in Rendering** (`RenderBlocks.java`, line 2252):
   ```java
   var8.setColorOpaque_F(var17 * var27, var20 * var27, var23 * var27);
   ```
   - Multiplies face shading × light brightness (both in gamma space)
   - Used directly in OpenGL 1.x fixed-function pipeline (gamma rendering)

### What Was Wrong in the Original Shader

```glsl
// WRONG APPROACH:
half lightBrightness = i.color.a;  // Gamma space value
lightBrightness = GammaToLinearSpace(lightBrightness);  // Convert to linear
col.rgb *= lightBrightness;  // Apply linear brightness
col.rgb *= calcBrightness(i.normal);  // Multiply by GAMMA space face multiplier!
```

**Issues:**
1. Converted light brightness to linear space
2. But face shading multipliers remained in gamma space
3. Mixing gamma and linear values produced incorrect (darker) results
4. Face shading values were also incorrect (0.2, 0.3, 0.6 instead of 0.5, 0.6, 0.8)

## The Solution

### Corrected Face Shading Values

Changed to match Minecraft Beta 1.7.3:
- **Bottom face (Y-)**: 0.2 → **0.5** ✓
- **X faces (sides)**: 0.3 → **0.6** ✓  
- **Z faces (sides)**: 0.6 → **0.8** ✓
- **Top face (Y+)**: 1.0 (unchanged) ✓
- **Diagonal faces (cross blocks)**: 1.0 → **0.7** (average of 0.6 and 0.8) ✓

### Corrected Lighting Pipeline

```glsl
// CORRECT APPROACH:
// Step 1: Get light brightness (gamma space)
half lightBrightness = max(0.02, i.color.a);

// Step 2: Get face shading (gamma space) 
half faceBrightness = calcBrightness(i.normal);

// Step 3: Multiply in gamma space (matching Minecraft)
half combinedBrightness = lightBrightness * faceBrightness;

// Step 4: Convert COMBINED result to linear space
combinedBrightness = GammaToLinearSpace(combinedBrightness);

// Step 5: Apply to color
col.rgb *= combinedBrightness;
```

**Why This Works:**
1. Both light brightness and face shading are kept in gamma space initially
2. They're multiplied together (matching Minecraft's approach)
3. Only the final combined value is converted to linear space
4. This matches Unity's linear color space rendering expectations
5. If Unity is in gamma color space, `GammaToLinearSpace` becomes a no-op

## Technical Details

### Color Space Conversion

Unity's `GammaToLinearSpace()` function performs:
```glsl
// Approximate sRGB gamma to linear conversion
linear = pow(gamma, 2.2)
```

This conversion is necessary because:
- Unity's **Linear Color Space** rendering expects linear light values
- Minecraft's values are in **Gamma Space** (designed for CRT monitors)
- Modern displays still expect gamma-corrected output, which Unity handles automatically

### Why Minecraft Used Gamma Space

Minecraft Beta 1.7.3 (2011) used OpenGL 1.x:
- Fixed-function pipeline assumed gamma space
- CRT monitors had non-linear response curves
- Gamma-corrected values looked correct on period-accurate hardware
- No automatic linear workflow like modern engines

### Order of Operations Matters

**Wrong:** `linear(brightness) × gamma(faceShading)` = Mixing color spaces ❌  
**Right:** `linear(brightness × faceShading)` = Consistent color space ✓

This is mathematically different:
- `pow(a, 2.2) × b ≠ pow(a × b, 2.2)` when b ≠ 1

## Testing & Verification

Compare the brightness of different faces:
- **Top faces**: Should be brightest (multiplier = 1.0)
- **Z faces**: Should be brighter than X faces (0.8 vs 0.6)
- **Bottom faces**: Should be noticeably darker but not black (0.5)
- **Overall**: Terrain should match Minecraft Beta 1.7.3's lighting

## References

- `RenderBlocks.java` (Minecraft Beta 1.7.3): Face shading and lighting application
- `WorldProvider.java`: Light brightness table generation
- `Block.java`: Block brightness calculation
- Unity Documentation: Linear vs Gamma color space

## Files Modified

- `Assets/VRCMinecraft/shaders/MCTerrain.shader`
  - Updated `calcBrightness()` face shading values
  - Corrected lighting calculation order in `frag()` shader
  - Added detailed comments explaining gamma/linear conversion
