using System;
using UnityEngine;

public class NoiseGenerator2D {
    private readonly double const1 = 0.5D * (Math.Sqrt(3.0D) - 1.0D);
    private readonly double const2 = (3.0D - Math.Sqrt(3.0D)) / 6.0D;
    private readonly int[] gradientTable;
    
    public double randomDX;
    public double randomDY;
    public double randomDZ;
    
    public int[] permutations;

    private static int wrap(double d) {
        return d > 0.0D ? (int)d : (int)d - 1;
    }

    private double dotGradient(int gradientIndex, double d, double d1) {
        int offset = gradientIndex * 2;
        return (double)gradientTable[offset] * d + (double)gradientTable[offset + 1] * d1;
    }
    public NoiseGenerator2D() : this(new JavaRandom()) {
    }

    public NoiseGenerator2D(JavaRandom random) {
        gradientTable = new int[]
        {
            1, 1, -1, 1, 1, -1, -1, -1,
            1, 0, -1, 0, 1, 0, -1, 0,
            0, 1, 0, -1, 0, 1, 0, -1
        };
        permutations = new int[512];
        this.randomDX = random.NextDouble() * 256.0D;
        this.randomDY = random.NextDouble() * 256.0D;
        this.randomDZ = random.NextDouble() * 256.0D;

        for(int i = 0; i < 256; i++) {
            permutations[i] = i;
        }

        for(int i = 0; i < 256; i++) {
            int k = random.NextInt(256 - i) + i;
            int l = permutations[i];
            permutations[i] = permutations[k];
            permutations[k] = l;
            permutations[i + 256] = permutations[i];
        }
    }

    public void generateNoiseArray(double[] array, double xPos, double zPos, int xSize, int zSize,
            double gridX, double gridZ, double amplitude) {
        int k = 0;
        int[] perm = this.permutations;
        double c1 = this.const1;
        double c2 = this.const2;
        double const2x2 = 2D * c2;
        double rDX = this.randomDX;
        double rDY = this.randomDY;
        
        for(int x = 0; x < xSize; x++) {
            double cx = (xPos + (double)x) * gridX + rDX;
            for(int z = 0; z < zSize; z++) {
                double cz = (zPos + (double)z) * gridZ + rDY;
                
                double d10 = (cx + cz) * c1;
                int j1 = wrap(cx + d10);
                int k1 = wrap(cz + d10);
                double d11 = (double)(j1 + k1) * c2;
                double d12 = (double)j1 - d11;
                double d13 = (double)k1 - d11;
                double d14 = cx - d12;
                double d15 = cz - d13;
                byte l1;
                byte i2;
                if(d14 > d15) {
                    l1 = 1;
                    i2 = 0;
                } else {
                    l1 = 0;
                    i2 = 1;
                }
                double d16 = (d14 - (double)l1) + c2;
                double d17 = (d15 - (double)i2) + c2;
                double d18 = (d14 - 1.0D) + const2x2;
                double d19 = (d15 - 1.0D) + const2x2;
                int j2 = j1 & 0xff;
                int k2 = k1 & 0xff;
                int l2 = perm[j2 + perm[k2]] % 12;
                int i3 = perm[j2 + l1 + perm[k2 + i2]] % 12;
                int j3 = perm[j2 + 1 + perm[k2 + 1]] % 12;
                double d20 = 0.5D - d14 * d14 - d15 * d15;
                double d7;
                if(d20 < 0.0D) {
                    d7 = 0.0D;
                } else {
                    d20 *= d20;
                    d7 = d20 * d20 * dotGradient(l2, d14, d15);
                }
                double d21 = 0.5D - d16 * d16 - d17 * d17;
                double d8;
                if(d21 < 0.0D) {
                    d8 = 0.0D;
                } else {
                    d21 *= d21;
                    d8 = d21 * d21 * dotGradient(i3, d16, d17);
                }
                double d22 = 0.5D - d18 * d18 - d19 * d19;
                double d9;
                if(d22 < 0.0D) {
                    d9 = 0.0D;
                } else {
                    d22 *= d22;
                    d9 = d22 * d22 * dotGradient(j3, d18, d19);
                }
                array[k++] += 70.0D * (d7 + d8 + d9) * amplitude;
            }
        }
    }
}
