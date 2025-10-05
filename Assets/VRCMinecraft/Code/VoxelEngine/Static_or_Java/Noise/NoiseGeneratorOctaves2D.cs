public class NoiseGeneratorOctaves2D {

    private readonly NoiseGenerator2D[] noiseGenerators;
    private readonly int octaves;

    public NoiseGeneratorOctaves2D(JavaRandom rand, int i) {
        octaves = i;
        noiseGenerators = new NoiseGenerator2D[i];
        for(int j = 0; j < i; j++) {
            noiseGenerators[j] = new NoiseGenerator2D(rand);
        }

    }

    public double[] generateNoiseArray(double[] array, double x, double z, int xSize, int zSize,
            double gridX, double gridZ, double fq) {
        return generateNoiseArray(array, x, z, xSize, zSize, gridX, gridZ, fq, 0.5D);
    }

    public double[] generateNoiseArray(double[] array, double xPos, double zPos, int xSize, int zSize,
            double gridX, double gridZ, double fq, double persistance) {
        // Optimized: Pre-calculate divided values
        double gridXDiv = gridX / 1.5D;
        double gridZDiv = gridZ / 1.5D;
        int totalSize = xSize * zSize;
        
        if(array == null || array.Length < totalSize) {
            array = new double[totalSize];
        } else {
            // Optimized: Use System.Array.Clear for faster zeroing
            System.Array.Clear(array, 0, array.Length);
        }
        
        double amplitude = 1.0D;
        double frequency = 1.0D;
        NoiseGenerator2D[] gens = noiseGenerators;
        int octCount = octaves;
        
        for(int l = 0; l < octCount; l++) {
            gens[l].generateNoiseArray(array, xPos, zPos, xSize, zSize, gridXDiv * frequency, gridZDiv * frequency, 0.55D / amplitude);
            frequency *= fq;
            amplitude *= persistance;
        }

        return array;
    }
}