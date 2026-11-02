using System.Reflection;

// Upstream Jellyfin 12.4 baseline. The last digit is our own iteration counter: clients
// (Android/iOS/official web) compare this number against the server feature floor, so keep
// the major/minor in sync with the upstream release we are based on instead of starting at 0.x.
[assembly: AssemblyVersion("12.4.2")]
[assembly: AssemblyFileVersion("12.4.2")]
[assembly: AssemblyInformationalVersion("12.4.2-videotools")]
