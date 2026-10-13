using System;
using Emby.Server.Implementations.Library;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library
{
    public class AudiobookResumeTests
    {
        private static long Minutes(double minutes) => TimeSpan.FromMinutes(minutes).Ticks;

        private static long Seconds(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

        [Fact]
        public void ShortPart_KeepsAPositionAfterAFewSeconds()
        {
            Assert.Equal(AudiobookResume.Outcome.Keep, AudiobookResume.Decide(Seconds(40), Minutes(3), 5, 5));
        }

        [Fact]
        public void ShortPart_StartsOverInItsFirstTwentieth()
        {
            Assert.Equal(AudiobookResume.Outcome.FromStart, AudiobookResume.Decide(Seconds(5), Minutes(3), 5, 5));
        }

        [Fact]
        public void ShortPart_IsPlayedInItsLastTwentieth()
        {
            Assert.Equal(AudiobookResume.Outcome.Played, AudiobookResume.Decide(Minutes(3) - Seconds(5), Minutes(3), 5, 5));
            Assert.Equal(AudiobookResume.Outcome.Keep, AudiobookResume.Decide(Minutes(2), Minutes(3), 5, 5));
        }

        [Fact]
        public void LongBook_KeepsTheConfiguredMinutes()
        {
            Assert.Equal(AudiobookResume.Outcome.FromStart, AudiobookResume.Decide(Minutes(4), Minutes(600), 5, 5));
            Assert.Equal(AudiobookResume.Outcome.Keep, AudiobookResume.Decide(Minutes(6), Minutes(600), 5, 5));
            Assert.Equal(AudiobookResume.Outcome.Played, AudiobookResume.Decide(Minutes(596), Minutes(600), 5, 5));
        }

        [Fact]
        public void PositionAtTheEnd_IsPlayed()
        {
            Assert.Equal(AudiobookResume.Outcome.Played, AudiobookResume.Decide(Minutes(3), Minutes(3), 0, 0));
        }
    }
}
