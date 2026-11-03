using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Builds the ffmpeg command lines for a transcode job, ordered from the fastest hardware encoder
/// the machine can run down to plain software encoding.
/// </summary>
/// <remarks>
/// Hardware encoder support cannot be detected reliably by looking at the ffmpeg build alone: a
/// build usually ships <c>hevc_qsv</c> / <c>hevc_nvenc</c> even when the machine has no such GPU,
/// and an Intel iGPU without AV1 encode support still advertises <c>av1_qsv</c>. The plan therefore
/// returns a chain of attempts; the worker runs them in order and stops at the first success.
/// Encoders that fail twice are skipped for the rest of the process lifetime so a machine without
/// that hardware does not pay the failed attempt on every job.
/// </remarks>
internal static class TranscodePlanner
{
    /// <summary>
    /// Number of failed runs after which a hardware encoder is no longer offered.
    /// </summary>
    private const int HardwareFailureLimit = 2;

    /// <summary>
    /// Sources measured below this frame rate are considered "low frame rate" for interpolation.
    /// </summary>
    private const double InterpolationSourceFloor = 23.0;

    /// <summary>
    /// Sources shorter than this are interpolated in a single pass; splitting adds no value there.
    /// </summary>
    private const double ParallelInterpolationMinDuration = 120.0;

    /// <summary>
    /// Target length of one parallel interpolation segment in seconds (before the pre-roll overlap).
    /// </summary>
    private const double ParallelSegmentSeconds = 120.0;

    /// <summary>
    /// Seconds re-decoded at the start of every segment so motion estimation has context.
    /// The overlap is trimmed frame-accurately before encoding.
    /// </summary>
    private const double ParallelPreRollSeconds = 1.0;

    /// <summary>
    /// Frame rates outside this range are treated as broken container metadata instead of truth.
    /// </summary>
    private const double MaxPlausibleFrameRate = 240.0;

    private const string QsvOnly = "qsv";

    private const string NvencOnly = "nvenc";

    private const string SoftwareOnly = "software";

    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> EncoderCache = new();

    private static readonly ConcurrentDictionary<string, int> HardwareFailures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where a transcode attempt decodes its input.
    /// </summary>
    private enum DecodeMode
    {
        /// <summary>CPU decoding.</summary>
        Software,

        /// <summary>Intel Quick Sync decoding.</summary>
        Qsv,

        /// <summary>NVIDIA CUDA decoding.</summary>
        Cuda
    }

