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
[Move] [Mode] [Trial] [Study] [Sweep] [Room] [Help] [Close]
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
| Mode | Explore, Challenge, Study |
| Trial | Next box, Reveal box, Blackout. Next box starts Challenge if you are in Explore |
| Study | Participant number, session (1 to 5), a Main row (Bank, Training, Distance, Question) and a Pilot row (Fusion, Single cues, Cue axes, Pairs) of blocks, Stop, Ground on/off. The text below shows the block's progress and where each staircase stands |
| Sweep | Sweep now, Repeat, Slower/Faster (0.5x to 2x). Research row: renderer (Stereo or HRTF), pitch (follows height or fixed), Diagnostics |
| Room | Walls: no walls (default), open (3), closed (4). Size: 4 x 4, 5 x 6, 8 x 10 m. Light: dark (default), lit. Objects: Reset |
| Help | The controls and what each mode is for |

Objects stay where they are when you change walls or size (they are clamped inside the room).

**Challenge** replaces the objects with one hidden 1 x 1 x 1 m box at least 1.5 m away (the Python
Random Box). Starting a box (Next box, or choosing Challenge) closes the menu. Sweep, then point at
the floor where you think the box is and pull the trigger. The box is revealed and the HUD shows
how far off you were, with the bearing and distance errors. Pull the trigger again for the next box.
Leaving Challenge brings your objects back.

Every guess is appended to `sona_challenge_log_v2.csv` in `Application.persistentDataPath` (on the
Quest: `Android/data/com.DefaultCompany.SONAquest/files/`). Trial numbers restart at every launch,
so the first column, `session`, holds the launch time to keep sessions apart. The scoring is in
`Core/SonaChallenge.cs`, and the parity harness checks it. Older builds wrote `sona_challenge_log.csv`
without the session column; that file is left in place.

**Copy the log off the headset after every session.** Android deletes the app's folder on uninstall,
and the app turns off Android backup. Uninstalling is also the usual way out when the headset refuses
an update built on another computer. Copy the log with:

```
adb pull /sdcard/Android/data/com.DefaultCompany.SONAquest/files/sona_challenge_log_v2.csv
```

## Study mode (Proposals 1.2 and 1.3)

Study runs the listening test of Proposal 1.3: after training, does SONA's learned sweep cue combine with the
direct-to-reverberant ratio (DRR)? The participant sits facing forward with wired headphones in the headset.
There are no live sweeps in this mode. Each trial plays pre-rendered sweeps of one box, built so that only the
box's timing and its DRR carry distance (`Core/SonaStudy.cs`). The design is in the Protocol tab of
Proposal 1.3, and the calculations and their sources are in `AudioSweep/SONA Proposal 1.2 implementation plan.md`.

1. Open the menu, choose the Study tab, and set the participant number and the session (the visit).
2. Press a block. The menu closes and the block begins. The first time you enter Study the app prepares the
   sounds, which takes a few seconds on the headset.
3. Hand the controller over. The HUD says what to do; it changes only between trials.

The Main row is a visit of Proposal 1.3. Test visits (1, 4 and 5) run Bank, Distance and Question; training
visits (2 and 3) run Training, Distance and Question.

| Block | What the participant does |
|---|---|
| Bank | Three sweeps, one different. Point at the sphere for the odd one and pull the trigger. No feedback. 228 trials, about 36 minutes |
| Training | One sweep. Point at the floor line where the box starts. The box then appears with the error in percent. 240 trials: half with the sweep cue alone, half with DRR agreeing |
| Distance | One sound, answered on the floor line as in training but with no feedback. 12 with the sweep cue alone (DRR held at its 2 m value) and 12 with DRR alone (no click, no floor tone, random onset) |
| Question | One question on the HUD: did the distances sound like something out there (left sphere) or like a sound pattern translated into a distance (right sphere)? |

The Pilot row is for the pilot only:

