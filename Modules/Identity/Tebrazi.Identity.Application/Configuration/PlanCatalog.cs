using Tebrazi.SharedKernel.Enums;

namespace Tebrazi.Identity.Application.Configuration;

/// <summary>
/// Port of <c>server/src/config/planConfig.js</c> — the single source of truth for plans.
/// A limit of -1 means unlimited, as in the original.
/// </summary>
public sealed record PlanDefinition(
    SubscriptionPlan Key,
    string Name,
    string Description,
    decimal Price,
    string? BillingPeriod,
    PlanLimits Limits,
    IReadOnlyList<string> Features,
    bool SelfService);

public sealed record PlanLimits(
    int Members,
    int StorageGb,
    int Copilots,
    int Documents,
    int Dashboards);

public static class PlanCatalog
{
    private static readonly Dictionary<SubscriptionPlan, PlanDefinition> Plans = new()
    {
        [SubscriptionPlan.FREE] = new(
            SubscriptionPlan.FREE, "Free", "Get started with the basics", 0m, null,
            new PlanLimits(Members: 3, StorageGb: 1, Copilots: 1, Documents: 50, Dashboards: 2),
            ["Up to 3 team members", "1 GB storage", "1 AI Co-Pilot", "Basic analytics", "Email support"],
            SelfService: true),

        [SubscriptionPlan.STARTER] = new(
            SubscriptionPlan.STARTER, "Starter", "Perfect for small teams getting started", 0m, null,
            new PlanLimits(Members: 5, StorageGb: 5, Copilots: 2, Documents: 200, Dashboards: 5),
            ["Up to 5 team members", "5 GB storage", "2 AI Co-Pilots", "Advanced analytics", "Priority email support"],
            SelfService: true),

        [SubscriptionPlan.PRO] = new(
            SubscriptionPlan.PRO, "Professional", "For growing teams that need more power", 29.99m, "month",
            new PlanLimits(Members: 25, StorageGb: 50, Copilots: 10, Documents: -1, Dashboards: -1),
            [
                "Up to 25 team members", "50 GB storage", "10 AI Co-Pilots",
                "Unlimited documents & dashboards", "Custom integrations", "Priority support"
            ],
            SelfService: false),

        [SubscriptionPlan.ENTERPRISE] = new(
            SubscriptionPlan.ENTERPRISE, "Enterprise", "For organizations that need everything", 99.99m, "month",
            new PlanLimits(Members: -1, StorageGb: -1, Copilots: -1, Documents: -1, Dashboards: -1),
            ["Unlimited team members", "Unlimited storage"],
            SelfService: false)
    };

    public static PlanDefinition Get(SubscriptionPlan plan) => Plans[plan];

    /// <summary>
    /// Parses a requested plan name, falling back to FREE when it is missing or unrecognised —
    /// the same forgiving behaviour as the Node register route.
    /// </summary>
    public static SubscriptionPlan Resolve(string? requested)
        => Enum.TryParse<SubscriptionPlan>(requested, ignoreCase: true, out var parsed)
           && Plans.ContainsKey(parsed)
            ? parsed
            : SubscriptionPlan.FREE;

    /// <summary>
    /// Self-service plans activate immediately; the rest begin a trial and notify the owner.
    /// </summary>
    public static bool IsSelfService(SubscriptionPlan plan) => Plans[plan].SelfService;

    public static IReadOnlyCollection<PlanDefinition> All => Plans.Values;
}
