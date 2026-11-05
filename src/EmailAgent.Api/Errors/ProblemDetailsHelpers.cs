using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EmailAgent.Api.Errors;

/// <summary>
/// M1T6 task step 1: the single §13.12 RFC 7807 writer for the account
/// surface. Every 4xx/5xx the account endpoints produce is a problem document
/// with the standard members <c>type</c>, <c>title</c>, <c>status</c>,
/// <c>detail</c>, <c>instance</c> (the request path) plus the optional
/// <c>errors</c> array of <c>{ name, reason }</c> entries for validation
/// failures.
/// </summary>
/// <remarks>
/// <para>
/// The result is <c>Results.Problem(ProblemDetails)</c> rather than the
/// task's literal <c>JsonResult</c>: <c>JsonResult</c> needs MVC's
/// <c>IActionResultExecutor&lt;JsonResult&gt;</c>, which M1T1's minimal host
/// never registers (there is no <c>AddControllers()</c>). The problem path
/// works with <c>AddProblemDetails()</c> alone — the plumbing the task says
/// M1T1 already added — and writes the identical
/// <c>application/problem+json</c> document. M6aT2's
/// <c>ProblemDetailsMapper</c> retrofit supersedes this helper anyway.
/// </para>
/// <para>
/// <c>errors</c> is omitted entirely (not <c>null</c>) when no field-level
/// detail applies, per §13.12's <c>errors?</c> member.
/// </para>
/// <para>
/// With <c>AddProblemDetails()</c> registered, the host's
/// <c>ProblemDetailsFactory</c> may append its standard <c>traceId</c>
/// extension member — RFC 7807-legal and the only member the factory adds;
/// the contract pinned by tests is the core set plus optional <c>errors</c>.
/// </para>
/// </remarks>
public static class ProblemDetailsHelpers
{
    /// <summary>The RFC 7807 media type (§13.12).</summary>
    public const string ProblemContentType = "application/problem+json";

    /// <summary>
    /// Builds the §13.12 problem document for <paramref name="status"/> and
    /// returns it as an <see cref="IResult"/> that sets the status code and
    /// the <c>application/problem+json</c> content type.
    /// </summary>
    /// <param name="ctx">Request whose path becomes <c>instance</c>.</param>
    /// <param name="status">4xx/5xx status code.</param>
    /// <param name="type">Stable relative error-class URI (<c>/errors/…</c>).</param>
    /// <param name="title">Short human-readable summary of the error class.</param>
    /// <param name="detail">Optional occurrence-specific explanation.</param>
    /// <param name="errors">Optional field-level <c>{ name, reason }</c> entries.</param>
    public static IResult Problem(
        HttpContext ctx,
        int status,
        string type,
        string title,
        string? detail = null,
        IReadOnlyList<(string Name, string Reason)>? errors = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var problem = new ProblemDetails
        {
            Type = type,
            Title = title,
            Status = status,
            Detail = detail,
            Instance = ctx.Request.Path.Value,
        };

        if (errors is { Count: > 0 })
        {
            // Serialized through ProblemDetails.Extensions so the member
            // lands at the top level of the document (§13.12).
            problem.Extensions["errors"] = errors
                .Select(error => (object)new Dictionary<string, string>
                {
                    ["name"] = error.Name,
                    ["reason"] = error.Reason,
                })
                .ToArray();
        }

        return Results.Problem(problem);
    }
}
