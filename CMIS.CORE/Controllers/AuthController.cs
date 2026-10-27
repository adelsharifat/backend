using CMIS.Core.Jwt;
using CMIS.Core.Models;
using CMIS.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;

namespace CMIS.Core.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly JwtOptions _jwtOpts;
    public AuthController(IAuthService auth, IOptions<JwtOptions> jwtOpts)
    {
        _auth = auth;
        _jwtOpts = jwtOpts.Value;
    }

    // POST api/auth/login
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.UserName) || string.IsNullOrWhiteSpace(dto.Password) || dto.ProjectId <= 0)
        {
            return BadRequest(new
            {
                code = "invalid_request",
                message = "Username, password, and a valid project are required."
            });
        }

        try
        {
            var deviceId = Request.Cookies["DeviceId"] ?? Guid.NewGuid().ToString("N");
            var result = await _auth.SignInAsync(dto.UserName, dto.Password, dto.ProjectId, deviceId, Request.Headers["User-Agent"]);
            if (!result.Success)
            {
                return Unauthorized(new
                {
                    code = "invalid_credentials",
                    message = result.Error ?? "Invalid username or password."
                });
            }

            var access = result.AccessToken!;
            var refresh = result.RefreshToken!;

            // Set the HttpOnly refresh-token cookie only after credentials succeed.
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Expires = DateTime.UtcNow.AddDays(_jwtOpts.RefreshTokenExpirationDays)
            };
            Response.Cookies.Append("RefreshToken", refresh, cookieOptions);
            Response.Cookies.Append("DeviceId", deviceId, new CookieOptions { Expires = DateTime.UtcNow.AddYears(1) });

            return Ok(new { accessToken = access });
        }
        catch (InvalidOperationException)
        {
            return Unauthorized(new
            {
                code = "invalid_credentials",
                message = "Invalid username or password."
            });
        }
    }

    // POST api/auth/refresh
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        try
        {
            if (!Request.Cookies.TryGetValue("RefreshToken", out var refreshToken))
                return Unauthorized();

            var (newAccess, newRefresh, success, error) = await _auth.RefreshAsync(refreshToken);

            if (!success)
                return Unauthorized(error);

            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Expires = DateTime.UtcNow.AddDays(_jwtOpts.RefreshTokenExpirationDays)
            };
            Response.Cookies.Append("RefreshToken", newRefresh, cookieOptions);
            return Ok(new { accessToken = newAccess });
        }
        catch (Exception err)
        {
            return Unauthorized("");
        }
    }

    // POST api/auth/logout
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        try
        {
            if (Request.Cookies.TryGetValue("RefreshToken", out var refresh))
                await _auth.SignOutAsync(refresh);
        }
        finally
        {
            // Logout must remain callable when the access token has expired.
            // Explicitly match the root path used when the cookie is created.
            Response.Cookies.Delete("RefreshToken", new CookieOptions
            {
                Path = "/",
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict
            });

            // Remove cookies created by older versions that used the browser's
            // default /api/auth path.
            Response.Cookies.Delete("RefreshToken", new CookieOptions
            {
                Path = "/api/auth",
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict
            });
        }

        return Ok();
    }
}
