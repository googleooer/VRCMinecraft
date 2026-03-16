public class NoiseGeneratorOctaves2D {

    private readonly NoiseGenerator2D noiseGen0;
    private readonly NoiseGenerator2D noiseGen1;
    private readonly NoiseGenerator2D noiseGen2;
    private readonly NoiseGenerator2D noiseGen3;
    private readonly int octaves;

    public NoiseGeneratorOctaves2D(JavaRandom rand, int i) {
        octaves = i;
        if (i > 0) noiseGen0 = new NoiseGenerator2D(rand);
        if (i > 1) noiseGen1 = new NoiseGenerator2D(rand);
        if (i > 2) noiseGen2 = new NoiseGenerator2D(rand);
        if (i > 3) noiseGen3 = new NoiseGenerator2D(rand);
    }

    public int GetOctaveCount()
    {
        return octaves;
    }

    public NoiseGenerator2D GetGenerator(int index)
    {
        if (index == 0) return noiseGen0;
        if (index == 1) return noiseGen1;
        if (index == 2) return noiseGen2;
        if (index == 3) return noiseGen3;
        return null;
    }

    public double[] generateNoiseArray(double[] array, double x, double z, int xSize, int zSize,
            double gridX, double gridZ, double fq) {
        return generateNoiseArray(array, x, z, xSize, zSize, gridX, gridZ, fq, 0.5D);
    }

    public double[] generateNoiseArray(double[] array, double xPos, double zPos, int xSize, int zSize,
            double gridX, double gridZ, double fq, double persistance) {
        double gridXDiv = gridX / 1.5D;
        double gridZDiv = gridZ / 1.5D;
        int totalSize = xSize * zSize;
        
        if(array == null || array.Length < totalSize) {
            array = new double[totalSize];
        } else {
            System.Array.Clear(array, 0, array.Length);
        }
        
        double amplitude = 1.0D;
        double frequency = 1.0D;

        for(int l = 0; l < octaves; l++) {
            NoiseGenerator2D generator = null;
            if (l == 0) generator = noiseGen0;
            else if (l == 1) generator = noiseGen1;
            else if (l == 2) generator = noiseGen2;
            else if (l == 3) generator = noiseGen3;

            if (generator == null) break;

            generator.generateNoiseArray(array, xPos, zPos, xSize, zSize, gridXDiv * frequency, gridZDiv * frequency, 0.55D / amplitude);
            frequency *= fq;
            amplitude *= persistance;
        }

        return array;
    }
}
