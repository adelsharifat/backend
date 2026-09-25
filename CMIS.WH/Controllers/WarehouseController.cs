using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;

namespace CMIS.WH.Controllers;

[ApiController]
[Route("api/warehouse")]
public sealed class WarehouseController : ControllerBase
{
    [HttpGet("healthy")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Healthy() => Ok(new
    {
        status = "Healthy",
        module = WarehouseModule.ModuleName,
        version = WarehouseModule.ModuleVersion,
        timestamp = DateTime.UtcNow
    });
}
