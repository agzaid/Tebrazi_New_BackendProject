using Microsoft.Extensions.DependencyInjection;
using Tebrazi.Infrastructure.Shared.Ai;
using Tebrazi.Infrastructure.Shared.Logging;
using Tebrazi.Infrastructure.Shared.Mediation;
using Tebrazi.Infrastructure.Shared.Placeholders;
using Tebrazi.SharedKernel.Abstractions;
using Tebrazi.SharedKernel.Logging;

namespace Tebrazi.Infrastructure.Shared.DependencyInjection;

public static class InfrastructureSharedModule
{
    /// <summary>Cross-cutting infrastructure every API host needs. Call once from Program.cs.</summary>
    public static IServiceCollection AddInfrastructureShared(this IServiceCollection services)
    {
        services.AddMediator();

        // Singleton, not scoped: it only wraps ILogger<T>, which is itself a singleton, and a
        // scoped logger cannot be injected into the singleton services that need one.
        services.AddSingleton(typeof(IAppLogger<>), typeof(AppLogger<>));

        // ── Placeholder ports ────────────────────────────────────────────────
        // Registered with plain Add, not TryAdd, and BEFORE every module: Program.cs calls this
        // first, so a real implementation registered by a later module wins the resolve. Each of
        // these logs at warning level when it is reached, so a skipped side effect is visible in
        // the log rather than silent.
        //
        // The AI gateway placeholder belongs to the same set — GET /api/appointments/ai-optimize
        // and the Visits AI endpoints all inject IAiGateway, and without this registration they
        // cannot even be constructed.
        services.AddScoped<IAiGateway, UnconfiguredAiGateway>();

        // Owned by the WaitingRoom, Payments and PatientNotes modules once those are ported.
        services.AddScoped<IWaitingQueueWriter, UnimplementedWaitingQueueWriter>();
        services.AddScoped<IPaymentWriter, UnimplementedPaymentWriter>();
        services.AddScoped<IPatientNoteWriter, UnimplementedPatientNoteWriter>();

        // Owned by the Reminders and Inventory modules once those are ported. The two side
        // effects PUT /api/prescriptions/{id}/send and .../dispense perform.
        services.AddScoped<IMedicationReminderScheduler, UnimplementedMedicationReminderScheduler>();
        services.AddScoped<IInventoryDeductionWriter, UnimplementedInventoryDeductionWriter>();

        // NOT a placeholder: emailService.js's own console transport is what Node uses when
        // neither RESEND_API_KEY nor SMTP_HOST is set, and it succeeds. Register a real
        // IEmailSender after this call to send mail for real.
        services.AddScoped<IEmailSender, ConsoleEmailSender>();

        return services;
    }
}
