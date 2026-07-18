using UnityEngine;
using UnityEditor;
using System.IO;

/// <summary>
/// Bakes MC Beta 1.7.3's exact star field (RenderGlobal.renderStars) into an equirect
/// coverage texture the skybox samples at runtime — the vanilla 1500-quad display list
/// is unaffordable as a per-pixel shader loop on Quest, but the geometry is fully static
/// (built once from Random(10842L) at client startup), so a one-time bake is 1:1.
///
/// Port notes (RenderGlobal.java:122-167):
///  - Java Random(10842L), 1500 iterations; each iteration ALWAYS draws 4 floats
///    (x, y, z, size) and only accepted stars (0.01 &lt; len² &lt; 1.0) draw the extra
///    nextDouble() roll angle — the draw order must match or every later star moves.
///  - Star quads are tangent squares on a radius-100 sphere, half-size 0.25-0.5,
///    randomly rolled in-plane. We rasterize coverage analytically (ray/tangent-plane
///    intersection + square test in the quad's own rotated basis) with 2x2 subsamples.
///  - Runtime look: vanilla draws the list with glColor4f(b,b,b,b) and
///    GL_SRC_ALPHA/GL_ONE, i.e. adds coverage * b² — the shader does exactly that.
///
/// Equirect mapping (MUST stay in sync with MCSkyV2.shader's star sampling):
///   u = atan2(x, -z) / 2pi + 0.5,  v = asin(y) / pi + 0.5   (in the star model space)
/// Run via menu: Tools > VRCMinecraft > Bake b1.7.3 Star Field
/// </summary>
public class StarFieldBaker : EditorWindow
{
    private const int W = 4096;
    private const int H = 2048;
    private const string OUT_PATH = "Assets/VRCMinecraft/Textures/starfield_b173.png";
    private const string SKY_SHADER_NAME = "Minecraft Sky (With Stars)";

