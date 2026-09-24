using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SoftPhone.Core.Contract;
using SoftPhone.Core.Incoming;

namespace SoftPhone.App;

/// <summary>
/// In-app incoming-call popup (mirrors the page's incoming modal). Shows the caller, the queue, and
/// the matched records — each with "Answer &amp; open" and "Open" when it links somewhere — plus
/// Answer / Decline / Voicemail. The coordinator positions and closes it.
/// </summary>
public partial class IncomingCallWindow : Window
{
    /// <summary>Records listed before the rest are summarized as "+N more".</summary>
    private const int MaxCardsShown = 20;

    private readonly Func<string?, Uri?> _resolveUrl;
    private bool _closingByHost;
    private bool _chosen;

    public string CallId { get; }

    /// <summary>The agent chose an action; the URL is set for "Answer &amp; open".</summary>
    public event Action<IncomingCallAction, string?>? Chosen;

    /// <summary>The agent opened a record's link without answering.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>The window closed without an action and without the coordinator closing it (e.g. Alt+F4).</summary>
    public event Action? ClosedByAgent;

    public IncomingCallWindow(RingingCall call, Func<string?, Uri?> resolveUrl)
    {
        InitializeComponent();
        CallId = call.CallId;
        _resolveUrl = resolveUrl;
        Render(call);

        Loaded += (_, _) =>
        {
            // Force above everything — including other topmost windows — regardless of the
            // phone window's "always on top" preference. Toggling re-asserts z-order.
            Topmost = false;
            Topmost = true;
            Activate();
        };
        Closed += (_, _) =>
        {
            if (!_closingByHost && !_chosen) ClosedByAgent?.Invoke();
        };
    }

    /// <summary>Close on the coordinator's behalf (the call ended or was handled) — not a dismissal.</summary>
    public void CloseByHost()
    {
        _closingByHost = true;
        try { Close(); } catch { }
    }

    /// <summary>(Re)draw the caller details and matched records.</summary>
    public void Render(RingingCall call)
    {
        var context = call.Context ?? new CallContext();
        var cards = context.Cards ?? new List<ContextCard>();
        var number = call.Call.From ?? "";

        // The context heading names the list ("Matched customers"), not the caller. One match is
        // the caller; with several (or none) the number is the only thing known for sure.
        var name = cards.Count == 1 && !string.IsNullOrWhiteSpace(cards[0].Title) ? cards[0].Title : null;
        CallerName.Text = name ?? (string.IsNullOrEmpty(number) ? "Incoming call" : number);
        CallerNumber.Text = name is null ? (string.IsNullOrEmpty(number) ? "" : "Incoming call") : number;

        var queue = QueueNameOf(context);
        QueueText.Text = queue ?? "";
        QueueBadge.Visibility = string.IsNullOrEmpty(queue) ? Visibility.Collapsed : Visibility.Visible;

        VoicemailButton.Visibility = call.CanVoicemail ? Visibility.Visible : Visibility.Collapsed;
        VoicemailColumn.Width = call.CanVoicemail ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        VoicemailGap.Width = call.CanVoicemail ? new GridLength(8) : new GridLength(0);

        RenderCards(context, cards);
    }

    private void RenderCards(CallContext context, List<ContextCard> cards)
    {
        CardsPanel.Children.Clear();
        if (cards.Count == 0)
        {
            CardsSection.Visibility = Visibility.Collapsed;
            return;
        }

        CardsSection.Visibility = Visibility.Visible;
        CardsHeading.Text = string.IsNullOrWhiteSpace(context.Heading) ? "Matched records" : context.Heading;

        foreach (var card in cards.Take(MaxCardsShown))
            CardsPanel.Children.Add(BuildCard(card));

        var more = cards.Count - MaxCardsShown;
        MoreCards.Text = more > 0 ? $"+{more} more matched record{(more == 1 ? "" : "s")}" : "";
        MoreCards.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement BuildCard(ContextCard card)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(card.Title) ? "(no name)" : card.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Themed("Ink"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(card.Subtitle))
            body.Children.Add(MutedText(card.Subtitle!));
        if (!string.IsNullOrWhiteSpace(card.Description))
            body.Children.Add(MutedText(card.Description!, wrap: true));

        if (card.Badges is { Length: > 0 })
        {
            var badges = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var badge in card.Badges)
                badges.Children.Add(new Border
                {
                    Background = Themed("Panel"),
                    BorderBrush = Themed("Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(0, 0, 4, 0),
                    Child = new TextBlock { Text = badge, FontSize = 11, Foreground = Themed("Muted") },
                });
            body.Children.Add(badges);
        }

        var links = (card.Links ?? new List<ContextLink>())
            .Select(l => (Text: string.IsNullOrWhiteSpace(l.Text) ? l.Url : l.Text, Uri: _resolveUrl(l.Url)))
            .Where(l => l.Uri is not null)
            .ToList();
        if (links.Count > 0)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var link in links)
            {
                var hyperlink = new Hyperlink(new Run(link.Text)) { Foreground = Themed("BrandDark") };
                var url = link.Uri!.AbsoluteUri;
                hyperlink.Click += (_, _) => OpenRequested?.Invoke(url);
                row.Children.Add(new TextBlock(hyperlink) { FontSize = 12, Margin = new Thickness(0, 0, 10, 0) });
            }
            body.Children.Add(row);
        }

        if (_resolveUrl(card.Url) is { } target)
        {
            var url = target.AbsoluteUri;
            var actions = new WrapPanel();
            var answerAndOpen = new Button
            {
                Content = string.IsNullOrWhiteSpace(card.AnswerAndOpenText) ? "Answer & open" : card.AnswerAndOpenText,
                Style = (Style)FindResource("CardAnswerButton"),
            };
            answerAndOpen.Click += (_, _) => Choose(IncomingCallAction.Answer, url);
            var open = new Button
            {
                Content = string.IsNullOrWhiteSpace(card.OpenText) ? "Open" : card.OpenText,
                Style = (Style)FindResource("CardButton"),
            };
            open.Click += (_, _) => OpenRequested?.Invoke(url);
            actions.Children.Add(answerAndOpen);
            actions.Children.Add(open);
            body.Children.Add(actions);
        }

        return new Border
        {
            Background = Themed("Panel"),
            BorderBrush = Themed("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 6, 0, 0),
            Child = body,
        };
    }

    private static TextBlock MutedText(string text, bool wrap = false) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Themed("Muted"),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxHeight = wrap ? 34 : double.PositiveInfinity, // two lines
    };

    private static Brush Themed(string key) => (Brush)System.Windows.Application.Current.FindResource(key);

    private static string? QueueNameOf(CallContext context)
    {
        if (context.Properties is not null)
        {
            // "queue" is the display name; older payloads only carried "queueId".
            foreach (var key in new[] { "queue", "queueId" })
                if (context.Properties.TryGetValue(key, out var q) && !string.IsNullOrEmpty(q))
                    return q;
        }
        return context.Cards?.SelectMany(c => c.Badges ?? Array.Empty<string>()).FirstOrDefault();
    }

    private void Choose(IncomingCallAction action, string? url = null)
    {
        if (_chosen) return;
        _chosen = true;
        Chosen?.Invoke(action, url);
    }

    private void Answer_Click(object sender, RoutedEventArgs e) => Choose(IncomingCallAction.Answer);
    private void Decline_Click(object sender, RoutedEventArgs e) => Choose(IncomingCallAction.Decline);
    private void Voicemail_Click(object sender, RoutedEventArgs e) => Choose(IncomingCallAction.Voicemail);
}
