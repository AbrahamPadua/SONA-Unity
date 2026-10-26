# SONA for Quest (Unity port of the Python prototype)

This folder is a C# port of the Python SONA pipeline in `Code/` (returns.py, frontend.py,
encoder.py, spatializer.py, scene.py, render.py, and the editor's trigger/repeat/random-box
behaviour from app.py), running on Quest 2 with real head tracking.

```
head pose + SonaSurfaces --frontend--> Return3D[] --encoder--> RenderPlan --renderer--> ears
```

## Run it

1. Open **SONA-quest** in Unity 6000.5.10f1 and wait for scripts to compile.
2. Menu **SONA > Set Up URP for Quest (fixes pink materials)**. The project had the URP package but no
   pipeline asset assigned, so Unity was running the deprecated Built-in pipeline and URP materials drew
   pink. This creates `Assets/SONA/Settings/SONA_URP_Quest.asset` (MSAA 4x, HDR off, depth/opaque
   textures off), assigns it to Graphics and every Quality level, and points the SONA materials at
   `SONA/Simple Lit` (see "White dots" below).
3. Menu **SONA > Create Quest Test Scene**. This makes `Assets/SONA/Scenes/SonaTestRoom.unity` with:
   - `OVRCameraRig` with **Tracking Origin = Floor Level** (the prefab ships as Eye Level),
   - a 5 x 6 x 3 m room with **no walls** (floor only) plus Box 1 and Panel 1, placed so you start
     where the Python listener stood, facing +Z,
   - the SONA controller, both renderers, the wave visualizer, the HUD, the laser pointer and the
     control bar; the scene is first in the build list.
4. **Meta > Tools > Project Setup Tool**, Android tab, **Fix All**.
5. Plug in the Quest, **File > Build Profiles > Android (Meta Quest)**, **Build And Run**.

**Testing in the editor without a headset:** press Play. The camera lifts to 1.6 m; hold the right
mouse button to look around, W/A/S/D to walk, left-click the control bar, Space to sweep.

## The dark world

You stand in pitch black: no sky, no lit room. What you can see:

- **The sweep** (Explore and Create room): every surface point flashes gold when the wave reaches it
  (brighter = louder in the mix, floor points dimmer), then glows cyan and fades out over 2.5 s; a ring on
  the floor shows the wavefront. **Never shown in Challenge**, so the box can only be found by ear.
  Turn it off for sound-only training with Sweep > Visuals.
- **Create room** draws the room dim and shaded (walls, floor, objects) so you can edit it; the selected
  object is cyan.
- **Challenge**: nothing until you guess; then the box appears in bright orange next to your green guess
  marker.
- The control bar, the laser and the status text (above eye level).

Room > Light > **Lit** shows the lit room for sighted demos; Blackout hides everything, the bar included.
The settings are on the **SONA Sweep Controller** (View: Dark World, Show Sweep Visuals) and the
**SONA Wave Visualizer** (dot size, flash, afterglow, brightness).

## Control bar

```
[:: SONA] [Explore] [Room] [Sweep] [Tools] [_] [X]
```

A small bar that opens below eye level in front of you (for the first few seconds it re-centres itself
while head tracking starts). Point with the right controller and pull the **right trigger** (or
left-click) to press a button.

