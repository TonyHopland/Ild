using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tool that hands an agent everything it needs to author an
/// <c>ild.config.json</c> preview config for a repository being enrolled into
/// ILD — without that agent having the ILD source tree checked out.
///
/// The returned document is the single source of truth for the schema, the
/// supported template tokens, and a working starter config. It is kept honest
/// by <c>WorktreePreviewServiceConfigSchemaTests</c>, which feeds the embedded
/// starter through the real <see cref="ILD.Core.Services.Interfaces.IWorktreePreviewService"/>
/// parser and validator, so this constant cannot silently drift from the code
/// that actually consumes the file.
/// </summary>
[McpServerToolType]
public sealed class ConfigTools
{
    [McpServerTool(Name = "get_preview_config_schema")]
    [Description(
        "Returns the schema, template-token reference, and a working starter for the " +
        "ild.config.json preview config that a repository being enrolled into ILD should " +
        "ship in its root. Call this before authoring ild.config.json — the response is " +
        "version-matched to the running ILD instance, so you do not need the ILD source. " +
        "The response is a JSON object with: summary, templateTokens, fieldReference, " +
        "schema (JSON Schema), and starter (a ready-to-edit example config).")]
    public string GetPreviewConfigSchema() => SchemaDocument;

    /// <summary>
    /// The canonical schema document. Property names and the required-field rules
    /// here mirror <c>WorktreePreviewService</c>'s config model and
    /// <c>ValidateProfile</c>; the starter is exercised by the round-trip test.
    /// </summary>
    internal const string SchemaDocument = """
    {
      "configFileName": "ild.config.json",
      "location": "the repository root of the project being enrolled",
      "summary": "ild.config.json declares how ILD installs dependencies and runs a project inside an isolated git worktree to produce a live preview. Each profile has install steps (run once to provision the worktree) and long-running services (started for the preview). ILD assigns a free port to each service, binds it, and waits for its healthUrl before considering the preview ready.",
      "howItWorks": [
        "Place the file at the repository root; ILD reads only the 'preview' section.",
        "A profile groups 'install' steps and 'services'. 'defaultProfile' selects one when none is requested; if omitted, the first profile is used.",
        "Each service declares a 'port' alias; ILD allocates an actual free port (preferring 'suggestedPort' when free) and exposes it to that service's command/env/healthUrl as ${PORT}.",
        "Reference another service's allocated port with ${PORT:<alias>} to wire services together (e.g. a frontend proxying to the backend).",
        "Commands run under /bin/sh from 'cwd' (relative to the worktree root). Install steps run before any service starts.",
        "A service is ready when an HTTP GET of its resolved 'healthUrl' succeeds. Set 'public': true on the user-facing service."
      ],
      "templateTokens": {
        "${WORKTREE}": "Absolute path to the prepared git worktree root.",
        "${STATE_DIR}": "Absolute path to a per-worktree state directory that persists across preview restarts. Use it for data/scratch that must live outside the repo tree.",
        "${HOST}": "Bind address for services to listen on (0.0.0.0).",
        "${PUBLIC_HOST}": "Externally reachable host used to build public URLs.",
        "${PORT}": "The port ILD allocated to THIS service. Valid only inside that service's own command, env values, and healthUrl.",
        "${PORT:<alias>}": "The port ILD allocated to another service, looked up by its 'port' alias. Use to connect services to each other."
      },
      "fieldReference": {
        "preview.defaultProfile": "Optional. Name of the profile to use by default.",
        "preview.profiles": "Required. Map of profile name to { install, services }.",
        "install[].cwd": "Optional. Working directory relative to the worktree root. Defaults to the worktree root.",
        "install[].command": "Required. Shell command run once to provision the worktree (e.g. dependency install).",
        "install[].env": "Optional. Extra environment variables for the step.",
        "services[].name": "Required. Unique (case-insensitive) service name within the profile.",
        "services[].command": "Required. Shell command that starts the long-running service in the foreground.",
        "services[].cwd": "Optional. Working directory relative to the worktree root.",
        "services[].port": "Required. Port alias; ILD allocates a real free port for it and surfaces it as ${PORT}.",
        "services[].suggestedPort": "Optional but recommended. Preferred port (used when free); must be > 0.",
        "services[].healthUrl": "Required. URL polled until it responds; marks the service ready. Typically uses ${PORT}.",
        "services[].public": "Optional. Set true on the user-facing service whose URL is surfaced to the user.",
        "services[].publicUrl": "Optional. Override for the advertised public URL; may use ${PUBLIC_HOST} and ${PORT}.",
        "services[].env": "Optional. Environment variables for the service; values may use any template token."
      },
      "schema": {
        "$schema": "http://json-schema.org/draft-07/schema#",
        "title": "ild.config.json",
        "type": "object",
        "properties": {
          "preview": {
            "type": "object",
            "properties": {
              "defaultProfile": { "type": "string" },
              "profiles": {
                "type": "object",
                "minProperties": 1,
                "additionalProperties": { "$ref": "#/definitions/profile" }
              }
            },
            "required": ["profiles"],
            "additionalProperties": false
          }
        },
        "required": ["preview"],
        "definitions": {
          "profile": {
            "type": "object",
            "properties": {
              "install": { "type": "array", "items": { "$ref": "#/definitions/command" } },
              "services": {
                "type": "array",
                "minItems": 1,
                "items": { "$ref": "#/definitions/service" }
              }
            },
            "required": ["services"],
            "additionalProperties": false
          },
          "command": {
            "type": "object",
            "properties": {
              "cwd": { "type": "string" },
              "command": { "type": "string", "minLength": 1 },
              "env": { "type": "object", "additionalProperties": { "type": "string" } }
            },
            "required": ["command"],
            "additionalProperties": false
          },
          "service": {
            "type": "object",
            "properties": {
              "name": { "type": "string", "minLength": 1 },
              "command": { "type": "string", "minLength": 1 },
              "cwd": { "type": "string" },
              "env": { "type": "object", "additionalProperties": { "type": "string" } },
              "port": { "type": "string", "minLength": 1 },
              "suggestedPort": { "type": "integer", "minimum": 1 },
              "healthUrl": { "type": "string", "minLength": 1 },
              "public": { "type": "boolean" },
              "publicUrl": { "type": "string" }
            },
            "required": ["name", "command", "port", "healthUrl"],
            "additionalProperties": false
          }
        }
      },
      "starter": {
        "preview": {
          "defaultProfile": "app",
          "profiles": {
            "app": {
              "install": [
                { "cwd": ".", "command": "echo 'TODO: replace with your install command, e.g. npm ci or dotnet restore'" }
              ],
              "services": [
                {
                  "name": "backend",
                  "cwd": ".",
                  "command": "echo 'TODO: replace with the command that starts your backend in the foreground'",
                  "port": "backend",
                  "suggestedPort": 5000,
                  "healthUrl": "http://127.0.0.1:${PORT}/health",
                  "env": { "ASPNETCORE_URLS": "http://${HOST}:${PORT}" }
                },
                {
                  "name": "app",
                  "cwd": ".",
                  "command": "echo 'TODO: replace with the command that starts your frontend dev server in the foreground'",
                  "port": "frontend",
                  "suggestedPort": 3000,
                  "healthUrl": "http://127.0.0.1:${PORT}/",
                  "public": true,
                  "env": { "API_PROXY_TARGET": "http://127.0.0.1:${PORT:backend}" }
                }
              ]
            }
          }
        }
      }
    }
    """;
}
