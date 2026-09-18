namespace Tebrazi.SharedKernel.Authorization;

/// <summary>
/// The 14 clinic permission keys. Names match the JSON keys the Node backend stores in
/// <c>clinic_staff.permissions</c> and returns to the client, so they are case-sensitive.
/// </summary>
public static class ClinicPermission
{
    public const string ViewPatientFiles    = "canViewPatientFiles";
    public const string ManageAppointments  = "canManageAppointments";
    public const string CheckInPatients     = "canCheckInPatients";
    public const string DraftNotes          = "canDraftNotes";
    public const string UploadDocuments     = "canUploadDocuments";
    public const string SendMessages        = "canSendMessages";
    public const string ViewAnalytics       = "canViewAnalytics";
    public const string ManageFollowUps     = "canManageFollowUps";
    public const string StartVisits         = "canStartVisits";
    public const string Prescribe           = "canPrescribe";
    public const string WriteSoap           = "canWriteSOAP";
    public const string ManageSettings      = "canManageSettings";
    public const string ManageStaff         = "canManageStaff";
    public const string OrderInvestigations = "canOrderInvestigations";

    /// <summary>Every key, in the order the Node service enumerates them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        ViewPatientFiles, ManageAppointments, CheckInPatients, DraftNotes,
        UploadDocuments, SendMessages, ViewAnalytics, ManageFollowUps,
        StartVisits, Prescribe, WriteSoap, ManageSettings, ManageStaff,
        OrderInvestigations
    ];
}
