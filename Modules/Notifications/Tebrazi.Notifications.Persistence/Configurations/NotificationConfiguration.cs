using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tebrazi.Notifications.Domain.Entities;

namespace Tebrazi.Notifications.Persistence.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");

        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(n => n.UserId).HasColumnName("user_id").HasMaxLength(36).IsRequired();
        builder.Property(n => n.Type).HasColumnName("type").HasMaxLength(64).IsRequired();
        builder.Property(n => n.Title).HasColumnName("title").HasMaxLength(300).IsRequired();
        builder.Property(n => n.Message).HasColumnName("message").HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(n => n.Data).HasColumnName("data").HasColumnType("nvarchar(max)");

        builder.Property(n => n.IsRead).HasColumnName("is_read").HasDefaultValue(false).IsRequired();
        builder.Property(n => n.ReadAt).HasColumnName("read_at");

        builder.Property(n => n.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(n => n.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();

        builder.HasIndex(n => n.UserId).HasDatabaseName("IX_notifications_user_id");
        builder.HasIndex(n => n.IsRead).HasDatabaseName("IX_notifications_is_read");
        builder.HasIndex(n => n.Type).HasDatabaseName("IX_notifications_type");
        builder.HasIndex(n => n.CreatedAt).HasDatabaseName("IX_notifications_created_at");

        // The bell badge reads exactly this pair on every page load.
        builder.HasIndex(n => new { n.UserId, n.IsRead }).HasDatabaseName("IX_notifications_user_id_is_read");
    }
}
