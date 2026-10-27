using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using CMIS.Core.Db;
using CMIS.Core.Models;
using CMIS.Core.Security;
using CMIS.Core.Security.Acl;
using CMIS.PIPE.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace CMIS.PIPE.Controllers;

[ApiController]
[Route("api/pipe/base-info")]
[Authorize]
public sealed class PipeBaseInfoController : ControllerBase
{
    private readonly DAL _dal;
    private readonly IPermissionService _permissions;
    private readonly AclRegistry _registry;

    public PipeBaseInfoController(DAL dal, IPermissionService permissions, AclRegistry registry)
    {
        _dal = dal;
        _permissions = permissions;
        _registry = registry;
    }

    /// <summary>
    /// Row-level guard of every endpoint that takes a company id (@Acl = PIPE.COMPANY).
    /// The id comes from the root combo of the client, so it is checked here as well: a value typed
    /// into the request by hand is a 403, not a read or a write in another company's data.
    /// </summary>
    private async Task<IActionResult?> GuardCompanyAsync(int companyId, CancellationToken cancellationToken)
    {
        var guard = await _permissions.CheckAllowedItemsAsync(PipeAcl.COMPANY, [companyId], cancellationToken);
        return guard.Allowed ? null : this.AclItemsDenied(PipeAcl.COMPANY, guard.Denied);
    }

    /// <summary>
    /// The Items permission behind a combo list. A screen may name its own permission
    /// (ICompanyAwareConfigure.GetCompanyAcl()), so the name is validated against the catalogue
    /// instead of being trusted: an unknown or non-Items name is null, which the endpoint turns
    /// into a 404 rather than an unfiltered list.
    /// </summary>
    private AclDefinition? ResolveItemsPermission(string? requested, string fallback)
    {
        var definition = _registry.Find(string.IsNullOrWhiteSpace(requested) ? fallback : requested);
        return definition is { Kind: AclPermissionKind.Items } ? definition : null;
    }

