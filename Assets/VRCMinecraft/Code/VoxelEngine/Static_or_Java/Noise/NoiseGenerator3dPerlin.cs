using System;

public class NoiseGenerator3dPerlin
{
    private int[] permutations;
    
    // Pre-computed gradient lookup tables to eliminate branching
    // Note: These are instance fields instead of static due to UdonSharp limitations
    private readonly double[] GRAD_X = new double[16] {1,  -1,  1, -1, 1, -1, 1, -1, 0,  0,  0,  0,  1,  0, -1,  0};
    private readonly double[] GRAD_Y = new double[16] {1,   1, -1, -1, 0,  0,  0,  0,  1, -1,  1, -1, 1, -1,  1, -1};
    private readonly double[] GRAD_Z = new double[16] {0,   0,  0,  0,  1,  1, -1, -1, 1,  1, -1, -1, 0,  1,  0, -1};

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
    
    // Optimized: Use pre-computed gradient table for maximum performance
    public double grad(int hash, double x, double y, double z)
    {
        int h = hash & 15;
        return GRAD_X[h] * x + GRAD_Y[h] * y + GRAD_Z[h] * z;
    }

    public double grad2d(int hash, double x, double z)
    {
        int h = hash & 15;
        return GRAD_X[h] * x + GRAD_Z[h] * z;
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
    
    // Optimized: Cache fade function results, reduce array lookups
    public void generateNoiseArray(double[] array, double xPos, double yPos, double zPos, int xSize,
            int ySize, int zSize, double gridX, double gridY, double gridZ, double amplitudeFactor)
    {
        double amplitude = 1.0D / amplitudeFactor;
        
        // Optimized 2D noise path
        if (ySize == 1)
        {
            int index = 0;
            // Cache permutations locally for faster access
            int[] perm = this.permutations;
            double xc = this.xCoord;
            double zc = this.zCoord;
            
            for (int dx = 0; dx < xSize; dx++)
            {
                double x = (xPos + (double)dx) * gridX + xc;
                int intX = floor_and_clamp_int(x);
                int p1 = intX & 0xff;
                double relX = x - intX;
                // Cache fade function
                double fx = relX * relX * relX * (relX * (relX * 6D - 15D) + 10D);

                for (int dz = 0; dz < zSize; dz++)
                {
                    double z = (zPos + (double)dz) * gridZ + zc;
                    int intZ = floor_and_clamp_int(z);
                    int p3 = intZ & 0xff;
                    double relZ = z - intZ;
                    // Cache fade function
                    double fz = relZ * relZ * relZ * (relZ * (relZ * 6D - 15D) + 10D);

                    int a1 = perm[p1];
                    int a2 = perm[a1] + p3;
                    
                    int b1 = perm[p1 + 1];
                    int b2 = perm[b1] + p3;

                    // Optimized gradient lookups with pre-computed table
                    int h0 = perm[a2] & 15;
                    double g0 = GRAD_X[h0] * relX + GRAD_Z[h0] * relZ;
                    
                    int h1 = perm[b2] & 15;
                    double g1 = GRAD_X[h1] * (relX - 1.0D) + GRAD_Z[h1] * relZ;
                    
                    double val1 = g0 + fx * (g1 - g0);
                    
                    int h2 = perm[a2 + 1] & 15;
                    double g2 = GRAD_X[h2] * relX + GRAD_Z[h2] * (relZ - 1.0D);
                    
                    int h3 = perm[b2 + 1] & 15;
                    double g3 = GRAD_X[h3] * (relX - 1.0D) + GRAD_Z[h3] * (relZ - 1.0D);
                    
                    double val2 = g2 + fx * (g3 - g2);

                    double value = val1 + fz * (val2 - val1);
                    array[index++] += value * amplitude;
                }
            }
            return;
        }

        // Optimized 3D noise path
        int arrayIndex = 0;
        int lastIntY = -1;
        double d13 = 0.0D, d15 = 0.0D, d16 = 0.0D, d18 = 0.0D;
        
        // Cache permutations and coords locally
        int[] perm3d = this.permutations;
        double xCoord3d = this.xCoord;
        double yCoord3d = this.yCoord;
        double zCoord3d = this.zCoord;

        for (int dx = 0; dx < xSize; dx++)
        {
            double x = (xPos + (double)dx) * gridX + xCoord3d;
            int intX = floor_and_clamp_int(x);
            int p1 = intX & 0xff;
            double relX = x - intX;
            // Cache fade function
            double fx = relX * relX * relX * (relX * (relX * 6D - 15D) + 10D);

            for (int dz = 0; dz < zSize; dz++)
            {
                double z = (zPos + (double)dz) * gridZ + zCoord3d;
                int intZ = floor_and_clamp_int(z);
                int p3 = intZ & 0xff;
                double relZ = z - intZ;
                // Cache fade function
                double fz = relZ * relZ * relZ * (relZ * (relZ * 6D - 15D) + 10D);

                for (int dy = 0; dy < ySize; dy++)
                {
                    double y = (yPos + (double)dy) * gridY + yCoord3d;
                    int intY = floor_and_clamp_int(y);
                    int p2 = intY & 0xff;
                    double relY = y - intY;
                    // Cache fade function
                    double fy = relY * relY * relY * (relY * (relY * 6D - 15D) + 10D);

                    if (dy == 0 || p2 != lastIntY)
                    {
                        lastIntY = p2;
                        int a1 = perm3d[p1] + p2;
                        int a2 = perm3d[a1] + p3;
                        int a3 = perm3d[a1 + 1] + p3;
                        int b1 = perm3d[p1 + 1] + p2;
                        int b2 = perm3d[b1] + p3;
                        int b3 = perm3d[b1 + 1] + p3;

                        // Optimized gradient lookups with pre-computed table
                        int h0 = perm3d[a2] & 15;
                        double g0 = GRAD_X[h0] * relX + GRAD_Y[h0] * relY + GRAD_Z[h0] * relZ;
                        
                        int h1 = perm3d[b2] & 15;
                        double g1 = GRAD_X[h1] * (relX - 1.0D) + GRAD_Y[h1] * relY + GRAD_Z[h1] * relZ;
                        
                        d13 = g0 + fx * (g1 - g0);
                        
                        int h2 = perm3d[a3] & 15;
                        double g2 = GRAD_X[h2] * relX + GRAD_Y[h2] * (relY - 1.0D) + GRAD_Z[h2] * relZ;
                        
                        int h3 = perm3d[b3] & 15;
                        double g3 = GRAD_X[h3] * (relX - 1.0D) + GRAD_Y[h3] * (relY - 1.0D) + GRAD_Z[h3] * relZ;
                        
                        d15 = g2 + fx * (g3 - g2);
                        
                        int h4 = perm3d[a2 + 1] & 15;
                        double g4 = GRAD_X[h4] * relX + GRAD_Y[h4] * relY + GRAD_Z[h4] * (relZ - 1.0D);
                        
                        int h5 = perm3d[b2 + 1] & 15;
                        double g5 = GRAD_X[h5] * (relX - 1.0D) + GRAD_Y[h5] * relY + GRAD_Z[h5] * (relZ - 1.0D);
                        
                        d16 = g4 + fx * (g5 - g4);
                        
                        int h6 = perm3d[a3 + 1] & 15;
                        double g6 = GRAD_X[h6] * relX + GRAD_Y[h6] * (relY - 1.0D) + GRAD_Z[h6] * (relZ - 1.0D);
                        
                        int h7 = perm3d[b3 + 1] & 15;
                        double g7 = GRAD_X[h7] * (relX - 1.0D) + GRAD_Y[h7] * (relY - 1.0D) + GRAD_Z[h7] * (relZ - 1.0D);
                        
                        d18 = g6 + fx * (g7 - g6);
                    }

                    // Inline lerp to eliminate method calls
                    double val1 = d13 + fy * (d15 - d13);
                    double val2 = d16 + fy * (d18 - d16);
                    double value = val1 + fz * (val2 - val1);
                    array[arrayIndex++] += value * amplitude;
                }
            }
        }
    }
}
