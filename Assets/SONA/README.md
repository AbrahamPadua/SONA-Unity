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
     menu; the scene is first in the build list.
4. **Meta > Tools > Project Setup Tool**, Android tab, **Fix All**.
5. Plug in the Quest, **File > Build Profiles > Android (Meta Quest)**, **Build And Run**.

**Testing in the editor without a headset:** press Play. The camera lifts to 1.6 m. Hold the right
mouse button to look around, use W/A/S/D to walk, click menu buttons, press Space to sweep and Tab
to open or close the menu.

## Controls

The controller uses three buttons. Everything else is in the menu.

| Quest | Keyboard / mouse | Action |
|---|---|---|
| Right A | Space | Sweep (restarts any sweep) |
| Right trigger | Left click | Press the menu button you point at. In Challenge, point at the floor to guess, then pull again for the next box |
| Left Menu | Tab | Open or close the menu. It also ends blackout |

While the menu is open the trigger only works on the menu, so a missed button never counts as a
guess. Starting a box closes the menu for you. When a trigger pull does nothing (menu open, not in
Challenge, or not pointing at the floor), the HUD says why.

## The HUD

Floating text above eye level, in three parts:

- a status line: the mode, and in Challenge the trial number and how many sweeps you used;
- your last result in Challenge, or the latest message for a few seconds;
- a hint for what to press next, with the buttons of the device you are using.

Researcher numbers (renderer, room, returns, compute time, HRTF voices) appear only with
Sweep > Research > Diagnostics.

## The dark world

You stand in pitch black: no sky and no lit room. What you can see:

- **The sweep** (Explore): every surface point flashes gold when the wave reaches it (brighter means
  louder in the mix, floor points dimmer), then glows cyan and fades out over 2.5 s. A ring on the
  floor shows the wavefront. Challenge never shows it, so you find the box by ear.
  Turn it off for sound-only training with Sweep > Show > Visuals.
- **Challenge**: nothing until you guess; then the box appears in bright orange next to your green
  guess marker.
- The menu, the laser and the HUD.

Room > Light > **Lit** shows the lit room for sighted demos. Trial > **Blackout** hides everything,
the menu included; press Menu (Tab) to end it. The settings are on the **SONA Sweep Controller**
(View: Dark World, Show Sweep Visuals) and the **SONA Wave Visualizer** (dot size, flash, afterglow,
brightness).

## The menu

```
[Move] [Mode] [Trial] [Sweep] [Room] [Help] [Close]
```

The menu opens below eye level in front of you, with Help showing on first launch. For the first
few seconds it re-centres itself while head tracking starts. To move it, hold the trigger on
`Move` (or the bar's background) and point somewhere else; it follows the laser at the same
distance and keeps facing you. If you turn more than 60 degrees away from it, walk more than 2.5 m
off, or the headset recenters, it comes back after a second to the same spot in front of you (the
spot you dragged it to, if you moved it). Looking down at the floor does not move it. Menu (Tab)
opens it in front of you again.

Each tab opens a popup above the bar; press the tab again to close it.

| Tab | Contents |
|---|---|
| Mode | Explore, Challenge |
| Trial | Next box, Reveal box, Blackout. Next box starts Challenge if you are in Explore |
| Sweep | Sweep now, Repeat, Slower/Faster (0.5x to 2x). Research row: renderer (Stereo or HRTF), pitch (follows height or fixed), Diagnostics |
| Room | Walls: no walls (default), open (3), closed (4). Size: 4 x 4, 5 x 6, 8 x 10 m. Light: dark (default), lit. Objects: Reset |
| Help | The controls and what each mode is for |

Objects stay where they are when you change walls or size (they are clamped inside the room).

**Challenge** replaces the objects with one hidden 1 x 1 x 1 m box at least 1.5 m away (the Python
Random Box). Starting a box (Next box, or choosing Challenge) closes the menu. Sweep, then point at
the floor where you think the box is and pull the trigger. The box is revealed and the HUD shows
how far off you were, with the bearing and distance errors. Pull the trigger again for the next box. Every guess is appended to
`sona_challenge_log.csv` in `Application.persistentDataPath` (on the Quest:
`Android/data/<package>/files/`, readable from Meta Quest Developer Hub). Leaving Challenge brings
your objects back.

All encoder, grain and clustering parameters are on the **SONA Sweep Controller** component,
with the Python defaults (near 0.4 m, sqrt foveation, 18 dB periphery, 0.14 s grains, and so on).

## The two renderers

- **Stereo** (default): the Python grains and ITD/ILD panning, but each grain is aimed with your
  head pose at the moment it starts, so the scene stays fixed in the world while you turn.
- **HRTF**: grains are clustered (same 60 ms slot + 30 degree sector + elevation band) and played
  from their real world positions through the Meta XR Audio spatializer, so elevation and
  front/back come from a measured HRTF. Set pitch to fixed (Sweep > Research) to compare
  "HRTF only" with "HRTF + height to pitch". Meta XR Audio renders at most 64 voices, so the
  clusterer widens its time slots until a sweep fits in 56; Diagnostics shows clusters, voices and
  anything dropped.

**Spherical wave**: tick `Spherical Wave` on the controller. Off is the Python cylindrical wave
(timing from horizontal distance).

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
- the real-time mixer matches the Python buffer within 1.5e-6 (float32) with head tracking off,
  and its gapless repeat matches the Python loop;
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
