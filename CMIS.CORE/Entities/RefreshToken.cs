namespace CMIS.Core.Entities;

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Token { get; set; } = null!;
    public int UserId { get; set; }
    public string DeviceId { get; set; } = null!;
    public string UserAgent { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
}