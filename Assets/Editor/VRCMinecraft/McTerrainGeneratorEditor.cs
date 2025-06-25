#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.IO;

/// <summary>
/// Custom editor for the McTerrainGenerator.
/// This version provides 100% per-pixel accurate previews with high performance by
/// offloading all noise calculations to a Compute Shader on the GPU.
/// Features interactive pan and zoom.
/// </summary>
[CustomEditor(typeof(McTerrainGenerator))]
public class McTerrainGeneratorEditor : Editor
{
    private McTerrainGenerator generator;
    private McWorld worldInstance;
    
    private ComputeShader previewShader;
    private RenderTexture sideViewTexture;
    private RenderTexture topViewTexture;

    private ComputeBuffer permUpperBuffer;
    
    // --- Pan and Zoom State ---
    private Vector2 previewOrigin = Vector2.zero;
    private float previewZoom = 256f;
    private float sideViewYZoom = 256f;
    private bool isPanningTopView = false;
    private bool isPanningSideView = false;
    private Vector2 panStartMousePos;
    private Vector2 panStartOrigin;


    private void OnEnable()
    {
        generator = (McTerrainGenerator)target;
        worldInstance = FindObjectOfType<McWorld>();

        previewShader = Resources.Load<ComputeShader>("TerrainPreview");

        if (previewShader == null)
        {
            Debug.LogError("TerrainPreview.compute shader not found. Make sure it is in a 'Resources' folder (e.g., 'Assets/Editor/Resources'). The file must be named 'TerrainPreview.compute'.");
            return;
        }

        // Set initial Y zoom to match world height
        sideViewYZoom = GetMaxWorldHeight();

        InitializeRenderTextures();
        GeneratePreviews();
    }

    private void OnDisable()
    {
        // Clean up all GPU resources
        ReleaseBuffers();
        if (sideViewTexture != null) sideViewTexture.Release();
        if (topViewTexture != null) topViewTexture.Release();
    }
    
