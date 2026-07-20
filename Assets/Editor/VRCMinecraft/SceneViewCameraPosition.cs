using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public class SceneViewCameraPosition
{
    private static GUIStyle style;
    private static Texture2D backgroundTexture;

    static SceneViewCameraPosition()
    { 
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private static void OnSceneGUI(SceneView sceneView)
    {
        if (sceneView.camera == null)
            return;

        if (style == null)
        {
            // Create a new GUIStyle
            style = new GUIStyle();
            style.alignment = TextAnchor.MiddleLeft;
            style.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
            style.padding = new RectOffset(5, 5, 2, 2);

            // Create a background texture
            if (backgroundTexture == null)
            {
                backgroundTexture = new Texture2D(1, 1);
                backgroundTexture.SetPixel(0, 0, new Color(0.1f, 0.1f, 0.1f, 0.6f));
                backgroundTexture.Apply();
            }
            style.normal.background = backgroundTexture;
        }

        Vector3 camPos = sceneView.camera.transform.position;
        
        // Show both Unity and Minecraft coordinates
        Vector3Int mcCoords = McUtils.UnityToMinecraftCoords(camPos, true);
        string unityText = $"Unity XYZ: {Mathf.Floor(camPos.x)}, {Mathf.Floor(camPos.y)}, {Mathf.Floor(camPos.z)}";
        string mcText = $"MC XYZ: {mcCoords.x}, {mcCoords.y}, {mcCoords.z}";

        Handles.BeginGUI();
        
        // Draw Unity coordinates
        Rect rect1 = new(10, sceneView.position.height - 40*3, 280, 20);
        GUI.Label(rect1, unityText, style);
        
        // Draw Minecraft coordinates
        Rect rect2 = new(10, sceneView.position.height - 40*2, 280, 20);
        GUI.Label(rect2, mcText, style);
        
        Handles.EndGUI();
    }
} 