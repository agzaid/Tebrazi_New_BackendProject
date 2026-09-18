using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tebrazi.Identity.Domain.Entities;

namespace Tebrazi.Identity.Persistence.Configurations;

public sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(o => o.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(o => o.Slug).HasColumnName("slug").HasMaxLength(220).IsRequired();
        builder.Property(o => o.Logo).HasColumnName("logo").HasMaxLength(500);

        // Was Json? in Postgres. Held as an opaque JSON document; the domain never reads into it.
        builder.Property(o => o.Settings).HasColumnName("settings").HasColumnType("nvarchar(max)");

        builder.Property(o => o.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(o => o.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(o => o.UpdatedAt).HasColumnName("updated_at");
        builder.Property(o => o.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        // The slug appears in URLs and the registration route loops until it finds a free one.
        // This unique index is what actually guarantees that, not the loop.
        builder.HasIndex(o => o.Slug).HasDatabaseName("UX_organizations_slug").IsUnique();

        builder.HasOne(o => o.Subscription)
            .WithOne(s => s.Organization)
            .HasForeignKey<Subscription>(s => s.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OrganizationMemberConfiguration : IEntityTypeConfiguration<OrganizationMember>
{
    public void Configure(EntityTypeBuilder<OrganizationMember> builder)
    {
        builder.ToTable("organization_members");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(m => m.OrganizationId).HasColumnName("organization_id").HasMaxLength(36).IsRequired();
        builder.Property(m => m.UserId).HasColumnName("user_id").HasMaxLength(36).IsRequired();
        builder.Property(m => m.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(m => m.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(m => m.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();

        builder.HasIndex(m => new { m.OrganizationId, m.UserId })
            .HasDatabaseName("UX_organization_members_org_user")
            .IsUnique();

        builder.HasIndex(m => m.UserId).HasDatabaseName("IX_organization_members_user_id");

        builder.HasOne(m => m.Organization)
            .WithMany(o => o.Members)
            .HasForeignKey(m => m.OrganizationId)
            // Restrict, not Cascade: the User -> Members path already cascades, and two cascade
            // paths into one table is the SQL Server "multiple cascade paths" error.
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("subscriptions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(s => s.OrganizationId).HasColumnName("organization_id").HasMaxLength(36).IsRequired();
        builder.Property(s => s.Plan).HasColumnName("plan").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.CurrentPeriodStart).HasColumnName("current_period_start").IsRequired();
        builder.Property(s => s.CurrentPeriodEnd).HasColumnName("current_period_end").IsRequired();
        builder.Property(s => s.CancelAtPeriodEnd).HasColumnName("cancel_at_period_end").HasDefaultValue(false);
        builder.Property(s => s.StripeCustomerId).HasColumnName("stripe_customer_id").HasMaxLength(100);
        builder.Property(s => s.StripeSubscriptionId).HasColumnName("stripe_subscription_id").HasMaxLength(100);

        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(s => s.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        builder.Property(s => s.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        builder.HasIndex(s => s.OrganizationId).HasDatabaseName("UX_subscriptions_organization_id").IsUnique();
        builder.HasIndex(s => s.Status).HasDatabaseName("IX_subscriptions_status");
        builder.HasIndex(s => s.Plan).HasDatabaseName("IX_subscriptions_plan");
    }
}
