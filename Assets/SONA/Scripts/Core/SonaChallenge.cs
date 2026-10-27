// SONA Quest port: scoring a Challenge guess (new; no Python equivalent).
// Plain C# like the rest of Core, so the parity harness checks it without Unity.
using System;

namespace Sona.Core
{
    /// <summary>How far a guess was from the hidden box, seen from the listener. Planar metres; degrees, + = right.</summary>
    public struct ChallengeScore
    {
        public double Error;            // guess to box
        public double BoxDistance;
        public double GuessDistance;
        public double BoxBearing;
        public double GuessBearing;
        public double BearingError;     // guess minus box in [-180, 180): + = the guess was right of the box
    }

    public static class SonaChallenge
    {
        /// <summary>Planar bearing of a world point from the listener: 0 = straight ahead, + = right, in [-180, 180).</summary>
        public static double Bearing(Vec3d point, ListenerPose3D listener)
        {
            double world = Math.Atan2(point.X - listener.Position.X, point.Z - listener.Position.Z);
            return SonaMath.WrapPi(world - listener.Heading) * SonaMath.Rad2Deg;
        }

        public static ChallengeScore Score(Vec3d box, Vec3d guess, ListenerPose3D listener)
        {
            Vec3d p = listener.Position;
            double boxBearing = Bearing(box, listener);
            double guessBearing = Bearing(guess, listener);
            return new ChallengeScore
            {
                Error = SonaMath.Hypot(guess.X - box.X, guess.Z - box.Z),
                BoxDistance = SonaMath.Hypot(box.X - p.X, box.Z - p.Z),
                GuessDistance = SonaMath.Hypot(guess.X - p.X, guess.Z - p.Z),
                BoxBearing = boxBearing,
                GuessBearing = guessBearing,
                BearingError = SonaMath.WrapPi((guessBearing - boxBearing) * SonaMath.Deg2Rad) * SonaMath.Rad2Deg,
            };
        }
    }
}
