using System.Reflection;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>套件的版本，以及移植時對照的 Python SDK 版本。</summary>
internal static class SdkInfo
{
    /// <summary>移植時對照的官方 Python SDK 版本；協定以它為準。</summary>
    public const string PythonSdkVersion = "0.2.174";

    /// <summary>套件自己的版本，不含 SourceLink 附加的 <c>+commit</c>。</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var version = typeof(SdkInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}