| Block | What the participant does |
|---|---|
| Fusion, Single cues, Cue axes | The oddity task run by staircases: congruent and opposite changes; delay alone (no reverb) or DRR alone (no click, random onset); both cues present with only one changing. The pilot uses them to set the bank's sizes and k* |
| Pairs | Two sounds: which box was farther? Left sphere for the first, right for the second. 30 pairs per cue, each cue alone as in the distance checks, the two distances a factor of 2 apart (`PairStep`, a placeholder) |

Every test visit plays the same bank of 228 trials. Distances are ln(d / 2 m) for each cue, and the odd
sweep moves along one of six unit directions: the sweep axis, the DRR axis, matching (1, 1)/sqrt(2), and
opposing (1, -k)/sqrt(1 + k^2) for k = k*/2, k* and 2k*. Each direction has 3 sizes, 2 signs, 3 odd positions
and 2 repeats, which makes 216 trials. The two standards sit at 2 m times e^-0.1, 1 or e^0.1, rotated so that
every cell and every odd position meets each reference equally often. Twelve catch trials move as far along
each axis as the sounds allow. The gains are frozen with the bank, and the order is shuffled once per
participant and visit, so a restarted visit plays the same order again. Every bank interval has the click, the
floor tone and the room. k* (`OpposingRatio`) and the sizes (`SweepAxisSizes`, `DrrAxisSizes`, `PairedSizes`)
in `StudyParams` are placeholders: the pilot sets them, and they are then frozen for the study. If a setting
would push a trial out of the sweep or the DRR range, the block stops with a message instead of playing.

Each staircase block runs four interleaved staircases of 65 trials (`trialsPerStaircase` on the SONA Study
component) with the rule of Negen et al. (2023): the difference shrinks by 0.9 after a right answer and
grows by 0.9^-3 after a wrong one, which settles at 75% correct. Each staircase stops growing at the largest
difference the sounds allow. Differences are lengths along a unit direction, so a congruent change of 0.3
moves each cue by 0.21. Training has 240 trials (`trainingTrials`).

The study's sweep matches Explore's default room: a 5 x 6 m floor in front of you, with the wave reaching
its far corner (6.5 m) at the end of the 1.2 s sweep, so a box at 2 m sounds 666 ms after the click.
Intervals with a click also play that floor's low sweep, the ground sound from Explore. It starts with the
click, sounds the same in every interval apart from the level roving, and keeps its level relative to the box
from the app's own mix. DRR-alone intervals never get it, because its start would mark the onset they hide.
Like the rest of SONA, the box does not block the floor behind it. **Ground** (Study tab, off by default) only
affects the staircase blocks, so the pilot can compare the floor tone on and off; every other block always
plays it. The setting applies from the next block, so one staircase never mixes trials with and without it.
Unlike Explore, the study's sounds are pre-rendered, so they do not follow your head.

Pressing Menu pauses the block, and so does taking the headset off. In a staircase block the trial in
progress is thrown away and a new one starts when the menu closes; in the other blocks the same trial plays
again. Each answered trial is appended to `sona_study_log.csv`, next to the challenge log: participant,
session, block, condition, the difference (staircase value or bank size), sign, unit direction, the bank's
reference, catch flag and trial number, the odd interval, the answer, reaction time, the target and answered
distance for floor-line answers, and for every interval its delay distance, DRR, onset, gain, level, peak and
onset sample. For Pairs, the odd interval is the farther sound; for the question, answer 1 is "out there" and
2 is "translated". It also records the sample rate, the headset's media volume step, where the sound went
(`wired`, `bluetooth` or `speaker`) and whether the ground sound played, so a bumped volume button or a
session without headphones shows up in the data.
If a newer build changes the columns, the old file is kept as `sona_study_log_old_<date>.csv` and a new one
starts. Copy the logs off with `adb pull` after every visit, like the challenge log.

