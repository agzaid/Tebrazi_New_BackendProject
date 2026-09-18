namespace Tebrazi.SharedKernel.Authorization;

/// <summary>
/// Clinic staff roles. Stored as a string on <c>clinic_staff.role</c> (as in the Node schema),
/// so the values here must match those strings exactly.
/// </summary>
public static class ClinicStaffRole
{
    public const string Assistant    = "ASSISTANT";
    public const string Receptionist = "RECEPTIONIST";
    public const string Nurse        = "NURSE";
    public const string Admin        = "ADMIN";

    public static readonly IReadOnlyList<string> All =
        [Assistant, Receptionist, Nurse, Admin];
}
