# Native Direct3D 12 renderer (#112)

Status: **steps 1-6** – the cutscene, model and animation views draw natively by default. `SPARKIV_D3D12=0` still
draws them with the WebView2/WebGL page (`src/SparkIV/Viewers/ModelView.html`) as before; that path is to be removed.
(Elsewhere in this document "with `SPARKIV_D3D12=1`" means the native path, now the default.)

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

Frame (since 2026-11-05, see "The frame loop" below): serial - wait on the waitable object → apply size/DPI → update →
`Begin` (the buffer's last fence mark and the frame before last waited for, its allocator and the command list reset) →
record → `Submit` (execute, signal) → `Present(1)`; pipelined - the same with the wait on the waitable object moved to
just before `Present`.

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

Drawn so far: the location (all placed objects but mirror-only far versions, which only the water's mirror shows; their checked meshes only) through the
cutscene's camera, with its first texture layer (step 3), and the cast posed at the frame with its worn props (step
4a; `D3D12NOCAST=1` leaves it out), vehicles in their paint (`ModelSource.Tints`: a material's paint slot takes the
actor's colour, as the page's `paint` uniform). Until 4a every level of detail of the location's models was drawn at
once. Not yet (at 4a): face shapes (a seam at Roman's neck in the intro; ported 2026-11-05, see "Materials"), peds' hair (alpha as gloss, stipple + blend: Bernie's
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
section's camera (else the free view, below). It reports the frame it shows, its frames per second while playing and
when the rate falls below 30 or is back, as the page's `frame`, `fps` and `fpsBelow`, so the status bar and
`SparkIV-fps.log` work as before.

**The free view** (2026-11-04): without the cutscene's camera the player looks through `OrbitCamera`
(`Rendering/D3D12`, generic, for the model view later), the page's own camera ported (ModelView.html `camera` ~190,
`fit`/`fitDistance`/`eye` ~3019-3085, the mouse ~4062-4110, the keys ~4112-4146): left drag turns (a cutscene: looking
round from the eye), middle or right drag (Shift + left) moves the target in the screen's plane, the wheel zooms (x
1.15 a notch), a double-click or the Fit button fits the cast (its bones where they are now, 0.3 m round them; also once
at the first section), W/S/A/D/Q/E walk (3 m/s or the scene's radius if smaller, Shift x 4, steps of at most 0.1 s).
`CutsceneView` takes the panel's pointer events (captured) and its own keys (the view takes the focus on a press in the
panel; keys cleared when it loses the focus or the window is left; Ctrl/Alt/Win combinations ignored) and orders them to
the player (`BeginDrag`/`EndDrag`, `Wheel`, `Fit`, `Walk`). A mouse drag's moves are not taken from the UI thread's
pointer events (they come in bursts and a frame late): `CursorDrag` (`Rendering/D3D12`) reads the cursor (GetCursorPos,
physical pixels / RasterizationScale = DIPs, the page's CSS pixels) and Shift on the render thread right before each
frame's view is built, so each frame turns by the mouse's whole move since the last (touch and pen still by their
events). The wheel's deltas of a frame are summed into one 1.15 step, as the browser coalesces wheel events per frame.
The swap chain queues at most one frame (`PanelSwapChain.MaximumFrameLatency`, was 2; `SPARKIV_D3D12_LATENCY=2` for the
old value; logged at start), a frame less from mouse to screen.

**Deliberate departure from the page: smooth damping** (2026-11-04, the user's choice; native only, the page keeps its
direct feel). The controls are the page's, but they change `OrbitCamera`'s goal (yaw, pitch, distance, target), and what
is drawn follows it each frame (`OrbitCamera.Settle`, called by the player every frame, also while stopped; the render
loop draws every frame anyway) with frame-rate-independent exponential damping, a = 1 - exp(-dt / tau):
- turning: yaw and pitch, `TurnTau` 70 ms (half the turn shown after 50 ms, 94 % after 200 ms). Looking round from the
  eye keeps the shown eye still (the damped state keeps the eye, not the target, when the section has a camera;
  `PivotAtEye`), orbiting keeps the target;
- moving and zooming: the kept point and the distance (in log space, so the wheel's 1.15 steps ease evenly),
  `MoveTau` 90 ms;
- walking: the speed eases towards the keys' (3 m/s or the scene's radius, Shift x 4) with `WalkTau` 110 ms, in and
  out; released it stops under 7 % of the walking speed, about 0.3 s later (some 0.29 m run-out from 3 m/s).
Not damped: leaving the cutscene's camera (`LookFrom` snaps, so the free view starts exactly at the camera's view),
Fit (snaps), section changes (the free camera isn't touched). `SPARKIV_CAMERA_SMOOTH=<ms>` sets the turn's tau, the
others in proportion (e.g. 140: twice as soft); 0 turns it off, the page's direct behaviour. A press or a walking key through the cutscene's camera leaves it from
its eye and direction (the page's `leaveFilmCamera`, also when the camera button goes off) and the button goes off
(`CameraLeft`, the page's "filmCamera" message). Near clip as the page's free view (a 1000th of the distance, with a
location at least far / 2000 up to 10 cm), far plane the location's; no depth of field, fades or draw distance; the
sun's shadow, the lamps and coronas from the free eye.

Models go onto the GPU on a loading thread, one load at a time, while the frames go on (`MeshRenderer.Prepare`, any
thread, its copies in its own command list on the same queue; `Register`, the render thread, at the start of a frame):
the cast in one group, each location (by its origin's key, as the view keeps them: the shown section's and the next
one's, `PrepareLocation`) in a group of its own, dropped with `RemoveGroup` when the view drops it. The location asked
for is shown once it is on the GPU; until then the one shown before stays. Textures are counted by the models using
them and freed with the last (their views' slots used again). The fence is safe from several threads (an event per
thread). In this mode the view no longer makes the page's location messages (the harbour's took seconds and
gigabytes).

### Light and tone mapping (step 4c)

The page lights every cutscene with a location by one path (its "outdoor" one, inside too). The native renderer does
the same, drawing the scene's light into a float target (`ToneMapper.LightFormat`, R16G16B16A16) and making the
picture from it:

- **Solid light** (`MeshRenderer` pixel shader): `albedo x (around x AO + sun x saturate((n.l - 0.25) x 4/3))`, the
  light from around eased from above to below by the normal's z. AO from the vertex colours (a map object's or prop's
  red by day and green at night, by the day/night factor; a ped's red; a vehicle's luminance, which also tints it;
  trees 0.5; glowing materials 1; 0.45 for baked meshes without colours), times the actor's AmbientDownMult, then
  scaled by AoMultiplier (peds also by PedAoMultiplier). Glowing materials (gta_emissive*) laid over or added by their
  multiplier x the exposure (the night ones only at night and not in a room). Peds' and vehicles' normals turned to
  the camera; the back faces of map objects' and props' solids culled (counter-clockwise front, as in GL). Sun and
  light from around already include the exposure (`FrameLight`).
- **Where the light comes from** (`CutsceneLighting`, GTA IV PC): the time cycle of the weather and hour under the
  map's modifier boxes at the camera, inside a room the room's modifier, eased from the outside's by how much of the
  screen the room's portals cover (the page's `roomStrength` / `mixSky`: `CutsceneLighting.RoomStrength`,
  `TimeCycle.Mixed`); an actor's AmbientDownMult as the page's `actorAmbientAt`, by where its pose puts it.
- **Fog** (`FrameLight.Fog`, the page's `gameFog`, rage_postfx program 21): after the lamps a triangle over the screen
  blends the fog's colour over the solid scene by its amount at the G-buffer's place (not the sky); the see-through
  materials take it each in their pixel shader, the water its own. In a room with a near fog this is most of the look:
  em_4 in Elizabeta's flat (et01_aa) fogs 78 % towards a warm grey from 3 m on. The same pass's depth effect
  (gDepthFxParams) before it since 2026-11-04 (below, "Glows, coronas, light volumes, forward lamps and the depth
  effect").
- **The picture** (`ToneMapper`): a quarter-size copy of the light (each texel the mean of its 4 x 4 block) is read
  back for the mean luminance; the eye adapts to it (kept within AdaptedLumMin and AdaptedLumMax, eased over
  AdaptTime while playing, at once when stopped; in the app measured every 200 ms); the composite scales the light by
  MidGrey / the adapted luminance and applies saturation, colour shift, colour correction (x 2) and gamma on the
  luminance, as the page's post composite. No bloom, depth of field or grain yet.

The G-buffer of the plan comes with the lamps (step 5): for this light a forward pass gives the page's result exactly.
Not yet: the cutscene's own lights and the lamps (step 5), the sky (until then black, which also pulls the adaptation),
normal and specular maps, shine.

### The sky (step 4e)

`CutsceneSky` (GTA IV PC) draws the sky into the light target before the meshes, as the page's HDR sky path does it
(gta_atmoscatt_clouds, from the traces): the sky colour with the azimuth colours towards the horizon, the sun
(`TimeCycle.SkySunDirection`, higher than the light's sun), the clouds from skydome.wtd's noise, made each time their
Phase moves by a rage_perlinnoise pass into a 256 x 256 target (the Phase grows by CloudSpeed with the clock; in the
visual tests it starts at the trace's value, `CLOUDPHASE`). While the clock is held the Phase stays at its start, as the
page's `clockLocked` (ModelView.html ~2169-2203; since 2026-11-05 natively too, issue N4): the view hands its
`ClockLocked` ("Lock time", else the mission script holding the clock: the intro) to `CutscenePlayer.HoldClouds` with the
page's "lockTime" message; cutgen holds them where the script holds the clock (`CLOUDHOLD=0/1` sets it; the game's Phase
stays at 393.984 in every intro frame of GTAIV.3.trace). Below the horizon faded to the far fog colour on the game's
dome, and the near fog's share. The main view, the water's mirror (below, "The water's mirror") and the mirrored
surroundings' map (`toMap`, below). In the app the player draws it through `IRenderScene.DrawBackground`.

### The water (step 4f, first version)

`CutsceneWater` (GTA IV PC) draws the location's water between the solid meshes and the see-through ones
(`MeshRenderer.Draw`'s `afterSolid`, in the app `IRenderScene.DrawAfterSolid`): the water.dat quads with their vertex
colours, and outside the sea grid round the mission's marker (the script's, as the page's "water" message: else round
the camera) with the page's swell and ripple simulation and the wind the mission script forces (bc_3: 0; else the
page's breeze), the near field taken from a mask of the quads (their boxes, made on the CPU), Fresnel, the sun's glint
and foam, the game's fog, the mirror (below, "The water's mirror") and, outside, seeing through to what lies behind
(below, "Seeing through the water").

**The sea grid's colour is 0 0 0** (2026-11-04). The near field's foam is weighted by the vertex colour's blue
(`nearWeight = vColor.b x 0.25`, gtaiv-pc.js ~496). The page's grid has no colour attribute (`seaGridVao`, gtaiv-pc.js
~883-904: positions only), so its blue is WebGL's generic value and the grid gets no foam. Native gave the grid 0 0 1
(weight 0.25, taken as the game's). Measured at bc_3 s3 f140.5: the near water came out ~150/255 in native against ~100
in both the game's picture and the page's, the far water equal in all three; with 0 0 0 native matches them (mean
difference 13.52 -> 7.28). So native now follows the page. The game's real grid colour is still open (spec open
question 9). The water.dat quads keep their own corner colours, as on the page.

### The water's mirror (2026-11-04)

As the page's reflection pass (ModelView.html ~3896-3916; read in gtaiv-pc.js ~554-570 and ~583): outside, with a
water level, `CutsceneWater.MirrorOf` gives the view mirrored in the water's level (p' = (x, y, 2 level - z): the
mirror matrix times the view-projection, the eye mirrored likewise). `BeginMirror` makes the mirror ready, the page's
`reflectionTarget`: max(1, width / 2) x max(1, height / 2) of the picture, the light's format, drawn with
`MeshRenderer.MirrorSamples` (4) samples a pixel and a depth of its own (the page's, ModelView.html ~2404-2421),
cleared to 0. Into it `CutsceneSky.Draw` (`mirror: true`, its 4-sample pipeline) draws the sky from the mirrored view
without the near fog (the page's `drawSky(mirrored)`, `skyNearFog` 0, ModelView.html ~2265), then
`MeshRenderer.DrawMirror` the mirrored meshes (below). `EndMirror` resolves the samples into the texture the water
reads (its SRV, t5). The water then reads the mirror at
its own pixel shifted by the waves' slope (across and up the picture) x min(0.5 / (0.03 x view depth), 2), kept on
the water's side of the horizon (the point 2000 m ahead on the water's plane, + 0.05 x near + 0.005; Direct3D's v
grows downwards, so the page's up-shift and its "at most" turn round), and takes sky light x (1 - alpha) + the
mirror's light. The player draws it in `DrawAfterSolid`, cutgen in `WaterOf` (`D3D12NOMIRROR=1`: none, the sky's light
in the bounced direction as before). bc_3 s3 f140.5: 8.18 without, 7.28 with.

**The mirrored meshes** (`MeshRenderer.DrawMirror`, called between the sky and `EndMirror`; the page's
`drawMeshes(true, mirrored, mirroredEye)`):
- *What:* the instances added with `mirrored: true`, which are the location's far versions (`LocationObject.Far`, the page's
  `m.far`). The mirror-only far versions (`LocationObject.MirrorOnly`) are now instances too, with `mainView: false`:
  drawn only there, not in the main view nor any shadow map. No actors (skinned models are left out; actors are never
  far). Per part, as the page's `seen` per mesh (ModelView.html ~3272-3312): not layered terrain (`Part.Layered`: layer
  coordinates and a material with more than one layer), not wholly under the clip (the part's sphere top under it),
  not under 1 % of its distance across (radius / distance < 0.01), not wholly beyond the haze (the light's fog end, or
  nearer the location's reach: the page's `hazeEnd()`) nor beyond the shot's draw distance, all from the mirrored eye.
  The page's frustum test is left out (it only saves work: nothing outside the view lands in the picture).
- *Pixels* (`PSMirror`, the page's ModelView.html ~423-429 and ~494-505): nothing under the level less 0.515 m (the
  game's clip plane, `MirrorClipDepth`), nothing beyond the draw distance measured along the camera's own forward from
  the mirrored eye (the page's `drawReach` test with `eyeAt` mirrored and `toEye` not; that forward in the frame's
  `Params2.yzw`, the clip in `FogParams.w`). The surface as the main pass takes it (texture x tint, parallax, layers, a
  dirt decal's alpha, the cut-out test). Then lit as the game's ReflectTexture pass: the texture x (the light from
  around x AO + the sun x saturate((n.l - 0.25) x 4/3)) by the vertex normal, with AO from the vertex colours alone (a
  map object's red by day, green at night). No shadow, no shine, no lamps. The far fog over it by the distance from
  the mirrored eye. Written premultiplied by texture alpha x vertex alpha (nothing where that is 0).
- *States:* the solid parts first, opaque with depth. Then the see-through and decal parts, ONE / ONE_MINUS_SRC_ALPHA
  without writing depth (the page's glass pass). All with the back faces culled and the winding turned round (the
  page's `frontFace(CW)`, ModelView.html ~3677-3689: `FrontCounterClockwise = false`).
- *Constants:* a frame slot of their own, after the main ones (this frame's constants taken over), so it only runs
  inside `MeshRenderer.Draw` (its `afterSolid`).
- *Callers:* the player from `DrawAfterSolid`, cutgen from `WaterOf`. `D3D12NOMIRRORMESH=1` leaves the meshes out (and the
  mirror-only instances); `D3D12NOMIRROR=1` leaves out the whole mirror.

| Reference | page | sky mirror only | with the meshes (and see-through) |
|---|---|---|---|
| bc_3 s3 f357.25 | 6.69 | 11.73 | 7.42 |
| bc_3 s1 f78.75 | 8.28 | 7.87 | 6.97 |
| bc_3 s3 f140.5 | 5.65 | 7.28 | 7.29 |
| bc_3 s3 f829.5 | 8.65 | 10.54 | 10.54 |

The guards et01_aa s3 f49.75 (2.66), intro s4 f420.5 (3.83) and bc_1 s1 f144 (0.99) are unchanged.

Cost (bc_3 s3, cutgen D3D12BENCH=1, Release, debug layer off, 1038 frames, the water drawn as while playing:
`exact: false`):

| Run | mean | median |
|---|---|---|
| no water (`D3D12BENCHNOWATER=1`, the earlier bench) | 8.52 ms | 7.98 ms |
| water + sky mirror (`D3D12NOMIRRORMESH=1`) | 10.18 ms | 8.49 ms |
| + mirrored meshes | 10.47 ms | 8.62 ms |
| + seeing through | 10.63 ms | 8.70 ms |

So the mirrored meshes cost about 0.3 ms and the see-through about 0.15 ms. The water's ~10 frames over 33 ms are its
warm-up (20000 wave steps, 2000 per list). With `exact: true` (a still frame) the bench had 147 frames of ~145 ms: the swell
starting again whenever its window moves.

### Seeing through the water (2026-11-04)

As the page's water pass outside (ModelView.html ~3319-3340: `gameKeepSolidDepth`, then `waterBehind`; gtaiv-pc.js
~531-537 and ~606):
- *The copy:* before the water, `CutsceneWater.Draw` copies the scene's depth (`depthBuffer`: the depth resource,
  which `IRenderScene.DrawAfterSolid` and cutgen's `WaterOf` now pass) into a D32 texture of its own (read as R32F,
  t6). Outside only (`D3D12NOWATERBEHIND=1`: none).
- *How deep:* per pixel the scene's point there is taken back through the inverse view-projection (in the constants
  after `Mirror`). The water in front of it is its view depth less the water's (the page's linear depths; 1 where the
  scene is nearer or not known). That depth feeds the shore's normal bend (already there).
- *Alpha:* saturate((d x waterColour.w x 0.25)^0.25 + 0.125), premultiplied over the lit scene.

bc_3's harbour is deep: only the far shore line changes (f357.25 0.00, f78.75 +0.03, f140.5 +0.01).

Deterministic: the water at a section time t is a pure function of t, so a frame drawn alone holds the same water as
one drawn after others (cutgen's D3D12CAP lists in any order give byte-identical pictures). The wave texture is a
warmed-up base (the page's 20000 steps of 16 ms, kick 0.25; made once, the same for every location, kept on the GPU)
plus floor(t x 60) steps of 1/60 s from the section's start (at most the last 18000), the kick of the wind; the swell
starts flat at its window and takes floor(t / 0.02) steps of 0.02 s (at most the last 1500). Each step's random numbers
come from its index (SplitMix64), not from a running generator. A later time goes on from the state held, an earlier
one (or a new section) starts again from the base; at most 2000 wave steps per command list (the GPU's timeout), the
rest in the next frames (cutgen draws `CutsceneWater.StepsLeft` rounds first, at most 30; 0 inside, where the sea
isn't simulated). While playing (`exact: false`) a small step back of the sound's sync is held and a window moving
with the camera carries the swell on. The sun's glint and light on it x the sun's
shadow (the page's waterShadow: at the point a metre up, that metre x (1 - 0.0004 d²)), read from
`MeshRenderer.SunShadowMap` when it was drawn this frame (its own SRV, t4).

### The sun's shadow (step 5)

As the page's (gtaiv-pc.js `sunShadowCascades`, `shadowTarget`, `gameSunShadow`; ModelView.html `castBox`,
`shadowConvert`), wherever the game's light is (the page's `outside`, true for every cutscene with a location, inside
too: et01_aa's room gets no sun through its walls). `CutsceneSunShadow` (GTA IV PC, in CutsceneLighting.cs) lays out
the four cascades per frame from the camera (u: forward laid flat across the sun, v = u x sun; slices to 9.5, 20, 50,
175 m; each the box round its slice's corners; biases 0.14/0.24/0.5/0.75 m) into `FrameLight.SunShadow` (the generic
`SunShadow` class). `MeshRenderer.DrawSunShadow` draws each cascade before the G-buffer into a 4096² R32F work target
(cleared to -100, depth 0..1 from the height along the sun, LEQUAL): the map's objects' parts chosen as the page
chooses its casters (below; solid parts, then the parts marked `SceneMaterial.ShadowByAlpha` - GTA IV buckets 1-3, not trees or vehicles - blended
SRCALPHA/INVSRCALPHA by their texture's alpha), then the cast's non-see-through parts with MAX blending (the page's max
of map and cast), nothing culled or cut out; then the 2 x 2 mean into the cascade's 2048² quarter of an 8192 x 2048
R16F map (read by its number in the texture heap). `Shade` multiplies the sun (and the sun's shine on peds, vehicles,
the map) by `SunShadow(world)`: cascade by view depth, twelve taps 1/2048 apart, + 1.5 (d / 175)², none from 175 m; the
page's GL rows turned over so the same texels are read. Floating litter casts none (`AddInstance(castsSunShadow:)`).

**Casters** (the page's `drawMeshes` `seen` with `castBox`, ModelView.html ~3281-3312, and the loop ~3385-3407): per
part (the page's mesh; `Part.Center` / `Radius`, the sphere of its mesh's box, x the instance's largest scale), after a
first test with the whole model's sphere: across the cascade's box on u and v (`|centre·u - box.x| > 1/box.z + r`:
out), and not wholly farther from the camera than `SunShadow.CasterReach` - the page's `outside` test, nothing beyond
`hazeEnd()` (the time cycle's FarClip, here the light's FogEnd, or nearer the farthest location object across the
ground from the origin: `CutsceneSunShadow.LocationReach`, the page's `hazeReach`) nor beyond `drawDistanceNow()` (the
.cut's draw distance steps of the section, `CutFile.DrawDistanceSteps`; the app passes them with `SetSection`). The
page's other caster rules hold already: mirror-only far versions cast nothing (instances only for the water's mirror,
`mainView: false`),
floating litter casts none, hidden materials aren't in the models' parts (the page's skipping of water materials isn't
ported: the sea is `CutsceneWater`'s quads, not checked whether a map model has a water material). Its main pass's same distance
culling of whole objects isn't ported; beyond the draw distance the main pass cuts per pixel instead (below, "The
draw distance"). cutgen: `D3D12NOCASTERREACH=1` leaves out the distance test.

### The draw distance (2026-11-04)

GTA IV makes the .cut's draw distance the cutscene camera's far clip (0xD53280). The page cuts it off per pixel
(`drawReach`, ModelView.html ~408-409 and ~429: `dot(vPosition - eyeAt, -toEye) > drawReach` -> discard, the depth
along the view, not the distance; set at ~3200-3201 `reachNow = filmView() ? drawDistanceNow() : Infinity`, with
`drawDistanceNow` at ~2317-2323: the last of the section's draw distance steps at or before the frame). Native:
`FrameLight.DrawReach` (0: no limit), written to the frame constants' `FogParams.z`; `Shade` (the solid pass, the
decals and the see-through pass, MeshRenderer.cs) discards where the clip w is beyond it. Not cut, as on the page: the
water (`CutsceneWater`'s own shader), the sky, the sun's and the lamps' shadow maps. Only through the cutscene's camera:
`CutscenePlayer` sets it from `CutsceneSunShadow.DrawDistance(drawSteps, frame)` (factored out of `CasterReach`),
cutgen likewise (`D3D12NODRAWREACH=1`: no limit). bc_3: the skyline towers and the far bridge are gone as in the game
(s3 f829.5 25.27 -> 10.54, page 8.65; s1 f292.5 14.09 -> 9.37, page 5.15); et01_aa, intro and bc_1 unchanged.

Identity: the per-part u/v test only drops parts that draw no texel, so it can't change the picture - bc_3 s3 f829.5
byte-identical with `D3D12NOCASTERREACH=1`. With the distance test bc_3 s3 f829.5 (draw distance 89.2 m in that shot)
differs in 93 of 2.04 M pixels by 1 level (x 1704-1786, y 603-807, right side), the diff against the game unchanged at
25.27; et01_aa s3 f49.75 byte-identical (2.66); intro s4 f420.5 3.83 (no draw distance steps, its haze end beyond the
cascades: nothing culled there by distance).

Not safe for identity, so not done: leaving out an object lying wholly low along the sun (below -100 or below every
point the camera can see). A cascade's texel isn't max(clear, objects): the depth is cleared to the far end, so any
fragment replaces the -100; and the 2 x 2 mean into the map mixes a changed low texel with its neighbours, so even a
value below every receiver can move the mean across a receiver's height.

**Cost** (cutgen `D3D12BENCH=1`, Release, debug layer off, RTX 2060, all frames; the step-4b line above had no lights,
fog, materials or post effects yet; map objects per cascade: the bench's means a frame):

| Date | Case | Sun shadow | Mean | Median | p95 | p99 | Max | Draws a frame | Map triangles per cascade |
|---|---|---|---|---|---|---|---|---|---|
| 2026-11-04 | intro s4, 1920 × 1080 | off | 8.21 ms | 8.10 | 9.40 | 9.75 | 82.8 (f0) | 16 939 | - |
| 2026-11-04 | intro s4 | on, whole models by their sphere (commit 4d1b9f8) | 15.70 ms | 16.32 | 18.21 | 18.53 | 158.0 (f0) | 21 635 | 216 k / 249 k / 352 k / 748 k |
| 2026-11-04 | intro s4 | on, map objects kept (the page's cache; not kept, see below) | 15.75 ms | 16.74 | 18.64 | 18.94 | 209.4 (f0) | 21 291 | - |
| 2026-11-04 | intro s4 | on, casters per part + reach | 15.66 ms | 16.30 | 18.25 | 18.66 | 154.6 (f0) | 20 574 | 141 k / 175 k / 279 k / 685 k |
| 2026-11-04 | bc_3 s3 (harbour, outdoor), 1920 × 1061 | off | 5.66 ms | 5.17 | 7.10 | 8.34 | 131.6 (f0) | 11 019 | - |
| 2026-11-04 | bc_3 s3 | on, whole models by their sphere | 8.82 ms | 8.17 | 11.08 | 16.47 | 224.8 (f0) | 13 419 | - |
| 2026-11-04 | bc_3 s3 | on, map objects kept | 8.42 ms | 7.35 | 11.55 | 16.06 | 76.7 (f0) | 12 039 | - |
| 2026-11-04 | bc_3 s3 | on, casters per part (no reach) | 8.83 ms | 8.27 | 11.01 | 13.97 | 220.9 (f0) | 13 228 | 53 k / 55 k / 144 k / 296 k |
| 2026-11-04 | bc_3 s3 | on, casters per part + reach | 8.59 ms | 7.99 | 10.68 | 12.54 | 187.7 (f0) | 12 610 | 53 k / 55 k / 144 k / 190 k |

Where the ~7.4 ms (intro s4) go, by experiments since removed: the map objects into a 1 x 1 scissor (same vertices,
no texels) 10.9 ms, the map objects left out 9.4 ms, nothing 8.2 ms: some 4.8 ms are the map objects' texels (four
4096² R32F + D32 targets: bandwidth), 1.5 ms their vertices and recording (1.1 ms on the CPU), 1.1 ms the rest (cast,
clears, the 2 x 2 means, the taps in `Shade`). Culling takes vertices off (intro's map triangles 1.56 M to 1.28 M a
frame), not texels: whatever lies in a cascade's box is drawn there, as by the game. Drawing the map objects highest
along the sun first (early depth rejection) didn't help either (15.61 ms).

The page's cache of the map objects (kept per cascade at 4096, drawn again only when its `shadowKey` changes,
ModelView.html ~3819-3841) was tried and taken out again: in cutscenes the camera mostly moves and every cascade with
it, so the map objects were drawn again in 91 % of intro s4's frames and 65 % of bc_3 s3's (exact key; the page's
rounding of the boxes to three decimals lets the far cascade drift up to ~6 % before redrawing). Kept, a frame saved
~6 ms; redrawn, the separate target cost a little more; net -0.4 ms on bc_3, +0.2 ms on intro, for 256 MB more.

Unlike the page: its main pass's culling by draw distance and haze end isn't there; glowing glass (gta_glass_emissive,
left out of the models) casts nothing. cutgen: `D3D12NOSUNSHADOW=1` turns it off.

### Lights and lamps (step 5, first version)

The solid pass and the decals also write a G-buffer (albedo, normal + AO, place + whether baked; a decal blends only
the albedo, as the game's decals write GB0's colour only); the lights are then added over it, one light volume each
(a box round its sphere, its faces turned away from the camera only, depth test "greater or equal", one instanced
draw), before the water and the see-through materials, which are drawn forward (the map's ones with the lamps, below). No cap: the
page lit at most 80 forward (#106); the intro's harbour adds some 130 at once. `CutsceneLights` (GTA IV PC) picks them
per frame as the page's `sceneLightsNow`: the section's own lights (the 8 strongest; env*/shd* only on baked
surfaces, the others only on peds and vehicles), the tail lamps of vehicles whose lamps the script turns on, the
location's lamps on at the hour, of the rooms seen, in view, faded by FadeDistance - with the page's formulas (light
and filler families, AO terms, cones). The sun and the light from around stay exactly as before. Lamp shadows as the
page's: the 4 shadow lamps nearest to the camera (distance less radius; `CutsceneLights.LampShadowSlots`) get a slot in
an R32F atlas (2048 x 4096: a row of two 1024² halves per lamp), drawn each frame before the G-buffer from the cast's
solid parts (instances added with `castsShadow`; the location casts none), sampled in the light pass with the page's
four rotated taps (its GL rows and pixel y turned over, so the reads are the page's). Other shadow lamps stay fully
lit. Since 2026-11-04 also the map's see-through materials lit forward by the lamps, the lamps' coronas and visible
light volumes (below), and the lamps' shine on the G-buffer (below, round 4).

### Materials (step 4d)

All textures are read by number from `TextureCache`'s heap (an unbounded, volatile descriptor table, `Tex[]` in space
1); each model has a buffer of its parts' materials (`PartData`, root SRV t2: texture numbers, flags, specular, paint,
dirt, vehicle shine, subsurface), and models with tangents or terrain layers have a third vertex stream (tangent, layer
texture coordinates, layer weights; others read a shared zero buffer with stride 0). As the page does it: terrain
layers 1–3 by their alpha x weight; normal maps (with tangents the page's pedBumpedRaw, written to the G-buffer
rounded as the game stores it; without, the page's screen-space branch, which with its bumpiness 0 comes down to the
normal x sign(2 blue - 1)); parallax; map `*_reflect` environment texture; the sun's shine (pedShine deferred and
forward variants, flat specular, spec masks; vehicleShine for paint, mesh, tyre, chrome, glass and lamp glass; paint2's
second shine); gta_vehicle_paint3's second layer, vehicle dirt, lamp glass glowing by its dimmer while the lamps are
on (`MaterialLook`, filled by `CutsceneDrawing` from the actor as the page's scene message); skin subsurface; peds'
see-through parts (gta_ped, bucket 1) blended by the stipple's share of their alpha, no cut-out; glass (vehicle glass
and the map's glass with vertex alpha by their texture's alpha, culled, writing depth; other glass at 0.45) and the
map's other see-through materials (culled, writing depth, forward shine); the location's decals lit by the surface
below (its normal and AO copied after the solid pass), normal decals taking its AO.
Animated textures: not in cutscenes, on the page nor natively. The page's `uvAnim` (ModelView.html ~2906-2920, ~3662)
reads `clip.uv`, which only the animation view's "clip" message fills (`AnimationView.TextureMatrix`, a map object's
`_uv_N` clip; natively `AnimationPose.UvAt`, step 6); a cutscene's timeline clip has no tracks (`Cutscene.Section`), and
`CutsceneLocation` shows the IDE's animated objects (anim, tanm) at rest. The five tested cutscenes' locations hold such
objects (the intro's ship ET_ShipIn62, ET_ShipINHullF01, ET_ShipInMast02; Times Square's far LOD signs of manhat09 in
intro s5, bc_3 and et01_ba; BM_coinop01 in et01_aa); whether they move a texture is not checked. Native = page.

Face shapes (2026-11-05, `MeshRenderer.Shapes.cs`, the page's makeFace / blendFace, ModelView.html ~1091-1128 and
poseCast ~2721-2730): a cutscene ped's .wbs shapes on its head mesh (`ModelSource.Shapes`, from `CutActor.Faces` by
`CutsceneDrawing`: position / 4096, normal and tangent / 128), weighted per frame by the face clip's type-21 tracks at
the actor's clip frame, linearly between frames (`CutscenePose.FaceAt`), added to the vertices before skinning. The CPU
adds them as the page does into an upload ring (a slot per frame in flight) and copies the changed ranges into vertex
buffers of the instance's own (default heap; the model's vertices copied in once) at the start of `Draw`, only when the
weights changed; the instance is then drawn from those (main, lamp shadow and sun shadow passes). Without weights (a
section without the face's tracks) the model's own vertices; the page keeps the last blend there. Only actors with a .wbs
in cutsprops.img have them (seen: bc_1's Roman, bc_3's cs_Bernie_CraneC, the intro's CS_Hossan in s3, a face in et01_aa s3); Niko has none, as on
the page. cutgen: `D3D12NOFACES=1` leaves them out.
(The mirrored surroundings in the shine: below, "The
mirrored surroundings' map".) (Glowing see-through parts and glass, and the lamps on the forward-drawn map materials:
below.)

### Post effects (step 4h)

`ToneMapper.Composite` now does the page's whole composite (gtaiv-pc.js `gamePostEnd`): the quarter copy, made once a
frame (also when no measure is taken), is blurred across and down (the page's blur passes) into the bloom, added above
the bright pass (`ToneParams.Bloom` = IntensityBloom x 0.5, `BrightPass`); the depth of field reads the depth buffer
(`OffscreenTarget.DepthBuffer`, `PanelSwapChain.DepthBuffer`) with the cutscene camera's CameraDof track
(`CutsceneCamera.DofAt`, near and far per frame), the four rotated reads in Direct3D's texture space; TLAD's film grain
(`ToneParams.Grain` = column 10 / 64 x Exposure) from a 1024 x 1024 texture of random bytes (fixed seed); the fade to
black (`CutsceneMessages.SectionFade`, only through the cutscene's camera) last. The frame's DOF and fade come in
`PostParams` (`SceneView.Post` in the app). The lamps' coronas and visible light volumes are added to the light before
it (below).

### Glows, coronas, light volumes, forward lamps and the depth effect (2026-11-04)

Five pieces of the page that native lacked, each ported as the page does it (all in `MeshRenderer`, the lists from
`CutsceneLights`, GTA IV PC):

- **See-through glows drawn apart** (ModelView.html ~3622-3646, ~3700-3734; the shader ~264-266, ~700-719, ~734). A
  see-through or glass part of draw bucket 4 or 5 (the page's `mat.emissive`), textured (`Part.GlowApart`): its glow =
  emissiveMultiplier x exposure (a gta_emissivenight's outside a room x (1 - day/night)); drawn apart (`glowApart`) when
  the glow is over 0 or it is a see-through gta_emissivenight, and blended or a glowing glass (`glowGlass`: not night,
  glass or a see-through gta_emissivestrong). Its lit draw is left out in a room, for `nightGlass` and `glowGlass`;
  where it is drawn, a gta_emissive's lit colour goes x (1 - its alpha) (the page's mix with glow 0; kind bit
  `GlowLit`). The glow draw (`VSGlow`/`PSGlow`, `_glowPipelines`): texture x tint x vertex colour x glow x (1 - the
  fog's amount), premultiplied by the part's alpha, the zShift (+0.0004 strong, -0.0004 the others; GL's 2 x zShift on
  -w..w is this on 0..w), back faces culled, writing depth, LESSEQUAL, ONE / INVSRCALPHA for gta_emissive (glowOver),
  ONE / ONE for the others. gta_glass_emissive is no longer left out of the models (the page draws it). cutgen:
  `D3D12NOGLOW=1`. This drew the intro's 3D credits (csTitles*, gta_emissive bucket 5), bc_3's pier wall lamps and
  bc_1's globe lamps.
- **The see-through pass's order** (ModelView.html ~3352-3380): in the decal pass first the dirt decals and the decals
  of a location model with dirt decals (`Part.DirtFirst`, the page's `dirtFirst` / `dirtModel`), in the see-through
  pass first the glowing see-through parts (the page's `glowFirst`: in a room multiplier over 0, outside
  gta_emissivenight), each group in instance order (`DrawPass`'s two sweeps). Native drew the intro bunk room's wall
  dirt after the posters (ruskgallposts): the posters were black.
- **The fog's depth effect** (gtaiv-pc.js ~304-314, ~994; `FrameLight.DepthFx` = TimeCycle columns 48, 50, 51, 52): from
  DepthFx.z to .w the light desaturated towards DesaturationFar and x (luma / exposure)^(ramp x (GammaFar - 1)), before
  the fog. The solid scene's: the light target is copied (`_lightCopy`, t9 in the lamps' table; `Draw`'s
  `lightTexture`, `ToneMapper.LightTexture`) and `PSFog` writes the result whole; the see-through materials in
  `PSBlend`. cutgen: `D3D12NODEPTHFX=1`.
- **The lamps' coronas** (the page's `coronasNow`, ModelView.html ~2599-2620, and `gameCoronas`, gtaiv-pc.js
  ~1721-1825): `CutsceneLights.CoronasNow` - every 2dfx light with CoronaSize and Intensity over 0 (also those that
  light nothing), on at the hour, not of another interior nor an unseen room; in a room moved 1 m towards the camera,
  else its size, x SprBght outside; half a side CoronaSize x 2.5. `VSCorona`/`PSCorona`: a quad facing the camera
  (right, up, forward from the view-projection's columns), no depth test; nearer than its size x saturate(0.5 (f / size
  + 1)), else 12 Poisson points against the solid scene's depth (copied before the water and glass, `KeepSolidDepth`),
  collapsed at 0.01 or less; the sprite (coronas.wtd "corona", `SetCoronaSprite`) x colour x strength x exposure, ONE /
  ONE. The bindless texture table is now visible to all stages (the vertex program reads the depth). cutgen:
  `D3D12NOCORONAS=1`. In the intro's ship the cabin lamps' coronas lie over much of the picture: this was the warm
  light the native picture lacked (s1 f399.5, s3 f1236.5, s3 f49.5).
- **The lamps' visible light volumes** (the page's `lightVolumesNow`, ModelView.html ~2576-2597, and
  `gameLightVolumes` / `gameLightVolumeMesh`, gtaiv-pc.js ~1539-1710): `CutsceneLights.VolumesNow` (flag 0x80000,
  strength VolumeIntensity x Range, reach radius x VolumeSize, within reach + 35 m; without the filler flag by the
  lookup texture lights_occluders.wtd "volume", `SetVolumeLookup`); `LightVolumeMesh` the game's unit meshes (spot n 5
  with its fan, point n 4), `VSVolume`/`PSVolume` against the whole depth (copied after the see-through pass), the far
  faces adding, the near ones taking off (FrontCounterClockwise as the page's GL CCW), x colour x 0.25 x exposure. The
  scene's depth along the view from Direct3D's depth through the view-projection's z and w columns (w = b / (depth - k)).
  cutgen: `D3D12NOVOLUMES=1`. Only et01_aa s1 f463.25 has one in view among the references (3.29 -> 3.20).
- **Lamps on the map's see-through materials drawn forward** (the page's `forwardLit`, ModelView.html ~558-660,
  ~3497-3508): the parts the page draws forward (`PartFlags.ForwardLit`: blended map materials, not glass, decals,
  vehicles or emissives) read the frame's lights (root SRV t3, the count in `FogParams.w` once `DrawLights` wrote them):
  the location's lamps reaching more than 4 m (not the cutscene's own), colour x (a⁴ - 0.1) / 0.9 x spot x max(n.l, 0)
  onto the light, x saturate(n.l) x |l.R|^(power / 4) onto the shine. Bernie's glass table top (bc_1) is lit so.
  cutgen: `D3D12NOFORWARDLAMPS=1`.

Unlike the page: no cap of 80 lights for the forward lamps either.

Measured (mean difference to the game's picture, /255; cutgen one frame at a time, which equals the run's values; page
= run 2026-11-04_10-54, before = run 2026-11-04_20-14):

| Reference | page | before | glows + order | + depth effect, coronas | + volumes, forward lamps |
|---|---|---|---|---|---|
| intro s1 f399.5 | 1.19 | 9.95 | 7.87 | 1.21 | 1.21 |
| intro s3 f1236.5 | 3.55 | 10.85 | 9.63 | 3.76 | 3.76 |
| intro s3 f49.5 | 3.02 | 6.23 | 6.23 | 3.06 | 3.06 |
| intro s2 f352.75 | 4.52 | 7.25 | 4.16 | 4.09 | 4.09 |
| bc_1 s2 f1174 | 1.80 | 5.51 | 5.03 | 4.78 | 1.84 |
| bc_1 s2 f84.5 | 1.41 | 3.34 | 3.25 | 2.72 | 1.45 |
| bc_3 s1 f292.5 | 5.15 | 9.37 | 9.08 | 7.09 | 7.10 |
| et01_aa s4 f112.25 | 2.13 | 3.76 | 3.52 | 3.52 | 2.16 |
| guard et01_aa s3 f49.75 | 2.52 | 2.66 | 2.55 | 2.55 | 2.55 |
| guard intro s4 f420.5 | 2.97 | 3.83 | 3.12 | 2.97 | 2.97 |
| guard bc_1 s1 f144 | 0.97 | 0.99 | 0.99 | 0.99 | 0.99 |
| guard bc_3 s3 f357.25 | 6.69 | 7.42 | 7.42 | 6.49 | 6.49 |

All 72: sum -61.6 against 2026-11-04_20-14. Worse by more than 0.05 only bc_3 s3 f506.5 (+0.13, a pier lamp's corona
at the picture's right edge, near the camera: drawn by the page's rules, kept) ; intro s3 f569.75 +0.04 (the depth
effect), et01_ba f402 +0.02.

### The mirrored surroundings' map (2026-11-04)

The game's parabolic map of the surroundings (EnvironmentSampler / ParabSampler), as the page builds and reads it
(ModelView.html ~3750-3794 and drawMeshes' mapPass ~3287-3312; gtaiv-pc.js `parabolicTarget` ~1207-1233,
`parabolicVertex` / `parabolicFragment` ~115-145, `mirroredAround` ~324-342):

- *The map* (`MeshRenderer.DrawParabolic`, from `Draw` after the sun's shadow, before the G-buffer, when the caller
  hands the sky's drawing: `Draw`'s `parabolicSky` and `parabolicHaze`; in the app `SceneView.ParabolicHaze` and
  `IRenderScene.DrawParabolicSky`, outside = wherever the game's light is): 1024 x 1024, the light's format
  (A16B16G16R16F), four levels, cleared to 0. The sky first (`CutsceneSky.Draw(toMap: true)`: the direction back from the
  map's place p = d.xy / (1 + d.z), nothing outside its circle, no near fog), then the location's far versions'
  (`mirrored` instances) solid parts reaching above the camera, not under 0.003 of their distance across, within 330 m
  across the ground less their radius, not wholly beyond the haze or the shot's draw distance; both faces, writing
  depth, blended SRCALPHA / INVSRCALPHA with the map's alpha kept (`VSParabolic`: d.xy / (len + d.z), depth
  1 - 1 / (len + 1); `PSParabolic` = `MirrorSurface(toMap)`: nothing below the camera, the texture lit by the light
  from around x AO and the sun, the far fog, alpha x saturate((64 - distance) x 0.25)). Each smaller level the mean
  of the 2 x 2 above (`PSHalve`, the page's generateMipmap). The part tests use the parts' spheres: the page's mesh
  boxes' tops only decide which parts are drawn, and what lies below the camera is cut per pixel anyway.
- *Reading it* (`MirroredAround` in the shader, texture number in `Params2.z`, `GlobalScalarsW` = globalScalars.w in
  `Params2.y`, the clamped trilinear sampler s1): at 0.5 + r.xy / (2 (1 + saturate(r.z))), the page's GL rows turned
  over, level lod x the game's darkening (1, 0.8409, 0.6389, 0.4518). Added as the page's pedShine (map materials with a
  spec map or flat spec, peds: 10 x globalScalars.w x AO x Fresnel x the map along R + v x saturate(d x 0.02) at lod
  4 - 4 saturate(power / 230) x saturate(5 R'.z) x (1 - saturate(d / 100))) and vehicleShine (paint the same with the
  vertex colour's luminance; vehicle glass s² x lum x the map at lod 0 x saturate(5 R.z) x (1.6 f + 0.4) x 36 x
  globalScalars.w x reflectivity; badges and lamp glass x 18).
- Not ported: the page's (1 - map alpha) x the sky's colour (its `skyColor`/`skyTopColor` in picture units, unmapped)
  where the map's alpha is under 1 - only at the circle's blurred rim -, and its caching of the map (drawn every frame
  here). cutgen: `D3D12NOPARABOLIC=1`.

Measured (all 72, against the run with the glows, coronas and forward lamps): sum -19.3; e.g. bc_3 s1 f292.5 7.10 ->
5.20 (page 5.15), s2 f1307 7.45 -> 5.94 (5.92), s1 f854.75 5.17 -> 3.74 (3.66), s3 f829.5 10.13 -> 8.71 (8.65), et01_ba
f468 6.00 -> 4.80 (4.86) and every et01_ba reference -0.2 to -0.64 (the car paint and the booth's panels mirror the
lamps and the sky). Worse: intro s6 f712.5 +0.27, s3 f749.75 +0.18, s6 f469.75 +0.13 - each now at the page's value
(3.96, 2.18, 3.15 vs page 3.96, 2.17, 3.21): native was better than the page there without the map; bc_1 s1 f940 +0.14
(a small even lift; that frame's larger gap is Bernie's TV screen, white natively, dark on the page - not found),
bc_1 s1 f144 +0.04. Guards: et01_aa s3 f49.75 2.55, intro s4 f420.5 2.95, bc_1 s1 f144 1.03, bc_3 s3 f357.25 6.35.

**Cost of the map** (cutgen `D3D12BENCH=1`, Release, `SPARKIV_D3D12_DEBUG=0`, all frames; the bench now draws as a
captured frame does: water, coronas, light volumes and the map; `D3D12NOPARABOLIC=1` without the map):

| Case | with the map | without | the map |
|---|---|---|---|
| intro s4, 1920 x 1080, 2049 frames | mean 21.27 ms, median 21.27 | 20.44, 20.43 | +0.83 ms |
| bc_3 s3, 1920 x 1061, 1038 frames | mean 12.05 ms, median 10.13 | 11.40, 9.57 | +0.65 ms (median +0.56) |

Under a millisecond, so the page's cache (it draws the map again only when its key changes, ModelView.html ~3756-3759:
the eye to three decimals, the visible meshes' count, the draw distance and the haze end, or the sky) isn't ported:
the map is drawn every frame. (Compared with the earlier bench lines, intro s4 15.7 ms and bc_3 s3 10.6 ms, the rest of
the rise is the glows, coronas, volumes, forward lamps and the fog's depth effect.)

### Smaller fixes (2026-11-04, round 3)

- **Render targets and colours without a texture** (ModelScene.cs ~1128-1134 and the material message's
  `color = Color ?? (RenderTarget ? 0 : null)`, ~1608; the page's base colour when untextured, ModelView.html ~448):
  a part without a texture takes the material's own colour, a render target (script_rt: a TV switched off) black.
  Native drew them with the white default texture: Bernie's TV screen (PUB3Bern_GB_tv) was grey where the game and
  the page show black. bc_1 s1 f940 3.59 -> 3.27 (page 3.25).
- **The depth test less or equal** (ModelView.html ~3180-3184, the game's ZFUNC 4) for the main passes, the water's
  mirror and the mirrored surroundings' map: a surface laid on another with the same vertices is drawn over it. (No
  reference changed by it.)
- Full run (all 72, one frame each): sum -0.47, nothing worse; bc_1 s1 f940 3.59 -> 3.27, f459.5 1.45 -> 1.30.
  Native mean 3.345 (page 3.38).
### Decal offsets and the lamps' shine (2026-11-05, round 4)

- **Decals and blended materials pulled as the page pulls them** (ModelView.html ~3510-3530: `polygonOffset(-0.25, -4)`
  for a decal, `(-0.25, -2)` for a blended material that isn't glass): Direct3D's DepthBias -4 / -2 with
  SlopeScaledDepthBias -0.25 (the page's 24-bit depth step is 2^-24, this D32 float's for depths 0.5 to 1 too; the
  blended non-glass parts, `Part.Pulled`, get pipeline variants from index 16). Native pulled its decals by (-1, -16):
  in the intro's galley the floor's shadow decal `cargoflrsha` (gta_default, bucket 2), 1 mm under the rubber mat
  `mp_bada_mat` (gta_normal_spec km_genrubrmat), came through the mat and laid it black. Found with cutgen's
  `D3D12PICK=x,y` (the location's triangles under a pixel, nearest first: model, mesh, drawn, far / mirror-only, room,
  material, shader, texture, bucket, winding; with `D3D12LAMPDUMP=1` every lamp of the location with why it is taken or
  left). intro s3 f569.75 6.05 -> 4.29 (page 4.22); the full run: sum -1.83,
  nothing worse by more than 0.01.
- **The lamps' shine on the G-buffer** (the page's lampShine, ModelView.html ~558-660 with gtaiv-pc.js pedShine
  ~234-237: 2 x shineAmount x the lamp's light x saturate(L.R)^power, for the lamp family and the cutscene's lights,
  not the fillers): the solid pass writes the amount (2 x shineAmount: 0.5 x the spec map's channels x intensity
  on the map, 0.25 on peds) into the albedo target's alpha and the power (spec map alpha x specularFactor, x 0.75 on
  peds) into the place's w (1, or 2 where baked, + 4 x the power in 64ths); decals (`PSDecal`) keep the surface's
  amount x (1 - their alpha) (the page blends the lit decal over the lit surface); `PSLight` adds amount x light x
  saturate(L.R)^power, R the view mirrored by the G-buffer's normal. Bernie's wallpaper (gta_normal_spec) by the hall
  lamp, the bikers' leather jackets in et01_aa.

| Reference | page | before (round 3) | decal offsets | + lamps' shine |
|---|---|---|---|---|
| intro s3 f569.75 | 4.22 | 6.05 | 4.29 | 4.26 |
| bc_1 s3 f994.5 | 1.58 | 2.43 | 2.41 | 1.63 |
| et01_aa s1 f463.25 | 2.81 | 3.20 | 3.20 | 2.82 |
| et01_aa s4 f337.25 | 2.00 | 2.35 | 2.35 | 2.00 |
| guard et01_aa s3 f49.75 | 2.52 | 2.55 | 2.55 | 2.54 |
| guard intro s4 f420.5 | 2.97 | 2.95 | 2.95 | 2.95 |
| guard bc_1 s1 f144 | 0.97 | 1.03 | 1.02 | 0.98 |
| guard bc_3 s3 f357.25 | 6.69 | 6.35 | 6.35 | 6.35 |

Full run with both (all 72): the lamps' shine sum -2.76 against the decal offsets alone, worse at most +0.03 (intro s4
f258.5); native mean 3.281 (page 3.38). Left: et01_ba (+0.23 to +0.46: the booth's panes and the car's glass show less
of a mirrored shape than the page's), bc_3 s3 f140.5 +0.27.
- cutgen's `D3D12DEBUGVIEW=<n>` (`MeshRenderer.DebugView`, `Params2.w`): the solid and see-through passes show 1 the
  albedo, 2 the normal, 3 AO, 4 the light without albedo, 5 alpha, 6 the vertex colour, 7 the texture's texel, 8 flags
  (paint2, environment, layers), 9 the texture at level 0, 10 the texture coordinates. `D3D12NODECALBELOW=1`, and the
  diagnostics `LOCMATS=<text>` (the location's materials) and `GLASSINFO=1`.

### The cast before the location (2026-11-05, round 5)

The page draws its meshes in the order of its visible list, the cast's meshes first, then the location's
(CutsceneView.xaml.cs PostVisible: `_castVisible.Concat(_locationVisible)`; ModelView.html's passes go through
`visible` in that order). Native drew its instances in the order they were added, the location first. In the
see-through pass that matters where a cast's glass lies behind a map's glass that writes its depth: et01_ba's car
windscreen behind the toll booth's pane (BM_E1_tollbooth01, gta_glass_spec) was rejected by the pane's depth, so the
windscreen's milky shine was missing through it. `DrawPass` now draws, within each of its sweeps, the instances of
actors' models first, then the rest.

| Reference | page | before | after |
|---|---|---|---|
| et01_ba f1118.25 | 4.28 | 4.74 | 4.29 |
| et01_ba f220.25 | 4.00 | 4.30 | 3.98 |
| et01_ba f402 | 3.74 | 4.06 | 3.75 |
| et01_ba f706.75 | 3.79 | 4.09 | 3.79 |
| et01_ba f468 | 4.86 | 4.80 | 4.86 |

All 72: sum -1.32, nothing else changed; native mean 3.263 (page 3.38).

Film grain (TLAD): its strength is the game's - the high-frequency energy (mean |pixel - its 3 x 3 mean|) over a flat
part of et01_ba f1118.25 is 2.87 in the game's picture, 2.79 on the page, 2.86 natively; without it native's value
there is 3.91 instead of 4.29. So the grain costs page and native alike (random against random) and isn't a gap of
its own.

Left above the page (native > page + 0.05) after this round, with what was found:
- et01_ba f1394.75 (+0.23): the toll keeper's helmet is much lighter natively (at (1320, 440): game 47, page 4d,
  native a2). Without the environment texture (cutgen `D3D12DEBUGVIEW=99`) and the mirrored surroundings' map it is 36
  (darker than both); each alone lifts it to 67 / 76. So the helmet takes its *_reflect environment and the map's shine
  both, where the page shows much less; not found why within the time limit.
- bc_3 s3 f140.5 (+0.27), bc_3 s1 f854.75 (+0.08), s3 f829.5 (+0.07): the difference against the page lies in the
  water's wave pattern (the swell and ripple simulation's state differs from the page's, both deterministic) - not
  ported further.
- intro s2 f682.5 (+0.11): native lights the ship's cabins seen through its windows (137 lights in that frame); the
  game's picture shows them dark. With the page's cap of 80 lights (`D3D12LIGHTCAP=80`) native gives the page's 1.98
  exactly: the page is closer only because its cap drops those lamps by distance. What the game does (its interior
  lamps from outside) is not known, so neither the cap nor a guessed rule is taken over.
- et01_ba f1292.25 (+0.09): not analysed.

### Worn props with their vertex colours (2026-11-05, round 6)

`CutsceneMessages.WornProps` (the worn props' meshes for native) copied only positions, normals, texture coordinates,
alpha and the bone binding; the page's prop message is the whole mesh message with only positions, normals, bones and
weights replaced (Cutscene.cs `Section()`, ~513-540). Native's props therefore had no vertex colours: a ped's AO is its
vertex red, so the toll keeper's helmet in et01_ba (CS_DeSean's prop `p_head_000`, gta_ped_reflect) took the whole light
from around, its *_reflect environment texture and the mirrored surroundings at AO 1 (at (1320, 440): game 47, page 4d,
native a2). Now the colours, tangents (unmoved, as the page's), layer coordinates and weights come along. et01_ba
f1394.75 4.25 -> 4.04 (page 4.02), f1292.25 3.27 -> 3.25 (page 3.18).

### Gaps counted as noise (decided 2026-11-05)

- **bc_3's water** (s3 f140.5 +0.27, s1 f854.75 +0.08, s3 f829.5 +0.07 against the page): the grain-free difference
  (8 x 8 block means of native minus page, `lowdiff.ps1`) lies almost wholly in the water, as a wave pattern: both
  simulations are deterministic (the page's value 5.65 in every run since 2026-11-02), but their states differ (native's
  warmed-up base and stepping per section time, see "Seeing through the water"). Mean signed difference -0.5, mean
  |difference| 1.6 at s3 f140.5, the figures and the pier equal.
- **Film grain** (TLAD): equal strength (see round 5), random on both sides.
- **intro s2 f682.5** (+0.11 after round 6, +0.05 after round 7): see "The game's lamp list" below.

Full run after round 6 (all 72): sum -0.28, native mean 3.259 (page 3.38). Above the page by more than 0.05: bc_3 s3
f140.5 +0.27, s1 f854.75 +0.09, s3 f829.5 +0.07 (water, noise), intro s2 f682.5 +0.11 (above), et01_ba f1292.25 +0.07
(open: the grain-free difference lies in the far bridge's fine detail and the man in front, signed mean 0.00).

### The game's lamp list (2026-11-05, round 7)

Instead of the page's 80-light cap, the lamps the game draws were read from `GTAIV.3.trace` (apitrace dump of single
frames, one line per deferred light draw) and matched with native's (`D3D12LAMPDUMP=1`, `D3D12LIGHTINFO=1`). Details,
call numbers and the open points in [2dfx-lights.md](GtaIV/Pc/formats/2dfx-lights.md#interior-lamps-seen-from-outside-and-flag-0x10000-gtaiv3trace-2026-11-05).
In f682.5 the game draws 74 lights, native drew 137; every game light has a native one.
- **2dfx flag 0x10000** (25 lamps of the intro's ship): never drawn by the game. `CutsceneLights` leaves them out
  (cutgen `D3D12KEEP10000=1` keeps them). f682.5 2.09 -> 2.03 (page 1.98).
- **Rooms seen from outside: observed, not ported (contradicted by f863).** Only rooms behind an outside portal on
  screen fits f682.5 (bridge yes, below deck no), but in f863 the game also drops room 0's and most of room 1's lamps.
  Native keeps every room as seen from outside (as before); the portal rule is behind cutgen
  `D3D12OUTSIDEPORTALROOMS=1` for study (no measurable effect on any reference).
- Open: what drops interior lamps from outside (f863); room 0 seen from room 3 (s3 f569.75); the game draws fewer of
  the cutscene's own lights than native's 8 (s3 f569.75: 3); it culls lamps outside the view (no visible effect).

### et01_ba f1292.25 (+0.07 against the page, round 8)

Native 3.25 (run 2026-11-05_13-00: 3.24), page 3.18; the signed mean is equal (+0.332 both against the game), the gap
stays at 4 x 4 and 8 x 8 block means (0.067 / 0.054), so it is shapes, not grain. By 32-pixel tiles it lies in:
- **The far bridge's suspender cables** (x 256-512, y 352-480, ~0.02 of the 0.066): thin lines at 500-540 m
  (`mhb_cable04_bkn`, gta_spec `mh_bkn_cablewhite02`), darker and harder in native (grey sd 18.6, page 17.4, game 16.6).
  The page draws its scene with 4 samples a pixel (gtaiv-pc.js ~1378-1392, `renderbufferStorageMultisample`), which
  thins sub-pixel lines; the game draws without: `GTAIV.2.trace` call 3, CreateDevice `MultiSampleType =
  D3DMULTISAMPLE_NONE`, no multisampled render target in calls 0-200000; its edges show stair steps as native's (the
  man's head at (640-710, 270-330)). Native keeps one sample, as the game.
- **A gap under the bridge** (x 480-560, y 500-545, ~0.009): game and page show far buildings, native showed the haze.
  `D3D12PICK` (which now also prints the pixel's ray) finds the far city's `SuprLOD_Ks_01` / `_02` there at 1100-1200
  m, beyond native's far plane of max(1000 m, draw distance x 2). The page's film far plane is max(sceneRadius x 8 +
  100, backgroundReach x 2), backgroundReach = 1.8 x the largest |coordinate| of the location's meshes (ModelView.html
  ~1037, ~3102). Ported: `CutsceneDrawing.FarClip` (the larger of both; player and cutgen; `D3D12FARNOREACH=1` the old
  plane). et01_ba f1292.25 3.25 -> 3.24; the gap now shows the far buildings as game and page.
- The rest spreads over the man in front and the edges (sub-pixel differences of thin geometry).
Not changed: the cables' gap comes from the page's smoothing, which the game hasn't (accepted, not ported).

Full run after round 7 (all 72): only f682.5 changed (-0.06), native mean 3.258. Above the page by more than 0.05:
bc_3 s3 f140.5 +0.27, s1 f854.75 +0.09, s3 f829.5 +0.07 (water, noise), et01_ba f1292.25 +0.07 (open).

## Profiling (2026-11-05)

`GpuProfiler` (`Rendering/D3D12`, generic) times each pass: a timestamp query heap (8 frame slots x 512 queries) and a
persistently mapped readback buffer; `Begin(list, name)` / `End(list, name)` put a timestamp into the command list
and note the CPU's clock (the recording between the marks) and, where the caller passes its counter, the draw calls
the pass adds. Names may nest and repeat (a name's spans in a frame are summed: the four cascades, the coronas' depth
copy and their draw). At the end of the frame the slot's timestamps are resolved into the readback buffer; they are
read once the GPU has passed the frame's fence mark (a frame or two later, never waited for; a frame whose slot is
still busy is not recorded), converted with the queue's timestamp frequency. `Cpu(name, ms)` adds a stage of the render
thread, `Count(name, n)` a number of the frame. The profiler lives on `D3D12Device.Profiler`: null unless profiling is
on, and every mark is `profiler?.Begin(...)`, so with it off nothing is recorded (no queries, no clock reads). Pictures
are byte-identical with and without (bc_3 s3 f357.25, cutgen D3D12CAP with and without `D3D12PROFILE=1`: same SHA-256,
diff 6.35).

**Switches:**
- App: `SPARKIV_D3D12_PROFILE=1` (with `SPARKIV_D3D12=1`). Once a second, after the fps line, a block into
  `SparkIV-d3d12.log` beside `SparkIV.exe`: per pass the GPU ms (mean over the second's frames, then the max), the CPU's
  recording ms between its marks, the draws a frame; then the CPU stages (mean, max): `wait (frame latency, VSync)` (the
  waitable object), `scene update` (with its parts `update: poses`, `update: camera, light, cascades`, `update: lights
  list`, `update: coronas + volumes lists`), `wait for the buffer's fence`, `recording` (from the opened list to the
  composite), `close + execute`, `present`; and the draws of the main view and maps and of the water's mirror.
- cutgen: `D3D12PROFILE=1` with `D3D12BENCH=<step>`: the same table after the bench, mean and p95 over all frames (frames
  without a pass count as 0). With `D3D12CAP` (pictures) the one frame's table (scene list and composite list).
  `D3D12BENCHPIPE=1`: the bench as the app's pipelined loop (sent, not waited for; see "The frame loop").

**Reading it:** `frame (GPU total)` is the GPU time from the frame's first to its last mark; the passes indented under
another (`  sun: ...`, `  sky (into the map)`, `  sky (into the mirror)`, `  water: mirror meshes`) are inside it and
not to be added again. The passes, in drawing order: `sky`, `G-buffer clear`, `lamp shadow atlas`, `sun shadow` (clear,
map objects = phases 0 and 1, cast, convert = the 2 x 2 mean into the map), `parabolic map`, `G-buffer solid` (the sun
and the light from around are lit here, in the solid pass's shader, with the sun's shadow taps), `decals` (with the copy
of the surface's normal), `lamps (light pass)` (one instanced draw of all light volumes), `fog + depth effect` (with the
light's copy), `coronas` (the solid depth's copy and the sprites), `water: mirror` (its sky and meshes and the
resolve), `water: draw` (the depth copy, the quads, the sea grid; the swell on the CPU), `water: sim` (the wave
texture's steps on the GPU), `see-through + glows` (the glows are drawn interleaved with the lit see-through parts, so
not apart), `volumes`, `post: quarter copy`, `post: measure copy`, `post: bloom blur`, `post: tone map + DOF + grain`
(one shader). In the bench, `close + execute + GPU wait` is the wait for the GPU (the bench draws one frame at a time):
bench frame ~ update + recording + that. In the app CPU and GPU overlap (one frame queued), so a frame takes about
max(CPU, GPU), CPU = update + recording + close/execute + present.

**Measured** (cutgen `D3D12BENCH=1 D3D12PROFILE=1`, Release, `SPARKIV_D3D12_DEBUG=0`, 1920 x 1080, RTX 2060, all frames
of the section; GPU ms mean / p95, the recording's CPU ms mean, draws a frame):

| Pass | intro s4 (2049 frames) | bc_3 s3 (1038) | bc_1 s1 (1076, inside) |
|---|---|---|---|
| **frame (GPU total)** | **14.37 / 15.89** | **8.21 / 8.57** | **8.62 / 9.42** |
| sky | 0.18 / 0.20 | 0.16 / 0.17 | 0.07 / 0.07 |
| lamp shadow atlas | 0.13 / 0.18 (830 draws) | 0.05 / 0.07 (215) | 0.02 / 0.02 (40) |
| **sun shadow** | **6.01 / 8.47** (rec 1.21, 3635) | **2.09 / 3.12** (rec 0.58, 1581) | **4.50 / 5.08** (rec 0.31, 1316) |
| &nbsp;&nbsp;clear | 0.14 / 0.13 | 0.12 / 0.12 | 0.12 / 0.11 |
| &nbsp;&nbsp;map objects | 4.68 / 6.58 (3199) | 1.23 / 2.11 (1457) | 3.71 / 4.22 (1232) |
| &nbsp;&nbsp;cast | 0.42 / 1.36 (432) | 0.11 / 0.21 (120) | 0.04 / 0.05 (80) |
| &nbsp;&nbsp;convert | 0.76 / 0.84 | 0.63 / 0.82 | 0.62 / 0.64 |
| parabolic map | 0.46 / 0.69 (709) | 0.33 / 0.64 (241) | 0.05 / 0.05 (0) |
| **G-buffer solid** | **2.55 / 3.07** (rec 1.79, 14 921) | **1.95 / 2.38** (rec 1.05, 10 375) | **1.29 / 1.77** (rec 0.17, 2911) |
| decals | 0.16 / 0.24 (223) | 0.09 / 0.12 (72) | 0.23 / 0.30 (71) |
| **lamps (light pass)** | **1.85 / 2.72** | **0.82 / 1.66** | **1.74 / 2.19** |
| fog + depth effect | 0.28 / 0.32 | 0.27 / 0.31 | 0.32 / 0.32 |
| coronas | 0.07 / 0.08 (391) | 0.10 / 0.13 (47) | 0.07 / 0.08 (5) |
| water: mirror | 0.87 / 1.11 (meshes 0.80, 7468 draws, rec 1.13) | 0.14 / 0.23 (564) | - |
| water: draw | 0.09 / 0.15 | 0.20 / 0.42 | - |
| water: sim | 0.94 / 0.17 | 1.65 / 0.16 | - |
| see-through + glows | 0.41 / 0.91 (1164) | 0.06 / 0.13 (396) | 0.04 / 0.07 (72) |
| volumes | 0.08 / 0.13 | - | - |
| post (quarter, measure, bloom, tone map + DOF + grain) | 0.27 / 0.28 | 0.28 / 0.28 | 0.27 / 0.28 |
| CPU: update (poses, camera/light/cascades, lights, coronas + volumes) | 0.83 (0.19, 0.55, 0.08, 0.02) | 0.66 | 0.66 |
| CPU: recording | 6.30 / 6.87 | 3.01 / 3.37 | 0.78 / 1.18 |
| draws a frame (main + maps / mirror) | 21 881 / 7 468 | 12 929 / 564 | 4 417 / 0 |
| bench frame (serial) mean / p95 | 21.78 / 23.48 | 12.10 / 12.46 | 10.26 / 11.30 |
| overlap estimate max(CPU, GPU) | ~14.4 ms (GPU) | ~8.2 ms (GPU) | ~8.6 ms (GPU) |

`water: sim`'s mean is its warm-up (the first ~10 frames step 20 000 waves); its p95 is the steady cost. All three are
GPU-bound: the CPU's part (update + recording, ~7.1 / 3.7 / 1.4 ms) is about half the GPU's or less. The intro's
harbour holds 60 Hz on average (14.4 ms) with little room (p95 15.9 ms). What dominates: the sun shadow's map objects
(texels into four 4096² R32F + D32 targets: 4.7 ms in the intro, and 3.7 ms in bc_1's room, where the sun doesn't
reach), the G-buffer solid pass, the lamps' light pass (one instanced draw, fill rate), and outside the water's mirror
(intro) and the shadow's convert (~0.6-0.8 ms, four 2048² means).

## The frame loop, the dark sun and parallel recording (2026-11-05)

What the profile of the app showed (Release, 1610 x 818, intro harbour, `SparkIV-d3d12.log` 14:53:29 against 14:53:36):
recording 6.1 ms + GPU 8.7 ms = 14.8 ms gave 60 fps; recording 6.7 ms + GPU 12.8 ms = 19.5 ms locked to exactly 30 fps,
with 25.8 ms in the waitable object's wait. With at most one frame queued (`MaximumFrameLatency` 1, for the camera's
feel) the loop waited first and recorded after, so the CPU's and the GPU's parts ran one after the other; each alone was
well under 16.7 ms.

### The frame loop (`D3D12View`, `FramePacer`, `PanelSwapChain`; generic)

`PanelSwapChain.End` is split into `Submit` (close, execute, signal: the GPU starts) and `Present`. `FramePacer` decides
each frame where the loop waits on the waitable object:
- **Serial** (as before): wait → update (the mouse read right before the view is built) → record → submit → present. The
  input is shown at the next refresh: **1 refresh of latency**. Only while the CPU's part (update to submit) and the GPU's
  fit in a refresh.
- **Pipelined**: update → record → submit → wait → present. The frame is updated and recorded while the GPU still draws
  the last one, so the rate is set by the larger part, not their sum. The input read right after one refresh is shown two
  refreshes later: **2 refreshes of latency**, the least possible once CPU + GPU take longer than a refresh (serial
  would show it after 2 refreshes too, but at 30 fps).
- The choice: serial while the costliest CPU + GPU of the last 30 frames stays under the refresh less 1.5 ms (one frame
  over it switches at once); pipelined until 30 frames in a row are under the refresh less 3 ms. The refresh from DWM
  (`DwmGetCompositionTimingInfo`, read once a second; 60 Hz when not known); the GPU time from two timestamps per
  frame in the swap chain's list (`PanelSwapChain.LastGpuMs`, a few frames old). `SPARKIV_D3D12_PACING=serial` or
  `pipelined` fixes the mode; `SPARKIV_D3D12_LATENCY=2` still raises the queue.
- Before: 1 refresh while it fit, else 30 fps with 2 refreshes. After: the same 1 refresh while it fits, else 60 fps
  (while the GPU alone fits) with 2 refreshes.
- The log's fps line says `serial` or `pipelined`; with `SPARKIV_D3D12_PROFILE=1` a `pacing:` line gives the frames
  pipelined, the refresh, CPU and GPU a frame, their sum, the frame time and how much of the sum overlapped.

**What the CPU writes while the GPU reads** (all checked): `PanelSwapChain.Begin` (and `OffscreenTarget.Begin`, now two
allocators) waits for every frame but the last one sent, so at most one frame is on the GPU while one is recorded.
Every per-frame buffer the CPU writes is a ring of at least two frames: `MeshRenderer`'s frame constants (main, mirror,
map), palette and lights (4 slots, one each a frame), its recorders' allocators (4 slots), `CutsceneSky`'s constants (8
slots, three draws a frame), `CutsceneWater`'s ring (4 slots, one a frame); the tone mapper's measure and the
profiler's readback are read only after their fence mark; the water's simulation, the sun's, lamps' and the map's
targets, G-buffer and depth are written and read by the GPU only, in queue order. Instance matrices, materials and
lights per draw go as root constants in the list.

### The sun's shadow where the sun is 0 (`MeshRenderer.Draw`)

Every read of the sun's shadow map is multiplied by the sun's colour (the frame's `Sun`: `LitBy` and the three shine
terms in `Shade`, the water's glint, light and foam; the mirror's and the map's light read no shadow). `Sun` is
`TimeCycle.Sun x SunMultiplier x Exposure` (`CutsceneLighting.LightOf`), eased by the room's strength. Where it is
exactly (0, 0, 0) the map's value can't reach a pixel: without the map `SunShadow` is 1 and 0 x 1 = 0 x anything the map
holds (also through FMA: a 0 product adds nothing). So then the cascades (map objects, cast, convert) are left out and the
map isn't bound - byte-identical (bc_1 s1 f704 and f884, sun 0: same SHA-256 as with the shadow drawn,
`D3D12KEEPDARKSUN=1`). This happens in bc_1 s1 in 360 of 1076 frames (f569-f928, Bernie's flat with the room's modifier
whole); bc_1 s1 f144 has sun 0.016 (room strength 0.96), et01_aa's room sun 25 (dark only through the walls' shadow: no
exact condition there, the map is needed), the outdoor sections never. Nothing approximated.

### Parallel recording (`MeshRenderer`, generic)

The heavy passes are recorded on worker threads (`Task.Run`) into direct command lists of their own while the render
thread records the rest: the four sun cascades (a list each: clear, phases, convert), the mirrored surroundings' map's
objects, the solid pass (four lists of consecutive draws: the pass's sequence of visits sweep x group x instance split
in four), the decals, the see-through pass (two lists). Each list sets every state it needs. Where its pass is drawn,
`Splice` closes the frame's list, sends it with the workers' lists after it in one `ExecuteCommandLists` (in order),
and opens the same list object again on an allocator of the renderer's (allowed as soon as it is sent), so the callers
go on unchanged; the states the following commands need are set again. Draw order within every pass is unchanged
(cast first). Per-draw overhead cut where free: no LINQ closure per instance in `DrawPass`. `SPARKIV_D3D12_PARALLEL=0`
records everything on the render thread as before (the same pictures). The profiler's marks may be set from workers
(`GpuProfiler.Open`/`Close`); the workers' times show as `worker: ...` CPU stages.

Tried and dropped: **bundles** (`ExecuteBundle`) instead of direct lists - same pictures, but the GPU took 2.1 ms more
for bc_1's solid pass (1.23 -> 3.35 ms, 2911 draws; not in the intro or bc_3), cause not found; and recording the
**water's mirror** ahead on a worker - from a bundle its 4-sample target came out 25 pixels by 1 different from run to
run; it stays on the render thread (1.0 ms in the intro).

**Measured** (cutgen `D3D12BENCH=1 D3D12PROFILE=1`, Release, `SPARKIV_D3D12_DEBUG=0`, 1920 x 1080, all frames; serial =
each frame waited for, as before; `D3D12BENCHPIPE=1` = the pipelined loop, at most one frame on the GPU):

| Scene | | before (26a35c4) | after, serial | after, pipelined |
|---|---|---|---|---|
| intro s4 | frame mean / p95 | 21.78 / 23.48 ms | 16.17 / 17.68 ms | **14.38 / 15.94 ms** |
| | GPU | 14.37 | 14.32 | 14.34 |
| | CPU update + recording | 0.83 + 6.30 | 0.87 + 3.19 | |
| | workers (sun, map, solid, decals, see-through) | - | 1.40, 0.25, 2.05, 0.26, 0.62 | |
| bc_3 s3 | frame mean / p95 | 12.10 / 12.46 | **9.48 / 9.78** | 8.24 / 8.58 |
| | GPU | 8.21 | 8.21 | 8.16 |
| | CPU update + recording | 0.66 + 3.01 | 0.65 + 1.61 | |
| bc_1 s1 | frame mean / p95 | 10.26 / 11.30 | **8.33 / 10.25** | 7.09 / 9.25 |
| | GPU (sun shadow) | 8.62 (4.50) | 7.28 (3.14: 360 frames without) | 7.04 |
| | CPU update + recording | 0.66 + 0.78 | 0.63 + 0.49 | |

The serial frame is now less than update + recording + GPU: the spliced lists start on the GPU while the rest is still
recorded. Expected in the app at 60 Hz: the intro pipelined (CPU ~4 ms + GPU ~14 ms over the line), ~14.3 ms, 60 fps,
2 refreshes of latency (before: 30 fps once over); bc_3 and bc_1 serial, 60 fps, 1 refresh of latency. Still on the
render thread in the intro: the water's mirror meshes (1.0 ms) and waiting for the solid pass's workers (0.8 ms).

## Section and location changes without long frames (2026-11-05)

The app held 60 fps but for single long frames (120-420 ms) at some section changes (SparkIV-d3d12.log, 17:05 to
17:08). Now no frame over 33 ms at any section or location change of the intro or bc_3.

**Reproduced offline:** cutgen's `D3D12PLAY=1` (`tests/visual/werkzeug/cutgen/Play.cs`) plays the whole cutscene
through `CutscenePlayer`, the class the app uses, offscreen at 1920 x 1080. It drives the player as `CutsceneView`
does:
- the cast;
- the location of each section's origin, and the next one's prepared ahead;
- the time cycle's light anew each game minute;
- the water;
- a clock of 1/60 s per frame, each frame waited for.

Per frame it records the render thread's time (update to submit) and the GPU's. Frames over `D3D12PLAYLIMIT` (33 ms)
are listed with their costliest passes and CPU stages (`GpuProfiler.LastFrame`), and the worst frame within 3 s of each
section change is reported. The player is now free of WinUI for this:
- it takes an `Action<Action>` for the UI thread instead of a `DispatcherQueue`;
- `IRenderScene` and `SceneView` moved to `RenderScene.cs`;
- `OrbitCamera.WalkKey` takes a virtual-key code.

`scene update` is broken down by the player's orders (`order: set location`, `prepare location`, `drop location`,
`loaded (register)`, `set section`, `set lighting`, `set water`) and the drawing's own steps (`draw: sky textures
upload`, `draw: water advance`).

**Causes found and fixes:**

| Cause (measured) | Fix |
|---|---|
| The water's 20 000-step warmed-up base, 2 000 steps a list: 10 frames of ~140 ms GPU when the water first showed | The base is written to a disk cache (`%LOCALAPPDATA%\SparkIV\D3D12\water-base-<key>.bin`, keyed by the adapter, the shader and the warm-up's numbers), read instead of the steps. Without the cache, while playing at most 32 warm-up steps a frame (since round 2; 48 at first). A capture, which always warms up itself, compares what it made with the cache and logs it ("water base made again: the same as the cache"). `SPARKIV_D3D12_WATERCACHE=0`: no cache. |
| The water simulated only once the location was drawn: the swell caught up the section's seconds at once (frame 179 of bc_3: 113 ms; intro section 2: 230 ms with the upload) | `CutsceneWater.Advance` from `DrawBackground` every frame from the section's start, also while the location isn't shown yet: a step a frame. `Draw` leaves what Advance did that frame. |
| The water exact (a still frame) at a section's end while the scene plays on: the swell recomputed from flat, 115 ms (bc_3, last frame of sections 2 and 3) | Exact only when stopped by the user (`_ranOut`). |
| The water's quads uploaded on the render thread with `WaitIdle` (20-42 ms) | `CutsceneWater.Prepare` on another thread, on the copy queue; `Use` swaps them in on the render thread, the old ones retired. |
| `WaitIdle` in `SetCoronaSprite` while the next location's uploads ran on the direct queue (530 ms) | Uploads on a **copy queue** (`D3D12Device.CopyQueue`, `CopyAndWait`): `MeshRenderer.Prepare`, the water's quads and the sprites. Copy lists leave what they copy into in COMMON, which the direct queue promotes to any read state. The corona sprite and volume lookup are made on the loading thread (`PrepareTexture`, `UseCoronaSprite`, `UseVolumeLookup`). |
| `RemoveGroup` (drop location): `WaitIdle` and freeing thousands of buffers and textures on the render thread (73-262 ms) | `D3D12Device.Retire`: freed once the GPU is past the frames that may use them, on a thread below normal priority. `ReleaseRetired` is called each frame (`PanelSwapChain.Begin`, `OffscreenTarget.Begin`); `ReleaseRetiredNow` before the renderer goes. |
| `FarClip` walked every vertex of the location on the render thread (13-34 ms) | `CutsceneDrawing.BackgroundReach`, cached per location and worked out on the loading thread (`Parallel.For` over the objects). `TryFarClip` on the render thread; the exact plane is set when the location is registered, before it shows. |
| The first frame drawing a location compiled the drawing code (recording 50 ms, workers 12 ms each) and made the targets (G-buffer, sun cascades, depth copies, mirror) | `JitWarmUp` compiles the renderer's, the player's and the water's methods on a background thread at the player's start. `MeshRenderer.MakeTargetsAhead` makes one kind of target per frame while nothing is drawn yet; `CutsceneWater.MakeTargets` does the same for the water. The sky and the water (shader compiles) are made on another thread. |
| A location's GPU prepare took 2 s on one thread | `MeshRenderer.Prepare` spreads the models over up to 4 threads (each its own copy list); `TextureCache` makes textures outside its lock (a slot is reserved first). Loading threads run at BelowNormal; the render thread at normal. |

Also, while the view draws, `GCSettings.LatencyMode` is `SustainedLowLatency` (no blocking full collections where they
can be avoided; restored on Dispose). Sky textures are made again only when their bytes change: every location has
skydome.wtd's.

**Prefetch:** the next section is prepared while the current one plays:
- `CutsceneView.NativeSectionOf` makes its motions and camera on another thread;
- `PrepareLocation` puts the next origin's location on the GPU, with its lamps (`CutsceneLights`), the far plane's reach,
  its water's quads and the sprites;
- at the change the render thread only swaps prepared objects in: `set section` 0.0 ms, `set location` ≤ 2 ms.

A seek to another section loads on demand as before. At most the shown and the next location and water are kept.

**Measured** (cutgen `D3D12PLAY=1`, Release, 1920 x 1080, RTX 2060; worst frame within 3 s of each change, CPU = render
thread update to submit, GPU = the frame's list):

| Change | Before: worst frame (cause) | After |
|---|---|---|
| intro s1 (start, location) | 96 ms CPU (first frame) | 30 ms CPU (first frame: lights list 9 ms, camera/light 7 ms, recording 8 ms) |
| intro: the harbour first drawn (s2) | 239 ms CPU / 360 ms GPU (water upload, swell catch-up), then 10 frames of 140 ms (base), then 533 ms (`loaded`, WaitIdle behind uploads) | ≤ 20.6 ms (frame 9: the water's targets and the cached base made) |
| intro s3 (location) | 268 ms CPU (drop location) | 1.4 ms CPU / 4.7 ms GPU |
| intro s4 (location) | 121 ms CPU (drop 73 ms, set location 34 ms) | 9.0 ms CPU / 6.6 ms GPU |
| intro s5, s6 | 15.0 / 16.8 ms GPU | 15.1 / 13.7 ms GPU (unchanged: no loading) |
| bc_3 s1 (start, location) | 140 ms CPU / 282 ms GPU, then 10 x 140 ms GPU | 31 ms CPU (first frame), 21.6 ms (frame 14: the water's targets and cached base made); the first frame drawing the harbour under 20 ms |
| bc_3 s2, s3 ends | 115 ms (exact swell) | 1.5 ms CPU / 8.4 ms GPU |
| whole run, frames over 33 ms | intro 14, bc_3 13 | intro 0, bc_3 0 (over 20 ms: 2 each, both in the first 15 frames) |

Without the water cache (first run on a machine, bc_3): the base warms over the first ~420 frames at ~4 ms GPU each, and
no frame is over 33 ms (worst 31.5 ms, the first frame drawing the harbour, measured before the targets were made ahead). Pictures are byte-identical to before on all
8 references checked (bc_3 s3 f357.25, s1 f292.5; intro s4 f420.5, s3 f49.5; bc_1 s1 f144, f940; et01_aa s3 f49.75;
et01_ba f468). The debug layer reports no new kinds of message (the copy queue's textures are made in COMMON).

### Seeks, dropped locations and the sound's nudges (2026-11-05, round 2)

The user's test of the app (intro, log from 18:21:14) showed what the first offline run lacked:
- 1.9 s frames when a location dropped earlier came back;
- 90-150 ms frames from the water after time jumps;
- scrubbing the timeline while playing stopped playback at the section's end.

**Reproduced:** `D3D12PLAYREAL=1` plays in real time:
- the wall's clock, frames paced to 60 a second;
- every 0.25 s the sound's `Sync`, a little off (±30 ms);
- locations loaded on another thread as the view does: when a section there or the next one wants them, and dropped
  (CPU data and GPU) when neither does, so they are loaded again later.

`D3D12PLAYSEEKS=1` adds the user's seeks (the view's `GoTo`):
- 3 s into section 2, back to the middle of section 1;
- then on to section 3, then back into section 2, whose location was dropped meanwhile;
- then ten slider steps within section 2 while playing, after which the next section must still follow.

A `GC pause` stage (`GC.GetTotalPauseDuration` per frame) is now in the app's log and in cutgen's.

| Hitch (app log 18:21-18:25) | Reproduced offline (before) | Cause | Fix | After (offline, intro, real time, seeks) |
|---|---|---|---|---|
| `loaded (register)` 1876 / 1890 ms | 619 ms and 2519 ms, after the seeks | A dropped location's load that came in anyway: `MeshRenderer.Discard` freed its thousands of buffers and textures on the render thread | `Discard` retires them, freed on the freeing thread | `loaded (register)` < 2 ms |
| `draw: water advance` 56-130 ms, `water: sim` 80-125 ms GPU | 37-39 ms on the first water frame, 34 ms GPU per frame while the copy engine uploaded | The swell recomputed from flat on a step back of the sound's sync; the wave texture and the swell caught up unbudgeted after jumps; the noise (4 MB of random bytes) and the cache file made on the render thread | While playing, a step back of up to 25 swell steps (0.5 s) is held as the waves' 12 are. The swell's catch-up is budgeted per frame: 60 steps playing, 300 stopped (a capture's Draw: all). The waves' catch-up: 16 steps a frame playing (with the copy engine busy a step took 0.5 ms instead of 0.07), 32 for the warm-up, 400 when stopped (one hitch at most, then the rest). The noise's bytes and the cache file are made or read on the water's own thread. Targets, noise and base each take a frame of their own. | ≤ 25 ms (first water frame), no water frame over 20 ms after |
| Scrubbing while playing stopped at the section's end | Checked: "next section 3 started after the scrub: yes" | `TimeSlider_ValueChanged` went to `GoTo(play: false)`. The view stopped and paused the sound, but the player was never told to stop (`Seek` alone) and played to the section's end, where the stopped view didn't go on. The page's seek stops it, so the page path stopped right at the scrub. | The slider keeps the play state (`GoTo(play: _playing)`), and `GoTo` tells the player the play state either way (`Play(play)`), so page and player stop or play on together. A seek within the section keeps the prefetch: the next section's data stays prepared. | Next section follows |
| first draw of 752,-212: workers 721 / 364 / 180 ms | not reproduced (offline 10.8 ms at the change) | (The app's `GC pause` stage will show whether it is the collector) | Workers' code compiled ahead (round 1) | - |
| `prepare location` 178 ms, `update: camera, light, cascades` 58 ms, `lights list` 50 ms, `wait for the buffer's fence` 64 ms, `close + execute` 49 ms | not reproduced | Not found in the code: each is constant work. Likely collector pauses or the system (the new `GC pause` stage will tell). | The copy queue has a lock of its own, so `Signal` (`Retire`, `Submit`) never waits behind a big upload being sent | - |

Offline totals (intro, real time with the seeks):
- before: 3 frames over 33 ms, the worst 2519 ms;
- after: 0 frames over 33 ms. The worst is 25 ms on the first water frame of section 1. Every change is under 18 ms
  CPU and 21 ms GPU, including the return to the dropped location and the scrub.

Pictures are unchanged: the 8 references are byte-identical.

## The model and animation views (step 6, 2026-11-05)

With `SPARKIV_D3D12=1` the model view (`ModelView`: .wdr, .wdd, .wft) and the animation view (`AnimationView`: .wad clips
on a ped or map object) draw natively too; without it they use the page as before. Both views keep all their controls;
at each place they tell the page something they tell the native scene the same (the page's messages go nowhere without
the page), and they take back what the page said (`spot`, `frame`, `loaded`).

| Class | Does |
|---|---|
| `ModelScenePlayer` (`Rendering/D3D12`, generic) | The views' `IViewerScene`: the page's messages as orders for the render thread (`SetScene`, `SetVisible`, `SetOptions`, `SetBackground`, `SetLights`, `Fit`, `Spot`, `SetAnimation`, `Play`, `Seek`, `SetSpeed`, `SetLoop`, `SetMove`, `SetFacials`); the page's `fit`/`fitDistance` (~3019-3076), `findSpots`/`goToSpot` (~1359-1457), playback `tick`/`play`/`seek`/`showFrame` (~2937-3014), `pose`'s camera following the movement through the world (~2852-2861); its camera `OrbitCamera` orbiting the model with the cutscene view's smoothing; near and far as the page's viewProjection (~3101-3107). The model goes onto the GPU on a loading thread (`MeshRenderer.Prepare`), all its meshes (shown or hidden by mesh id each frame, no reload when the tree changes). |
| `IViewerScene` (`RenderScene.cs`) | A scene that draws its picture itself: `D3D12View` calls its `Draw` with the back buffer instead of the light target, tone mapper and `MeshRenderer.Draw` (the cutscene path is unchanged). |
| `MeshRenderer.DrawViewer` (`MeshRenderer.Viewer.cs`, a partial of `MeshRenderer`) | The page's path for a lone model: its fragment shader with outdoor, grade and baked false and no cutscene lights (ModelView.html ~421-736), with gtaiv-pc.js's model-view parts (vehicleShine's last branch ~454-459 and the vehicle glass ~406-423, the stipple ~472-481): base texture (or the material's colour, the page's `hsl((id x 0.618) mod 1, 0.25, 0.62)`), paint3's second layer, terrain layers, dirt decals, cut-outs at 45/255, trees' 8 x 8 stipple (GL's rows from the bottom), normals turned to the camera, ped normal maps (`pedBumpedRaw` with the G-buffer's rounding) or the screen-space `bumped`, parallax, `*_reflect` environment, light 0.35 x around + 0.65 x n.l from the head light (~3116-3122), gta_spec's shine, emissives, see-through by texture alpha or 0.45. States per mesh as `drawMeshes` (~3381-3707): solid first, then the see-through ones (dirt decals first) ONE / INVSRCALPHA, depth written for vehicle and map glass, map glass culled, decals pulled (-0.25, -4), blended (-0.25, -2), depth less or equal. Then the wire frame (each edge once, ~1051-1070, in 0.95/0.75/0.25), the ground grid while moving (~3130-3147, ~3962-3969) and the lights' markers (diamond / spot cone, MarkerSize 0.012, faint where hidden, ~1297-1346, ~3985-4003). Background: the gradient #68788a to #22252a, grey, black or white (~24-27). 4 samples a pixel, resolved into the target (the page's `antialias: true`). Own root signature and pipelines, made at the first frame. |
| `AnimationPose` (`Viewers/GtaIV/Pc`, GTA IV PC) | The page's `sampledPose` with the facial clips (~1659-1709), `findFollowers`, `gameHelper`, `worldMatrices`, the mover (`pose`, `moverPath` ~2840-2882) and `updateUv` (~2910-2920) for the animation view's skeleton and clip (`ViewerSkeleton`, `ViewerClip`, made by `AnimationView` from the same data it sends the page). |
| `NativeViewInput` (`Rendering/D3D12`, WinUI) | The panel's mouse and keys as the cutscene view's (CursorDrag for mouse drags, wheel, double-click fit, W/S/A/D/Q/E). |
| `TextureCache.ImageMipmaps` | PNG textures with their smaller levels (2 x 2 means, the page's `generateMipmap`); on only for the views (the cutscenes' cast keeps none: their pictures are unchanged). |

**Comparison with the page** (cutgen `MODELVIEW=...`, `tests/visual/werkzeug/cutgen/ModelMode.cs`: the native picture
through `ModelScenePlayer`, and the same messages and camera for the page, `seite/baue-modellseite.ps1` ->
`test_model.html` in headless Edge, the canvas laid over its background; 1280 x 720, mean greyscale difference /255):

| Case | Model | Materials | page vs native |
|---|---|---|---|
| vehicle | vehicles.img admiral.wft (fitted view) | 4 glass, 4 blend | 0.33 |
| vehicle from above | admiral.wft, yaw -2.2, pitch 0.6 (glass in view) | | 0.32 |
| vehicle, plain colours, grey | admiral.wft, textures off | | 0.10 |
| ped | componentpeds.img f_y_business_01.wdd | 8 cut-out | 0.25 |
| map object | brook_s.img ci_clsdride2_01.wdr (rides) | 4 blend, 41 cut-out, 2 emissive | 0.42 |
| map object with decals | brook_s.img ci5a_ksun.wdr | 2 decal, 2 blend, 20 cut-out | 0.56 |
| big building | brook_s.img refinary1_01.wdr | 2 blend, 34 cut-out | 0.36 |
| shop block | brook_s.img rt6a_ksunss.wdr | 1 blend, 1 emissive | 0.36 |
| animation | Niko (playerped.rpf), anim.img move_player.wad walk, frame 12 | | 0.26 |

No pixel is 48 or more apart except 0.02-0.05 % at the vehicle's edges. The differences are the edges (both draw 4
samples a pixel, but ANGLE's and Direct3D 12's sample positions and resolve differ) and on large surfaces seen at a slant
a faint texture filtering difference (mip levels made by the page's driver against the 2 x 2 means here; anisotropic
filtering's footprint). The pose of the animation lies exactly over the page's. The wire frame, the ground grid and the
markers are drawn after the page's picture is taken, so they are checked by eye only.

## Next steps (#112)

4. **Port the page's logic**, game-specific parts in their own module (as `gtaiv-pc.js` now): camera and animation
   (skinning), shaders (HLSL via DXC), sky, water, shadows, post effects (DoF, bloom, tone mapping), decals, coronas,
   room modifiers.
5. **Deferred lighting as in the game:** G-buffer, a light volume per lamp, no lamp cap (takes over #106).
6. **Switch** the cutscene view, then the model view; remove the WebView2 path once the visual run is not worse.
