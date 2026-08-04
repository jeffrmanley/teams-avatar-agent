using System.Text.Json;
using System.Web;
using Microsoft.Graph.Models;

namespace TeamsAvatarBot.LiveKitBridge;

/// <summary>
/// Parses a Teams meeting join URL into the bits needed by Microsoft Graph
/// Communications to dial in. Two URL formats are supported:
///
/// <para><b>Legacy (<c>/l/meetup-join/...</c>)</b> — the older calendar URL
/// embeds <c>ChatInfo.ThreadId</c> in the path and <c>?context=&lt;json&gt;</c>
/// holds <c>Tid</c> (tenant) + <c>Oid</c> (organizer). Resolves to a
/// <see cref="ChatInfo"/> + <see cref="OrganizerMeetingInfo"/> pair.</para>
///
/// <para><b>Short (<c>/meet/&lt;id&gt;?p=&lt;passcode&gt;</c>)</b> — Teams's
/// current default. Path segment is the JoinMeetingId; <c>?p=</c> is the
/// passcode. Resolves to a <see cref="JoinMeetingIdMeetingInfo"/>. Tenant
/// id isn't in the URL — caller substitutes the bot's own tenant when
/// <c>TenantId</c> on the result is null (works as long as the meeting is
/// hosted in the same tenant the bot is registered in).</para>
/// </summary>
public static class TeamsMeetingJoinUrl
{
    /// <param name="ChatInfo">Set only for legacy meetup-join URLs.</param>
    /// <param name="MeetingInfo">Either <see cref="OrganizerMeetingInfo"/> (legacy) or <see cref="JoinMeetingIdMeetingInfo"/> (short).</param>
    /// <param name="TenantId">Set only for legacy URLs; null for short URLs.</param>
    public sealed record Parsed(ChatInfo? ChatInfo, MeetingInfo MeetingInfo, string? TenantId);

    public static Parsed Parse(string meetingUrl)
    {
        if (string.IsNullOrWhiteSpace(meetingUrl))
            throw new ArgumentException("meetingUrl is empty", nameof(meetingUrl));

        var uri = new Uri(meetingUrl);
        if (!uri.Host.EndsWith("teams.microsoft.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"not a Teams meeting URL (host={uri.Host})", nameof(meetingUrl));

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Short format: /meet/<JoinMeetingId>?p=<Passcode>
        if (segments.Length >= 2 && segments[0].Equals("meet", StringComparison.OrdinalIgnoreCase))
            return ParseShortFormat(uri, segments);

        // Legacy format: /l/meetup-join/<threadId>/<messageId>?context=...
        var idx = Array.FindIndex(segments, s => s.Equals("meetup-join", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < segments.Length)
            return ParseLegacyFormat(uri, segments, idx);

        throw new ArgumentException(
            "URL is neither a /meet/<id> short link nor a /l/meetup-join/<thread> legacy link",
            nameof(meetingUrl));
    }

    private static Parsed ParseShortFormat(Uri uri, string[] segments)
    {
        var meetingId = HttpUtility.UrlDecode(segments[1]);
        var query     = HttpUtility.ParseQueryString(uri.Query);
        var passcode  = query["p"];

        if (string.IsNullOrWhiteSpace(meetingId))
            throw new ArgumentException("short URL has empty meeting id", nameof(uri));

        var meetingInfo = new JoinMeetingIdMeetingInfo
        {
            JoinMeetingId = meetingId,
            Passcode      = string.IsNullOrEmpty(passcode) ? null : passcode,
        };

        return new Parsed(ChatInfo: null, MeetingInfo: meetingInfo, TenantId: null);
    }

    private static Parsed ParseLegacyFormat(Uri uri, string[] segments, int meetupJoinIdx)
    {
        var threadId  = HttpUtility.UrlDecode(segments[meetupJoinIdx + 1]);
        var messageId = meetupJoinIdx + 2 < segments.Length ? HttpUtility.UrlDecode(segments[meetupJoinIdx + 2]) : "0";

        var query    = HttpUtility.ParseQueryString(uri.Query);
        var contextJ = query["context"];
        if (string.IsNullOrEmpty(contextJ))
            throw new ArgumentException("legacy URL is missing ?context= JSON blob", nameof(uri));

        using var doc = JsonDocument.Parse(contextJ);
        var root     = doc.RootElement;
        var tenantId = root.TryGetProperty("Tid", out var t) ? t.GetString() : null;
        var organizerId = root.TryGetProperty("Oid", out var o) ? o.GetString() : null;

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(organizerId))
            throw new ArgumentException("context JSON missing Tid and/or Oid", nameof(uri));

        var chatInfo = new ChatInfo
        {
            ThreadId            = threadId,
            MessageId           = messageId,
            ReplyChainMessageId = null,
        };

        var meetingInfo = new OrganizerMeetingInfo
        {
            Organizer = new IdentitySet
            {
                User = new Identity
                {
                    Id            = organizerId,
                    AdditionalData = new Dictionary<string, object>
                    {
                        ["tenantId"] = tenantId!,
                    },
                },
            },
        };

        return new Parsed(chatInfo, meetingInfo, tenantId!);
    }
}
