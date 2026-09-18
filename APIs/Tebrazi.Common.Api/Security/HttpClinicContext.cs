using Tebrazi.SharedKernel.Abstractions;

namespace Tebrazi.Common.Api.Security;

/// <summary>
/// Resolves the active clinic per request. Precedence matches the Node middleware exactly:
/// the <c>X-Clinic-Id</c> header (which the axios interceptor always sends when a clinic is
/// active), then a <c>clinicId</c> query parameter.
///
/// The Node version also fell back to <c>req.body.clinicId</c>. That is deliberately NOT done
/// here: reading the body to make an authorization decision means buffering it before the model
/// binder runs. Endpoints that need a clinic id from the body take it as a route or query
/// parameter instead.
/// </summary>
public sealed class HttpClinicContext(IHttpContextAccessor accessor) : IClinicContext
{
    public const string HeaderName = "X-Clinic-Id";

    public string? ClinicId
    {
        get
        {
            var request = accessor.HttpContext?.Request;
            if (request is null) return null;

            if (request.Headers.TryGetValue(HeaderName, out var header))
            {
                var value = header.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }

            if (request.Query.TryGetValue("clinicId", out var query))
            {
                var value = query.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }

            return null;
        }
    }
}
