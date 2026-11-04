# Native Direct3D 12 renderer (#112)

Status: **step 4c, light and tone mapping** – in the app behind the switch `SPARKIV_D3D12=1`.
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

Drawn so far: the location (all placed objects but mirror-only far versions, which only the water's mirror shows; their checked meshes only) through the
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
`SparkIV-fps.log` work as before.

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
  em_4 in Elizabeta's flat (et01_aa) fogs 78 % towards a warm grey from 3 m on. Not yet: the same pass's depth effect
  (gDepthFxParams).
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
visual tests it starts at the trace's value, `CLOUDPHASE`), below the horizon faded to the far fog colour on the game's
dome, and the near fog's share. The main view, and the water's mirror (below, "The water's mirror"); the parabolic
surroundings map not yet. In the app the player draws it through `IRenderScene.DrawBackground`.

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
draw), before the water and the see-through materials, which stay forward without lamp light for now. No cap: the
page lit at most 80 forward (#106); the intro's harbour adds some 130 at once. `CutsceneLights` (GTA IV PC) picks them
per frame as the page's `sceneLightsNow`: the section's own lights (the 8 strongest; env*/shd* only on baked
surfaces, the others only on peds and vehicles), the tail lamps of vehicles whose lamps the script turns on, the
location's lamps on at the hour, of the rooms seen, in view, faded by FadeDistance - with the page's formulas (light
and filler families, AO terms, cones). The sun and the light from around stay exactly as before. Lamp shadows as the
page's: the 4 shadow lamps nearest to the camera (distance less radius; `CutsceneLights.LampShadowSlots`) get a slot in
an R32F atlas (2048 x 4096: a row of two 1024² halves per lamp), drawn each frame before the G-buffer from the cast's
solid parts (instances added with `castsShadow`; the location casts none), sampled in the light pass with the page's
four rotated taps (its GL rows and pixel y turned over, so the reads are the page's). Other shadow lamps stay fully
lit. Not yet: the lamps' shine, light volumes' visible cones and coronas.

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
Not yet: the mirrored surroundings in the shine (the parabolic map and its sky fallback), the lamps' shine, glowing
glass (gta_glass_emissive), lamps on the forward-drawn materials, animated textures.

### Post effects (step 4h)

`ToneMapper.Composite` now does the page's whole composite (gtaiv-pc.js `gamePostEnd`): the quarter copy, made once a
frame (also when no measure is taken), is blurred across and down (the page's blur passes) into the bloom, added above
the bright pass (`ToneParams.Bloom` = IntensityBloom x 0.5, `BrightPass`); the depth of field reads the depth buffer
(`OffscreenTarget.DepthBuffer`, `PanelSwapChain.DepthBuffer`) with the cutscene camera's CameraDof track
(`CutsceneCamera.DofAt`, near and far per frame), the four rotated reads in Direct3D's texture space; TLAD's film grain
(`ToneParams.Grain` = column 10 / 64 x Exposure) from a 1024 x 1024 texture of random bytes (fixed seed); the fade to
black (`CutsceneMessages.SectionFade`, only through the cutscene's camera) last. The frame's DOF and fade come in
`PostParams` (`SceneView.Post` in the app). Not yet: the lamps' coronas and visible light volumes.

## Next steps (#112)

4. **Port the page's logic**, game-specific parts in their own module (as `gtaiv-pc.js` now): camera and animation
   (skinning), shaders (HLSL via DXC), sky, water, shadows, post effects (DoF, bloom, tone mapping), decals, coronas,
   room modifiers.
5. **Deferred lighting as in the game:** G-buffer, a light volume per lamp, no lamp cap (takes over #106).
6. **Switch** the cutscene view, then the model view; remove the WebView2 path once the visual run is not worse.
