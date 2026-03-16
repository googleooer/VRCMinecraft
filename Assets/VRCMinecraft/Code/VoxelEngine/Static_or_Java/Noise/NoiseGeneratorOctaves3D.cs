public class NoiseGeneratorOctaves3D
{
    private readonly NoiseGenerator3dPerlin noiseGen0;
    private readonly NoiseGenerator3dPerlin noiseGen1;
    private readonly NoiseGenerator3dPerlin noiseGen2;
    private readonly NoiseGenerator3dPerlin noiseGen3;
    private readonly NoiseGenerator3dPerlin noiseGen4;
    private readonly NoiseGenerator3dPerlin noiseGen5;
    private readonly NoiseGenerator3dPerlin noiseGen6;
    private readonly NoiseGenerator3dPerlin noiseGen7;
    private readonly NoiseGenerator3dPerlin noiseGen8;
    private readonly NoiseGenerator3dPerlin noiseGen9;
    private readonly NoiseGenerator3dPerlin noiseGen10;
    private readonly NoiseGenerator3dPerlin noiseGen11;
    private readonly NoiseGenerator3dPerlin noiseGen12;
    private readonly NoiseGenerator3dPerlin noiseGen13;
    private readonly NoiseGenerator3dPerlin noiseGen14;
    private readonly NoiseGenerator3dPerlin noiseGen15;
    private readonly int octaves;

    // Converted from Java to C#
    public NoiseGeneratorOctaves3D(JavaRandom random, int i)
    {
        octaves = i;
        if (i > 0) noiseGen0 = new NoiseGenerator3dPerlin(random);
        if (i > 1) noiseGen1 = new NoiseGenerator3dPerlin(random);
        if (i > 2) noiseGen2 = new NoiseGenerator3dPerlin(random);
        if (i > 3) noiseGen3 = new NoiseGenerator3dPerlin(random);
        if (i > 4) noiseGen4 = new NoiseGenerator3dPerlin(random);
        if (i > 5) noiseGen5 = new NoiseGenerator3dPerlin(random);
        if (i > 6) noiseGen6 = new NoiseGenerator3dPerlin(random);
        if (i > 7) noiseGen7 = new NoiseGenerator3dPerlin(random);
        if (i > 8) noiseGen8 = new NoiseGenerator3dPerlin(random);
        if (i > 9) noiseGen9 = new NoiseGenerator3dPerlin(random);
        if (i > 10) noiseGen10 = new NoiseGenerator3dPerlin(random);
        if (i > 11) noiseGen11 = new NoiseGenerator3dPerlin(random);
        if (i > 12) noiseGen12 = new NoiseGenerator3dPerlin(random);
        if (i > 13) noiseGen13 = new NoiseGenerator3dPerlin(random);
        if (i > 14) noiseGen14 = new NoiseGenerator3dPerlin(random);
        if (i > 15) noiseGen15 = new NoiseGenerator3dPerlin(random);
    }

    public NoiseGenerator3dPerlin GetGenerator(int index)
    {
        switch (index)
        {
            case 0: return noiseGen0;
            case 1: return noiseGen1;
            case 2: return noiseGen2;
            case 3: return noiseGen3;
            case 4: return noiseGen4;
            case 5: return noiseGen5;
            case 6: return noiseGen6;
            case 7: return noiseGen7;
            case 8: return noiseGen8;
            case 9: return noiseGen9;
            case 10: return noiseGen10;
            case 11: return noiseGen11;
            case 12: return noiseGen12;
            case 13: return noiseGen13;
            case 14: return noiseGen14;
            case 15: return noiseGen15;
        }
        return null;
    }

    // Converted from Java to C#
    public double generateNoise(double d, double d1)
    {
        double d2 = 0.0D;
        double d3 = 1.0D;
        for(int i = 0; i < octaves; i++)
        {
            NoiseGenerator3dPerlin generator = GetGenerator(i);
            if (generator == null) break;
            d2 += generator.generateNoise(d * d3, d1 * d3) / d3;
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
        
        for(int i = 0; i < octaves; i++){
            NoiseGenerator3dPerlin generator = GetGenerator(i);
            if (generator == null) break;
            generator.generateNoiseArray(array, x, y, z, xSize, ySize, zSize, gridX * frequency, gridY * frequency, gridZ * frequency, frequency);
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
