using Microsoft.Graph.Models;
using TeamsAvatarBot.LiveKitBridge;

namespace TeamsAvatarBot.Tests;

/// <summary>
/// Pin the Teams meeting URL → MeetingInfo translation used by /api/join.
/// Teams URL formats have shifted historically (legacy meetup-join with
/// context JSON, modern /meet/&lt;id&gt;?p=&lt;passcode&gt; short links);
/// a regression here would silently break /api/join with a confusing 500.
/// </summary>
public sealed class TeamsMeetingJoinUrlTests
{
    private const string Tenant   = "11111111-1111-1111-1111-111111111111";
    private const string Organizer = "22222222-2222-2222-2222-222222222222";
    private const string ThreadId = "19:meeting_AbCdEfGhIjKlMnOpQrStUvWxYz@thread.v2";

    private static string BuildLegacyUrl(string threadId = ThreadId, string messageId = "0",
                                         string tenant = Tenant, string organizer = Organizer)
    {
        var contextJson = $"{{\"Tid\":\"{tenant}\",\"Oid\":\"{organizer}\"}}";
        var context     = Uri.EscapeDataString(contextJson);
        var thread      = Uri.EscapeDataString(threadId);
        return $"https://teams.microsoft.com/l/meetup-join/{thread}/{messageId}?context={context}";
    }

    [Fact]
    public void Legacy_calendar_url_parses_into_organizer_meeting_info()
    {
        var parsed = TeamsMeetingJoinUrl.Parse(BuildLegacyUrl());

        Assert.NotNull(parsed.ChatInfo);
        Assert.Equal(ThreadId, parsed.ChatInfo!.ThreadId);
        Assert.Equal("0",      parsed.ChatInfo.MessageId);
        Assert.Equal(Tenant,   parsed.TenantId);

        var orgInfo = Assert.IsType<OrganizerMeetingInfo>(parsed.MeetingInfo);
        Assert.Equal(Organizer, orgInfo.Organizer?.User?.Id);
    }

    [Fact]
    public void Short_meet_url_parses_into_join_meeting_id_info()
    {
        // Real-world example shape from Teams calendar UI as of 2026-05.
        var parsed = TeamsMeetingJoinUrl.Parse("https://teams.microsoft.com/meet/25102509550796?p=k5axvpfJBdBm0qfNqy");

        Assert.Null(parsed.ChatInfo);
        Assert.Null(parsed.TenantId);  // not in URL — caller substitutes from BotConfig

        var idInfo = Assert.IsType<JoinMeetingIdMeetingInfo>(parsed.MeetingInfo);
        Assert.Equal("25102509550796",   idInfo.JoinMeetingId);
        Assert.Equal("k5axvpfJBdBm0qfNqy", idInfo.Passcode);
    }

    [Fact]
    public void Short_meet_url_without_passcode_parses_with_null_passcode()
    {
        var parsed = TeamsMeetingJoinUrl.Parse("https://teams.microsoft.com/meet/12345678901234");
        var idInfo = Assert.IsType<JoinMeetingIdMeetingInfo>(parsed.MeetingInfo);
        Assert.Equal("12345678901234", idInfo.JoinMeetingId);
        Assert.Null(idInfo.Passcode);
    }

    [Fact]
    public void Throws_argument_exception_on_non_teams_host()
    {
        Assert.Throws<ArgumentException>(() =>
            TeamsMeetingJoinUrl.Parse("https://zoom.us/j/123456789"));
    }

    [Fact]
    public void Throws_on_unrecognised_teams_path_shape()
    {
        Assert.Throws<ArgumentException>(() =>
            TeamsMeetingJoinUrl.Parse("https://teams.microsoft.com/l/something-else/abc"));
    }

    [Fact]
    public void Legacy_url_throws_when_context_json_missing_tid_or_oid()
    {
        var bad = $"https://teams.microsoft.com/l/meetup-join/{Uri.EscapeDataString(ThreadId)}/0?context={Uri.EscapeDataString("{\"Foo\":\"bar\"}")}";
        Assert.Throws<ArgumentException>(() => TeamsMeetingJoinUrl.Parse(bad));
    }

    [Fact]
    public void Throws_on_empty_input()
    {
        Assert.Throws<ArgumentException>(() => TeamsMeetingJoinUrl.Parse(""));
    }
}
