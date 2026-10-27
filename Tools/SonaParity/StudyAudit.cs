// Parity harness: offline audit of the Proposal 1.2 stimulus grid (gate 1 of the engineering memo).
// Every condition, both signs and the full range of differences, with random standards, gains and odd
// positions, rendered through the same Core code the headset runs. Each interval is measured from the
// output, and no feature other than the intended cues may pick out the odd interval.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Sona.Core;

static partial class Program
{
    const int AuditSteps = 8;     // differences from MaxDelta / 8 up to MaxDelta
    const int AuditDraws = 100;   // trials per condition, sign and difference

    static void StudyAudit(string outDir)
    {
        var sp = new StudyParams();
        var st = new StudyStimulus(sp);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        CultureInfo inv = CultureInfo.InvariantCulture;
        var kw = new KWeighting(sp.SampleRate);
        int firstDry = 0;
        while (st.Dry[firstDry * 2] == 0.0 && st.Dry[firstDry * 2 + 1] == 0.0) firstDry++;
        double det = st.DryEnergy * st.WetEnergy - st.CrossEnergy * st.CrossEnergy;

        var csv = new StringBuilder("condition,sign,delta,trial,interval,odd,delay_m,drr_m,onset_s,click_sample,box_sample,measured_box_sample,drr_req_db,drr_meas_db,signal_drr_db,gain_db,box_rms_db,peak_db,k_level_db\n");
        var rng = new StudyRandom(2026);
        var bufs = new[] { new float[st.IntervalFrames * 2], new float[st.IntervalFrames * 2], new float[st.IntervalFrames * 2] };
        double worstDrr = 0.0, worstDryWet = 0.0, worstRms = 0.0, maxPeak = 0.0, worstCopy = 0.0, worstLeak = 0.0;
        bool onsetsOk = true, clicksOk = true, drrAxisOk = true, leakOk = true;
        string worstLeakName = "";
        int trials = 0;
        var table = new List<string>();
        string[] featureNames = { "RMS", "peak", "K-level", "onset" };

        foreach (StudyCondition c in StudyCondition.All)
        {
            var hits = new int[4];
            int n = 0;
            double maxUp = StudyTrials.MaxDelta(sp, c, 1), maxDown = StudyTrials.MaxDelta(sp, c, -1);
            foreach (int sign in new[] { 1, -1 })
            {
                double max = sign > 0 ? maxUp : maxDown;
                for (int step = 1; step <= AuditSteps; step++)
                {
                    double delta = max * step / AuditSteps;
                    for (int t = 0; t < AuditDraws; t++)
                    {
                        int odd;
                        StudyInterval[] ivs = StudyTrials.Plan(sp, c, delta, sign, rng, out odd);
                        var f = new double[4, 3];
                        for (int j = 0; j < 3; j++)
                        {
                            float[] buf = bufs[j];
                            Array.Clear(buf, 0, buf.Length);
                            st.Render(ref ivs[j], buf, 0);
                            StudyInterval iv = ivs[j];
                            double g = Math.Pow(10.0, iv.GainDb / 20.0);

                            // Where the box actually starts: the first nonzero frame after the click.
                            long m = iv.Click ? st.LeadFrames + st.Click.Length : 0;
                            while (buf[m * 2] == 0f && buf[m * 2 + 1] == 0f) m++;
                            long measuredBox = m - firstDry;
                            long planned = st.LeadFrames + (long)Math.Round(iv.Onset * sp.SampleRate);
                            onsetsOk &= measuredBox == iv.BoxSample && measuredBox == planned;
                            if (iv.Click)
                            {
                                bool sounded = false;
                                for (int i = st.LeadFrames; i < st.LeadFrames + st.Click.Length; i++) sounded |= buf[i * 2] != 0f;
                                clicksOk &= sounded;
                            }

                            // Least squares of the box onto the dry and wet parts gives the DRR actually played.
                            long b = iv.BoxSample * 2;
                            double yd = 0.0, yw = 0.0;
                            for (int i = 0; i < st.Wet.Length; i++)
                            {
                                double y = buf[b + i];
                                yd += y * (i < st.Dry.Length ? st.Dry[i] : 0.0);
                                yw += y * st.Wet[i];
                            }
                            double alpha = (yd * st.WetEnergy - yw * st.CrossEnergy) / det;
                            double beta = (st.DryEnergy * yw - st.CrossEnergy * yd) / det;
                            bool room = !double.IsPositiveInfinity(iv.DrrDb);
                            double drrMeasured = room ? 20.0 * Math.Log10(alpha / beta) : double.PositiveInfinity;
                            if (room) worstDrr = Math.Max(worstDrr, Math.Abs(drrMeasured - iv.DrrDb));
                            else worstDryWet = Math.Max(worstDryWet, Math.Abs(beta / alpha));
                            worstRms = Math.Max(worstRms, Math.Abs(iv.BoxRms / g / sp.TargetRms - 1.0));
                            maxPeak = Math.Max(maxPeak, iv.Peak);

                            double kLevel = 10.0 * Math.Log10(kw.MeanSquare(buf, iv.BoxSample, st.BoxFrames));
                            f[0, j] = 20.0 * Math.Log10(iv.BoxRms);
                            f[1, j] = 20.0 * Math.Log10(iv.Peak);
                            f[2, j] = kLevel;
                            f[3, j] = iv.Onset;
                            csv.Append(string.Format(inv, "{0},{1},{2:F4},{3},{4},{5},{6:F4},{7:F4},{8:F6},{9},{10},{11},{12:F3},{13:F6},{14:F3},{15:F3},{16:F3},{17:F3},{18:F3}\n",
                                c.Name, sign, delta, trials, j, j == odd ? 1 : 0, iv.DelayDistance, iv.DrrDistance, iv.Onset, iv.ClickSample, iv.BoxSample, measuredBox,
                                iv.DrrDb, drrMeasured, iv.SignalDrrDb, iv.GainDb, f[0, j], f[1, j], kLevel));
                        }

                        int s1 = (odd + 1) % 3;
                        if (c.KDrr == 0.0)
                        {
                            // Only the delay changed: the odd box must be the standard's box, moved and regained.
                            double go = Math.Pow(10.0, ivs[odd].GainDb / 20.0), gs = Math.Pow(10.0, ivs[s1].GainDb / 20.0);
                            long bo = ivs[odd].BoxSample * 2, bs = ivs[s1].BoxSample * 2;
                            for (int i = 0; i < st.Wet.Length; i++)
                                worstCopy = Math.Max(worstCopy, Math.Abs(bufs[odd][bo + i] / go - bufs[s1][bs + i] / gs));
                        }
                        if (c.KDelay == 0.0 && c.Click) drrAxisOk &= ivs[odd].BoxSample == ivs[s1].BoxSample;

                        for (int k = 0; k < 4; k++)
                        {
                            int pick = 0;
                            double best = -1.0;
                            for (int j = 0; j < 3; j++)
                            {
                                double gap = Math.Abs(f[k, j] - 0.5 * (f[k, (j + 1) % 3] + f[k, (j + 2) % 3]));
                                if (gap > best) { best = gap; pick = j; }
                            }
                            if (pick == odd) hits[k]++;
                        }
                        n++;
                        trials++;
                    }
                }
            }

            // A nuisance-only observer picks the interval whose feature stands out; it must stay at chance (1/3).
            double limit = 1.0 / 3.0 + 3.0 * Math.Sqrt((1.0 / 3.0) * (2.0 / 3.0) / n);
            var row = new StringBuilder(string.Format(inv, "        {0,-11} {1,6}  {2,5:F3} / {3,5:F3}", c.Name, n, maxUp, maxDown));
            for (int k = 0; k < 4; k++)
            {
                // The onset is the delay cue itself whenever there is a click, so it only counts as a nuisance without one.
                if (k == 3 && c.Click) { row.Append("      n/a"); continue; }
                double acc = hits[k] / (double)n;
                row.Append(string.Format(inv, "  {0,7:F3}", acc));
                leakOk &= acc <= limit;
                if (worstLeakName == "" || acc - limit > worstLeak)
                {
                    worstLeak = acc - limit;
                    worstLeakName = string.Format(inv, "{0} in {1}: {2:F3} against a limit of {3:F3}", featureNames[k], c.Name, acc, limit);
                }
            }
            table.Add(row.ToString());
        }

        string csvPath = Path.Combine(outDir, "study_audit.csv");
        File.WriteAllText(csvPath, csv.ToString());
        Console.WriteLine(string.Format(inv, "  audit: {0} trials, {1} intervals at {2} Hz in {3:F1} s; wrote {4}",
                                        trials, trials * 3, sp.SampleRate, watch.Elapsed.TotalSeconds, csvPath));

        Check(onsetsOk, "audit: every box starts on its planned sample, measured as the first sound after the click");
        Check(clicksOk, "audit: every interval that should have a click has one");
        Check(worstDrr < 1e-3 && worstDryWet < 1e-6,
              string.Format(inv, "audit: the DRR measured in every room interval matches its request (worst {0:E1} dB); dry intervals carry no reverb (worst {1:E1})", worstDrr, worstDryWet));
        Check(worstRms < 1e-5, string.Format(inv, "audit: every box plays at the target RMS plus its own gain (worst relative error {0:E1})", worstRms));
        Check(maxPeak < 1.0, string.Format(inv, "audit: nothing clips anywhere in the grid (highest peak {0:F3})", maxPeak));
        Check(worstCopy < 1e-6, string.Format(inv, "audit: when only the delay changes, the odd box is the standard's box moved in time (worst {0:E1})", worstCopy));
        Check(drrAxisOk, "audit: when only the DRR changes, the odd box starts on the standard's sample");

        Console.WriteLine("        odd-one-out accuracy of a nuisance-only observer (chance = 0.333)");
        Console.WriteLine("        condition   trials  max delta +/-     RMS     peak  K-level    onset");
        foreach (string line in table) Console.WriteLine(line);
        Check(leakOk, "audit: no nuisance feature picks the odd interval above chance (closest: " + worstLeakName + ")");

        // What DRR alone does to loudness (BS.1770 K-weighted level) and to the box's peak, at equal RMS and no gain.
        Func<double, double[]> levels = drr =>
        {
            var b2 = new float[st.IntervalFrames * 2];
            var iv = new StudyInterval { Click = false, Onset = 0.5, DrrDb = drr };
            st.Render(ref iv, b2, 0);
            return new[] { 10.0 * Math.Log10(kw.MeanSquare(b2, iv.BoxSample, st.BoxFrames)), 20.0 * Math.Log10(iv.Peak) };
        };
        double[] dryLevels = levels(double.PositiveInfinity);
        var kLine = new StringBuilder("        K-weighted level, dB re dry:");
        var pLine = new StringBuilder("        box peak, dB re dry:        ");
        var spread = new[] { double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity, double.NegativeInfinity };
        foreach (double drr in new[] { -10.0, -5.0, 0.0, 5.0, 10.0, 15.0, 20.0 })
        {
            double[] l = levels(drr);
            double k = l[0] - dryLevels[0], pk = l[1] - dryLevels[1];
            spread[0] = Math.Min(spread[0], k); spread[1] = Math.Max(spread[1], k);
            spread[2] = Math.Min(spread[2], pk); spread[3] = Math.Max(spread[3], pk);
            kLine.Append(string.Format(inv, "  {0:+0;-0}: {1:+0.00;-0.00}", drr, k));
            pLine.Append(string.Format(inv, "  {0:+0;-0}: {1:+0.00;-0.00}", drr, pk));
        }
        Console.WriteLine("        At equal RMS, by DRR (dB):");
        Console.WriteLine(kLine.ToString());
        Console.WriteLine(pLine.ToString());
        Console.WriteLine(string.Format(inv, "        spread from -10 to +20 dB DRR: loudness {0:F2} dB, peak {1:F2} dB, against +/-{2} dB of roving",
                                        spread[1] - spread[0], spread[3] - spread[2], sp.RoveDb));

        Check(kw.Matches48k(), "audit: the K-weighting filter reproduces the BS.1770 coefficients at 48 kHz");
    }

