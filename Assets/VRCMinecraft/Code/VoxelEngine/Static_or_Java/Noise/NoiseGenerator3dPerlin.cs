using System;

public class NoiseGenerator3dPerlin
{
    private int[] permutations;

    public double xCoord;
    public double yCoord;
    public double zCoord;

    public NoiseGenerator3dPerlin(JavaRandom random)
    {
        permutations = new int[512];
        this.xCoord = random.NextDouble() * 256.0D;
        this.yCoord = random.NextDouble() * 256.0D;
        this.zCoord = random.NextDouble() * 256.0D;

        for (int i = 0; i < 256; i++)
        {
            permutations[i] = i;
        }

        for (int i = 0; i < 256; i++)
        {
            int k = random.NextInt(256 - i) + i;
            int l = permutations[i];
            permutations[i] = permutations[k];
            permutations[k] = l;
            permutations[i + 256] = permutations[i];
        }
    }

    private static int floor_and_clamp_int(double val)
    {
        val = Math.Floor(val);
        if (val < int.MinValue) return int.MinValue;
        if (val > int.MaxValue) return int.MaxValue;
        return (int)val;
    }

    public static double lerp(double t, double a, double b)
    {
        return a + t * (b - a);
    }
    
    // This is the original grad function from NoiseGeneratorPerlin.java
    // It's used for 3D noise and, confusingly, for parts of the "optimized" 2D noise.
    public double grad(int hash, double x, double y, double z)
    {
        int h = hash & 15;
        double u = h < 8 ? x : y;
        double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    // This is a new function added to match the original grad2d from NoiseGeneratorPerlin.java
    // It was missing from your implementation and is used for one specific calculation
    // in the optimized 2D noise generation path.
    public double grad2d(int hash, double x, double z)
    {
        int h = hash & 15;
        double u = (1 - ((h & 8) >> 3)) * x;
        double v = h < 4 ? 0.0D : (h != 12 && h != 14 ? z : x);
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    public double generateNoise(double xPos, double yPos, double zPos)
    {
        double x = xPos + this.xCoord;
        double y = yPos + this.yCoord;
        double z = zPos + this.zCoord;

        int intX = floor_and_clamp_int(x);
        int intY = floor_and_clamp_int(y);
        int intZ = floor_and_clamp_int(z);

        int p1 = intX & 255;
        int p2 = intY & 255;
        int p3 = intZ & 255;

        x -= intX;
        y -= intY;
        z -= intZ;

        double fx = x * x * x * (x * (x * 6.0D - 15.0D) + 10.0D);
        double fy = y * y * y * (y * (y * 6.0D - 15.0D) + 10.0D);
        double fz = z * z * z * (z * (z * 6.0D - 15.0D) + 10.0D);

        int a1 = permutations[p1] + p2;
        int a2 = permutations[a1] + p3;
        int a3 = permutations[a1 + 1] + p3;
        int a4 = permutations[p1 + 1] + p2;
        int a5 = permutations[a4] + p3;
        int a6 = permutations[a4 + 1] + p3;

        return lerp(fz, lerp(fy,
                lerp(fx, grad(permutations[a2], x, y, z),
                        grad(permutations[a5], x - 1.0D, y, z)),
                lerp(fx, grad(permutations[a3], x, y - 1.0D, z),
                        grad(permutations[a6], x - 1.0D, y - 1.0D, z))),
                lerp(fy,
                        lerp(fx, grad(permutations[a2 + 1], x, y, z - 1.0D),
                                grad(permutations[a5 + 1], x - 1.0D, y, z - 1.0D)),
                        lerp(fx, grad(permutations[a3 + 1], x, y - 1.0D, z - 1.0D),
                                grad(permutations[a6 + 1], x - 1.0D, y - 1.0D, z - 1.0D))));
    }

    public double generateNoise(double d, double d1)
    {
        return generateNoise(d, d1, 0.0D);
    }
    
    // This method has been significantly corrected to match the original Java implementation.

    public void generateNoiseArray(double[] array, double xPos, double yPos, double zPos, int xSize,
            int ySize, int zSize, double gridX, double gridY, double gridZ, double amplitudeFactor)
    {
        double amplitude = 1.0D / amplitudeFactor;
        
        // This is the optimized path for 2D noise generation (e.g., for terrain heightmaps).
        // Your original C# code used the wrong gradient functions here.
        if (ySize == 1)
        {
            int index = 0;
            for (int dx = 0; dx < xSize; dx++)
            {
                double x = (xPos + (double)dx) * gridX + this.xCoord;
                int intX = floor_and_clamp_int(x);
                int p1 = intX & 0xff;
                double relX = x - intX;
                double fx = relX * relX * relX * (relX * (relX * 6D - 15D) + 10D);

                for (int dz = 0; dz < zSize; dz++)
                {
                    double z = (zPos + (double)dz) * gridZ + this.zCoord;
                    int intZ = floor_and_clamp_int(z);
                    int p3 = intZ & 0xff;
                    double relZ = z - intZ;
                    double fz = relZ * relZ * relZ * (relZ * (relZ * 6D - 15D) + 10D);

                    // DISCREPANCY FIX: The original Java code uses a bizarre mix of grad2d and grad (the 3D version).
                    // This has been corrected to match the original logic exactly.
                    // Your C# code was incorrectly using a different grad2d function for all of these.
                    
                    int a1 = permutations[p1] + 0; // y is 0
                    int a2 = permutations[a1] + p3;
                    
                    int b1 = permutations[p1 + 1] + 0; // y is 0
                    int b2 = permutations[b1] + p3;

                    // The original code uses grad2d for the first point...
                    double val1 = lerp(fx, 
                        grad2d(permutations[a2], relX, relZ), 
                        grad(permutations[b2], relX - 1.0D, 0.0D, relZ));
                    
                    // ...and grad (the 3D version with y=0) for the second point.
                    double val2 = lerp(fx, 
                        grad(permutations[a2 + 1], relX, 0.0D, relZ - 1.0D), 
                        grad(permutations[b2 + 1], relX - 1.0D, 0.0D, relZ - 1.0D));

                    double value = lerp(fz, val1, val2);
                    array[index++] += value * amplitude;
                }
            }
            return;
        }

        // This is the full 3D noise generation path. Your original C# code for this part was mostly correct,
        // but it has been cleaned up for clarity and to ensure it matches the Java logic precisely.
        int arrayIndex = 0;
        int lastIntY = -1;
        double d13 = 0.0D, d15 = 0.0D, d16 = 0.0D, d18 = 0.0D;

        for (int dx = 0; dx < xSize; dx++)
        {
            double x = (xPos + (double)dx) * gridX + this.xCoord;
            int intX = floor_and_clamp_int(x);
            int p1 = intX & 0xff;
            double relX = x - intX;
            double fx = relX * relX * relX * (relX * (relX * 6D - 15D) + 10D);

            for (int dz = 0; dz < zSize; dz++)
            {
                double z = (zPos + (double)dz) * gridZ + this.zCoord;
                int intZ = floor_and_clamp_int(z);
                int p3 = intZ & 0xff;
                double relZ = z - intZ;
                double fz = relZ * relZ * relZ * (relZ * (relZ * 6D - 15D) + 10D);

                for (int dy = 0; dy < ySize; dy++)
                {
                    double y = (yPos + (double)dy) * gridY + this.yCoord;
                    int intY = floor_and_clamp_int(y);
                    int p2 = intY & 0xff;
                    double relY = y - intY;
                    double fy = relY * relY * relY * (relY * (relY * 6D - 15D) + 10D);

                    if (dy == 0 || p2 != lastIntY)
                    {
                        lastIntY = p2;
                        int a1 = permutations[p1] + p2;
                        int a2 = permutations[a1] + p3;
                        int a3 = permutations[a1 + 1] + p3;
                        int b1 = permutations[p1 + 1] + p2;
                        int b2 = permutations[b1] + p3;
                        int b3 = permutations[b1 + 1] + p3;

                        d13 = lerp(fx, grad(permutations[a2], relX, relY, relZ), grad(permutations[b2], relX - 1.0D, relY, relZ));
                        d15 = lerp(fx, grad(permutations[a3], relX, relY - 1.0D, relZ), grad(permutations[b3], relX - 1.0D, relY - 1.0D, relZ));
                        d16 = lerp(fx, grad(permutations[a2 + 1], relX, relY, relZ - 1.0D), grad(permutations[b2 + 1], relX - 1.0D, relY, relZ - 1.0D));
                        d18 = lerp(fx, grad(permutations[a3 + 1], relX, relY - 1.0D, relZ - 1.0D), grad(permutations[b3 + 1], relX - 1.0D, relY - 1.0D, relZ - 1.0D));
                    }

                    double val1 = lerp(fy, d13, d15);
                    double val2 = lerp(fy, d16, d18);
                    double value = lerp(fz, val1, val2);
                    array[arrayIndex++] += value * amplitude;
                }
            }
        }
    }
}
