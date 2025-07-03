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
        string text = $"XYZ: {Mathf.Floor(camPos.x):F2}, {Mathf.Floor(camPos.y):F2}, {Mathf.Floor(camPos.z):F2}";

        Handles.BeginGUI();
        
        // Define the rectangle for the label in the bottom-left corner
        Rect rect = new(10, sceneView.position.height - 40*2, 220, 20);
        
        // Draw the label
        GUI.Label(rect, text, style);
        
        Handles.EndGUI();
    }
} 