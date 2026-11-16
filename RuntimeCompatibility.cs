using System.IO.Pipes;
using System.Net;
using System.Security.Authentication;

namespace NetPilot;

internal static class RuntimeCompatibility
{
#if LEGACY_WINDOWS
    internal const int RuntimeMajor = 6;
    internal const string TargetFramework = "net6.0";
    internal const string PackagePlatform = "win7-x64";
    internal const string UpdateDirectory = "application-updates-win7";
    internal const string TitleSuffix = " (Windows 7)";
    // .NET 6 itself requests FILE_FLAG_FIRST_PIPE_INSTANCE when maxInstances is 1.
    internal const PipeOptions ControlPipeOptions = PipeOptions.Asynchronous;
#else
    internal const int RuntimeMajor = 10;
    internal const string TargetFramework = "net10.0";
    internal const string PackagePlatform = "win-x64";
    internal const string UpdateDirectory = "application-updates";
    internal const string TitleSuffix = "";
    internal const PipeOptions ControlPipeOptions = PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance;
#endif
    internal static bool RequiresLegacyTls(Version windowsVersion) => windowsVersion.Major == 6 && windowsVersion.Minor == 1;
    internal static bool IsWindows7 => OperatingSystem.IsWindows() && RequiresLegacyTls(Environment.OSVersion.Version);
    internal static Version PreferredHttpVersion => IsWindows7 ? HttpVersion.Version11 : HttpVersion.Version20;
    internal static string ArchiveName(string version, bool publicName = false)
        => "RtlineDnsPilot-" + version + (publicName ? "-public-" : "-") + PackagePlatform + ".zip";
    internal static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
    internal static bool IsAsciiLetter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    internal static bool IsAsciiLetterOrDigit(char value) => IsAsciiLetter(value) || IsAsciiDigit(value);

    internal static HttpClientHandler CreateHttpsHandler() => ConfigureHttpsHandler(new HttpClientHandler
    { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null }, IsWindows7);

    internal static HttpClientHandler ConfigureHttpsHandler(HttpClientHandler handler, bool windows7)
    {
        if (windows7) handler.SslProtocols = SslProtocols.Tls12;
        return handler;
    }

    internal static void ConfigurePinnedHttpsHandler(SocketsHttpHandler handler, bool windows7)
    {
        if (windows7) handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12;
        // Certificate/name validation remains the platform default; no callback is installed.
    }
}
