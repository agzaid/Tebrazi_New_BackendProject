using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tebrazi.Visits.Domain.Entities;

namespace Tebrazi.Visits.Persistence.Configurations;

public sealed class VisitConfiguration : IEntityTypeConfiguration<Visit>
{
    public void Configure(EntityTypeBuilder<Visit> builder)
    {
        builder.ToTable("visits");

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        // Cross-module keys. No navigations: organizations and users belong to Identity's model,
        // clinics and clinic patients to Clinics'. Reads go through the published ports.
        builder.Property(v => v.OrganizationId).HasColumnName("organization_id").HasMaxLength(36).IsRequired();
        builder.Property(v => v.ClinicId).HasColumnName("clinic_id").HasMaxLength(36).IsRequired();
        builder.Property(v => v.PhysicianId).HasColumnName("physician_id").HasMaxLength(36).IsRequired();
        builder.Property(v => v.PatientUserId).HasColumnName("patient_user_id").HasMaxLength(36).IsRequired();
        builder.Property(v => v.SubprofileId).HasColumnName("subprofile_id").HasMaxLength(36);
        builder.Property(v => v.ClinicPatientId).HasColumnName("clinic_patient_id").HasMaxLength(36);

        // Stored as the member name, never the ordinal: the client compares against
        // "IN_PROGRESS", and an ordinal would also silently shift if a member were inserted.
        builder.Property(v => v.Status)
            .HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(v => v.AudioUrl).HasColumnName("audio_url").HasMaxLength(1000);
        builder.Property(v => v.RawTranscript).HasColumnName("raw_transcript").HasColumnType("nvarchar(max)");
        builder.Property(v => v.RawNotes).HasColumnName("raw_notes").HasColumnType("nvarchar(max)");

        builder.Property(v => v.Subjective).HasColumnName("subjective").HasColumnType("nvarchar(max)");
        builder.Property(v => v.Objective).HasColumnName("objective").HasColumnType("nvarchar(max)");
        builder.Property(v => v.Assessment).HasColumnName("assessment").HasColumnType("nvarchar(max)");
        builder.Property(v => v.Plan).HasColumnName("plan").HasColumnType("nvarchar(max)");

        builder.Property(v => v.ChiefComplaint).HasColumnName("chief_complaint").HasMaxLength(500);

        // A primitive collection: EF stores the list as a JSON array in one column and can still
        // translate Contains to SQL. Postgres held this as text[]; the wire shape is the same
        // JSON array either way.
        builder.PrimitiveCollection(v => v.Diagnosis)
            .HasColumnName("diagnosis").HasColumnType("nvarchar(max)");

        // Opaque JSON, stored verbatim. The server never reads into these.
        builder.Property(v => v.DiagnosisCodes).HasColumnName("diagnosis_codes").HasColumnType("nvarchar(max)");
        builder.Property(v => v.SharedSections).HasColumnName("shared_sections").HasColumnType("nvarchar(max)");
        builder.Property(v => v.SpecialtyData).HasColumnName("specialty_data").HasColumnType("nvarchar(max)");

        builder.Property(v => v.FollowUpDate).HasColumnName("follow_up_date");
        builder.Property(v => v.FollowUpNotes).HasColumnName("follow_up_notes").HasMaxLength(2000);

        builder.Property(v => v.PatientFeedback).HasColumnName("patient_feedback").HasColumnType("nvarchar(max)");
        builder.Property(v => v.PatientFeedbackAt).HasColumnName("patient_feedback_at");
        builder.Property(v => v.PatientDismissedAt).HasColumnName("patient_dismissed_at");

        builder.Property(v => v.VisitDate).HasColumnName("visit_date").IsRequired();
        builder.Property(v => v.CompletedAt).HasColumnName("completed_at");
        builder.Property(v => v.DeletedAt).HasColumnName("deleted_at");

        builder.Property(v => v.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(v => v.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(v => v.UpdatedAt).HasColumnName("updated_at");
        builder.Property(v => v.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        // Index set ported from the Prisma model, including its composites — the list and inbox
        // queries filter on (clinic, date) and (patient, date) respectively.
        builder.HasIndex(v => v.OrganizationId).HasDatabaseName("IX_visits_organization_id");
        builder.HasIndex(v => v.ClinicId).HasDatabaseName("IX_visits_clinic_id");
        builder.HasIndex(v => v.PhysicianId).HasDatabaseName("IX_visits_physician_id");
        builder.HasIndex(v => v.PatientUserId).HasDatabaseName("IX_visits_patient_user_id");
        builder.HasIndex(v => v.SubprofileId).HasDatabaseName("IX_visits_subprofile_id");
        builder.HasIndex(v => v.ClinicPatientId).HasDatabaseName("IX_visits_clinic_patient_id");
        builder.HasIndex(v => v.VisitDate).HasDatabaseName("IX_visits_visit_date");
        builder.HasIndex(v => v.Status).HasDatabaseName("IX_visits_status");
        builder.HasIndex(v => new { v.ClinicId, v.VisitDate }).HasDatabaseName("IX_visits_clinic_id_visit_date");
        builder.HasIndex(v => new { v.PatientUserId, v.VisitDate }).HasDatabaseName("IX_visits_patient_user_id_visit_date");
        builder.HasIndex(v => new { v.PhysicianId, v.Status }).HasDatabaseName("IX_visits_physician_id_status");

        builder.HasMany(v => v.Investigations)
            .WithOne()
            .HasForeignKey(i => i.VisitId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class InvestigationConfiguration : IEntityTypeConfiguration<Investigation>
{
    public void Configure(EntityTypeBuilder<Investigation> builder)
    {
        builder.ToTable("investigations");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        builder.Property(i => i.VisitId).HasColumnName("visit_id").HasMaxLength(36).IsRequired();
        builder.Property(i => i.SubprofileId).HasColumnName("subprofile_id").HasMaxLength(36);

        builder.Property(i => i.Type).HasColumnName("type").HasMaxLength(50).IsRequired();
        builder.Property(i => i.Name).HasColumnName("name").HasMaxLength(300).IsRequired();
        builder.Property(i => i.Instructions).HasColumnName("instructions").HasMaxLength(2000);

        builder.Property(i => i.Status)
            .HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(i => i.ResultUrl).HasColumnName("result_url").HasMaxLength(1000);
        builder.Property(i => i.ResultNotes).HasColumnName("result_notes").HasColumnType("nvarchar(max)");

        // The Prisma field, and the one the client reads. Distinct from the created_at audit
        // stamp below, which the API never returns.
        builder.Property(i => i.RequestedAt).HasColumnName("requested_at").IsRequired();
        builder.Property(i => i.CompletedAt).HasColumnName("completed_at");

        builder.Property(i => i.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(i => i.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at");
        builder.Property(i => i.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        builder.HasIndex(i => i.VisitId).HasDatabaseName("IX_investigations_visit_id");
        builder.HasIndex(i => i.SubprofileId).HasDatabaseName("IX_investigations_subprofile_id");
        builder.HasIndex(i => i.Status).HasDatabaseName("IX_investigations_status");
    }
}
