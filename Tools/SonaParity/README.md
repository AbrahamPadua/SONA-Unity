# SONA parity harness

Checks that the Unity C# core (`Assets/SONA/Scripts/Core`) still reproduces the Python prototype.

    powershell -ExecutionPolicy Bypass -File run_parity.ps1

1. `py_reference.py` runs the original Python code (`../../../../Code`, or set `SONA_PY_CODE`)
   on 7 rooms and saves returns, plans and the rendered audio to `ref/`.
2. `dotnet run -- cs_out` runs the same rooms through the C# core, plus C#-only checks (Unity room
   geometry, real-time mixer, gapless repeat, HRTF voice allocation, world anchoring, the Proposal 1.2
   study stimuli and staircase) and writes `cs_out/`.
3. `compare.py ref cs_out` diffs the two. Expect `C# CHECKS: ALL PASS` and `ALL PASS`.

Step 2 also runs the study audit (`StudyAudit.cs`), which takes about 45 seconds. It renders 9,600
trials across every study condition, both directions and the full range of differences, measures
each interval from the output, and writes one row per interval to `cs_out/study_audit.csv`. It
checks onsets, DRR, level, clipping and that no level or timing feature picks out the odd interval
better than chance.

Needs Python with numpy (your `.sona_env` works) and the .NET 8 SDK.
