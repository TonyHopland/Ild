namespace ILD.Data.DTOs;

/// <summary>
/// Whether an adapter's CLI accepts a model selector, and what a blank
/// <see cref="Entities.AiProvider.Model"/> means for it. Declared per adapter
/// (ADR-0009) rather than inferred from the provider type, so the UI and the
/// connection-field validation both read one answer instead of keeping their
/// own list of types.
/// </summary>
public enum AdapterModelSupport
{
    /// <summary>The CLI's model flag has not been verified for this adapter: no field, no flag.</summary>
    Unsupported,

    /// <summary>Blank is meaningful and means "whatever the CLI picks by default"; the flag is then omitted entirely.</summary>
    Optional,

    /// <summary>The model is part of the provider's connection details; blank is a validation error.</summary>
    Required,
}

/// <summary>
/// One registered provider type as the AI Providers form sees it, before any
/// provider of that type exists.
/// </summary>
public record AgentAdapterDescriptor(string Type, AdapterModelSupport ModelSupport);
