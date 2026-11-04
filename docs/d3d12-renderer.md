# Native Direct3D 12 renderer (#112)

Status: **step 1, prototype** – behind the switch `SPARKIV_D3D12=1`. Without it SparkIV draws with the WebView2/WebGL
page (`src/SparkIV/Viewers/ModelView.html`) exactly as before.

## Why

Cutscene playback is bound by JavaScript and WebGL call submission, not by the GPU: the intro's harbour issues
3400–5600 draw calls a frame, each through JS → ANGLE → D3D11, and frames take 34 ms or more on an RTX 2060 (#94).
WebGL's uniform and texture-unit limits also block the game's deferred lighting (#106). The WinUI player UI (top bar,
timeline, status bar, dialogs) stays; only the drawing area changes.

## Architecture (`src/SparkIV/Rendering/D3D12/`)

| Class | Does |
|---|---|
| `D3D12Device` | DXGI factory, adapter by `GpuPreference.HighPerformance` (no software adapter), `ID3D12Device` at feature level 11.0, the direct queue and one fence (`Signal`/`Wait`/`WaitIdle`). Debug build only: debug layer and `ID3D12InfoQueue`, its messages written to `SparkIV-d3d12.log` beside `SparkIV.exe`. |
| `PanelSwapChain` | Composition swap chain (`CreateSwapChainForComposition`, flip-discard, 3 × B8G8R8A8, `FrameLatencyWaitableObject`, maximum latency 2) set on the WinUI `SwapChainPanel` through `ISwapChainPanelNative::SetSwapChain` (IID `63aad0b8-7c24-40ff-85a8-640d944cc325`, vtable slot 3). Buffers in pixels (`ActualSize × CompositionScale`), shown at `1 / CompositionScale` via `IDXGISwapChain2.MatrixTransform`. An RTV per buffer, one D32 depth buffer, per buffer a command allocator and the fence mark of its last frame. |
| `MeshRenderer` | Root signature with 20 root constants (row-major view-projection, light direction + ambient), HLSL compiled at run time with DXC (`vs_6_0`/`ps_6_0`) to DXIL. One model's meshes in one vertex buffer (position + normal, 24 bytes) and one 32-bit index buffer, both copied from an upload heap into default heaps. Lambert + ambient, no textures, both faces drawn. |
| `D3D12View` | Ties the panel to device, swap chain and renderer. Draws on its own thread, which waits on the waitable object before each frame, so frames go out at the refresh rate and the UI thread stays free (loading the cast doesn't stall the view). Size and DPI changes are recorded on the UI thread and applied at the next frame. Once a second: frames per second, mean and longest frame time into the status bar (`FpsText`) and the log. |

Frame: wait on the waitable object → apply size/DPI → wait for the buffer's last fence mark → reset its allocator and
the command list → barrier present→render target → clear, draw → barrier back → execute → `Present(1)` → signal.

### How the data gets to the GPU

`ModelScene.Meshes` (`SceneMesh`) already holds positions in model space (skinned meshes in the bones' rest pose),
normals and 16-bit indices per mesh. The prototype concatenates all meshes of the first loaded actor, rebasing the
indices into 32 bits, and copies them once. No JSON or base64 hop as with the page.

### In the cutscene view

`CutsceneView.xaml` has a `SwapChainPanel x:Name="Native"` next to the `WebView2`, below the spinner, message and
subtitle overlays. With the switch, `CutsceneView_Loaded` hides the WebView2, shows the panel and starts `D3D12View`
instead of the page; once the cast has loaded, the first actor with meshes is drawn, the camera going round it. All
page messages stay in place and go nowhere (`_pageReady` stays false). `Dispose` stops the render thread, takes the
swap chain off the panel and releases everything after the GPU is idle.

### Packages and licences

`Vortice.Direct3D12`, `Vortice.DXGI`, `Vortice.Dxc` 3.8.3 (MIT); `Vortice.Dxc.Native` brings `dxcompiler.dll` and
`dxil.dll` (DirectX Shader Compiler: Microsoft licence, LLVM licence, MIT), copied next to `SparkIV.exe`. Listed in
`licenses/THIRD-PARTY-NOTICES.txt`, texts in `licenses/DirectXShaderCompiler-*.txt`.

## Measurements

| Date | Machine | Case | Result |
|---|---|---|---|
| 2026-11-04 | RTX 2060, 60 Hz, Debug build with the debug layer | `bc_1.cut`, Bernie (12 meshes, 14 889 triangles, on the GPU in 6.4 ms), 774 × 537 px, 33 s | 60 fps every second, mean 16.7 ms; longest frame mostly 16.7–18.9 ms, twice 33 ms (one refresh missed), single 24–27 ms; no debug-layer messages. UI over the panel works. |

## Next steps (#112)

2. **Offscreen mode** for the visual test suite: render a frame to a texture, save it as PNG, compare the 72
   references against the new renderer from the first image on.
3. **Geometry straight from `ModelScene`** for the whole cast and the location: vertex/index buffers per mesh,
   textures (DXT straight from the WTD).
4. **Port the page's logic**, game-specific parts in their own module (as `gtaiv-pc.js` now): camera and animation
   (skinning), shaders (HLSL via DXC), sky, water, shadows, post effects (DoF, bloom, tone mapping), decals, coronas,
   room modifiers.
5. **Deferred lighting as in the game:** G-buffer, a light volume per lamp, no lamp cap (takes over #106).
6. **Switch** the cutscene view, then the model view; remove the WebView2 path once the visual run is not worse.
