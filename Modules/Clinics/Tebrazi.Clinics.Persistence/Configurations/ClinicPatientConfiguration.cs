using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tebrazi.Clinics.Domain.Entities;

namespace Tebrazi.Clinics.Persistence.Configurations;

public sealed class ClinicPatientConfiguration : IEntityTypeConfiguration<ClinicPatient>
{
    public void Configure(EntityTypeBuilder<ClinicPatient> builder)
    {
        builder.ToTable("clinic_patients");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(p => p.PhysicianUserId).HasColumnName("physician_user_id").HasMaxLength(36).IsRequired();
        builder.Property(p => p.ClinicId).HasColumnName("clinic_id").HasMaxLength(36);

        // ⚠ THE WIDTHS ARE CONTRACT, NOT HYGIENE. Prisma declares every one of these as an
        // unbounded Postgres `text` (schema.prisma:575-581) and connections.js:789-797 writes them
        // with NO validation — `phone?.trim() || null`, and `bloodType || null` not even trimmed.
        // A narrow nvarchar here raises "String or binary data would be truncated", EF wraps it in
        // a DbUpdateException, and POST /api/connections/clinic-patients answers
        // `500 {"error":"Failed to create patient file"}` for a body Node stores and answers 201.
        // A receptionist typing two numbers into the free-text phone field, or any blood note over
        // ten characters, was enough. 450 is the widest nvarchar that can still carry a
        // nonclustered index key here; blood_type is not indexed, so it matches `text` exactly.
        builder.Property(p => p.Name).HasColumnName("name").HasMaxLength(450).IsRequired();
        builder.Property(p => p.Phone).HasColumnName("phone").HasMaxLength(450);
        builder.Property(p => p.Email).HasColumnName("email").HasMaxLength(450);
        builder.Property(p => p.DateOfBirth).HasColumnName("date_of_birth");

        // gender stays narrow: it is an ENUM on both backends, not free text, and Prisma rejects
        // anything but MALE|FEMALE|OTHER before the column is ever reached.
        builder.Property(p => p.Gender).HasColumnName("gender").HasConversion<string>().HasMaxLength(10);
        builder.Property(p => p.NationalId).HasColumnName("national_id").HasMaxLength(450);
        builder.Property(p => p.BloodType).HasColumnName("blood_type").HasColumnType("nvarchar(max)");

        builder.Property(p => p.Notes).HasColumnName("notes").HasColumnType("nvarchar(max)");

        // Postgres held these as text[]; a primitive collection stores each as one JSON array
        // column and keeps the same wire shape.
        builder.PrimitiveCollection(p => p.Allergies)
            .HasColumnName("allergies").HasColumnType("nvarchar(max)");
        builder.PrimitiveCollection(p => p.ChronicConditions)
            .HasColumnName("chronic_conditions").HasColumnType("nvarchar(max)");

        builder.Property(p => p.LinkedUserId).HasColumnName("linked_user_id").HasMaxLength(36);
        builder.Property(p => p.LinkedAt).HasColumnName("linked_at");

        builder.Property(p => p.IsActive).HasColumnName("is_active").HasDefaultValue(true).IsRequired();
        builder.Property(p => p.LastVisitDate).HasColumnName("last_visit_date");

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        builder.Property(p => p.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        builder.HasIndex(p => p.PhysicianUserId).HasDatabaseName("IX_clinic_patients_physician_user_id");
        builder.HasIndex(p => p.ClinicId).HasDatabaseName("IX_clinic_patients_clinic_id");
        builder.HasIndex(p => p.LinkedUserId).HasDatabaseName("IX_clinic_patients_linked_user_id");
        builder.HasIndex(p => p.Phone).HasDatabaseName("IX_clinic_patients_phone");
        builder.HasIndex(p => p.Name).HasDatabaseName("IX_clinic_patients_name");

        // No foreign key to clinics: clinic_id is nullable in the Node schema and a chart may be
        // filed before a clinic is chosen. A real FK is added when that stops being true.
    }
}
