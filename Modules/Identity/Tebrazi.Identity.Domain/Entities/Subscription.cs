using Tebrazi.SharedKernel.Base;
using Tebrazi.SharedKernel.Enums;

namespace Tebrazi.Identity.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>Subscription</c> model (table <c>subscriptions</c>). One per
/// organization. Self-service plans are created ACTIVE with no practical expiry; paid plans
/// start as a 14-day TRIALING period, matching the Node registration route.
/// </summary>
public sealed class Subscription : MutableEntity<string>
{
    private Subscription() { }

    public string OrganizationId { get; private set; } = null!;
    public SubscriptionPlan Plan { get; private set; } = SubscriptionPlan.FREE;
    public SubscriptionStatus Status { get; private set; } = SubscriptionStatus.ACTIVE;
    public DateTime CurrentPeriodStart { get; private set; }
    public DateTime CurrentPeriodEnd { get; private set; }
    public bool CancelAtPeriodEnd { get; private set; }
    public string? StripeCustomerId { get; private set; }
    public string? StripeSubscriptionId { get; private set; }

    public Organization Organization { get; private set; } = null!;

    public static Subscription Create(
        string organizationId,
        SubscriptionPlan plan,
        SubscriptionStatus status,
        DateTime periodStart,
        DateTime periodEnd)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            OrganizationId = organizationId,
            Plan = plan,
            Status = status,
            CurrentPeriodStart = periodStart,
            CurrentPeriodEnd = periodEnd
        };

    public void ChangePlan(SubscriptionPlan plan, SubscriptionStatus status)
    {
        Plan = plan;
        Status = status;
    }

    public void SetCancelAtPeriodEnd(bool cancel) => CancelAtPeriodEnd = cancel;
}
