using CMIS.Core.Hubs;
using CMIS.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace CMIS.Core.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UserController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly IHubContext<SessionHub> _hub;

    public UserController(IAuthService auth, IHubContext<SessionHub> hub)
    {
        _auth = auth;
        _hub = hub;
    }

    // GET api/user/sessions
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions()
    {
        var validUserId = int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out int userId);

        if (!validUserId || userId <= 0)
            return Unauthorized("Invalid user find in token.");

        var sessions = await _auth.GetActiveSessionsAsync(userId);
        return Ok(sessions.Select(s => new
        {
            s.Id,
            s.DeviceId,
            s.UserAgent,
            s.CreatedAt,
            s.ExpiresAt
        }));
    }

    // GET api/user/information
    [HttpGet("information")]
    public async Task<IActionResult> GetInformation()
    {
        if (!int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId) || userId <= 0)
            return Unauthorized("Invalid user in token.");

        try
        {
            return Ok(await _auth.GetUserInformationAsync(userId));
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    // DELETE api/user/sessions/{id}
    [HttpDelete("sessions/{id}")]
    public async Task<IActionResult> RevokeSession(Guid id)
    {
        var validUserId = int.TryParse(
       User.FindFirstValue(JwtRegisteredClaimNames.Sub),
       out var userId);

        if (!validUserId || userId <= 0)
            return Unauthorized("Invalid user in token.");

        await _auth.RevokeSessionAsync(id, userId);

        await _hub.Clients
            .Group($"session:{id}")
            .SendAsync("SessionRevoked");

        return NoContent();
    }

    [HttpDelete("sessions/others")]
    public async Task<IActionResult> RevokeOtherSessions()
    {
        var validUserId = int.TryParse(
            User.FindFirstValue(JwtRegisteredClaimNames.Sub),
            out var userId);

        var sessionClaim = User.FindFirstValue("sid");

        if (!validUserId ||
            userId <= 0 ||
            !Guid.TryParse(sessionClaim, out var currentSessionId))
        {
            return Unauthorized();
        }

        var revokedSessionIds =
            await _auth.RevokeOtherSessionsAsync(
                userId,
                currentSessionId);

        foreach (var sessionId in revokedSessionIds)
        {
            await _hub.Clients
                .Group($"session:{sessionId}")
                .SendAsync("SessionRevoked");
        }

        return NoContent();
    }

    // DELETE api/user/sessions
    [HttpDelete("sessions")]
    public async Task<IActionResult> RevokeAll()
    {
        var validUserId = int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out int userId);

        if (!validUserId || userId <= 0)
            return Unauthorized("Invalid user find in token.");

        await _auth.RevokeAllSessionsAsync(userId);

        return NoContent();
    }
}
