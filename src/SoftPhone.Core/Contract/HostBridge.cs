using System.Text.Json;

namespace SoftPhone.Core.Contract;

// The incoming-call handoff between the /softphone page and this app over the WebView2 message
// channel (window.chrome.webview). It mirrors the page's soft-phone/host-bridge.js in the
// CrestApps.OrchardCore Telephony module.
//
// The page shows its own full-screen incoming modal unless this app confirms that its
// notification for that exact call is on screen:
//   page → app  softphone-ready                  the page loaded
//   app  → page host-ready                       this app takes part in the handoff
//   page → app  incoming-call                    a call rings (caller, queue, matched records)
//   app  → page incoming-call-shown              our notification for that call is on screen
//   app  → page incoming-call-dismissed          our notification closed without an answer
//   app  → page incoming-call-action             the agent chose answer/decline/voicemail here
//   page → app  incoming-call-action-result      the page ran (or could not run) that action
//   page → app  incoming-call-ended              the call no longer rings on the page
// With no confirmation within 2 s the page shows its modal, so a notification is never lost.

/// <summary>What the agent chose for a ringing call.</summary>
public enum IncomingCallAction
{
    Answer,
    Decline,
    Voicemail,
}

/// <summary>A message the page sent to this app.</summary>
public abstract record PageMessage;

/// <summary>The page loaded and speaks <paramref name="Protocol"/>.</summary>
public sealed record PageReadyMessage(int Protocol) : PageMessage;

/// <summary>A call rings on the page. The call's state is always ringing/inbound.</summary>
public sealed record PageIncomingCallMessage(Call Call, CallContext Context, bool CanVoicemail) : PageMessage;

/// <summary>The call no longer rings on the page (answered, declined, expired or ended).</summary>
public sealed record PageIncomingCallEndedMessage(string CallId) : PageMessage;

/// <summary>The page's outcome for an action this app sent.</summary>
public sealed record PageActionResultMessage(string CallId, string Action, bool Handled) : PageMessage;

public static class HostBridge
{
    public const int Protocol = 1;

    /// <summary>Upper bound on matched records read from one message.</summary>
    public const int MaxCards = 50;

    /// <summary>Parse a page message (the WebView2 WebMessageAsJson string); null if not one we understand.</summary>
    public static PageMessage? ParsePageMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            switch (Str(root, "type"))
            {
                case "softphone-ready":
                    var protocol = root.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.Number
                        && p.TryGetInt32(out var v) ? v : 0;
                    return protocol >= 1 ? new PageReadyMessage(protocol) : null;

                case "incoming-call":
                    return ParseIncomingCall(root);

                case "incoming-call-ended":
                    return Str(root, "callId") is { Length: > 0 } endedId ? new PageIncomingCallEndedMessage(endedId) : null;

                case "incoming-call-action-result":
                    return Str(root, "callId") is { Length: > 0 } resultId
                        ? new PageActionResultMessage(resultId, Str(root, "action") ?? "", Bool(root, "handled"))
                        : null;

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PageIncomingCallMessage? ParseIncomingCall(JsonElement root)
    {
        var callId = Str(root, "callId");
        if (string.IsNullOrEmpty(callId)) return null;

        var call = new Call
        {
            CallId = callId,
            From = Str(root, "from") ?? "",
            State = "Ringing",
            Direction = "Inbound",
        };

        var cards = new List<ContextCard>();
        if (root.TryGetProperty("cards", out var cardsEl) && cardsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cardsEl.EnumerateArray())
            {
                if (cards.Count >= MaxCards) break;
                if (c.ValueKind != JsonValueKind.Object) continue;
                cards.Add(new ContextCard
                {
                    Id = Str(c, "id") ?? "",
                    Title = Str(c, "title") ?? "",
                    Subtitle = NullIfEmpty(Str(c, "subtitle")),
                    Description = NullIfEmpty(Str(c, "description")),
                    Url = NullIfEmpty(Str(c, "url")),
                    OpenText = NullIfEmpty(Str(c, "openText")),
                    AnswerAndOpenText = NullIfEmpty(Str(c, "answerAndOpenText")),
                    Badges = Strings(c, "badges"),
                    Links = Links(c),
                });
            }
        }

        var context = new CallContext
        {
            Heading = NullIfEmpty(Str(root, "heading")),
            Cards = cards,
            Properties = new(),
        };
        if (Str(root, "queue") is { Length: > 0 } queue)
            context.Properties["queue"] = queue;

        return new PageIncomingCallMessage(call, context, Bool(root, "canVoicemail"));
    }

    // ---- app → page ----

    public static string HostReady() => Serialize(new { type = "host-ready", protocol = Protocol });

    /// <param name="ringing">Whether this app plays a ringtone for the call (the page rings itself if not).</param>
    public static string Shown(string callId, bool ringing) =>
        Serialize(new { type = "incoming-call-shown", callId, ringing });

    public static string Dismissed(string callId) => Serialize(new { type = "incoming-call-dismissed", callId });

    public static string Action(string callId, IncomingCallAction action) =>
        Serialize(new { type = "incoming-call-action", callId, action = ActionName(action) });

    public static string ActionName(IncomingCallAction action) => action switch
    {
        IncomingCallAction.Answer => "answer",
        IncomingCallAction.Decline => "decline",
        IncomingCallAction.Voicemail => "voicemail",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>
    /// An absolute http(s) URL for a card link, resolving a path-only URL against the tenant origin;
    /// null for anything else (javascript:, file:, custom protocols), so only web links are ever opened.
    /// </summary>
    public static Uri? ResolveWebUrl(string? url, string? origin)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();

        Uri? uri = null;
        // "/Admin/..." parses as an absolute file: URI on Windows, so treat a leading slash as a path.
        if (!url.StartsWith('/') && Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            uri = absolute;
        else if (Uri.TryCreate(origin, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, url, out var relative))
            uri = relative;

        return uri is not null && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri : null;
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, ContractHelpers.Json);

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string[]? Strings(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var values = arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
            .Select(x => x.GetString()!)
            .ToArray();
        return values.Length > 0 ? values : null;
    }

    private static List<ContextLink>? Links(JsonElement el)
    {
        if (!el.TryGetProperty("links", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var links = arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => new ContextLink { Text = Str(x, "text"), Url = Str(x, "url") })
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .ToList();
        return links.Count > 0 ? links : null;
    }
}
