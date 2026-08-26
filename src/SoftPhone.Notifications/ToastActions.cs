using System.Collections.Specialized;
using System.Web;

namespace SoftPhone.Notifications;

/// <summary>The action a toast (body click or a button) requests when activated.</summary>
public enum ToastActionKind
{
    /// <summary>Body click or the Answer button — open/navigate the phone to answerCallId.</summary>
    Answer,
    /// <summary>Decline button — Reject(callId) on the background hub, no media leg.</summary>
    Decline,
    /// <summary>Voicemail button — Voicemail(callId) on the background hub, no media leg.</summary>
    Voicemail,
    /// <summary>Unrecognized / non-call activation.</summary>
    Unknown,
}

/// <summary>A parsed toast activation: what to do and which call it targets.</summary>
public readonly record struct ToastAction(ToastActionKind Kind, string CallId)
{
    public bool IsCallAction => Kind is ToastActionKind.Answer or ToastActionKind.Decline or ToastActionKind.Voicemail;
}

/// <summary>
/// Builds and parses the toast activation argument strings (contract §8: toasts carry
/// <c>action=answer&amp;callId=…</c>). Pure and unit-tested; used symmetrically by the
/// toast builder and the activation handler (running app + cold-start).
/// </summary>
public static class ToastArguments
{
    public const string ActionKey = "action";
    public const string CallIdKey = "callId";

    public static string ForAnswer(string callId) => Build(ToastActionKind.Answer, callId);
    public static string ForDecline(string callId) => Build(ToastActionKind.Decline, callId);
    public static string ForVoicemail(string callId) => Build(ToastActionKind.Voicemail, callId);

    public static string Build(ToastActionKind kind, string callId)
    {
        var action = kind switch
        {
            ToastActionKind.Answer => "answer",
            ToastActionKind.Decline => "decline",
            ToastActionKind.Voicemail => "voicemail",
            _ => "unknown",
        };
        return $"{ActionKey}={Uri.EscapeDataString(action)}&{CallIdKey}={Uri.EscapeDataString(callId ?? "")}";
    }

    /// <summary>Parse an activation argument string back into a typed action.</summary>
    public static ToastAction Parse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return new ToastAction(ToastActionKind.Unknown, "");

        NameValueCollection q = HttpUtility.ParseQueryString(arguments);
        var action = (q[ActionKey] ?? "").Trim().ToLowerInvariant();
        var callId = q[CallIdKey] ?? "";

        var kind = action switch
        {
            "answer" => ToastActionKind.Answer,
            "decline" => ToastActionKind.Decline,
            "voicemail" => ToastActionKind.Voicemail,
            _ => ToastActionKind.Unknown,
        };
        return new ToastAction(kind, callId);
    }
}
