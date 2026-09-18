using Microsoft.AspNetCore.Mvc;
using Tebrazi.SharedKernel.Mediation;

namespace Tebrazi.Common.Api.Controllers;

/// <summary>
/// Base for every controller. Controllers stay thin: build a Command or Query, send it,
/// return the result. No business logic, and no DbContext.
///
/// Note there is deliberately NO response envelope. The React client reads the payload
/// directly (<c>res.json(patients)</c> in the Node original), so wrapping responses in a
/// <c>{ success, data }</c> object would break every call site in the frontend.
/// </summary>
[ApiController]
public abstract class BaseApiController(IMediator mediator) : ControllerBase
{
    protected IMediator Mediator { get; } = mediator;

    /// <summary>Sends a request through the mediator, carrying the request-abort token.</summary>
    protected Task<TResult> Send<TResult>(IRequest<TResult> request)
        => Mediator.Send(request, HttpContext.RequestAborted);

    /// <summary>200 with the payload serialized at the top level.</summary>
    protected IActionResult Payload<T>(T data) => Ok(data);

    /// <summary>201 with the payload, for endpoints the Node backend answered with 201.</summary>
    protected IActionResult CreatedPayload<T>(T data) => StatusCode(StatusCodes.Status201Created, data);

    /// <summary>The <c>{ message }</c> acknowledgement shape several Node endpoints return.</summary>
    protected IActionResult Acknowledge(string message) => Ok(new { message });
}
