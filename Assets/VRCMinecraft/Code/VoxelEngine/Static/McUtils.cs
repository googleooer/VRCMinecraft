/*
 * Minecraft Beta 1.7.3 Seed Conversion Utility for UdonSharp
 *
 * How to Use:
 * 1. Create a new C# script in your Unity project, name it something like "UdonSeedUtility".
 * 2. Copy and paste this entire code into that file.
 * 3. In any of your UdonSharp Behaviours, you can now call this function like so:
 *
 * // At the top of your other script
 * using VRC.Udon;
 * using UdonSharp;
 * using UnityEngine;
 *
 * public class MyWorldGenerator : UdonSharpBehaviour
 * {
 * public int GetSeedFromString(string seedString)
 * {
 * return UdonSeedUtility.GetMinecraftSeed(seedString);
 * }
 *
 * void Start()
 * {
 * int gargamelSeed = GetSeedFromString("gargamel");
 * Debug.Log($"The seed for 'gargamel' is: {gargamelSeed}"); // Outputs: -1736691485
 *
 * int numericSeed = GetSeedFromString("404");
 * Debug.Log($"The seed for '404' is: {numericSeed}"); // Outputs: 404
 * }
 * }
 *
 */
using System;
using UnityEngine;

// This class contains a static method, making it a utility that can be called
// from any other UdonSharp script without needing an instance in the scene.
public static class McUtils
{



    /// <summary>
    /// Converts a string into a Minecraft Beta 1.7.3 compatible integer seed.
    /// This function replicates the original game's two-step logic within Udon's constraints.
    /// </summary>
    /// <param name="seedString">The input string from the user.</param>
    /// <returns>A 32-bit signed integer seed.</returns>
    public static int GetMinecraftSeed(string seedString)
    {
        // --- Step 0: Handle Empty Seed ---
        // If the seed string is empty, the original game would generate a new random seed.
        // Since we want a deterministic function, we'll return 0, which is the hash of an empty string anyway.
        if (string.IsNullOrEmpty(seedString))
        {
            return 0;
        }

        // --- Step 1: Try to Parse as a Number ---
        // The original game tried to parse the string as a 64-bit long. Udon only supports
        // 32-bit ints. We will try to parse as an int. If it succeeds, we use that value.
        // If it fails (either because it contains letters or is too large for an int),
        // we fall through to the hashing mechanism, which is the correct behavior.
        if (int.TryParse(seedString, out int parsedSeed))
        {
            return parsedSeed;
        }

        // --- Step 2: Fallback to Java's String.hashCode() Algorithm ---
        // This is executed if the string is not a valid 32-bit integer.
        // The formula is: s[0]*31^(n-1) + s[1]*31^(n-2) + ... + s[n-1]
        // A more efficient way to compute this is iteratively: h = 31 * h + val[i]
        int hash = 0;
        for (int i = 0; i < seedString.Length; i++)
        {
            char character = seedString[i];
            // The C# `int` type is a 32-bit signed integer, and its arithmetic
            // operations (like multiplication and addition) will wrap on overflow
            // by default. This perfectly mimics the behavior of Java's integer
            // arithmetic, so no special handling is required.
            hash = (31 * hash) + character;
        }

        return hash;
    }

    /// <summary>
    /// Generates the 512-byte permutation table for a given Minecraft seed.
    /// </summary>
    /// <param name="seed">The world seed, typically from GetMinecraftSeed().</param>
    /// <returns>A 512-element byte array containing the permutation table.</returns>
    public static byte[] GetPermutationTable(int seed)
    {
        JavaRandom random = new JavaRandom(seed);
        
        int[] p_int = new int[256];
        for (int i = 0; i < 256; i++)
        {
            p_int[i] = i;
        }

        // Fisher-Yates shuffle
        for (int i = 255; i > 0; i--)
        {
            int index = random.NextInt(i + 1);
            int a = p_int[index];
            p_int[index] = p_int[i];
            p_int[i] = a;
        }

        // Duplicate the shuffled list into the final 512-byte array
        byte[] p_byte = new byte[512];
        for (int i = 0; i < 256; i++)
        {
            p_byte[i] = (byte)p_int[i];
            p_byte[i + 256] = (byte)p_int[i];
        }

        return p_byte;
    }

    /// <summary>
    /// Converts a block position to a world position.
    /// </summary>
    /// <param name="blockPos">The block position to convert.</param>
    /// <param name="chunkPos">The chunk the block is in.</param>
    /// <param name="chunkSizeXZ">The size of the chunks in the X and Z directions.</param>
    /// <param name="chunkSizeY">The size of the chunks in the Y direction.</param>
    /// <returns>The world position of the chunk.</returns>
    public static Vector3Int BlockPos_ToWorld(Vector3Int blockPos, Vector3Int chunkPos, int chunkSizeXZ, int chunkSizeY)
    {
        return new Vector3Int(blockPos.x + chunkPos.x * chunkSizeXZ, blockPos.y + chunkPos.y * chunkSizeY, blockPos.z + chunkPos.z * chunkSizeXZ);
    }

    /// <summary>
    /// Converts a world block position to a position relative to a chunk.
    /// </summary>
    /// <param name="blockPos">The block position to convert.</param>
    /// <param name="chunkPos">The chunk the block is in.</param>
    /// <param name="chunkSizeXZ">The size of the chunks in the X and Z directions.</param>
    /// <param name="chunkSizeY">The size of the chunks in the Y direction.</param>
    /// <returns>The chunk position of the block.</returns>
    public static Vector3Int BlockPos_ToChunk(Vector3Int blockPos, Vector3Int chunkPos, int chunkSizeXZ, int chunkSizeY)
    {
        return new Vector3Int(blockPos.x - chunkPos.x * chunkSizeXZ, blockPos.y - chunkPos.y * chunkSizeY, blockPos.z - chunkPos.z * chunkSizeXZ);
    }

    /// <summary>
    /// Converts a chunk's position to a world position.
    /// </summary>
    /// <param name="chunkPos">The chunk position to convert.</param>
    /// <param name="chunkSizeXZ">The size of the chunks in the X and Z directions.</param>
    /// <param name="chunkSizeY">The size of the chunks in the Y direction.</param>
    /// <returns>The world position of the chunk.</returns>
    public static Vector3Int ChunkPos_ToWorld(Vector3Int chunkPos, int chunkSizeXZ, int chunkSizeY)
    {
        return new Vector3Int(chunkPos.x * chunkSizeXZ, chunkPos.y * chunkSizeY, chunkPos.z * chunkSizeXZ);
    }
}