using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PerlinNoiseGenerator : UdonSharpBehaviour
{
    // Beta 1.7.3 exact parameters
    private const float COORDINATE_SCALE = 684.412f;
    private const float HEIGHT_SCALE = 684.412f;
    private const float MAIN_NOISE_SCALE_X = 80.0f;
    private const float MAIN_NOISE_SCALE_Y = 160.0f;
    private const float MAIN_NOISE_SCALE_Z = 80.0f;
    private const float DEPTH_NOISE_SCALE_X = 200.0f;
    private const float DEPTH_NOISE_SCALE_Z = 200.0f;
    
    private int[] permutation;
    private float[] gradients1D;
    private int seed;
    
    public void Initialize(int worldSeed, int offset)
    {
        seed = worldSeed + offset;
        permutation = new int[512];
        gradients1D = new float[512];
        
        // Initialize permutation table
        Random.InitState(seed);
        for (int i = 0; i < 256; i++)
        {
            permutation[i] = i;
        }
        
        // Shuffle using Fisher-Yates
        for (int i = 255; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            int temp = permutation[i];
            permutation[i] = permutation[j];
            permutation[j] = temp;
        }
        
        // Duplicate for wrapping
        for (int i = 0; i < 256; i++)
        {
            permutation[256 + i] = permutation[i];
            gradients1D[i] = Random.Range(-1.0f, 1.0f);
            gradients1D[256 + i] = gradients1D[i];
        }
    }
    
    public float GenerateNoise(float x, float y, float z, int octaves, float persistence)
    {
        float total = 0;
        float frequency = 1;
        float amplitude = 1;
        float maxValue = 0;
        
        for (int i = 0; i < octaves; i++)
        {
            total += PerlinNoise3D(x * frequency, y * frequency, z * frequency) * amplitude;
            maxValue += amplitude;
            amplitude *= persistence;
            frequency *= 2;
        }
        
        return total / maxValue;
    }
    
    private float PerlinNoise3D(float x, float y, float z)
    {
        // Find unit grid cell
        int X = Mathf.FloorToInt(x) & 255;
        int Y = Mathf.FloorToInt(y) & 255;
        int Z = Mathf.FloorToInt(z) & 255;
        
        // Relative position in cell
        x -= Mathf.Floor(x);
        y -= Mathf.Floor(y);
        z -= Mathf.Floor(z);
        
        // Fade curves
        float u = Fade(x);
        float v = Fade(y);
        float w = Fade(z);
        
        // Hash coordinates of cube corners
        int A = permutation[X] + Y;
        int AA = permutation[A] + Z;
        int AB = permutation[A + 1] + Z;
        int B = permutation[X + 1] + Y;
        int BA = permutation[B] + Z;
        int BB = permutation[B + 1] + Z;
        
        // Blend results from 8 corners
        float res = Lerp(w, 
            Lerp(v, 
                Lerp(u, Grad(permutation[AA], x, y, z), 
                        Grad(permutation[BA], x - 1, y, z)),
                Lerp(u, Grad(permutation[AB], x, y - 1, z), 
                        Grad(permutation[BB], x - 1, y - 1, z))),
            Lerp(v, 
                Lerp(u, Grad(permutation[AA + 1], x, y, z - 1), 
                        Grad(permutation[BA + 1], x - 1, y, z - 1)),
                Lerp(u, Grad(permutation[AB + 1], x, y - 1, z - 1), 
                        Grad(permutation[BB + 1], x - 1, y - 1, z - 1))));
        
        return res;
    }
    
    private float Fade(float t)
    {
        return t * t * t * (t * (t * 6 - 15) + 10);
    }
    
    private float Lerp(float t, float a, float b)
    {
        return a + t * (b - a);
    }
    
    private float Grad(int hash, float x, float y, float z)
    {
        int h = hash & 15;
        float u = h < 8 ? x : y;
        float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}