namespace Tebrazi.SharedKernel.Abstractions;

/// <summary>
/// Port of <c>server/src/services/aiGateway.js</c> — the single door to every model call.
///
/// The Node gateway is multi-provider with fallback (OpenAI, then Gemini, then MicroMind), an
/// in-memory prompt cache, a retry wrapper and an audit row per call. None of that is ported
/// yet. What IS ported is the SHAPE, so the endpoints that depend on it can be written now and
/// keep working unchanged when a real implementation lands.
///
/// <para><b>Throwing is normal.</b> The Node gateway throws when every provider fails
/// ("AI Gateway: All providers failed"), and most call sites catch it and degrade — returning the
/// visit unchanged, or an empty suggestion list, rather than a 500. Handlers must copy that
/// endpoint by endpoint; the failure response is part of each endpoint's contract, not a detail
/// the gateway can decide.</para>
/// </summary>
public interface IAiGateway
{
    /// <summary>
    /// A chat completion. Rejects a prompt over 50,000 characters before spending anything on
    /// it, which is the Node gateway's own cost guard.
    /// </summary>
    Task<AiChatResult> ChatAsync(AiChatRequest request, CancellationToken ct = default);

    /// <summary>Speech to text. OpenAI Whisper only in the Node gateway.</summary>
    Task<AiTranscriptionResult> TranscribeAsync(AiTranscriptionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Drug interaction checking. Multi-stage in Node: OpenFDA drug labels, then OpenFDA adverse
    /// events, then LLM clinical reasoning, then an allergy cross-check.
    /// </summary>
    Task<AiDrugCheckResult> CheckDrugsAsync(AiDrugCheckRequest request, CancellationToken ct = default);

    /// <summary>
    /// False when no provider is configured. Lets a handler choose its documented degraded
    /// response without paying for a call it knows will fail.
    /// </summary>
    bool IsConfigured { get; }
}

/// <param name="Agent">Names the caller in the audit trail, e.g. "scribe", "coder".</param>
public sealed record AiChatRequest(
    string System,
    string User,
    string? Model = null,
    double? Temperature = null,
    int? MaxTokens = null,
    bool UseCache = true,
    string? Agent = null,
    string? UserId = null);

/// <param name="Text">The completion body. Empty string, never null, matching the Node gateway.</param>
/// <param name="Cached">True when the prompt cache answered and no provider was called.</param>
public sealed record AiChatResult(
    string Text,
    string Provider,
    string? Model,
    int? TokensUsed = null,
    decimal? CostUsd = null,
    bool Cached = false);

public sealed record AiTranscriptionRequest(
    byte[] Audio,
    string? FileName = null,
    string? Language = null,
    string? Agent = null,
    string? UserId = null);

public sealed record AiTranscriptionResult(
    string Text,
    double Duration,
    string? Language,
    string Provider,
    string? Model,
    decimal? CostUsd = null);

public sealed record AiDrugCheckRequest(
    IReadOnlyList<string> Drugs,
    IReadOnlyList<string>? PatientAllergies = null,
    IReadOnlyList<string>? PatientConditions = null,
    string? Agent = null,
    string? UserId = null);

/// <param name="Raw">
/// The gateway's own JSON, passed through verbatim. The interaction payload is deeply nested and
/// the client reads it directly, so re-modelling it here would only create a second shape to
/// keep in sync.
/// </param>
public sealed record AiDrugCheckResult(string Raw, string Provider, bool Cached = false);

/// <summary>
/// Thrown when no provider could answer. Mirrors the Node gateway's terminal error, and is what
/// a handler catches to produce its documented degraded response.
/// </summary>
public sealed class AiUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
