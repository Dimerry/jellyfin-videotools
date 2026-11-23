using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Detects MP4 files whose video track has no composition time offset table (ctts) while the
/// elementary stream still contains B-frames. Such files only carry decode-order timestamps,
/// so players that trust container timestamps (ExoPlayer and friends) drop or delay frames and
/// look like they stutter, even though the bitstream itself is valid.
/// </summary>
internal static class Mp4TimingInspector
{
    private const int MaxCacheEntries = 8192;
    private const int ProbeTimeoutMs = 20000;
    private const int DrainTimeoutMs = 2000;
    private static readonly string[] VideoExtensions = [".mp4", ".m4v", ".mov"];
    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.Ordinal);
    private static readonly bool Enabled = ReadEnabled();

    /// <summary>
    /// Returns <c>true</c> when the given path is an MP4 whose timestamps are decode-order only
    /// because the container has no ctts box, while the stream contains B-frames.
    /// </summary>
    /// <param name="path">Absolute path of the media file.</param>
    /// <param name="mediaEncoder">Media encoder, used to locate ffprobe.</param>
    /// <param name="logger">Logger.</param>
    /// <returns><c>true</c> if playback should be transcoded instead of direct played.</returns>
    internal static bool ShouldForceTranscode(string? path, IMediaEncoder mediaEncoder, ILogger logger)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(path) || !IsSupportedExtension(path))
        {
            return false;
        }

        string cacheKey;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                return false;
            }

            cacheKey = string.Create(
                CultureInfo.InvariantCulture,
                $"{path}|{info.LastWriteTimeUtc.Ticks}|{info.Length}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        bool result;
        try
        {
            result = Inspect(path, mediaEncoder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            logger.LogDebug(ex, "Unable to inspect MP4 timing of {Path}", path);
            result = false;
        }

        if (Cache.Count >= MaxCacheEntries)
        {
            Cache.Clear();
        }

        Cache[cacheKey] = result;

        if (result)
        {
            logger.LogInformation(
                "MP4 file {Path} has B-frames but no composition time offsets (ctts); direct play disabled, the file will be transcoded",
                path);
        }

        return result;
    }

    private static bool ReadEnabled()
    {
        var value = Environment.GetEnvironmentVariable("VIDEOTOOLS_NO_CTTS_TRANSCODE");
        return !string.Equals(value, "0", StringComparison.Ordinal)
            && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedExtension(string path)
    {
        var extension = Path.GetExtension(path);
        foreach (var candidate in VideoExtensions)
        {
            if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Inspect(string path, IMediaEncoder mediaEncoder)
    {
        if (!TryInspectContainer(path, out var hasVideoTrack, out var hasCompositionOffsets))
        {
            return false;
        }

        if (!hasVideoTrack || hasCompositionOffsets)
        {
            return false;
        }

        return HasBFrames(path, mediaEncoder);
    }

    /// <summary>
    /// Walks the MP4 box tree and reports whether the first video track exists and whether its
    /// sample table carries a ctts box.
    /// </summary>
    private static bool TryInspectContainer(string path, out bool hasVideoTrack, out bool hasCompositionOffsets)
    {
        hasVideoTrack = false;
        hasCompositionOffsets = false;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 8192,
            FileOptions.RandomAccess);

        var length = stream.Length;
        Box? moov = null;
        var fragmented = false;

        foreach (var box in EnumerateBoxes(stream, 0, length))
        {
            if (string.Equals(box.Type, "moov", StringComparison.Ordinal))
            {
                moov = box;
            }
            else if (string.Equals(box.Type, "moof", StringComparison.Ordinal))
            {
                // Fragmented MP4 stores timing in trun boxes, which is a different (and fine) layout.
                fragmented = true;
            }
        }

        if (fragmented || moov is null)
        {
            return false;
        }

        foreach (var trak in EnumerateBoxes(stream, moov.Value.Start, moov.Value.End))
        {
            if (!string.Equals(trak.Type, "trak", StringComparison.Ordinal))
            {
                continue;
            }

            var isVideo = false;
            Box? sampleTable = null;

            foreach (var mdia in EnumerateBoxes(stream, trak.Start, trak.End))
            {
                if (!string.Equals(mdia.Type, "mdia", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var child in EnumerateBoxes(stream, mdia.Start, mdia.End))
                {
                    if (string.Equals(child.Type, "hdlr", StringComparison.Ordinal))
                    {
                        stream.Position = child.Start + 8;
                        var buffer = new byte[4];
                        if (stream.Read(buffer, 0, 4) == 4)
                        {
                            isVideo = buffer[0] == (byte)'v' && buffer[1] == (byte)'i'
                                && buffer[2] == (byte)'d' && buffer[3] == (byte)'e';
                        }
                    }
                    else if (string.Equals(child.Type, "minf", StringComparison.Ordinal))
                    {
                        foreach (var minfChild in EnumerateBoxes(stream, child.Start, child.End))
                        {
                            if (string.Equals(minfChild.Type, "stbl", StringComparison.Ordinal))
                            {
                                sampleTable = minfChild;
                            }
                        }
                    }
                }
            }

            if (!isVideo || sampleTable is null)
            {
                continue;
            }

            hasVideoTrack = true;
            foreach (var stblChild in EnumerateBoxes(stream, sampleTable.Value.Start, sampleTable.Value.End))
            {
                if (string.Equals(stblChild.Type, "ctts", StringComparison.Ordinal))
                {
                    hasCompositionOffsets = true;
                }
            }

            return true;
        }

        return true;
    }

    private static bool HasBFrames(string path, IMediaEncoder mediaEncoder)
    {
        var probePath = mediaEncoder.ProbePath;
        if (string.IsNullOrWhiteSpace(probePath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(probePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-select_streams");
        startInfo.ArgumentList.Add("v:0");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("stream=has_b_frames");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=nw=1:nk=1");
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        // Both pipes must be drained concurrently: a probe that fills the 4 KB stderr
        // buffer blocks on write, so reading stdout synchronously can deadlock until the
        // timeout fires even though the process is healthy.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorsTask = process.StandardError.ReadToEndAsync();

        var exited = process.WaitForExit(ProbeTimeoutMs);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
            }
        }

        // Observe both tasks instead of discarding them; an abandoned read leaves the
        // pipe undrained and the probe result unusable.
        var output = ReadAll(outputTask);
        _ = ReadAll(errorsTask);

        if (!exited)
        {
            return false;
        }

        return int.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames)
            && frames > 0;
    }

    private static string ReadAll(Task<string> readTask)
    {
        try
        {
            // Bounded wait: a process that survived Kill() would otherwise keep the pipe
            // open and block the caller forever.
            if (!readTask.Wait(DrainTimeoutMs))
            {
                return string.Empty;
            }

            return readTask.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or AggregateException)
        {
            // The pipe was torn down while draining (for example after a kill).
            return string.Empty;
        }
    }

    private static IEnumerable<Box> EnumerateBoxes(FileStream stream, long start, long end)
    {
        var boxes = new List<Box>();
        var position = start;
        var header = new byte[16];

        while (position + 8 <= end)
        {
            stream.Position = position;
            if (stream.Read(header, 0, 8) != 8)
            {
                break;
            }

            var size = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            long headerSize = 8;

            if (size == 1)
            {
                if (position + 16 > end || stream.Read(header, 8, 8) != 8)
                {
                    break;
                }

                size = 0;
                for (var i = 8; i < 16; i++)
                {
                    size = (size << 8) | header[i];
                }

                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < headerSize || position + size > end)
            {
                break;
            }

            boxes.Add(new Box(type, position + headerSize, position + size));
            position += size;
        }

        return boxes;
    }

    /// <summary>
    /// A parsed MP4 box: type plus the payload range (header excluded).
    /// </summary>
    private readonly record struct Box(string Type, long Start, long End);
}
