/*
 * JavaRandom.cs
 * This class is a direct C# port of java.util.Random's LCG.
 * It is kept in its own file to be used by other standard C# utility scripts.
 * This script does NOT use UdonSharp.
 *
 * CORRECTED VERSION:
 * - NextDouble() now uses division instead of multiplication by a pre-calculated
 * double to guarantee bit-for-bit floating point equivalence with Java.
 * - NextInt(int bound) has been implemented with the full, correct logic from
 * java.util.Random to handle non-power-of-two bounds without introducing bias,
 * which is critical for feature placement.
 */

using System;
using UnityEngine;

public class JavaRandom
{
    private long seed;
    private const long multiplier = 0x5DEECE66DL;
    private const long addend = 0xBL;
    private const long mask = (1L << 48) - 1;

    public JavaRandom()
    {
        // This constructor is not recommended for terrain generation as it's non-deterministic.
        // It's provided for completeness.
        SetSeed(SeedUniquifier() ^ (long)(Time.realtimeSinceStartup * 1000000000));
    }

    public JavaRandom(long seed)
    {
        SetSeed(seed);
    }

    private long seedUniquifier = 8682522807148012L;
    private long SeedUniquifier()
    {
        // L'Ecuyer, "Tables of Linear Congruential Generators of
        // Different Sizes and Good Lattice Structure", 1999
        long current = seedUniquifier;
        long next = current * 181783497276652981L;
        seedUniquifier = next;
        return next;
    }

    public void SetSeed(long seed)
    {
        this.seed = (seed ^ multiplier) & mask;
    }

    protected int Next(int bits)
    {
        seed = (seed * multiplier + addend) & mask;
        ulong clamped = (ulong)seed >> (48 - bits);
        return clamped > int.MaxValue ? int.MaxValue : (int)clamped;
    }

    public int NextInt()
    {
        return Next(32);
    }

    /// <summary>
    /// Returns a pseudorandom, uniformly distributed int value between 0 (inclusive) 
    /// and the specified value (exclusive), drawn from this random number generator's sequence.
    /// </summary>
    /// <param name="bound">The exclusive upper bound.</param>
    /// <returns>The random integer.</returns>
    public int NextInt(int bound)
    {
        if (bound <= 0)
        {
            // In a real-world scenario, you'd throw an ArgumentOutOfRangeException.
            // In Udon, logging an error is safer.
            Debug.LogError("bound must be positive");
            return 0;
        }

        // This logic is a direct port of java.util.Random.nextInt(int)
        // It correctly handles non-powers-of-two by rejecting values
        // that would lead to a non-uniform distribution.
        if ((bound & -bound) == bound)  // i.e., bound is a power of 2
        {
            return (int)((bound * (long)Next(31)) >> 31);
        }

        int bits, val;
        do
        {
            bits = Next(31);
            val = bits % bound;
        } while (bits - val + (bound - 1) < 0);

        return val;
    }

    public long NextLong()
    {
        return ((long)Next(32) << 32) + Next(32);
    }

    public bool NextBoolean()
    {
        return Next(1) != 0;
    }

    public float NextFloat()
    {
        return Next(24) / ((float)(1 << 24));
    }

    /// <summary>
    /// Returns the next pseudorandom, uniformly distributed double value 
    /// between 0.0 (inclusive) and 1.0 (exclusive). This is a direct port
    /// of the Java implementation to ensure identical floating point results.
    /// </summary>
    public double NextDouble()
    {
        return (((long)Next(26) << 27) + Next(27)) / (double)(1L << 53);
    }
}
