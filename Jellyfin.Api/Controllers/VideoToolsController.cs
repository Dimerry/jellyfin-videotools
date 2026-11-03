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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
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
    private static CancellationTokenSource? _scanCts;
    private static bool _workerStarted;

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IProviderManager _providerManager;
    private readonly IDirectoryService _directoryService;
    private readonly ILogger<VideoToolsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoToolsController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="directoryService">Instance of the <see cref="IDirectoryService"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public VideoToolsController(
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IMediaEncoder mediaEncoder,
        IProviderManager providerManager,
        IDirectoryService directoryService,
        ILogger<VideoToolsController> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _mediaEncoder = mediaEncoder;
        _providerManager = providerManager;
        _directoryService = directoryService;
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
    /// <returns>The created job.</returns>
    [HttpPost("{itemId}/Transcode")]
    public ActionResult<VideoToolJobDto> StartTranscode(
        [FromRoute] Guid itemId,
        [FromQuery] string codec = "h265",
        [FromQuery] int bitrateKbps = 3000,
        [FromQuery] string resolution = "1080p",
        [FromQuery] bool interpolate = false)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        if (!TryParseTranscodeParams(codec, bitrateKbps, resolution, out var normalizedCodec, out var maxHeight, out var error))
        {
            return BadRequest(error);
        }

        var job = EnqueueTranscode(item, normalizedCodec, bitrateKbps, maxHeight, interpolate);
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
    /// <returns>The queued jobs and the names of the items that were skipped.</returns>
    [HttpPost("Transcode")]
    public ActionResult<TranscodeBatchResultDto> StartTranscodeBatch(
        [FromBody] Guid[] itemIds,
        [FromQuery] string codec = "h265",
        [FromQuery] int bitrateKbps = 3000,
        [FromQuery] string resolution = "1080p",
        [FromQuery] bool interpolate = false)
    {
        if (itemIds is null || itemIds.Length == 0)
        {
            return BadRequest("No items were selected.");
        }

        if (!TryParseTranscodeParams(codec, bitrateKbps, resolution, out var normalizedCodec, out var maxHeight, out var error))
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

            var job = EnqueueTranscode(item, normalizedCodec, bitrateKbps, maxHeight, interpolate);
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
    /// <returns>The created job.</returns>
    [HttpPost("{itemId}/Cut")]
    public ActionResult<VideoToolJobDto> StartCut(
        [FromRoute] Guid itemId,
        [FromQuery] double startSeconds = 0,
        [FromQuery] double endSeconds = 0,
        [FromQuery] int rotation = 0)
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

        var keepSeconds = duration - startSeconds - endSeconds;
        var paths = CreateCutPaths(sourcePath);

        var args = BuildCutArgs(sourcePath, paths.WorkPath, startSeconds, keepSeconds, rotation);

        var job = CreateJob("Cut", item.Id, item.Name, paths.FinalPath, sourcePath, duration);
        job.WorkPath = paths.WorkPath;
        job.CutStartSeconds = startSeconds;
        job.CutEndSeconds = endSeconds;
        job.CutRotation = rotation;
        StartJob(job, args);
        return Accepted(job);
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

                var paths = CreateTranscodePaths(failedJob.SourcePath);
                var outputPath = paths.FinalPath;

                var job = CreateJob("Transcode", failedJob.ItemId, failedJob.ItemName, outputPath, failedJob.SourcePath, duration);
                job.WorkPath = paths.WorkPath;
                job.Codec = codec == "av1" ? "AV1" : "H.265";
                job.Resolution = maxHeight == 2160 ? "4K" : "1080p";
                job.BitrateKbps = bitrateKbps;
                job.FrameInterpolation = interpolate;
                StartJob(job, BuildTranscodePlan(probe, failedJob.SourcePath, paths.WorkPath, codec, bitrateKbps, maxHeight, interpolate));
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
                StartJob(job, BuildCutArgs(failedJob.SourcePath, paths.WorkPath, startSeconds, keepSeconds, failedJob.CutRotation));
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
    /// <returns>The JPEG frame.</returns>
    [HttpGet("{itemId}/Frame")]
    public async Task<ActionResult> GetFrame(
        [FromRoute] Guid itemId,
        [FromQuery] double timeSeconds = 0,
        [FromQuery] int maxWidth = 480,
        [FromQuery] int rotation = 0)
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
            "{0:N}_{1}_{2}_{3}_{4}.jpg",
            itemId,
            modified,
            maxWidth,
            rotation,
            timeSeconds.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', 'p'));
        var cachePath = Path.Combine(FrameCacheDirectory, cacheName);

        if (!System.IO.File.Exists(cachePath))
        {
            await _frameLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!System.IO.File.Exists(cachePath))
                {
                    var width = ComputePreviewWidth(sourcePath, maxWidth, rotation);
                    var error = await RenderFrameAsync(sourcePath, timeSeconds, width, rotation, cachePath).ConfigureAwait(false);
                    if (error is not null && timeSeconds > 0)
                    {
                        // The reported duration can be slightly longer than the last decodable frame;
                        // step back a little before giving up.
                        foreach (var backoff in BackoffSeconds)
                        {
                            var retryTime = Math.Max(0, timeSeconds - backoff);
                            error = await RenderFrameAsync(sourcePath, retryTime, width, rotation, cachePath).ConfigureAwait(false);
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
    private int ComputePreviewWidth(string sourcePath, int maxWidth, int rotation)
    {
        var probe = ProbeSource(
            sourcePath,
            string.IsNullOrWhiteSpace(_mediaEncoder.ProbePath) ? "ffprobe" : _mediaEncoder.ProbePath);

        var width = rotation is 90 or 270 ? probe.Height : probe.Width;
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
        string outputPath)
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

    private static bool TryParseTranscodeParams(
        string codec,
        int bitrateKbps,
        string resolution,
        out string normalizedCodec,
        out int maxHeight,
        out string? error)
    {
        normalizedCodec = codec.Trim().ToLowerInvariant();
        maxHeight = 0;
        error = null;

        if (normalizedCodec is not ("av1" or "h265" or "hevc"))
        {
            error = "codec must be 'av1' or 'h265'.";
            return false;
        }

        if (bitrateKbps < 100 || bitrateKbps > 200000)
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

    private VideoToolJobDto? EnqueueTranscode(BaseItem item, string codec, int bitrateKbps, int maxHeight, bool interpolate)
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
        job.BitrateKbps = bitrateKbps;
        job.FrameInterpolation = interpolate;
        StartJob(job, BuildTranscodePlan(probe, sourcePath, paths.WorkPath, codec, bitrateKbps, maxHeight, interpolate));
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
    /// <returns>The attempts to run in order.</returns>
    private IReadOnlyList<TranscodePlanner.Attempt> BuildTranscodePlan(
        MediaProbe probe,
        string sourcePath,
        string workPath,
        string codec,
        int bitrateKbps,
        int maxHeight,
        bool interpolate = false)
    {
        var ffmpegPath = string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath) ? "ffmpeg" : _mediaEncoder.EncoderPath;
        var plan = TranscodePlanner.Build(new TranscodePlanner.Request
        {
            SourcePath = sourcePath,
            OutputPath = workPath,
            Codec = codec,
            BitrateKbps = bitrateKbps,
            MaxHeight = maxHeight,
            SourceVideoCodec = probe.VideoCodec,
            Width = probe.Width,
            Height = probe.Height,
            FrameRate = probe.FrameRate,
            NominalFrameRate = probe.NominalFrameRate,
            SubtitleIndexes = probe.SubtitleIndexes ?? Array.Empty<int>(),
            FfmpegPath = ffmpegPath,
            Interpolate = interpolate
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

            return new MediaProbe(duration, width, height, subtitleIndexes, frameRate, nominalFrameRate, videoCodec);
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
    /// <param name="sourcePath">The source file path.</param>
    /// <returns>The final path and the temporary work path.</returns>
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

                exitCode = await RunProcessAsync(work, attempt.Args, job, errorLines).ConfigureAwait(false);
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
        string VideoCodec);

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
