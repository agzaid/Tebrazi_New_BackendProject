using Tebrazi.SharedKernel.Base;
using Tebrazi.SharedKernel.Enums;

namespace Tebrazi.Identity.Domain.Entities;

/// <summary>
/// Port of the Prisma <c>Organization</c> model (table <c>organizations</c>) — the tenant.
/// <see cref="Settings"/> was <c>Json?</c> in Postgres and is stored here as an nvarchar(max)
/// JSON document; it is opaque to the domain.
/// </summary>
public sealed class Organization : MutableEntity<string>
{
    private Organization() { }

    public string Name { get; private set; } = null!;
    public string Slug { get; private set; } = null!;
    public string? Logo { get; private set; }
    public string? Settings { get; private set; }

    public ICollection<OrganizationMember> Members { get; private set; } = [];
    public Subscription? Subscription { get; private set; }

    public static Organization Create(string name, string slug, string? settings = "{}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        return new Organization
        {
            Id = Guid.NewGuid().ToString(),
            Name = name.Trim(),
            Slug = slug,
            Settings = settings
        };
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public void SetLogo(string? logo) => Logo = logo;

    public void SetSettings(string? settings) => Settings = settings;

    /// <summary>
    /// Builds the slug the way the Node registration route does: lowercase, every run of
    /// non-alphanumerics collapsed to a single hyphen, leading and trailing hyphens trimmed.
    /// Uniqueness is the caller's problem — it appends "-1", "-2", ... until the slug is free.
    /// </summary>
    public static string Slugify(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var builder = new System.Text.StringBuilder(name.Length);
        var pendingHyphen = false;

        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                if (pendingHyphen && builder.Length > 0) builder.Append('-');
                pendingHyphen = false;
                builder.Append(ch);
            }
            else
            {
                pendingHyphen = true;
            }
        }

        return builder.ToString();
    }
}

/// <summary>Port of the Prisma <c>OrganizationMember</c> model (table <c>organization_members</c>).</summary>
public sealed class OrganizationMember : ImmutableEntity<string>
{
    private OrganizationMember() { }

    public string OrganizationId { get; private set; } = null!;
    public string UserId { get; private set; } = null!;
    public OrgRole Role { get; private set; } = OrgRole.MEMBER;

    public Organization Organization { get; private set; } = null!;
    public User User { get; private set; } = null!;

    public static OrganizationMember Create(string organizationId, string userId, OrgRole role)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            OrganizationId = organizationId,
            UserId = userId,
            Role = role
        };
}
