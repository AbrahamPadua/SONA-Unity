// Parity harness: runs the C# SONA core on the same cases as py_reference.py and writes raw outputs.
using System;
using System.Collections.Generic;
using System.IO;
using Sona.Core;

static class Program
{
    sealed class Case
    {
        public string Name;
        public string Preset;
        public double[] Custom;          // w, d, h
        public WallMode CustomWalls;
        public double[] Listener;        // x, y, z, yawDeg (scene.py frame)
        public Dictionary<string, double[]> Objects = new Dictionary<string, double[]>(); // id -> x, y, yaw
        public double Speed = 1.0;
    }

    static List<Case> Cases()
    {
        var c = new List<Case>
        {
            new Case { Name = "open_demo", Preset = "open_demo" },
            new Case { Name = "enclosed_demo", Preset = "enclosed_demo" },
            new Case { Name = "no_walls_demo", Preset = "no_walls_demo" },
            new Case { Name = "open_large", Preset = "open_large" },
            new Case { Name = "custom_8x12x4_closed", Custom = new[] { 8.0, 12.0, 4.0 }, CustomWalls = WallMode.Closed },
            new Case { Name = "open_demo_moved", Preset = "open_demo", Listener = new[] { 1.2, 1.0, 1.5, 37.0 }, Speed = 1.5 },
            new Case { Name = "enclosed_compact_back", Preset = "enclosed_compact", Listener = new[] { 0.3, 2.0, 1.6, -120.0 }, Speed = 0.5 },
        };
        c[5].Objects["box-1"] = new[] { -1.0, 3.2, 30.0 };
        c[5].Objects["panel-1"] = new[] { 1.4, 4.1, 75.0 };
        return c;
    }

    static RoomLayout Build(Case c)
    {
        RoomLayout layout = c.Custom != null
            ? SonaRoomModel.MakeRoom(c.Custom[0], c.Custom[1], c.Custom[2], c.CustomWalls, true)
            : SonaRoomModel.MakePreset(c.Preset);
        if (c.Listener != null)
        {
            layout.ListenerX = c.Listener[0];
            layout.ListenerY = c.Listener[1];
            layout.ListenerZ = c.Listener[2];
            layout.ListenerYawDegrees = SonaMath.FloorMod(c.Listener[3], 360.0);
        }
        foreach (var kv in c.Objects)
        {
            ObjectSpec o = layout.Objects.Find(x => x.Id == kv.Key);
            o.X = kv.Value[0];
            o.Y = kv.Value[1];
            o.YawDegrees = SonaMath.FloorMod(kv.Value[2], 360.0);
            SonaRoomModel.ClampObject(layout.Room, o);
        }
        return layout;
    }

