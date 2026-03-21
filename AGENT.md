This is a project aiming to recreate Minecraft Beta 1.7.3 in VRChat's Udon scripting language as close as possible.

When consulting the Minecraft Beta 1.7.3 source code, only check *.java files.
You can consult Minecraft Beta 1.7.3's source code from F:\vrchat_projects\VRCMinecraft_Beta173_Deobf\RetroMCP
You can check .unity scene files to get the structure of a scene.

# UdonSharp limitations
Always make sure to check udon_blacklisted.txt, udon_whitelisted_types.txt, and udon_whitelisted.txt
Local method declarations are not currently supported by UdonSharp.
Multidimensional arrays are not yet supported by UdonSharp, consider using jagged arrays instead, OR one-dimensional arrays with smart logic to distinguish each axis.
Method is not exposed to Udon: 'new List<Vector3>()'.
Method is not exposed to Udon: 'gameObject.AddComponent<>()'
Method is not exposed to Udon: 'gameObject.AddComponent(typeof())'
Method is not exposed to Udon: 'UnityEngine.RenderTexture.active.set' (ask user to make rendertextures manually)
Method is not exposed to Udon: 'Graphics.Blit' (use VRCGraphics.Blit)
Nested type declarations are not currently supported by U#
Try/Catch/Finally is not supported by UdonSharp since Udon does not have a way to handle exceptions
Base Unity Coroutines are not exposed to Udon.
Base Unity Threading is not exposed to Udon.
Base Unity Yield is not exposed to Udon.
UdonSharp does not currently support node type 'YieldBreakStatement'

# Udon VM Quirks
Never use post-increment/decrement on an object field as an expression (e.g. `int x = obj.field++;`). The Udon VM does not return the pre-increment value correctly — it silently returns field+1 instead of field, skipping the first value. Always split into a read then a separate assignment:
```csharp
// BAD — Udon returns wrong value for first iteration:
int col = chunk._index++;

// GOOD — unambiguous across all runtimes:
int col = chunk._index;
chunk._index = col + 1;
```

# Chunk-related information
We are using individual materials for blocks
Chunks are 16x64x16
The world height goes from 0 to 256

# Performance Considerations
The Quest 2 has 6GB of total RAM, 4GB of actual usable memory for games.
The Quest 2 runs a weaker mobile SoC


## VRCGraphics / VRCAsyncGPUReadback / VRChat Shader Globals

When working on VRChat worlds that need GPU-side logic, treat **VRCGraphics** as **Udon-controlled GPU orchestration**, not as unrestricted Unity graphics or compute access.
When debugging systems that have both CPU and GPU options, ALWAYS assume that I'm referring to the issue happening on the GPU unless specified otherwise.

### Mental model

```text
Udon / UdonSharp
  ├─ configure materials, RenderTextures, and cached shader property IDs
  ├─ kick GPU work (Blit / Custom Render Texture / instanced draw)
  ├─ optionally push shared world-wide values via VRCShader.SetGlobal*
  └─ optionally request small GPU→CPU transfers via VRCAsyncGPUReadback
                │
                ▼
Shader pass(es)
  ├─ read previous texture/state
  ├─ transform / simulate / render
  └─ write next RenderTexture / display texture
                │
                ▼
CPU-side follow-up
  └─ OnAsyncGpuReadbackComplete -> TryGetData -> UI / logic / networking
```

### Use the right tool

- Use **`VRCGraphics.Blit`** for full-screen or texture-space processing: minimaps, post effects, data transforms, helper/export passes, and texture feedback systems.
- Use **`VRCGraphics.DrawMeshInstanced`** when drawing the **same mesh many times** and GPU instancing is the right fit.
- Use **`VRCShader.SetGlobal*`** only for **shared world-wide shader state** that multiple shaders/materials need.
- Use **material instance properties** (`Material.Set*`) for **local system state** when only one material/CRT pipeline needs the value.
- Use **`VRCAsyncGPUReadback`** only when Udon actually needs GPU results on the CPU side.

### Hard rules

1. **Do not assume full Unity `Graphics` / compute-shader access.** Design around the documented VRChat surface area: `Blit`, `DrawMeshInstanced`, global shader setters, and async GPU readback.
2. **`VRCGraphics.Blit` requires a non-null destination `RenderTexture`.** Never plan on the Unity pattern that blits to a null backbuffer target.
3. **Quest / Android caveat for `Blit`:** either put `ZTest Always` in the shader **or** disable depth on the target `RenderTexture`, or the blit can fail.
4. **Cache `VRCShader.PropertyToID()` once during initialization** and reuse the integer ID afterward.
5. **Only `_Udon*` names (or the literal `_AudioTexture`) are valid for `VRCShader.SetGlobal*`.** Use a project-specific namespace such as `_UdonMySystem_*` to avoid collisions.
6. **Assume every global you set is visible to all shaders in the world, including avatar shaders.** Never use globals for secret or per-player-only data.
7. **Do not invent custom `_VRChat*` shader globals.** That namespace is protected; only use the documented ones.
8. **Do not rely on `SetGlobalInteger()` behaving like a true integer path.** Treat it as float-backed unless proven otherwise in the target setup.