    /// <summary>
    /// Companies the caller may use, filtered by the Items permission named in acl
    /// (default PIPE.COMPANY, legacy CM.GetCompaniesCombo @Acl).
    /// </summary>
    [HttpGet("companies")]
    public async Task<IActionResult> GetCompanies([FromQuery] string? acl, CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();

        var permission = ResolveItemsPermission(acl, PipeAcl.COMPANY);
        if (permission is null)
            return NotFound(new ApiError("acl_unknown", "The requested ACL is not a declared Items permission."));
        if (!await _permissions.IsAllowedAsync(permission.QualifiedName, cancellationToken))
            return this.AclDenied(permission.QualifiedName);

        var table = await _dal.QueryTableAsync("CM.GetCompaniesCombo", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@Acl", SqlDbType.NVarChar, 100) { Value = permission.QualifiedName });
        return Ok(ToRows(table));
    }

    /// <summary>
    /// Contracts the caller may use, filtered by the Items permission named in acl
    /// (default PIPE.CONTRACTS, legacy CM.GetAllowedContracts @Schema @Acl).
    /// </summary>
    [HttpGet("contracts")]
    public async Task<IActionResult> GetAllowedContracts(
        [FromQuery] string? acl,
        [FromQuery] string? schema,
        CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();

        var permission = ResolveItemsPermission(acl, PipeAcl.CONTRACTS);
        if (permission is null)
            return NotFound(new ApiError("acl_unknown", "The requested ACL is not a declared Items permission."));
        if (!await _permissions.IsAllowedAsync(permission.QualifiedName, cancellationToken))
            return this.AclDenied(permission.QualifiedName);

        var table = await _dal.QueryTableAsync("CM.GetAllowedContracts", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@Schema", SqlDbType.NVarChar, 50) { Value = string.IsNullOrWhiteSpace(schema) ? permission.Schema : schema.Trim() },
            new SqlParameter("@Acl", SqlDbType.NVarChar, 100) { Value = permission.Name });
        return Ok(ToRows(table));
    }

    [HttpGet("units")]
    public async Task<IActionResult> GetUnits(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetUnits", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    /// <summary>Look-ups of the line-list form, behind the front-door permission of the module.</summary>
    [HttpGet("line-list/lookups")]
    [RequireAcl(PipeAcl.LINELIST)]
    public async Task<IActionResult> GetLineListLookups(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var unitsTask = _dal.QueryTableAsync("PIPE.GetUnits", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        var configTask = _dal.QueryTableAsync("CM.GetConfig", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@Scope", SqlDbType.NVarChar, 50) { Value = "PIPE" });
        var rtTask = _dal.QueryTableAsync("PIPE.GetRTLabels", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        var ndtTask = _dal.QueryTableAsync("QCPIPE.GetNDTSpec", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        await Task.WhenAll(unitsTask, configTask, rtTask, ndtTask);

        var config = configTask.Result.Rows.Cast<DataRow>().Where(row => !row.IsNull("Name"))
            .ToDictionary(row => Convert.ToString(row["Name"])!, row => row.IsNull("Value") ? Array.Empty<string>() :
                Convert.ToString(row["Value"])!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        string[] Values(string key, params string[] fallback) => config.TryGetValue(key, out var values) && values.Length > 0 ? values : fallback;

        return Ok(new
        {
            units = unitsTask.Result.Rows.Cast<DataRow>().Select(row => new
            {
                id = Convert.ToInt32(row["Id"]),
                name = Convert.ToString(row.Table.Columns.Contains("Unit") ? row["Unit"] : row["Name"]) ?? string.Empty,
                abbreviation = row.Table.Columns.Contains("Abbreviation") && !row.IsNull("Abbreviation") ? Convert.ToString(row["Abbreviation"]) : null
            }).ToArray(),
            pipeClasses = Values("pipeClassItems"),
            pipeSizes = Values("pipeSize", "0.5", "0.75", "1", "1.5", "2", "3", "4", "6", "8", "10", "12", "14", "16", "18", "20", "24", "30", "36"),
            agUg = Values("agUg", "AG", "UG"),
            vendorLine = Values("VendorLine", "NO", "YES"),
            insulationThickness = Values("InsulationTHK", "", "25", "40", "50", "60", "65", "70", "80", "100", "120", "130", "200"),
            insulationType = Values("InsulationType", "", "CS", "MW", "NO"),
            tracingNumbers = Values("TracingNo", "", "1", "2", "3", "4"),
            tracingSizes = Values("TracingSize", "", "3/8\"", "1/2\"", "3/4\"", "1\""),
            phases = Values("Phase", "", "-", "LQ", "LQ/VAP", "MIX", "VAP"),
            testPressureFluids = Values("TestPressureFluid", "", "-", "Water", "Air", "Visual"),
            paintCodes = Values("PaintCode", "", "-", "A", "B", "C", "D", "E", "F"),
            rtLabels = ToRows(rtTask.Result),
            ndtSpecs = ToRows(ndtTask.Result)
        });
    }

    [HttpPost("line-list")]
    public async Task<IActionResult> SaveLineList([FromQuery] int companyId, [FromBody] LineListSaveRequest request, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || request.UnitId <= 0 || string.IsNullOrWhiteSpace(request.RefNo) || string.IsNullOrWhiteSpace(request.RevNo) ||
            !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();

        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;

        // Creating and editing a line list are two different permissions (the legacy tiles
        // ROLE_NEW_LINELIST and ROLE_LINE_LIST_EDIT).
        var required = request.Id is > 0 ? PipeAcl.LINE_LIST_EDIT : PipeAcl.NEW_LINELIST;
        if (!await _permissions.IsAllowedAsync(required, cancellationToken)) return this.AclDenied(required);

        var lineType = await _dal.GetTableParameterTypeAsync("PIPE.SaveLineList", "@Lines", cancellationToken);
        var fileType = await _dal.GetTableParameterTypeAsync("PIPE.SaveLineList", "@Files", cancellationToken);
        var lines = await CreateTypedTableAsync(lineType, request.Lines, cancellationToken);
        var files = await CreateTypedTableAsync(fileType, request.Files, cancellationToken);
        var id = new SqlParameter("@Id", SqlDbType.Int) { Direction = request.Id is > 0 ? ParameterDirection.Input : ParameterDirection.Output };
        if (request.Id is > 0) id.Value = request.Id.Value;

        await _dal.ExecuteAsync("PIPE.SaveLineList", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@Build", SqlDbType.Int) { Value = 1 },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@UnitId", SqlDbType.Int) { Value = request.UnitId },
            new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId },
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@RefNo", SqlDbType.NVarChar, 100) { Value = request.RefNo.Trim() },
            new SqlParameter("@RevNo", SqlDbType.NVarChar, 50) { Value = request.RevNo.Trim() },
            new SqlParameter("@Remark", SqlDbType.NVarChar, -1) { Value = (object?)request.Remark ?? DBNull.Value },
            new SqlParameter("@RecieveDate", SqlDbType.DateTime) { Value = request.ReceiveDate },
            StructuredParameter("@Lines", lineType, lines),
            StructuredParameter("@Files", fileType, files),
            id);
        var savedId = request.Id ?? Convert.ToInt32(id.Value);
        var savedLines = await _dal.QueryTableAsync("PIPE.GetLineListLines", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@LineListId", SqlDbType.Int) { Value = savedId });
        var responseLines = ToRows(savedLines).Select(row =>
        {
            row["rowKey"] = row.TryGetValue("id", out var rowId) && rowId is not null ? Convert.ToString(rowId)! : Guid.NewGuid().ToString();
            if (row.ContainsKey("modified")) row["modified"] = false;
            return row;
        }).ToArray();
        return Ok(new { id = savedId, lines = responseLines });
    }

    [HttpGet("contractor-estimates")]
    public async Task<IActionResult> GetContractorEstimates([FromQuery] int companyId, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var table = await _dal.QueryTableAsync("PIPE.GetContractorEstimateGrid", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });
        return Ok(ToRows(table));
    }

    [HttpPut("contractor-estimates")]
    public async Task<IActionResult> SaveContractorEstimates([FromQuery] int companyId, [FromBody] JsonElement rows, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var items = CreateTable(("Id", typeof(int)), ("ContractId", typeof(int)), ("UnitId", typeof(int)),
            ("AgUg", typeof(string)), ("EstimateQty", typeof(decimal)), ("IsModified", typeof(bool)),
            ("IsDelete", typeof(bool)), ("Show", typeof(bool)));
        FillTable(items, rows);
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SaveContractorEstimate", "@Items", cancellationToken);
        await _dal.ExecuteAsync("PIPE.SaveContractorEstimate", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            StructuredParameter("@Items", typeName, items));
        return NoContent();
    }

    [HttpGet("contractor-priorities")]
    public async Task<IActionResult> GetContractorPriorities([FromQuery] int companyId, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var table = await _dal.QueryTableAsync("PIPE.GetContractorPriorityGrid", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });
        return Ok(ToRows(table));
    }

    [HttpPut("contractor-priorities")]
    public async Task<IActionResult> SaveContractorPriorities([FromQuery] int companyId, [FromBody] JsonElement rows, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var items = CreateTable(("ContractId", typeof(string)), ("UnitId", typeof(string)),
            ("Priority", typeof(int)), ("IsDelete", typeof(bool)));
        FillTable(items, rows);
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SaveContractorPriority", "@Data", cancellationToken);
        await _dal.ExecuteAsync("PIPE.SaveContractorPriority", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            StructuredParameter("@Data", typeName, items));
        return NoContent();
    }

    [HttpGet("line-contracts")]
    public async Task<IActionResult> GetLineContracts([FromQuery] int companyId, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var table = await _dal.QueryTableAsync("PIPE.GetLinesContracts", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId }, new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId }, new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });
        return Ok(ToRows(table));
    }

    [HttpPut("line-contracts")]
    public async Task<IActionResult> SaveLineContracts([FromQuery] int companyId, [FromBody] JsonElement rows, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var items = CreateTable(("LineId", typeof(int)), ("ContractId", typeof(int)));
        FillTable(items, rows);
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SaveLinesContracts", "@Lines", cancellationToken);
        await _dal.ExecuteAsync("PIPE.SaveLinesContracts", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId }, new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId }, StructuredParameter("@Lines", typeName, items));
        return NoContent();
    }

    [HttpGet("sheet-contracts")]
    public async Task<IActionResult> GetSheetContracts([FromQuery] int companyId, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var table = await _dal.QueryTableAsync("PIPE.GetSheetContracts", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId }, new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId }, new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });
        return Ok(ToRows(table));
    }

    [HttpPut("sheet-contracts")]
    public async Task<IActionResult> SaveSheetContracts([FromQuery] int companyId, [FromBody] JsonElement rows, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var items = CreateTable(("SheetId", typeof(int)), ("ContractId", typeof(int)));
        FillTable(items, rows);
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SaveSheetContracts", "@Sheets", cancellationToken);
        await _dal.ExecuteAsync("PIPE.SaveSheetContracts", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId }, new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId }, StructuredParameter("@Sheets", typeName, items));
        return NoContent();
    }

    [HttpGet("pms-materials")]
    public async Task<IActionResult> GetPmsMaterials([FromQuery] int companyId, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var table = await _dal.QueryTableAsync("PIPE.GetPMSMatGrid", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId }, new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId });
        return Ok(ToRows(table));
    }

    [HttpGet("pms-materials/warehouse-items")]
    public async Task<IActionResult> GetWarehouseItems(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetWarehouseMaterials", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    [HttpPut("pms-materials")]
    public async Task<IActionResult> SavePmsMaterials([FromQuery] int companyId, [FromBody] JsonElement rows, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var items = CreateTable(("Id", typeof(int)), ("Unit", typeof(string)), ("Service", typeof(string)),
            ("LineNumber", typeof(string)), ("MaterialId", typeof(int)), ("Qty", typeof(decimal)), ("Modified", typeof(bool)));
        FillTable(items, rows);
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SavePMSMat", "@Items", cancellationToken);
        await _dal.ExecuteAsync("PIPE.SavePMSMat", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId }, new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId }, StructuredParameter("@Items", typeName, items));
        return NoContent();
    }

    [HttpDelete("pms-materials")]
    public async Task<IActionResult> DeletePmsMaterials([FromBody] int[] ids, CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();
        var table = CreateTable(("Id", typeof(int)));
        foreach (var id in ids.Distinct()) table.Rows.Add(id);
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.DeletePMSMat", "@Id", cancellationToken);
        await _dal.ExecuteAsync("PIPE.DeletePMSMat", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId }, new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            StructuredParameter("@Id", typeName, table));
        return NoContent();
    }

    [HttpGet("locations")]
    public async Task<ActionResult<IReadOnlyList<LocationRow>>> GetLocations(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();

        var table = await _dal.QueryTableAsync("PIPE.GetLocations", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });

        return Ok(table.Rows.Cast<DataRow>()
            .Select(row => new LocationRow(Convert.ToString(row["Location"]) ?? string.Empty, Convert.ToInt32(row["Priority"]))));
    }

    [HttpPut("locations")]
    public async Task<IActionResult> SaveLocations(
        [FromBody] IReadOnlyList<LocationRow> rows,
        CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();

        var locations = new DataTable();
        locations.Columns.Add("Location", typeof(string));
        locations.Columns.Add("Priority", typeof(int));
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Location))
                return BadRequest(new { message = "Each location needs a name." });

            locations.Rows.Add(row.Location.Trim(), row.Priority);
        }

        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SaveLocations", "@Locations", cancellationToken);
        var locationsParameter = StructuredParameter("@Locations", typeName, locations);

        await _dal.ExecuteAsync("PIPE.SaveLocations", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            locationsParameter);

        return NoContent();
    }

    [HttpGet("pms-thickness")]
    [RequireAcl(PipeAcl.PMS_THK)]
    public async Task<ActionResult<PmsThicknessData>> GetPmsThickness(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out _)) return Unauthorized();

        var table = await _dal.QueryTableAsync("PIPE.GetPMSThkGrid", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });

        var rows = table.Rows.Cast<DataRow>().Select(row => new PmsThicknessRow(
            Id: Convert.ToInt32(row["Id"]),
            PipeClass: Convert.ToString(row["PipeClass"]) ?? string.Empty,
            Size: Convert.ToDecimal(row["Size"]),
            Thk: row.IsNull("Thk") ? null : Convert.ToDecimal(row["Thk"]),
            Mat: NullIfDbNull(row["Mat"]),
            MainMat: NullIfDbNull(row["MainMat"]),
            PWHT: NullIfDbNull(row["PWHT"]),
            Sch: NullIfDbNull(row["Sch"]),
            InvoiceMaterial: NullIfDbNull(row["InvoiceMaterial"]),
            InvoiceSchedule: NullIfDbNull(row["InvoiceSchedule"]),
            Modified: !row.IsNull("Modified") && Convert.ToBoolean(row["Modified"]))).ToArray();
        var pwhtValues = await GetPwhtValues(projectId, cancellationToken);

        // The screen shows the grid to everyone allowed to read it, but only unlocks editing
        // with PMS_THK_EDIT. This is the "Deny = visible but disabled" state of the client ACL,
        // not a permission check: the write endpoints are guarded on the server.
        var canEdit = await _permissions.IsAllowedAsync(PipeAcl.PMS_THK_EDIT, cancellationToken);
        return Ok(new PmsThicknessData(rows, pwhtValues, canEdit));
    }

    [HttpPut("pms-thickness")]
    [RequireAcl(PipeAcl.PMS_THK_EDIT)]
    public async Task<IActionResult> SavePmsThickness(
        [FromBody] IReadOnlyList<PmsThicknessRow> rows,
        CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();

        var allowedPwht = await GetPwhtValues(projectId, cancellationToken);
        if (rows.Any(row => !string.IsNullOrWhiteSpace(row.PWHT) && !allowedPwht.Contains(row.PWHT, StringComparer.Ordinal)))
            return BadRequest(new { message = "PWHT must be one of the configured project values." });

        var items = new DataTable();
        items.Columns.Add("Id", typeof(int));
        items.Columns.Add("PipeClass", typeof(string));
        items.Columns.Add("Size", typeof(decimal));
        items.Columns.Add("Thk", typeof(decimal));
        items.Columns.Add("Mat", typeof(string));
        items.Columns.Add("MainMat", typeof(string));
        items.Columns.Add("PWHT", typeof(string));
        items.Columns.Add("Sch", typeof(string));
        items.Columns.Add("InvoiceMaterial", typeof(string));
        items.Columns.Add("InvoiceSchedule", typeof(string));
        items.Columns.Add("Modified", typeof(bool));

        foreach (var row in rows)
            items.Rows.Add(row.Id, row.PipeClass, row.Size, row.Thk ?? (object)DBNull.Value,
                row.Mat ?? (object)DBNull.Value, row.MainMat ?? (object)DBNull.Value,
                row.PWHT ?? (object)DBNull.Value, row.Sch ?? (object)DBNull.Value,
                row.InvoiceMaterial ?? (object)DBNull.Value, row.InvoiceSchedule ?? (object)DBNull.Value,
                row.Modified);

        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SavePMSThk", "@Items", cancellationToken);
        await _dal.ExecuteAsync("PIPE.SavePMSThk", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            StructuredParameter("@Items", typeName, items));

        return NoContent();
    }

    [HttpDelete("pms-thickness")]
    [RequireAcl(PipeAcl.PMS_THK_EDIT)]
    public async Task<IActionResult> DeletePmsThickness([FromBody] int[] ids, CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();
        var table = CreateTable(("Id", typeof(int)));
        foreach (var id in ids.Distinct()) table.Rows.Add(id);
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.DeletePMSThk", "@Id", cancellationToken);
        await _dal.ExecuteAsync("PIPE.DeletePMSThk", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId }, new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            StructuredParameter("@Id", typeName, table));
        return NoContent();
    }

    [HttpGet("material-list")]
    public async Task<IActionResult> GetMaterialList(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetMaterialList", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    [HttpPut("material-list")]
    public async Task<IActionResult> SaveMaterialList([FromBody] JsonElement rows, CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();
        var items = CreateTable(
            ("MaterialId", typeof(long)), ("Punchtype", typeof(string)), ("Category", typeof(string)),
            ("MaterialTypeId", typeof(int)), ("MainSize", typeof(string)), ("Size1", typeof(decimal)),
            ("Size2", typeof(decimal)), ("Sch1", typeof(string)), ("Sch2", typeof(string)),
            ("Rating", typeof(string)), ("Thk1", typeof(decimal)), ("Thk2", typeof(decimal)),
            ("JointType", typeof(string)), ("FaceType", typeof(string)), ("Wight", typeof(decimal)),
            ("MainMat", typeof(string)), ("TakeOf1", typeof(decimal)), ("TakeOf2", typeof(decimal)));
        FillTable(items, rows);
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SaveMaterialList", "@Materials", cancellationToken);
        await _dal.ExecuteAsync("PIPE.SaveMaterialList", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            StructuredParameter("@Materials", typeName, items));
        return NoContent();
    }

    [HttpGet("material-list/types")]
    public async Task<IActionResult> GetMaterialTypes(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetMaterialTypeComboForBaseInfoMaterialList", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    [HttpGet("cladding")]
    public async Task<IActionResult> GetCladdings(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetAllMatCladdings", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    [HttpPut("cladding")]
    public async Task<IActionResult> SaveCladdings([FromBody] JsonElement rows, CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId) ||
            !Guid.TryParse(User.FindFirstValue("sid"), out var sessionId)) return Unauthorized();
        var items = CreateTable(
            ("Id", typeof(int)), ("CladdingMat", typeof(string)), ("Size1", typeof(decimal)),
            ("Size2", typeof(decimal)), ("Class", typeof(int)), ("TraceNo", typeof(int)),
            ("SizeTrace", typeof(string)), ("InsulThk", typeof(int)), ("Cladding", typeof(int)),
            ("isDelete", typeof(bool)));
        FillTable(items, rows);
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SaveMatCladdings", "@Items", cancellationToken);
        await _dal.ExecuteAsync("PIPE.SaveMatCladdings", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@SessionId", SqlDbType.UniqueIdentifier) { Value = sessionId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            StructuredParameter("@Items", typeName, items));
        return NoContent();
    }

    [HttpGet("pipe-class-codes")]
    public async Task<IActionResult> GetPipeClassCodes(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetPipeClassItemCodesGrid", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    [HttpGet("pipe-class-codes/materials")]
    public async Task<IActionResult> GetPipeClassCodeMaterials(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetMaterialsForItemCodePipeClasses", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    [HttpPut("pipe-class-codes")]
    public async Task<IActionResult> SavePipeClassCodes([FromBody] JsonElement rows, CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId) ||
            !Guid.TryParse(User.FindFirstValue("sid"), out var sessionId)) return Unauthorized();
        var items = CreateTable(("Id", typeof(int)), ("PipeClass", typeof(string)), ("ItemCode", typeof(int)), ("isDelete", typeof(bool)));
        FillTable(items, rows);
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SavePipeClassItemCodes", "@Items", cancellationToken);
        await _dal.ExecuteAsync("PIPE.SavePipeClassItemCodes", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@SessionId", SqlDbType.UniqueIdentifier) { Value = sessionId },
            StructuredParameter("@Items", typeName, items));
        return NoContent();
    }

    [HttpGet("material-details")]
    public async Task<IActionResult> GetMaterialDetails(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetMaterialDetails", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(table.Rows.Cast<DataRow>().Select(row => new MaterialDetailRow(
            Id: Convert.ToInt32(row["MaterialId"]),
            SurfaceAreaPipingM2: ReadDecimal(row, "SurfaceAreaPiping(M2 )"),
            InstalLengthPipingMt: ReadDecimal(row, "InstalLengthPiping(mt)"),
            MatCladding: NullIfDbNull(row["MatCladding"]))));
    }

    [HttpPut("material-details")]
    public async Task<IActionResult> SaveMaterialDetails([FromBody] IReadOnlyList<MaterialDetailRow> rows, CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();
        var items = CreateTable(("Id", typeof(int)), ("SurfaceAreaPiping(M2 )", typeof(decimal)),
            ("InstalLengthPiping(mt)", typeof(decimal)), ("MatCladding", typeof(string)));
        foreach (var row in rows.Where(row => row.Modified))
            items.Rows.Add(row.Id, row.SurfaceAreaPipingM2 ?? (object)DBNull.Value,
                row.InstalLengthPipingMt ?? (object)DBNull.Value, row.MatCladding ?? (object)DBNull.Value);
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SaveMaterialDetails", "@Items", cancellationToken);
        await _dal.ExecuteAsync("PIPE.SaveMaterialDetails", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            StructuredParameter("@Items", typeName, items));
        return NoContent();
    }

    private bool TryGetProjectId(out int projectId) => int.TryParse(User.FindFirstValue("project"), out projectId);

    private bool TryGetUserId(out int userId) => int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out userId);

    // Permission checks used to live here (HasPipePermission / GetPipePermissions, with a private
    // copy of the exact-then-PIPE.* rule). They moved to the shared ACL core so the vocabulary is
    // declared in PipeAcl, the guard is an attribute, and the rule exists in exactly one place.

    private async Task<string[]> GetPwhtValues(int projectId, CancellationToken cancellationToken)
    {
        var table = await _dal.QueryTableAsync("CM.GetConfig", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@Scope", SqlDbType.NVarChar, 50) { Value = "PIPE" });
        var configured = table.Rows.Cast<DataRow>().FirstOrDefault(row =>
            string.Equals(Convert.ToString(row["Name"]), "pwht", StringComparison.OrdinalIgnoreCase));
        return configured is null || configured.IsNull("Value")
            ? ["YES", "NO"]
            : Convert.ToString(configured["Value"])!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static SqlParameter StructuredParameter(string name, string typeName, DataTable value) => new(name, SqlDbType.Structured)
    {
        TypeName = typeName,
        Value = value
    };

    private async Task<DataTable> CreateTypedTableAsync(string typeName, JsonElement rows, CancellationToken cancellationToken)
    {
        var parts = typeName.Split('.', 2);
        if (parts.Length != 2) throw new InvalidOperationException($"Invalid table type name: {typeName}");
        var schema = parts[0].Trim('[', ']');
        var name = parts[1].Trim('[', ']');
        var definition = await _dal.QueryTableAsync("""
            SELECT c.name AS ColumnName, t.name AS TypeName, c.max_length AS MaxLength, c.precision AS [Precision], c.scale AS Scale
            FROM sys.table_types tt
            JOIN sys.schemas s ON s.schema_id = tt.schema_id
            JOIN sys.columns c ON c.object_id = tt.type_table_object_id
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE s.name = @Schema AND tt.name = @TypeName
            ORDER BY c.column_id;
            """, CommandType.Text, cancellationToken,
            new SqlParameter("@Schema", SqlDbType.NVarChar, 128) { Value = schema },
            new SqlParameter("@TypeName", SqlDbType.NVarChar, 128) { Value = name });

        var table = new DataTable();
        foreach (DataRow column in definition.Rows)
            table.Columns.Add(Convert.ToString(column["ColumnName"])!, ToClrType(Convert.ToString(column["TypeName"])!)).AllowDBNull = true;

        if (rows.ValueKind != JsonValueKind.Array) return table;
        foreach (var source in rows.EnumerateArray())
        {
            var row = table.NewRow();
            foreach (DataColumn column in table.Columns)
            {
                if (!TryGetProperty(source, column.ColumnName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    row[column] = DBNull.Value;
                    continue;
                }
                row[column] = ConvertJsonValue(value, column.DataType);
            }
            table.Rows.Add(row);
        }
        return table;
    }

    private static Type ToClrType(string sqlType) => sqlType.ToLowerInvariant() switch
    {
        "bit" => typeof(bool), "tinyint" => typeof(byte), "smallint" => typeof(short), "int" => typeof(int), "bigint" => typeof(long),
        "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal), "float" => typeof(double), "real" => typeof(float),
        "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime), "uniqueidentifier" => typeof(Guid),
        "binary" or "varbinary" or "image" or "timestamp" => typeof(byte[]), _ => typeof(string)
    };

    private static object ConvertJsonValue(JsonElement value, Type targetType)
    {
        if (targetType == typeof(string)) return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
        if (targetType == typeof(bool)) return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : bool.Parse(value.ToString());
        if (targetType == typeof(DateTime)) return value.GetDateTime();
        if (targetType == typeof(Guid)) return value.GetGuid();
        if (targetType == typeof(byte[])) return value.GetBytesFromBase64();
        return Convert.ChangeType(value.ToString(), targetType, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? NullIfDbNull(object value) => value is DBNull ? null : Convert.ToString(value);
    private static decimal? ReadDecimal(DataRow row, string column) => row.IsNull(column) ? null : Convert.ToDecimal(row[column]);

    private static DataTable CreateTable(params (string Name, Type Type)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, type) in columns) table.Columns.Add(name, type).AllowDBNull = true;
        return table;
    }

    private static void FillTable(DataTable table, JsonElement rows)
    {
        if (rows.ValueKind != JsonValueKind.Array) throw new ArgumentException("Expected a list of rows.");
        foreach (var source in rows.EnumerateArray())
        {
            var row = table.NewRow();
            foreach (DataColumn column in table.Columns)
            {
                var propertyName = column.ColumnName == "Wight" ? "Weight" : column.ColumnName;
                if (!TryGetProperty(source, propertyName, out var value) && !TryGetProperty(source, column.ColumnName, out value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    row[column] = DBNull.Value;
                    continue;
                }
                row[column] = column.DataType == typeof(string) ? value.ToString() :
                    column.DataType == typeof(int) ? value.GetInt32() :
                    column.DataType == typeof(long) ? value.GetInt64() :
                    column.DataType == typeof(decimal) ? value.GetDecimal() : Convert.ChangeType(value.ToString(), column.DataType)!;
            }
            table.Rows.Add(row);
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static IReadOnlyList<Dictionary<string, object?>> ToRows(DataTable table) => table.Rows.Cast<DataRow>()
        .Select(row => table.Columns.Cast<DataColumn>().ToDictionary(
            column => JsonNamingPolicy.CamelCase.ConvertName(column.ColumnName),
            column => row.IsNull(column) ? null : row[column], StringComparer.OrdinalIgnoreCase)).ToArray();
}

public sealed record LocationRow(string Location, int Priority);

public sealed record PmsThicknessRow(
    int Id,
    string PipeClass,
    decimal Size,
    decimal? Thk,
    string? Mat,
    string? MainMat,
    string? PWHT,
    string? Sch,
    string? InvoiceMaterial,
    string? InvoiceSchedule,
    bool Modified);

public sealed record PmsThicknessData(
    IReadOnlyList<PmsThicknessRow> Rows,
    IReadOnlyList<string> PwhtValues,
    bool CanEdit);

public sealed record MaterialDetailRow(
    int Id,
    decimal? SurfaceAreaPipingM2,
    decimal? InstalLengthPipingMt,
    string? MatCladding,
    bool Modified = false);

public sealed record LineListSaveRequest(
    int? Id,
    int UnitId,
    string RefNo,
    string RevNo,
    string? Remark,
    DateTime ReceiveDate,
    JsonElement Lines,
    JsonElement Files);
