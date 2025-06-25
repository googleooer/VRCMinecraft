
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// A minimal Perlin noise implementation is required. Since new files are disallowed,
// it should be added as another component in the scene and referenced.
// Create a new empty GameObject, add this script to it.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PerlinNoiseGenerator : UdonSharpBehaviour
{
}
