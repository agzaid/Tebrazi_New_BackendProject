using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tebrazi.Identity.Domain.Entities;

namespace Tebrazi.Identity.Persistence.Configurations;

public sealed class PhysicianProfileConfiguration : IEntityTypeConfiguration<PhysicianProfile>
{
    public void Configure(EntityTypeBuilder<PhysicianProfile> builder)
    {
        builder.ToTable("physician_profiles");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(p => p.UserId).HasColumnName("user_id").HasMaxLength(36).IsRequired();
        builder.Property(p => p.LicenseNumber).HasColumnName("license_number").HasMaxLength(100).IsRequired();
        builder.Property(p => p.Specialty).HasColumnName("specialty").HasMaxLength(120).IsRequired();
        builder.Property(p => p.Qualifications).HasColumnName("qualifications").HasMaxLength(1000);

        // @db.Text in Prisma — unbounded, and the scratchpad in particular holds free-form notes.
        builder.Property(p => p.Bio).HasColumnName("bio").HasColumnType("nvarchar(max)");
        builder.Property(p => p.ScratchpadNotes).HasColumnName("scratchpad_notes").HasColumnType("nvarchar(max)");

        builder.Property(p => p.YearsOfExperience).HasColumnName("years_of_experience");
        builder.Property(p => p.Verified).HasColumnName("verified").HasDefaultValue(false);
        builder.Property(p => p.VerifiedAt).HasColumnName("verified_at");

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        builder.Property(p => p.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        builder.HasIndex(p => p.UserId).HasDatabaseName("UX_physician_profiles_user_id").IsUnique();
        builder.HasIndex(p => p.Specialty).HasDatabaseName("IX_physician_profiles_specialty");
    }
}

