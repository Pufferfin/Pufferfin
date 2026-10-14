using System;
using System.Collections.Generic;
using System.Threading;
using Emby.Server.Implementations.SyncPlay;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Requests;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.SyncPlay;

public class GroupTests
{
    public GroupTests()
    {
        var mockLogger = new Mock<ILogger<Emby.Server.Implementations.SyncPlay.Group>>();
        MockLoggerFactory = new Mock<ILoggerFactory>();
        MockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);

        MockUserManager = new Mock<IUserManager>();
        MockSessionManager = new Mock<ISessionManager>();
        MockLibraryManager = new Mock<ILibraryManager>();
        MockItem = new Mock<BaseItem>();
        MockItem.Setup(i => i.IsVisibleStandalone(It.IsAny<User>())).Returns(true);
    }

    private Mock<ILoggerFactory> MockLoggerFactory { get; }

    private Mock<IUserManager> MockUserManager { get; }

    private Mock<ISessionManager> MockSessionManager { get; }

    private Mock<ILibraryManager> MockLibraryManager { get; }

    private Mock<BaseItem> MockItem { get; }

    [Fact]
    public void HasAccessToPlayQueue_ReturnsTrue_WhenItemsAreVisible()
    {
        MockLibraryManager.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns(MockItem.Object);

        var group = new Emby.Server.Implementations.SyncPlay.Group(MockLoggerFactory.Object, MockUserManager.Object, MockSessionManager.Object, MockLibraryManager.Object);
        var itemId = Guid.NewGuid();
        var playlist = new List<Guid> { itemId };
        group.PlayQueue.Reset();
        group.PlayQueue.SetPlaylist(playlist);

        Assert.Single(group.PlayQueue.GetPlaylist());
        Assert.Equal(itemId, group.PlayQueue.GetPlaylist()[0].ItemId);

        var user = new User("test-user", "auth-provider", "pwdreset-provider");
        var result = group.HasAccessToPlayQueue(user);

        Assert.True(result);
    }

    [Fact]
    public void HasAccessToPlayQueue_ReturnsFalse_WhenLibraryReturnsNullForItem()
    {
        MockLibraryManager.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);

        Assert.Null(MockLibraryManager.Object.GetItemById(Guid.NewGuid()));

        var group = new Emby.Server.Implementations.SyncPlay.Group(MockLoggerFactory.Object, MockUserManager.Object, MockSessionManager.Object, MockLibraryManager.Object);
        var itemId = Guid.NewGuid();
        var playlist = new List<Guid> { itemId };
        group.PlayQueue.Reset();
        group.PlayQueue.SetPlaylist(playlist);

        Assert.Single(group.PlayQueue.GetPlaylist());
        Assert.Equal(itemId, group.PlayQueue.GetPlaylist()[0].ItemId);

        var user = new User("test-user", "auth-provider", "pwdreset-provider");
        var result = group.HasAccessToPlayQueue(user);

        Assert.False(result);
    }

    private SessionInfo NewSession(string id, User user, string deviceId)
        => new(MockSessionManager.Object, new Mock<ILogger>().Object)
        {
            Id = id,
            UserId = user.Id,
            UserName = user.Username,
            DeviceId = deviceId
        };

    [Fact]
    public void OnlyTheHostStartsPlayback_AndTheNextMemberHostsOnceTheHostLeaves()
    {
        // PUFFERFIN: one host per group, the first to have joined
        MockLibraryManager.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns(MockItem.Object);
        var hostUser = new User("host", "auth-provider", "pwdreset-provider");
        var memberUser = new User("member", "auth-provider", "pwdreset-provider");
        MockUserManager.Setup(m => m.GetUserById(hostUser.Id)).Returns(hostUser);
        MockUserManager.Setup(m => m.GetUserById(memberUser.Id)).Returns(memberUser);

        var group = new Emby.Server.Implementations.SyncPlay.Group(MockLoggerFactory.Object, MockUserManager.Object, MockSessionManager.Object, MockLibraryManager.Object);
        var host = NewSession("host-session", hostUser, "host-device");
        var member = NewSession("member-session", memberUser, "member-device");
        group.CreateGroup(host, new NewGroupRequest("group"), CancellationToken.None);
        group.SessionJoin(member, new JoinGroupRequest(group.GroupId), CancellationToken.None);

        Assert.Equal("host-device", group.GetInfo().HostDeviceId);
        Assert.Equal("host", group.GetInfo().HostUserName);

        group.HandleRequest(member, new PlayGroupRequest(new[] { Guid.NewGuid() }, 0, 0), CancellationToken.None);
        Assert.Empty(group.PlayQueue.GetPlaylist());

        group.HandleRequest(host, new PlayGroupRequest(new[] { Guid.NewGuid() }, 0, 0), CancellationToken.None);
        Assert.Single(group.PlayQueue.GetPlaylist());

        group.SessionLeave(host, new LeaveGroupRequest(), CancellationToken.None);
        Assert.Equal("member-device", group.GetInfo().HostDeviceId);
    }
}
