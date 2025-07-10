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
        //int treesRand = (int) ((noiseGen.generateNoise(x * 0.5D, z * 0.5D) / 8D + random.NextDouble() * 4D + 4D) / 3D);
        int treesRand = 0;

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
}