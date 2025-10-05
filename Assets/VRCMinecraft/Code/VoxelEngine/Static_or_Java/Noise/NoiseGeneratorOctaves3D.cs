public class NoiseGeneratorOctaves3D
{
    public readonly NoiseGenerator3dPerlin[] generatorCollection; // OPTIMIZED: Public for octave-by-octave generation
    private readonly int octaves;

    // Converted from Java to C#
    public NoiseGeneratorOctaves3D(JavaRandom random, int i)
    {
        octaves = i;
        generatorCollection = new NoiseGenerator3dPerlin[i];
        for(int j = 0; j < i; j++)
        {
            generatorCollection[j] = new NoiseGenerator3dPerlin(random);
        }
    }

    // Converted from Java to C#
    public double generateNoise(double d, double d1)
    {
        double d2 = 0.0D;
        double d3 = 1.0D;
        for(int i = 0; i < octaves; i++)
        {
            d2 += generatorCollection[i].generateNoise(d * d3, d1 * d3) / d3;
            d3 /= 2.0D;
        }
        return d2;
    }

    public double[] generateNoiseOctaves(double[] array, double x, double y, double z, int xSize, int ySize, int zSize, double gridX, double gridY, double gridZ)
    {
        int totalSize = xSize * ySize * zSize;
        if(array == null)
        {
            array = new double[totalSize];
        } else {
            // Optimized: Use System.Array.Clear for faster zeroing
            System.Array.Clear(array, 0, array.Length);
        }
        double frequency = 1.0D;
        NoiseGenerator3dPerlin[] gens = generatorCollection;
        int octCount = octaves;
        
        for(int i = 0; i < octCount; i++){
            gens[i].generateNoiseArray(array, x, y, z, xSize, ySize, zSize, gridX * frequency, gridY * frequency, gridZ * frequency, frequency);
            frequency *= 0.5D;
        }
        return array;
    }

    // Converted from Java to C#
    public double[] generateNoiseArray(double[] ad, int x, int z, int xSize, int zSize,
            double gridX, double gridZ, double d2) {
        return this.generateNoiseOctaves(ad, (double)x, 10.0D, (double)z, xSize, 1, zSize, gridX, 1.0D, gridZ);
    }
}