using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tebrazi.Clinics.Domain.Entities;

namespace Tebrazi.Clinics.Persistence.Configurations;

public sealed class StaffPinConfiguration : IEntityTypeConfiguration<StaffPin>
{
    public void Configure(EntityTypeBuilder<StaffPin> builder)
    {
        builder.ToTable("staff_pins");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(p => p.ClinicId).HasColumnName("clinic_id").HasMaxLength(36).IsRequired();
        builder.Property(p => p.GeneratedByUserId).HasColumnName("generated_by_user_id").HasMaxLength(36).IsRequired();
        builder.Property(p => p.Role).HasColumnName("role").HasMaxLength(30).IsRequired();
        builder.Property(p => p.Pin).HasColumnName("pin").HasMaxLength(10).IsRequired();
        builder.Property(p => p.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(p => p.UsedAt).HasColumnName("used_at");
        builder.Property(p => p.UsedByUserId).HasColumnName("used_by_user_id").HasMaxLength(36);

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();

        // NOT unique. A 4-digit PIN is only unique among LIVE ones, and redeemed rows accumulate
        // forever — a unique index here would exhaust the 9,000-value space and start rejecting
        // valid PINs.
        builder.HasIndex(p => p.Pin).HasDatabaseName("IX_staff_pins_pin");

        builder.HasIndex(p => new { p.ClinicId, p.ExpiresAt })
            .HasDatabaseName("IX_staff_pins_clinic_id_expires_at");
    }
}

public sealed class StaffInvitationConfiguration : IEntityTypeConfiguration<StaffInvitation>
{
    public void Configure(EntityTypeBuilder<StaffInvitation> builder)
    {
        builder.ToTable("staff_invitations");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(i => i.ClinicId).HasColumnName("clinic_id").HasMaxLength(36).IsRequired();
        builder.Property(i => i.InvitedByUserId).HasColumnName("invited_by_user_id").HasMaxLength(36).IsRequired();
        builder.Property(i => i.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
        builder.Property(i => i.Role).HasColumnName("role").HasMaxLength(30).IsRequired();
        builder.Property(i => i.Permissions).HasColumnName("permissions").HasColumnType("nvarchar(max)");
        builder.Property(i => i.Token).HasColumnName("token").HasMaxLength(64).IsRequired();

        builder.Property(i => i.Status)
            .HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(i => i.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(i => i.AcceptedAt).HasColumnName("accepted_at");
        builder.Property(i => i.AcceptedByUserId).HasColumnName("accepted_by_user_id").HasMaxLength(36);

        builder.Property(i => i.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(i => i.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();

        // The token IS the credential — it must be unique, and the lookup must be a seek.
        builder.HasIndex(i => i.Token).HasDatabaseName("UX_staff_invitations_token").IsUnique();

        builder.HasIndex(i => i.Email).HasDatabaseName("IX_staff_invitations_email");
        builder.HasIndex(i => new { i.ClinicId, i.Status })
            .HasDatabaseName("IX_staff_invitations_clinic_id_status");
    }
}
