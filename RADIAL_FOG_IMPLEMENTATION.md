# Minecraft-Style Radial Fog Implementation

## Overview

This implementation adds Minecraft Beta 1.7.3-style radial fog to the terrain shader, eliminating the "fog wall" effect associated with linear fog while maintaining Quest VR compatibility (no depth buffer required).

## Linear Fog vs Radial Fog

### Linear Fog (Traditional Unity Fog)
- **Distance Calculation**: Based on depth buffer or linear interpolation along view direction
- **Visual Effect**: Creates a visible "fog wall" where fog begins abruptly
- **Formula**: `fogFactor = (fogEnd - depth) / (fogEnd - fogStart)`
- **Problem**: Objects at the same depth but different angles appear with different fog density

### Radial Fog (Minecraft-Style)
- **Distance Calculation**: Based on actual 3D distance from camera to fragment
- **Visual Effect**: Natural, gradual fog that increases uniformly with distance
- **Formula**: `distance = length(worldPos - cameraPos)` then apply fog function
- **Benefit**: Consistent fog appearance regardless of viewing angle

## Minecraft Beta 1.7.3 Fog Implementation

### Fog Modes (from EntityRenderer.java)

1. **Linear Fog** (Default terrain fog):
   ```java
   GL11.glFogi(GL11.GL_FOG_MODE, GL11.GL_LINEAR);
   GL11.glFogf(GL11.GL_FOG_START, this.farPlaneDistance * 0.25F); // 25% of render distance
   GL11.glFogf(GL11.GL_FOG_END, this.farPlaneDistance);           // 100% of render distance
   ```

2. **Exponential Fog** (Water, lava, clouds):
   ```java
   GL11.glFogi(GL11.GL_FOG_MODE, GL11.GL_EXP);
   GL11.glFogf(GL11.GL_FOG_DENSITY, 0.1F); // Water/clouds: 0.1, Lava: 2.0
   ```

3. **Radial Distance Mode** (NV_fog_distance extension):
   ```java
   if(GLContext.getCapabilities().GL_NV_fog_distance) {
       GL11.glFogi(NVFogDistance.GL_FOG_DISTANCE_MODE_NV, NVFogDistance.GL_EYE_RADIAL_NV);
   }
   ```

### Fog Colors by Environment
- **Sky/Overworld**: Dynamic based on time of day and weather
- **Water**: `(0.4, 0.4, 0.9)` - Blue-tinted fog
- **Lava**: `(0.4, 0.3, 0.3)` - Red-tinted fog  
- **Clouds**: `(1.0, 1.0, 1.0)` - White fog
- **Nether**: Starts at distance 0 (immediate fog)

## Shader Implementation

### Properties Added
```glsl
_FogColor ("Fog Color", Color) = (0.5, 0.6, 0.7, 1.0)
_FogDensity ("Fog Density", Range(0.0, 0.1)) = 0.02
_FogStart ("Fog Start Distance", Float) = 32.0
_FogEnd ("Fog End Distance", Float) = 128.0
_FogMode ("Fog Mode", Range(0, 2)) = 0
```

### Vertex Shader Changes
```glsl
struct v2f {
    // ... existing fields ...
    float3 worldPos : TEXCOORD3; // World position for radial distance calculation
};

v2f vert (appdata v) {
    // ... existing code ...
    
    // Calculate world position for radial fog (Quest-compatible, no depth buffer needed)
    o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
    
    return o;
}
```

### Fragment Shader Fog Function
```glsl
half calcMinecraftFog(float3 worldPos)
{
    // Calculate radial distance from camera (Quest-compatible)
    float3 viewDir = worldPos - _WorldSpaceCameraPos;
    half distance = length(viewDir);
    
    half fogFactor = 1.0;
    
    if (_FogMode < 0.5) // Linear fog
    {
        fogFactor = saturate((_FogEnd - distance) / (_FogEnd - _FogStart));
    }
    else if (_FogMode < 1.5) // Exponential fog
    {
        fogFactor = exp(-_FogDensity * distance);
    }
    else // Exponential Squared fog
    {
        fogFactor = exp(-_FogDensity * _FogDensity * distance * distance);
    }
    
    return saturate(fogFactor);
}
```

### Fog Application
```glsl
// Calculate fog factor based on radial distance
half fogFactor = calcMinecraftFog(i.worldPos);

// Apply Minecraft-style fog blending
col.rgb = lerp(_FogColor.rgb, col.rgb, fogFactor);

// Keep Unity's built-in fog as fallback
UNITY_APPLY_FOG(i.fogCoord, col);
```

## Quest VR Compatibility

### No Depth Buffer Required
- Uses `worldPos - _WorldSpaceCameraPos` for distance calculation
- Computed in vertex shader, interpolated to fragment shader
- No dependency on depth buffer or screen-space calculations

### Performance Considerations
- Distance calculation is performed per-vertex, not per-fragment
- Minimal additional cost since world position is needed for other calculations
- Compatible with Quest's rendering pipeline limitations

## Configuration Examples

### Default Minecraft Terrain Fog
```
Fog Mode: 0 (Linear)
Fog Start: 32.0 (25% of 128 block render distance)
Fog End: 128.0 (100% of render distance)
Fog Color: (0.5, 0.6, 0.7) - Sky blue
```

### Water Fog
```
Fog Mode: 1 (Exponential)
Fog Density: 0.1
Fog Color: (0.4, 0.4, 0.9) - Blue tint
```

### Lava Fog
```
Fog Mode: 1 (Exponential)
Fog Density: 2.0
Fog Color: (0.4, 0.3, 0.3) - Red tint
```

### Nether Fog
```
Fog Mode: 0 (Linear)
Fog Start: 0.0 (immediate fog)
Fog End: 64.0 (shorter render distance)
Fog Color: (0.2, 0.1, 0.1) - Dark red
```

## Dynamic Fog Color Integration

The fog color can be dynamically controlled by scripts to match:
- **Time of day**: Sunrise/sunset colors
- **Weather**: Rain/fog atmospheric effects
- **Biome**: Desert, forest, ocean-specific fog colors
- **Dimension**: Overworld, Nether, End-specific fog

## Testing & Verification

### Visual Tests
1. **No Fog Wall**: Fog should appear gradual and natural, not as a visible plane
2. **Consistent Distance**: Objects at the same distance should have identical fog density
3. **Smooth Transition**: Fog should blend smoothly from clear to opaque
4. **Performance**: Should maintain Quest VR frame rate targets

### Comparison with Minecraft
- Fog should start at approximately 25% of render distance
- Fog should be fully opaque at render distance
- Fog color should match the sky/atmosphere color
- Different fog modes should behave like Minecraft's water/lava fog

## Files Modified

- `Assets/VRCMinecraft/shaders/MCTerrain.shader`
  - Added fog properties and variables
  - Modified vertex shader to calculate world position
  - Added `calcMinecraftFog()` function
  - Updated fragment shader to apply radial fog
  - Maintained Unity fog compatibility

## References

- `EntityRenderer.java` (Minecraft Beta 1.7.3): Fog setup and modes
- Unity Documentation: Fog and color space handling
- Quest VR Documentation: Performance and compatibility requirements
- OpenGL NV_fog_distance extension specification
