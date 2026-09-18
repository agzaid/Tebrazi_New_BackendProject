namespace Tebrazi.Visits.Application.ApiModels.Responses;

/// <summary>
/// The bare <c>{ "message": "..." }</c> acknowledgement several Visits endpoints return, e.g.
/// <c>DELETE /api/visits/{id}</c> answering <c>{"message":"Visit archived successfully"}</c>.
///
/// Shared across this module's use-case files so the shape is defined once. Anything richer than
/// a single message needs its own per-endpoint record — see the note in
/// docs/MODULES-visits-appointments-prescriptions.md about there being no reusable VisitDto.
/// </summary>
public sealed record MessageResponse(string Message);

/// <summary>
/// <c>{ "success": true }</c>. Distinct from <see cref="MessageResponse"/> because
/// <c>DELETE /api/visits/{id}/investigations/{invId}</c> answers with this and nothing else —
/// including when the investigation did not exist.
/// </summary>
public sealed record SuccessResponse(bool Success);
