using System.Text;
using System.Text.Json;
using ILD.Data;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Generates a Pi extension TypeScript file from the shared <see cref="ToolDescriptors"/>.
/// The generated extension registers tools that call the ILD agent-scoped API.
/// </summary>
internal static class PiExtensionGenerator
{
    /// <summary>
    /// Generate the full TypeScript extension content.
    /// </summary>
    public static string Generate(string apiUrl, string apiToken, string loopRunId)
        => Generate(apiUrl, apiToken, loopRunId, allowedToolNames: null);

    public static string Generate(string apiUrl, string apiToken, string loopRunId, IReadOnlyCollection<string>? allowedToolNames)
        => Generate(apiUrl, apiToken, loopRunId, allowedToolNames, isChatSession: false);

    /// <param name="isChatSession">
    /// When true the extension identifies its calls with the
    /// <c>X-ILD-Chat-Session-Id</c> header (so created work items are stamped with
    /// the chat session id) instead of the loop-run header.
    /// </param>
    public static string Generate(string apiUrl, string apiToken, string contextId, IReadOnlyCollection<string>? allowedToolNames, bool isChatSession)
    {
        var headerName = isChatSession ? "X-ILD-Chat-Session-Id" : "X-ILD-Run-Id";
        var sb = new StringBuilder();
        var allowedToolNameSet = allowedToolNames == null
            ? null
            : new HashSet<string>(allowedToolNames, StringComparer.OrdinalIgnoreCase);

        // Header
        sb.AppendLine("// ILD Platform Extension for Pi");
        sb.AppendLine("// Auto-generated from ToolDescriptors — do not edit.");
        sb.AppendLine();
        sb.AppendLine("import type { ExtensionAPI } from \"@earendil-works/pi-coding-agent\";");
        sb.AppendLine("import { Type } from \"typebox\";");
        sb.AppendLine();

        // Constants — safely escaped
        sb.Append("const API_BASE = \"");
        sb.Append(EscapeTsString(apiUrl));
        sb.AppendLine("\";");
        sb.Append("const API_TOKEN = \"");
        sb.Append(EscapeTsString(apiToken));
        sb.AppendLine("\";");
        sb.Append("const LOOP_RUN_ID = \"");
        sb.Append(EscapeTsString(contextId));
        sb.AppendLine("\";");
        sb.AppendLine();

        // HTTP helpers
        AppendHttpHelpers(sb, headerName);

        // Extension factory
        sb.AppendLine("export default function (pi: ExtensionAPI) {");

        foreach (var tool in ToolDescriptors.All)
        {
            if (allowedToolNameSet != null && !allowedToolNameSet.Contains(tool.Name))
                continue;

            AppendToolRegistration(sb, tool);
            sb.AppendLine();
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendHttpHelpers(StringBuilder sb, string headerName)
    {
        sb.AppendLine("function joinApiUrl(base: string, path: string): string {");
        sb.AppendLine("    const normalizedBase = base.endsWith(\"/\") ? base.slice(0, -1) : base;");
        sb.AppendLine("    const normalizedPath = path.startsWith(\"/\") ? path : `/${path}`;");
        sb.AppendLine("    return `${normalizedBase}${normalizedPath}`;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("async function ildGet(path: string): Promise<string> {");
        sb.AppendLine("    const url = joinApiUrl(API_BASE, path);");
        sb.AppendLine("    const headers: Record<string, string> = {};");
        sb.AppendLine("    if (API_TOKEN) headers[\"Authorization\"] = `Bearer ${API_TOKEN}`;");
        sb.AppendLine($"    if (LOOP_RUN_ID) headers[\"{headerName}\"] = LOOP_RUN_ID;");
        sb.AppendLine("    const resp = await fetch(url, { headers });");
        sb.AppendLine("    const body = await resp.text();");
        sb.AppendLine("    if (!resp.ok) throw new Error(`GET ${path} failed: ${resp.status} ${resp.statusText}`);");
        sb.AppendLine("    return body;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("async function ildPost(path: string, body: object): Promise<string> {");
        sb.AppendLine("    const url = joinApiUrl(API_BASE, path);");
        sb.AppendLine("    const headers: Record<string, string> = { \"Content-Type\": \"application/json\" };");
        sb.AppendLine("    if (API_TOKEN) headers[\"Authorization\"] = `Bearer ${API_TOKEN}`;");
        sb.AppendLine($"    if (LOOP_RUN_ID) headers[\"{headerName}\"] = LOOP_RUN_ID;");
        sb.AppendLine("    const resp = await fetch(url, {");
        sb.AppendLine("        method: \"POST\",");
        sb.AppendLine("        headers,");
        sb.AppendLine("        body: JSON.stringify(body),");
        sb.AppendLine("    });");
        sb.AppendLine("    const text = await resp.text();");
        sb.AppendLine("    if (!resp.ok) throw new Error(`POST ${path} failed: ${resp.status} ${resp.statusText}`);");
        sb.AppendLine("    return text;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("async function ildPut(path: string, body: object): Promise<string> {");
        sb.AppendLine("    const url = joinApiUrl(API_BASE, path);");
        sb.AppendLine("    const headers: Record<string, string> = { \"Content-Type\": \"application/json\" };");
        sb.AppendLine("    if (API_TOKEN) headers[\"Authorization\"] = `Bearer ${API_TOKEN}`;");
        sb.AppendLine($"    if (LOOP_RUN_ID) headers[\"{headerName}\"] = LOOP_RUN_ID;");
        sb.AppendLine("    const resp = await fetch(url, {");
        sb.AppendLine("        method: \"PUT\",");
        sb.AppendLine("        headers,");
        sb.AppendLine("        body: JSON.stringify(body),");
        sb.AppendLine("    });");
        sb.AppendLine("    const text = await resp.text();");
        sb.AppendLine("    if (!resp.ok) throw new Error(`PUT ${path} failed: ${resp.status} ${resp.statusText}`);");
        sb.AppendLine("    return text;");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void AppendToolRegistration(StringBuilder sb, ToolDescriptor tool)
    {
        sb.AppendLine("    pi.registerTool({");
        sb.Append("        name: \"");
        sb.Append(EscapeTsString(tool.Name));
        sb.AppendLine("\",");
        sb.Append("        label: \"");
        sb.Append(EscapeTsString(tool.Label));
        sb.AppendLine("\",");
        sb.Append("        description: \"");
        sb.Append(EscapeTsString(tool.Description));
        sb.AppendLine("\",");

        // Parameters schema
        sb.AppendLine("        parameters: Type.Object({");
        foreach (var param in tool.Parameters)
        {
            sb.Append("            ");
            sb.Append(param.Name);
            sb.Append(": ");
            if (param.IsOptional)
                sb.Append("Type.Optional(");

            // Array types need special handling: Type.Array(Type.String({ description: "..." }))
            if (param.TsType == "string-array")
            {
                sb.Append("Type.Array(Type.String({ description: \"");
                sb.Append(EscapeTsString(param.Description));
                sb.Append("\" }))");
            }
            else
            {
                sb.Append("Type.");
                sb.Append(TsTypeToTypeBox(param.TsType));
                sb.Append("({ description: \"");
                sb.Append(EscapeTsString(param.Description));
                sb.Append("\" })");
            }

            if (param.IsOptional)
                sb.Append(")");
            sb.AppendLine(",");
        }
        sb.AppendLine("        }),");

        // Execute function
        sb.AppendLine("        async execute(_toolCallId, params) {");
        AppendExecuteBody(sb, tool);
        sb.AppendLine("        },");
        sb.AppendLine("    });");
    }

    private static void AppendExecuteBody(StringBuilder sb, ToolDescriptor tool)
    {
        // Uniform builder: substitute {name} path placeholders, append any
        // remaining non-body params as query string, then dispatch by verb. This
        // covers GET/POST/PUT with any mix of path, query, and body params.
        var pathParams = tool.Parameters
            .Where(p => tool.EndpointPath.Contains("{" + p.Name + "}", StringComparison.Ordinal))
            .ToArray();
        var queryParams = tool.Parameters
            .Where(p => !p.IsBodyParam && !tool.EndpointPath.Contains("{" + p.Name + "}", StringComparison.Ordinal))
            .ToArray();
        var bodyParams = tool.Parameters.Where(p => p.IsBodyParam).ToArray();

        sb.Append("            let path = \"");
        sb.Append(tool.EndpointPath);
        sb.AppendLine("\";");
        foreach (var param in pathParams)
        {
            sb.Append("            path = path.replace(\"{");
            sb.Append(param.Name);
            sb.Append("}\", encodeURIComponent(String(params.");
            sb.Append(param.Name);
            sb.AppendLine(")));");
        }

        var hasQuery = queryParams.Length > 0;
        if (hasQuery)
        {
            sb.AppendLine("            const qs = new URLSearchParams();");
            foreach (var param in queryParams)
            {
                sb.Append("            if (params.");
                sb.Append(param.Name);
                if (param.TsType == "number")
                {
                    sb.Append(" !== undefined) qs.set(\"");
                    sb.Append(param.Name);
                    sb.Append("\", String(params.");
                    sb.Append(param.Name);
                    sb.AppendLine("));");
                }
                else
                {
                    sb.Append(" != null) qs.set(\"");
                    sb.Append(param.Name);
                    sb.Append("\", String(params.");
                    sb.Append(param.Name);
                    sb.AppendLine("));");
                }
            }
            sb.AppendLine("            const url = qs.toString() ? `${path}?${qs.toString()}` : path;");
        }
        else
        {
            sb.AppendLine("            const url = path;");
        }

        if (tool.HttpMethod == HttpMethod.Post || tool.HttpMethod == HttpMethod.Put)
        {
            sb.AppendLine("            const body: any = {");
            foreach (var param in bodyParams)
            {
                sb.Append("                ");
                sb.Append(param.Name);
                sb.Append(": params.");
                sb.Append(param.Name);
                if (param.IsOptional)
                    sb.AppendLine(" ?? undefined,");
                else
                    sb.AppendLine(",");
            }
            sb.AppendLine("            };");
            var verb = tool.HttpMethod == HttpMethod.Put ? "ildPut" : "ildPost";
            sb.Append("            return { content: [{ type: \"text\", text: await ");
            sb.Append(verb);
            sb.AppendLine("(url, body) }], details: {} };");
        }
        else
        {
            sb.AppendLine("            return { content: [{ type: \"text\", text: await ildGet(url) }], details: {} };");
        }
    }

    private static string TsTypeToTypeBox(string tsType) => tsType switch
    {
        "string" => "String",
        "number" => "Number",
        "boolean" => "Boolean",
        _ => "String",
    };

    private static string EscapeTsString(string value)
        => JsonSerializer.Serialize(value)[1..^1];
}
