using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.VideoToolDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Custom video tools: transcode an item to AV1/H.265 or cut a clip, writing the result next to the source file.
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
[Tags("VideoTools")]
public class VideoToolsController : BaseJellyfinApiController
{
    private static readonly ConcurrentDictionary<Guid, VideoToolJobDto> _jobs = new();
    private static readonly ConcurrentDictionary<Guid, Process> _processes = new();
    private static readonly ConcurrentQueue<JobWork> _pending = new();
    private static readonly SemaphoreSlim _pendingSignal = new(0);
    private static readonly object _workerLock = new();
    private static readonly object _pathLock = new();
    private static readonly object _renameLock = new();
    private static readonly object _scanLock = new();
    private static readonly HashSet<string> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _foldersToScan = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string FrameCacheDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-videotools-frames");
    private static readonly SemaphoreSlim _frameLock = new(2, 2);
    private static readonly double[] BackoffSeconds = [0.5, 1.0, 2.0, 5.0];
    private static readonly JsonSerializerOptions DefaultsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private static CancellationTokenSource? _scanCts;
    private static bool _workerStarted;

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IProviderManager _providerManager;
    private readonly IDirectoryService _directoryService;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<VideoToolsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoToolsController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="directoryService">Instance of the <see cref="IDirectoryService"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public VideoToolsController(
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IMediaEncoder mediaEncoder,
        IProviderManager providerManager,
        IDirectoryService directoryService,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<VideoToolsController> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _mediaEncoder = mediaEncoder;
        _providerManager = providerManager;
        _directoryService = directoryService;
        _serverConfigurationManager = serverConfigurationManager;
        _logger = logger;
    }

    /// <summary>
    /// Starts a background transcode of a video item to AV1 (software) or H.265 (Intel QSV hardware).
    /// The source file is renamed to "&lt;name&gt;.&lt;ext&gt;.old" when the job succeeds and the new file
    /// takes over the original name with an mp4 container.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="codec">Target codec: h265 (default, QSV) or av1 (software, slower).</param>
    /// <param name="bitrateKbps">Target video bitrate in kbps.</param>
    /// <param name="resolution">Maximum output resolution: 1080p (default) or 4k. Sources smaller than the limit are not upscaled.</param>
    /// <param name="interpolate">When true, sources below 23 fps are motion-interpolated to 30 fps.</param>
    /// <param name="rateControl">"quality" (default) or "bitrate": encode to a quality level or to <paramref name="bitrateKbps"/>.</param>
    /// <param name="quality">Quality level used in quality mode: highest, high, medium, low or lowest.</param>
    /// <param name="encoder">First encoder to try, e.g. h265_qsv; "auto" keeps the default cascade.</param>
    /// <returns>The created job.</returns>
    [HttpPost("{itemId}/Transcode")]
    public ActionResult<VideoToolJobDto> StartTranscode(
        [FromRoute] Guid itemId,
        [FromQuery] string? codec = null,
        [FromQuery] int? bitrateKbps = null,
        [FromQuery] string? resolution = null,
        [FromQuery] bool interpolate = false,
        [FromQuery] string? rateControl = null,
        [FromQuery] string? quality = null,
        [FromQuery] string? encoder = null)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var useQuality = !string.Equals(rateControl, "bitrate", StringComparison.OrdinalIgnoreCase);
        var qualityPreset = ParseQualityPreset(quality);
        var encoderPreference = ParseEncoderPreference(encoder);

        // A client that queues from the library without opening the dialog sends no explicit
        // settings; in that case the stored defaults decide codec, resolution and size mode.
        // Explicit query parameters always win over the defaults.
        if (codec is null && resolution is null && rateControl is null)
        {
            var transcodeDefaults = LoadDefaults().Transcode;
            codec = transcodeDefaults.Codec;
            resolution = transcodeDefaults.Resolution;
            useQuality = string.Equals(transcodeDefaults.RateControl, "quality", StringComparison.OrdinalIgnoreCase);
            qualityPreset = ParseQualityPreset(transcodeDefaults.Quality);
            encoderPreference = ParseEncoderPreference(transcodeDefaults.Encoder);
            bitrateKbps ??= transcodeDefaults.BitrateKbps;
        }

        codec ??= "h265";
        bitrateKbps ??= 3000;
        resolution ??= "1080p";

        if (!TryParseTranscodeParams(codec, bitrateKbps.Value, resolution, out var normalizedCodec, out var maxHeight, out var error, useQuality))
        {
            return BadRequest(error);
        }

        var job = EnqueueTranscode(item, normalizedCodec, bitrateKbps.Value, maxHeight, interpolate, useQuality, qualityPreset, encoderPreference);
        if (job is null)
        {
            return BadRequest("Item has no local video file.");
        }

