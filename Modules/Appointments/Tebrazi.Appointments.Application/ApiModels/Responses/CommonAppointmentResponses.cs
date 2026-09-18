namespace Tebrazi.Appointments.Application.ApiModels.Responses;

/// <summary>
/// The bare <c>{ "message": "..." }</c> acknowledgement. TWO endpoints return it, and they are
/// written in two different files, so both must use THIS record rather than declaring their own:
/// <c>DELETE /api/appointments/{id}</c> answering <c>{"message":"Appointment deleted"}</c>
/// (appointments.js:950) and <c>DELETE /api/appointments/slots/{id}</c> answering
/// <c>{"message":"Slot removed"}</c> (appointments.js:110).
///
/// Note that most appointment MUTATIONS do not use this: <c>/confirm</c>, <c>/cancel</c>,
/// <c>/complete</c> and <c>/no-show</c> return the whole updated row WITH a <c>message</c> key
/// appended, which needs a per-endpoint record instead.
///
/// It is the ONLY shared response type in this module, on purpose. The other repeated shapes are
/// each confined to a single file — <c>{ count, message }</c> to the two slot-generation routes,
/// and "22 scalars plus message" to the four status transitions — so they belong beside the
/// endpoints that own them. There is deliberately no shared appointment row DTO: the relation
/// projections differ per endpoint (<c>GET /</c>'s <c>physician</c> carries <c>specialty</c>,
/// <c>POST /</c>'s does not), and several endpoints append or omit top-level keys.
/// </summary>
public sealed record MessageResponse(string Message);
