namespace SoftPhone.Core.Hub;

/// <summary>Connection lifecycle state surfaced by the connection host (mirrors the extension).</summary>
public enum ConnectionStatus
{
    Idle,
    Connecting,
    Connected,
    Reconnecting,
    SignedOut,
    Error,
}