    // ITU-R BS.1770 K-weighting (high shelf, then high-pass), designed for any rate the way libebur128 does it.
    sealed class KWeighting
    {
        readonly double[] _b1 = new double[3], _a1 = new double[3], _b2 = { 1.0, -2.0, 1.0 }, _a2 = new double[3];

        public KWeighting(int sampleRate)
        {
            double f0 = 1681.974450955533, gain = 3.999843853973347, q = 0.7071752369554196;
            double k = Math.Tan(Math.PI * f0 / sampleRate);
            double vh = Math.Pow(10.0, gain / 20.0), vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1.0 + k / q + k * k;
            _b1[0] = (vh + vb * k / q + k * k) / a0;
            _b1[1] = 2.0 * (k * k - vh) / a0;
            _b1[2] = (vh - vb * k / q + k * k) / a0;
            _a1[0] = 1.0;
            _a1[1] = 2.0 * (k * k - 1.0) / a0;
            _a1[2] = (1.0 - k / q + k * k) / a0;
            f0 = 38.13547087602444;
            q = 0.5003270373238773;
            k = Math.Tan(Math.PI * f0 / sampleRate);
            a0 = 1.0 + k / q + k * k;
            _a2[0] = 1.0;
            _a2[1] = 2.0 * (k * k - 1.0) / a0;
            _a2[2] = (1.0 - k / q + k * k) / a0;
        }

