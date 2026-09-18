using Microsoft.Extensions.Logging;
using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Infrastructure.Shared.Ai;

/// <summary>
/// The placeholder <see cref="IAiGateway"/>. It is registered so that endpoints depending on AI
/// can be built, wired and route-tested before a provider exists — and so their DEGRADED paths
/// are the ones actually exercised, rather than being dead code nobody has run.
///
/// It reports <see cref="IsConfigured"/> false and throws <see cref="AiUnavailableException"/>
/// from every call, which is exactly what the Node gateway does with no API key present. A
/// handler that catches that and returns its documented fallback therefore behaves correctly
/// against both this and a real implementation.
///
/// Replacing it means registering a real gateway AFTER
/// <c>AddInfrastructureShared</c> — <c>TryAdd</c> semantics here mean the last registration of
/// <see cref="IAiGateway"/> wins.
/// </summary>
public sealed class UnconfiguredAiGateway(ILogger<UnconfiguredAiGateway> logger) : IAiGateway
{
    public bool IsConfigured => false;

    public Task<AiChatResult> ChatAsync(AiChatRequest request, CancellationToken ct = default)
        => Unavailable<AiChatResult>(request.Agent ?? "chat");

    public Task<AiTranscriptionResult> TranscribeAsync(
        AiTranscriptionRequest request, CancellationToken ct = default)
        => Unavailable<AiTranscriptionResult>(request.Agent ?? "scribe");

    public Task<AiDrugCheckResult> CheckDrugsAsync(
        AiDrugCheckRequest request, CancellationToken ct = default)
        => Unavailable<AiDrugCheckResult>(request.Agent ?? "drug-check");

    private Task<T> Unavailable<T>(string agent)
    {
        logger.LogWarning(
            "AI gateway is not configured; refusing the {Agent} call. "
            + "Register a real IAiGateway after AddInfrastructureShared to enable it.",
            agent);

        // The Node gateway's own terminal message, so log-grepping across the two backends finds
        // the same string.
        return Task.FromException<T>(
            new AiUnavailableException("AI Gateway: All providers failed. No provider is configured."));
    }
}
