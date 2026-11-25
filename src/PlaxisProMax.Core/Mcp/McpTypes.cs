using System.Text.Json;

namespace PlaxisProMax.Mcp;

/// <summary>One tool as the MCP client sees it: its name, description and input schema (from Sketch ProMax's ConnectorTools.Def).</summary>
internal sealed record McpTool(string Name, string? Description, JsonElement InputSchema);

/// <summary>A tool call's answer: text, whether it failed, and an optional picture (from Sketch ProMax's ConnectorResult).</summary>
internal sealed record McpResult(string Text, bool IsError, string? PngBase64 = null);
