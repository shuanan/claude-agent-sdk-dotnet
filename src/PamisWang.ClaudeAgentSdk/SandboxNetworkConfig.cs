using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// sandbox 裡 bash 指令的網路隔離。對應 Python SDK 的 <c>SandboxNetworkConfig</c>；
/// 每個欄位都可以不給，<see langword="null"/> 的欄位不會送出，由 Claude Code 用預設。
/// </summary>
public sealed record SandboxNetworkConfig
{
    /// <summary>sandbox 裡的程序可以連的網域（<c>allowedDomains</c>）。</summary>
    public IReadOnlyList<string>? AllowedDomains { get; init; }

    /// <summary>一律封鎖的網域，就算符合 <see cref="AllowedDomains"/> 也一樣（<c>deniedDomains</c>）。</summary>
    public IReadOnlyList<string>? DeniedDomains { get; init; }

    /// <summary>寫在管理設定時，只採用管理設定裡的 <c>allowedDomains</c>（<c>allowManagedDomainsOnly</c>）。</summary>
    public bool? AllowManagedDomainsOnly { get; init; }

    /// <summary>sandbox 裡可以用的 Unix socket 路徑，例如 SSH agent（<c>allowUnixSockets</c>）。</summary>
    public IReadOnlyList<string>? AllowUnixSockets { get; init; }

    /// <summary>允許所有 Unix socket，比較不安全（<c>allowAllUnixSockets</c>）。</summary>
    public bool? AllowAllUnixSockets { get; init; }

    /// <summary>允許綁定 localhost 的連接埠，只有 macOS（<c>allowLocalBinding</c>）。</summary>
    public bool? AllowLocalBinding { get; init; }

    /// <summary>只有 macOS：允許的 XPC／Mach 服務名稱，結尾可以用萬用字元（<c>allowMachLookup</c>）。</summary>
    public IReadOnlyList<string>? AllowMachLookup { get; init; }

    /// <summary>自備 HTTP proxy 時的連接埠（<c>httpProxyPort</c>）。</summary>
    public int? HttpProxyPort { get; init; }

    /// <summary>自備 SOCKS5 proxy 時的連接埠（<c>socksProxyPort</c>）。</summary>
    public int? SocksProxyPort { get; init; }

    /// <summary>寫成 settings 裡的格式，鍵名跟 Python 的 TypedDict 一樣。</summary>
    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteOptional("allowedDomains", AllowedDomains);
        writer.WriteOptional("deniedDomains", DeniedDomains);
        writer.WriteOptional("allowManagedDomainsOnly", AllowManagedDomainsOnly);
        writer.WriteOptional("allowUnixSockets", AllowUnixSockets);
        writer.WriteOptional("allowAllUnixSockets", AllowAllUnixSockets);
        writer.WriteOptional("allowLocalBinding", AllowLocalBinding);
        writer.WriteOptional("allowMachLookup", AllowMachLookup);
        writer.WriteOptional("httpProxyPort", HttpProxyPort);
        writer.WriteOptional("socksProxyPort", SocksProxyPort);
        writer.WriteEndObject();
    }
}
