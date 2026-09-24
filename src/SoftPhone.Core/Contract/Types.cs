using System.Text.Json;
using System.Text.RegularExpressions;

namespace SoftPhone.Core.Contract;

// Types mirroring the server integration contract provided by the
// CrestApps.OrchardCore Telephony module (the /softphone endpoints + telephony
// hub). Treat these shapes as fixed — they are the server seam, kept identical to
// the browser extension's src/common/types.ts.

/// <summary>A call as delivered over the hub. Contract §B "Payload shapes".</summary>
public sealed class Call
{
    public string CallId { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string State { get; set; } = "";
    public string Direction { get; set; } = "";
    public string ProviderName { get; set; } = "";
}

/// <summary>A matched record shown with an incoming call (e.g. a customer the caller's number matched).</summary>
public sealed class ContextCard
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }
    public bool? OpenInNewTab { get; set; }
    public string[]? Badges { get; set; }

    /// <summary>Label for the "open" action (e.g. "Open activity"); null means the generic "Open".</summary>
    public string? OpenText { get; set; }

    /// <summary>Label for the "answer and open" action; null means the generic "Answer &amp; open".</summary>
    public string? AnswerAndOpenText { get; set; }

    /// <summary>Extra links on the card (e.g. "Customer record").</summary>
    public List<ContextLink>? Links { get; set; }
}

public sealed class ContextLink
{
    public string? Text { get; set; }
    public string? Url { get; set; }
}

/// <summary>Screen-pop / caller context accompanying an incoming call. Contract §B.</summary>
public sealed class CallContext
{
    public string? Heading { get; set; }
    public List<ContextCard>? Cards { get; set; }
    public Dictionary<string, string>? Properties { get; set; }
}

/// <summary>Client → server invocation argument for Reject/Voicemail. Contract §B.</summary>
public sealed class CallReference
{
    public string CallId { get; set; } = "";
}

/// <summary>
/// Server → client payload for <c>DialRequested</c> (contract §B): an operator started a call
/// from outside the phone (e.g. the CRM "call" button). Arrives with a single <c>Number</c>
/// (already trimmed by the server; may be E.164 or a raw dialable string). The soft phone —
/// not the server — places the call.
/// </summary>
public sealed class TelephonyDialRequest
{
    public string Number { get; set; } = "";
}

/// <summary>
/// Server → client payload for <c>IncomingCallAnswered</c>: a call that rang on all of the user's soft
/// phones was answered on one of them, so the others stop ringing at once.
/// </summary>
public sealed class IncomingCallAnsweredNotice
{
    public string? CallId { get; set; }

    /// <summary>The answered offer (the <c>reservationId</c> property of the call context), when there was one.</summary>
    public string? OfferId { get; set; }
}

/// <summary>Response of {adminPrefix}/contact-center/agent/current-incoming-offer. Contract §B.</summary>
public sealed class PendingIncomingCallOffer
{
    public Call Call { get; set; } = new();
    public CallContext Context { get; set; } = new();
    public string? ExpiresUtc { get; set; }
    public string? ServerTimeUtc { get; set; }
}

/// <summary>Response of GET /softphone/extension-config. Contract §C.</summary>
public sealed class ExtensionConfig
{
    public string HubUrl { get; set; } = "";
    public string CurrentIncomingOfferUrl { get; set; } = "";
    public string SoftPhoneUrl { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string UserId { get; set; } = "";
}

/// <summary>Contract helpers. Kept identical in spirit to the extension's derived helpers.</summary>
public static class ContractHelpers
{
    /// <summary>
    /// JSON options shared by every contract (de)serialization. Case-insensitive so
    /// both the camelCase config endpoint and the PascalCase hub payloads bind.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    // The contract does not enumerate State values, so ringing is matched by keyword —
    // identical to the extension's isRingingState().
    private static readonly Regex RingingStateRegex =
        new("ring|offer|incoming|alert|pending", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Heuristic: is this call still ringing (a parked inbound offer awaiting the
    /// agent)? When CallStateChanged reports a non-ringing state, the background
    /// stops the ringtone and clears the notification.
    /// </summary>
    public static bool IsRingingState(string? state) =>
        !string.IsNullOrEmpty(state) && RingingStateRegex.IsMatch(state);

    /// <summary>Derived helper: caller display name from a call context.</summary>
    public static string CallerDisplayName(CallContext? context, string fallback)
    {
        if (context is null) return fallback;
        if (!string.IsNullOrEmpty(context.Heading)) return context.Heading!;
        var firstTitle = context.Cards is { Count: > 0 } ? context.Cards[0].Title : null;
        return !string.IsNullOrEmpty(firstTitle) ? firstTitle! : fallback;
    }

    /// <summary>Derived helper: the primary screen-pop URL from a call context, if any.</summary>
    public static ScreenPopTarget? ScreenPopTargetOf(CallContext? context)
    {
        var card = context?.Cards?.FirstOrDefault(c => !string.IsNullOrEmpty(c.Url));
        if (card?.Url is not { Length: > 0 } url) return null;
        return new ScreenPopTarget(url, card.OpenInNewTab ?? false);
    }
}

public readonly record struct ScreenPopTarget(string Url, bool OpenInNewTab);
