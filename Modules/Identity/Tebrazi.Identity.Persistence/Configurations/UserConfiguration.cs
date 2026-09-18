using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tebrazi.Identity.Domain.Entities;

namespace Tebrazi.Identity.Persistence.Configurations;

/// <summary>
/// Maps <see cref="User"/> to the <c>users</c> table. Table and column names are the Prisma
/// <c>@@map</c>/<c>@map</c> names so a later data migration out of Postgres is a straight copy.
///
/// Every index declared here is created by an EF migration. Unlike the KACCC codebase — where
/// indexes were declared in EF, never migrated, and therefore did not exist — these are real.
/// That matters most for the unique ones: EF does not enforce uniqueness client-side, so a
/// unique index that exists only in the model protects nothing.
/// </summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(u => u.Email).HasColumnName("email").HasMaxLength(320);
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(100).IsRequired();
        builder.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(u => u.Phone).HasColumnName("phone").HasMaxLength(32);
        builder.Property(u => u.ProfilePictureUrl).HasColumnName("profile_picture_url").HasMaxLength(500);

        // Enums persist as their NAMES. The React client compares against the literal strings
        // ("PHYSICIAN", "ADMIN"), and storing ordinals would make every raw SQL report wrong
        // the moment a member is inserted into the middle of an enum.
        builder.Property(u => u.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(u => u.UserType).HasColumnName("user_type").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(u => u.Active).HasColumnName("active").HasDefaultValue(true);
        builder.Property(u => u.CurrentOrganizationId).HasColumnName("current_organization_id").HasMaxLength(36);
        builder.Property(u => u.SubscriptionTier).HasColumnName("subscription_tier").HasMaxLength(30).HasDefaultValue("FREE");

        builder.Property(u => u.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(u => u.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(u => u.UpdatedAt).HasColumnName("updated_at");
        builder.Property(u => u.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        // Phone is globally unique and is the primary identifier for phone-first patients.
        // Filtered so the many NULL phones do not collide — SQL Server treats NULLs as equal
        // in a unique index, unlike Postgres, so without the filter only ONE user could have
        // a null phone.
        builder.HasIndex(u => u.Phone)
            .HasDatabaseName("UX_users_phone")
            .IsUnique()
            .HasFilter("[phone] IS NOT NULL");

        // The composite the Node schema actually keys on. NOT email alone: one address may hold
        // both a physician and a patient account.
        builder.HasIndex(u => new { u.Email, u.UserType })
            .HasDatabaseName("UX_users_email_user_type")
            .IsUnique()
            .HasFilter("[email] IS NOT NULL");

        builder.HasIndex(u => u.Email).HasDatabaseName("IX_users_email");
        builder.HasIndex(u => u.Role).HasDatabaseName("IX_users_role");
        builder.HasIndex(u => u.UserType).HasDatabaseName("IX_users_user_type");
        builder.HasIndex(u => u.CurrentOrganizationId).HasDatabaseName("IX_users_current_organization_id");

        builder.HasMany(u => u.Sessions)
            .WithOne(s => s.User)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.PasswordResetTokens)
            .WithOne(t => t.User)
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(u => u.Memberships)
            .WithOne(m => m.User)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(u => u.PhysicianProfile)
            .WithOne(p => p.User)
            .HasForeignKey<PhysicianProfile>(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // There is deliberately NO relationship to PatientProfile. That entity belongs to the
        // Patients module, which owns the patient's clinical record and reaches back to the user
        // by id. Mapping it here would put one entity in two EF models.
    }
}
