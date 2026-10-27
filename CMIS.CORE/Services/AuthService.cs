using CMIS.Core.Db;
using CMIS.Core.Entities;
using CMIS.Core.Jwt;
using CMIS.Core.Security;
using CMIS.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Data;
using System.Security.Claims;

namespace CMIS.Core.Services;

public class AuthService : IAuthService
{
    private readonly DAL _db;
    private readonly JwtOptions _jwtOpts;

    public AuthService(DAL db, IOptions<JwtOptions> jwtOpts)
    {
        _db = db;
        _jwtOpts = jwtOpts.Value;
    }

    public async Task<(string? AccessToken, string? RefreshToken, bool Success, string? Error)> SignInAsync(
        string username, string password, int projectId, string deviceId, string userAgent)
    {
        // This deployment currently exposes project 1 only. Validate the selection
        // on the server; a browser-supplied project must never select arbitrary data.
        if (projectId != 1)
            return (null, null, false, "The selected project is not available.");
        // 1️⃣ Get user
        UserCredential user;
        try
        {
            user = await _db.QuerySingleAsync(
                "CM.sp_GetUserByUserName",
                r => new UserCredential(
                    int.Parse(r["Id"].ToString()!),
                    (string)r["UserName"],
                    (string)r["Password"]),
                new SqlParameter("@UserName", SqlDbType.NVarChar, 100) { Value = username });
        }
        catch (InvalidOperationException)
        {
            return (null, null, false, "Invalid username or password.");
        }

        // ---- MD5 verification ----
        var suppliedHash = Md5Helper.ComputeHash(password);
        if (!string.Equals(suppliedHash, user.PasswordHash, StringComparison.OrdinalIgnoreCase))
            return (null, null, false, "Invalid username or password.");

        var refreshToken = Guid.NewGuid().ToString("N");

        var refreshEntity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = refreshToken,
            UserId = user.Id,
            DeviceId = deviceId,
            UserAgent = userAgent,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(_jwtOpts.RefreshTokenExpirationDays),
            IsRevoked = false
        };
        
        // 2️⃣ Create tokens
        var extraClaims = new[] { new Claim("project", projectId.ToString()) };

        var accessToken = JwtHelper.GenerateAccessToken(user.Id, refreshEntity.Id, extraClaims, _jwtOpts);

