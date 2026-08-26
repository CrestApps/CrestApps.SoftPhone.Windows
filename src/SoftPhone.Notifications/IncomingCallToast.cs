using System.Security;
using System.Xml.Linq;

namespace SoftPhone.Notifications;

/// <summary>Data needed to render an incoming-call toast.</summary>
public sealed class IncomingCallToastModel
{
    public required string CallId { get; init; }
    /// <summary>Caller display name (ContractHelpers.CallerDisplayName).</summary>
    public required string CallerName { get; init; }
    /// <summary>Raw caller number/address, shown as the second line.</summary>
    public string? FromNumber { get; init; }
    /// <summary>Optional queue name, shown as attribution.</summary>
    public string? QueueName { get; init; }
}

/// <summary>
/// Builds the incoming-call toast payload as toast-schema XML (the same schema the
/// Windows App SDK AppNotification and the legacy ToastNotification both accept). Pure
/// string building so it is fully unit-testable; the app hands the XML to the OS.
///
/// Buttons carry the activation arguments produced by <see cref="ToastArguments"/>:
/// Answer (also the body click), Decline, and Voicemail. Contract §7/§8.
/// </summary>
public static class IncomingCallToast
{
    /// <summary>Build the toast XML for an incoming call.</summary>
    public static string BuildXml(IncomingCallToastModel model)
    {
        var attribution = string.IsNullOrWhiteSpace(model.QueueName)
            ? "Incoming call"
            : $"Incoming call · {model.QueueName}";

        var secondLine = string.IsNullOrWhiteSpace(model.FromNumber)
            ? "Incoming call"
            : model.FromNumber!;

        // Body click == Answer (contract §7). Use "foreground" so the app is activated.
        var toast = new XElement("toast",
            new XAttribute("launch", ToastArguments.ForAnswer(model.CallId)),
            new XAttribute("scenario", "incomingCall"),
            new XAttribute("duration", "long"),
            new XElement("visual",
                new XElement("binding",
                    new XAttribute("template", "ToastGeneric"),
                    new XElement("text", model.CallerName),
                    new XElement("text", secondLine),
                    new XElement("text",
                        new XAttribute("placement", "attribution"),
                        attribution))),
            new XElement("actions",
                Button("Answer", ToastArguments.ForAnswer(model.CallId), "foreground"),
                Button("Decline", ToastArguments.ForDecline(model.CallId), "background"),
                Button("Voicemail", ToastArguments.ForVoicemail(model.CallId), "background")));

        return new XDocument(toast).ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Button(string content, string arguments, string activationType) =>
        new("action",
            new XAttribute("content", content),
            new XAttribute("arguments", arguments),
            new XAttribute("activationType", activationType));

    /// <summary>The tag used to find/replace/clear this call's toast in the OS action center.</summary>
    public static string TagFor(string callId)
    {
        // Tags are length-limited (16 chars historically for legacy toasts); hash to be safe.
        var safe = new string((callId ?? "").Where(char.IsLetterOrDigit).ToArray());
        return safe.Length <= 16 ? safe : safe[..16];
    }
}
