public class WorldChunkManagerOld {
    public double[] temperatures;
    public double[] rainfall;
    public double[] modifierNoise;

    private readonly NoiseGeneratorOctaves2D tempNoiseGen;
    private readonly NoiseGeneratorOctaves2D rainNoiseGen;
    private readonly NoiseGeneratorOctaves2D modifierNoiseGen;
    
    private readonly BiomeOld biomeProvider;

    public WorldChunkManagerOld(long seed) {
        tempNoiseGen = new NoiseGeneratorOctaves2D(new JavaRandom(seed * 9871L), 4);
        rainNoiseGen = new NoiseGeneratorOctaves2D(new JavaRandom(seed * 39811L), 4);
        modifierNoiseGen = new NoiseGeneratorOctaves2D(new JavaRandom(seed * 543321L), 2);
        
        biomeProvider = new BiomeOld();
    }

    public BetaBiomeEnum[] getBiomeBlock(BetaBiomeEnum[] biomes, int x, int z, int xSize, int zSize) {
        int totalSize = xSize * zSize;
        if(biomes == null || biomes.Length < totalSize) {
            biomes = new BetaBiomeEnum[totalSize];
        }

        temperatures = tempNoiseGen.generateNoiseArray(temperatures, x, z, xSize, xSize, 0.02500000037252903D, 0.02500000037252903D, 0.25D, 0.5D);
        rainfall = rainNoiseGen.generateNoiseArray(rainfall, x, z, xSize, xSize, 0.05000000074505806D, 0.05000000074505806D, 0.33333333333333331D, 0.5D);
        modifierNoise = modifierNoiseGen.generateNoiseArray(modifierNoise, x, z, xSize, xSize, 0.25D, 0.25D, 0.58823529411764708D, 0.5D);
        
        double[] temps = temperatures;
        double[] rains = rainfall;
        double[] mods = modifierNoise;
        
        int index = 0;
        for(int blockX = 0; blockX < xSize; blockX++) {
            for(int blockZ = 0; blockZ < zSize; blockZ++) {
                double modifier = mods[index] * 1.1D + 0.5D;
                
                double temp_d1 = 0.01D;
                double temp_d2 = 1.0D - temp_d1;
                double finalTemp = (temps[index] * 0.15D + 0.7D) * temp_d2 + modifier * temp_d1;

                double rain_d1 = 0.002D;
                double rain_d2 = 1.0D - rain_d1;
                double finalRain = (rains[index] * 0.15D + 0.5D) * rain_d2 + modifier * rain_d1;

                finalTemp = 1.0D - (1.0D - finalTemp) * (1.0D - finalTemp);

                if(finalTemp < 0.0D) finalTemp = 0.0D;
                if(finalRain < 0.0D) finalRain = 0.0D;
                if(finalTemp > 1.0D) finalTemp = 1.0D;
                if(finalRain > 1.0D) finalRain = 1.0D;
                
                temps[index] = finalTemp;
                rains[index] = finalRain;
                
                biomes[index] = biomeProvider.getBiomeFromLookup(finalTemp, finalRain);
                
                index++;
            }
        }
        return biomes;
    }
}
