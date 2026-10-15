# SONA for Meta Quest

A Unity port of the SONA echolocation prototype, with head-tracked spatial audio,
exploration and challenge modes, and an editable virtual room.

## Open and run

1. Install **Unity 6000.5.10f1** with **Android Build Support**, including the Android
   SDK, NDK, and OpenJDK.
2. Add this repository's root folder as a project in Unity Hub. Let Unity restore
   the dependencies declared in `Packages/manifest.json` and `Packages/packages-lock.json`.
3. Open `Assets/SONA/Scenes/SonaTestRoom.unity`. Press Play for desktop controls,
   or select Android in Build Profiles and build to a developer-enabled Meta Quest.

See [the SONA guide](Assets/SONA/README.md) for setup menus, headset and desktop
controls, rendering modes, and troubleshooting.

## Project layout

- `Assets/SONA`: C# audio pipeline, runtime controls, editor tools, shaders, and test room.
- `Assets/XR`, `Assets/Resources`, `Assets/Oculus`, `Assets/Plugins`: XR, audio, and Android configuration.
- `Packages`, `ProjectSettings`: pinned dependencies and shared Unity settings.
- `Tools/SonaParity`: optional C# and Python parity checks; see its [instructions](Tools/SonaParity/README.md).
- `Tools/SonaDiagnostics`: editor graphics diagnostic source.

The Unity application runs independently of the Python prototype. Full parity
checks additionally require that prototype, Python with NumPy, and the .NET 8 SDK.
Set `SONA_PY_CODE` to the prototype's source directory when it is elsewhere.

## Local files

Git excludes Unity caches, APKs and other build output, recovery scenes, generated
IDE projects, parity output, signing keys, and environment files. Meta developer
agent settings and platform publishing settings are also local: configure these
through the Meta tools when needed, and keep access tokens out of tracked assets.

See [LICENSE](LICENSE) for licensing.
