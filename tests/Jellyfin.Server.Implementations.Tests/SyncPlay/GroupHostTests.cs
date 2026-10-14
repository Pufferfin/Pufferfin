using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Model.SyncPlay;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.SyncPlay;

public class GroupHostTests
{
    [Fact]
    public void GetHost_IsTheFirstToJoin()
    {
        Assert.Equal("creator", GroupHost.GetHost(new[] { "creator", "member" }));
        Assert.Null(GroupHost.GetHost(System.Array.Empty<string>()));
    }

    [Theory]
    [InlineData(PlaybackRequestType.Play)]
    [InlineData(PlaybackRequestType.Pause)]
    [InlineData(PlaybackRequestType.Unpause)]
    [InlineData(PlaybackRequestType.Seek)]
    [InlineData(PlaybackRequestType.NextItem)]
    [InlineData(PlaybackRequestType.SetPlaylistItem)]
    [InlineData(PlaybackRequestType.Queue)]
    public void MemberCannotDriveTheGroup(PlaybackRequestType type)
    {
        var joinOrder = new[] { "host", "member" };
        Assert.False(GroupHost.IsAllowed(joinOrder, "member", type));
        Assert.True(GroupHost.IsAllowed(joinOrder, "host", type));
    }

    [Theory]
    [InlineData(PlaybackRequestType.Buffer)]
    [InlineData(PlaybackRequestType.Ready)]
    [InlineData(PlaybackRequestType.Ping)]
    [InlineData(PlaybackRequestType.IgnoreWait)]
    public void EveryMemberReportsItsState(PlaybackRequestType type)
    {
        Assert.True(GroupHost.IsAllowed(new[] { "host", "member" }, "member", type));
    }

    [Fact]
    public void TheNextMemberHostsOnceTheHostLeaves()
    {
        var joinOrder = new System.Collections.Generic.List<string> { "host", "first", "second" };
        joinOrder.Remove("host");
        Assert.True(GroupHost.IsAllowed(joinOrder, "first", PlaybackRequestType.Play));
        Assert.False(GroupHost.IsAllowed(joinOrder, "second", PlaybackRequestType.Play));
    }
}
