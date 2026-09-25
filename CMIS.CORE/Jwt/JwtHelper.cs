using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace CMIS.Core.Jwt;

public static class JwtHelper
{
    public static string GenerateAccessToken(int userId, Guid sessionId, IEnumerable<Claim> extraClaims, JwtOptions opts)
    {
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("sid", sessionId.ToString())
        };
        claims.AddRange(extraClaims);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opts.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: opts.Issuer,
            audience: opts.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(opts.AccessTokenExpirationMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static ClaimsPrincipal ValidateRefreshToken(string token, JwtOptions opts)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(opts.SecretKey);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = opts.Issuer,
            ValidAudience = opts.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ClockSkew = TimeSpan.Zero
        };
        return tokenHandler.ValidateToken(token, parameters, out _);
    }
}