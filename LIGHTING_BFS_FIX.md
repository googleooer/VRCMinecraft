# Lighting BFS Algorithm Fix - Minecraft Beta 1.7.3

## **CRITICAL ISSUE IDENTIFIED**

Our current implementation uses a **PUSH-based** BFS, but Minecraft Beta 1.7.3 uses a **PULL-based** algorithm!

## **Minecraft's Algorithm (CORRECT)**

### **Core Principle: PULL from all 6 neighbors**

```java
// For each block being updated:
int light_X_minus = getSavedLightValue(x-1, y, z);
int light_X_plus  = getSavedLightValue(x+1, y, z);
int light_Y_minus = getSavedLightValue(x, y-1, z);
int light_Y_plus  = getSavedLightValue(x, y+1, z);
int light_Z_minus = getSavedLightValue(x, y, z-1);
int light_Z_plus  = getSavedLightValue(x, y, z+1);

// Find MAXIMUM of all 6 neighbors
int maxNeighborLight = max(light_X_minus, light_X_plus, light_Y_minus, 
                            light_Y_plus, light_Z_minus, light_Z_plus);

// Apply opacity (air has opacity 0, treated as 1)
int opacity = Block.lightOpacity[blockID];
if (opacity == 0) opacity = 1;

int newLight = maxNeighborLight - opacity;
if (newLight < 0) newLight = 0;

// Max with emission
int emission = Block.lightValue[blockID];
if (emission > newLight) newLight = emission;

// UPDATE if changed
if (currentLight != newLight) {
    setLight(newLight);
    
    // Schedule ALL 6 NEIGHBORS to re-evaluate (not push to them!)
    scheduleLightUpdate(x-1, y, z);
    scheduleLightUpdate(x+1, y, z);
    scheduleLightUpdate(x, y-1, z);
    scheduleLightUpdate(x, y+1, z);
    scheduleLightUpdate(x, y, z-1);
    scheduleLightUpdate(x, y, z+1);
}
```

## **Our Current Algorithm (WRONG)**

```csharp
// WRONG: We take current block's light and PUSH it to neighbors
int centerLight = chunk.lightData[centerIndex];

// WRONG: Push to each neighbor individually
_PropagateLightToNeighbor(chunk, fullData, x-1, y, z, centerLight, ...);
_PropagateLightToNeighbor(chunk, fullData, x+1, y, z, centerLight, ...);
// etc...

// In _PropagateLightToNeighbor:
int newLight = sourceLight - opacity;  // WRONG: Only considering ONE source
if (newLight > currentLight) {
    currentLight = newLight;
    queue.add(neighbor);  // Add neighbor to propagate further
}
```

## **Why This Causes Pitch Black Chunks**

1. `_ImportLightFromNeighbors` correctly sets boundary blocks to light values from ready neighbors
2. Our PUSH-based BFS starts from light sources (sky light 15, emissive blocks)
3. Light pushes outward, but when it reaches a boundary that already has imported light, it doesn't properly "meet in the middle"
4. The imported light on boundaries doesn't propagate inward because there's no mechanism for blocks to PULL from their neighbors
5. Result: Interior blocks remain dark even though boundary has light

## **The Correct Fix**

### **Change BFS from PUSH to PULL:**

