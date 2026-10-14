// PUFFERFIN: a SyncPlay group has one host, who drives what everyone watches.
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.SyncPlay;

namespace MediaBrowser.Controller.SyncPlay
{
    /// <summary>
    /// Who leads a SyncPlay group. The host is the member who has been in the group the longest: the one who
    /// created it, then, once they leave, the next one to have joined. Only the host chooses, starts, pauses and
    /// seeks; every member still reports buffering and readiness, and may stop following.
    /// </summary>
    public static class GroupHost
    {
        /// <summary>
        /// Tells whether only the host may make a request.
        /// </summary>
        /// <param name="type">The type of the request.</param>
        /// <returns><c>true</c> when the request drives the group; <c>false</c> for the ones every member sends.</returns>
        public static bool IsHostOnly(PlaybackRequestType type) => type switch
        {
            PlaybackRequestType.Buffer => false,
            PlaybackRequestType.Ready => false,
            PlaybackRequestType.Ping => false,
            PlaybackRequestType.IgnoreWait => false,
            _ => true
        };

        /// <summary>
        /// Gets the host among the members, in the order they joined.
        /// </summary>
        /// <param name="joinOrder">The session ids of the members, the first to join first.</param>
        /// <returns>The session id of the host, or <c>null</c> for an empty group.</returns>
        public static string? GetHost(IEnumerable<string> joinOrder) => joinOrder.FirstOrDefault();

        /// <summary>
        /// Tells whether a member may make a request.
        /// </summary>
        /// <param name="joinOrder">The session ids of the members, the first to join first.</param>
        /// <param name="sessionId">The session making the request.</param>
        /// <param name="type">The type of the request.</param>
        /// <returns><c>true</c> when the request is allowed.</returns>
        public static bool IsAllowed(IEnumerable<string> joinOrder, string sessionId, PlaybackRequestType type)
            => !IsHostOnly(type) || string.Equals(GetHost(joinOrder), sessionId, System.StringComparison.OrdinalIgnoreCase);
    }
}
