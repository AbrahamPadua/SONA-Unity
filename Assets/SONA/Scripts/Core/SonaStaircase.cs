// SONA Quest port: the oddity staircase of Negen et al. (2023, p. 610). The difference (ln distance) shrinks
// by DownFactor after a correct answer and grows by DownFactor^-UpSteps after an error, so it settles where
// p = UpSteps / (UpSteps + 1) of answers are correct (Kaernbach 1991). Negen's 0.9 and 0.9^-3 give 0.75;
// their paper says about 2/3, which would need UpSteps = 2.
using System;

namespace Sona.Core
{
    public sealed class Staircase
    {
        public readonly double DownFactor;
        public readonly int UpSteps;
        public readonly double Cap;
        public readonly int MaxTrials;

        public double Value;     // the next trial's difference
        public int Trials;
        public int Correct;
        public int CapHits;      // errors whose step the cap cut short: the track is censored if these pile up

        public Staircase(double start = 0.2, double downFactor = 0.9, int upSteps = 3, double cap = 0.4, int maxTrials = 65)
        {
            if (!(start > 0.0) || !(downFactor > 0.0 && downFactor < 1.0) || upSteps < 1 || !(cap >= start) || maxTrials < 1)
                throw new ArgumentException("need start > 0, 0 < downFactor < 1, upSteps >= 1, cap >= start, maxTrials >= 1");
            Value = start;
            DownFactor = downFactor;
            UpSteps = upSteps;
            Cap = cap;
            MaxTrials = maxTrials;
        }

        public bool Done { get { return Trials >= MaxTrials; } }
        public double TargetProportion { get { return UpSteps / (UpSteps + 1.0); } }

        public void Update(bool correct)
        {
            Trials++;
            if (correct)
            {
                Correct++;
                Value *= DownFactor;
                return;
            }
            double next = Value / Math.Pow(DownFactor, UpSteps);
            if (next > Cap)
            {
                CapHits++;
                next = Cap;
            }
            Value = next;
        }
    }
}
