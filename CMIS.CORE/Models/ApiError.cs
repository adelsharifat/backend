namespace CMIS.Core.Models;

public sealed record ApiError(
    string Code,
    string Message);
