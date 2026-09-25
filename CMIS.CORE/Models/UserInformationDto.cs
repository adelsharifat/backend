namespace CMIS.Core.Models;

public sealed record UserInformationDto(
    int Id,
    string UserName,
    string? FirstName,
    string? LastName,
    string? DisplayName,
    string? FirstNameEn,
    string? LastNameEn,
    string? DisplayNameEn,
    byte[]? ProfileImage,
    string? ProfileImageContentType);
