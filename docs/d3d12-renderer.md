# Native Direct3D 12 renderer (#112)

Status: **step 4b, playing** – in the app behind the switch `SPARKIV_D3D12=1`.
Without it SparkIV draws with the WebView2/WebGL page (`src/SparkIV/Viewers/ModelView.html`) exactly as before.

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
| `MeshRenderer` | Root signature: 44 root constants (row-major view-projection, the instance's model-to-scene matrix, light direction + ambient, the part's material: cut-out, dirt mask) and a descriptor table with the texture (t0), a static anisotropic (16×) wrapping sampler. HLSL compiled at run time with DXC (`vs_6_0`/`ps_6_0`) to DXIL. Per model its meshes in one vertex buffer (position, normal, texture coordinates, vertex alpha: 36 bytes) and one 32-bit index buffer, copied from upload heaps into default heaps; a part per mesh (index range, texture, pass). Models are drawn as instances. Passes: solid (cut-out texels clipped at alpha 0.5), then decals (alpha-blended, no depth write, pulled forward by a depth bias; dirt decals' alpha = dot(texel, dirt mask) × vertex alpha) and see-through materials (alpha-blended, no depth write, not sorted yet). Glass and hidden planes left out for now. Lambert + ambient, only the first texture layer, both faces drawn. Draws into any target. |
| `TextureCache` | The textures of the models' `Textures` blobs, each blob made once (by reference), with a shader resource view in one shader-visible heap (65 536 views; number 0 is white). Blobs as the game has them ("SXT1", `ModelScene.Raw`): DXT1/3/5 straight as BC1/2/3, A8R8G8B8 as B8G8R8A8, L8 as R8 read as grey (view swizzle), with their mip levels as far as the data holds them (as the page's `uploadRaw`). PNG blobs (a cast's) decoded with System.Drawing, without mip levels. BC textures whose size isn't a multiple of 4 can't be made and are drawn white (counted). Uploads are sent in batches of about 256 MB. |
| `ModelSource` | What a model draws: its checked meshes (`ModelPart.CheckedMeshes`: the level of detail and the variation the page shows too), materials, textures, and whether it moves with bones. Skinned models get a second vertex stream (four bone numbers and weights, 32 bytes); the bones' matrices of each skinned instance are written every frame into a slot of a persistently mapped upload buffer (four slots, 65 536 matrices each) and read as a root SRV (t1); bone numbers are clamped to the instance's bone count. |
| `CutscenePose` (`Viewers/GtaIV/Pc`, GTA IV PC) | An actor's bones at any frame, the page's `sampledPose`/`findFollowers`/`gameHelper`/`worldMatrices`/`poseCast` in C#: the clip's tracks over the rest pose (positions linear, turns nlerp), helper bones following a tracked sibling, the ped helper bones by the game's rules (arm roll, upper-arm roll, forearm twist, neck roll, calf roll), the clip's model tracks placing a ped in the scene, removed bones (`[VEHICLE_REMOVAL]`, with what hangs on them) as zero matrices. Its input, `CutsceneMessages.CastMotion` (`Motions()`), is what `Section()` sends the page, taken out of it (the messages checked byte for byte before and after). Worn props (`WornProps()`) are fixed to their bone as for the page. |
| `OffscreenTarget` | Drawing without a window: a B8G8R8A8 texture and a D32 depth buffer of a given size; after a frame the colour is copied into a readback buffer (rows every 256 bytes) and saved as PNG (System.Drawing). |
| `CutsceneCamera` (`Viewers/GtaIV/Pc`, GTA IV PC) | A section's camera clip (camera_0) at any frame: the page's `filmCameraNow` in C# – positions linear, the turn by normalized linear interpolation, looking along its y with z up, FOV of the whole frame, not smoothed across a cut (> 25 cm or 5°). `ViewProjection`: near 0.1 m like the page. |

`D3D12Device`, `MeshRenderer`, `TextureCache`, `OffscreenTarget` and `CutsceneCamera` use no WinUI, so the visual tests' cutgen
compiles them in.
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

### Offscreen mode for the visual tests (step 2)

The visual regression run (local test tools, not in the repository) can draw every reference with the native
renderer instead of the page in headless Edge (`lauf.ps1 -Renderer d3d12`): cutgen loads the section's location as
before and, with `D3D12CAP=<frames>` (`D3D12SIZE=WxH`, `D3D12TAG`, `D3D12FOV` for a reference whose trace froze the
field of view), draws those frames into an `OffscreenTarget` at the reference's size and saves them as PNG. The
comparison with the game's picture is the same as for the page; the values are kept apart from the page's.

Drawn so far: the location (all placed objects but mirror-only far versions, their checked meshes only) through the
cutscene's camera, with its first texture layer (step 3), and the cast posed at the frame with its worn props (step
4a; `D3D12NOCAST=1` leaves it out), vehicles in their paint (`ModelSource.Tints`: a material's paint slot takes the
actor's colour, as the page's `paint` uniform). Until 4a every level of detail of the location's models was drawn at
once. Not yet: face shapes (a seam at Roman's neck in the intro), peds' hair (alpha as gloss, stipple + blend: Bernie's
hair comes out dark), the second paint layer, vehicle dirt and shine, animated textures, terrain layers, normal and specular maps, vertex colours, glass,
lighting, sky, water, post effects (steps 4b–5).
The differences are therefore large; what the run shows now is that framing, camera and geometry match the game.
First check (bc_1, 13 references): walls, door frames, pipes and furniture lie exactly over the game's, also at the
reference with a frozen FOV (s3 f994.50); a section with its location takes about 5–10 s. Full run (2026-11-04): all
72 references (bc_1, bc_3, et01_aa, et01_ba, intro) in 3 minutes (the page in headless Edge: over an hour); spot
checks indoors, outdoors with the skyline, TLAD and the intro's crane from above all lie over the game's picture.

## Measurements

| Date | Machine | Case | Result |
|---|---|---|---|
| 2026-11-04 | RTX 2060, 60 Hz, Debug build with the debug layer | `bc_1.cut`, Bernie (12 meshes, 14 889 triangles, on the GPU in 6.4 ms), 774 × 537 px, 33 s | 60 fps every second, mean 16.7 ms; longest frame mostly 16.7–18.9 ms, twice 33 ms (one refresh missed), single 24–27 ms; no debug-layer messages. UI over the panel works. |

### The harbour, offscreen (step 4b)

cutgen's `D3D12BENCH=<step>` draws every step-th frame of a section without saving it, waiting for each (poses,
recording and the GPU one after the other, so the worst case), with the debug layer off (`SPARKIV_D3D12_DEBUG=0`; on, it
checks each of the 16 000 calls and a frame takes some 300 ms), Release build:

