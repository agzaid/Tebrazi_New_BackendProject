using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tebrazi.Prescriptions.Domain.Entities;

namespace Tebrazi.Prescriptions.Persistence.Configurations;

public sealed class PrescriptionConfiguration : IEntityTypeConfiguration<Prescription>
{
    public void Configure(EntityTypeBuilder<Prescription> builder)
    {
        builder.ToTable("prescriptions");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        // visit_id is a plain column, deliberately without a foreign key: the visits table is
        // owned by the Visits module and its own context. A database-level FK here would let
        // either module's migrations reorder the other's, and EF would want the principal in
        // this model. The Visits port is what enforces the relationship.
        builder.Property(p => p.VisitId).HasColumnName("visit_id").HasMaxLength(36).IsRequired();
        builder.Property(p => p.PhysicianId).HasColumnName("physician_id").HasMaxLength(36).IsRequired();
        builder.Property(p => p.SubprofileId).HasColumnName("subprofile_id").HasMaxLength(36);

        builder.Property(p => p.Status)
            .HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        // Opaque JSON, required. Stored verbatim — the drug-line shape is the client's.
        builder.Property(p => p.Medications)
            .HasColumnName("medications").HasColumnType("nvarchar(max)").IsRequired();

        builder.Property(p => p.Notes).HasColumnName("notes").HasColumnType("nvarchar(max)");

        builder.Property(p => p.SignedAt).HasColumnName("signed_at");
        builder.Property(p => p.SentToPatientAt).HasColumnName("sent_to_patient_at");
        builder.Property(p => p.PdfUrl).HasColumnName("pdf_url").HasMaxLength(1000);

        builder.Property(p => p.RefillRequestedAt).HasColumnName("refill_requested_at");
        builder.Property(p => p.RefillStatus).HasColumnName("refill_status").HasMaxLength(20);
        builder.Property(p => p.RefillNotes).HasColumnName("refill_notes").HasColumnType("nvarchar(max)");

        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        builder.Property(p => p.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        builder.HasIndex(p => p.VisitId).HasDatabaseName("IX_prescriptions_visit_id");
        builder.HasIndex(p => p.PhysicianId).HasDatabaseName("IX_prescriptions_physician_id");
        builder.HasIndex(p => p.SubprofileId).HasDatabaseName("IX_prescriptions_subprofile_id");
        builder.HasIndex(p => p.Status).HasDatabaseName("IX_prescriptions_status");

        // Not in the Prisma index set. The physician's refill queue filters on refill_status
        // alone, and without this it is a full scan of every prescription they have ever written.
        builder.HasIndex(p => p.RefillStatus).HasDatabaseName("IX_prescriptions_refill_status");
    }
}

public sealed class InteractionAlertConfiguration : IEntityTypeConfiguration<InteractionAlert>
{
    public void Configure(EntityTypeBuilder<InteractionAlert> builder)
    {
        builder.ToTable("interaction_alerts");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        // Both nullable, exactly one set per row. No foreign keys: the physician profile and the
        // user live in Identity's model, and the visit in Visits'.
        builder.Property(a => a.PhysicianId).HasColumnName("physician_id").HasMaxLength(36);
        builder.Property(a => a.PatientUserId).HasColumnName("patient_user_id").HasMaxLength(36);
        builder.Property(a => a.VisitId).HasColumnName("visit_id").HasMaxLength(36);

        builder.Property(a => a.Drugs)
            .HasColumnName("drugs").HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(a => a.Interactions)
            .HasColumnName("interactions").HasColumnType("nvarchar(max)").IsRequired();

        builder.Property(a => a.AlertCount).HasColumnName("alert_count").HasDefaultValue(0).IsRequired();
        builder.Property(a => a.CheckedAt).HasColumnName("checked_at").IsRequired();

        builder.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(a => a.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();

        builder.HasIndex(a => a.PhysicianId).HasDatabaseName("IX_interaction_alerts_physician_id");
        builder.HasIndex(a => a.PatientUserId).HasDatabaseName("IX_interaction_alerts_patient_user_id");
    }
}