    static void WriteDoubles(string path, List<double[]> rows)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(rows.Count);
            w.Write(rows.Count > 0 ? rows[0].Length : 0);
            foreach (var r in rows) foreach (var v in r) w.Write(v);
        }
    }

    static void WriteFloats(string path, float[] data)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(data.Length);
            foreach (var v in data) w.Write(v);
        }
    }

    static int Main(string[] args)
    {
        string outDir = args[0];
        Directory.CreateDirectory(outDir);
        var meta = new List<string>();
        foreach (Case c in Cases())
        {
            RoomLayout layout = Build(c);
            double far = Math.Max(SonaRoomModel.FarthestCornerDistance(layout), 1e-6);
            ListenerPose3D pose = SonaRoomModel.ListenerPose(layout);
            List<SurfaceQuad> quads = SonaRoomModel.SurfaceQuads(layout);
            var returns = SonaFrontend.SurfaceEmission(quads, pose, 5.0, far + 1e-9);
            var ep = new EncodingParams { Near = 0.4, Far = far, SweepDuration = 1.2 / c.Speed, FoveationExponent = 0.5 };
            var plan = SonaEncoder.Encode(returns, ep);
            var gp = new GranularParams { SampleRate = 44100 };
            var bank = new GrainBank(gp);
            var pitch = new PitchMapper();
            double outputGain, rawPeak;
            float[] offline = GranularRenderer.RenderOffline(plan, ep.SweepDuration, bank, pitch, out outputGain, out rawPeak);

            // Real-time block mixer, frozen azimuths (must equal the offline render).
            GrainEvent[] events = GranularRenderer.PrepareEvents(plan, bank, pitch);
            int frames = offline.Length / 2;
            float[] frozen = RunMixer(events, bank, gp, outputGain, frames, false, null);

            // Live head tracking with the head held at the ping pose (must match within ITD rounding).
            var head = new HeadPoseSnapshot(pose.Position, QuatD.FromYawDegrees(layout.ListenerYawDegrees));
            float[] live = RunMixer(events, bank, gp, outputGain, frames, true, head);

            var rrows = new List<double[]>();
            foreach (var r in returns)
                rrows.Add(new[] { r.WorldPosition.X, r.WorldPosition.Y, r.WorldPosition.Z, r.Azimuth, r.Elevation, r.Range, r.PropagationRange });
            var prows = new List<double[]>();
            foreach (var e in plan)
            {
                bool fl;
                double f = pitch.FrequencyOf(e, out fl);
                prows.Add(new[] { e.Onset, e.WorldPosition.X, e.WorldPosition.Y, e.WorldPosition.Z, e.Azimuth, e.Elevation, e.Amplitude, f });
            }
            WriteDoubles(Path.Combine(outDir, c.Name + "_returns.bin"), rrows);
            WriteDoubles(Path.Combine(outDir, c.Name + "_plan.bin"), prows);
            WriteFloats(Path.Combine(outDir, c.Name + "_audio.bin"), offline);
            WriteFloats(Path.Combine(outDir, c.Name + "_mixer_frozen.bin"), frozen);
            WriteFloats(Path.Combine(outDir, c.Name + "_mixer_live.bin"), live);

            // HRTF clustering stats (no Python equivalent; sanity + budget check).
            var clusters = SonaClusterer.Build(plan, events, bank, pose, outputGain, new ClusterParams());
            int members = 0;
            foreach (var cl in clusters.Clusters) members += cl.Members;
            // ---- Extra checks (C#-only paths) ----
            // (a) GranularGain.Measure equals the offline render's gain.
            double measured = GranularGain.Measure(plan, ep.SweepDuration, bank, pitch);
            Check(Math.Abs(measured - outputGain) < 1e-15, c.Name + ": GranularGain.Measure == RenderOffline gain");

            // (b) Unity geometry path: room-builder placements -> primitive faces -> samples == scene.py quads.
            var placementQuads = new List<SurfaceQuad>();
            foreach (PrimitivePlacement pl in SonaRoomModel.Placements(layout))
            {
                PrimitivePlacement local = pl;
                QuatD rot = QuatD.Euler(pl.EulerDegrees.X, pl.EulerDegrees.Y, pl.EulerDegrees.Z);
                Func<Vec3d, Vec3d> toWorld = q => local.ToLocal(q);
                if (pl.Shape == PrimitiveShape.Box)
                    SonaPrimitives.AppendBoxQuads(placementQuads, toWorld, rot.Rotate(new Vec3d(1, 0, 0)), rot.Rotate(new Vec3d(0, 1, 0)), rot.Rotate(new Vec3d(0, 0, 1)), pl.SourceId, pl.Tag);
                else
                    SonaPrimitives.AppendQuadQuad(placementQuads, toWorld, rot.Rotate(new Vec3d(0, 0, -1)), pl.SourceId, pl.Tag);
            }
            List<SurfaceQuad> refQuads = SonaRoomModel.SurfaceQuads(layout, layout.ListenerX, layout.ListenerY);
            Check(placementQuads.Count == refQuads.Count, c.Name + ": placement quad count " + placementQuads.Count + " == " + refQuads.Count);
            double maxPt = 0, maxN = 0;
            int ptMismatch = 0;
            for (int qi = 0; qi < Math.Min(placementQuads.Count, refQuads.Count); qi++)
            {
                var a = new List<Vec3d>(); var b = new List<Vec3d>();
                SonaFrontend.SampleQuad(placementQuads[qi], 5.0, a);
                SonaFrontend.SampleQuad(refQuads[qi], 5.0, b);
                if (a.Count != b.Count) { ptMismatch++; continue; }
                Comparison<Vec3d> cmp = (u, v) => { int k = Math.Round(u.X, 6).CompareTo(Math.Round(v.X, 6)); if (k != 0) return k; k = Math.Round(u.Y, 6).CompareTo(Math.Round(v.Y, 6)); return k != 0 ? k : Math.Round(u.Z, 6).CompareTo(Math.Round(v.Z, 6)); };
                a.Sort(cmp); b.Sort(cmp);
                for (int k = 0; k < a.Count; k++) maxPt = Math.Max(maxPt, (a[k] - b[k]).Length);
                maxN = Math.Max(maxN, (placementQuads[qi].Normal - refQuads[qi].Normal).Length);
                if (SonaMaterials.IsFloor(placementQuads[qi].Material) != SonaMaterials.IsFloor(refQuads[qi].Material)) ptMismatch++;
            }
            Check(ptMismatch == 0 && maxPt < 1e-9 && maxN < 1e-9, string.Format("{0}: placement samples match scene.py (max point err {1:E1}, normal err {2:E1}, count/tag mismatches {3})", c.Name, maxPt, maxN, ptMismatch));

            // (c) Gapless repeat: A then PlayAfterCurrent(A) == offline render twice; CancelQueued leaves A then silence.
            {
                var mixer = new StereoBlockMixer(gp.SampleRate);
                MixSweep A = NewSweep(1, events, bank, gp, outputGain, frames, false);
                MixSweep B = NewSweep(2, events, bank, gp, outputGain, frames, false);
                mixer.Play(A, 0.0);
                mixer.PlayAfterCurrent(B);
                float[] two = RunBlocks(mixer, gp.SampleRate, frames * 2 + 3000);
                double err = 0;
                for (int i = 0; i < frames * 2; i++)
                {
                    err = Math.Max(err, Math.Abs(two[i] - offline[i]));
                    err = Math.Max(err, Math.Abs(two[frames * 2 + i] - offline[i]));
                }
                double tail = 0;
                for (int i = frames * 4; i < two.Length; i++) tail = Math.Max(tail, Math.Abs(two[i]));
                Check(err < 1e-5 && tail == 0, string.Format("{0}: gapless repeat == Python loop (max err {1:E1})", c.Name, err));

                var m2 = new StereoBlockMixer(gp.SampleRate);
                m2.Play(NewSweep(3, events, bank, gp, outputGain, frames, false), 0.0);
                m2.PlayAfterCurrent(NewSweep(4, events, bank, gp, outputGain, frames, false));
                float[] part1 = RunBlocks(m2, gp.SampleRate, 4096);   // A has started, B is queued
                m2.CancelQueued();
                float[] part2 = RunBlocks(m2, gp.SampleRate, frames * 2, 4096);
                double after = 0;
                for (int i = (frames - 4096) * 2; i < part2.Length; i++) after = Math.Max(after, Math.Abs(part2[i]));
                double before = 0;
                for (int i = 0; i < 4096 * 2; i++) before = Math.Max(before, Math.Abs(part1[i] - offline[i]));
                Check(after == 0 && before < 1e-5, c.Name + ": CancelQueued lets the current sweep finish and drops the next");
            }

            // (d) HRTF voice allocation: no overlaps on a voice, nothing dropped, energy preserved.
            {
                var clustersR = SonaClusterer.Build(plan, events, bank, pose, outputGain, new ClusterParams());
                var alloc = new HrtfVoiceAllocator(56);
                int dropped;
                var plays = alloc.Allocate(clustersR, 7, 10.0, 0.01, 0.06, out dropped);
                double clusterEnergy = 0, playEnergy = 0;
                foreach (var cl in clustersR.Clusters) foreach (var v in cl.Samples) clusterEnergy += (double)v * v;
                bool overlapsOk = true;
                foreach (var pl in plays)
                {
                    for (int k = 1; k < pl.Segments.Count; k++)
                        if (pl.Segments[k].StartDsp < pl.Segments[k - 1].EndDsp + 0.01 - 1e-9) overlapsOk = false;
                    foreach (var v in pl.Samples) playEnergy += (double)v * v;
                }
                // second sweep right after the first cycle (repeat) must also fit
                int dropped2;
                double cycle = frames / (double)gp.SampleRate;
                var plays2 = alloc.Allocate(clustersR, 8, 10.0 + cycle, 0.01, 0.06, out dropped2);
                Check(overlapsOk && dropped == 0 && dropped2 == 0 && plays.Count <= 56,
                      string.Format("{0}: HRTF allocation {1} voices, {2} clusters, dropped {3}/{4} (repeat), energy ratio {5:F4}",
                                    c.Name, plays.Count, clustersR.Clusters.Count, dropped, dropped2, playEnergy / Math.Max(clusterEnergy, 1e-30)));
            }

            Console.WriteLine("{0,-24} returns={1,5} plan={2,5} far={3:F4} frames={4} grains={5} clusters={6} predictedVoices={7} slot={8:F3}s members={9}",
                c.Name, returns.Count, plan.Count, far, frames, bank.Count, clusters.Clusters.Count, clusters.PredictedMaxVoices,
                clusters.SlotSecondsUsed, members);
        }
        // ---- World anchoring in live mode ----
        {
            var gp = new GranularParams { SampleRate = 48000 };
            var bank = new GrainBank(gp);
            var pitch = new PitchMapper();
            var head0 = new Vec3d(0, 1.6, 0);
            // One return straight ahead, 2 m away, at ear height.
            var ret = new Return3D();
            SonaFrontend.TryMakeReturn(new Vec3d(0, 1.6, 2.0), new ListenerPose3D(head0, 0.0), null, Vec3d.Zero, "probe", null, out ret);
            var ep = new EncodingParams { Near = 0.4, Far = 3.0 };
            var plan = SonaEncoder.Encode(new List<Return3D> { ret }, ep);
            var events = GranularRenderer.PrepareEvents(plan, bank, pitch);
            Func<double, float[]> run = yawDeg =>
            {
                var mixer = new StereoBlockMixer(gp.SampleRate);
                mixer.HeadPose = new HeadPoseSnapshot(head0, QuatD.FromYawDegrees(yawDeg));
                mixer.Play(new MixSweep { Id = 1, Events = events, Grains = bank.SnapshotF(), Params = gp, OutputGain = 0.1f, CycleFrames = 48000, LiveHeadTracking = true }, 0.0);
                return RunBlocks(mixer, gp.SampleRate, 48000);
            };
            Func<float[], int, double> energy = (buf, ch) => { double e = 0; for (int i = ch; i < buf.Length; i += 2) e += (double)buf[i] * buf[i]; return e; };
            float[] ahead = run(0), turnedRight = run(90), turnedLeft = run(-90), lookingBack = run(180);
            Check(Math.Abs(energy(ahead, 0) - energy(ahead, 1)) < 1e-9 * energy(ahead, 0), "live: source ahead is centred");
            Check(energy(turnedRight, 0) > 2.5 * energy(turnedRight, 1), "live: turn head right -> source moves to LEFT ear (world-anchored)");
            Check(energy(turnedLeft, 1) > 2.5 * energy(turnedLeft, 0), "live: turn head left -> source moves to RIGHT ear");
            Check(Math.Abs(energy(lookingBack, 0) - energy(lookingBack, 1)) < 1e-6 * energy(lookingBack, 0), "live: facing away -> centred again (ITD/ILD cone of confusion)");
            var pitched = new HeadPoseSnapshot(head0, QuatD.Euler(-60, 0, 0));   // looking up 60 degrees
            Check(Math.Abs(pitched.AzimuthOf(new Vec3d(0, 1.6, 2.0))) < 1e-12, "live: pitching the head does not pan a source straight ahead");
            var lp = ListenerPose3D.FromForward(head0, new Vec3d(0, -1, 0), new Vec3d(0.6, 0, 0.8));
            Check(Math.Abs(lp.Heading - Math.Atan2(0.6, 0.8)) < 1e-12, "heading falls back to the head's up vector when looking straight down");
        }

        // ---- Room tab: default room, wall and size changes ----
        {
            RoomLayout def = SonaRoomModel.MakeRoom(5, 6, 3, WallMode.NoWalls, true);
            RoomLayout py = SonaRoomModel.MakePreset("no_walls_demo");
            var qa = SonaRoomModel.SurfaceQuads(def);
            var qb = SonaRoomModel.SurfaceQuads(py);
            bool same = qa.Count == qb.Count;
            for (int i = 0; same && i < qa.Count; i++)
                same = (qa[i].C0 - qb[i].C0).Length < 1e-12 && (qa[i].C2 - qb[i].C2).Length < 1e-12 && qa[i].Material == qb[i].Material;
            Check(same && def.ListenerY == 0.0, "default room (5 x 6 x 3, no walls, Box 1 + Panel 1) == Python no_walls_demo");

            RoomLayout lay = SonaRoomModel.MakeRoom(5, 6, 3, WallMode.NoWalls, true);
            SonaRoomModel.ChangeRoom(lay, 5, 6, 3, WallMode.Closed);
            Check(lay.Room.Enclosed && lay.ListenerY == 0.5 && lay.Objects.Count == 2, "walls -> closed keeps both objects, listener 0.5 m inside");
            SonaRoomModel.ChangeRoom(lay, 4, 4, 3, WallMode.Open);
            ObjectSpec panel = lay.Find("panel-1");
            bool inside = true;
            foreach (ObjectSpec o in lay.Objects)
                foreach (double[] c in SonaRoomModel.Footprint(o))
                    inside &= c[0] >= lay.Room.XMin - 1e-9 && c[0] <= lay.Room.XMax + 1e-9 && c[1] >= -1e-9 && c[1] <= lay.Room.Depth + 1e-9;
            Check(inside && panel != null && Math.Abs(panel.Y - (4.0 - 0.04)) < 1e-9, "shrink to 4 x 4 clamps objects inside (panel pushed to the back wall)");
            SonaRoomModel.ChangeRoom(lay, 4, 4, 1.5, WallMode.Open);
            Check(lay.Find("panel-1").Height <= 1.5 && lay.Find("box-1").Height == 1.0, "lowering the ceiling caps object heights");
        }

        // ---- Challenge scoring (known answers; + = right of the box) ----
        {
            double side = Math.Atan(1.0 / 3.0) * SonaMath.Rad2Deg;   // 1 m off at 3 m: 18.43 degrees
            var up = new Vec3d(0, 1, 0);
            var facingZ = ListenerPose3D.FromForward(Vec3d.Zero, new Vec3d(0, 0, 1), up);
            ChallengeScore s = SonaChallenge.Score(new Vec3d(0, 0.5, 3), new Vec3d(1, 0, 3), facingZ);
            Check(Math.Abs(s.Error - 1.0) < 1e-12 && Math.Abs(s.BoxDistance - 3.0) < 1e-12 && Math.Abs(s.GuessDistance - Math.Sqrt(10.0)) < 1e-12,
                  "challenge: guess 1 m right of a box 3 m ahead is 1 m off, at 3 m and 3.16 m");
            Check(Math.Abs(s.BoxBearing) < 1e-9 && Math.Abs(s.BearingError - side) < 1e-9, "challenge: that guess is 18.43 degrees right");

            var facingX = ListenerPose3D.FromForward(Vec3d.Zero, new Vec3d(1, 0, 0), up);
            ChallengeScore right = SonaChallenge.Score(new Vec3d(3, 0, 0), new Vec3d(3, 0, -1), facingX);
            ChallengeScore left = SonaChallenge.Score(new Vec3d(3, 0, 0), new Vec3d(3, 0, 1), facingX);
            Check(Math.Abs(right.BearingError - side) < 1e-9 && Math.Abs(left.BearingError + side) < 1e-9,
                  "challenge: facing +X, -Z is right and +Z is left");

            double behind = Math.Atan(0.1 / 3.0) * SonaMath.Rad2Deg;
            ChallengeScore wrap = SonaChallenge.Score(new Vec3d(-0.1, 0, -3), new Vec3d(0.1, 0, -3), facingZ);
            Check(Math.Abs(wrap.BearingError + 2.0 * behind) < 1e-9, "challenge: errors across straight behind wrap to a few degrees, not 356");

            var lookingDown = ListenerPose3D.FromForward(Vec3d.Zero, new Vec3d(0, -1, 0), new Vec3d(0, 0, 1));
            ChallengeScore down = SonaChallenge.Score(new Vec3d(0, 0, 3), new Vec3d(1, 0, 3), lookingDown);
            Check(Math.Abs(down.BearingError - side) < 1e-9, "challenge: looking straight down still scores from the way the head faces");
        }

        Console.WriteLine(s_failures == 0 ? "C# CHECKS: ALL PASS" : ("C# CHECKS: " + s_failures + " FAILED"));
        return s_failures == 0 ? 0 : 1;
    }

    static int s_failures;

    static void Check(bool ok, string what)
    {
        if (!ok) s_failures++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
    }

    static MixSweep NewSweep(int id, GrainEvent[] events, GrainBank bank, GranularParams gp, double gain, int frames, bool live)
    {
        return new MixSweep { Id = id, Events = events, Grains = bank.SnapshotF(), Params = gp, OutputGain = (float)gain, CycleFrames = frames, LiveHeadTracking = live };
    }

    static float[] RunBlocks(StereoBlockMixer mixer, int sr, int frames, long startClock = 0)
    {
        var output = new float[frames * 2];
        const int block = 1024;
        var buf = new float[block * 2];
        long done = 0;
        while (done < frames)
        {
            mixer.Render(buf, 2, (startClock + done) / (double)sr, true);
            int n = (int)Math.Min(block, frames - done);
            Array.Copy(buf, 0, output, done * 2, n * 2);
            done += block;
        }
        return output;
    }

    static float[] RunMixer(GrainEvent[] events, GrainBank bank, GranularParams gp, double outputGain, int frames,
                            bool live, HeadPoseSnapshot head)
    {
        var mixer = new StereoBlockMixer(gp.SampleRate);
        mixer.HeadPose = head;
        var sweep = new MixSweep
        {
            Id = 1,
            Events = events,
            Grains = bank.SnapshotF(),
            Params = gp,
            OutputGain = (float)outputGain,
            CycleFrames = frames,
            LiveHeadTracking = live,
        };
        mixer.Play(sweep, 0.0);
        var output = new float[frames * 2];
        const int block = 1024;
        var buf = new float[block * 2];
        long clock = 0;
        while (clock < frames)
        {
            mixer.Render(buf, 2, clock / (double)gp.SampleRate, true);
            int n = (int)Math.Min(block, frames - clock);
            Array.Copy(buf, 0, output, clock * 2, n * 2);
            clock += block;
        }
        return output;
    }
}
