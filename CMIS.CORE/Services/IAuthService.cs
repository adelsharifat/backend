using System.Security.Claims;
using CMIS.Core.Entities;
using CMIS.Core.Models;

namespace CMIS.Core.Services;

public interface IAuthService
{
    Task<(string? AccessToken, string? RefreshToken, bool Success, string? Error)> SignInAsync(string username, string password, string deviceId, string userAgent);
    Task SignOutAsync(string refreshToken);
    Task<(string AccessToken, string RefreshToken, bool success, string error)> RefreshAsync(string refreshToken);
    Task<IReadOnlyList<RefreshToken>> GetActiveSessionsAsync(int userId);
    Task RevokeSessionAsync(Guid refreshTokenId, int userId);
    Task<IReadOnlyList<Guid>> RevokeOtherSessionsAsync(int userId, Guid currentSessionId);
    Task RevokeAllSessionsAsync(int userId);
    Task<bool> IsSessionActiveAsync(Guid sessionId, int userId);
    Task<UserInformationDto> GetUserInformationAsync(int userId);
}
