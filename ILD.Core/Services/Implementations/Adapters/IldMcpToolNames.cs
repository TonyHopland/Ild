using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// The tool names the ILD MCP server exposes, read from the
/// <c>[McpServerTool(Name = …)]</c> attributes in its DLL's metadata. Pi's
/// <c>--tools</c> allowlist also filters extension tools, so pi has to name every
/// ILD tool before the server is ever started; reading them here keeps the server
/// the only definition. The DLL is inspected, never loaded, so ILD.Core needs no
/// reference to ILD.McpServer.
/// </summary>
internal static class IldMcpToolNames
{
    private const string ToolAttributeName = "McpServerToolAttribute";

    private static readonly ConcurrentDictionary<string, (DateTime WrittenUtc, IReadOnlyList<string> Names)> Cache = new();

    /// <summary>Returns an empty list when the DLL is missing or is not a readable assembly.</summary>
    public static IReadOnlyList<string> Read(string serverDll)
    {
        var path = Path.GetFullPath(serverDll);
        if (!File.Exists(path))
            return Array.Empty<string>();

        var writtenUtc = File.GetLastWriteTimeUtc(path);
        if (Cache.TryGetValue(path, out var cached) && cached.WrittenUtc == writtenUtc)
            return cached.Names;

        var names = ReadUncached(path);
        Cache[path] = (writtenUtc, names);
        return names;
    }

    private static IReadOnlyList<string> ReadUncached(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return Array.Empty<string>();

            var reader = pe.GetMetadataReader();
            var names = new List<string>();
            foreach (var handle in reader.CustomAttributes)
            {
                var attribute = reader.GetCustomAttribute(handle);
                if (AttributeTypeName(reader, attribute) != ToolAttributeName)
                    continue;

                var value = attribute.DecodeValue(AttributeTypeProvider.Instance);
                foreach (var argument in value.NamedArguments)
                {
                    if (argument.Name == "Name" && argument.Value is string name)
                        names.Add(name);
                }
            }
            return names;
        }
        catch (BadImageFormatException)
        {
            return Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? AttributeTypeName(MetadataReader reader, CustomAttribute attribute)
    {
        var parent = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default(EntityHandle),
        };

        return parent.Kind switch
        {
            HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name),
            HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
            _ => null,
        };
    }

    /// <summary>
    /// Types are only needed to walk the attribute blob; they are reduced to
    /// names. Enum-typed arguments are read as <c>int</c>, the C# default.
    /// </summary>
    private sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly AttributeTypeProvider Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }
}
