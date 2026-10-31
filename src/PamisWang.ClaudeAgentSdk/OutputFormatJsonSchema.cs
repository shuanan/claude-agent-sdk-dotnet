using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 要求最後的回覆符合這份 JSON Schema（<c>--json-schema</c>）。對應 Python 的 <c>{"type": "json_schema", "schema": …}</c>。
/// </summary>
/// <param name="Schema">JSON Schema；JSON 的 <c>null</c> 跟 Python 的 <c>None</c> 一樣，不帶參數。</param>
public sealed record OutputFormatJsonSchema(JsonElement Schema) : OutputFormat;
