using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CMIS.PIPE.Controllers;

[ApiController]
[Route("api/pipe")]
public sealed class PipeController : ControllerBase
{
    [HttpGet("healthy")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Healthy() => Ok(new
    {
        status = "Healthy",
        module = PipeModule.ModuleName,
        version = PipeModule.ModuleVersion,
        timestamp = DateTime.UtcNow
    });
}
