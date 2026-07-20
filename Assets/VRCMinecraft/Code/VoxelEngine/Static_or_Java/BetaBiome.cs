public enum BetaBiomeEnum
    {
        RAINFOREST,
        SWAMPLAND,
        SEASONAL_FOREST,
        FOREST,
        SAVANNA,
        SHRUBLAND,
        TAIGA,
        DESERT,
        PLAINS,
        ICE_DESERT,
        TUNDRA
    }

public static class BetaBiome
{

    public static string getName(BetaBiomeEnum biome)
    {
        return biome.ToString();
    }

    // Converted from Java to C#
    public static int getTreesPerChunk(JavaRandom random, NoiseGeneratorOctaves3D noiseGen, int chunkX, int chunkZ, BetaBiomeEnum biome) {
        int x = chunkX * 16 + 8;
        int z = chunkZ * 16 + 8;
        // CRITICAL: Enable tree noise calculation for proper Beta 1.7.3 tree distribution
        int treesRand = (int) ((noiseGen.generateNoise(x * 0.5D, z * 0.5D) / 8D + random.NextDouble() * 4D + 4D) / 3D);

        int trees = 0;
        if (random.NextInt(10) == 0) {
            trees++;
        }
        if (biome == BetaBiomeEnum.FOREST) {
            trees += treesRand + 5;
        }

        if (biome == BetaBiomeEnum.RAINFOREST) {
            trees += treesRand + 5;
        }

        if (biome == BetaBiomeEnum.SEASONAL_FOREST) {
            trees += treesRand + 2;
        }

        if (biome == BetaBiomeEnum.TAIGA) {
            trees += treesRand + 5;
        }

        if (biome == BetaBiomeEnum.DESERT) {
            trees -= 20;
        }

        if (biome == BetaBiomeEnum.TUNDRA) {
            trees -= 20;
        }

        if (biome == BetaBiomeEnum.PLAINS) {
            trees -= 20;
        }
        return trees;
    }

    public static int getCactusForBiome(BetaBiomeEnum biome) {
        int k16 = 0;
        if (biome == BetaBiomeEnum.DESERT) {
            k16 += 10;
        }
        return k16;
    }

    public static int getDeadBushPerChunk(BetaBiomeEnum biome) {
        int byte1 = 0;
        if (biome == BetaBiomeEnum.DESERT) {
            byte1 = 2;
        }
        return byte1;
    }

    public static int getGrassPerChunk(BetaBiomeEnum biome) {
        int byte1 = 0;
        if (biome == BetaBiomeEnum.FOREST) {
            byte1 = 2;
        }

        if (biome == BetaBiomeEnum.RAINFOREST) {
            byte1 = 10;
        }

        if (biome == BetaBiomeEnum.SEASONAL_FOREST) {
            byte1 = 2;
        }

        if (biome == BetaBiomeEnum.TAIGA) {
            byte1 = 1;
        }

        if (biome == BetaBiomeEnum.PLAINS) {
            byte1 = 10;
        }
        return byte1;
    }

    public static int getFlowersPerChunk(BetaBiomeEnum biome) {
        int flowers = 0;
        if (biome == BetaBiomeEnum.FOREST) {
            flowers = 2;
        }

        if (biome == BetaBiomeEnum.SEASONAL_FOREST) {
            flowers = 4;
        }

        if (biome == BetaBiomeEnum.TAIGA) {
            flowers = 2;
        }

        if (biome == BetaBiomeEnum.PLAINS) {
            flowers = 3;
        }
        return flowers;
    }

    // ===== BIOME TINTING SUPPORT (Beta 1.7.3) =====
    // Simplified biome color calculation based on temperature and rainfall
    // Matches Minecraft Beta 1.7.3's ColorizerGrass and ColorizerFoliage behavior
    
    /// <summary>
    /// Get grass color based on temperature and rainfall.
    /// EXACT Beta 1.7.3 implementation using grasscolor.png texture lookup.
    /// </summary>
    public static UnityEngine.Color GetGrassColor(double temperature, double rainfall, UnityEngine.Texture2D grassColorTexture) {
        // If no texture provided, return default grass green
        if (grassColorTexture == null)
        {
            return new UnityEngine.Color(0.5f, 0.8f, 0.4f, 1.0f);
        }
        
        // Clamp values to 0-1 range
        temperature = UnityEngine.Mathf.Clamp01((float)temperature);
        rainfall = UnityEngine.Mathf.Clamp01((float)rainfall);
        
        // EXACT Beta 1.7.3 algorithm from ColorizerGrass.java:
        // var2 *= var0; (rainfall *= temperature)
        // int var4 = (int)((1.0D - var0) * 255.0D);  // X coordinate
        // int var5 = (int)((1.0D - var2) * 255.0D);  // Y coordinate
        // return grassBuffer[var5 << 8 | var4];
        rainfall *= temperature;
        
        int x = (int)((1.0 - temperature) * 255.0);
        int y = (int)((1.0 - rainfall) * 255.0);
        
        // Clamp to texture bounds
        x = UnityEngine.Mathf.Clamp(x, 0, 255);
        y = UnityEngine.Mathf.Clamp(y, 0, 255);
        
        // Sample from texture (GetPixel uses bottom-left origin, but we want top-left, so invert Y)
        UnityEngine.Color color = grassColorTexture.GetPixel(x, 255 - y);
        color.a = 1.0f; // Ensure full alpha
        
        // Convert the raw gamma color from the texture to linear space.
        // This is the correct conversion for projects using Linear color space.
        return color.linear;
    }
    
