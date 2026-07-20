using UnityEngine;
using UnityEditor;
using System.IO;

/// <summary>
/// Generates a fire animation strip using MC Beta 1.7.3's exact procedural fire algorithm
/// (TextureFlamesFX.java). Outputs a 16x512 PNG (32 stacked frames) and auto-assigns it
/// to the cutout material's _FireTex slot on the chunk prefab.
/// Run via menu: Tools > VRCMinecraft > Generate Fire Texture
/// </summary>
public class FireTextureGenerator : EditorWindow
{
    [MenuItem("Tools/VRCMinecraft/Generate Fire Texture")]
    public static void Generate()
    {
        string dir = "Assets/VRCMinecraft/textures";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        int frameCount = 32;
        int frameH = 16;
        int stripH = frameCount * frameH;
        Texture2D strip = new Texture2D(16, stripH, TextureFormat.RGBA32, false);
        strip.filterMode = FilterMode.Point;
        strip.wrapMode = TextureWrapMode.Repeat;

        float[] bufA = new float[16 * 20];
        float[] bufB = new float[16 * 20];
        System.Random rng = new System.Random(12345);

        for (int tick = 0; tick < 30; tick++)
            SimTick(ref bufA, ref bufB, rng);

        for (int frame = 0; frame < frameCount; frame++)
        {
            SimTick(ref bufA, ref bufB, rng);

            int baseY = frame * frameH;
            for (int y = 0; y < frameH; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    strip.SetPixel(x, baseY + (frameH - 1 - y), ToColor(bufA[x + y * 16]));
                }
            }
        }
        strip.Apply();

