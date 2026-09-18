using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tebrazi.Patients.Domain.Entities;

namespace Tebrazi.Patients.Persistence.Configurations;

public sealed class PatientProfileConfiguration : IEntityTypeConfiguration<PatientProfile>
{
    public void Configure(EntityTypeBuilder<PatientProfile> builder)
    {
        builder.ToTable("patient_profiles");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        // Cross-module key onto users.id. No navigation — the user belongs to Identity's model.
        builder.Property(p => p.UserId).HasColumnName("user_id").HasMaxLength(36).IsRequired();

        builder.Property(p => p.DateOfBirth).HasColumnName("date_of_birth");
        builder.Property(p => p.Gender).HasColumnName("gender").HasConversion<string>().HasMaxLength(10);
        builder.Property(p => p.Nationality).HasColumnName("nationality").HasMaxLength(100);
        builder.Property(p => p.BloodType).HasColumnName("blood_type").HasMaxLength(10);
        builder.Property(p => p.EmergencyContact).HasColumnName("emergency_contact").HasMaxLength(200);
        builder.Property(p => p.EmergencyPhone).HasColumnName("emergency_phone").HasMaxLength(32);
        builder.Property(p => p.Address).HasColumnName("address").HasMaxLength(500);
        builder.Property(p => p.WhatsappNumber).HasColumnName("whatsapp_number").HasMaxLength(32);

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        builder.Property(p => p.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        // One profile per user. Enforced here because the get-or-create is check-then-insert and
        // two concurrent first requests would otherwise both create one.
        builder.HasIndex(p => p.UserId).HasDatabaseName("UX_patient_profiles_user_id").IsUnique();

        builder.HasMany(p => p.Subprofiles)
            .WithOne(s => s.PatientProfile)
            .HasForeignKey(s => s.PatientProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.Allergies)
            .WithOne()
            .HasForeignKey(a => a.PatientProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.Conditions)
            .WithOne()
            .HasForeignKey(c => c.PatientProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.Medications)
            .WithOne()
            .HasForeignKey(m => m.PatientProfileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class FamilySubprofileConfiguration : IEntityTypeConfiguration<FamilySubprofile>
{
    public void Configure(EntityTypeBuilder<FamilySubprofile> builder)
    {
        builder.ToTable("family_subprofiles");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(s => s.PatientProfileId).HasColumnName("patient_profile_id").HasMaxLength(36).IsRequired();
        builder.Property(s => s.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(s => s.DateOfBirth).HasColumnName("date_of_birth");
        builder.Property(s => s.Gender).HasColumnName("gender").HasConversion<string>().HasMaxLength(10);
        builder.Property(s => s.Relation).HasColumnName("relation").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.BloodType).HasColumnName("blood_type").HasMaxLength(10);
        builder.Property(s => s.IsActive).HasColumnName("is_active").HasDefaultValue(true);

        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(s => s.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        builder.Property(s => s.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        builder.HasIndex(s => new { s.PatientProfileId, s.IsActive })
            .HasDatabaseName("IX_family_subprofiles_patient_profile_id_is_active");
    }
}

/// <summary>
/// The three health-record tables share a shape, so their mapping is shared too. Each has two
/// nullable owner columns — one for the account holder, one for a dependant.
/// </summary>
internal static class HealthRecordMapping
{
    public static void ApplyCommon<T>(EntityTypeBuilder<T> builder, string table) where T : HealthRecord
    {
        builder.ToTable(table);

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(r => r.PatientProfileId).HasColumnName("patient_profile_id").HasMaxLength(36);
        builder.Property(r => r.SubprofileId).HasColumnName("subprofile_id").HasMaxLength(36);
        builder.Property(r => r.DeletedAt).HasColumnName("deleted_at");

        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(r => r.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        builder.Property(r => r.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        builder.HasIndex(r => r.PatientProfileId).HasDatabaseName($"IX_{table}_patient_profile_id");
        builder.HasIndex(r => r.SubprofileId).HasDatabaseName($"IX_{table}_subprofile_id");

        builder.HasOne<FamilySubprofile>()
            .WithMany()
            .HasForeignKey(r => r.SubprofileId)
            // Restrict, not Cascade: the profile -> subprofile -> record path plus the direct
            // profile -> record path would be two cascade paths into one table, which SQL Server
            // rejects outright.
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AllergyConfiguration : IEntityTypeConfiguration<Allergy>
{
    public void Configure(EntityTypeBuilder<Allergy> builder)
    {
        HealthRecordMapping.ApplyCommon(builder, "allergies");

        builder.Property(a => a.Allergen).HasColumnName("allergen").HasMaxLength(200).IsRequired();
        builder.Property(a => a.Severity).HasColumnName("severity").HasMaxLength(50);
        builder.Property(a => a.Reaction).HasColumnName("reaction").HasMaxLength(500);
    }
}

public sealed class ChronicConditionConfiguration : IEntityTypeConfiguration<ChronicCondition>
{
    public void Configure(EntityTypeBuilder<ChronicCondition> builder)
    {
        HealthRecordMapping.ApplyCommon(builder, "chronic_conditions");

        builder.Property(c => c.Condition).HasColumnName("condition").HasMaxLength(300).IsRequired();
        builder.Property(c => c.DiagnosedDate).HasColumnName("diagnosed_date");
        builder.Property(c => c.Notes).HasColumnName("notes").HasMaxLength(2000);
        builder.Property(c => c.IsActive).HasColumnName("is_active").HasDefaultValue(true);
    }
}

public sealed class CurrentMedicationConfiguration : IEntityTypeConfiguration<CurrentMedication>
{
    public void Configure(EntityTypeBuilder<CurrentMedication> builder)
    {
        HealthRecordMapping.ApplyCommon(builder, "current_medications");

        builder.Property(m => m.DrugName).HasColumnName("drug_name").HasMaxLength(300).IsRequired();
        builder.Property(m => m.Dosage).HasColumnName("dosage").HasMaxLength(100);
        builder.Property(m => m.Frequency).HasColumnName("frequency").HasMaxLength(100);
        builder.Property(m => m.PrescribedBy).HasColumnName("prescribed_by").HasMaxLength(200);
        builder.Property(m => m.StartDate).HasColumnName("start_date");
        builder.Property(m => m.EndDate).HasColumnName("end_date");
        builder.Property(m => m.IsActive).HasColumnName("is_active").HasDefaultValue(true);
    }
}

public sealed class ExternalVisitConfiguration : IEntityTypeConfiguration<ExternalVisit>
{
    public void Configure(EntityTypeBuilder<ExternalVisit> builder)
    {
        builder.ToTable("external_visits");

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(v => v.PatientUserId).HasColumnName("patient_user_id").HasMaxLength(36).IsRequired();
        builder.Property(v => v.SubprofileId).HasColumnName("subprofile_id").HasMaxLength(36);
        builder.Property(v => v.DoctorName).HasColumnName("doctor_name").HasMaxLength(200).IsRequired();
        builder.Property(v => v.ClinicName).HasColumnName("clinic_name").HasMaxLength(200);
        builder.Property(v => v.Specialty).HasColumnName("specialty").HasMaxLength(120);
        builder.Property(v => v.VisitDate).HasColumnName("visit_date").IsRequired();
        builder.Property(v => v.ChiefComplaint).HasColumnName("chief_complaint").HasMaxLength(1000);
        builder.Property(v => v.Diagnosis).HasColumnName("diagnosis").HasMaxLength(1000);
        builder.Property(v => v.Notes).HasColumnName("notes").HasColumnType("nvarchar(max)");
        builder.Property(v => v.Medications).HasColumnName("medications").HasColumnType("nvarchar(max)");
        builder.Property(v => v.FollowUpDate).HasColumnName("follow_up_date");
        builder.Property(v => v.FollowUpNotes).HasColumnName("follow_up_notes").HasMaxLength(1000);

        builder.Property(v => v.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(v => v.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(v => v.UpdatedAt).HasColumnName("updated_at");
        builder.Property(v => v.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        builder.HasIndex(v => v.PatientUserId).HasDatabaseName("IX_external_visits_patient_user_id");
        builder.HasIndex(v => v.SubprofileId).HasDatabaseName("IX_external_visits_subprofile_id");
        builder.HasIndex(v => v.VisitDate).HasDatabaseName("IX_external_visits_visit_date");

        builder.HasOne<FamilySubprofile>()
            .WithMany()
            .HasForeignKey(v => v.SubprofileId)
            // SetNull matches the Prisma onDelete: removing a dependant keeps the visit record.
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class ExternalDoctorConfiguration : IEntityTypeConfiguration<ExternalDoctor>
{
    public void Configure(EntityTypeBuilder<ExternalDoctor> builder)
    {
        builder.ToTable("external_doctors");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(d => d.PatientUserId).HasColumnName("patient_user_id").HasMaxLength(36).IsRequired();
        builder.Property(d => d.SubprofileId).HasColumnName("subprofile_id").HasMaxLength(36);
        builder.Property(d => d.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(d => d.Specialty).HasColumnName("specialty").HasMaxLength(120);
        builder.Property(d => d.ClinicName).HasColumnName("clinic_name").HasMaxLength(200);
        builder.Property(d => d.Phone).HasColumnName("phone").HasMaxLength(32);
        builder.Property(d => d.Email).HasColumnName("email").HasMaxLength(320);
        builder.Property(d => d.Address).HasColumnName("address").HasMaxLength(500);
        builder.Property(d => d.Notes).HasColumnName("notes").HasColumnType("nvarchar(max)");

        builder.Property(d => d.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(d => d.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(d => d.UpdatedAt).HasColumnName("updated_at");
        builder.Property(d => d.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        builder.HasIndex(d => d.PatientUserId).HasDatabaseName("IX_external_doctors_patient_user_id");
        builder.HasIndex(d => d.SubprofileId).HasDatabaseName("IX_external_doctors_subprofile_id");

        builder.HasOne<FamilySubprofile>()
            .WithMany()
            .HasForeignKey(d => d.SubprofileId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