The project's audio runs at 48 kHz (Project Settings > Audio > System Sample Rate). Left at the system
default, the Quest plays at 24 kHz, which cuts everything above 12 kHz. Listeners hear DRR mainly through the
spectrum (Larsen et al. 2008), so the study needs the full band.

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
| render.py | `GranularRenderer.RenderOffline` in `Core/SonaGranular.cs` |
| (new) HRTF rendering | `Core/SonaClusterer.cs`, `Runtime/SonaHrtfRenderer.cs` |
| (new) Proposal 1.2 and 1.3 study | `Core/SonaStudy.cs`, `Core/SonaStaircase.cs`, `Runtime/SonaStudyController.cs`; observer simulations in `Tools/SonaStudy/observers.py` |

Everything in `Scripts/Core` is plain C# with no Unity dependency, like the Python encoder.

## How it was checked

Against the Python code itself on 7 rooms (all presets plus a moved/rotated listener and
rotated objects, at 0.5x, 1x and 1.5x speed):

- returns and render plans match to about 1e-15;
- the offline render is bit-identical to `GranularBinauralSpatializer.render` (max diff 0.0);
- the real-time mixer matches the Python buffer within 1.5e-6 (float32) with head tracking off,
  and its gapless repeat matches the Python loop;
- the room builder's Unity geometry reproduces scene.py's surfaces to 1e-15 m;
- turning the head right moves a straight-ahead source to the left ear in live mode;
- HRTF voice allocation fits every test room in 56 voices with nothing dropped, including
  back-to-back repeats;
- the study sounds play the requested DRR, level and onset, measured from the output, and an audit of
  9,600 trials finds no level, loudness or onset feature that picks out the odd interval, and no peak
  feature when only the delay changes. Where the DRR changes, the box's peak moves with it (at the same
  level, a box that is mostly reverb peaks up to 1.5 dB higher), so the audit counts the peak as part of
  the DRR cue;
- the bank holds 216 trials and 12 catch trials, balanced as described above, and a second audit renders all
  228 as they play, floor tone on. Every box starts on its planned sample, the DRR and level match their
  requests to within 1e-7 dB (the rendered box's DRR sits a fixed 0.99 dB off its request, the same in every
  interval), and the floor tone is identical in every interval apart from the gain. No gain, level or loudness
  feature picks out the odd interval, nor does the peak where the DRR stays fixed or the duration where the
  timing stays fixed. With the gain taken out, the odd
  interval's whole level differs from the standards by under 0.001 dB. Only the level in the box's own window
  changes (up to 2.2 dB, 3.7 dB on catch trials), and only where the timing changes, because the box then sits
  on a different part of the floor tone. Whether that matters to a listener is the pilot's floor-tone check.

The parity harness also writes the bank to `cs_out/study_bank.csv`, which `Tools/SonaStudy/observers.py`
reads to simulate the four observer models on the trials the headset plays.

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
- Any GameObject with a **SONA Surface** component is heard: `Box` (Cube primitive) or `Quad`
  (Quad primitive). Tag a floor with surface tag `floor` to get the low floor tone.
- In HRTF mode a repeat can start a few tens of milliseconds late if voices are still busy;
  stereo repeats are sample-exact.
- Keep the Unity project out of OneDrive if you can: OneDrive locks files during imports and
  syncs the multi-GB Library folder. Use git for backup instead.
- **Android build fails with `java.nio.file.AccessDeniedException ... arm64-v8a`** (in a Gradle task
  such as `mergeReleaseNativeLibs` or `copyReleaseJniLibsProjectOnly`): OneDrive is holding Gradle's
  intermediate files. Delete `Library/Bee/Android/Prj/IL2CPP/Gradle/unityLibrary/build`,
  `.../Gradle/launcher/build` and `.../Gradle/build`, then build again (pausing OneDrive while you
  build also helps). Moving the project out of OneDrive stops it for good.
