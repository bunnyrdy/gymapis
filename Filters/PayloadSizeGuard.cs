using GymApis.Exceptions;
using GymApis.Middleware;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GymApis.Filters;

/// <summary>
/// Rejects an over-sized request before anything tries to read it.
///
/// Without this, an upload over the endpoint's <c>[RequestSizeLimit]</c> is
/// caught by Kestrel *during model binding*, and MVC files it as a model error
/// with the exception's text flattened into the message — by which point the
/// real exception is gone. That produced two problems:
///
///   * the response was a 400 ("one or more fields are invalid"), which is
///     wrong and unactionable — nothing is wrong with the fields;
///   * the message quoted the exact ceiling ("The max request body size is
///     4194304 bytes"), handing anyone probing for a request-size denial of
///     service the number for free.
///
/// A resource filter runs after <c>RequestSizeLimitAttribute</c> has published
/// the limit onto the feature collection but before model binding, so this is
/// the first point where both the limit and the declared length are known. The
/// body is never read, so an over-sized request costs one header parse.
///
/// A chunked upload sends no Content-Length and still falls through to model
/// binding; that path is covered by the InvalidModelStateResponseFactory in
/// Program.cs, which refuses to echo framework-authored messages.
/// </summary>
public sealed class PayloadSizeGuard : IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var declared = context.HttpContext.Request.ContentLength;
        if (declared is null) return;

        var limit = context.HttpContext.Features
            .Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize;
        if (limit is null || declared <= limit) return;

        var problem = ApiProblem.Create(
            context.HttpContext,
            StatusCodes.Status413PayloadTooLarge,
            "Payload Too Large",
            // Deliberately no number. "Too large" is all the caller needs; the
            // limit is documented for the UI, not disclosed per-request.
            "That upload is too large.",
            ErrorCodes.PayloadTooLarge);

        context.Result = new ObjectResult(problem)
        {
            StatusCode   = StatusCodes.Status413PayloadTooLarge,
            ContentTypes = { "application/problem+json" },
        };
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
