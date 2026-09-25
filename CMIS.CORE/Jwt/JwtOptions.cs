namespace CMIS.Core.Jwt;

public class JwtOptions
{
    public string Issuer { get; set; } = null!;
    public string Audience { get; set; } = null!;
    public string SecretKey { get; set; } = null!;
    public int AccessTokenExpirationMinutes { get; set; } = 15;
    public int RefreshTokenExpirationDays { get; set; } = 30;
}