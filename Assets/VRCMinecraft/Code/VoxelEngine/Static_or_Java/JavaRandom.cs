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

    // OpenJDK uses a process-wide static AtomicLong here, but UdonSharp does not support
    // static fields on user-defined types. We keep it as an instance field — for this
    // project the parameterless `new JavaRandom()` path is never used by worldgen
    // (worldgen always seeds explicitly with `new JavaRandom(long)`), so the practical
    // impact of the missing global counter is nil.
    private long seedUniquifier = 8682522807148012L;
    private long SeedUniquifier()
    {
        // L'Ecuyer, "Tables of Linear Congruential Generators of
        // Different Sizes and Good Lattice Structure", 1999. Correct constant is
        // 1181783497276652981L (19 digits). Previous code dropped the leading 1.
        long next = seedUniquifier * 1181783497276652981L;
        seedUniquifier = next;
        return next;
    }

    public void SetSeed(long seed)
    {
        this.seed = (seed ^ multiplier) & mask;
    }

    protected int Next(int bits)
    {
        // Java spec: this.seed = (this.seed * 0x5DEECE66DL + 0xBL) & ((1L << 48) - 1);
        //            return (int)(this.seed >>> (48 - bits));
        // Java's `(int)long` is bit-pattern truncation — for next(32) with bit 31 set, it
        // produces a negative int. C# `(int)long` is bit-pattern truncation outside `checked`
        // blocks, BUT the Udon runtime always uses checked semantics for the underlying
        // SystemConvert.ToInt32(long) extern, which throws OverflowException for values
        // outside [int.MinValue, int.MaxValue]. UdonSharp does not support `unchecked`.
        //
        // Workaround: the XOR-then-subtract trick maps any unsigned 32-bit value held in
        // a long ([0, 2^32)) into the equivalent signed int range ([-2^31, 2^31)) using
        // only long arithmetic. For shifted >= 2^31, this produces a negative long that
        // fits in int range; for shifted < 2^31, it's a no-op on the numeric value.
        seed = (seed * multiplier + addend) & mask;
        long shifted = seed >> (48 - bits);
        return (int)((shifted ^ 0x80000000L) - 0x80000000L);
    }

    // PERF NOTE: the public draw methods below flatten the LCG advance inline instead of calling
    // Next(bits). In the Udon VM every user-method call costs ~5-20us of re-entry overhead, and
    // worldgen draws these tens of thousands of times per column finalize (surface params,
    // bedrock mask, decoration scatter). The math is byte-for-byte identical to the nested form —
    // one LCG advance per draw in the exact same order — so the generated terrain is unchanged.

    public int NextInt()
    {
        seed = (seed * multiplier + addend) & mask;
        long shifted = seed >> 16; // Next(32)
        return (int)((shifted ^ 0x80000000L) - 0x80000000L);
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
        // Next(31) yields [0, 2^31), which always fits a checked int cast — no sign fixup needed.
        if ((bound & -bound) == bound)  // i.e., bound is a power of 2
        {
            seed = (seed * multiplier + addend) & mask;
            return (int)((bound * (seed >> 17)) >> 31);
        }

        int bits, val;
        do
        {
            seed = (seed * multiplier + addend) & mask;
            bits = (int)(seed >> 17);
            val = bits % bound;
        } while (bits - val + (bound - 1) < 0);

        return val;
    }

    public long NextLong()
    {
        // Two signed Next(32) draws, same as ((long)Next(32) << 32) + Next(32).
        seed = (seed * multiplier + addend) & mask;
        int hi = (int)(((seed >> 16) ^ 0x80000000L) - 0x80000000L);
        seed = (seed * multiplier + addend) & mask;
        int lo = (int)(((seed >> 16) ^ 0x80000000L) - 0x80000000L);
        return ((long)hi << 32) + lo;
    }

    public bool NextBoolean()
    {
        seed = (seed * multiplier + addend) & mask;
        return (seed >> 47) != 0; // Next(1)
    }

    public float NextFloat()
    {
        seed = (seed * multiplier + addend) & mask;
        return (int)(seed >> 24) / ((float)(1 << 24)); // Next(24): [0, 2^24), checked-cast safe
    }

    /// <summary>
    /// Returns the next pseudorandom, uniformly distributed double value 
    /// between 0.0 (inclusive) and 1.0 (exclusive). This is a direct port
    /// of the Java implementation to ensure identical floating point results.
    /// </summary>
    public double NextDouble()
    {
        // (((long)Next(26) << 27) + Next(27)) / 2^53, flattened. Both draws are non-negative.
        seed = (seed * multiplier + addend) & mask;
        long hi = seed >> 22; // Next(26)
        seed = (seed * multiplier + addend) & mask;
        long lo = seed >> 21; // Next(27)
        return ((hi << 27) + lo) / (double)(1L << 53);
    }
}
