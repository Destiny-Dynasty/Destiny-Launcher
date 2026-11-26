namespace DestinyLauncher.Linux;

internal enum VideoPlaybackAction { Release, Pause, Play }

internal static class VideoPlaybackPolicy
{
    internal static VideoPlaybackAction Select(bool busy, bool visible, bool active, bool minimized, bool home)
    {
        if (busy || !visible) return VideoPlaybackAction.Release;
        if (!active || minimized || !home) return VideoPlaybackAction.Pause;
        return VideoPlaybackAction.Play;
    }
}
