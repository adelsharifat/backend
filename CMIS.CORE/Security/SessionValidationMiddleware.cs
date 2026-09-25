using CMIS.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Claims;

namespace CMIS.Core.Security;

public class SessionValidationMiddleware
{
    private readonly RequestDelegate _next;

    public SessionValidationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IAuthService authService)
    {
        // Only validate authenticated requests
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var sessionClaim =
                context.User.FindFirst("sid")?.Value;

            var userClaim =
                context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            if (!Guid.TryParse(sessionClaim, out var sessionId) ||
                !int.TryParse(userClaim, out var userId))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var valid = await authService.IsSessionActiveAsync(
                sessionId,
                userId);

            if (!valid)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await _next(context);
    }
}