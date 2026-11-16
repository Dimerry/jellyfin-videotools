#pragma warning disable CS1591

namespace MediaBrowser.Model.Configuration;

/// <summary>
/// The video encoder a transcode job should use.
/// </summary>
/// <remarks>
/// <see cref="auto"/> keeps the historic behaviour: the encoder matching
/// <see cref="EncodingOptions.HardwareAccelerationType"/> when hardware encoding is enabled,
/// otherwise the software encoder. Every other value names one encoder explicitly; when it is
/// missing on this machine, or fails, the server walks down the hardware encoders it does
/// support and finally falls back to the software encoder.
/// </remarks>
public enum VideoEncoderPreference
{
    auto,

    // H.264
    libx264,
    h264_qsv,
    h264_nvenc,
    h264_amf,
    h264_vaapi,
    h264_videotoolbox,
    h264_rkmpp,

    // HEVC / H.265
    libx265,
    hevc_qsv,
    hevc_nvenc,
    hevc_amf,
    hevc_vaapi,
    hevc_videotoolbox,
    hevc_rkmpp,

    // AV1
    libsvtav1,
    av1_qsv,
    av1_nvenc,
    av1_amf,
    av1_vaapi,
    av1_rkmpp
}
