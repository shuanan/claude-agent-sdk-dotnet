using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>在 API 端執行的工具呼叫，例如網頁搜尋、advisor。</summary>
/// <param name="Id">這次呼叫的 ID。</param>
/// <param name="Name">工具名稱。</param>
/// <param name="Input">工具的參數。</param>
public sealed record ServerToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock;
