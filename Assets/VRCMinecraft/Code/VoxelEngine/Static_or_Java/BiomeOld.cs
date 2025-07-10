/// <summary>
/// This class is no longer static. Create an instance of it in your WorldChunkManager
/// to hold the biome lookup table, preventing it from being regenerated constantly.
/// </summary>
public class BiomeOld {

    // This table is now an instance field, generated only once in the constructor.
    private readonly BetaBiomeEnum[] biomeLookupTable;

    public BiomeOld()
    {
        // The lookup table is generated once when the object is created.
        this.biomeLookupTable = generateBiomeLookup();
    }

    private BetaBiomeEnum[] generateBiomeLookup() {
        BetaBiomeEnum[] biomelut = new BetaBiomeEnum[4096];
        for(int i = 0; i < 64; ++i) {
            for(int j = 0; j < 64; ++j) {
                biomelut[i + j * 64] = getBiome(i / 63.0f, j / 63.0f);
            }
        }
        return biomelut;
    }

    /// <summary>
    /// Gets the biome from the pre-calculated lookup table.
    /// </summary>
    public BetaBiomeEnum getBiomeFromLookup(double d, double d1) {
        int i = (int)(d * 63.0D);
        int j = (int)(d1 * 63.0D);
        
        // Clamp values to prevent ArrayOutOfBoundsException
        if (i < 0) i = 0;
        if (i > 63) i = 63;
        if (j < 0) j = 0;
        if (j > 63) j = 63;
        
        return biomeLookupTable[i + j * 64];
    }

    private static BetaBiomeEnum getBiome(float f, float f1) {
        f1 *= f;
        return f < 0.1F ? BetaBiomeEnum.TUNDRA
                : (f1 < 0.2F ? (f < 0.5F ? BetaBiomeEnum.TUNDRA : (f < 0.95F ? BetaBiomeEnum.SAVANNA : BetaBiomeEnum.DESERT))
                : (f1 > 0.5F && f < 0.7F ? BetaBiomeEnum.SWAMPLAND
                : (f < 0.5F ? BetaBiomeEnum.TAIGA : (f < 0.97F ? (f1 < 0.35F ? BetaBiomeEnum.SHRUBLAND : BetaBiomeEnum.FOREST)
                : (f1 < 0.45F ? BetaBiomeEnum.PLAINS : (f1 < 0.9F ? BetaBiomeEnum.SEASONAL_FOREST : BetaBiomeEnum.RAINFOREST))))));
    }

    // These methods can remain static as they don't depend on the lookup table instance.
    public static byte top(BetaBiomeEnum biome) {
        if(biome == BetaBiomeEnum.DESERT) {
            return (byte)BlockMaterial.SAND;
        }
        return (byte)BlockMaterial.GRASS;
    }
    
    public static byte filler(BetaBiomeEnum biome) {
        if(biome == BetaBiomeEnum.DESERT) {
            return (byte)BlockMaterial.SAND;
        }
        return (byte)BlockMaterial.DIRT;
    }
}
