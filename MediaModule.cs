using Windows.Media.Control;
using Windows.Storage.Streams;
using System.Security.Cryptography;

namespace OpulentHardware.VolumeMixer;

internal static class MediaModule
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Task<GlobalSystemMediaTransportControlsSessionManager>? _managerTask;
    private static string? _cachedArtworkKey;
    private static string? _cachedArtworkId;
    private static byte[]? _cachedArtworkBytes;
    private static string _cachedArtworkMime = "image/png";

    internal static async Task<MediaSnapshot> ReadAsync()
    {
        await Gate.WaitAsync();
        try
        {
            GlobalSystemMediaTransportControlsSessionManager manager = await GetManagerAsync();
            GlobalSystemMediaTransportControlsSession? session = manager.GetCurrentSession();
            if (session is null) return MediaSnapshot.Empty;

            GlobalSystemMediaTransportControlsSessionMediaProperties media = await session.TryGetMediaPropertiesAsync();
            GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
            GlobalSystemMediaTransportControlsSessionPlaybackControls controls = playback.Controls;
            string artworkKey = $"{session.SourceAppUserModelId}|{media.Artist}|{media.AlbumTitle}|{media.Title}";
            string? artworkId = await ReadArtworkAsync(media, artworkKey);

            return new MediaSnapshot(
                Present: true,
                Title: media.Title ?? string.Empty,
                Artist: media.Artist ?? string.Empty,
                Album: media.AlbumTitle ?? string.Empty,
                Source: FriendlySource(session.SourceAppUserModelId),
                Playing: playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                DurationMs: Math.Max(0, (long)timeline.EndTime.TotalMilliseconds),
                PositionMs: Math.Max(0, (long)timeline.Position.TotalMilliseconds),
                CanPrevious: controls.IsPreviousEnabled,
                CanPlayPause: controls.IsPlayEnabled || controls.IsPauseEnabled || controls.IsPlayPauseToggleEnabled,
                CanNext: controls.IsNextEnabled,
                CanSeek: controls.IsPlaybackPositionEnabled,
                ArtworkId: artworkId);
        }
        catch
        {
            return MediaSnapshot.Empty;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static async Task<bool> CommandAsync(string command)
    {
        await Gate.WaitAsync();
        try
        {
            GlobalSystemMediaTransportControlsSession? session = (await GetManagerAsync()).GetCurrentSession();
            if (session is null) return false;
            return command.Trim().ToLowerInvariant() switch
            {
                "previous" => await session.TrySkipPreviousAsync(),
                "playpause" => await session.TryTogglePlayPauseAsync(),
                "next" => await session.TrySkipNextAsync(),
                _ => false
            };
        }
        catch
        {
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static async Task<bool> SeekRelativeAsync(long offsetMs)
    {
        await Gate.WaitAsync();
        try
        {
            GlobalSystemMediaTransportControlsSession? session = (await GetManagerAsync()).GetCurrentSession();
            if (session is null || !session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled) return false;
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
            long startMs = Math.Max(0, (long)timeline.StartTime.TotalMilliseconds);
            long endMs = Math.Max(startMs, (long)timeline.EndTime.TotalMilliseconds);
            long currentMs = Math.Max(startMs, (long)timeline.Position.TotalMilliseconds);
            long requestedMs = currentMs + Math.Clamp(offsetMs, -60_000, 60_000);
            long targetMs = endMs > startMs ? Math.Clamp(requestedMs, startMs, endMs) : Math.Max(startMs, requestedMs);
            return await session.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(targetMs).Ticks);
        }
        catch
        {
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static bool TryGetArtwork(string artworkId, out byte[]? bytes, out string mime)
    {
        bytes = _cachedArtworkBytes;
        mime = _cachedArtworkMime;
        return bytes is not null && string.Equals(artworkId, _cachedArtworkId, StringComparison.Ordinal);
    }

    private static Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync() =>
        _managerTask ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();

    private static async Task<string?> ReadArtworkAsync(GlobalSystemMediaTransportControlsSessionMediaProperties media, string key)
    {
        if (key == _cachedArtworkKey && _cachedArtworkId is not null) return _cachedArtworkId;
        _cachedArtworkKey = key;
        _cachedArtworkId = null;
        _cachedArtworkBytes = null;
        if (media.Thumbnail is null) return null;

        using IRandomAccessStreamWithContentType stream = await media.Thumbnail.OpenReadAsync();
        if (stream.Size > 5_000_000) return null;
        using Stream input = stream.AsStreamForRead();
        using var output = new MemoryStream((int)Math.Min(stream.Size, 5_000_000));
        await input.CopyToAsync(output);
        string mime = string.IsNullOrWhiteSpace(stream.ContentType) ? "image/png" : stream.ContentType;
        _cachedArtworkBytes = output.ToArray();
        _cachedArtworkMime = mime;
        _cachedArtworkId = Convert.ToHexString(SHA256.HashData(_cachedArtworkBytes)).ToLowerInvariant()[..20];
        return _cachedArtworkId;
    }

    private static string FriendlySource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "WINDOWS MEDIA";
        string value = source.Split('!')[0];
        if (value.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "SPOTIFY";
        if (value.Contains("applemusic", StringComparison.OrdinalIgnoreCase) || value.Contains("itunes", StringComparison.OrdinalIgnoreCase)) return "APPLE MUSIC";
        if (value.Contains("zunemusic", StringComparison.OrdinalIgnoreCase)) return "MEDIA PLAYER";
        if (value.Contains("youtube", StringComparison.OrdinalIgnoreCase)) return "YOUTUBE MUSIC";
        return Path.GetFileNameWithoutExtension(value).Replace('.', ' ').ToUpperInvariant();
    }
}

internal sealed record MediaSnapshot(
    bool Present,
    string Title,
    string Artist,
    string Album,
    string Source,
    bool Playing,
    long DurationMs,
    long PositionMs,
    bool CanPrevious,
    bool CanPlayPause,
    bool CanNext,
    bool CanSeek,
    string? ArtworkId)
{
    internal static MediaSnapshot Empty { get; } = new(false, string.Empty, string.Empty, string.Empty, string.Empty, false, 0, 0, false, false, false, false, null);
}
