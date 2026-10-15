# SONA for Meta Quest

SONA is a Unity port of the echolocation prototype for Meta Quest. It uses head
tracking for spatial audio, with exploration and challenge modes and a virtual
room you can edit.

## Open and run

1. Install Unity 6000.5.10f1 with Android Build Support, including the Android
   SDK, NDK, and OpenJDK.
2. Add this repository's root folder as a project in Unity Hub. Wait for Unity to
   restore the dependencies in `Packages/manifest.json` and `Packages/packages-lock.json`.
3. Open `Assets/SONA/Scenes/SonaTestRoom.unity` and press Play to use the desktop
   controls. To run it on a developer-enabled Meta Quest, select Android in Build
   Profiles and build to the headset.

For setup menus, headset and desktop controls, rendering modes, and
troubleshooting, see [the SONA guide](Assets/SONA/README.md).

## Project layout

- `Assets/SONA`: the C# audio pipeline, runtime controls, editor tools, shaders, and test room.
- `Assets/XR`, `Assets/Resources`, `Assets/Oculus`, `Assets/Plugins`: XR, audio, and Android configuration.
- `Packages`, `ProjectSettings`: pinned dependencies and shared Unity settings.
- `Tools/SonaParity`: optional checks that compare the C# pipeline with the Python prototype. See the [instructions](Tools/SonaParity/README.md).
- `Tools/SonaDiagnostics`: source for graphics diagnostics in the Unity editor.

The Unity app runs on its own. To run the full parity checks, you also need the
Python prototype, Python with NumPy, and the .NET 8 SDK. Set `SONA_PY_CODE` to the
prototype's source directory if it is elsewhere.

## Local files

Unity caches, APKs and other build output, recovery scenes, generated IDE
projects, parity output, signing keys, and environment files stay out of Git.
Configure Meta developer agent and platform publishing settings locally through
the Meta tools when you need them. Keep access tokens out of tracked assets.

The project license is in [LICENSE](LICENSE).
