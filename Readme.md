# VRCMinecraft Voxel Engine

This project is a Voxel Engine built with UdonSharp for VRChat.

## UdonSharp Optimizations and Considerations

This section details some of the key optimization strategies and UdonSharp-specific considerations employed in the voxel engine's design, primarily within `McWorld.cs` and `McChunk.cs`.

### Data Structures

*   **Global Voxel Data (1D Array):** The core world voxel data is stored in `McWorld.data` as a flattened `byte[]` (1D array). This is a common C# optimization pattern adapted for UdonSharp because UdonSharp does not support true multi-dimensional arrays (e.g., `byte[,,]`). Jagged arrays (`byte[][][]`) would likely introduce more overhead and pointer indirections, making the 1D array approach generally more performant for cache locality and access speed after index calculation.
*   **Chunk Storage (1D Array):** Similarly, chunk instances in `McWorld.cs` are stored in a one-dimensional array (`McChunk[] chunks_1D`), with 3D chunk array coordinates mapped to a 1D index via a helper function. This approach is also used for the `chunkDataFinalized_1D` boolean flags. This was chosen to reduce array indirections and simplify data management compared to jagged arrays, aligning with practices often beneficial in performance-sensitive or restricted environments like UdonSharp.
*   **Circular Buffer for Chunk Rebuild Queue:** The `chunkRebuildQueue` in `McWorld.cs` is implemented as a circular buffer (also known as a ring buffer). This data structure allows for O(1) time complexity for both enqueue (adding a chunk to be rebuilt) and dequeue (removing a chunk to process it) operations. This is a significant improvement over potentially using array-based lists that might require shifting elements (an O(n) operation) when adding to the front or removing from arbitrary positions.
*   **Parallel Arrays for Block Type Management:** `McBlockTypeManager.cs` utilizes parallel arrays to manage various properties of different block types (e.g., visibility, texture IDs). This is a common workaround in UdonSharp, which has limitations on serializing lists or arrays of custom classes directly in the Unity Inspector. While less ideal than a list of struct/class, it's a practical approach for Udon.

### Performance Strategies

*   **Time Slicing with Custom Events:** Computationally intensive tasks, such as initial world data generation (`McWorld.PopulateDataSliceForCurrentTargetChunk`) and individual chunk mesh building (`McChunk.ProcessMeshSlice`), are broken down into smaller slices. `SendCustomEventDelayedFrames` or `SendCustomEventDelayedSeconds` are used to process these slices over multiple frames. This cooperative multitasking approach prevents the engine from blocking the main thread, which is crucial for maintaining responsiveness and avoiding VRChat client freezes.
*   **Mesh Data Pooling:** `McChunk.cs` pre-allocates and reuses arrays for mesh data (vertices, triangles, UVs, normals) across multiple mesh builds (e.g., `opaque_vertexPool`, `transparent_trianglePool`). This significantly reduces garbage generation and collection (GC) pauses that would occur if these arrays were newly allocated for every mesh rebuild.
*   **Caching Frequently Used Data:** Some frequently accessed data or small arrays that were previously created in loops have been converted to `static readonly` or `readonly` instance fields. For example, direction offset arrays used for neighbor checking in `McWorld.cs` and `McChunk.cs` are now cached to avoid repeated small memory allocations within performance-critical loops.

### UdonSharp Limitations Avoided/Handled

*   **`GetComponent<T>()` in Loops:** The codebase consistently avoids calling `GetComponent<T>()` inside frequently executed loops. Component references are typically obtained once during initialization (e.g., in `Start()` or via `[SerializeField, GetComponent]`) and cached in member variables.
*   **Array Allocation Management:** Care is taken to minimize dynamic array allocations in hot paths. Pooling and pre-allocation are preferred, as seen in mesh data handling.
*   **Generic Collections:** The direct use of generic collections like `List<T>` or `Dictionary<T,V>` is generally avoided in UdonBehaviours, especially for data that might need to be synced or heavily manipulated at runtime, due to historical performance characteristics or full support nuances in UdonSharp. Standard arrays are used with manual management where necessary (e.g., the circular buffer).

### Assumptions for Optimizations

The optimization strategies implemented are based on the general assumption that the following practices are beneficial for UdonSharp performance and reducing Garbage Collector (GC) pressure:
*   Minimizing C# object allocations, particularly arrays and new objects, within frequently executed loops.
*   Employing efficient data structures for common operations, such as using a circular buffer for queue management to achieve O(1) performance for enqueue/dequeue.
*   Leveraging time-slicing for long-running tasks to maintain application responsiveness.
*   Caching frequently accessed component references and data.