```csharp
// Correct: Each block PULLS from ALL 6 neighbors and takes MAX
private void _UpdateBlockLight(ChunkData chunk, byte[] fullData, int x, int y, int z, bool isSkyLight)
{
    int blockIndex = y * (chunkSizeXZ * chunkSizeXZ) + z * chunkSizeXZ + x;
    byte blockID = fullData[blockIndex];
    
    // Get light from ALL 6 neighbors
    int light_X_minus = _GetNeighborLight(chunk, fullData, x-1, y, z, isSkyLight);
    int light_X_plus  = _GetNeighborLight(chunk, fullData, x+1, y, z, isSkyLight);
    int light_Y_minus = _GetNeighborLight(chunk, fullData, x, y-1, z, isSkyLight);
    int light_Y_plus  = _GetNeighborLight(chunk, fullData, x, y+1, z, isSkyLight);
    int light_Z_minus = _GetNeighborLight(chunk, fullData, x, y, z-1, isSkyLight);
    int light_Z_plus  = _GetNeighborLight(chunk, fullData, x, y, z+1, isSkyLight);
    
    // Find MAX of all neighbors
    int maxNeighborLight = light_X_minus;
    if (light_X_plus > maxNeighborLight) maxNeighborLight = light_X_plus;
    if (light_Y_minus > maxNeighborLight) maxNeighborLight = light_Y_minus;
    if (light_Y_plus > maxNeighborLight) maxNeighborLight = light_Y_plus;
    if (light_Z_minus > maxNeighborLight) maxNeighborLight = light_Z_minus;
    if (light_Z_plus > maxNeighborLight) maxNeighborLight = light_Z_plus;
    
    // Apply opacity
    int opacity = lightOpacityCache[blockID];
    if (opacity == 0) opacity = 1;
    
    int newLight = maxNeighborLight - opacity;
    if (newLight < 0) newLight = 0;
    
    // Max with emission/sky visibility
    int emissionOrSky = 0;
    if (isSkyLight) {
        // Check if can see sky
        if (_CanBlockSeeSky(chunk, x, y, z)) emissionOrSky = 15;
    } else {
        emissionOrSky = lightEmissionCache[blockID];
    }
    
    if (emissionOrSky > newLight) newLight = emissionOrSky;
    
    // Get current light
    byte currentLightByte = chunk.lightData[blockIndex];
    int currentLight = isSkyLight ? ((currentLightByte >> 4) & 0xF) : (currentLightByte & 0xF);
    
    // UPDATE if changed
    if (newLight != currentLight) {
        // Set new light value
        if (isSkyLight) {
            int blockLight = currentLightByte & 0xF;
            chunk.lightData[blockIndex] = (byte)((newLight << 4) | blockLight);
        } else {
            int skyLight = (currentLightByte >> 4) & 0xF;
            chunk.lightData[blockIndex] = (byte)((skyLight << 4) | newLight);
        }
        
        // Schedule ALL 6 neighbors to re-evaluate (add to queue)
        _ScheduleNeighborUpdate(chunk, x-1, y, z, queue);
        _ScheduleNeighborUpdate(chunk, x+1, y, z, queue);
        _ScheduleNeighborUpdate(chunk, x, y-1, z, queue);
        _ScheduleNeighborUpdate(chunk, x, y+1, z, queue);
        _ScheduleNeighborUpdate(chunk, x, y, z-1, queue);
        _ScheduleNeighborUpdate(chunk, x, y, z+1, queue);
    }
}
```

## **Why PULL Works**

1. Each block evaluates: "What's the brightest light among ALL my neighbors?"
2. Subtracts its own opacity
3. If result changes, tells all neighbors to re-evaluate
4. Light naturally "flows" from bright to dark areas
5. Imported boundary light properly propagates inward because blocks PULL from those bright boundaries

## **Implementation Steps**

1. ✅ Keep `_ImportLightFromNeighbors` - it correctly seeds boundary blocks
2. ❌ Replace PUSH-based BFS with PULL-based algorithm  
3. ✅ Each block queries ALL 6 neighbors and takes MAX
4. ✅ Subtracts opacity from MAX (not from individual neighbors)
5. ✅ Schedules neighbors to update (not pushes light to them)
6. ✅ Keep recursion guard to prevent infinite cross-chunk updates

## **Expected Results**

- ✅ Boundary light properly propagates inward
- ✅ No more pitch black chunk interiors
- ✅ Light "meets in the middle" correctly
- ✅ Matches Minecraft Beta 1.7.3 behavior exactly
