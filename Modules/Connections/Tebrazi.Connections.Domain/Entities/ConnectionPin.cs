using Tebrazi.SharedKernel.Base;

namespace Tebrazi.Connections.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>ConnectionPin</c> model (table <c>connection_pins</c>,
/// schema.prisma:549-563) — the short-lived four-digit code a physician reads out so a patient can
/// pair instantly, without a handshake.
///
/// <para><b>This is NOT the clinic STAFF pin.</b> Clinics already owns an unrelated
/// <c>StaffPin</c> (<c>staff_pins</c>, driven by
/// <c>Modules/Clinics/…/UseCases/Commands/StaffPinCommands.cs</c>) that lets a receptionist unlock
/// a shared terminal. Different table, different lifetime, different meaning. Do not reach for
/// one when you want the other.</para>
///
/// <para>Lifecycle, from <c>connections.js</c>:</para>
/// <list type="number">
/// <item><c>POST /generate-pin</c> force-expires every live pin the physician holds
/// (<see cref="ForceExpire"/>, connections.js:1546-1553), then inserts a new one with
/// <c>expiresAt = now + 5 minutes</c> (:1571-1581).</item>
/// <item><c>POST /connect-by-pin</c> looks the code up among pins that are unused AND unexpired
/// (:1618-1624) and stamps <see cref="MarkUsed"/> (:1644-1647, :1684-1687).</item>
/// </list>
///
/// <para>There is no <c>updatedAt</c> column in the Prisma model, which is why this derives from
/// <see cref="ImmutableEntity{TKey}"/> rather than <c>MutableEntity</c>: the two mutations it does
/// have (expire, mark-used) are explicit column writes, not audited modifications.</para>
/// </summary>
public sealed class ConnectionPin : ImmutableEntity<string>
{
    private ConnectionPin() { }

    /// <summary>The physician the pin connects TO. A USER id.</summary>
    public string PhysicianUserId { get; private set; } = null!;

    /// <summary>
    /// The clinic the pin was issued at, or null. Nullable in the Node schema and written as
    /// <c>clinicId || null</c> from the body or the <c>X-Clinic-Id</c> header
    /// (connections.js:1518, :1577). Nothing reads it back on the connect path.
    /// </summary>
    public string? ClinicId { get; private set; }

    /// <summary>
    /// The code itself — four decimal digits as TEXT, e.g. <c>"4821"</c>, never a number. Leading
    /// zeros are impossible because Node generates in the range 1000-9999
    /// (connections.js:1559), but the column stays textual because the connect route compares it
    /// to the raw request body.
    /// </summary>
    public string Pin { get; private set; } = null!;

    /// <summary>Absolute expiry. <c>now + 5 minutes</c> at creation; moved to <c>now</c> by
    /// <see cref="ForceExpire"/>.</summary>
    public DateTime ExpiresAt { get; private set; }

    /// <summary>When the pin was redeemed. Null while unused. A used pin never becomes reusable.</summary>
    public DateTime? UsedAt { get; private set; }

    /// <summary>The patient who redeemed it. Null while unused.</summary>
    public string? UsedByUserId { get; private set; }

    /// <summary>The five-minute window <c>POST /generate-pin</c> opens (connections.js:1571-1572).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The value <c>POST /generate-pin</c> echoes as <c>expiresInSeconds</c>
    /// (connections.js:1593). It is a HARD-CODED 300 in the response, not a recomputation of
    /// <see cref="ExpiresAt"/> minus now — reproduce the literal, not the arithmetic.
    /// </summary>
    public const int LifetimeSeconds = 300;

    /// <param name="physicianUserId">The physician the pin connects to. A USER id.</param>
    /// <param name="pin">Four decimal digits as text.</param>
    /// <param name="expiresAt">Absolute expiry — <c>now + <see cref="Lifetime"/></c> at the one call site.</param>
    /// <param name="clinicId">The issuing clinic, or null.</param>
    public static ConnectionPin Create(
        string physicianUserId,
        string pin,
        DateTime expiresAt,
        string? clinicId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicianUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        return new ConnectionPin
        {
            Id = Guid.NewGuid().ToString(),
            PhysicianUserId = physicianUserId,
            ClinicId = clinicId,
            Pin = pin,
            ExpiresAt = expiresAt
        };
    }

    /// <summary>
    /// Generates one candidate code, exactly as
    /// <c>String(Math.floor(1000 + Math.random() * 9000))</c> does (connections.js:1559): a
    /// uniform integer in <b>1000-9999 inclusive</b>, rendered with no padding.
    ///
    /// <para><see cref="Random.Shared"/> rather than a cryptographic source, matching
    /// <c>Math.random()</c>. The pin is a five-minute pairing convenience read out loud in a
    /// consulting room, not a secret; the collision loop in the route is what keeps it unique
    /// among LIVE pins, and there are at most 9000 of those.</para>
    /// </summary>
    public static string GeneratePin()
        => Random.Shared.Next(1000, 10000).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// True when the pin is still redeemable at <paramref name="now"/> — unused AND not yet
    /// expired, which is the <c>{ usedAt: null, expiresAt: { gt: new Date() } }</c> filter both
    /// PIN routes apply (connections.js:1549-1550, :1561, :1621-1622).
    ///
    /// <para>The bound is STRICTLY greater than: a pin whose <c>expiresAt</c> equals now is
    /// already dead. That is what makes <see cref="ForceExpire"/> effective.</para>
    /// </summary>
    /// <param name="now">The instant to test against, normally <c>DateTime.UtcNow</c>.</param>
    public bool IsRedeemable(DateTime now) => UsedAt is null && ExpiresAt > now;

    /// <summary>
    /// The <c>data: { expiresAt: new Date() }</c> force-expire that <c>POST /generate-pin</c>
    /// applies to a physician's existing live pins before issuing a new one
    /// (connections.js:1546-1553). Sets expiry to <paramref name="now"/>, which
    /// <see cref="IsRedeemable"/>'s strict comparison then treats as dead.
    /// </summary>
    /// <param name="now">The expiry instant to write.</param>
    public void ForceExpire(DateTime now) => ExpiresAt = now;

    /// <summary>
    /// Redeems the pin (connections.js:1644-1647 and :1684-1687):
    /// <c>{ usedAt: new Date(), usedByUserId }</c>.
    ///
    /// <para>Written unconditionally in both branches — including the branch where the patient was
    /// ALREADY connected, so a pin is burned even when it changes nothing.</para>
    /// </summary>
    /// <param name="usedAt">The redemption instant.</param>
    /// <param name="usedByUserId">The redeeming patient's USER id.</param>
    public void MarkUsed(DateTime usedAt, string usedByUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usedByUserId);
        UsedAt = usedAt;
        UsedByUserId = usedByUserId;
    }
}
