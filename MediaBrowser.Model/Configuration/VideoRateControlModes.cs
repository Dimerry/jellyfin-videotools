#pragma warning disable CS1591

namespace MediaBrowser.Model.Configuration;

/// <summary>
/// How the video encoder is told what size its output should be.
/// </summary>
public enum VideoRateControlModes
{
    /// <summary>
    /// Encode to the bitrate the client asked for (the historic behaviour).
    /// </summary>
    bitrate,

    /// <summary>
    /// Encode to a quality level (<see cref="EncodingOptions.VideoQualityPreset"/>) and let the
    /// encoder spend whatever bitrate that quality needs.
    /// </summary>
    quality
}
