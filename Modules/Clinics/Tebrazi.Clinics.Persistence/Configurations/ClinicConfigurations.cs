using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tebrazi.Clinics.Domain.Entities;

namespace Tebrazi.Clinics.Persistence.Configurations;

public sealed class ClinicConfiguration : IEntityTypeConfiguration<Clinic>
{
    public void Configure(EntityTypeBuilder<Clinic> builder)
    {
        builder.ToTable("clinics");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        // Cross-module foreign keys, held as plain columns. No HasOne() to Organization or
        // PhysicianProfile: those entities belong to Identity's model, not this one.
        builder.Property(c => c.OrganizationId).HasColumnName("organization_id").HasMaxLength(36).IsRequired();
        builder.Property(c => c.PhysicianId).HasColumnName("physician_id").HasMaxLength(36).IsRequired();

        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(c => c.Address).HasColumnName("address").HasMaxLength(500);
        builder.Property(c => c.City).HasColumnName("city").HasMaxLength(120);
        builder.Property(c => c.Country).HasColumnName("country").HasMaxLength(120);
        builder.Property(c => c.Phone).HasColumnName("phone").HasMaxLength(32);
        builder.Property(c => c.Email).HasColumnName("email").HasMaxLength(320);
        builder.Property(c => c.WorkingHours).HasColumnName("working_hours").HasColumnType("nvarchar(max)");
        builder.Property(c => c.Specialty).HasColumnName("specialty").HasMaxLength(120);
        builder.Property(c => c.Logo).HasColumnName("logo").HasMaxLength(500);
        builder.Property(c => c.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(c => c.AllowPatientBooking).HasColumnName("allow_patient_booking").HasDefaultValue(true);
        builder.Property(c => c.TwilioPhoneNumber).HasColumnName("twilio_phone_number").HasMaxLength(32);
        builder.Property(c => c.TwilioWhatsAppNumber).HasColumnName("twilio_whatsapp_number").HasMaxLength(32);
        builder.Property(c => c.ConsultationFee).HasColumnName("consultation_fee").HasDefaultValue(300d);
        builder.Property(c => c.FollowUpFee).HasColumnName("follow_up_fee").HasDefaultValue(200d);

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");
        builder.Property(c => c.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        builder.HasIndex(c => c.OrganizationId).HasDatabaseName("IX_clinics_organization_id");
        builder.HasIndex(c => c.PhysicianId).HasDatabaseName("IX_clinics_physician_id");

        // The clinic list and switcher both filter on (physician, active) — covered together
        // rather than as two separate seeks.
        builder.HasIndex(c => new { c.PhysicianId, c.IsActive })
            .HasDatabaseName("IX_clinics_physician_id_is_active");

        // The public directory filters active clinics by specialty and city.
        builder.HasIndex(c => new { c.IsActive, c.Specialty })
            .HasDatabaseName("IX_clinics_is_active_specialty");

        builder.HasMany(c => c.StaffMembers)
            .WithOne(s => s.Clinic)
            .HasForeignKey(s => s.ClinicId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(c => c.StaffPins)
            .WithOne(p => p.Clinic)
            .HasForeignKey(p => p.ClinicId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(c => c.StaffInvitations)
            .WithOne(i => i.Clinic)
            .HasForeignKey(i => i.ClinicId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ClinicStaffConfiguration : IEntityTypeConfiguration<ClinicStaff>
{
    public void Configure(EntityTypeBuilder<ClinicStaff> builder)
    {
        builder.ToTable("clinic_staff");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(s => s.ClinicId).HasColumnName("clinic_id").HasMaxLength(36).IsRequired();
        builder.Property(s => s.UserId).HasColumnName("user_id").HasMaxLength(36).IsRequired();

        // A string, not an enum: the Node schema stores the role as free text with a default.
        builder.Property(s => s.Role).HasColumnName("role").HasMaxLength(30).IsRequired();

        builder.Property(s => s.Permissions).HasColumnName("permissions").HasColumnType("nvarchar(max)");
        builder.Property(s => s.IsActive).HasColumnName("is_active").HasDefaultValue(true);

        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(s => s.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();

        // One membership per person per clinic. Enforced in the DATABASE, which is the only
        // place it can be: the join-by-PIN and invite paths both check first, and both races
        // would otherwise create a duplicate.
        builder.HasIndex(s => new { s.ClinicId, s.UserId })
            .HasDatabaseName("UX_clinic_staff_clinic_user")
            .IsUnique();

        // The permission evaluator runs this lookup on every clinic-scoped request.
        builder.HasIndex(s => new { s.UserId, s.IsActive })
            .HasDatabaseName("IX_clinic_staff_user_id_is_active");
    }
}
