using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// bash 指令的 sandbox（檔案系統與網路隔離）。對應 Python SDK 的 <c>SandboxSettings</c>；
/// 會合併進 <c>--settings</c> 的 <c>sandbox</c>。每個欄位都可以不給，<see langword="null"/> 的欄位不會送出。
/// 工具層級的檔案與網路限制不在這裡設定，要用權限規則（Read、Edit、WebFetch 的 allow／deny）。
/// </summary>
public sealed record SandboxSettings
{
    /// <summary>啟用 bash 的 sandbox，只有 macOS 與 Linux（<c>enabled</c>）。</summary>
    public bool? Enabled { get; init; }

    /// <summary>在 sandbox 裡跑的 bash 指令自動批准（<c>autoAllowBashIfSandboxed</c>）。</summary>
    public bool? AutoAllowBashIfSandboxed { get; init; }

    /// <summary>在 sandbox 外面跑的指令，例如 <c>git</c>、<c>docker</c>（<c>excludedCommands</c>）。</summary>
    public IReadOnlyList<string>? ExcludedCommands { get; init; }

    /// <summary>
    /// 允許指令用 <c>dangerouslyDisableSandbox</c> 跳出 sandbox（<c>allowUnsandboxedCommands</c>）；
    /// <see langword="false"/> 時所有指令都要在 sandbox 裡跑，除非列在 <see cref="ExcludedCommands"/>。
    /// </summary>
    public bool? AllowUnsandboxedCommands { get; init; }

    /// <summary>sandbox 自己的網路隔離（<c>network</c>）。</summary>
    public SandboxNetworkConfig? Network { get; init; }

    /// <summary>
    /// 不回報的違規（<c>ignoreViolations</c>）：鍵是指令的樣式（<c>*</c> 代表所有指令），值是違規內容裡的字串。
    /// 兩者都是單純的子字串比對，不是萬用字元。只影響回報，不影響 sandbox 擋什麼。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? IgnoreViolations { get; init; }

    /// <summary>給沒有特權的 Docker 環境用的較弱 sandbox，只有 Linux，安全性較低（<c>enableWeakerNestedSandbox</c>）。</summary>
    public bool? EnableWeakerNestedSandbox { get; init; }

    /// <summary>寫成 settings 裡的格式，鍵名跟 Python 的 TypedDict 一樣。</summary>
    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteOptional("enabled", Enabled);
        writer.WriteOptional("autoAllowBashIfSandboxed", AutoAllowBashIfSandboxed);
        writer.WriteOptional("excludedCommands", ExcludedCommands);
        writer.WriteOptional("allowUnsandboxedCommands", AllowUnsandboxedCommands);
        if (Network is not null)
        {
            writer.WritePropertyName("network");
            Network.WriteTo(writer);
        }

        if (IgnoreViolations is not null)
        {
            writer.WriteStartObject("ignoreViolations");
            foreach (var (pattern, values) in IgnoreViolations)
            {
                writer.WriteOptional(pattern, values);
            }

            writer.WriteEndObject();
        }

        writer.WriteOptional("enableWeakerNestedSandbox", EnableWeakerNestedSandbox);
        writer.WriteEndObject();
    }
}
