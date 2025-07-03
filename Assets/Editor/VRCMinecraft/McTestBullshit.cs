/*
 * Minecraft Beta 1.7.3 Seed Tester - Editor Script
 *
 * How to Use:
 * 1. Place this script inside a folder named "Editor" in your Unity project's Assets directory.
 * 2. Make sure your McUtils.cs and JavaRandom.cs scripts are also in the project.
 * 3. A new menu item will appear at the top of the Unity editor: "Tools > Test Minecraft Seed".
 * 4. Click this menu item to open the seed testing popup window.
 *
 */

using UnityEngine;
using UnityEditor;
using System.Text; // Required for StringBuilder

public class SeedTesterWindow : EditorWindow
{
    private string seedInput = "gargamel";
    private string seedResultText = "(Result will be shown here)";
    private string permTableResultText = "(Permutation table will be shown here)";
    private Vector2 scrollPos; // For the scroll view

    // Creates the menu item in the Unity Editor
    [MenuItem("Tools/Test Minecraft Seed")]
    public static void ShowWindow()
    {
        GetWindow<SeedTesterWindow>("MC Seed Tester");
    }

    // This method is called to draw the GUI for the editor window
    void OnGUI()
    {
        GUILayout.Label("Minecraft b1.7.3 Seed & Permutation Tester", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Enter a string or a number to test the seed conversion and permutation table generation logic.", MessageType.Info);

        EditorGUILayout.Space();

        // Input field for the seed string
        seedInput = EditorGUILayout.TextField("Seed String:", seedInput);

        EditorGUILayout.Space();
        
        // --- Buttons ---
        EditorGUILayout.BeginHorizontal();

        // Button to trigger the seed calculation
        if (GUILayout.Button("Calculate Seed"))
        {
            int calculatedSeed = McUtils.GetMinecraftSeed(seedInput);
            seedResultText = $"Seed Result: {calculatedSeed}";
            permTableResultText = "(Permutation table cleared)";
        }

        // Button to trigger permutation table generation
        if (GUILayout.Button("Generate Permutation Table"))
        {
            int seed = McUtils.GetMinecraftSeed(seedInput);
            seedResultText = $"Using Seed: {seed}";
            
            byte[] permTable = McUtils.GetPermutationTable(seed);
            
            // Format the byte array into a readable string
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Permutation Table (512 bytes):");
            for (int i = 0; i < permTable.Length; i++)
            {
                sb.Append(permTable[i].ToString().PadLeft(3, ' '));
                if ((i + 1) % 16 == 0)
                {
                    sb.AppendLine(); // Newline every 16 numbers
                }
                else
                {
                    sb.Append(", ");
                }
            }
            permTableResultText = sb.ToString();
        }

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space();

        // --- Results Display ---
        EditorGUILayout.LabelField(seedResultText, EditorStyles.boldLabel);
        
        EditorGUILayout.Space();

        // Display the permutation table result in a scrollable text area
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.Height(200));
        EditorGUILayout.TextArea(permTableResultText, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }
}
