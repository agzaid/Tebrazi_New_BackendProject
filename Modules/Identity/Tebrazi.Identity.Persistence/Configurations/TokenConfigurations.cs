using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tebrazi.Identity.Domain.Entities;

namespace Tebrazi.Identity.Persistence.Configurations;

public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(s => s.UserId).HasColumnName("user_id").HasMaxLength(36).IsRequired();
        builder.Property(s => s.Token).HasColumnName("token").HasMaxLength(512).IsRequired();
        builder.Property(s => s.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(s => s.DeviceInfo).HasColumnName("device_info").HasMaxLength(300);
        builder.Property(s => s.IpAddress).HasColumnName("ip_address").HasMaxLength(64);

        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(s => s.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();

        builder.HasIndex(s => s.Token).HasDatabaseName("UX_sessions_token").IsUnique();
        builder.HasIndex(s => s.UserId).HasDatabaseName("IX_sessions_user_id");

        // Expiry sweeps scan on this column; without the index they scan the whole table.
        builder.HasIndex(s => s.ExpiresAt).HasDatabaseName("IX_sessions_expires_at");
    }
}

public sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("password_reset_tokens");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(t => t.UserId).HasColumnName("user_id").HasMaxLength(36).IsRequired();
        builder.Property(t => t.Token).HasColumnName("token").HasMaxLength(128).IsRequired();
        builder.Property(t => t.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(t => t.UsedAt).HasColumnName("used_at");

        builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(t => t.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();

        builder.HasIndex(t => t.Token).HasDatabaseName("UX_password_reset_tokens_token").IsUnique();
        builder.HasIndex(t => t.UserId).HasDatabaseName("IX_password_reset_tokens_user_id");
        builder.HasIndex(t => t.ExpiresAt).HasDatabaseName("IX_password_reset_tokens_expires_at");
    }
}

public sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.ToTable("invitations");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(i => i.OrganizationId).HasColumnName("organization_id").HasMaxLength(36).IsRequired();
        builder.Property(i => i.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
        builder.Property(i => i.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(i => i.Token).HasColumnName("token").HasMaxLength(128).IsRequired();
        builder.Property(i => i.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(i => i.AcceptedAt).HasColumnName("accepted_at");
        builder.Property(i => i.InvitedBy).HasColumnName("invited_by").HasMaxLength(36).IsRequired();

        builder.Property(i => i.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(i => i.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();

        builder.HasIndex(i => i.Token).HasDatabaseName("UX_invitations_token").IsUnique();
        builder.HasIndex(i => i.Email).HasDatabaseName("IX_invitations_email");
        builder.HasIndex(i => i.OrganizationId).HasDatabaseName("IX_invitations_organization_id");
    }
}

public sealed class OtpCodeConfiguration : IEntityTypeConfiguration<OtpCode>
{
    public void Configure(EntityTypeBuilder<OtpCode> builder)
    {
        builder.ToTable("otp_codes");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(o => o.Phone).HasColumnName("phone").HasMaxLength(32).IsRequired();
        builder.Property(o => o.Code).HasColumnName("code").HasMaxLength(10).IsRequired();
        builder.Property(o => o.Purpose).HasColumnName("purpose").HasMaxLength(30).IsRequired();
        builder.Property(o => o.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(o => o.Verified).HasColumnName("verified").HasDefaultValue(false);
        builder.Property(o => o.Attempts).HasColumnName("attempts").HasDefaultValue(0);

        builder.Property(o => o.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(o => o.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();

        builder.HasIndex(o => new { o.Phone, o.Purpose }).HasDatabaseName("IX_otp_codes_phone_purpose");
        builder.HasIndex(o => o.ExpiresAt).HasDatabaseName("IX_otp_codes_expires_at");
    }
}