    [MenuItem("Tools/VRCMinecraft/Bake b1.7.3 Star Field")]
    public static void Bake()
    {
        float[] cov = new float[W * H];
        JavaRandom rand = new JavaRandom(10842L);
        int accepted = 0;

        for (int i = 0; i < 1500; i++)
        {
            // Verbatim draws — floats promoted to double exactly like the Java locals.
            double x = (double)(rand.NextFloat() * 2.0f - 1.0f);
            double y = (double)(rand.NextFloat() * 2.0f - 1.0f);
            double z = (double)(rand.NextFloat() * 2.0f - 1.0f);
            double size = (double)(0.25f + rand.NextFloat() * 0.25f);
            double lenSq = x * x + y * y + z * z;
            if (!(lenSq < 1.0 && lenSq > 0.01)) continue; // rejected: no nextDouble()

            accepted++;
            double inv = 1.0 / System.Math.Sqrt(lenSq);
            x *= inv; y *= inv; z *= inv;
            double cx = x * 100.0, cy = y * 100.0, cz = z * 100.0;
            double azim = System.Math.Atan2(x, z);
            double sinAz = System.Math.Sin(azim), cosAz = System.Math.Cos(azim);
            double polar = System.Math.Atan2(System.Math.Sqrt(x * x + z * z), y);
            double sinPol = System.Math.Sin(polar), cosPol = System.Math.Cos(polar);
            double roll = rand.NextDouble() * System.Math.PI * 2.0;
            double sinRoll = System.Math.Sin(roll), cosRoll = System.Math.Cos(roll);

            // The vanilla corner loop maps square coords (a,b) through roll + the two
            // spherical rotations. Running it with (a,b)=(1,0) and (0,1) yields the
            // quad's orthonormal in-plane axes A and B; inside-quad is then just
            // |dot(P-C,A)| <= size && |dot(P-C,B)| <= size.
            double aX, aY, aZ, bX, bY, bZ;
            {
                double ar = cosRoll, br = sinRoll;                    // (a,b) = (1,0)
                double dy = ar * sinPol, dt = -ar * cosPol;
                aX = dt * sinAz - br * cosAz;
                aY = dy;
                aZ = br * sinAz + dt * cosAz;
            }
            {
                double ar = -sinRoll, br = cosRoll;                   // (a,b) = (0,1)
                double dy = ar * sinPol, dt = -ar * cosPol;
                bX = dt * sinAz - br * cosAz;
                bY = dy;
                bZ = br * sinAz + dt * cosAz;
            }

            // Angular bounding box in equirect space (corner reach = size*sqrt2 at r=100),
            // padded ~2 pixels for the subsample grid.
            double angR = System.Math.Atan(size * 1.4142135623730951 / 100.0) + 2.0 * System.Math.PI / H;
            double lat = System.Math.Asin(y < -1.0 ? -1.0 : (y > 1.0 ? 1.0 : y));
            double lon = System.Math.Atan2(x, -z);
            int py0 = (int)System.Math.Floor(((lat - angR) / System.Math.PI + 0.5) * H);
            int py1 = (int)System.Math.Ceiling(((lat + angR) / System.Math.PI + 0.5) * H);
            if (py0 < 0) py0 = 0;
            if (py1 > H - 1) py1 = H - 1;

            for (int py = py0; py <= py1; py++)
            {
                double rowLat = ((py + 0.5) / H - 0.5) * System.Math.PI;
                double rowCos = System.Math.Cos(rowLat);
                double lonHalf = rowCos > 1e-4 ? angR / rowCos : System.Math.PI;
                int px0, px1;
                if (lonHalf >= System.Math.PI) { px0 = 0; px1 = W - 1; }
                else
                {
                    px0 = (int)System.Math.Floor(((lon - lonHalf) / (2.0 * System.Math.PI) + 0.5) * W);
                    px1 = (int)System.Math.Ceiling(((lon + lonHalf) / (2.0 * System.Math.PI) + 0.5) * W);
                }

                for (int px = px0; px <= px1; px++)
                {
                    int wpx = ((px % W) + W) % W;
                    float hit = 0f;
                    for (int sy = 0; sy < 2; sy++)
                    {
                        for (int sx = 0; sx < 2; sx++)
                        {
                            double su = (wpx + (sx + 0.5) * 0.5) / W;
                            double sv = (py + (sy + 0.5) * 0.5) / H;
                            double slon = (su - 0.5) * 2.0 * System.Math.PI;
                            double slat = (sv - 0.5) * System.Math.PI;
                            double dyd = System.Math.Sin(slat);
                            double cl = System.Math.Cos(slat);
                            double dxd = System.Math.Sin(slon) * cl;
                            double dzd = -System.Math.Cos(slon) * cl;
                            double dotN = dxd * x + dyd * y + dzd * z; // (x,y,z) = unit star dir
                            if (dotN <= 1e-6) continue;
                            double t = 100.0 / dotN;
                            double pX = dxd * t - cx;
                            double pY = dyd * t - cy;
                            double pZ = dzd * t - cz;
                            double ca = pX * aX + pY * aY + pZ * aZ;
                            double cb = pX * bX + pY * bY + pZ * bZ;
                            if (System.Math.Abs(ca) <= size && System.Math.Abs(cb) <= size) hit += 0.25f;
                        }
                    }
                    if (hit > 0f)
                    {
                        int idx = py * W + wpx;
                        float v = cov[idx] + hit;
                        cov[idx] = v > 1f ? 1f : v;
                    }
                }
            }
        }

        string dir = Path.GetDirectoryName(OUT_PATH);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        Texture2D tex = new Texture2D(W, H, TextureFormat.R8, false, true);
        byte[] data = new byte[W * H];
        for (int i = 0; i < cov.Length; i++) data[i] = (byte)(cov[i] * 255f + 0.5f);
        tex.SetPixelData(data, 0);
        File.WriteAllBytes(OUT_PATH, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(OUT_PATH);

        TextureImporter importer = AssetImporter.GetAtPath(OUT_PATH) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.SingleChannel;
            TextureImporterSettings s = new TextureImporterSettings();
            importer.ReadTextureSettings(s);
            s.singleChannelComponent = TextureImporterSingleChannelComponent.Red;
            s.sRGBTexture = false;
            importer.SetTextureSettings(s);
            importer.mipmapEnabled = false;       // computed-UV lon seam would smear a mip column
            importer.wrapModeU = TextureWrapMode.Repeat;   // longitude wraps
            importer.wrapModeV = TextureWrapMode.Clamp;    // latitude clamps at the poles
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = 4096;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed; // R8, 8MB
            importer.SaveAndReimport();
        }

        Texture2D asset = AssetDatabase.LoadAssetAtPath<Texture2D>(OUT_PATH);
        int assignedCount = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Material"))
        {
            string mPath = AssetDatabase.GUIDToAssetPath(guid);
            Material m = AssetDatabase.LoadAssetAtPath<Material>(mPath);
            if (m == null || m.shader == null || m.shader.name != SKY_SHADER_NAME) continue;
            if (!m.HasProperty("_StarTex")) continue;
            m.SetTexture("_StarTex", asset);
            EditorUtility.SetDirty(m);
            assignedCount++;
        }
        AssetDatabase.SaveAssets();

        Debug.Log($"[StarFieldBaker] Baked {accepted} stars (of 1500 candidates, seed 10842) to {OUT_PATH}; assigned _StarTex on {assignedCount} '{SKY_SHADER_NAME}' material(s).");
    }
}
