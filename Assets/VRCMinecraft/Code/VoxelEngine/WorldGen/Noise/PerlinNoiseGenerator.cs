using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PerlinNoiseGenerator : UdonSharpBehaviour
{
    // Beta 1.7.3 exact parameters
    [SerializeField] private float COORDINATE_SCALE = 684.412f;
    [SerializeField] private float HEIGHT_SCALE = 684.412f;
    [SerializeField] private float MAIN_NOISE_SCALE_X = 80.0f;
    [SerializeField] private float MAIN_NOISE_SCALE_Y = 160.0f;
    [SerializeField] private float MAIN_NOISE_SCALE_Z = 80.0f;
    [SerializeField] private float DEPTH_NOISE_SCALE_X = 200.0f;
    [SerializeField] private float DEPTH_NOISE_SCALE_Z = 200.0f;
    
    private int[] permutation;
    private Vector3[] gradients3D; // Use 3D gradients for better noise
    private int seed;
    
    // Predefined gradient vectors for 3D Perlin noise
    private readonly Vector3[] grad3 = new Vector3[] {
        new Vector3(1,1,0), new Vector3(-1,1,0), new Vector3(1,-1,0), new Vector3(-1,-1,0),
        new Vector3(1,0,1), new Vector3(-1,0,1), new Vector3(1,0,-1), new Vector3(-1,0,-1),
        new Vector3(0,1,1), new Vector3(0,-1,1), new Vector3(0,1,-1), new Vector3(0,-1,-1)
    };
    
    public void Initialize(int worldSeed, int offset)
    {
        seed = worldSeed + offset;
        permutation = new int[512];
        gradients3D = new Vector3[512];
        
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
        
        // Duplicate for wrapping and assign gradients
        for (int i = 0; i < 256; i++)
        {
            permutation[256 + i] = permutation[i];
            gradients3D[i] = grad3[permutation[i] % 12];
            gradients3D[256 + i] = gradients3D[i];
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
        
        // Normalize to [-1, 1] range
        return total / maxValue;
    }
    
    private float PerlinNoise3D(float x, float y, float z)
    {
        // Find unit grid cell containing point
        int X = FastFloor(x) & 255;
        int Y = FastFloor(y) & 255;
        int Z = FastFloor(z) & 255;
        
        // Find relative x,y,z of point in cell
        x -= FastFloor(x);
        y -= FastFloor(y);
        z -= FastFloor(z);
        
        // Compute fade curves for each of x,y,z
        float u = Fade(x);
        float v = Fade(y);
        float w = Fade(z);
        
        // Hash coordinates of the 8 cube corners
        int A = (permutation[X] + Y) & 255;
        int AA = (permutation[A] + Z) & 255;
        int AB = (permutation[A + 1] + Z) & 255;
        int B = (permutation[(X + 1) & 255] + Y) & 255;
        int BA = (permutation[B] + Z) & 255;
        int BB = (permutation[B + 1] + Z) & 255;
        
        // And add blended results from 8 corners of cube
        float res = Lerp(w, 
            Lerp(v, 
                Lerp(u, 
                    Dot(gradients3D[permutation[AA & 255]], x, y, z),
                    Dot(gradients3D[permutation[BA & 255]], x - 1, y, z)),
                Lerp(u, 
                    Dot(gradients3D[permutation[AB & 255]], x, y - 1, z),
                    Dot(gradients3D[permutation[BB & 255]], x - 1, y - 1, z))),
            Lerp(v, 
                Lerp(u, 
                    Dot(gradients3D[permutation[(AA + 1) & 255]], x, y, z - 1),
                    Dot(gradients3D[permutation[(BA + 1) & 255]], x - 1, y, z - 1)),
                Lerp(u, 
                    Dot(gradients3D[permutation[(AB + 1) & 255]], x, y - 1, z - 1),
                    Dot(gradients3D[permutation[(BB + 1) & 255]], x - 1, y - 1, z - 1))));
                    
        return res;
    }
    
    // Optimized floor function
    private int FastFloor(float x)
    {
        return x > 0 ? (int)x : (int)x - 1;
    }
    
    // Fade function as defined by Ken Perlin
    private float Fade(float t)
    {
        return t * t * t * (t * (t * 6 - 15) + 10);
    }
    
    private float Lerp(float t, float a, float b)
    {
        return a + t * (b - a);
    }
    
    // 3D dot product
    private float Dot(Vector3 g, float x, float y, float z)
    {
        return g.x * x + g.y * y + g.z * z;
    }
}