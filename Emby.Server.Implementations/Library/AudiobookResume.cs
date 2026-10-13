// PUFFERFIN: the resume window of an audiobook part, scaled to the part.
using System;

namespace Emby.Server.Implementations.Library
{
    /// <summary>
    /// The beginning and the end of an audiobook part where no position is kept. Upstream
    /// uses whole minutes (5 by default), which wipes every position in a book told in parts
    /// of a few minutes and marks them played at once. A part keeps at most a twentieth of
    /// its length at each end, so a long single-file book keeps the configured minutes.
    /// </summary>
    internal static class AudiobookResume
    {
        /// <summary>The share of a part, at each end, below which the configured minutes shrink.</summary>
        private const int PartShareDivisor = 20;

        /// <summary>What becomes of a reported position.</summary>
        internal enum Outcome
        {
            /// <summary>The position is kept.</summary>
            Keep,

            /// <summary>Too close to the beginning: the part starts over.</summary>
            FromStart,

            /// <summary>Close to the end: the part is played.</summary>
            Played
        }

        /// <summary>
        /// Tells what becomes of a position reported in an audiobook part.
        /// </summary>
        /// <param name="positionTicks">The reported position.</param>
        /// <param name="runtimeTicks">The length of the part.</param>
        /// <param name="minResumeMinutes">The configured beginning, in minutes.</param>
        /// <param name="maxResumeMinutes">The configured end, in minutes.</param>
        /// <returns>The outcome.</returns>
        internal static Outcome Decide(long positionTicks, long runtimeTicks, int minResumeMinutes, int maxResumeMinutes)
        {
            var share = runtimeTicks / PartShareDivisor;
            var beginning = Math.Min(TimeSpan.FromMinutes(Math.Max(0, minResumeMinutes)).Ticks, share);
            var end = Math.Min(TimeSpan.FromMinutes(Math.Max(0, maxResumeMinutes)).Ticks, share);

            if (positionTicks >= runtimeTicks || runtimeTicks - positionTicks < end)
            {
                return Outcome.Played;
            }

            return positionTicks < beginning ? Outcome.FromStart : Outcome.Keep;
        }
    }
}
