#pragma warning disable CS1591

namespace MediaBrowser.Model.Configuration;

/// <summary>
/// Quality level used when <see cref="EncodingOptions.VideoRateControlMode"/> is
/// <see cref="VideoRateControlModes.quality"/>.
/// </summary>
/// <remarks>
/// The lowest value (<see cref="highest"/>) is the best looking output and the largest file; the
/// highest value (<see cref="lowest"/>) saves the most bitrate.
/// </remarks>
public enum VideoQualityPresets
{
    highest,
    high,
    medium,
    low,
    lowest
}