        return Accepted(job);
    }

    /// <summary>
    /// Queues a transcode for several video items using the same settings.
    /// Items without a local file are skipped and reported back by name.
    /// </summary>
    /// <param name="itemIds">The item ids to transcode.</param>
    /// <param name="codec">Target codec: h265 (default, QSV) or av1 (software, slower).</param>
    /// <param name="bitrateKbps">Target video bitrate in kbps.</param>
    /// <param name="resolution">Maximum output resolution: 1080p (default) or 4k.</param>
    /// <param name="interpolate">When true, sources below 23 fps are motion-interpolated to 30 fps.</param>
    /// <param name="rateControl">"quality" (default) or "bitrate".</param>
    /// <param name="quality">Quality level used in quality mode.</param>
    /// <param name="encoder">First encoder to try; "auto" keeps the default cascade.</param>
    /// <returns>The queued jobs and the names of the items that were skipped.</returns>
    [HttpPost("Transcode")]
    public ActionResult<TranscodeBatchResultDto> StartTranscodeBatch(
        [FromBody] Guid[] itemIds,
        [FromQuery] string? codec = null,
        [FromQuery] int? bitrateKbps = null,
        [FromQuery] string? resolution = null,
        [FromQuery] bool interpolate = false,
        [FromQuery] string? rateControl = null,
        [FromQuery] string? quality = null,
        [FromQuery] string? encoder = null)
    {
        if (itemIds is null || itemIds.Length == 0)
        {
            return BadRequest("No items were selected.");
        }

        var useQuality = !string.Equals(rateControl, "bitrate", StringComparison.OrdinalIgnoreCase);
        var qualityPreset = ParseQualityPreset(quality);
        var encoderPreference = ParseEncoderPreference(encoder);

        // Same defaults rule as the single-item endpoint: a client without explicit settings gets
        // the stored defaults, explicit query parameters always win.
        if (codec is null && resolution is null && rateControl is null)
        {
            var transcodeDefaults = LoadDefaults().Transcode;
            codec = transcodeDefaults.Codec;
            resolution = transcodeDefaults.Resolution;
            useQuality = string.Equals(transcodeDefaults.RateControl, "quality", StringComparison.OrdinalIgnoreCase);
            qualityPreset = ParseQualityPreset(transcodeDefaults.Quality);
            encoderPreference = ParseEncoderPreference(transcodeDefaults.Encoder);
            bitrateKbps ??= transcodeDefaults.BitrateKbps;
        }

        codec ??= "h265";
        bitrateKbps ??= 3000;
        resolution ??= "1080p";

        if (!TryParseTranscodeParams(codec, bitrateKbps.Value, resolution, out var normalizedCodec, out var maxHeight, out var error, useQuality))
        {
            return BadRequest(error);
        }

        var result = new TranscodeBatchResultDto();
        foreach (var itemId in itemIds)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                result.SkippedNames.Add(itemId.ToString());
                continue;
            }

            var job = EnqueueTranscode(item, normalizedCodec, bitrateKbps.Value, maxHeight, interpolate, useQuality, qualityPreset, encoderPreference);
            if (job is null)
            {
                result.SkippedNames.Add(item.Name);
                _logger.LogWarning("Skipping transcode of {Name}: no local video file", item.Name);
                continue;
            }

            result.Jobs.Add(job);
        }

        result.Added = result.Jobs.Count;
        result.Skipped = result.SkippedNames.Count;
        return Ok(result);
    }

    /// <summary>
    /// Starts a background job that cuts the given number of seconds from the start and the end of a video item,
    /// keeping the middle part. The result takes over the original file name, and the original is kept as
    /// "&lt;name&gt;.old&lt;ext&gt;".
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="startSeconds">Seconds to remove from the start.</param>
    /// <param name="endSeconds">Seconds to remove from the end.</param>
    /// <param name="rotation">Counter-clockwise rotation in degrees (0, 90, 180 or 270).</param>
    /// <param name="cropLeft">Percent of the source width to remove from the left edge (0-100).</param>
    /// <param name="cropRight">Percent of the source width to remove from the right edge (0-100).</param>
    /// <param name="codec">Target codec for the re-encode a crop needs: h265 or av1; omitted means the stored cut defaults.</param>
    /// <param name="rateControl">"quality" or "bitrate"; omitted means the stored cut defaults.</param>
    /// <param name="quality">Quality level used in quality mode.</param>
    /// <param name="encoder">First encoder to try; "auto" keeps the default cascade.</param>
    /// <returns>The created job.</returns>
    [HttpPost("{itemId}/Cut")]
    public ActionResult<VideoToolJobDto> StartCut(
        [FromRoute] Guid itemId,
        [FromQuery] double startSeconds = 0,
        [FromQuery] double endSeconds = 0,
        [FromQuery] int rotation = 0,
        [FromQuery] double cropLeft = 0,
        [FromQuery] double cropRight = 0,
        [FromQuery] string? codec = null,
        [FromQuery] string? rateControl = null,
        [FromQuery] string? quality = null,
        [FromQuery] string? encoder = null)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var sourcePath = item.Path;
        if (string.IsNullOrWhiteSpace(sourcePath) || !System.IO.File.Exists(sourcePath))
        {
            return BadRequest("Item has no local video file.");
        }

        var probe = ProbeSource(
            sourcePath,
            string.IsNullOrWhiteSpace(_mediaEncoder.ProbePath) ? "ffprobe" : _mediaEncoder.ProbePath);
        var duration = probe.Duration > 0
            ? probe.Duration
            : TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds;
        if (duration <= 0)
        {
            return BadRequest("Item duration is unknown.");
        }

        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds)
            || startSeconds < 0 || endSeconds < 0)
        {
            return BadRequest("Trim values must be zero or positive numbers.");
        }

        if (startSeconds + endSeconds >= duration)
        {
            return BadRequest("Trim values remove the whole video.");
        }

        if (rotation != 0 && rotation != 90 && rotation != 180 && rotation != 270)
        {
            return BadRequest("Rotation must be 0, 90, 180 or 270 degrees.");
        }

        if (!TryNormalizeCrop(cropLeft, cropRight, out cropLeft, out cropRight, out var cropError))
        {
            return BadRequest(cropError);
        }

        if (cropLeft + cropRight > 0 && !HasVideoDimensions(probe.Width, probe.Height))
        {
            return BadRequest("Could not read the source video dimensions needed for cropping.");
        }

        var keepSeconds = duration - startSeconds - endSeconds;
        var paths = CreateCutPaths(sourcePath);

        // A crop re-encodes the picture, so its encoder and size mode come from the cut defaults
        // unless the client sends explicit values (the plain trim path never re-encodes and
        // therefore ignores them entirely).
        var cutCodec = "h265";
        var useQuality = true;
        var qualityPreset = VideoQualityPresets.high;
        var encoderPreference = VideoEncoderPreference.auto;
        if (HasCrop(cropLeft, cropRight))
        {
            var cutDefaults = LoadDefaults().Cut;
            cutCodec = cutDefaults.Codec;
            useQuality = !string.Equals(cutDefaults.RateControl, "bitrate", StringComparison.OrdinalIgnoreCase);
            qualityPreset = ParseQualityPreset(cutDefaults.Quality);
            encoderPreference = ParseEncoderPreference(cutDefaults.Encoder);

            if (codec is not null)
            {
                cutCodec = codec;
            }

            if (rateControl is not null)
            {
                useQuality = !string.Equals(rateControl, "bitrate", StringComparison.OrdinalIgnoreCase);
            }

            if (quality is not null)
            {
                qualityPreset = ParseQualityPreset(quality);
            }

            if (encoder is not null)
            {
                encoderPreference = ParseEncoderPreference(encoder);
            }

            // The cut pipeline only offers the two library codecs; anything else falls back to h265.
            cutCodec = string.Equals(cutCodec, "av1", StringComparison.OrdinalIgnoreCase) ? "av1" : "h265";
        }

        var job = CreateJob("Cut", item.Id, item.Name, paths.FinalPath, sourcePath, duration);
        job.WorkPath = paths.WorkPath;
        job.CutStartSeconds = startSeconds;
        job.CutEndSeconds = endSeconds;
        job.CutRotation = rotation;
        job.CutCropLeftPercent = cropLeft;
        job.CutCropRightPercent = cropRight;
        job.SourceWidth = probe.Width;
        job.SourceHeight = probe.Height;
        if (HasCrop(cropLeft, cropRight))
        {
            job.Codec = string.Equals(cutCodec, "av1", StringComparison.OrdinalIgnoreCase) ? "AV1" : "H.265";
            job.UseQuality = useQuality;
            job.Quality = useQuality ? qualityPreset.ToString().ToLowerInvariant() : null;
            job.BitrateKbps = useQuality ? null : Math.Max(3000, (int)Math.Round(EstimateReencodeBitrate(probe, job)));
        }

        StartJob(job, BuildCutPlan(job, probe, cutCodec, useQuality, qualityPreset, encoderPreference));
        return Accepted(job);
    }

    /// <summary>
    /// Gets the queue defaults used when a job is started from the library without explicit settings.
    /// </summary>
    /// <returns>The stored defaults.</returns>
    [HttpGet("Defaults")]
    public ActionResult<VideoToolsDefaultsDto> GetDefaults()
        => Ok(LoadDefaults());

    /// <summary>
    /// Replaces the queue defaults.
    /// </summary>
    /// <param name="defaults">The new defaults.</param>
    /// <returns>The stored defaults.</returns>
    [HttpPost("Defaults")]
    public ActionResult<VideoToolsDefaultsDto> SaveDefaults([FromBody] VideoToolsDefaultsDto defaults)
    {
        if (defaults is null)
        {
            return BadRequest("No defaults were provided.");
        }

        var normalized = NormalizeDefaults(defaults);
        SaveDefaultsToDisk(normalized);
        return Ok(normalized);
    }

    /// <summary>
    /// Gets recent jobs.
    /// </summary>
    /// <returns>The list of jobs.</returns>
    [HttpGet("Jobs")]
    public ActionResult<List<VideoToolJobDto>> GetJobs()
        => Ok(_jobs.Values.OrderByDescending(j => j.CreatedAt).Take(50).ToList());

    /// <summary>
    /// Gets a single job by id.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <returns>The job.</returns>
    [HttpGet("Jobs/{jobId}")]
    public ActionResult<VideoToolJobDto> GetJob([FromRoute] Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return NotFound();
        }

        return Ok(job);
    }

    /// <summary>
    /// Clears completed and failed jobs from the in-memory history. Active jobs are kept.
    /// </summary>
    /// <returns>The number of removed jobs.</returns>
    [HttpDelete("Jobs/Finished")]
    public ActionResult<int> ClearFinishedJobs()
    {
        var finishedIds = _jobs
            .Where(pair => pair.Value.Status is "Completed" or "Failed" or "Canceled")
            .Select(pair => pair.Key)
            .ToList();

        foreach (var id in finishedIds)
        {
            _jobs.TryRemove(id, out _);
        }

        return Ok(finishedIds.Count);
    }

    /// <summary>
    /// Re-runs a failed job with its original settings and a new output file name.
    /// </summary>
    /// <param name="jobId">The failed job id.</param>
    /// <returns>The newly queued job.</returns>
    [HttpPost("Jobs/{jobId}/Retry")]
    public ActionResult<VideoToolJobDto> RetryJob([FromRoute] Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var failedJob))
        {
            return NotFound();
        }

        if (!string.Equals(failedJob.Status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Only failed jobs can be retried.");
        }

        if (string.IsNullOrWhiteSpace(failedJob.SourcePath) || !System.IO.File.Exists(failedJob.SourcePath))
        {
            return BadRequest("The source file no longer exists.");
        }

        try
        {
            var probe = ProbeSource(
                failedJob.SourcePath,
                string.IsNullOrWhiteSpace(_mediaEncoder.ProbePath) ? "ffprobe" : _mediaEncoder.ProbePath);
            var duration = probe.Duration > 0 ? probe.Duration : failedJob.SourceDurationSeconds;

            if (string.Equals(failedJob.Kind, "Transcode", StringComparison.OrdinalIgnoreCase))
            {
                var codec = string.Equals(failedJob.Codec, "AV1", StringComparison.OrdinalIgnoreCase) ? "av1" : "h265";
                var maxHeight = string.Equals(failedJob.Resolution, "4K", StringComparison.OrdinalIgnoreCase) ? 2160 : 1080;
                var bitrateKbps = failedJob.BitrateKbps ?? 3000;
                var interpolate = failedJob.FrameInterpolation;
                var retryUseQuality = failedJob.UseQuality;
                var retryPreset = ParseQualityPreset(failedJob.Quality);

                var paths = CreateTranscodePaths(failedJob.SourcePath);
                var outputPath = paths.FinalPath;

                var job = CreateJob("Transcode", failedJob.ItemId, failedJob.ItemName, outputPath, failedJob.SourcePath, duration);
                job.WorkPath = paths.WorkPath;
                job.Codec = codec == "av1" ? "AV1" : "H.265";
                job.Resolution = maxHeight == 2160 ? "4K" : "1080p";
                job.UseQuality = retryUseQuality;
                job.Quality = retryUseQuality ? retryPreset.ToString().ToLowerInvariant() : null;
                job.BitrateKbps = retryUseQuality ? null : bitrateKbps;
                job.FrameInterpolation = interpolate;
                StartJob(job, BuildTranscodePlan(probe, failedJob.SourcePath, paths.WorkPath, codec, bitrateKbps, maxHeight, interpolate, retryUseQuality, retryPreset));
                return Accepted(job);
            }

            if (string.Equals(failedJob.Kind, "Cut", StringComparison.OrdinalIgnoreCase))
            {
                var startSeconds = failedJob.CutStartSeconds;
                var endSeconds = failedJob.CutEndSeconds;
                if (duration <= 0 || startSeconds + endSeconds >= duration)
                {
                    return BadRequest("The original cut range is no longer valid for this video.");
                }

                var keepSeconds = duration - startSeconds - endSeconds;
                var paths = CreateCutPaths(failedJob.SourcePath);

                var job = CreateJob("Cut", failedJob.ItemId, failedJob.ItemName, paths.FinalPath, failedJob.SourcePath, duration);
                job.WorkPath = paths.WorkPath;
                job.CutStartSeconds = startSeconds;
                job.CutEndSeconds = endSeconds;
                job.CutRotation = failedJob.CutRotation;
                job.CutCropLeftPercent = failedJob.CutCropLeftPercent;
                job.CutCropRightPercent = failedJob.CutCropRightPercent;
                job.SourceWidth = failedJob.SourceWidth;
                job.SourceHeight = failedJob.SourceHeight;

                // The original encoder settings are not persisted on the job; re-planning from the
                // stored cut defaults keeps a retried crop consistent with new crops.
                if (HasCrop(failedJob.CutCropLeftPercent, failedJob.CutCropRightPercent))
                {
                    var cutDefaults = LoadDefaults().Cut;
                    job.Codec = string.Equals(cutDefaults.Codec, "av1", StringComparison.OrdinalIgnoreCase) ? "AV1" : "H.265";
                    StartJob(
                        job,
                        BuildCutPlan(
                            job,
                            probe,
                            cutDefaults.Codec,
                            !string.Equals(cutDefaults.RateControl, "bitrate", StringComparison.OrdinalIgnoreCase),
                            ParseQualityPreset(cutDefaults.Quality),
                            ParseEncoderPreference(cutDefaults.Encoder)));
                }
                else
                {
                    StartJob(job, BuildCutPlan(job, probe));
                }

                return Accepted(job);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retry video tool job {JobId}", jobId);
            return BadRequest(ex.Message);
        }

        return BadRequest("Unsupported job kind.");
    }

    /// <summary>
    /// Cancels a queued or running job. Finished jobs cannot be canceled.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <returns>The updated job.</returns>
    [HttpPost("Jobs/{jobId}/Cancel")]
    public ActionResult<VideoToolJobDto> CancelJob([FromRoute] Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return NotFound();
        }

        if (job.Status is "Completed" or "Failed" or "Canceled")
        {
            return BadRequest("Job is already finished.");
        }

        job.CancelRequested = true;

        if (string.Equals(job.Status, "Queued", StringComparison.OrdinalIgnoreCase))
        {
            MarkCanceled(job);
            UpdateQueuePositions();
            return Ok(job);
        }

        if (_processes.TryGetValue(jobId, out var process))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill process for video tool job {JobId}", jobId);
            }
        }

        job.Status = "Canceled";
        job.QueuePosition = null;
        job.CompletedAt = DateTimeOffset.UtcNow;
        UpdateQueuePositions();
        return Accepted(job);
    }

    /// <summary>
    /// Gets the current on-disk name of an item, so the client can prefill the rename dialog.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The current file or folder name.</returns>
    [HttpGet("{itemId}/RenameInfo")]
    public ActionResult<ItemRenameInfoDto> GetRenameInfo([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var path = item.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Item has no local path.");
        }

        var isFolder = Directory.Exists(path);
        if (!isFolder && !System.IO.File.Exists(path))
        {
            return BadRequest("The item file no longer exists.");
        }

        return Ok(new ItemRenameInfoDto
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
            IsFolder = isFolder,
            Path = path
        });
    }

    /// <summary>
    /// Renames the file or folder behind an item on disk.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="newName">The new name, including the file extension for files.</param>
    /// <param name="onConflict">What to do when the target already exists: ask, keepBoth or overwrite.</param>
    /// <returns>The rename result; <c>Conflict</c> is true when the client has to ask the user.</returns>
    [HttpPost("{itemId}/Rename")]
    public ActionResult<ItemRenameResultDto> RenameItem(
        [FromRoute] Guid itemId,
        [FromQuery] string newName,
        [FromQuery] string onConflict = "ask")
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var typeName = item.GetType().Name;
        if (typeName is "CollectionFolder" or "UserView" or "AggregateFolder")
        {
            return BadRequest("Library folders cannot be renamed here.");
        }

        var path = item.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Item has no local path.");
        }

        var isFolder = Directory.Exists(path);
        if (!isFolder && !System.IO.File.Exists(path))
        {
            return BadRequest("The item file no longer exists.");
        }

        newName = (newName ?? string.Empty).Trim();
        if (!IsValidName(newName))
        {
            return BadRequest("Invalid name.");
        }

        onConflict = string.IsNullOrWhiteSpace(onConflict) ? "ask" : onConflict.Trim().ToLowerInvariant();
        if (onConflict is not ("ask" or "keepboth" or "overwrite"))
        {
            return BadRequest("onConflict must be 'ask', 'keepBoth' or 'overwrite'.");
        }

        var directory = Path.GetDirectoryName(path)!;
        var proposed = newName;
        if (!isFolder && Path.GetExtension(proposed).Length == 0)
        {
            // Keep the original extension when the user only typed a new base name.
            proposed += Path.GetExtension(path);
        }

        lock (_renameLock)
        {
            var target = Path.Combine(directory, proposed);
            if (string.Equals(target, path, StringComparison.Ordinal))
            {
                return Ok(new ItemRenameResultDto { Success = true, NewName = proposed, NewPath = path });
            }

            var overwritten = false;
            if (PathExists(target))
            {
                if (onConflict == "keepboth")
                {
                    target = GetFreePath(target);
                }
                else if (onConflict == "overwrite")
                {
                    DeletePath(target);
                    overwritten = true;
                }
                else
                {
                    return Ok(new ItemRenameResultDto { Success = false, Conflict = true, NewName = proposed });
                }
            }

            try
            {
                if (isFolder)
                {
                    Directory.Move(path, target);
                }
                else
                {
                    System.IO.File.Move(path, target);
                }

                _libraryMonitor.ReportFileSystemChanged(directory);
                _logger.LogInformation("Renamed {OldPath} to {NewPath}", path, target);

                return Ok(new ItemRenameResultDto
                {
                    Success = true,
                    Overwritten = overwritten,
                    NewName = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar)),
                    NewPath = target
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rename {Path}", path);
                return BadRequest(ex.Message);
            }
        }
    }

    private static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255)
        {
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        return name is not ("CON" or "PRN" or "AUX" or "NUL");
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// Streams the completed output file for a job.
    /// </summary>
    /// <param name="jobId">The job id.</param>
    /// <returns>The output file.</returns>
    [HttpGet("Jobs/{jobId}/Stream")]
    public ActionResult StreamJob([FromRoute] Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)
            || !string.Equals(job.Status, "Completed", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(job.OutputPath)
            || !System.IO.File.Exists(job.OutputPath))
        {
            return NotFound();
        }

        var contentType = Path.GetExtension(job.OutputPath).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream"
        };

        return new PhysicalFileResult(job.OutputPath, contentType)
        {
            EnableRangeProcessing = true
        };
    }

    /// <summary>
    /// Renders a single video frame as a JPEG, used by the cut dialog to show the first and
    /// last kept frame while the trim values are edited.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="timeSeconds">Position in the source video, in seconds.</param>
    /// <param name="maxWidth">Maximum preview width in pixels.</param>
    /// <param name="rotation">Counter-clockwise rotation to bake into the preview (0, 90, 180 or 270).</param>
    /// <param name="cropLeft">Percent of the width removed from the left edge for the preview.</param>
    /// <param name="cropRight">Percent of the width removed from the right edge for the preview.</param>
    /// <returns>The JPEG frame.</returns>
    [HttpGet("{itemId}/Frame")]
    public async Task<ActionResult> GetFrame(
        [FromRoute] Guid itemId,
        [FromQuery] double timeSeconds = 0,
        [FromQuery] int maxWidth = 480,
        [FromQuery] int rotation = 0,
        [FromQuery] double cropLeft = 0,
        [FromQuery] double cropRight = 0)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var sourcePath = item.Path;
        if (string.IsNullOrWhiteSpace(sourcePath) || !System.IO.File.Exists(sourcePath))
        {
            return BadRequest("Item has no local video file.");
        }

        if (!double.IsFinite(timeSeconds) || timeSeconds < 0)
        {
            return BadRequest("timeSeconds must be zero or positive.");
        }

        if (rotation != 0 && rotation != 90 && rotation != 180 && rotation != 270)
        {
            return BadRequest("Rotation must be 0, 90, 180 or 270 degrees.");
        }

        maxWidth = Math.Clamp(maxWidth, 80, 1280);

        var duration = TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds;
        if (duration > 0)
        {
            // Stay just inside the last frame instead of asking for a position ffmpeg cannot render.
            timeSeconds = Math.Min(timeSeconds, Math.Max(0, duration - 0.3));
        }

        if (!Directory.Exists(FrameCacheDirectory))
        {
            Directory.CreateDirectory(FrameCacheDirectory);
        }

        var modified = System.IO.File.GetLastWriteTimeUtc(sourcePath).Ticks;
        var cacheName = string.Format(
            CultureInfo.InvariantCulture,
            "{0:N}_{1}_{2}_{3}_{4}_{5}_{6}.jpg",
            itemId,
            modified,
            maxWidth,
            rotation,
            timeSeconds.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', 'p'),
            cropLeft.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', 'p'),
            cropRight.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', 'p'));
        var cachePath = Path.Combine(FrameCacheDirectory, cacheName);

        if (!System.IO.File.Exists(cachePath))
        {
            await _frameLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!System.IO.File.Exists(cachePath))
                {
                    var width = ComputePreviewWidth(sourcePath, maxWidth, rotation, cropLeft, cropRight);
                    var error = await RenderFrameAsync(sourcePath, timeSeconds, width, rotation, cachePath, cropLeft, cropRight).ConfigureAwait(false);
                    if (error is not null && timeSeconds > 0)
                    {
                        // The reported duration can be slightly longer than the last decodable frame;
                        // step back a little before giving up.
                        foreach (var backoff in BackoffSeconds)
                        {
                            var retryTime = Math.Max(0, timeSeconds - backoff);
                            error = await RenderFrameAsync(sourcePath, retryTime, width, rotation, cachePath, cropLeft, cropRight).ConfigureAwait(false);
                            if (error is null)
                            {
                                break;
                            }
                        }
                    }

                    if (error is not null)
                    {
                        _logger.LogWarning("Frame preview failed for {Path}: {Error}", sourcePath, error);
                        return BadRequest("Could not render the frame.");
                    }

                    PruneFrameCache();
                }
            }
            finally
            {
                _frameLock.Release();
            }
        }

        Response.Headers["Cache-Control"] = "private, max-age=600";
        return PhysicalFile(cachePath, "image/jpeg");
    }

    /// <summary>
    /// Picks the preview width, taking the rotation into account so a portrait preview is not upscaled.
    /// </summary>
    private int ComputePreviewWidth(string sourcePath, int maxWidth, int rotation, double cropLeft = 0, double cropRight = 0)
    {
        var probe = ProbeSource(
            sourcePath,
            string.IsNullOrWhiteSpace(_mediaEncoder.ProbePath) ? "ffprobe" : _mediaEncoder.ProbePath);

        var width = rotation is 90 or 270 ? probe.Height : probe.Width;
        if (width > 0 && rotation is not (90 or 270))
        {
            // A cropped preview is narrower, so its natural width is the kept width.
            width = CropKeptWidth(width, cropLeft, cropRight);
        }

        if (width <= 0)
        {
            return maxWidth;
        }

        var target = Math.Min(maxWidth, width);
        return target < 2 ? 2 : target & ~1;
    }

    /// <summary>
    /// Runs ffmpeg to write one frame at the given position as a JPEG.
    /// </summary>
    /// <returns><c>null</c> on success, otherwise the last ffmpeg error output.</returns>
    private async Task<string?> RenderFrameAsync(
        string sourcePath,
        double timeSeconds,
        int width,
        int rotation,
        string outputPath,
        double cropLeft = 0,
        double cropRight = 0)
    {
        var encoderPath = string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath) ? "ffmpeg" : _mediaEncoder.EncoderPath;
        var filters = new List<string>();
        switch (rotation)
        {
            case 90:
                filters.Add("transpose=2");
                break;
            case 180:
                filters.Add("transpose=2");
                filters.Add("transpose=2");
                break;
            case 270:
                filters.Add("transpose=1");
                break;
        }

        if (HasCrop(cropLeft, cropRight))
        {
            // Crop before the scale so the preview shows exactly what the cut job will keep.
            filters.Add(BuildCropFilterExpression(cropLeft, cropRight, -1));
        }

        filters.Add("scale=" + width.ToString(CultureInfo.InvariantCulture) + ":-2");

        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-ss", timeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", sourcePath,
            "-frames:v", "1",
            "-an",
            "-sn",
            "-dn",
            "-vf", string.Join(',', filters),
            "-q:v", "4",
            "-update", "1",
            "-f", "image2",
            outputPath
        };

        var startInfo = new ProcessStartInfo(encoderPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return "ffmpeg could not be started.";
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // Ignore: the process is already gone.
            }

            return "ffmpeg timed out.";
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !System.IO.File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            try
            {
                System.IO.File.Delete(outputPath);
            }
            catch
            {
                // Ignore cleanup failures.
            }

            return string.IsNullOrWhiteSpace(stderr) ? "ffmpeg exited with code " + process.ExitCode : stderr.Trim();
        }

        return null;
    }

    /// <summary>
    /// Deletes the oldest cached preview frames once the cache grows past a few hundred files.
    /// </summary>
    private static void PruneFrameCache()
    {
        try
        {
            var files = new DirectoryInfo(FrameCacheDirectory).GetFiles("*.jpg");
            if (files.Length <= 400)
            {
                return;
            }

            foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(200))
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // Ignore files that are still being served.
                }
            }
        }
        catch
        {
            // Cache pruning is best effort only.
        }
    }

    /// <summary>
    /// Parses a quality preset name, defaulting to <see cref="VideoQualityPresets.medium"/>.
    /// </summary>
    /// <param name="value">The preset name.</param>
    /// <returns>The parsed preset.</returns>
    private static VideoQualityPresets ParseQualityPreset(string? value)
        => Enum.TryParse<VideoQualityPresets>(value, true, out var preset) ? preset : VideoQualityPresets.medium;

    /// <summary>
    /// Parses an encoder preference name, defaulting to <see cref="VideoEncoderPreference.auto"/>.
    /// </summary>
    /// <param name="value">The encoder name.</param>
    /// <returns>The parsed preference.</returns>
    private static VideoEncoderPreference ParseEncoderPreference(string? value)
        => Enum.TryParse<VideoEncoderPreference>(value, true, out var preference) ? preference : VideoEncoderPreference.auto;

    private static bool TryParseTranscodeParams(
        string codec,
        int bitrateKbps,
        string resolution,
        out string normalizedCodec,
        out int maxHeight,
        out string? error,
        bool useQuality = false)
    {
        normalizedCodec = codec.Trim().ToLowerInvariant();
        maxHeight = 0;
        error = null;

        if (normalizedCodec is not ("av1" or "h265" or "hevc"))
        {
            error = "codec must be 'av1' or 'h265'.";
            return false;
        }

        if (!useQuality && (bitrateKbps < 100 || bitrateKbps > 200000))
        {
            error = "bitrateKbps must be between 100 and 200000.";
            return false;
        }

        maxHeight = resolution.Trim().ToLowerInvariant() switch
        {
            "1080p" => 1080,
            "4k" or "2160p" => 2160,
            _ => 0
        };

        if (maxHeight == 0)
        {
            error = "resolution must be '1080p' or '4k'.";
            return false;
        }

        return true;
    }

    private VideoToolJobDto? EnqueueTranscode(
        BaseItem item,
        string codec,
        int bitrateKbps,
        int maxHeight,
        bool interpolate,
        bool useQuality = true,
        VideoQualityPresets qualityPreset = VideoQualityPresets.medium,
        VideoEncoderPreference preferredEncoder = VideoEncoderPreference.auto)
    {
        var sourcePath = item.Path;
        if (string.IsNullOrWhiteSpace(sourcePath) || !System.IO.File.Exists(sourcePath))
        {
            return null;
        }

        var probe = ProbeSource(
            sourcePath,
            string.IsNullOrWhiteSpace(_mediaEncoder.ProbePath) ? "ffprobe" : _mediaEncoder.ProbePath);
        var duration = probe.Duration > 0
            ? probe.Duration
            : TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds;

        var paths = CreateTranscodePaths(sourcePath);

        var job = CreateJob("Transcode", item.Id, item.Name, paths.FinalPath, sourcePath, duration);
        job.WorkPath = paths.WorkPath;
        job.Codec = codec == "av1" ? "AV1" : "H.265";
        job.Resolution = maxHeight == 2160 ? "4K" : "1080p";

        // Quality mode is what the user asked for; a bitrate column value would be a lie because
        // the encoder picks the rate itself. Bitrate mode keeps the requested figure.
        job.UseQuality = useQuality;
        job.Quality = useQuality ? qualityPreset.ToString().ToLowerInvariant() : null;
        job.BitrateKbps = useQuality ? null : bitrateKbps;
        job.FrameInterpolation = interpolate;
        StartJob(job, BuildTranscodePlan(probe, sourcePath, paths.WorkPath, codec, bitrateKbps, maxHeight, interpolate, useQuality, qualityPreset, preferredEncoder));
        return job;
    }

    private static VideoToolJobDto CreateJob(string kind, Guid itemId, string itemName, string outputPath, string sourcePath, double duration)
    {
        var job = new VideoToolJobDto
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            ItemId = itemId,
            ItemName = itemName,
            OutputPath = outputPath,
            Status = "Queued",
            CreatedAt = DateTimeOffset.UtcNow,
            SourceDurationSeconds = duration
        };
        job.SourcePath = sourcePath;
        return job;
    }

    /// <summary>
    /// Builds the ffmpeg run(s) for a cut job. A pure trim is a single stream copy; a crop changes the
    /// frame size and cannot be stream-copied, so it is re-encoded with the same hardware/software
    /// fallback chain a transcode uses (hardware decode fails on the crop filter, which is expected).
    /// </summary>
    /// <param name="job">The cut job.</param>
    /// <param name="probe">The probed source, used to size the crop and the re-encode.</param>
    /// <param name="codec">Target codec for the re-encode: h265 or av1.</param>
    /// <param name="useQuality">Whether to encode to a quality level instead of a bitrate.</param>
    /// <param name="qualityPreset">The quality level used when <paramref name="useQuality"/> is set.</param>
    /// <param name="preferredEncoder">The encoder to try first, or <see cref="VideoEncoderPreference.auto"/>.</param>
    /// <returns>The attempts to run in order.</returns>
    private IReadOnlyList<TranscodePlanner.Attempt> BuildCutPlan(
        VideoToolJobDto job,
        MediaProbe probe,
        string codec = "h265",
        bool useQuality = true,
        VideoQualityPresets qualityPreset = VideoQualityPresets.high,
        VideoEncoderPreference preferredEncoder = VideoEncoderPreference.auto)
    {
        var keepSeconds = job.SourceDurationSeconds - job.CutStartSeconds - job.CutEndSeconds;
        if (!HasCrop(job.CutCropLeftPercent, job.CutCropRightPercent))
        {
            return new List<TranscodePlanner.Attempt>
            {
                new(BuildCutArgs(job.SourcePath, job.WorkPath, job.CutStartSeconds, keepSeconds, job.CutRotation), "copy", false, false, "Stream copy")
            };
        }

        var ffmpegPath = string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath) ? "ffmpeg" : _mediaEncoder.EncoderPath;
        var cascade = TranscodePlanner.Build(new TranscodePlanner.Request
        {
            // Same encoder cascade as the default transcode, but the format, bitrate and resolution
            // are kept as close to the source as the cascade allows rather than forcing a 1080p H.265.
            SourcePath = job.SourcePath,
            OutputPath = job.WorkPath,
            Codec = codec,
            BitrateKbps = Math.Max(3000, (int)Math.Round(EstimateReencodeBitrate(probe, job))),

            // Cropping re-encodes the picture, so quality mode keeps the crop visually identical to
            // the source instead of squeezing it into a guessed bitrate. Bitrate mode is still
            // available on the transcode dialog for people who want a fixed size.
            UseQuality = useQuality,
            QualityPreset = qualityPreset,
            PreferredEncoder = preferredEncoder,
            // Leave headroom to the encoder's bitrate ceiling: hardware encoders round the frame
            // size up to macroblock multiples, and that small overshoot must not trigger a scale.
            MaxHeight = probe.Height > 0 ? probe.Height * 2 : 2160,
            SourceVideoCodec = probe.VideoCodec,
            Width = probe.Width,
            Height = probe.Height,
            FrameRate = probe.FrameRate,
            NominalFrameRate = probe.NominalFrameRate,
            SubtitleIndexes = probe.SubtitleIndexes ?? Array.Empty<int>(),
            FfmpegPath = ffmpegPath,
            Interpolate = false,
            Duration = keepSeconds,
            Trim = new TranscodePlanner.TrimRequest(job.CutStartSeconds, keepSeconds, job.CutRotation),
            VideoFilter = BuildCropFilter(job)
        });

        // Video must be re-encoded, so only the audio/other streams are copied; swapping the whole
        // stream map for -map 0 -c copy would silently defeat the crop filter.
        var attempts = new List<TranscodePlanner.Attempt>(cascade.Count);
        foreach (var attempt in cascade)
        {
            attempts.Add(EnsureAudioCopyAttempt(attempt));
        }

        _logger.LogInformation(
            "Video tool crop plan for {SourcePath}: crop {Left}% left / {Right}% right, {Encoders}",
            job.SourcePath,
            job.CutCropLeftPercent,
            job.CutCropRightPercent,
            string.Join(" -> ", cascade.Select(attempt => attempt.Encoder)));

        return attempts;
    }

    /// <summary>
    /// Rewrites a transcode attempt so its audio tracks are copied instead of re-encoded, keeping
    /// the original audio quality when only the video is re-encoded for a crop.
    /// </summary>
    /// <param name="attempt">The planned transcode attempt.</param>
    /// <returns>The attempt with copied audio.</returns>
    private static TranscodePlanner.Attempt EnsureAudioCopyAttempt(TranscodePlanner.Attempt attempt)
        => attempt with { Args = EnsureAudioCopy(attempt.Args) };

    /// <summary>
    /// Adds an explicit audio/picture copy to a transcode attempt so the re-encoded video keeps the
    /// original audio tracks unchanged.
    /// </summary>
    /// <param name="args">The transcode argument list.</param>
    /// <returns>The argument list with an audio copy.</returns>
    private static List<string> EnsureAudioCopy(List<string> args)
    {
        List<string> result = new(args.Count + 4);
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];

            // The planner appends "-c:a aac -b:a 192k" after the video encoder, which overrides any
            // earlier audio setting. Drop that pair and put a copy in its place, otherwise the
            // audio is re-encoded (and a copy followed by aac is simply ambiguous to ffmpeg).
            if (arg == "-c:a" && index + 1 < args.Count)
            {
                index += 1;
                if (index + 3 < args.Count && args[index + 1] == "-b:a")
                {
                    index += 3;
                }

                if (!result.Contains("-c:a"))
                {
                    result.Add("-c:a");
                    result.Add("copy");
                }

                continue;
            }

            result.Add(arg);
        }

        return result;
    }

    /// <summary>
    /// Builds the crop filter applied to the kept part of the video.
    /// </summary>
    /// <param name="job">The cut job.</param>
    /// <returns>The filter expression.</returns>
    private static string BuildCropFilter(VideoToolJobDto job)
    {
        var width = job.SourceWidth > 0 ? job.SourceWidth : 1920;
        return BuildCropFilterExpression(job.CutCropLeftPercent, job.CutCropRightPercent, width);
    }

    /// <summary>
    /// Builds a crop filter from the percentages removed on each side.
    /// </summary>
    /// <param name="leftPercent">Percent removed from the left edge.</param>
    /// <param name="rightPercent">Percent removed from the right edge.</param>
    /// <param name="sourceWidth">Source width in pixels, or a non-positive value to let ffmpeg read it.</param>
    /// <returns>The filter expression.</returns>
    private static string BuildCropFilterExpression(double leftPercent, double rightPercent, int sourceWidth)
    {
        leftPercent = Math.Max(0, leftPercent);
        rightPercent = Math.Max(0, rightPercent);

        if (sourceWidth > 0)
        {
            var leftPx = (int)Math.Round(sourceWidth * leftPercent / 100d, MidpointRounding.AwayFromZero);
            var keepWidth = CropKeptWidth(sourceWidth, leftPercent, rightPercent);
            return string.Format(
                CultureInfo.InvariantCulture,
                "crop={0}:ih:{1}:0",
                keepWidth,
                leftPx);
        }

        // Unknown width: express everything relative to the decoded frame size inside ffmpeg.
        return string.Format(
            CultureInfo.InvariantCulture,
            "crop=trunc(iw-(iw*{0}/100)-(iw*{1}/100)):ih:trunc(iw*{0}/100):0",
            leftPercent.ToString("0.######", CultureInfo.InvariantCulture),
            rightPercent.ToString("0.######", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Computes the width kept after cropping, rounded down to an even number because yuv420p
    /// and the common encoders require it.
    /// </summary>
    private static int CropKeptWidth(int sourceWidth, double leftPercent, double rightPercent)
    {
        if (sourceWidth <= 0)
        {
            return sourceWidth;
        }

        var leftPx = (int)Math.Round(sourceWidth * Math.Max(0, leftPercent) / 100d, MidpointRounding.AwayFromZero);
        var rightPx = (int)Math.Round(sourceWidth * Math.Max(0, rightPercent) / 100d, MidpointRounding.AwayFromZero);
        var keepWidth = sourceWidth - leftPx - rightPx;
        keepWidth = Math.Max(2, keepWidth);
        return keepWidth % 2 == 0 ? keepWidth : keepWidth - 1;
    }

    /// <summary>
    /// Validates and clamps the crop percentages. A value of zero or below means no crop.
    /// </summary>
    /// <param name="left">The requested left crop percent.</param>
    /// <param name="right">The requested right crop percent.</param>
    /// <param name="normalizedLeft">The clamped left crop percent.</param>
    /// <param name="normalizedRight">The clamped right crop percent.</param>
    /// <param name="error">The validation error, if any.</param>
    /// <returns><c>true</c> when the crop values are usable.</returns>
    private static bool TryNormalizeCrop(double left, double right, out double normalizedLeft, out double normalizedRight, out string error)
    {
        normalizedLeft = 0;
        normalizedRight = 0;
        error = string.Empty;

        if (!double.IsFinite(left) || !double.IsFinite(right))
        {
            error = "Crop values must be finite numbers.";
            return false;
        }

        left = Math.Max(0, left);
        right = Math.Max(0, right);
        if (left >= 100 || right >= 100 || left + right >= 100)
        {
            error = "The crop cannot remove the whole width.";
            return false;
        }

        normalizedLeft = left;
        normalizedRight = right;
        return true;
    }

    private static bool HasCrop(double leftPercent, double rightPercent)
        => leftPercent > 0 || rightPercent > 0;

    private static bool HasVideoDimensions(int width, int height)
        => width > 0 && height > 0;

    /// <summary>
    /// Estimates a re-encode bitrate that keeps the source's bitrate per pixel so a cropped
    /// re-encode does not visibly degrade.
    /// </summary>
    /// <param name="probe">The probed source.</param>
    /// <param name="job">The cut job.</param>
    /// <returns>The target bitrate in kbps.</returns>
    private static double EstimateReencodeBitrate(MediaProbe probe, VideoToolJobDto job)
    {
        var keptRatio = Math.Max(0.05, 1 - (job.CutCropLeftPercent + job.CutCropRightPercent) / 100d);
        var sourceBitrate = probe.VideoBitrateKbps > 0 ? probe.VideoBitrateKbps : 8000;
        return sourceBitrate * keptRatio;
    }

    private static List<string> BuildCutArgs(string sourcePath, string outputPath, double startSeconds, double keepSeconds, int rotation = 0)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture)
        };

        // -display_rotation is an input option: it rewrites the display matrix, so the
        // stream can still be copied bit-for-bit instead of being re-encoded.
        // The value is a counter-clockwise angle, which is what the UI offers, so it is
        // passed through unchanged (ffmpeg's convention matches the label for CCW).
        if (rotation != 0)
        {
            args.Add("-display_rotation");
            args.Add(rotation.ToString(CultureInfo.InvariantCulture));
        }

        args.AddRange(
        [
            "-i", sourcePath,
            "-t", keepSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-map", "0",
            "-c", "copy",
            "-map_metadata", "0",
            outputPath
        ]);

        return args;
    }

    /// <summary>
    /// Builds the ordered ffmpeg attempts for a transcode: hardware encoders first, software last.
    /// </summary>
    /// <param name="probe">The probed source.</param>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="workPath">The temporary output path.</param>
    /// <param name="codec">The target codec.</param>
    /// <param name="bitrateKbps">The target bitrate.</param>
    /// <param name="maxHeight">The maximum output height.</param>
    /// <param name="interpolate">Whether to motion-interpolate a low frame rate source to 30 fps.</param>
    /// <param name="useQuality">Whether to encode to a quality level instead of a bitrate.</param>
    /// <param name="qualityPreset">The quality level used when <paramref name="useQuality"/> is set.</param>
    /// <param name="preferredEncoder">The encoder to try first, or <see cref="VideoEncoderPreference.auto"/>.</param>
    /// <returns>The attempts to run in order.</returns>
    private IReadOnlyList<TranscodePlanner.Attempt> BuildTranscodePlan(
        MediaProbe probe,
        string sourcePath,
        string workPath,
        string codec,
        int bitrateKbps,
        int maxHeight,
        bool interpolate = false,
        bool useQuality = true,
        VideoQualityPresets qualityPreset = VideoQualityPresets.medium,
        VideoEncoderPreference preferredEncoder = VideoEncoderPreference.auto)
    {
        var ffmpegPath = string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath) ? "ffmpeg" : _mediaEncoder.EncoderPath;
        var plan = TranscodePlanner.Build(new TranscodePlanner.Request
        {
            SourcePath = sourcePath,
            OutputPath = workPath,
            Codec = codec,
            BitrateKbps = bitrateKbps,
            UseQuality = useQuality,
            QualityPreset = qualityPreset,
            PreferredEncoder = preferredEncoder,
            MaxHeight = maxHeight,
            SourceVideoCodec = probe.VideoCodec,
            Width = probe.Width,
            Height = probe.Height,
            FrameRate = probe.FrameRate,
            NominalFrameRate = probe.NominalFrameRate,
            SubtitleIndexes = probe.SubtitleIndexes ?? Array.Empty<int>(),
            FfmpegPath = ffmpegPath,
            Interpolate = interpolate,
            Duration = probe.Duration > 0 ? probe.Duration : 0
        });

        _logger.LogInformation(
            "Video tool transcode plan for {SourcePath}: {Encoders}",
            sourcePath,
            string.Join(" -> ", plan.Select(attempt => attempt.Encoder)));

        return plan;
    }

    /// <summary>
    /// Reads an ffprobe frame rate value such as "30000/1001".
    /// </summary>
    /// <param name="stream">The stream element.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The rate in frames per second, or 0 when it is missing.</returns>
    private static double ParseFrameRate(JsonElement stream, string propertyName)
    {
        var value = GetString(stream, propertyName);
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var separator = value.IndexOf('/', StringComparison.Ordinal);
        if (separator > 0
            && double.TryParse(value[..separator], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(value[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator > 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static MediaProbe ProbeSource(string sourcePath, string probePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(probePath)
            {
                ArgumentList =
                {
                    "-v", "error",
                    "-print_format", "json",
                    "-show_streams",
                    "-show_format",
                    sourcePath
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return default;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return default;
            }

            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var duration = 0d;
            if (root.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var durationElement)
                && double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration))
            {
                duration = parsedDuration;
            }

            var width = 0;
            var height = 0;
            var frameRate = 0d;
            var nominalFrameRate = 0d;
            var videoCodec = string.Empty;
            var videoBitrate = 0d;
            var subtitleIndexes = new List<int>();
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = GetString(stream, "codec_type");
                    if (codecType == "video" && width == 0)
                    {
                        if (stream.TryGetProperty("disposition", out var disposition)
                            && disposition.TryGetProperty("attached_pic", out var attachedPic)
                            && attachedPic.TryGetInt32(out var isAttachedPic)
                            && isAttachedPic == 1)
                        {
                            continue;
                        }

                        if (stream.TryGetProperty("width", out var widthElement)
                            && widthElement.TryGetInt32(out var parsedWidth))
                        {
                            width = parsedWidth;
                        }

                        if (stream.TryGetProperty("height", out var heightElement)
                            && heightElement.TryGetInt32(out var parsedHeight))
                        {
                            height = parsedHeight;
                        }

                        frameRate = ParseFrameRate(stream, "avg_frame_rate");
                        nominalFrameRate = ParseFrameRate(stream, "r_frame_rate");
                        videoCodec = GetString(stream, "codec_name") ?? string.Empty;
                        if (stream.TryGetProperty("bit_rate", out var bitRateElement)
                            && double.TryParse(bitRateElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedBitRate)
                            && parsedBitRate > 0)
                        {
                            // ffprobe reports bits per second; the planner works in kbps.
                            videoBitrate = parsedBitRate / 1000d;
                        }
                    }
                    else if (codecType == "subtitle")
                    {
                        var codecName = GetString(stream, "codec_name");
                        if (codecName is "subrip" or "ass" or "ssa" or "mov_text" or "webvtt" or "text"
                            && stream.TryGetProperty("index", out var indexElement)
                            && indexElement.TryGetInt32(out var index))
                        {
                            subtitleIndexes.Add(index);
                        }
                    }
                }
            }

            // Container frame rates are frequently garbage (avg 0/0, or a bogus 1000/1 "tbr" on
            // WMV/ASF). Trust them only inside a sane range; otherwise infer the real rate from
            // packet timestamps so the encoder gets a usable -r (and interpolation a usable source rate).
            if (!IsPlausibleFrameRate(frameRate))
            {
                frameRate = 0;
            }

            if (!IsPlausibleFrameRate(nominalFrameRate))
            {
                nominalFrameRate = 0;
            }

            if (frameRate <= 0 && nominalFrameRate > 0)
            {
                // TranscodePlanner prefers the measured rate; fall back to the nominal one.
                frameRate = nominalFrameRate;
                nominalFrameRate = 0;
            }

            if (frameRate <= 0)
            {
                frameRate = InferFrameRateFromPackets(sourcePath, probePath);
            }

            return new MediaProbe(duration, width, height, subtitleIndexes, frameRate, nominalFrameRate, videoCodec, videoBitrate);
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>
    /// Frame rates outside this range are treated as broken container metadata instead of truth.
    /// </summary>
    private static bool IsPlausibleFrameRate(double fps)
        => fps >= 1 && fps <= 240;

    /// <summary>
    /// Estimates the real frame rate from the PTS of the first video packets. Used when the
    /// container reports no usable frame rate (e.g. WMV files advertising 1000/1).
    /// </summary>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="probePath">The ffprobe binary.</param>
    /// <returns>The estimated frames per second, or 0 when it cannot be determined.</returns>
    private static double InferFrameRateFromPackets(string sourcePath, string probePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo(probePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-select_streams");
            startInfo.ArgumentList.Add("v:0");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("packet=pts_time");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("csv=p=0");
            startInfo.ArgumentList.Add("-read_intervals");
            startInfo.ArgumentList.Add("%+#120");
            startInfo.ArgumentList.Add(sourcePath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return 0;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);

            var deltas = new List<double>();
            double last = 0;
            var haveLast = false;
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0
                    || !double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)
                    || double.IsNaN(pts))
                {
                    continue;
                }

                if (haveLast && pts - last > 0.0005)
                {
                    deltas.Add(pts - last);
                }

                last = pts;
                haveLast = true;
            }

            if (deltas.Count < 10)
            {
                return 0;
            }

            deltas.Sort();
            var median = deltas[deltas.Count / 2];
            if (median <= 0)
            {
                return 0;
            }

            var fps = 1.0 / median;
            return IsPlausibleFrameRate(fps) ? Math.Round(fps, 3) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>
    /// Builds the final and temporary paths for a transcode. The finished file takes over the source name
    /// with an mp4 container (no codec/container marker in the name), so the source itself is parked as
    /// "&lt;name&gt;.old&lt;ext&gt;" when the job succeeds.
    /// </summary>
    /// <returns>The final path and the temporary work path.</returns>
    /// <summary>
    /// Path of the queue defaults file.
    /// </summary>
    /// <returns>The file path.</returns>
    private string GetDefaultsFilePath()
        => Path.Combine(
            _serverConfigurationManager.ApplicationPaths.ConfigurationDirectoryPath,
            "videotools-defaults.json");

    /// <summary>
    /// Loads the queue defaults, falling back to built-in values when the file is missing or broken.
    /// </summary>
    /// <returns>The defaults.</returns>
    private VideoToolsDefaultsDto LoadDefaults()
    {
        try
        {
            var path = GetDefaultsFilePath();
            if (System.IO.File.Exists(path))
            {
                var json = System.IO.File.ReadAllText(path);
                var defaults = JsonSerializer.Deserialize<VideoToolsDefaultsDto>(
                    json,
                    DefaultsJsonOptions);

                if (defaults is not null)
                {
                    return NormalizeDefaults(defaults);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read video tool defaults, using built-in values");
        }

        return NormalizeDefaults(new VideoToolsDefaultsDto());
    }

    /// <summary>
    /// Writes the queue defaults next to the server configuration.
    /// </summary>
    /// <param name="defaults">The defaults to persist.</param>
    private void SaveDefaultsToDisk(VideoToolsDefaultsDto defaults)
    {
        var path = GetDefaultsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(defaults, DefaultsJsonOptions));
    }

    /// <summary>
    /// Clamps the defaults to the values the transcode and cut pipelines accept.
    /// </summary>
    /// <param name="defaults">The incoming defaults.</param>
    /// <returns>A normalized copy.</returns>
    private static VideoToolsDefaultsDto NormalizeDefaults(VideoToolsDefaultsDto defaults)
    {
        var transcode = defaults.Transcode ?? new VideoToolsTranscodeDefaults();
        var cut = defaults.Cut ?? new VideoToolsCutDefaults();

        transcode.Codec = string.Equals(transcode.Codec, "av1", StringComparison.OrdinalIgnoreCase) ? "av1" : "h265";
        transcode.Encoder = string.IsNullOrWhiteSpace(transcode.Encoder) ? "auto" : transcode.Encoder.Trim().ToLowerInvariant();
        transcode.Resolution = string.Equals(transcode.Resolution, "4k", StringComparison.OrdinalIgnoreCase)
            || string.Equals(transcode.Resolution, "2160p", StringComparison.OrdinalIgnoreCase)
                ? "4k"
                : "1080p";
        transcode.RateControl = string.Equals(transcode.RateControl, "bitrate", StringComparison.OrdinalIgnoreCase) ? "bitrate" : "quality";
        transcode.Quality = NormalizeQuality(transcode.Quality, "medium");
        transcode.BitrateKbps = Math.Clamp(transcode.BitrateKbps, 100, 200000);

        cut.Codec = string.Equals(cut.Codec, "av1", StringComparison.OrdinalIgnoreCase) ? "av1" : "h265";
        cut.Encoder = string.IsNullOrWhiteSpace(cut.Encoder) ? "auto" : cut.Encoder.Trim().ToLowerInvariant();
        cut.RateControl = string.Equals(cut.RateControl, "bitrate", StringComparison.OrdinalIgnoreCase) ? "bitrate" : "quality";
        cut.Quality = NormalizeQuality(cut.Quality, "high");
        cut.BitrateKbps = Math.Clamp(cut.BitrateKbps, 100, 200000);

        return new VideoToolsDefaultsDto { Transcode = transcode, Cut = cut };
    }

    /// <summary>
    /// Maps a quality name onto the supported presets.
    /// </summary>
    /// <param name="value">The requested quality.</param>
    /// <param name="fallback">The value used when the request is not recognized.</param>
    /// <returns>The quality name.</returns>
    private static string NormalizeQuality(string? value, string fallback)
    {
        var quality = value?.Trim().ToLowerInvariant();
        return quality is "highest" or "high" or "medium" or "low" or "lowest" ? quality : fallback;
    }

    private static (string FinalPath, string WorkPath) CreateTranscodePaths(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var candidate = Path.Combine(directory, baseName + ".mp4");

        // When the source is already an mp4 the new file simply replaces it, so the name is not "taken".
        var finalPath = string.Equals(candidate, sourcePath, StringComparison.Ordinal)
            ? candidate
            : GetUniquePath(candidate);

        var workPath = GetUniquePath(Path.Combine(directory, "." + baseName + ".videotools.mp4"));
        return (finalPath, workPath);
    }

    /// <summary>
    /// Builds the final and temporary paths for a cut. The new file takes over the source name (same container),
    /// so the original is parked as "&lt;name&gt;.old&lt;ext&gt;" when the job succeeds.
    /// </summary>
    /// <param name="sourcePath">The source file path.</param>
    /// <returns>The final path and the temporary work path.</returns>
    private static (string FinalPath, string WorkPath) CreateCutPaths(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);

        var finalPath = Path.Combine(directory, Path.GetFileName(sourcePath));
        var workPath = GetUniquePath(Path.Combine(directory, "." + baseName + ".videotools.cut" + extension));
        return (finalPath, workPath);
    }

    /// <summary>
    /// Builds the path that keeps the original file after a transcode, e.g. "clip.mp4" becomes
    /// "clip.old.mp4". A numbered marker is inserted when a backup already exists.
    /// </summary>
    /// <param name="sourcePath">The original file path.</param>
    /// <returns>The free backup path.</returns>
    private static string GetOldPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);

        var candidate = Path.Combine(directory, name + ".old" + extension);
        if (!PathExists(candidate))
        {
            return candidate;
        }

        for (var i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(
                directory,
                name + " (" + i.ToString(CultureInfo.InvariantCulture) + ").old" + extension);
            if (!PathExists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Could not find a free backup file name.");
    }

    private static string GetFreePath(string path)
    {
        if (!PathExists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, name + " (" + i.ToString(CultureInfo.InvariantCulture) + ")" + extension);
            if (!PathExists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Could not find a free file name.");
    }

    private static bool PathExists(string path)
        => System.IO.File.Exists(path) || Directory.Exists(path);

    private static string GetUniquePath(string path)
    {
        lock (_pathLock)
        {
            if (!System.IO.File.Exists(path) && !_reservedPaths.Contains(path))
            {
                _reservedPaths.Add(path);
                return path;
            }

            var directory = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            for (var i = 1; i < 1000; i++)
            {
                var candidate = Path.Combine(directory, name + "." + i.ToString(CultureInfo.InvariantCulture) + extension);
                if (!System.IO.File.Exists(candidate) && !_reservedPaths.Contains(candidate))
                {
                    _reservedPaths.Add(candidate);
                    return candidate;
                }
            }
        }

        throw new IOException("Could not find a free output file name.");
    }

    private void StartJob(VideoToolJobDto job, List<string> args)
    {
        // Cut jobs are a single stream copy; no encoder choice involved.
        StartJob(
            job,
            new List<TranscodePlanner.Attempt>
            {
                new(args, "copy", false, false, "Stream copy")
            });
    }

    private void StartJob(VideoToolJobDto job, IReadOnlyList<TranscodePlanner.Attempt> attempts)
    {
        _jobs[job.Id] = job;
        var encoderPath = string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath) ? "ffmpeg" : _mediaEncoder.EncoderPath;

        lock (_scanLock)
        {
            // A new job arrived; postpone the pending folder scan until the queue is idle again.
            _scanCts?.Cancel();
            _scanCts = null;
            _pending.Enqueue(new JobWork(job, attempts, encoderPath, _libraryMonitor, _libraryManager, _providerManager, _directoryService, _logger));
        }

        UpdateQueuePositions();
        _pendingSignal.Release();
        EnsureWorker();
    }

    private static void EnsureWorker()
    {
        lock (_workerLock)
        {
            if (_workerStarted)
            {
                return;
            }

            _workerStarted = true;
        }

        _ = Task.Run(ProcessQueue);
    }

    private static async Task ProcessQueue()
    {
        while (true)
        {
            await _pendingSignal.WaitAsync().ConfigureAwait(false);

            while (_pending.TryDequeue(out var work))
            {
                if (work.Job.CancelRequested || string.Equals(work.Job.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
                {
                    MarkCanceled(work.Job);
                    continue;
                }

                work.Job.QueuePosition = null;
                work.Job.Status = "Running";
                work.Job.StartedAt = DateTimeOffset.UtcNow;
                UpdateQueuePositions();

                try
                {
                    await RunFfmpegAsync(work).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (work.Job.CancelRequested)
                    {
                        MarkCanceled(work.Job);
                    }
                    else
                    {
                        work.Job.Status = "Failed";
                        work.Job.CompletedAt = DateTimeOffset.UtcNow;
                        work.Job.Error = ex.Message;
                        work.Logger.LogError(ex, "Video tool job {JobId} failed", work.Job.Id);
                    }
                }

                UpdateQueuePositions();
                ScheduleScanIfIdle(work.LibraryMonitor, work.Logger);
                RefreshPosterAfterCut(work);
            }
        }
    }

    /// <summary>
    /// A cut replaces the video in place, so the stored primary image still shows the untrimmed
    /// frame. Re-grab the poster from the new file, silently and without a library scan.
    /// Transcoded output keeps the same content and duration, so its poster is left alone.
    /// </summary>
    /// <param name="work">The finished job.</param>
    private static void RefreshPosterAfterCut(JobWork work)
    {
        var job = work.Job;
        if (!string.Equals(job.Kind, "Cut", StringComparison.Ordinal)
            || !string.Equals(job.Status, "Completed", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var item = work.LibraryManager.GetItemById(job.ItemId);
            if (item is null)
            {
                work.Logger.LogWarning("Video tool job {JobId}: item {ItemId} not found, skipping poster refresh", job.Id, job.ItemId);
                return;
            }

            var outputPath = job.OutputPath;
            if (string.IsNullOrWhiteSpace(outputPath) || !System.IO.File.Exists(outputPath))
            {
                return;
            }

            var keepSeconds = job.SourceDurationSeconds - job.CutStartSeconds - job.CutEndSeconds;
            if (!double.IsFinite(keepSeconds) || keepSeconds <= 0)
            {
                return;
            }

            // Same 10% offset the screen grabber provider uses, measured on the trimmed duration
            // so the frame comes from the new content rather than the old one.
            var grabSeconds = Math.Min(keepSeconds * 0.1, Math.Max(keepSeconds - 1, 0));
            var tempPath = Path.Combine(Path.GetTempPath(), "videotools-poster-" + job.Id.ToString("N") + ".jpg");

            var error = RenderStillFrame(work.EncoderPath, outputPath, grabSeconds, tempPath);
            if (error is not null)
            {
                work.Logger.LogWarning("Video tool job {JobId}: poster frame extraction failed: {Error}", job.Id, error);
                return;
            }

            // SaveImage consumes (and deletes) the extracted file.
            work.ProviderManager.SaveImage(item, tempPath, "image/jpeg", ImageType.Primary, null, null, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            work.Logger.LogInformation("Video tool job {JobId}: poster re-extracted from the trimmed video", job.Id);
        }
        catch (Exception ex)
        {
            // A stale poster is cosmetic; never turn it into a failed job or a crashed queue loop.
            work.Logger.LogError(ex, "Video tool job {JobId}: poster refresh failed", work.Job.Id);
        }
    }

    /// <summary>
    /// Grabs a single frame as JPEG. Returns null on success, otherwise the failure message.
    /// </summary>
    private static string? RenderStillFrame(string encoderPath, string sourcePath, double timeSeconds, string outputPath)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-ss", timeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", sourcePath,
            "-frames:v", "1",
            "-an",
            "-sn",
            "-dn",
            "-vf", "scale=720:-2",
            "-q:v", "4",
            "-update", "1",
            "-f", "image2",
            outputPath
        };

        var startInfo = new ProcessStartInfo(encoderPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return "ffmpeg could not be started.";
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best effort.
            }

            return "ffmpeg timed out.";
        }

        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !System.IO.File.Exists(outputPath))
        {
            return string.IsNullOrWhiteSpace(stderr) ? "ffmpeg failed." : stderr.Trim();
        }

        return null;
    }

    private static void AddFolderForScan(string outputPath)
    {
        var folder = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        lock (_scanLock)
        {
            _foldersToScan.Add(folder);
        }
    }

    private static void ScheduleScanIfIdle(ILibraryMonitor libraryMonitor, ILogger logger)
    {
        lock (_scanLock)
        {
            if (!_pending.IsEmpty || _foldersToScan.Count == 0)
            {
                return;
            }

            _scanCts?.Cancel();
            var cts = new CancellationTokenSource();
            _scanCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);

                    List<string> folders;
                    lock (_scanLock)
                    {
                        if (!ReferenceEquals(_scanCts, cts))
                        {
                            return;
                        }

                        folders = _foldersToScan.ToList();
                        _foldersToScan.Clear();
                        _scanCts = null;
                    }

                    foreach (var folder in folders)
                    {
                        try
                        {
                            libraryMonitor.ReportFileSystemChanged(folder);
                            logger.LogInformation("Scheduled folder scan after video queue finished: {Folder}", folder);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Failed to scan folder after video queue finished: {Folder}", folder);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // A new job was queued before the delay elapsed.
                }
                finally
                {
                    cts.Dispose();
                }
            });
        }
    }

    private static void UpdateQueuePositions()
    {
        var queuedIds = new HashSet<Guid>();
        var position = 1;
        foreach (var work in _pending)
        {
            if (!string.Equals(work.Job.Status, "Queued", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            work.Job.QueuePosition = position++;
            queuedIds.Add(work.Job.Id);
        }

        foreach (var job in _jobs.Values)
        {
            if (job.Status == "Queued" && !queuedIds.Contains(job.Id))
            {
                job.QueuePosition = null;
            }
        }
    }

    private static void MarkCanceled(VideoToolJobDto job)
    {
        job.Status = "Canceled";
        job.Progress = null;
        job.QueuePosition = null;
        job.CompletedAt = DateTimeOffset.UtcNow;

        var workPath = string.IsNullOrEmpty(job.WorkPath) ? job.OutputPath : job.WorkPath;

        lock (_pathLock)
        {
            _reservedPaths.Remove(workPath);
        }

        try
        {
            // Only ever delete the partial output. A previously finished file that happens to sit at
            // OutputPath must survive; that is why the work file is compared against it.
            var isPartialOutput = !string.Equals(workPath, job.OutputPath, StringComparison.Ordinal);
            if (isPartialOutput && System.IO.File.Exists(workPath))
            {
                System.IO.File.Delete(workPath);
            }
        }
        catch (Exception)
        {
            // The file may still be held by ffmpeg; leave it for the next cleanup attempt.
        }
    }

    private static async Task RunFfmpegAsync(JobWork work)
    {
        var job = work.Job;
        var errorLines = new LinkedList<string>();

        try
        {
            var exitCode = -1;

            // Walk the plan: GPU encoding first, then GPU decoding without the GPU encoder, then
            // software. A failure is only reported to the user if every option failed.
            for (var index = 0; index < work.Attempts.Count; index++)
            {
                if (job.CancelRequested)
                {
                    break;
                }

                var attempt = work.Attempts[index];
                if (index > 0)
                {
                    TryDeleteWorkFile(job);
                    job.Progress = 0;
                    lock (errorLines)
                    {
                        errorLines.Clear();
                    }
                }

                job.Encoder = attempt.Encoder;
                job.EncoderKind = attempt.IsHardware ? "Hardware" : "Software";

                if (attempt.ParallelPlan is { } plan)
                {
                    exitCode = await RunParallelInterpolationAsync(work, plan, job, errorLines).ConfigureAwait(false);
                }
                else
                {
                    exitCode = await RunProcessAsync(work, attempt.Args, job, errorLines).ConfigureAwait(false);
                }

                if (exitCode == 0 && System.IO.File.Exists(job.WorkPath))
                {
                    if (attempt.IsHardware)
                    {
                        TranscodePlanner.ReportHardwareSuccess(attempt.Encoder);
                    }

                    break;
                }

                if (job.CancelRequested)
                {
                    break;
                }

                if (attempt.IsHardware)
                {
                    // A hardware encoder that fails with CPU-decoded frames is not usable at all on
                    // this machine, so stop offering it after a couple of such failures.
                    if (!attempt.HardwareDecode)
                    {
                        TranscodePlanner.ReportHardwareFailure(attempt.Encoder);
                    }

                    job.FallbackFrom ??= attempt.Encoder;
                    work.Logger.LogWarning(
                        "Video tool job {JobId}: hardware encoder {Encoder} ({Label}) failed with exit code {ExitCode}, trying the next option",
                        job.Id,
                        attempt.Encoder,
                        attempt.Label,
                        exitCode);
                }
            }

            if (job.CancelRequested)
            {
                MarkCanceled(job);
                work.Logger.LogInformation("Video tool job {JobId} canceled", job.Id);
            }
            else if (exitCode == 0 && System.IO.File.Exists(job.WorkPath))
            {
                FinalizeOutput(job);
                job.Status = "Completed";
                job.Progress = 100;
                job.CompletedAt = DateTimeOffset.UtcNow;
                AddFolderForScan(job.OutputPath);
                work.Logger.LogInformation("Video tool job {JobId} finished: {OutputPath}", job.Id, job.OutputPath);
            }
            else
            {
                job.Status = "Failed";
                job.CompletedAt = DateTimeOffset.UtcNow;
                lock (errorLines)
                {
                    job.Error = string.Join(Environment.NewLine, errorLines);
                }

                // Do not leave a hidden half-written file behind; a retry starts from scratch.
                TryDeleteWorkFile(job);
                work.Logger.LogError("Video tool job {JobId} failed with exit code {ExitCode}: {Error}", job.Id, exitCode, job.Error);
            }
        }
        catch (Exception ex)
        {
            if (job.CancelRequested)
            {
                MarkCanceled(job);
            }
            else
            {
                job.Status = "Failed";
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.Error = ex.Message;
                work.Logger.LogError(ex, "Video tool job {JobId} failed to start", job.Id);
            }
        }
        finally
        {
            _processes.TryRemove(job.Id, out _);
            lock (_pathLock)
            {
                if (!string.IsNullOrEmpty(job.WorkPath))
                {
                    _reservedPaths.Remove(job.WorkPath);
                }
            }
        }
    }

    /// <summary>
    /// Runs the segmented interpolation pipeline: one audio extract, N concurrently encoded video
    /// slices (each with a pre-roll for motion context), then concat and mux. Progress maps the
    /// slowest running slice onto the job. Any failure returns non-zero so the caller falls back
    /// to the next attempt in the plan (the single-pass chain).
    /// </summary>
    /// <param name="work">The job work item.</param>
    /// <param name="plan">The segment geometry and encoder settings.</param>
    /// <param name="job">The job being updated.</param>
    /// <param name="errorLines">Collector for the last ffmpeg error lines.</param>
    /// <returns>Zero on success, non-zero when the next attempt should take over.</returns>
    private static async Task<int> RunParallelInterpolationAsync(
        JobWork work,
        TranscodePlanner.ParallelInterpolationPlan plan,
        VideoToolJobDto job,
        LinkedList<string> errorLines)
    {
        var ffmpegPath = work.EncoderPath;
        var workDir = job.WorkPath + ".interp";
        var audioPath = Path.Combine(workDir, "audio.m4a");
        var segmentPaths = new string[plan.Segments];

        try
        {
            System.IO.Directory.CreateDirectory(workDir);

            // Segment boundaries: N slices of equal nominal length. Slice i covers
            // [i*len - preRoll, (i+1)*len); the trim filter cuts the pre-roll back off
            // after interpolation so the concat is seamless. Trim windows are relative
            // to the seeked timeline (-ss resets timestamps to zero); the last slice
            // keeps everything to EOF so rounding cannot drop trailing frames.
            var nominal = plan.Duration / plan.Segments;
            var progressSlots = new double[plan.Segments];
            var videoTasks = new Task<bool>[plan.Segments];
            for (var i = 0; i < plan.Segments; i++)
            {
                var index = i;
                segmentPaths[i] = Path.Combine(workDir, "seg" + i.ToString("00", CultureInfo.InvariantCulture) + ".mp4");
                var seekStart = Math.Max(0, i * nominal - plan.PreRollSeconds);
                var trimStart = Math.Max(0, i * nominal - seekStart);
                var trimEnd = i == plan.Segments - 1
                    ? plan.Duration + plan.PreRollSeconds + nominal
                    : trimStart + nominal;
                videoTasks[i] = Task.Run(() => RunSegment(
                    ffmpegPath, plan, job, errorLines, index, seekStart, trimStart, trimEnd, segmentPaths[index], progressSlots));
            }

            // Audio is tiny next to the video slices; run it alongside them.
            var audioArgs = new List<string>
            {
                "-hide_banner", "-nostdin", "-y",
                "-i", plan.SourcePath,
                "-vn", "-c:a", "aac", "-b:a", "192k",
                audioPath
            };
            var audioTask = Task.Run(async () =>
            {
                var code = await RunBareProcessAsync(work, audioArgs, errorLines).ConfigureAwait(false);
                return code == 0 && System.IO.File.Exists(audioPath);
            });

            await Task.WhenAll(videoTasks).ConfigureAwait(false);
            var audioOk = await audioTask.ConfigureAwait(false);

            if (videoTasks.Any(t => !t.Result) || (!audioOk && HasAudioStream(plan.SourcePath, ffmpegPath)))
            {
                work.Logger.LogWarning(
                    "Video tool job {JobId}: segmented interpolation failed on at least one slice, falling back to a single pass",
                    job.Id);
                return 1;
            }

            // Concat: the slices are already encoded with identical parameters, so the
            // concat demuxer streams them together without re-encoding.
            var listPath = Path.Combine(workDir, "list.txt");
            var lines = segmentPaths
                .Where(System.IO.File.Exists)
                .Select(p => "file '" + p.Replace("'", "'\\''", StringComparison.Ordinal) + "'");
            await System.IO.File.WriteAllLinesAsync(listPath, lines).ConfigureAwait(false);

            var muxArgs = new List<string>
            {
                "-hide_banner", "-nostdin", "-y",
                "-f", "concat", "-safe", "0", "-i", listPath
            };
            if (System.IO.File.Exists(audioPath))
            {
                muxArgs.AddRange(["-i", audioPath]);
            }

            muxArgs.AddRange(
            [
                "-map", "0:v:0",
                "-map", System.IO.File.Exists(audioPath) ? "1:a:0" : "0:a:0?",
                "-c", "copy",
                "-movflags", "+faststart",
                job.WorkPath
            ]);

            var muxCode = await RunBareProcessAsync(work, muxArgs, errorLines).ConfigureAwait(false);
            if (muxCode != 0 || !System.IO.File.Exists(job.WorkPath) || new FileInfo(job.WorkPath).Length == 0)
            {
                return muxCode == 0 ? 1 : muxCode;
            }

            return 0;
        }
        catch (Exception ex)
        {
            work.Logger.LogError(ex, "Video tool job {JobId}: segmented interpolation pipeline threw", job.Id);
            lock (errorLines)
            {
                errorLines.AddLast("segmented interpolation: " + ex.Message);
            }

            return 1;
        }
        finally
        {
            try
            {
                if (System.IO.Directory.Exists(workDir))
                {
                    System.IO.Directory.Delete(workDir, recursive: true);
                }
            }
            catch
            {
                // Cleanup is best effort; a retry uses a fresh work path anyway.
            }
        }
    }

    /// <summary>
    /// Encodes one interpolation segment. Per-segment progress lands in <paramref name="progressSlots"/>
    /// and is aggregated onto the job; errors land in the shared collector.
    /// </summary>
    /// <returns><c>true</c> when the slice file exists and is non-empty.</returns>
    private static bool RunSegment(
        string ffmpegPath,
        TranscodePlanner.ParallelInterpolationPlan plan,
        VideoToolJobDto job,
        LinkedList<string> errorLines,
        int index,
        double seekStart,
        double trimStart,
        double trimEnd,
        string outputPath,
        double[] progressSlots)
    {
        try
        {
            var filters = new List<string>();
            if (plan.TargetSize is { } size)
            {
                filters.Add("scale=" + size.Width + ":" + size.Height);
            }

            filters.Add("minterpolate=fps=30:mi_mode=mci:me_mode=bidir:mc_mode=aobmc:vsbmc=1");

            // The first setpts normalizes the seeked timeline to zero (input -ss already does,
            // this guards against containers with broken timestamps); trim then cuts the
            // pre-roll and the slice tail on that normalized timeline.
            var args = new List<string>
            {
                "-hide_banner", "-nostdin", "-y",
                "-ss", seekStart.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", plan.SourcePath,
                "-vf", "setpts=PTS-STARTPTS"
                    + ",trim=start=" + trimStart.ToString("0.###", CultureInfo.InvariantCulture)
                    + ":end=" + trimEnd.ToString("0.###", CultureInfo.InvariantCulture)
                    + ",setpts=PTS-STARTPTS,"
                    + string.Join(",", filters),
                "-map", "0:v:0",
                "-an", "-sn", "-dn",
                "-c:v", plan.Encoder,
                "-preset", plan.Preset,
                "-b:v", plan.BitrateKbps.ToString(CultureInfo.InvariantCulture) + "k",
                "-r", "30",
                "-movflags", "+faststart",
                outputPath
            };

            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var segmentSpan = trimEnd - trimStart;
            string? line;
            while ((line = process.StandardError.ReadLine()) is not null)
            {
                lock (errorLines)
                {
                    errorLines.AddLast(line);
                    while (errorLines.Count > 40)
                    {
                        errorLines.RemoveFirst();
                    }
                }

                var timeIndex = line.IndexOf("time=", StringComparison.Ordinal);
                if (timeIndex >= 0 && segmentSpan > 0)
                {
                    var value = line.AsSpan(timeIndex + 5).Trim();
                    var spaceIndex = value.IndexOf(' ');
                    if (spaceIndex > 0)
                    {
                        value = value[..spaceIndex];
                    }

                    if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var elapsed))
                    {
                        progressSlots[index] = Math.Clamp(elapsed.TotalSeconds / segmentSpan, 0, 1);
                        job.Progress = Math.Clamp(95.0 * progressSlots.Sum() / progressSlots.Length, 0, 95);
                    }
                }

                if (job.CancelRequested && !process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Already gone.
                    }
                }
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return false;
            }

            return System.IO.File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex)
        {
            lock (errorLines)
            {
                errorLines.AddLast("segment " + index.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
            }

            return false;
        }
    }

    /// <summary>
    /// Runs a short-lived ffmpeg process without progress reporting (audio extract, mux). Errors
    /// land in the shared collector.
    /// </summary>
    /// <returns>The process exit code.</returns>
    private static async Task<int> RunBareProcessAsync(
        JobWork work,
        List<string> args,
        LinkedList<string> errorLines)
    {
        var startInfo = new ProcessStartInfo(work.EncoderPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            lock (errorLines)
            {
                errorLines.AddLast(stderr.Trim());
                while (errorLines.Count > 40)
                {
                    errorLines.RemoveFirst();
                }
            }
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Cheap check whether the source carries an audio stream at all.
    /// </summary>
    /// <returns><c>true</c> when an audio stream is declared.</returns>
    private static bool HasAudioStream(string sourcePath, string ffprobePath)
    {
        try
        {
            var probe = ffprobePath.Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase);
            using var process = Process.Start(new ProcessStartInfo(probe)
            {
                ArgumentList = { "-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", sourcePath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Trim().Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs ffmpeg once and feeds its progress into the job.
    /// </summary>
    /// <param name="work">The job work item.</param>
    /// <param name="args">The arguments to pass to ffmpeg.</param>
    /// <param name="job">The job being updated.</param>
    /// <param name="errorLines">Collector for the last ffmpeg error lines.</param>
    /// <returns>The ffmpeg exit code.</returns>
    private static async Task<int> RunProcessAsync(
        JobWork work,
        List<string> args,
        VideoToolJobDto job,
        LinkedList<string> errorLines)
    {
        var startInfo = new ProcessStartInfo(work.EncoderPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        _processes[job.Id] = process;
        job.ProcessId = process.Id;

        if (job.CancelRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // The process may have exited before the cancellation reached it.
            }
        }

        var duration = job.SourceDurationSeconds;

        var readTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                lock (errorLines)
                {
                    errorLines.AddLast(line);
                    while (errorLines.Count > 40)
                    {
                        errorLines.RemoveFirst();
                    }
                }

                if (duration > 0)
                {
                    var timeIndex = line.IndexOf("time=", StringComparison.Ordinal);
                    if (timeIndex >= 0)
                    {
                        var value = line.AsSpan(timeIndex + 5).Trim();
                        var spaceIndex = value.IndexOf(' ');
                        if (spaceIndex > 0)
                        {
                            value = value[..spaceIndex];
                        }

                        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var elapsed))
                        {
                            job.Progress = Math.Clamp(elapsed.TotalSeconds / duration * 100, 0, 100);
                        }
                    }
                }
            }
        });

        await process.WaitForExitAsync().ConfigureAwait(false);
        await readTask.ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>
    /// Removes a partially written work file before a retry.
    /// </summary>
    /// <param name="job">The job owning the file.</param>
    private static void TryDeleteWorkFile(VideoToolJobDto job)
    {
        try
        {
            if (!string.IsNullOrEmpty(job.WorkPath) && System.IO.File.Exists(job.WorkPath))
            {
                System.IO.File.Delete(job.WorkPath);
            }
        }
        catch (Exception)
        {
            // The file may still be held by ffmpeg; the next attempt overwrites it anyway.
        }
    }

    /// <summary>
    /// Moves the finished temporary file into place. The original file is kept next to the new one with an
    /// ".old" marker before its extension, so the new file can take over the original name (transcodes
    /// always do, cuts keep the same container and therefore the same name).
    /// </summary>
    /// <param name="job">The finished job.</param>
    private static void FinalizeOutput(VideoToolJobDto job)
    {
        if (job.Kind is "Transcode" or "Cut"
            && System.IO.File.Exists(job.SourcePath)
            && !string.Equals(job.SourcePath, job.WorkPath, StringComparison.Ordinal))
        {
            var oldPath = GetOldPath(job.SourcePath);
            System.IO.File.Move(job.SourcePath, oldPath, overwrite: false);
        }

        var outputPath = job.OutputPath;
        if (System.IO.File.Exists(outputPath) && !string.Equals(outputPath, job.WorkPath, StringComparison.Ordinal))
        {
            outputPath = GetUniquePath(outputPath);
        }

        System.IO.File.Move(job.WorkPath, outputPath, overwrite: true);
        job.OutputPath = outputPath;
    }

    private readonly record struct MediaProbe(
        double Duration,
        int Width,
        int Height,
        IReadOnlyList<int> SubtitleIndexes,
        double FrameRate,
        double NominalFrameRate,
        string VideoCodec,
        double VideoBitrateKbps = 0);

    private sealed record JobWork(
        VideoToolJobDto Job,
        IReadOnlyList<TranscodePlanner.Attempt> Attempts,
        string EncoderPath,
        ILibraryMonitor LibraryMonitor,
        ILibraryManager LibraryManager,
        IProviderManager ProviderManager,
        IDirectoryService DirectoryService,
        ILogger Logger);
}
