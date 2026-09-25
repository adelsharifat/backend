using System.Text.Json;
using CMIS.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CMIS.Core.Middleware;

public sealed class ApiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiExceptionMiddleware> _logger;

    public ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (UnauthorizedAccessException)
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized,
                new ApiError("unauthorized", "Authentication is required."));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unhandled API exception.");
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                new ApiError("internal_error", "An unexpected server error occurred."));
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, ApiError error)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(error));
    }
}