| Date | Case | Frames | Mean | Median | p95 | p99 | Max | Draws a frame |
|---|---|---|---|---|---|---|---|---|
| 2026-11-04 | intro s4 (the harbour: 5 379 instances, 4.0 M triangles, 5 509 textures / 300 MB), 1920 × 1080, RTX 2060 | 2 049 (all) | 2.76 ms | 2.60 ms | 3.15 ms | 3.73 ms | 29.9 ms (frame 0) | 16 078 |

Only the first eight frames take longer (22–30 ms: the driver and pipelines warming up). The page took 34 ms and more
for the same harbour (#94), so the call submission, not the GPU, was what held it back. No culling yet: every
instance is drawn every frame.

### Playing in the app (step 4b)

`CutscenePlayer` (`Viewers/GtaIV/Pc`, GTA IV PC) is the `IRenderScene` `D3D12View` draws in the cutscene view. The view
stays in charge as with the page (sound, sections, locations by origin) and tells the player at each place it tells
the page: the location shown (`SetLocation`, also with the Location button), the section with its actors' motions and
camera (`SetSection`, made ahead on another thread like the page's section data), `Seek`, `Play`, `Sync` (the sound's
time, followed gently as the page does) and the camera button. The render thread takes those orders at the start of a
frame, keeps the clock as the page's `tick` does, poses the section's actors (the others hidden) and looks through the
section's camera (else it goes round the cast). It reports the frame it shows, its frames per second while playing and
when the rate falls below 30 or is back, as the page's `frame`, `fps` and `fpsBelow`, so the status bar and
`SparkIV-fps.log` work as before. For now a new location puts the whole scene onto the GPU again on the render
thread (a pause when a section at another origin starts); loading on a thread of its own comes next.

## Next steps (#112)

4. **Port the page's logic**, game-specific parts in their own module (as `gtaiv-pc.js` now): camera and animation
   (skinning), shaders (HLSL via DXC), sky, water, shadows, post effects (DoF, bloom, tone mapping), decals, coronas,
   room modifiers.
5. **Deferred lighting as in the game:** G-buffer, a light volume per lamp, no lamp cap (takes over #106).
6. **Switch** the cutscene view, then the model view; remove the WebView2 path once the visual run is not worse.
