using System.IO;
using System.Media;

namespace SoftPhone.App;

/// <summary>
/// Plays the bundled ringtone in a loop from the app process (contract §8) — not the web
/// page, so autoplay policy is irrelevant and it rings even when no window is focused.
/// Respects the effective RingtoneEnabled setting supplied by the caller.
/// </summary>
public sealed class RingtonePlayer : IDisposable
{
    private readonly SoundPlayer? _player;
    private bool _playing;

    public RingtonePlayer()
    {
        var path = AppPaths.Asset("ringtone.wav");
        if (File.Exists(path))
            _player = new SoundPlayer(path);
        else
            Log.Warn($"Ringtone asset missing: {path}");
    }

    public void Play()
    {
        if (_player is null || _playing) return;
        try
        {
            _player.PlayLooping();
            _playing = true;
        }
        catch (Exception ex)
        {
            Log.Error("Ringtone play failed", ex);
        }
    }

    public void Stop()
    {
        if (!_playing) return;
        try { _player?.Stop(); } catch { }
        _playing = false;
    }

    public void Dispose()
    {
        Stop();
        _player?.Dispose();
    }
}
