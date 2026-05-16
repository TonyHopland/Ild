using System.Text;
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
    {
        var sb = new StringBuilder();

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
        sb.Append(EscapeTsString(loopRunId));
        sb.AppendLine("\";");
        sb.AppendLine();

        // HTTP helpers
        AppendHttpHelpers(sb);

        // Extension factory
        sb.AppendLine("export default function (pi: ExtensionAPI) {");

        foreach (var tool in ToolDescriptors.All)
        {
            AppendToolRegistration(sb, tool);
            sb.AppendLine();
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendHttpHelpers(StringBuilder sb)
    {
        sb.AppendLine("async function ildGet(path: string): Promise<string> {");
        sb.AppendLine("    const url = API_BASE + path;");
        sb.AppendLine("    const headers: Record<string, string> = {};");
        sb.AppendLine("    if (API_TOKEN) headers[\"Authorization\"] = `Bearer ${API_TOKEN}`;");
        sb.AppendLine("    if (LOOP_RUN_ID) headers[\"X-ILD-Run-Id\"] = LOOP_RUN_ID;");
        sb.AppendLine("    const resp = await fetch(url, { headers });");
        sb.AppendLine("    const body = await resp.text();");
        sb.AppendLine("    if (!resp.ok) throw new Error(`GET ${path} failed: ${resp.status} ${resp.statusText}`);");
        sb.AppendLine("    return body;");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("async function ildPost(path: string, body: object): Promise<string> {");
        sb.AppendLine("    const url = API_BASE + path;");
        sb.AppendLine("    const headers: Record<string, string> = { \"Content-Type\": \"application/json\" };");
        sb.AppendLine("    if (API_TOKEN) headers[\"Authorization\"] = `Bearer ${API_TOKEN}`;");
        sb.AppendLine("    if (LOOP_RUN_ID) headers[\"X-ILD-Run-Id\"] = LOOP_RUN_ID;");
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
        if (tool.HttpMethod == HttpMethod.Post)
        {
            AppendPostExecuteBody(sb, tool);
        }
        else if (tool.EndpointPath.Contains("{"))
        {
            AppendGetWithPathParam(sb, tool);
        }
        else
        {
            AppendGetWithQueryParams(sb, tool);
        }
    }

    private static void AppendGetWithPathParam(StringBuilder sb, ToolDescriptor tool)
    {
        // e.g. api/v1/agent/workitems/{id} -> uses a required path param
        var pathParam = tool.Parameters.FirstOrDefault(p => !p.IsOptional);
        if (pathParam != null)
        {
            var endpointWithPlaceholder = tool.EndpointPath
                .Replace("{" + pathParam.Name + "}", "${encodeURIComponent(params." + pathParam.Name + ")}");
            sb.Append("            return { content: [{ type: \"text\", text: await ildGet(`");
            sb.Append(endpointWithPlaceholder);
            sb.AppendLine("`) }], details: {} };");
        }
        else
        {
            sb.Append("            return { content: [{ type: \"text\", text: await ildGet(\"");
            sb.Append(tool.EndpointPath);
            sb.AppendLine("\") }], details: {} };");
        }
    }

    private static void AppendGetWithQueryParams(StringBuilder sb, ToolDescriptor tool)
    {
        sb.AppendLine("            const qs = new URLSearchParams();");
        foreach (var param in tool.Parameters)
        {
            sb.Append("            if (params.");
            sb.Append(param.Name);
            if (param.TsType == "number")
            {
                sb.AppendLine(" !== undefined) qs.set(\"");
                sb.Append(param.Name);
                sb.AppendLine("\", String(params.");
                sb.Append(param.Name);
                sb.AppendLine("));");
            }
            else
            {
                sb.AppendLine(" != null) qs.set(\"");
                sb.Append(param.Name);
                sb.AppendLine("\", params.");
                sb.Append(param.Name);
                sb.AppendLine(");");
            }
        }
        sb.Append("            const url = qs.toString() ? \"");
        sb.Append(tool.EndpointPath);
        sb.AppendLine("?${qs.toString()}\" : \"");
        sb.Append(tool.EndpointPath);
        sb.AppendLine("\";");
        sb.AppendLine("            return { content: [{ type: \"text\", text: await ildGet(url) }], details: {} };");
    }

    private static void AppendPostExecuteBody(StringBuilder sb, ToolDescriptor tool)
    {
        var bodyParams = tool.Parameters.Where(p => p.IsBodyParam).ToArray();

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
        sb.Append("            return { content: [{ type: \"text\", text: await ildPost(\"");
        sb.Append(tool.EndpointPath);
        sb.AppendLine("\", body) }], details: {} };");
    }

    private static string TsTypeToTypeBox(string tsType) => tsType switch
    {
        "string" => "String",
        "number" => "Number",
        "boolean" => "Boolean",
        _ => "String",
    };

    private static string EscapeTsString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("`", "\\`");
}
