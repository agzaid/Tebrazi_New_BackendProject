using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tebrazi.Connections.Domain.Entities;

namespace Tebrazi.Connections.Persistence.Configurations;

public sealed class DoctorPatientConnectionConfiguration
    : IEntityTypeConfiguration<DoctorPatientConnection>
{
    public void Configure(EntityTypeBuilder<DoctorPatientConnection> builder)
    {
        builder.ToTable("doctor_patient_connections");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        // No foreign keys, and this is the cascade-safety comment from the Prisma model
        // (schema.prisma:518-520) honoured in the only way this architecture allows.
        //
        // There, both User relations are `onDelete: Cascade` and the schema warns in capitals
        // that the app must never hard-delete a User, because every connection — and with it
        // every clinical link the connection authorises — would go with it. `users`,
        // `physician_profiles` and `family_subprofiles` are owned by the Identity and Patients
        // contexts, which this module may not map, so there is no principal end here to declare a
        // relationship against. The result is that the destructive cascade the Prisma comment
        // warns about CANNOT EXIST in this database, which is the intent the comment expresses.
        // The relationship is enforced by IIdentityDirectory and IPatientDirectory instead.
        builder.Property(c => c.PhysicianUserId)
            .HasColumnName("physician_user_id").HasMaxLength(36).IsRequired();
        builder.Property(c => c.PatientUserId)
            .HasColumnName("patient_user_id").HasMaxLength(36).IsRequired();
        builder.Property(c => c.SubprofileId)
            .HasColumnName("subprofile_id").HasMaxLength(36);

        builder.Property(c => c.Status)
            .HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(c => c.InitiatedBy)
            .HasColumnName("initiated_by").HasMaxLength(36).IsRequired();

        builder.Property(c => c.ConnectedAt).HasColumnName("connected_at");

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");
        builder.Property(c => c.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);

        // The three Prisma indexes, one for one (schema.prisma:526-528).
        builder.HasIndex(c => c.PhysicianUserId)
            .HasDatabaseName("IX_doctor_patient_connections_physician_user_id");
        builder.HasIndex(c => c.PatientUserId)
            .HasDatabaseName("IX_doctor_patient_connections_patient_user_id");
        builder.HasIndex(c => c.Status)
            .HasDatabaseName("IX_doctor_patient_connections_status");

        // ── @@unique([physicianUserId, patientUserId, subprofileId]), FILTERED ───────────────
        //
        // Prisma declares the triple unique (schema.prisma:525) and PostgreSQL implements that
        // with NULLS DISTINCT: two rows whose subprofile_id is NULL do NOT collide, so in the
        // Node backend the constraint constrains DEPENDANT edges only and a physician/patient
        // pair can legally accumulate several parent edges.
        //
        // SQL Server's unique index treats NULLs as EQUAL, so an unfiltered index here would
        // reject the second parent edge and turn a 201 into a 500 on a path Node allows. The
        // filter reproduces Postgres' behaviour exactly: unique across the triple wherever
        // subprofile_id is set, unconstrained where it is not.
        //
        // Nothing in connections.js depends on a parent edge being unique — every write that
        // could create one probes first with `findFirst` (POST /request at :189, add-by-phone at
        // :556, connect-by-pin at :1638, link-account at :939) and answers 409 or returns early.
        builder.HasIndex(c => new { c.PhysicianUserId, c.PatientUserId, c.SubprofileId })
            .IsUnique()
            .HasFilter("[subprofile_id] IS NOT NULL")
            .HasDatabaseName("UX_doctor_patient_connections_physician_patient_subprofile");
    }
}

public sealed class ConnectionPinConfiguration : IEntityTypeConfiguration<ConnectionPin>
{
    public void Configure(EntityTypeBuilder<ConnectionPin> builder)
    {
        builder.ToTable("connection_pins");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();

        // Again no foreign keys: users and clinics belong to other contexts.
        builder.Property(p => p.PhysicianUserId)
            .HasColumnName("physician_user_id").HasMaxLength(36).IsRequired();

        // ⚠ NOT HasMaxLength(36), unlike every other id column in this file. On the physician path
        // Node stores whatever the caller put in the X-Clinic-Id header straight onto the row —
        // `clinicId: clinicId || null` (connections.js:1577), with no existence check and no length
        // check — into an unbounded Postgres `text` (schema.prisma:552). A 36-character cap makes
        // an over-long header a truncation error inside SaveChangesAsync, which the route's guard
        // turns into `500 {"error":"Failed to generate PIN"}` where Node answers 200 — and the
        // force-expire sweep has already committed by then, so the physician's previous code is
        // dead and no new one exists. Do NOT "fix" this with a validation gate in the handler
        // instead: Node has none, and a 400 here is a status code the client has never seen.
        // The column is not indexed, so nvarchar(max) is free.
        builder.Property(p => p.ClinicId).HasColumnName("clinic_id").HasColumnType("nvarchar(max)");

        // TEXT, not a number: the code is compared to the raw request body and leading zeros
        // would be lost by any numeric type. Four characters is the generated width; the column
        // is wider so a legacy or hand-written value still materialises.
        builder.Property(p => p.Pin).HasColumnName("pin").HasMaxLength(10).IsRequired();

        builder.Property(p => p.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(p => p.UsedAt).HasColumnName("used_at");
        builder.Property(p => p.UsedByUserId).HasColumnName("used_by_user_id").HasMaxLength(36);

        // ConnectionPin is an ImmutableEntity: the Prisma model has createdAt and no updatedAt,
        // so there are no updated_at / updated_by columns to map.
        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();

        // The three Prisma indexes (schema.prisma:559-561). `pin` is indexed and NOT unique —
        // uniqueness is only ever required among LIVE pins, which the generation loop enforces in
        // application code, and a used pin must be allowed to keep its value forever.
        builder.HasIndex(p => p.Pin).HasDatabaseName("IX_connection_pins_pin");
        builder.HasIndex(p => p.PhysicianUserId)
            .HasDatabaseName("IX_connection_pins_physician_user_id");
        builder.HasIndex(p => p.ExpiresAt).HasDatabaseName("IX_connection_pins_expires_at");
    }
}