- **Move it**: hold the trigger on `:: SONA` (or the bar's background) and point somewhere else; it
  follows the laser at the same distance and keeps facing you.
- **Fold it**: `_` shrinks it to `:: SONA [+] [X]`; `+` opens it again.
- **Close it**: `X`. The **left Menu button** (Tab) opens it in front of you again; pressed while the bar
  is open it closes it, or brings it back if you walked away from it.

Each tab opens a popup above the bar (press the tab again to close it):

| Tab | Contents |
|---|---|
| Mode (shows the current one) | Explore, Challenge, Create room |
| Room | Walls: no walls (default), open (3), closed (4). Size: 4 x 4, 5 x 6, 8 x 10 m. Light: dark (default), lit |
| Sweep | Sweep now, repeat, wave speed, renderer, pitch, wave shape (cylinder/sphere), sweep visuals on/off |
| Tools | What the mode needs (below) |

Objects stay where they are when you change walls or size (they are clamped inside the room).

- **Explore** tools: Reset objects (Box 1 + Panel 1), Blackout.
- **Challenge**: replaces the objects with one hidden 1 x 1 x 1 m box at least 1.5 m away (the
  Python Random Box). Sweep, then point at the floor where you think it is and pull the trigger: the
  box is revealed and the HUD shows the error (metres, bearing, distance). Every guess is appended
  to `sona_challenge_log.csv` in `Application.persistentDataPath` (on the Quest:
  `Android/data/<package>/files/`, readable from Meta Quest Developer Hub). Tools: New box (X),
  Reveal (Y), Blackout. Leaving Challenge brings your objects back.
- **Create room** tools: Add box, Add panel, Rotate 15 degrees, Delete, Width/Depth/Height in 0.5 m steps,
  Clear all, Reset. Hold the trigger on a box or panel to drag it along the floor.

## Controls

| Quest | Keyboard / mouse | Action |
|---|---|---|
| Right trigger | Left click | Press bar buttons, move the bar, drag objects, place a guess |
| Left Menu | Tab | Open/close the control bar (or bring it back in front of you) |
| Right A | Space | Trigger sweep (restarts any sweep) |
| Right B | R | Repeat on/off (each cycle re-pings from your current pose) |
| Right stick up/down | Up/Down | Wave speed 0.5x to 2.0x in 0.25 steps |
| Right stick click | M | Renderer: Stereo live head, Stereo frozen (Python), HRTF |
| Left X | N | Challenge: new hidden box |
| Left Y | V | Reveal/hide the box |
| Left stick left/right | [ ] | Wall layout: no walls, open, closed |
| Left stick click | P | Elevation to pitch on/off |
| Left trigger | B | Blackout: hide all visuals for blind trials |

All encoder, grain and clustering parameters are on the **SONA Sweep Controller** component,
with the Python defaults (near 0.4 m, sqrt foveation, 18 dB periphery, 0.14 s grains, and so on).

## The three renderers

- **Stereo, live head** (default): the Python grains and ITD/ILD panning, but each grain is aimed
  with your head pose at the moment it starts, so the scene stays fixed in the world while you
  turn (plan step 12).
- **Stereo, frozen (Python)**: azimuths fixed at ping time. Sample-identical to the Python render,
  so it rotates with your head. Use it as the A/B baseline for step 12.
- **HRTF**: grains are clustered (same 60 ms slot + 30 degree sector + elevation band) and played
  from their real world positions through the Meta XR Audio spatializer, so elevation and
  front/back come from a measured HRTF. Toggle pitch (P / left stick click) for step 13:
  "HRTF only" vs "HRTF + elevation to pitch". Meta XR Audio renders at most 64 voices, so the
  clusterer widens its time slots until a sweep fits in 56; the HUD shows clusters, voices and
  anything dropped.

**Spherical wave** (step 14): tick `Spherical Wave` on the controller. Off is the Python
cylindrical wave (timing from horizontal distance).

## Mapping from the Python files

| Python | C# |
|---|---|
| returns.py (`Return`, `ListenerPose`) | `Core/SonaTypes.cs` |
| frontend.py (`sample_quad`, `surface_emission`) | `Core/SonaFrontend.cs`, `Runtime/SonaSurface.cs` |
| encoder.py | `Core/SonaEncoder.cs` |
| spatializer.py (pitch, grains, ITD/ILD, mix gain, peak limit) | `Core/SonaPitch.cs`, `Core/SonaGranular.cs` |
| scene.py (presets, boxes, panels, custom rooms, random box) | `Core/SonaRoomModel.cs`, `Runtime/SonaRoomBuilder.cs` |
| app.py `render_sweep`, trigger, repeat, wave speed | `Runtime/SonaSweepController.cs` |
| audio_player.py | `Runtime/SonaStereoRenderer.cs` (real-time mixer on Unity's audio thread) |
| render.py | menu **SONA > Export Python-Parity sweep.wav** |
| (new) HRTF rendering | `Core/SonaClusterer.cs`, `Runtime/SonaHrtfRenderer.cs` |

Everything in `Scripts/Core` is plain C# with no Unity dependency, like the Python encoder.

## How it was checked

Against the Python code itself on 7 rooms (all presets plus a moved/rotated listener and
rotated objects, at 0.5x, 1x and 1.5x speed):

- returns and render plans match to about 1e-15;
- the offline render is bit-identical to `GranularBinauralSpatializer.render` (max diff 0.0),
  and **SONA > Export Python-Parity sweep.wav** writes the same samples as `python render.py`;
- the real-time mixer matches the Python buffer within 1.5e-6 (float32), and its gapless repeat
  matches the Python loop;
- the room builder's Unity geometry reproduces scene.py's surfaces to 1e-15 m;
- turning the head right moves a straight-ahead source to the left ear in live mode;
- HRTF voice allocation fits every test room in 56 voices with nothing dropped, including
  back-to-back repeats.

The harness is in `Tools/SonaParity` (outside Assets, so Unity ignores it).

## Things to know

- **Pink materials** mean no URP asset is assigned: run **SONA > Set Up URP for Quest**.
- **White dots on surfaces (editor only)**: a Unity bug with URP Lit on Intel Iris Xe graphics under
  Direct3D 11 while the Android target is active (URP uses half precision then). The SONA floor, wall and
  object materials use `Assets/SONA/Shaders/SonaSimpleLit.shader`, which is URP Simple Lit compiled at
  full precision on Direct3D only; on the Quest it is identical to URP Simple Lit. If you add your own
  materials and see dots, give them the `SONA/Simple Lit` shader.
- **Tracking origin must be Floor Level** (the setup menu sets it). The HUD warns if your head
  is under 0.5 m, which usually means Eye Level.
- Geometry is still X-ray (no occlusion), exactly like the Python prototype.
- Any GameObject with a **SONA Surface** component is heard: `Box` (Cube primitive), `Quad`
  (Quad primitive), or `Mesh` (any Read/Write-enabled mesh, area-sampled; for scanned rooms later).
  Tag a floor with surface tag `floor` to get the low floor tone.
- In HRTF mode a repeat can start a few tens of milliseconds late if voices are still busy;
  stereo repeats are sample-exact.
- Keep the Unity project out of OneDrive if you can: OneDrive locks files during imports and
  syncs the multi-GB Library folder. Use git for backup instead.
- **Android build fails with `java.nio.file.AccessDeniedException ... arm64-v8a`** (in a Gradle task
  such as `mergeReleaseNativeLibs` or `copyReleaseJniLibsProjectOnly`): OneDrive is holding Gradle's
  intermediate files. Delete `Library/Bee/Android/Prj/IL2CPP/Gradle/unityLibrary/build`,
  `.../Gradle/launcher/build` and `.../Gradle/build`, then build again (pausing OneDrive while you
  build also helps). Moving the project out of OneDrive stops it for good.
