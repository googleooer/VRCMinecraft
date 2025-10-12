# Lighting System - Opacity Integration Fix

## Summary

Updated the lighting system to properly integrate with `McBlockTypeManager`'s light opacity data, ensuring consistency across the entire codebase.

## Changes Made

### 1. McWorld.cs - Core Lighting System
**File**: `Assets/VRCMinecraft/Code/VoxelEngine/McWorld.cs`

#### Changed: `_InitializeLightingSystem()`
- **Before**: Created hardcoded `blockLightOpacity` array with manually set values
- **After**: Loads opacity values from `McBlockTypeManager.GetBlockLightOpacity()` for all blocks
- **Benefit**: Single source of truth for all block opacity values

#### Enhanced Documentation:
- Added detailed comments explaining the skylight vertical pass algorithm
- Clarified that opacity forcing (air→1) only applies to horizontal propagation
- Documented face lighting sampling with concrete examples

### 2. McBlockTypeManager.cs - Data Management
**File**: `Assets/VRCMinecraft/Code/VoxelEngine/McBlockTypeManager.cs`

#### Changed: Build Memory Optimization
- Added `lightOpacityData = null;` to the build cleanup section
- Ensures consistency with other editor-only arrays

### 3. McBlockTypeManagerEditor.cs - Editor Integration
**File**: `Assets/Editor/VRCMinecraft/McBlockTypeManagerEditor.cs`

#### Added: Light Opacity Editor Support
- New `SerializedProperty lightOpacityDataProp` for editor access
- Integrated into all array operations:
  - `OnEnable()` - Property initialization
  - `AddNewBlockType()` - Default value of 15 (opaque)
  - `ReorderBlockTypes()` - Maintains order during reordering
  - `ForceSyncAllArrays()` - Syncs with other arrays
  - `CheckOverallMismatch()` - Validates array sizes
  - `CheckCanDisplayProperties()` - Enables property display
  - `RemoveElementFromAllArrays()` - Cleanup on removal

#### Added: UI Controls
- New `DrawIntSlider()` helper method for integer slider input
- Light Opacity slider in inspector (0-15 range)
- Tooltip: "0=air, 1=leaves, 3=water, 15=opaque"
- Updated element height calculation (+1 line)

## How It Works

### Opacity Values (0-15 scale)
- **0** = Air, transparent blocks (no light reduction during vertical pass)
- **1** = Leaves, web (minimal light reduction)
- **3** = Water, ice (medium light reduction)
- **15** = Opaque blocks (complete light blockage)

### Lighting Algorithm

#### Vertical Skylight Pass (Initial)
```csharp
// For each block going downward:
stored_light = incoming_light - current_block_opacity
```
Example: Air(Y=10, light=15) → Water(Y=9) gets light = 15 - 3 = 12

#### Horizontal Propagation
```csharp
// Air blocks get forced to opacity=1 for decay
if (opacity == 0) opacity = 1;
propagated_light = max_neighbor_light - target_opacity
```
Example: Through water = 3 (water opacity) + 1 (forced air) = 4 per step

#### Face Rendering
Each face samples the **adjacent block** it faces, not the block it belongs to:
- Water block top face → samples air above (bright)
- Water block bottom face → samples block below (dimmer)

## Configuration in Editor

1. Open the `McBlockTypeManager` asset in the inspector
2. Expand any block type in the list
3. Find **"Light Opacity"** slider under the "General" section
4. Adjust value 0-15:
   - 0 for transparent/air-like blocks
   - 1 for leaves
   - 3 for water/ice
   - 15 for solid opaque blocks
5. Click **"Bake Properties"** to update runtime data
6. Changes will take effect after `McWorld` initializes

## Testing Recommendations

1. **Check Console Log**: Should see `"[McWorld] Loaded light opacity values from McBlockTypeManager"`
2. **Verify Water Lighting**:
   - Place water blocks underwater
   - Top faces should be brighter (sampling air/water above)
   - Bottom/side faces should be darker
3. **Light Decay**: Light should decay by 3 per water block vertically, 4 per block horizontally

## Benefits

✅ **Single Source of Truth**: All opacity values come from `McBlockTypeManager`  
✅ **Editor Configuration**: Easily adjust opacity per block in the inspector  
✅ **Consistency**: No more hardcoded values scattered across the codebase  
✅ **Maintainability**: Adding new blocks automatically uses configured opacity  
✅ **Fallback**: Still has hardcoded fallback if manager unavailable  

## Migration Notes

- Existing block types will default to opacity=15 (opaque) when first opened
- Use "Force Sync Arrays" button if array size mismatches occur
- Water blocks (IDs 8, 9) and ice (ID 79) should be set to opacity=3
- Leaves (ID 18) should be set to opacity=1
- Air and transparent blocks should be set to opacity=0