    /// <summary>
    /// Builds the ordered list of attempts for a transcode request.
    /// </summary>
    /// <param name="request">The transcode request.</param>
    /// <returns>The attempts, fastest first. The last one is always a software encoder.</returns>
    internal static IReadOnlyList<Attempt> Build(Request request)
    {
        var isAv1 = string.Equals(request.Codec, "av1", StringComparison.OrdinalIgnoreCase);
        var forced = GetForcedEncoder();
        var available = GetAvailableEncoders(request.FfmpegPath);
        var attempts = new List<Attempt>();
        var interpolate = ShouldInterpolate(request);

        foreach (var (encoder, label) in GetCandidates(isAv1))
        {
            var isHardware = IsHardwareEncoder(encoder);

            // Motion interpolation needs the frames on the CPU: QSV/CUDA decoders output GPU
            // surfaces the minterpolate filter cannot read, so interpolation forces software decode.
            if (isHardware && interpolate)
            {
                // Fall through to the software-decoded variant below; the hardware *encoder* is kept.
            }
            else if (isHardware)
            {
                if (forced == SoftwareOnly
                    || (forced == QsvOnly && !encoder.EndsWith("_qsv", StringComparison.Ordinal))
                    || (forced == NvencOnly && !encoder.EndsWith("_nvenc", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!available.Contains(encoder) || HardwareFailures.GetValueOrDefault(encoder) >= HardwareFailureLimit)
                {
                    continue;
                }

                // GPU decoding is an optimisation only: a decoder that rejects the source must not
                // cost us the hardware encoder, so the CPU-decoded variant is always appended too.
                if (IsHardwareDecodable(request.SourceVideoCodec))
                {
                    var decode = encoder.EndsWith("_qsv", StringComparison.Ordinal) ? DecodeMode.Qsv : DecodeMode.Cuda;
                    attempts.Add(BuildAttempt(request, encoder, label, decode, isHardware));
                }
            }

            attempts.Add(BuildAttempt(request, encoder, label, DecodeMode.Software, isHardware));
        }

        // minterpolate is single-threaded inside ffmpeg, so a single pass saturates one core and a
        // 20-core NAS finishes a 15-minute source in three quarters of an hour. When the source is
        // long enough, offer a segmented plan first: N ffmpeg workers each interpolate+encode a
        // slice (with a short pre-roll for motion context), the worker concatenates afterwards.
        // It is tried before the single-pass attempts; if any stage fails the chain falls back.
        if (interpolate
            && request.Duration > ParallelInterpolationMinDuration
            && forced != SoftwareOnly)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var parallelAttempts = new List<Attempt>();
            foreach (var attempt in attempts)
            {
                if (seen.Add(attempt.Encoder))
                {
                    parallelAttempts.Add(BuildParallelAttempt(request, attempt.Encoder));
                }
            }

            attempts.InsertRange(0, parallelAttempts);
        }

        return attempts;
    }

    /// <summary>
    /// Builds the segmented interpolation attempt for one encoder. The attempt carries the
    /// segment geometry; <see cref="Controllers.VideoToolsController"/> runs the stages and
    /// concatenates the outputs itself.
    /// </summary>
    /// <param name="request">The transcode request.</param>
    /// <param name="encoder">The video encoder to use for every segment.</param>
    /// <returns>The parallel attempt.</returns>
    private static Attempt BuildParallelAttempt(Request request, string encoder)
    {
        var segments = Math.Max(2, Math.Min(12, (int)Math.Ceiling(request.Duration / ParallelSegmentSeconds)));
        var isAv1 = string.Equals(request.Codec, "av1", StringComparison.OrdinalIgnoreCase);
        var label = "Segmented interpolation x" + segments.ToString(CultureInfo.InvariantCulture);

        var plan = new ParallelInterpolationPlan(
            request.SourcePath,
            request.OutputPath,
            request.Duration,
            segments,
            ParallelPreRollSeconds,
            encoder,
            isAv1 ? "8" : "medium",
            request.BitrateKbps,
            GetTargetSize(request));

        // Args stay empty: the segmented pipeline is driven by the controller, not by one process.
        return new Attempt([], encoder, IsHardwareEncoder(encoder), false, label, plan);
    }

    /// <summary>
    /// Tells whether a hardware encoder should be offered by the planner.
    /// </summary>
    /// <param name="encoder">The encoder name.</param>
    /// <returns><c>true</c> when the name is a hardware encoder.</returns>
    internal static bool IsHardwareEncoder(string encoder)
    {
        return encoder.EndsWith("_qsv", StringComparison.Ordinal)
            || encoder.EndsWith("_nvenc", StringComparison.Ordinal);
    }

    /// <summary>
    /// Records that a hardware encoder produced output.
    /// </summary>
    /// <param name="encoder">The encoder name.</param>
    internal static void ReportHardwareSuccess(string encoder)
    {
        HardwareFailures.TryRemove(encoder, out _);
    }

    /// <summary>
    /// Records that a hardware encoder failed with CPU-decoded frames, which means the encoder
    /// itself is not usable on this machine.
    /// </summary>
    /// <param name="encoder">The encoder name.</param>
    internal static void ReportHardwareFailure(string encoder)
    {
        HardwareFailures.AddOrUpdate(encoder, 1, (_, count) => count + 1);
    }

    /// <summary>
    /// Returns the encoders the local ffmpeg build provides.
    /// </summary>
    /// <param name="ffmpegPath">The ffmpeg binary.</param>
    /// <returns>The encoder names, or an empty set when ffmpeg could not be queried.</returns>
    internal static IReadOnlySet<string> GetAvailableEncoders(string ffmpegPath)
    {
        return EncoderCache.GetOrAdd(ffmpegPath, path =>
        {
            var encoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var startInfo = new ProcessStartInfo(path)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-hide_banner");
                startInfo.ArgumentList.Add("-encoders");

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return encoders;
                }

                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                if (!process.WaitForExit(15000))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                        // The process already exited.
                    }
                }

                foreach (var line in output.Split('\n'))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    // Encoder lines look like " V....D hevc_qsv   HEVC (Intel Quick Sync Video ...)".
                    if (parts.Length >= 2
                        && parts[0].Length == 6
                        && parts[0].Contains('V', StringComparison.Ordinal))
                    {
                        encoders.Add(parts[1]);
                    }
                }
            }
            catch (Exception)
            {
                // No usable ffmpeg here; the caller still gets the software encoder attempts.
            }

            return encoders;
        });
    }

    private static string? GetForcedEncoder()
    {
        var forced = Environment.GetEnvironmentVariable("VIDEOTOOLS_ENCODER")?.Trim().ToLowerInvariant();
        return forced switch
        {
            "qsv" => QsvOnly,
            "nvenc" => NvencOnly,
            "software" => SoftwareOnly,
            _ => null
        };
    }

    private static (string Encoder, string Label)[] GetCandidates(bool isAv1)
    {
        return isAv1
            ?
            [
                ("av1_qsv", "Intel QSV AV1"),
                ("av1_nvenc", "NVIDIA NVENC AV1"),
                ("libsvtav1", "SVT-AV1 (software)")
            ]
            :
            [
                ("hevc_qsv", "Intel QSV H.265"),
                ("hevc_nvenc", "NVIDIA NVENC H.265"),
                ("libx265", "x265 (software)")
            ];
    }

    private static Attempt BuildAttempt(
        Request request,
        string encoder,
        string label,
        DecodeMode decode,
        bool isHardware)
    {
        var isAv1 = string.Equals(request.Codec, "av1", StringComparison.OrdinalIgnoreCase);
        var interpolate = ShouldInterpolate(request);
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y"
        };

        switch (decode)
        {
            case DecodeMode.Qsv:
                args.AddRange(["-hwaccel", "qsv", "-hwaccel_output_format", "qsv"]);
                break;
            case DecodeMode.Cuda:
                args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]);
                break;
        }

        args.Add("-i");
        args.Add(request.SourcePath);

        var targetSize = GetTargetSize(request);
        var filters = new List<string>();
        if (targetSize is { } size)
        {
            filters.Add(decode switch
            {
                DecodeMode.Qsv => string.Create(CultureInfo.InvariantCulture, $"scale_qsv={size.Width}:{size.Height}"),
                DecodeMode.Cuda => string.Create(CultureInfo.InvariantCulture, $"scale_cuda={size.Width}:{size.Height}"),
                _ => string.Create(CultureInfo.InvariantCulture, $"scale={size.Width}:{size.Height}")
            });
        }

        if (interpolate)
        {
            // Motion-compensated interpolation on the CPU; the hardware *encoder* is unaffected.
            filters.Add("minterpolate=fps=30:mi_mode=mci:me_mode=bidir:mc_mode=aobmc:vsbmc=1");
        }

        if (filters.Count > 0)
        {
            args.Add("-vf");
            args.Add(string.Join(",", filters));
        }

        args.AddRange(
        [
            "-map", "0:v:0",
            "-map", "0:a?",
            "-map_metadata", "0",
            "-map_chapters", "0",
            "-c:v", encoder
        ]);

        // SVT-AV1 numbers its presets the other way round; QSV/NVENC/x265 all accept "medium".
        args.Add("-preset");
        args.Add(encoder == "libsvtav1" ? "8" : "medium");

        args.Add("-b:v");
        args.Add(request.BitrateKbps.ToString(CultureInfo.InvariantCulture) + "k");

        // The encoder derives its frame-rate based bitrate control from the stream frame rate, which
        // is wrong on lots of remuxed/phone files (120/1 reported for a 30 fps video). A 3000k
        // request then yields a quarter of the bitrate. Pin the output rate to the real rate.
        // With interpolation the target rate is always 30; without one, only a plausible source
        // rate is pinned (a garbage 1000/1 "tbr" would burst the encoder's frame count/budget).
        var frameRate = interpolate ? 30.0 : GetEncoderFrameRate(request);
        if (frameRate > 0)
        {
            args.Add("-r");
            args.Add(FormatFrameRate(frameRate));
        }

        if (!isAv1)
        {
            args.Add("-maxrate");
            args.Add(request.BitrateKbps.ToString(CultureInfo.InvariantCulture) + "k");
            args.Add("-bufsize");
            args.Add((request.BitrateKbps * 2).ToString(CultureInfo.InvariantCulture) + "k");
        }

        // MP4 needs AAC audio for broad compatibility; DTS/TrueHD cannot be copied into MP4.
        args.AddRange(["-c:a", "aac", "-b:a", "192k"]);

        if (request.SubtitleIndexes.Count > 0)
        {
            foreach (var index in request.SubtitleIndexes)
            {
                args.Add("-map");
                args.Add("0:" + index.ToString(CultureInfo.InvariantCulture));
            }

            // Text subtitles are converted to MP4's mov_text. Bitmap subtitles are skipped.
            args.AddRange(["-c:s", "mov_text"]);
        }
        else
        {
            args.Add("-sn");
        }

        args.AddRange(["-movflags", "+faststart", request.OutputPath]);

        return new Attempt(args, encoder, isHardware, decode != DecodeMode.Software, label);
    }

    /// <summary>
    /// Tells whether the GPU can decode the given codec in hardware.
    /// </summary>
    /// <param name="codec">The ffprobe codec name.</param>
    /// <returns><c>true</c> when hardware decoding is worth trying.</returns>
    private static bool IsHardwareDecodable(string? codec)
    {
        return codec switch
        {
            "h264" or "hevc" or "av1" or "vp8" or "vp9" or "mpeg2video" or "vc1" or "mjpeg" => true,
            _ => false
        };
    }

    /// <summary>
    /// Tells whether the request asked for interpolation and the source actually qualifies
    /// (a usable, plausible frame rate below the floor).
    /// </summary>
    /// <param name="request">The transcode request.</param>
    /// <returns><c>true</c> when the source should be motion-interpolated to 30 fps.</returns>
    private static bool ShouldInterpolate(Request request)
    {
        if (!request.Interpolate)
        {
            return false;
        }

        var rate = request.FrameRate >= 1
            ? request.FrameRate
            : request.NominalFrameRate;
        return rate >= 1 && rate <= MaxPlausibleFrameRate && rate < InterpolationSourceFloor;
    }

    private static double GetEncoderFrameRate(Request request)
    {
        if (request.FrameRate >= 1)
        {
            return request.FrameRate;
        }

        return request.NominalFrameRate >= 1 ? request.NominalFrameRate : 0;
    }

    private static string FormatFrameRate(double frameRate)
    {
        double[] standardRates = { 23.976, 24, 25, 29.97, 30, 48, 50, 59.94, 60, 100, 119.88, 120 };
        var best = frameRate;
        var bestDelta = 0.05;
        foreach (var standard in standardRates)
        {
            var delta = Math.Abs(frameRate - standard);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = standard;
            }
        }

        return Math.Round(best, 3, MidpointRounding.AwayFromZero)
            .ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static (int Width, int Height)? GetTargetSize(Request request)
    {
        if (request.Width <= 0 || request.Height <= 0 || request.Height <= request.MaxHeight)
        {
            return null;
        }

        var width = (int)Math.Round(
            request.Width * (double)request.MaxHeight / request.Height,
            MidpointRounding.AwayFromZero);
        width = Math.Max(2, width & ~1);
        return (width, request.MaxHeight);
    }

    /// <summary>
    /// Everything the planner needs to know about one transcode.
    /// </summary>
    internal sealed record Request
    {
        /// <summary>
        /// Gets the source file path.
        /// </summary>
        public string SourcePath { get; init; } = string.Empty;

        /// <summary>
        /// Gets the source duration in seconds, used to size the segmented interpolation plan.
        /// </summary>
        public double Duration { get; init; }

        /// <summary>
        /// Gets the temporary output path.
        /// </summary>
        public string OutputPath { get; init; } = string.Empty;

        /// <summary>
        /// Gets the target codec: <c>h265</c> or <c>av1</c>.
        /// </summary>
        public string Codec { get; init; } = "h265";

        /// <summary>
        /// Gets the target video bitrate in kbps.
        /// </summary>
        public int BitrateKbps { get; init; }

        /// <summary>
        /// Gets the maximum output height.
        /// </summary>
        public int MaxHeight { get; init; }

        /// <summary>
        /// Gets the source video codec reported by ffprobe.
        /// </summary>
        public string? SourceVideoCodec { get; init; }

        /// <summary>
        /// Gets the source width.
        /// </summary>
        public int Width { get; init; }

        /// <summary>
        /// Gets the source height.
        /// </summary>
        public int Height { get; init; }

        /// <summary>
        /// Gets the measured average frame rate.
        /// </summary>
        public double FrameRate { get; init; }

        /// <summary>
        /// Gets the container's nominal frame rate.
        /// </summary>
        public double NominalFrameRate { get; init; }

        /// <summary>
        /// Gets the subtitle stream indexes to carry over.
        /// </summary>
        public IReadOnlyList<int> SubtitleIndexes { get; init; } = Array.Empty<int>();

        /// <summary>
        /// Gets the ffmpeg binary used to list the available encoders.
        /// </summary>
        public string FfmpegPath { get; init; } = "ffmpeg";

        /// <summary>
        /// Gets a value indicating whether a low frame rate source should be interpolated to 30 fps.
        /// Only honored when the measured rate is plausible and below the floor.
        /// </summary>
        public bool Interpolate { get; init; }
    }

    /// <summary>
    /// Geometry of a segmented interpolation job: the controller splits the source into this many
    /// overlapping slices, encodes them concurrently and concatenates the results.
    /// </summary>
    /// <param name="SourcePath">The input file.</param>
    /// <param name="OutputPath">The final muxed output.</param>
    /// <param name="Duration">Source duration in seconds.</param>
    /// <param name="Segments">Number of video slices.</param>
    /// <param name="PreRollSeconds">Overlap re-decoded at the start of each slice.</param>
    /// <param name="Encoder">Video encoder used for every slice.</param>
    /// <param name="Preset">Encoder preset.</param>
    /// <param name="BitrateKbps">Target video bitrate.</param>
    /// <param name="TargetSize">Optional scale target.</param>
    internal sealed record ParallelInterpolationPlan(
        string SourcePath,
        string OutputPath,
        double Duration,
        int Segments,
        double PreRollSeconds,
        string Encoder,
        string Preset,
        int BitrateKbps,
        (int Width, int Height)? TargetSize);

    /// <summary>
    /// One ffmpeg invocation to try.
    /// </summary>
    /// <param name="Args">The ffmpeg arguments.</param>
    /// <param name="Encoder">The video encoder name, e.g. <c>hevc_nvenc</c>.</param>
    /// <param name="IsHardware">Whether the encoder runs on a GPU.</param>
    /// <param name="HardwareDecode">Whether the input is decoded on the GPU as well.</param>
    /// <param name="Label">Human readable name used in logs and the UI.</param>
    /// <param name="ParallelPlan">Present when the attempt is a segmented interpolation pipeline
    /// driven by the controller instead of a single ffmpeg process.</param>
    internal sealed record Attempt(
        List<string> Args,
        string Encoder,
        bool IsHardware,
        bool HardwareDecode,
        string Label,
        ParallelInterpolationPlan? ParallelPlan = null);
}
