namespace PamisWang.ClaudeAgentSdk.Transport;

/// <summary>
/// CLI 的環境變數：繼承目前程序的，再照 Python SDK 的規則補上與覆寫。對應 <c>connect()</c> 裡組 <c>process_env</c> 的部分。
/// </summary>
internal static class CliEnvironment
{
    /// <summary>
    /// CLI 只把 sdk-ts、sdk-py、sdk-cli 當成 SDK 啟動，有些判斷甚至只認前兩個。
    /// 協定是照 Python SDK 移植的，用 sdk-py，CLI 的行為才會跟錄下的協定樣本一致。
    /// </summary>
    public const string Entrypoint = "sdk-py";

    public static string ClientApp => $"PamisWang.ClaudeAgentSdk/{SdkInfo.Version}";

    /// <summary>修改 <paramref name="environment"/>，一開始應該是目前程序的環境變數。</summary>
    public static void Apply(IDictionary<string, string?> environment, ClaudeAgentOptions options)
    {
        // 不讓 CLI 以為自己在另一個 Claude Code 裡面執行；呼叫端真的要的話可以從 Env 設回來
        environment.Remove("CLAUDECODE");
        environment["CLAUDE_CODE_ENTRYPOINT"] = Entrypoint;

        foreach (var (key, value) in options.Env)
        {
            environment[key] = value;
        }

        // Python SDK 一律設成自己的版本，不讓呼叫端改；這裡報對照的 Python SDK 版本，跟錄協定樣本時一樣
        environment["CLAUDE_AGENT_SDK_VERSION"] = SdkInfo.PythonSdkVersion;

        // CLI 會把它放進 User-Agent 的 client-app，表明實際上是哪個用戶端；應用程式可以換成自己的名字
        if (!ContainsKeyIgnoreCase(environment, "CLAUDE_AGENT_SDK_CLIENT_APP"))
        {
            environment["CLAUDE_AGENT_SDK_CLIENT_APP"] = ClientApp;
        }

        // 讓 CLI 送出 session_state_changed，收尾時才知道它什麼時候回到 idle；呼叫端自己設過就不動
        if (!ContainsKeyIgnoreCase(environment, "CLAUDE_CODE_SDK_READS_SESSION_STATE"))
        {
            environment["CLAUDE_CODE_SDK_READS_SESSION_STATE"] = "1";
        }

        // 跟 Python 一樣在 Env 之後設：開了 EnableFileCheckpointing 就一定是 true
        if (options.EnableFileCheckpointing)
        {
            environment["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"] = "true";
        }

        if (options.Cwd is not null)
        {
            environment["PWD"] = options.Cwd;
        }
    }

    private static bool ContainsKeyIgnoreCase(IDictionary<string, string?> environment, string name) =>
        environment.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}