    private void InitializeRenderTextures()
    {
        if (sideViewTexture == null || !sideViewTexture.IsCreated())
        {
            sideViewTexture = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGB32);
            sideViewTexture.enableRandomWrite = true;
            sideViewTexture.Create();
        }
        if (topViewTexture == null || !topViewTexture.IsCreated())
        {
            topViewTexture = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGB32);
            topViewTexture.enableRandomWrite = true;
            topViewTexture.Create();
        }
    }

    private void ReleaseBuffers()
    {
        permUpperBuffer?.Release();
        permUpperBuffer = null;
    }

    public override void OnInspectorGUI()
    {
        if (previewShader == null)
        {
            EditorGUILayout.HelpBox("TerrainPreview.compute shader not found. Make sure it is in a 'Resources' folder and that there are no script errors preventing it from loading.", MessageType.Error);
            return;
        }

        EditorGUI.BeginChangeCheck();
        
        // If any values changed, regenerate the preview
        if (EditorGUI.EndChangeCheck())
        {
            GeneratePreviews();
        }

        GUILayout.Label("GPU Accelerated Previews (Drag to Pan, Scroll to Zoom, Ctrl+Scroll for Y-Zoom)", EditorStyles.largeLabel);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.Space(16);
        // --- Top-Down Preview ---
        EditorGUILayout.BeginVertical();
        GUILayout.Label("Top-Down (XZ)", EditorStyles.boldLabel);
        GUILayout.Space(16);
        GUILayout.Box(topViewTexture, GUILayout.Width(256), GUILayout.Height(256));
        Rect topDownRect = GUILayoutUtility.GetLastRect();
        DrawCoordinateLabels(topDownRect, "X", "Z", previewZoom, GetMaxWorldHeight());
        EditorGUILayout.EndVertical();
        
        // --- Side Profile Preview ---
        EditorGUILayout.BeginVertical();
        GUILayout.Label("Side Profile (XY)", EditorStyles.boldLabel);
        GUILayout.Space(16);
        GUILayout.Box(sideViewTexture, GUILayout.Width(256), GUILayout.Height(256));
        Rect sideOnRect = GUILayoutUtility.GetLastRect();
        DrawCoordinateLabels(sideOnRect, "X", "Y", previewZoom, sideViewYZoom);
        EditorGUILayout.EndVertical();
        
        EditorGUILayout.EndHorizontal();

        // Handle Pan and Zoom input for both rects
        HandlePreviewInput(topDownRect, ref isPanningTopView, false); // isSideView = false
        HandlePreviewInput(sideOnRect, ref isPanningSideView, true);   // isSideView = true
        
        EditorGUILayout.Space(10); 
        
        if (GUILayout.Button("Reset View", GUILayout.Height(24)))
        {
            previewOrigin = Vector2.zero;
            previewZoom = 256f;
            sideViewYZoom = GetMaxWorldHeight();
            GeneratePreviews();
        }

        // --- Auto-refresh on change ---
        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        if (EditorGUI.EndChangeCheck())
        {
            GeneratePreviews();
        }
    }

    private void HandlePreviewInput(Rect previewRect, ref bool isPanning, bool isSideView)
    {
        Event e = Event.current;
        if (previewRect.Contains(e.mousePosition))
        {
            // Zooming with scroll wheel
            if (e.type == EventType.ScrollWheel)
            {
                if(isSideView && e.control) // Vertical zoom on side view with Ctrl
                {
                    sideViewYZoom *= 1.0f - e.delta.y * 0.1f;
                    sideViewYZoom = Mathf.Max(1.0f, sideViewYZoom);
                }
                else // Standard horizontal zoom
                {
                    previewZoom *= 1.0f - e.delta.y * 0.1f;
                    previewZoom = Mathf.Max(1.0f, previewZoom); 
                }
                GeneratePreviews();
                e.Use();
            }
            
            // Panning start
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                isPanning = true;
                panStartMousePos = e.mousePosition;
                panStartOrigin = previewOrigin;
                e.Use();
            }
        }
        
        // Panning update
        if (isPanning && e.type == EventType.MouseDrag && e.button == 0)
        {
            Vector2 delta = e.mousePosition - panStartMousePos;
            float scale = previewZoom / 256f;

            if (isSideView)
            {
                previewOrigin.x = panStartOrigin.x - delta.x * scale;
            }
            else // Top-down view
            {
                previewOrigin = panStartOrigin - new Vector2(delta.x, -delta.y) * scale;
            }

            GeneratePreviews();
            e.Use();
        }
        
        // Panning end
        if (isPanning && e.type == EventType.MouseUp && e.button == 0)
        {
            isPanning = false;
            e.Use();
        }
    }

    private int GetMaxWorldHeight()
    {
        if (worldInstance == null) worldInstance = FindObjectOfType<McWorld>();
        return worldInstance != null ? worldInstance.worldDimensionY * worldInstance.chunkSizeY : 256;
    }
    
    private void GeneratePreviews()
    {
        if (generator == null || !generator.isActiveAndEnabled || previewShader == null) return;
        
        generator.InitializeGenerator(worldInstance != null ? worldInstance.worldSeedString.GetHashCode() : 0);

        int sideViewKernel = previewShader.FindKernel("CSMain_SideView");
        int topViewKernel = previewShader.FindKernel("CSMain_TopDownView");
        
        ReleaseBuffers();
        
        // Pack parameters for shader - include all the important values
        Vector4 parameters = new Vector4(
            generator.seaLevel,
            generator.surfaceDepth,
            generator.terrainHeightMultiplier,
            worldInstance != null ? worldInstance.worldSeedString.GetHashCode() : 0
        );
        
        // Noise scales used in Beta 1.7.3
        Vector4 noiseScales = new Vector4(
            684.412f,  // coordinateScale
            200.0f,    // depthScale  
            80.0f,     // mainScale
            512.0f     // selectorScale
        );
        
        previewShader.SetVector("_Params", parameters);
        previewShader.SetVector("_NoiseScales", noiseScales);
        previewShader.SetVector("_PreviewTransform", new Vector4(previewOrigin.x, previewOrigin.y, previewZoom, sideViewYZoom));
        
        // Dispatch kernels
        previewShader.SetTexture(sideViewKernel, "_Result", sideViewTexture);
        previewShader.Dispatch(sideViewKernel, sideViewTexture.width / 8, sideViewTexture.height / 8, 1);
        
        previewShader.SetTexture(topViewKernel, "_Result", topViewTexture);
        previewShader.Dispatch(topViewKernel, topViewTexture.width / 8, topViewTexture.height / 8, 1);
        
        Repaint();
    }

    private void DrawCoordinateLabels(Rect area, string horizontalAxis, string verticalAxis, float horizontalMax, float verticalMax)
    {
        GUIStyle style = new GUIStyle();
        style.alignment = TextAnchor.MiddleCenter;
        style.normal.textColor = Color.white;
        style.fontSize = 10;
        
        string horizontalMinLabel = (previewOrigin.x - horizontalMax / 2f).ToString("F0");
        string horizontalMaxLabel = (previewOrigin.x + horizontalMax / 2f).ToString("F0");
        string verticalMinLabel = "0"; // Default for side view Y-axis
        string verticalMaxLabel = verticalMax.ToString("F0");
        
        if(verticalAxis == "Z")
        {
             // For Top-Down View, Y-axis of preview is Z in world
             verticalMinLabel = (previewOrigin.y - horizontalMax / 2f).ToString("F0");
             verticalMaxLabel = (previewOrigin.y + horizontalMax / 2f).ToString("F0");
        }
        
        EditorGUI.LabelField(new Rect(area.x, area.y - 20, 50, 20), horizontalMinLabel, style);
        EditorGUI.LabelField(new Rect(area.xMax - 50, area.y - 20, 50, 20), horizontalMaxLabel, style);
        EditorGUI.LabelField(new Rect(area.x + area.width / 2 - 15, area.y - 35, 30, 20), horizontalAxis, style);

        EditorGUI.LabelField(new Rect(area.x - 50, area.y, 50, 20), verticalMaxLabel, style);
        EditorGUI.LabelField(new Rect(area.x - 50, area.yMax - 20, 50, 20), verticalMinLabel, style);

        Matrix4x4 matrix = GUI.matrix;
        GUIUtility.RotateAroundPivot(-90, new Vector2(area.x - 30, area.y + area.height / 2));
        EditorGUI.LabelField(new Rect(area.x - 25, area.y + area.height/2 - 15, 30, 20), verticalAxis, style);
        GUI.matrix = matrix;
        GUILayout.Space(8);
    }
}
#endif