### Shader-global guidance

Use the documented built-in VRChat globals directly in shaders when needed:

- Camera / capture context: `_VRChatCameraMode`, `_VRChatCameraMask`
- Mirror context: `_VRChatMirrorMode`, `_VRChatFaceMirrorMode`, `_VRChatMirrorCameraPos`
- Screen / photo camera transforms: `_VRChatScreenCameraPos`, `_VRChatPhotoCameraPos`, `_VRChatScreenCameraRot`, `_VRChatPhotoCameraRot`
- Time: `_VRChatTimeUTCUnixSeconds`, `_VRChatTimeNetworkMs`, `_VRChatTimeEncoded1`, `_VRChatTimeEncoded2`

Time-specific rules:

- Define `_VRChatTime*` values as **`uint`** in shaders.
- Prefer the SDK helper include when decoding time:
  `Packages/com.vrchat.base/ShaderLibrary/VRCTime.cginc`
- `_VRChatTimeNetworkMs` is for synchronization/offsets, not meaningful wall-clock display.
- Local-time globals describe the **observer's** local/preferred timezone, not the wearer/owner of the avatar.

### Async GPU readback pattern

Treat `VRCAsyncGPUReadback` as an event-driven pipeline:

1. Allocate an output array of the expected type/size.
2. Call `VRCAsyncGPUReadback.Request(...)` from an `UdonSharpBehaviour` / `UdonBehaviour`.
3. Handle `OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)`.
4. Check `request.hasError`.
5. Pull data with `request.TryGetData(...)`.

Guidelines:

- Prefer readback of **small, purpose-built textures or regions**, not full-resolution outputs every frame.
- Use readback for **control-plane data** (IDs, counters, sampled pixels, compact state), not bulk streaming.
- If the source texture format is awkward for CPU/Udon consumption, add a **helper/export shader pass** that repacks it into a readback-friendly texture first.

### Recommended architecture for stateful GPU systems

When a task looks like simulation, emulation, CA, GPU terminal state, or texture-based compute, use this pattern:

```text
previous state texture
        │
        ▼
Blit / CRT pass A: decode + simulate/update small hot state
        │
        ├─ optional additional passes or in-shader loops
        ▼
Blit / CRT pass B: commit / display / export
        │
        ├─ optional instanced rendering for visualization
        └─ optional async readback of a compact export texture
```

Design rules:

- Keep **hot state small** and update it often.
- Keep **bulk data** (large memory, maps, framebuffers) in separate textures.
- Prefer **feedback-loop textures / double-buffering / Custom Render Texture workflows** over CPU-managed per-pixel logic.
- Do not make Udon parse or iterate over large images every frame.
- If you need CPU visibility, build a **debug/export view** that exposes only the compact state you need.

### What to learn from `pimaker/rvc`

Treat `pimaker/rvc` as an **advanced reference architecture**, not as a drop-in prefab:

- It uses a **Custom Render Texture** plus Udon scripts to manage runtime.
- The main shader is split into at least **`CPUTick`** and **`Commit`** passes.
- Udon controls the system by setting **material properties** such as `_Init`, `_InitRaw`, `_DoTick`, `_Ticks`, `_UdonUARTInChar`, and texture bindings.
- Program images are supplied by swapping textures into material slots like `_Data_RAM_R/G/B/A`.
- A helper/display/export path repacks GPU integer state into a format Udon/Unity can read.
- This is a strong example of **texture-based persistent state + helper export passes + minimal CPU orchestration**.

### Performance / platform rules

- For **worlds**, custom shaders are allowed on Android/Quest, but design for performance first.
- Avoid unnecessary transparency on mobile/Quest.
- Cameras are permitted in worlds, but do not overuse them.
- Enable **GPU instancing** on materials that participate in instanced workflows.
- Prefer lower-resolution helper/export textures and lower readback frequency.

### Agent behavior

When asked to design or implement a VRCGraphics-based system:

1. First decide:
   - what data lives in textures,
   - what data lives in material properties or globals,
   - what (if anything) must come back to Udon.
2. Default to **material properties** for local control and **globals** only for deliberate world-wide broadcasts.
3. For CPU extraction, default to **`VRCAsyncGPUReadback`**, not legacy camera+`ReadPixels` hacks.
4. For Quest/mobile compatibility, check:
   - blit depth/ZTest setup,
   - shader cost,
   - camera count,
   - transparency,
   - readback frequency,
   - texture resolution.
5. If a design needs undocumented APIs or assumes arbitrary GPU buffer access, rewrite it into a **texture + pass + optional readback** architecture.
