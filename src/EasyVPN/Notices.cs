namespace EasyVPN;

public enum NoticeLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>A notification: what happened to the connection, in a few words.</summary>
public sealed record Notice(string Title, string Text, NoticeLevel Level);

/// <summary>The connection as one poll of the agent saw it.</summary>
public sealed record ConnectionView(bool AgentOnline, ConnectionState State, string? FacilityId, string? FacilityName, string? Message);

/// <summary>
/// What is worth a notification, from one poll to the next: connected, interrupted, back again,
/// failed, the agent gone - and disconnected, unless this app asked for it (a switch made from
/// another workstation is news; one's own Disconnect is not). Nothing for what was already so
/// when the app started.
/// </summary>
public sealed class Notices
{
    private ConnectionView? _last;

    /// <param name="askedHere">This app asked to connect or disconnect a moment ago.</param>
    public IReadOnlyList<Notice> Next(ConnectionView now, bool askedHere)
    {
        var before = _last;
        _last = now;
        if (before is null) return [];

        var notices = new List<Notice>();
        if (before.AgentOnline && !now.AgentOnline)
        {
            notices.Add(new("EasyVPN Agent stopped", "EasyVPN cannot connect until the EasyVPN Agent service runs again.", NoticeLevel.Warning));
            return notices;
        }
        if (!before.AgentOnline || !now.AgentOnline) return notices;

        var same = string.Equals(before.FacilityId, now.FacilityId, StringComparison.OrdinalIgnoreCase);
        var was = same ? before.State : ConnectionState.Disconnected;
        var wasUp = before.State is ConnectionState.Connected or ConnectionState.Interrupted;
        var name = now.FacilityName ?? now.FacilityId ?? "The facility";

        if (wasUp && !askedHere && (!same || now.State is ConnectionState.Disconnecting or ConnectionState.Disconnected))
        {
            var previous = before.FacilityName ?? before.FacilityId ?? "the facility";
            notices.Add(new($"Disconnected from {previous}",
                !same && now.FacilityId is not null ? $"The gateway switched to {name}." : now.Message ?? "The gateway disconnected it.",
                NoticeLevel.Warning));
        }

        if (now.State == was) return notices;
        switch (now.State)
        {
            case ConnectionState.Connected when was == ConnectionState.Interrupted:
                notices.Add(new($"{name} is back", "It answers again, and its traffic flows.", NoticeLevel.Info));
                break;
            case ConnectionState.Connected:
                notices.Add(new($"Connected to {name}", "Its networks are reachable from this PC.", NoticeLevel.Info));
                break;
            case ConnectionState.Interrupted:
                notices.Add(new($"{name} interrupted", now.Message ?? "It stopped answering: its traffic is held until it answers again.", NoticeLevel.Warning));
                break;
            case ConnectionState.Failed:
                notices.Add(new(was is ConnectionState.Connected or ConnectionState.Interrupted ? $"Lost {name}" : $"Could not connect to {name}",
                    now.Message ?? "The gateway gave up.", NoticeLevel.Error));
                break;
        }
        return notices;
    }
}
