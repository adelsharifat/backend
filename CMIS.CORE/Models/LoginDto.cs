// CMIS.Core/Models/LoginDto.cs
namespace CMIS.Core.Models;

public record LoginDto(string UserName, string Password, int ProjectId);
