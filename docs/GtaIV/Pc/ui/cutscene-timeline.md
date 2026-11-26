# Cutscene Timeline View (Design)

Status: **design proposal** for [reditec/SparkIV#119](https://github.com/reditec/SparkIV/issues/119).  
Applies to: **GTA IV, PC**, WinUI 3 cutscene viewer (`SparkIV.WinUI.Viewers.GtaIV.Pc.CutsceneView`).  
This document is design-first; it does not describe shipped timeline UI.

## Goal

Add a timeline to the cutscene view that shows, over the scene's whole time:

- one bar per clip, grouped by **Camera**, **Actors** (peds, props, vehicles), and **Lights**;
- markers for subtitles and draw-distance changes (MVP); experimental CameraControl(144) cut candidates and parsed `[EFFECTS]`/event markers only after validation;
- a playhead/scrub interaction that stays in step with the existing transport slider;
- click-to-inspect for bars and click-to-jump for markers.

The timeline must reuse existing parsed/loaded cutscene data where that data exists, and call out data gaps explicitly where it does not.

## Current UI baseline

Source: `src/SparkIV/Viewers/GtaIV/Pc/CutsceneView.xaml`, `CutsceneView.xaml.cs`.

Current outer layout:

| Grid row | Content | Code reference |
|---|---|---|
| 0 | `CommandBar` (camera/location/lights/subtitles/background/start/export) | `CutsceneView.xaml` lines 33-81 |
| 1 | Main content: left lists (`Sections`, `Cast`, `Subtitles`, 280 px) + right 3D viewport | lines 83-158 |
| 2 | `PlayingPanel`: `TimeSlider` over whole scene, `SectionMarks` canvas overlay, transport buttons, frame/section/volume text | lines 160-209 |
| 3 | Status / FPS / clock | lines 211-223 |

Current time model (all in `CutsceneView.xaml.cs`):

- `CutFile.Sections[i].Duration` accumulates into `_sectionStart[]`; `_sectionStart[^1]` is scene length.
- `TimeSlider.Minimum/Maximum` are milliseconds over the played range (`_from`, `_to`); one-section `.wad` playback narrows the range (`OnlySection`, `CutsceneWadViewer`).
- `Locate(seconds)` maps scene seconds to `(Section, Frame)` using section FPS (`FpsOf` -> `CutsceneMessages.FramesPerSecond`).
- `Now` is current scene seconds: `(_section >= 0 ? _sectionStart[_section] : 0) + _frame / (double)_fps`.
- `GoTo(seconds, play)` is the single seek/play entry point; it handles section changes, audio seek, deferred audio wait, and timer start.
- `ShowFrame()` updates slider, frame/section text, play glyph, clock, subtitle overlay, and subtitle list selection.
- `DrawSectionMarks()` draws thin rectangles at section starts over the slider track.

## Layout sketch (proposed)

The timeline sits **between the viewport row and the existing transport panel**. It spans the full width of the cutscene view (same width as `PlayingPanel`), so its time axis can share the same left/right inset and mapping as the `TimeSlider` / `SectionMarks` overlay. The existing transport panel remains the authoritative scrub control for MVP; the timeline adds a richer synchronized view and hit targets.

```text
+--------------------------------------------------------------------------------------+
| CommandBar: [Cutscene camera] [Show camera] [Location] [Scene lights] [Subtitles] ...|
+--------------------------------------------------------------------------------------+
| Sections/Cast/Subtitles (280px) | 3D viewport / SwapChainPanel                        |
| - Sections                      |  [subtitle overlay bottom]                          |
| - Cast                          |                                                     |
| - Subtitles                     |                                                     |
+--------------------------------------------------------------------------------------+
| Timeline header: [Timeline v] [Zoom - +] [Fit] [Follow playhead] [Legend]  00:12.3  |
| Time ruler:  0s      5s      10s      15s      20s      25s      30s                |
| Markers:     |   *D  ^T      *D   ^T        vDD      ^T *D          !E               |
| Camera   [==== camera_0 ====][==== camera_1 ====][=========== camera_2 ===========]  |
| Actors >  Peds                                                                         |
|           [== CS_Niko_0 ==]      [======= CS_Niko_1 =======]                         |
|           [== CS_Roman_0 ================================]                            |
|           Props                                                                        |
|           [== csOldPhone_0 ==]                                                        |
|           Vehicles                                                                     |
|           [================== schafter_0 =================]                           |
| Lights >  [spt_dr_0]      [fil_red_0] [rim_0]                                        |
|           ^ playhead (vertical line across ruler + tracks)                            |
+--------------------------------------------------------------------------------------+
| PlayingPanel: [----|====playhead====|---- section marks ----]                         |
| Frame/time    [PrevSec][Prev][Play/Pause][Next][NextSec]         Section / Volume     |
+--------------------------------------------------------------------------------------+
| Status text                                              FPS                 Clock     |
+--------------------------------------------------------------------------------------+
```

Legend for sketch markers: `^T` subtitle, `vDD` draw-distance change. `*D` CameraControl(144) is experimental/Phase 2 only (unresolved semantics), not shown by default in MVP; `!E` `[EFFECTS]` requires parsing. Glyphs/colors illustrative; see Visual spec.

### Placement rationale

- **Full width below viewport, above transport:** keeps time alignment simple and avoids squeezing the 280 px left list column. Section/cast selection remains visible.
- **Transport panel kept:** existing keyboard accelerators, play/pause, section buttons, and volume remain stable. The timeline does not replace the slider in MVP; it mirrors it.
- **Height:** default 180-240 px, min 120 px, max ~40% of viewer height. Implement as a resizable row/splitter in a later phase; MVP can use a fixed height with collapse.
- **Collapse:** timeline header has a chevron/toggle. Collapsed state leaves only a header row (or hides timeline row entirely) and preserves current viewer behavior.

### Alternatives considered (not recommended for MVP)

- **Inside left panel below Subtitles:** too narrow for time-aligned bars/markers; conflicts with subtitle list use.
- **Overlay on viewport:** obscures 3D content and fights subtitle overlay; poor for dense marker hit-testing.

## Track grouping and folding model

### Groups

| Group | Contents | Default state | Primary source |
|---|---|---|---|
| `Camera` | One lane for section camera clip bars; camera-cut markers shown in marker strip above lanes | Expanded | `CutSection.Camera`, camera clip `Animation` in section WAD |
| `Actors` | Subgroups `Peds`, `Props`, `Vehicles` | Expanded; subgroups expanded when non-empty | `CutSection.Models`, `CutActor` |
| `Lights` | One lane per light clip, or packed lanes if many | Expanded when present, otherwise empty header | `CutSection.Lights`, `CutsceneLights.TracksOf` |
| `Markers` (ruler strip) | MVP: subtitles, draw distance. Experimental/parsed-only: CameraControl 144, `[EFFECTS]`, sound/facial/action events behind validation | Always visible | see Data model mapping |

### Actor classification

Classification is display-only and must not change playback logic. Use existing actor/scene information:

- `Ped`: `CutActor.IsPed` is true (`Scene != null && Scene.Bones.Count > 40 && !Scene.Materials.Any(m => m.Vehicle)`).
- `Vehicle`: actor has a loaded scene and any vehicle material/paint data (`Scene.Materials.Any(m => m.Vehicle)` or `CutActor.Paint != null`) and is not classified as ped.
- `Prop`: any other loaded actor/model with a scene.

The row label should be `CutModel.Model` (same identity as cast), with tooltip containing clip/head clip names and actor source/error.

### Folding behavior

- Group header toggles child lane visibility. State is remembered per view instance; optionally persisted in static fields like other cutscene view preferences (`s_camera`, `s_lights`, etc.).
- Collapsing a group does not remove its markers from the global marker strip; markers remain clickable.
- Empty groups render a disabled/empty header (`No lights`) rather than being hidden, so users can understand absence.
- Later enhancement: per-track hide/mute. Design reserves a left gutter icon cell per lane but MVP has no mute behavior.

## Visual spec

Textual/structural spec; exact XAML theme resources should follow existing cutscene/animation viewer styles (`LayerFillColorDefaultBrush`, `CardStrokeColorDefaultBrush`, caption/strong text styles).

### Time ruler and playhead

- Ruler ticks: adaptive by zoom. At default fit, show 1s/5s/10s ticks depending on scene length. Always show `m:ss.f` labels at major ticks.
- Playhead: 2 px vertical line in accent color across ruler, marker strip, and all track lanes. Top cap/triangle in ruler for grab affordance.
- Playhead position uses same scene-time mapping as `TimeSlider.Value` (`Now`, milliseconds). During playback, update from `OnFrame`/`ShowFrame`; during scrub, update immediately.
- Section boundaries: keep existing `SectionMarks` on the transport slider; also draw subtle vertical section separators in timeline lanes at `_sectionStart[i]`.

### Bars

| Bar type | Label | Tooltip/detail | Placement |
|---|---|---|---|
| Camera clip | `section.Camera` (e.g. `camera_0`) or `Camera` fallback | section, start/end, frame count/FPS when clip available, DOF/FOV availability, missing-camera note | Camera lane, one bar per section |
| Actor clip | `CutModel.Model` | clip name, head clip, actor source/error, section, frame count/FPS, missing-track note count | Actor subgroup lane; segmented per section appearance |
| Light clip | light clip name (`spt_dr_0`) | section, frames/FPS, track kinds present | Lights lane; packed rows to avoid overlap |

Bar geometry:

- X start = section start + clip local offset (normally 0 in current data model). X end = start + clip duration.
- Duration source preference: actual clip duration `Animation.Duration` when WAD clip is present; fallback `CutSection.Duration`.
- Minimum visible width: 2 px. Very short bars get a pill/diamond marker mode at high zoom-out to remain clickable.
- Label truncation: ellipsis; full label in tooltip.
- Missing data: if WAD clip missing, render hatched/outlined bar and include `(missing .wad/clip)` in tooltip. Detectable through `CutsceneDocument.Sections[index] == null` and clip lookup.

### Markers

Markers live in a dedicated marker strip under the ruler, grouped by rows to reduce overlap. Rows are fixed priority from top to bottom:

1. Camera cuts
2. Subtitles
3. Draw distance
4. Effects / events (sound, facial, other clip events)
5. Reserved/future

| Marker | Glyph/text | Source | Click behavior | Availability |
|---|---|---|---|---|
| Camera cut candidate | `D` | camera `CameraControl` (track 144) transitions | Jump playhead to cut time | **Phase 2 / Experimental only** - unresolved semantics; hidden by default in MVP |
| Subtitle | `T` | `CutFile.Subtitles` (`Start`, `Length`, `Key`, `Part`) + `CutsceneDocument.Texts` | Jump to subtitle start; select subtitle row | Available now |
| Draw distance | `DD` | `CutFile.DrawDistances` / `DrawDistanceSteps` | Jump to change time; show metres | Available now |
| Effect | `FX` | `[EFFECTS]` block | Jump/select effect | **Not parsed today** |
| Sound event | `S` | clip event tracks (e.g. `UV1`/AudioEvent hash where present) | Jump to event frame/time | **Not currently present in cutscenes per data-support** |
| Facial animation | `F` | clip event tracks (`FacialAnimation`) | Jump to event frame/time | **Not currently present in cutscenes per data-support** |

Density rules:

- Collapse adjacent markers of same kind within 3 px into a cluster badge (`+N`). Tooltip lists contained markers.
- Zoom-in expands clusters.
- Marker hit target minimum 16x16 px.
- Do not stack more than 5 marker rows; overflow goes into cluster with legend/filter text.
- Colors are not the only discriminator; marker glyphs/labels and legend text are required.

## Interaction spec

### Selection and inspection

- **Click bar:** selects bar and opens details pane/popover. MVP recommendation: non-modal details flyout anchored near timeline/viewport to avoid relayout churn; alternative is a Details section in left panel.
- **Bar details ("clip details from animation view" equivalent):** show clip identity, section, model, actor class, start/end, frame count, FPS, track count, missing-track summary, and source/error. Reuse computation patterns from `CutsceneMessages.Motions` (missing clip, missing camera clip, no clip notes) and animation-view track inspection where applicable.
- **Click marker:** jumps playhead via `GoTo(marker.Seconds, play: false)` by default; selecting marker shows marker details. Optionally preserve playing state by user preference.
- **Double-click bar:** jump to bar start and select its section.

### Scrub/playhead sync

- Dragging playhead in timeline calls the same seek path as slider (`GoTo`). While playing and scrubbing, preserve current playing/scrubbing semantics used by `TimeSlider_ValueChanged` (it currently passes `play: _playing`).
- Moving `TimeSlider` updates timeline playhead on `ShowFrame()`.
- Playback updates from existing frame callbacks (`CutscenePlayer.FrameChanged` -> `OnFrame` -> `ShowFrame`) keep both in sync.
- Audio master clock behavior must remain unchanged: when audio exists, `Timer_Tick` follows audio and section changes remain authoritative.

### Zoom/scroll

- MVP: `Fit` (whole scene) and `+/-` zoom buttons; horizontal scroll when zoomed in.
- Zoom anchor: mouse cursor when wheel-zooming over timeline; otherwise playhead.
- Optional: drag in ruler to scrub; drag in empty lane area to pan when zoomed.

### Collapse groups

- Clicking group header chevron collapses/expands child rows.
- Collapsed groups still contribute section separators and global markers.
- Keyboard accessibility: group headers focusable, `Enter/Space` toggles.

### Keyboard/accessibility

- Existing space/left/right accelerators remain.
- Timeline focusable region supports: `Left/Right` step frame, `Ctrl+Left/Right` previous/next marker, `Home/End` scene start/end, `+/-` zoom.
- All bars/markers expose Automation names, e.g. "Camera clip camera_0, section 1, 0.0 to 12.4 seconds".
- Respect high-contrast/theme resources and text scaling.
- Announce seek/selection changes via UIA live region where appropriate (without spamming during drag).

## Data model mapping

All field names/types below are grounded in current code. Proposed types are suggestions for implementation; they are not present today.

### Scene/section timing

| Timeline element | Source field/type | Code reference | Notes |
|---|---|---|---|
| Scene sections | `CutFile.Sections: List<CutSection>` | `src/SparkIV/Viewers/GtaIV/Pc/Cutscene.cs` | Section order is file order. |
| Section duration | `CutSection.Duration: float` seconds; parsed from `[DURATION]` ms | `CutFile.Parse` | Converted by `/ 1000f`. |
| Section start/end | `CutsceneView._sectionStart: double[]`; `Length`/`_from`/`_to` | `CutsceneView.xaml.cs` ctor | Used by slider and section marks. |
| Current time | `CutsceneView.Now: double` | `CutsceneView.xaml.cs` | Derived from current section start + frame/fps. |
| Frame <-> time locate | `CutsceneView.Locate(double seconds)` -> `(int Section, int Frame)` | `CutsceneView.xaml.cs` | Uses `FpsOf(section)`. |
| Section FPS | `CutsceneMessages.FramesPerSecond(Animation clip)` fallback 30 | `Cutscene.cs`, `CutsceneView.FpsOf` | Clip-based FPS when available. |

### Clip bars

| Timeline element | Source field/type | Code reference | Notes |
|---|---|---|---|
| Camera bar | `CutSection.Camera: string?`; camera clip from section WAD | `CutFile.Parse` `[CAMERA]`; `CutsceneView.SectionDataOf/ShowSection`; `CutsceneCamera.From(Animation)` | Missing camera clip note already produced. |
| Actor bar identity | `CutSection.Models: List<CutModel>`; `CutModel(int Id, string Model, string Clip, string? HeadClip)` | `CutFile.Parse` `[MODELS]`; `CutsceneMessages.Motions` | `Model` is display label; `Clip`/`HeadClip` are WAD clip names. |
| Actor details | `CutActor` fields: `Id`, `Model`, `Part`, `Number`, `Scene`, `Source`, `Error`, `IsPed`, `Paint`, `Dirt`, `LightsOn`, `Shine`, `PropScene`, `Props`, `Faces`, `TextureVariations` | `Cutscene.cs` | `CutActorRow` currently surfaces `Source`/`Error`. |
| Clip details | `Animation.ShortName`, `FrameCount`, `Duration`, `Tracks`; notes from motion building | `RageLib/GtaIV/Pc/Animation/AnimationFile.cs`; `CutsceneMessages.Motions`; `AnimationView.SendClip` for reference detail fields | Animation viewer shows frames/fps/tracks/events; cutscene details should be equivalent but scoped to cutscene actor/clip. |
| Light bar | `CutSection.Lights: List<string>`; `CutsceneLights.CutLight` (`Name`, `Frames`, `Fps`, `Position`, `Color`, `Strength`, `Range`, `Cone`, `Direction`) | `CutFile.Parse` `[LIGHTS]`; `CutsceneLights.TracksOf` | Track 140/141/142 semantics are documented in code/data-support. |
| Missing track/bone notes | `CutsceneMessages.Motions` adds notes like missing clip/camera clip; bone tracks matched by `AnimationTrack.BoneId` and actor skeleton IDs | `CutsceneMessages.Motions`; `AnimationTrackKind.BonePosition/BoneRotation` | "Tracks for bones model lacks" can be computed by comparing decoded bone tracks against model skeleton IDs, as animation viewer does (`notInPed`). |

### Markers

| Marker | Source field/type | Code reference | Availability / caveat |
|---|---|---|---|
| Subtitle | `CutSubtitle(int Start, int Length, string Key, int Part)`; `CutFile.Subtitles` | `CutFile.Parse` `[TEXT]`; `CutsceneView` builds `CutSubtitleRow` | Times are ms from start of subtitle's part first section; `CutsceneView` already converts to scene seconds. |
| Draw distance | `CutFile.DrawDistances: List<(int Part, float Ms, float Metres)>`; `DrawDistanceSteps(int sectionIndex, float fps)`; `DrawDistance` | `CutFile.Parse` `[DRAW_DISTANCE]`; `CutsceneView.SendScene`/player draw-distance use | Has part/file fallback behavior; reuse `DrawDistanceSteps` logic. |
| Camera cut candidate (144) | `AnimationTrackKind.CameraControl = 144` | `RageLib/GtaIV/Pc/Animation/AnimationFile.cs`; `docs/GtaIV/Pc/formats/cutscene-camera.md`; `docs/GtaIV/Pc/data-support.md` | **Unresolved semantics.** Phase 2/experimental only, hidden by default; carry provenance + validation status. |
| Sound event | `AnimationTrackKind.UV1` used as AudioEvent in animation viewer; integer track values hashed to sound names | `AnimationFile` comments; `AnimationView.SendClip` | Data-support says not present in cutscenes; used in animation viewer. Surface only if actually present. |
| Facial animation event | `AnimationTrackKind.FacialAnimation = 134` | `AnimationFile`; `AnimationView.SendClip` | Same caveat as sound events. Distinct from Type21 face-shape weights (`CastMotion.Face`). |
| `[EFFECTS]` | `[EFFECTS]` block documented in data-support; **no `CutFile.Parse` case today** | `docs/GtaIV/Pc/data-support.md` (ignored, assumed fields); `CutFile.Parse` lacks `EFFECTS` | Must be parsed/modeled before markers can be shown. |

### Proposed extraction model (implementation suggestion)

```csharp
// Proposed - not present today. Names illustrative.
public sealed class CutsceneTimelineModel
{
    public required double SceneLengthSeconds { get; init; }
    public required IReadOnlyList<CutsceneTimelineSection> Sections { get; init; }
    public required IReadOnlyList<CutsceneTimelineGroup> Groups { get; init; }
    public required IReadOnlyList<CutsceneTimelineMarker> Markers { get; init; }
}

public sealed record CutsceneTimelineSection(int Index, int Part, double StartSeconds, double EndSeconds, float Fps, string? Anim, string? CameraClip);

public sealed record CutsceneTimelineGroup(string Id, string Label, IReadOnlyList<CutsceneTimelineLane> Lanes);

public sealed record CutsceneTimelineLane(string Id, string Label, string Kind, IReadOnlyList<CutsceneTimelineBar> Bars);

public sealed record CutsceneTimelineBar(
    string Id, string Label, string Kind,
    double StartSeconds, double EndSeconds, int SectionIndex,
    string? Model, string? ClipName, string? HeadClipName,
    CutsceneTimelineClipDetails? Details, IReadOnlyList<string> Notes);

public sealed record CutsceneTimelineClipDetails(
    int? FrameCount, float? Fps, int? TrackCount,
    int? MatchedBoneTracks, int? TracksForBonesModelLacks);

public sealed record CutsceneTimelineMarker(
    string Id, string Kind, double TimeSeconds, double? EndSeconds,
    string Label, string Detail, int? SectionIndex, bool Experimental);
```

Extraction should be a pure/model-building step over `CutsceneDocument` + section WAD clips, separate from XAML rendering. This enables unit tests and avoids coupling to `CutsceneView` layout.

## Phased implementation plan

### MVP (issue scope)

1. **Data extraction/model**
   - Add a `CutsceneTimelineModelBuilder` (name TBD) that consumes `CutsceneDocument`.
   - Build section bars for camera, actor clips, and light clips.
   - Build markers for subtitles and draw-distance changes.
   - Surface missing-clip/missing-camera notes in bar details.
2. **Timeline UI skeleton**
   - Insert timeline row in `CutsceneView.xaml` between viewport and `PlayingPanel`.
   - Render ruler, playhead, group headers, simple bars, and marker strip.
   - Collapse/expand groups.
3. **Interaction**
   - Bar click shows details (flyout/pane) with clip/model/frames/tracks/missing bone-track summary.
   - Marker click calls `GoTo`.
   - Playhead/scrubber synchronized with existing `TimeSlider`/`ShowFrame`.
4. **Docs/tests**
   - Update this doc with implemented deviations.
   - Add unit tests for time mapping and marker extraction (subtitles, draw distance, section boundaries).

### Phase 2 (data-complete markers)

1. **CameraControl (144) cut marker extraction**
   - Read camera clip tracks where `Kind == AnimationTrackKind.CameraControl`.
   - Prototype cut detection from value transitions (`-1/1`) but gate behind "experimental" label until validated against known cutscenes/traces.
   - Cite `cutscene-camera.md` caveat in tooltip.
2. **`[EFFECTS]` parsing**
   - Extend `CutFile.Parse` with `case "EFFECTS"` and add a `CutEffect` record/list.
   - Document parsed fields as assumption-backed until verified.
   - Render effect markers with effect name, clip, time, and object/bone refs.
3. **Clip event extraction**
   - For each section clip, scan integer/decoded tracks for discrete events (ActionFlags, FacialAnimation, AudioEvent/UV1-as-hash) and expose as markers when present.
   - Keep availability note: data-support currently says these are not present in cutscenes; display only when actually found.

### Phase 3 (editing-quality UX)

1. Per-track hide/mute (issue "Later").
2. Zoom/pan with minimap, marker filtering, search.
3. Hover scrub preview and richer clip detail panel (reuse/adapt animation event table).
4. Performance virtualization for large scenes/many lanes.

## Risks and constraints

- **CameraControl semantics are unresolved.** `AnimationTrackKind.CameraControl` exists, but `data-support.md` and `cutscene-camera.md` mark it unclear/not used and describe exe cut-detection complexity. Do not render 144-derived cuts by default in MVP; if added, keep Phase 2/experimental, hidden by default, with provenance/validation status.
- **`[EFFECTS]` is not parsed.** `data-support.md` documents it as ignored and `CutFile.Parse` has no `EFFECTS` case. Marker support requires parser/model work and field verification.
- **Sound/facial clip events may be absent in cutscenes.** Data-support track table says ActionFlags/UV0/UV1/FacialAnimation are not present in cutscenes and used in animation viewer. The UI should degrade cleanly to "no events found".
- **Timebase complexity.** Subtitles are part-relative (`CutSubtitle.Part`, `Start` ms from part first section). Draw distances have part/file fallback behavior in `CutFile.DrawDistanceSteps`. Marker extraction must reuse these rules, not reimplement ad hoc.
- **Section FPS differences.** `FpsOf(section)` can differ by section; clip frame-to-scene-time conversion must be per section.
- **Large scenes/perf.** Timeline rendering must avoid per-frame allocation and full visual rebuilds. Use lightweight drawing and update only playhead/active elements during playback.
- **UI density.** Many actor/light tracks can overwhelm the panel. Collapsed groups, lane packing, clustering, and zoom are required.
- **One-section WAD view.** `OnlySection` narrows the playable range. Timeline should follow existing `_from/_to` range.
- **No invented format semantics.** Any assumption (e.g. `[EFFECTS]` field meaning) must be labeled assumption and cite `data-support.md`.

## Accessibility notes

- Provide AutomationProperties.Name/HelpText for timeline, ruler, playhead, group headers, bars, and markers.
- Ensure minimum hit targets (16x16 px) and keyboard operability for selection and seeking.
- Do not rely on color alone; pair color with glyph, text label, and legend.
- Respect high-contrast/theme resources and text scaling.
- Announce seek/selection changes via UIA live region where appropriate (without spamming during drag).

## Suggested code touch points (implementation phase)

- `src/SparkIV/Viewers/GtaIV/Pc/CutsceneView.xaml` - add timeline row, header, ruler/track host, detail affordance.
- `src/SparkIV/Viewers/GtaIV/Pc/CutsceneView.xaml.cs` - timeline model build, playhead sync, selection handlers, group collapse state.
- `src/SparkIV/Viewers/GtaIV/Pc/Cutscene.cs` - optional `[EFFECTS]` parsing/model; timeline extraction helpers may live here or nearby.
- `src/SparkIV/Viewers/GtaIV/Pc/CutsceneLights.cs` - light clip track details for light bar inspection.
- `src/SparkIV/Viewers/AnimationView*.cs` - reference for clip detail fields/event presentation; reuse patterns, not cutscene playback logic.
- `docs/GtaIV/Pc/data-support.md` - existing source for `[TEXT]`, `[DRAW_DISTANCE]`, `[EFFECTS]`, CameraControl status.
- `docs/GtaIV/Pc/formats/cutscene-camera.md` - camera track/cut caveats.

## Validation checklist for implementation

- Timeline playhead stays in sync with slider during play, pause, scrub, section jump, subtitle click.
- Clicking subtitle marker selects the same subtitle row and seeks to same time as `SubtitleList_ItemClick`.
- Draw-distance markers appear at times consistent with `CutFile.DrawDistanceSteps`.
- Bar details report missing WAD/camera clip using existing notes.
- CameraControl 144 not shown by default in MVP; any 144-derived cut marker is Phase 2/experimental with provenance/validation status. `[EFFECTS]` not presented as supported unless parser/semantics are implemented and validated.