        // 3️⃣ Persist refresh token
        await _db.ExecuteAsync(
            "CM.sp_InsertRefreshToken",
            new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = refreshEntity.Id },
            new SqlParameter("@Token", SqlDbType.NVarChar, 200) { Value = refreshEntity.Token },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = refreshEntity.UserId },
            new SqlParameter("@DeviceId", SqlDbType.NVarChar, 100) { Value = refreshEntity.DeviceId },
            new SqlParameter("@UserAgent", SqlDbType.NVarChar, 300) { Value = refreshEntity.UserAgent },
            new SqlParameter("@CreatedAt", SqlDbType.DateTime2) { Value = refreshEntity.CreatedAt },
            new SqlParameter("@ExpiresAt", SqlDbType.DateTime2) { Value = refreshEntity.ExpiresAt });

        return (accessToken, refreshToken, true, null);
    }

    public async Task SignOutAsync(string refreshToken)
    {
        await _db.ExecuteAsync("CM.sp_RevokeRefreshToken",
            new SqlParameter("@Token", SqlDbType.NVarChar, 200) { Value = refreshToken });
    }

    public async Task<(string AccessToken, string RefreshToken, bool success, string error)> RefreshAsync(string refreshToken)
    {
        // fetch current token
        var cur = await _db.QuerySingleAsync(
            "CM.sp_GetRefreshToken",
            r => new RefreshToken
            {
                Id = (Guid)r["Id"],
                Token = (string)r["Token"],
                UserId = (int)r["UserId"],
                DeviceId = (string)r["DeviceId"],
                UserAgent = (string)r["UserAgent"],
                CreatedAt = (DateTime)r["CreatedAt"],
                ExpiresAt = (DateTime)r["ExpiresAt"],
                IsRevoked = (bool)r["IsRevoked"]
            },
            new SqlParameter("@Token", SqlDbType.NVarChar, 200) { Value = refreshToken });

        if (cur.IsRevoked)
            return (null, refreshToken, false, "Token is revoked");

        if (cur.ExpiresAt < DateTime.UtcNow)
            return (null, refreshToken, false, "Token is expired");

        // rotate
        var newRefresh = Guid.NewGuid().ToString("N");
        var newId = Guid.NewGuid();
        var newExpires = DateTime.UtcNow.AddDays(_jwtOpts.RefreshTokenExpirationDays);

        await _db.ExecuteAsync(
            "CM.sp_RotateRefreshToken",
            new SqlParameter("@OldToken", SqlDbType.NVarChar, 200) { Value = refreshToken },
            new SqlParameter("@NewId", SqlDbType.UniqueIdentifier) { Value = newId },
            new SqlParameter("@NewToken", SqlDbType.NVarChar, 200) { Value = newRefresh },
            new SqlParameter("@ExpiresAt", SqlDbType.DateTime2) { Value = newExpires });

        var extraClaims = new[] { new Claim("project", "1") };
        var newAccess = JwtHelper.GenerateAccessToken(cur.UserId, newId, extraClaims, _jwtOpts);
        return (newAccess, newRefresh, true, null);
    }

    public async Task<IReadOnlyList<RefreshToken>> GetActiveSessionsAsync(int userId)
    {
        var list = await _db.QueryAsync(
            "CM.sp_GetActiveSessions",
            r => new RefreshToken
            {
                Id = (Guid)r["Id"],
                DeviceId = (string)r["DeviceId"],
                UserAgent = (string)r["UserAgent"],
                CreatedAt = (DateTime)r["CreatedAt"],
                ExpiresAt = (DateTime)r["ExpiresAt"]
            },
            new SqlParameter("@UserId", userId));

        return list;
    }

    public async Task RevokeSessionAsync(Guid refreshTokenId, int userId)
    {
        await _db.ExecuteAsync(
            "CM.sp_RevokeSessionById",
            new SqlParameter("@RefreshTokenId", SqlDbType.UniqueIdentifier) { Value = refreshTokenId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId }
            );
    }

    public async Task<IReadOnlyList<Guid>> RevokeOtherSessionsAsync(int userId, Guid currentSessionId)
    {
        var sessionIds = await _db.QueryAsync(
            "CM.sp_RevokeOtherSessions",
            r => (Guid)r["Id"],
            new SqlParameter("@UserId", SqlDbType.Int)
            {
                Value = userId
            },
            new SqlParameter("@CurrentSessionId", SqlDbType.UniqueIdentifier)
            {
                Value = currentSessionId
            });

        return sessionIds;
    }

    public async Task RevokeAllSessionsAsync(int userId)
    {
        await _db.ExecuteAsync(
            "CM.sp_RevokeAllSessions",
            new SqlParameter("@UserId", userId));
    }

    public async Task<bool> IsSessionActiveAsync(Guid sessionId, int userId)
    {
        try
        {
            var session = await _db.QuerySingleAsync(
                "CM.sp_GetSessionStatus",
                r => new
                {
                    UserId = (int)r["UserId"],
                    IsRevoked = (bool)r["IsRevoked"],
                    ExpiresAt = (DateTime)r["ExpiresAt"]
                },
                new SqlParameter("@SessionId", SqlDbType.UniqueIdentifier)
                {
                    Value = sessionId
                });

            return session.UserId == userId
                && !session.IsRevoked
                && session.ExpiresAt > DateTime.UtcNow;
        }
        catch (InvalidOperationException)
        {
            // Session doesn't exist
            return false;
        }
    }

    public async Task<UserInformationDto> GetUserInformationAsync(int userId)
    {
        return await _db.QuerySingleAsync(
            "CM.sp_GetUserInformation",
            r => new UserInformationDto(
                Id: (int)r["Id"],
                UserName: (string)r["UserName"],
                FirstName: r["FirstName"] as string,
                LastName: r["LastName"] as string,
                DisplayName: r["DisplayName"] as string,
                FirstNameEn: r["FirstNameEn"] as string,
                LastNameEn: r["LastNameEn"] as string,
                DisplayNameEn: r["DisplayNameEn"] as string,
                ProfileImage: r["ProfileImage"] is DBNull ? null : (byte[])r["ProfileImage"],
                ProfileImageContentType: GetImageContentType(r["ProfileImage"] is DBNull ? null : (byte[])r["ProfileImage"])),
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });
    }

    private static string? GetImageContentType(byte[]? image)
    {
        if (image is null || image.Length < 4)
            return null;

        if (image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF)
            return "image/jpeg";
        if (image[0] == 0x89 && image[1] == 0x50 && image[2] == 0x4E && image[3] == 0x47)
            return "image/png";
        if (image[0] == 0x47 && image[1] == 0x49 && image[2] == 0x46)
            return "image/gif";
        if (image.Length >= 12 && image[0] == 0x52 && image[1] == 0x49 && image[2] == 0x46 && image[3] == 0x46
            && image[8] == 0x57 && image[9] == 0x45 && image[10] == 0x42 && image[11] == 0x50)
            return "image/webp";

        return "application/octet-stream";
    }

    private sealed record UserCredential(
        int Id,
        string UserName,
        string PasswordHash);
}
