/*
 * JavaRandom.cs
 * This class is a direct C# port of java.util.Random's LCG.
 * It is kept in its own file to be used by other standard C# utility scripts.
 * This script does NOT use UdonSharp.
 */

using UnityEngine;

public class JavaRandom
{
    private long seed;
    private const long multiplier = 0x5DEECE66DL;
    private const long addend = 0xBL;
    private const long mask = (1L << 48) - 1;

    public JavaRandom(long seed)
    {
        SetSeed(seed);
    }

    public void SetSeed(long seed)
    {
        this.seed = (seed ^ multiplier) & mask;
    }

    private int Next(int bits)
    {
        seed = (seed * multiplier + addend) & mask;
        return (int)((ulong)seed >> (48 - bits));
    }

    public int NextInt(int n)
    {
        if (n <= 0)
            Debug.LogError("n must be positive");

        if ((n & -n) == n) // i.e., n is a power of 2
            return (int)((n * (long)Next(31)) >> 31);

        int bits, val;
        do
        {
            bits = Next(31);
            val = bits % n;
        } while (bits - val + (n - 1) < 0);
        return val;
    }
}
