namespace Jellyfin.Api.Models.VideoToolDtos;

/// <summary>
/// User-editable defaults applied when a job is queued from the library without explicit settings.
/// </summary>
public sealed class VideoToolsDefaultsDto
{
    /// <summary>
    /// Gets or sets the default transcode settings.
    /// </summary>
    public VideoToolsTranscodeDefaults Transcode { get; set; } = new();

    /// <summary>
    /// Gets or sets the default cut settings. Only used by crop jobs, since a plain trim is a stream copy.
    /// </summary>
    public VideoToolsCutDefaults Cut { get; set; } = new();
}

/// <summary>
/// Default transcode settings, mirroring the transcode dialog options.
/// </summary>
public sealed class VideoToolsTranscodeDefaults
{
    /// <summary>
    /// Gets or sets the target codec: <c>h265</c> or <c>av1</c>.
    /// </summary>
    public string Codec { get; set; } = "h265";

    /// <summary>
    /// Gets or sets the first encoder to try, or <c>auto</c>.
    /// </summary>
    public string Encoder { get; set; } = "auto";

    /// <summary>
    /// Gets or sets the maximum resolution: <c>1080p</c> or <c>4k</c>.
    /// </summary>
    public string Resolution { get; set; } = "1080p";

    /// <summary>
    /// Gets or sets the rate control mode: <c>quality</c> or <c>bitrate</c>.
    /// </summary>
    public string RateControl { get; set; } = "quality";

    /// <summary>
    /// Gets or sets the quality level used in quality mode.
    /// </summary>
    public string Quality { get; set; } = "medium";

    /// <summary>
    /// Gets or sets the target bitrate in kbps, used in bitrate mode.
    /// </summary>
    public int BitrateKbps { get; set; } = 3000;
}

/// <summary>
/// Default crop settings. There is no resolution limit: cropping keeps the source geometry.
/// </summary>
public sealed class VideoToolsCutDefaults
{
    /// <summary>
    /// Gets or sets the target codec used when a crop re-encodes the picture: <c>h265</c> or <c>av1</c>.
    /// </summary>
    public string Codec { get; set; } = "h265";

    /// <summary>
    /// Gets or sets the first encoder to try, or <c>auto</c>.
    /// </summary>
    public string Encoder { get; set; } = "auto";

    /// <summary>
    /// Gets or sets the rate control mode: <c>quality</c> or <c>bitrate</c>.
    /// </summary>
    public string RateControl { get; set; } = "quality";

    /// <summary>
    /// Gets or sets the quality level used in quality mode.
    /// </summary>
    public string Quality { get; set; } = "high";

    /// <summary>
    /// Gets or sets the target bitrate in kbps, used in bitrate mode.
    /// </summary>
    public int BitrateKbps { get; set; } = 3000;
}