        string path = dir + "/fire_strip.png";
        File.WriteAllBytes(path, strip.EncodeToPNG());
        AssetDatabase.ImportAsset(path);

        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.alphaIsTransparency = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.maxTextureSize = 1024;
            importer.SaveAndReimport();
        }

        DestroyImmediate(strip);
        AssetDatabase.Refresh();

        // Auto-assign to cutout materials that use the MCTerrain (Cutout) shader
        Texture2D fireTexAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (fireTexAsset != null)
        {
            int assigned = 0;
            // Find all materials in the project
            string[] materialGuids = AssetDatabase.FindAssets("t:Material", new[] { "Assets/VRCMinecraft" });
            foreach (string guid in materialGuids)
            {
                string matPath = AssetDatabase.GUIDToAssetPath(guid);
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat != null && mat.HasProperty("_FireTex"))
                {
                    mat.SetTexture("_FireTex", fireTexAsset);
                    EditorUtility.SetDirty(mat);
                    assigned++;
                    Debug.Log($"Assigned fire_strip to material: {matPath}");
                }
            }

            // Also check chunk prefabs in the scene for runtime materials
            var mcWorlds = Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in mcWorlds)
            {
                if (mb.GetType().Name != "McWorld") continue;
                Transform chunkPrefab = null;
                var field = mb.GetType().GetField("chunkPrefab", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (field != null) chunkPrefab = field.GetValue(mb) as Transform;
                if (chunkPrefab == null)
                {
                    // Try finding it as a serialized property
                    var so = new SerializedObject(mb);
                    var prop = so.FindProperty("chunkPrefab");
                    if (prop != null && prop.objectReferenceValue != null)
                        chunkPrefab = prop.objectReferenceValue as Transform;
                    if (chunkPrefab == null) continue;
                }

                Transform cutout = chunkPrefab.Find("Cutout");
                if (cutout == null) cutout = chunkPrefab.Find("cutout");
                if (cutout == null) continue;

                var renderer = cutout.GetComponent<MeshRenderer>();
                if (renderer == null || renderer.sharedMaterial == null) continue;

                if (renderer.sharedMaterial.HasProperty("_FireTex"))
                {
                    renderer.sharedMaterial.SetTexture("_FireTex", fireTexAsset);
                    EditorUtility.SetDirty(renderer.sharedMaterial);
                    assigned++;
                    Debug.Log($"Assigned fire_strip to chunk prefab cutout material: {renderer.sharedMaterial.name}");
                }
            }

            if (assigned > 0)
                Debug.Log($"Fire strip auto-assigned to {assigned} material(s)");
            else
                Debug.LogWarning("Could not auto-assign fire_strip. Manually assign it to your Cutout material's 'Fire Strip Texture' slot.");
        }

        Debug.Log($"Fire strip generated at {path} ({frameCount} frames, {stripH}px tall)");
    }

    [MenuItem("Tools/VRCMinecraft/Generate Lava Texture")]
    public static void GenerateLava()
    {
        string dir = "Assets/VRCMinecraft/textures";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        int frameCount = 64;
        int frameH = 16;
        int stripH = frameCount * frameH;
        Texture2D strip = new Texture2D(16, stripH, TextureFormat.RGBA32, false);
        strip.filterMode = FilterMode.Point;
        strip.wrapMode = TextureWrapMode.Repeat;

        float[] bufA = new float[256];
        float[] bufB = new float[256];
        float[] phase = new float[256];
        float[] impulse = new float[256];

        System.Random rng = new System.Random(54321);

        // Warm up long enough for the simulation to reach a natural state
        for (int tick = 0; tick < 120; tick++)
            LavaSimTick(ref bufA, ref bufB, phase, impulse, rng);

        for (int frame = 0; frame < frameCount; frame++)
        {
            // Run 2 sim steps per captured frame for more visual variety
            LavaSimTick(ref bufA, ref bufB, phase, impulse, rng);
            LavaSimTick(ref bufA, ref bufB, phase, impulse, rng);

            int baseY = frame * frameH;
            for (int y = 0; y < frameH; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    strip.SetPixel(x, baseY + (frameH - 1 - y), LavaToColor(bufA[x + y * 16]));
                }
            }
        }
        strip.Apply();

        string path = dir + "/lava_strip.png";
        File.WriteAllBytes(path, strip.EncodeToPNG());
        AssetDatabase.ImportAsset(path);

        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.alphaIsTransparency = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.maxTextureSize = 1024;
            importer.SaveAndReimport();
        }

        DestroyImmediate(strip);
        AssetDatabase.Refresh();

        Texture2D lavaTexAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (lavaTexAsset != null)
        {
            int assigned = 0;
            string[] materialGuids = AssetDatabase.FindAssets("t:Material", new[] { "Assets/VRCMinecraft" });
            foreach (string guid in materialGuids)
            {
                string matPath = AssetDatabase.GUIDToAssetPath(guid);
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat != null && mat.HasProperty("_LavaTex"))
                {
                    mat.SetTexture("_LavaTex", lavaTexAsset);
                    EditorUtility.SetDirty(mat);
                    assigned++;
                    Debug.Log($"Assigned lava_strip to material: {matPath}");
                }
            }
            if (assigned > 0)
                Debug.Log($"Lava strip auto-assigned to {assigned} material(s)");
            else
                Debug.LogWarning("Could not auto-assign lava_strip. Manually assign it to the Transparent material's '_LavaTex' slot.");
        }

        Debug.Log($"Lava strip generated at {path} ({frameCount} frames, {stripH}px tall)");
    }

    // MC's TextureLavaFX.onTick() - exact algorithm
    static void LavaSimTick(ref float[] src, ref float[] dst, float[] phase, float[] impulse, System.Random rng)
    {
        for (int x = 0; x < 16; x++)
        {
            for (int y = 0; y < 16; y++)
            {
                float sum = 0.0f;
                int sx = (int)(Mathf.Sin(y * Mathf.PI * 2.0f / 16.0f) * 1.2f);
                int sy = (int)(Mathf.Sin(x * Mathf.PI * 2.0f / 16.0f) * 1.2f);

                for (int dx = x - 1; dx <= x + 1; dx++)
                {
                    for (int dy = y - 1; dy <= y + 1; dy++)
                    {
                        int wx = (dx + sx) & 15;
                        int wy = (dy + sy) & 15;
                        sum += src[wx + wy * 16];
                    }
                }

                int pi = x + y * 16;
                dst[pi] = sum / 10.0f
                    + (phase[((x + 0) & 15) + ((y + 0) & 15) * 16]
                     + phase[((x + 1) & 15) + ((y + 0) & 15) * 16]
                     + phase[((x + 1) & 15) + ((y + 1) & 15) * 16]
                     + phase[((x + 0) & 15) + ((y + 1) & 15) * 16]) / 4.0f * 0.8f;

                phase[pi] += impulse[pi] * 0.01f;
                if (phase[pi] < 0.0f) phase[pi] = 0.0f;

                impulse[pi] -= 0.06f;
                if (rng.NextDouble() < 0.005)
                    impulse[pi] = 1.5f;
            }
        }

        float[] tmp = src;
        src = dst;
        dst = tmp;
    }

    // MC's lava color mapping: R=v*100+155, G=v²*255, B=v⁴*128, A=255
    static Color LavaToColor(float raw)
    {
        float v = Mathf.Clamp01(raw * 2.0f);
        float r = (v * 100f + 155f) / 255f;
        float g = (v * v * 255f) / 255f;
        float b = (v * v * v * v * 128f) / 255f;
        return new Color(r, g, b, 1f); // lava is fully opaque
    }

    static void SimTick(ref float[] src, ref float[] dst, System.Random rng)
    {
        for (int x = 0; x < 16; x++)
        {
            for (int y = 0; y < 20; y++)
            {
                int count = 18;
                float sum = src[x + ((y + 1) % 20) * 16] * count;

                for (int dx = x - 1; dx <= x + 1; dx++)
                {
                    for (int dy = y; dy <= y + 1; dy++)
                    {
                        if (dx >= 0 && dy >= 0 && dx < 16 && dy < 20)
                            sum += src[dx + dy * 16];
                        count++;
                    }
                }

                dst[x + y * 16] = sum / (count * 1.06f);

                if (y >= 19)
                {
                    double r1 = rng.NextDouble();
                    double r2 = rng.NextDouble();
                    double r3 = rng.NextDouble();
                    double r4 = rng.NextDouble();
                    dst[x + y * 16] = (float)(r1 * r2 * r3 * 4.0 + r4 * 0.1 + 0.2);
                }
            }
        }

        float[] tmp = src;
        src = dst;
        dst = tmp;
    }

    static Color ToColor(float raw)
    {
        float v = Mathf.Clamp01(raw * 1.8f);
        float r = (v * 155f + 100f) / 255f;
        float g = (v * v * 255f) / 255f;
        float b = (Mathf.Pow(v, 10f) * 255f) / 255f;
        float a = v < 0.5f ? 0f : 1f;
        return new Color(r, g, b, a);
    }
}