        /// <summary>Mean square of the K-weighted signal over both channels of a frame range (no gating).</summary>
        public double MeanSquare(float[] interleaved, long startFrame, int frames)
        {
            double sum = 0.0;
            for (int ch = 0; ch < 2; ch++)
            {
                double x1 = 0, x2 = 0, u1 = 0, u2 = 0, z1 = 0, z2 = 0;
                for (long i = startFrame; i < startFrame + frames; i++)
                {
                    // The offset keeps the shelf's state out of subnormal numbers in silent stretches (20x slower there);
                    // the high-pass removes it.
                    double x = interleaved[i * 2 + ch] + 1e-20;
                    double u = _b1[0] * x + _b1[1] * x1 + _b1[2] * x2 - _a1[1] * u1 - _a1[2] * u2;
                    double z = _b2[0] * u + _b2[1] * u1 + _b2[2] * u2 - _a2[1] * z1 - _a2[2] * z2;
                    x2 = x1; x1 = x; u2 = u1; u1 = u; z2 = z1; z1 = z;
                    sum += z * z;
                }
            }
            return sum / (2.0 * frames);
        }

        /// <summary>The 48 kHz coefficients printed in ITU-R BS.1770 (stage 1 shelf, stage 2 high-pass).</summary>
        public bool Matches48k()
        {
            var k48 = new KWeighting(48000);
            double[] want = { 1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585,
                              -1.99004745483398, 0.99007225036621 };
            double[] got = { k48._b1[0], k48._b1[1], k48._b1[2], k48._a1[1], k48._a1[2], k48._a2[1], k48._a2[2] };
            for (int i = 0; i < want.Length; i++) if (Math.Abs(want[i] - got[i]) > 1e-6) return false;
            return true;
        }
    }
}