    /// <summary>
    /// Get foliage/leaves color based on temperature and rainfall.
    /// EXACT Beta 1.7.3 implementation using foliagecolor.png texture lookup.
    /// </summary>
    public static UnityEngine.Color GetFoliageColor(double temperature, double rainfall, UnityEngine.Texture2D foliageColorTexture) {
        // If no texture provided, return default foliage green
        if (foliageColorTexture == null)
        {
            return new UnityEngine.Color(0.4f, 0.7f, 0.3f, 1.0f);
        }
        
        // Clamp values to 0-1 range
        temperature = UnityEngine.Mathf.Clamp01((float)temperature);
        rainfall = UnityEngine.Mathf.Clamp01((float)rainfall);
        
        // EXACT Beta 1.7.3 algorithm from ColorizerFoliage.java:
        // var2 *= var0; (rainfall *= temperature)
        // int var4 = (int)((1.0D - var0) * 255.0D);  // X coordinate
        // int var5 = (int)((1.0D - var2) * 255.0D);  // Y coordinate
        // return foliageBuffer[var5 << 8 | var4];
        rainfall *= temperature;
        
        int x = (int)((1.0 - temperature) * 255.0);
        int y = (int)((1.0 - rainfall) * 255.0);
        
        // Clamp to texture bounds
        x = UnityEngine.Mathf.Clamp(x, 0, 255);
        y = UnityEngine.Mathf.Clamp(y, 0, 255);
        
        // Sample from texture (GetPixel uses bottom-left origin, but we want top-left, so invert Y)
        UnityEngine.Color color = foliageColorTexture.GetPixel(x, 255 - y);
        color.a = 1.0f; // Ensure full alpha
        
        // Convert the raw gamma color from the texture to linear space.
        return color.linear;
    }
    
    /// <summary>
    /// Get water color based on temperature and rainfall.
    /// EXACT Beta 1.7.3 implementation using watercolor.png texture lookup.
    /// </summary>
    public static UnityEngine.Color GetWaterColor(double temperature, double rainfall, UnityEngine.Texture2D waterColorTexture) {
        // If no texture provided, return default water blue
        if (waterColorTexture == null)
        {
            return new UnityEngine.Color(0.247f, 0.463f, 0.894f, 1.0f); // Default Minecraft water color
        }
        
        // Clamp values to 0-1 range
        temperature = UnityEngine.Mathf.Clamp01((float)temperature);
        rainfall = UnityEngine.Mathf.Clamp01((float)rainfall);
        
        // EXACT Beta 1.7.3 algorithm from ColorizerWater.java:
        // var2 *= var0; (rainfall *= temperature)
        // int var4 = (int)((1.0D - var0) * 255.0D);  // X coordinate
        // int var5 = (int)((1.0D - var2) * 255.0D);  // Y coordinate
        // return waterBuffer[var5 << 8 | var4];
        rainfall *= temperature;
        
        int x = (int)((1.0 - temperature) * 255.0);
        int y = (int)((1.0 - rainfall) * 255.0);
        
        // Clamp to texture bounds
        x = UnityEngine.Mathf.Clamp(x, 0, 255);
        y = UnityEngine.Mathf.Clamp(y, 0, 255);
        
        // Sample from texture (GetPixel uses bottom-left origin, but we want top-left, so invert Y)
        UnityEngine.Color color = waterColorTexture.GetPixel(x, 255 - y);
        color.a = 1.0f; // Ensure full alpha
        
        // Convert the raw gamma color from the texture to linear space.
        return color.linear;
    }
    
    /// <summary>
    /// Check if a block type should be tinted with grass color.
    /// </summary>
    public static bool IsGrassTintedBlock(byte blockID) {
        return blockID == 2  // Grass block (ID 2)
            || blockID == 31; // Tall grass (ID 31)
    }
    
    /// <summary>
    /// Check if a block type should be tinted with foliage color.
    /// </summary>
    public static bool IsFoliageTintedBlock(byte blockID) {
        return blockID == 18; // Leaves (ID 18)
    }
    
    /// <summary>
    /// Check if a block type should be tinted with water color.
    /// </summary>
    public static bool IsWaterTintedBlock(byte blockID) {
        return blockID == 8  // Flowing water (ID 8)
            || blockID == 9; // Still water (ID 9)
    }
}