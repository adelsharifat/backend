using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using CMIS.Core.Db;
using CMIS.Core.Models;
using CMIS.Core.Security;
using CMIS.Core.Security.Acl;
using CMIS.PIPE.Security;
using CMIS.PIPE.Data;
using CMIS.PIPE.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.SqlClient;
using Microsoft.Net.Http.Headers;

namespace CMIS.PIPE.Controllers;

[ApiController]
[Route("api/pipe")]
[Authorize]
public sealed class PipeController : ControllerBase
{
    /// <summary>
    /// The type a file is served under, guessed from its extension the way the desktop client's shell
    /// did when it launched the local copy of a file.
    /// </summary>
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly DAL _dal;
    private readonly PipeDal _pipeDal;
    private readonly IPermissionService _permissions;
    private readonly AclRegistry _registry;

    public PipeController(DAL dal, PipeDal pipeDal, IPermissionService permissions, AclRegistry registry)
    {
        _dal = dal;
        _pipeDal = pipeDal;
        _permissions = permissions;
        _registry = registry;
    }

    [AllowAnonymous]
    [HttpGet("healthy")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Healthy() => Ok(new
    {
        status = "Healthy",
        module = PipeModule.ModuleName,
        version = PipeModule.ModuleVersion,
        timestamp = DateTime.UtcNow
    });

    /// <summary>
    /// Row-level guard of every endpoint that takes a company id (@Acl = PIPE.COMPANY).
    /// The id comes from the root combo of the client, so it is checked here as well: a value typed
    /// into the request by hand is a 403, not a read or a write in another company's data.
    /// </summary>
    private async Task<IActionResult?> GuardCompanyAsync(int companyId, CancellationToken cancellationToken)
    {
        // Qualified once, so the check and the `403` message both name `PIPE.COMPANY` — the name the
        // ACL table stores — instead of the bare constant.
        var permission = _permissions.Qualify(PipeAcl.COMPANY);
        var guard = await _permissions.CheckAllowedItemsAsync(permission, [companyId], cancellationToken);
        return guard.Allowed ? null : this.AclItemsDenied(permission, guard.Denied);
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
    [HttpGet("base-info/companies")]
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
    [HttpGet("base-info/contracts")]
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

    [HttpGet("base-info/units")]
    public async Task<IActionResult> GetUnits(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetUnits", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    /// <summary>Look-ups of the line-list form, behind the front-door permission of the module.</summary>
    [HttpGet("base-info/line-list/lookups")]
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
            ndtSpecs = ToRows(ndtTask.Result),
            // The `CM.GetConfig` rows themselves (`{ "pipeSize": ["1", ...] }`). The named lists above
            // are what a combo normally uses; this lets the client resolve a key that has no list of its
            // own, and fall back to the module bundle's defaults when a key has no row either.
            config
        });
    }

    [HttpPost("base-info/line-list")]
    public async Task<IActionResult> SaveLineList(
        [FromQuery] int companyId,
        [FromBody] SaveLineListRequest request,
        CancellationToken cancellationToken)
    {
        if (companyId <= 0 || request.UnitId is not > 0 || request.Lines is null || request.Files is null ||
            string.IsNullOrWhiteSpace(request.RefNo) || string.IsNullOrWhiteSpace(request.RevNo) ||
            !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId))
            return BadRequest();

        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard)
            return companyGuard;

        // Creating and editing are separate legacy permissions.
        var required = request.Id is > 0 ? PipeAcl.LINE_LIST_EDIT : PipeAcl.NEW_LINELIST;
        if (!await _permissions.IsAllowedAsync(required, cancellationToken))
            return this.AclDenied(required);

        // The generated contract owns TVP mapping and SQL parameter metadata. Identity and scope
        // values always come from the authenticated request, never from the client payload.
        var result = await _pipeDal.SaveLineListAsync(new SaveLineListRequest
        {
            Build = 1,
            Id = request.Id,
            UserId = userId,
            UnitId = request.UnitId,
            CompanyId = companyId,
            ProjectId = projectId,
            RefNo = request.RefNo.Trim(),
            RevNo = request.RevNo.Trim(),
            Remark = request.Remark,
            RecieveDate = request.RecieveDate,
            // The SQL table column PIPE.Line.DisableRT is NOT NULL. The generated TVP
            // contract exposes its corresponding field as nullable DisableForRT, so
            // normalize omitted/null values before the stored procedure receives the rows.
            Lines = request.Lines.Select(line => line with { DisableForRT = line.DisableForRT ?? false }).ToArray(),
            Files = request.Files
        }, cancellationToken);

        var savedId = result.Id ?? request.Id;
        if (savedId is not > 0)
            throw new InvalidOperationException("PIPE.SaveLineList did not return a valid line-list id.");

        var savedLines = await _dal.QueryTableAsync("PIPE.GetLineListLines", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@LineListId", SqlDbType.Int) { Value = savedId.Value });
        var responseLines = ToRows(savedLines).Select(row =>
        {
            row["rowKey"] = row.TryGetValue("id", out var rowId) && rowId is not null
                ? Convert.ToString(rowId)!
                : Guid.NewGuid().ToString();
            if (row.ContainsKey("modified")) row["modified"] = false;
            return row;
        }).ToArray();

        return Ok(new { id = savedId.Value, lines = responseLines });
    }

    /// <summary>
    /// The lines of one company — the grid of the module (legacy <c>frmLineListGrid</c> →
    /// <c>Dal.Get().ListLines(Project.Id, LoginInfo.Id, CompanyId)</c>, the <c>PIPE.ListLines</c>
    /// procedure).
    /// </summary>
    /// <remarks>
    /// The procedure is the row-level half of the ACL: it filters the lines by the companies the caller
    /// may see, which is why it takes <c>@UserId</c>. <see cref="GuardCompanyAsync"/> re-checks the
    /// company id the client sent before the procedure runs, so a value typed into the request by hand
    /// is a 403 instead of a read of another company's lines.
    /// </remarks>
    [HttpGet("base-info/line-list/lines")]
    [RequireAcl(PipeAcl.LINELIST)]
    public async Task<IActionResult> GetLineListLines([FromQuery] int companyId, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();

        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;

        var lines = await _dal.QueryTableAsync("PIPE.ListLines", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId });
        return Ok(ToRows(lines));
    }

    /// <summary>
    /// One line list as its form edits it: the header, its lines and its files — the three reads the
    /// legacy <c>frmLineList.LoadEntity</c> made (<c>PIPE.GetLineList</c>, <c>PIPE.GetLineListLines</c>,
    /// <c>PIPE.ListLineListFiles</c>), answered in one call because a form needs all three at once.
    /// </summary>
    /// <remarks>
    /// The company of the record comes with the header, and it is what the row-level guard checks: a
    /// line list of another company is a 403 even when its id is known. A record that is not there is a
    /// 404, which the client turns into "Line list not found" and closes the tab with (legacy
    /// <c>EntityNotFoundMessage</c>).
    /// </remarks>
    [HttpGet("base-info/line-list/{id:int}")]
    [RequireAcl(PipeAcl.LINELIST)]
    public async Task<IActionResult> GetLineList(int id, CancellationToken cancellationToken)
    {
        if (id <= 0 || !TryGetProjectId(out var projectId)) return BadRequest();

        var header = await _dal.QueryTableAsync("PIPE.GetLineList", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@Id", SqlDbType.Int) { Value = id });
        if (header.Rows.Count == 0) return NotFound();

        var record = ToRows(header)[0];
        // The record's company: the row-level guard, and the value the form moves its company combo to.
        var companyId = record.TryGetValue("companyId", out var company) && company is not null ? Convert.ToInt32(company) : 0;
        if (companyId > 0 && await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;

        var linesTask = _dal.QueryTableAsync("PIPE.GetLineListLines", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@LineListId", SqlDbType.Int) { Value = id });
        var filesTask = _dal.QueryTableAsync("PIPE.ListLineListFiles", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@Id", SqlDbType.Int) { Value = id });
        await Task.WhenAll(linesTask, filesTask);

        // `PIPE.GetLineList` spells the date `RecieveDate` (the legacy name); the form asks for the day it
        // holds, so the camel-cased column travels under both names.
        if (record.TryGetValue("recieveDate", out var receiveDate)) record["receiveDate"] = receiveDate;
        record["projectId"] = projectId;
        record["lines"] = ToRows(linesTask.Result);
        record["files"] = ToRows(filesTask.Result);
        return Ok(record);
    }

    /// <summary>
    /// One stored attachment of a line list, as bytes — the route the legacy
    /// <c>Util.OpenFile(fileTable, streamId)</c> needed and the one the web attachment control had no
    /// answer for: the row is read out of the SQL file table and streamed, so no server path ever has to
    /// reach the browser.
    /// </summary>
    /// <remarks>
    /// <c>PIPE.ListLineListFiles</c> answers the file rows of the record (its id, its name, the stream id
    /// of the bytes and the file table they live in) and <c>CM.GetFileContent</c> is the legacy read of
    /// the bytes themselves — the same two steps the desktop client made before it wrote its temporary
    /// copy and launched it.
    ///
    /// The answer is <c>inline</c> with the file's own name and type, so a PDF or an image opens in the tab
    /// and anything else is left to the browser. Saving is the client's business: it already has the same
    /// bytes, and a `download` parameter would only be a second way to ask for them.
    ///
    /// The company guard runs on the record and not on the file id: a file of another company's line list
    /// is a 403, the way that line list is.
    /// </remarks>
    [HttpGet("base-info/line-list/{id:int}/file/{fileId:int}")]
    [RequireAcl(PipeAcl.LINELIST)]
    public async Task<IActionResult> GetLineListFile(int id, int fileId, CancellationToken cancellationToken)
    {
        if (id <= 0 || fileId <= 0) return BadRequest();

        // The record and its company, read exactly as `GetLineList` reads them: a file is only reachable
        // through a record the caller may see.
        var header = await _dal.QueryTableAsync("PIPE.GetLineList", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@Id", SqlDbType.Int) { Value = id });
        if (header.Rows.Count == 0) return NotFound();

        var record = ToRows(header)[0];
        var companyId = record.TryGetValue("companyId", out var company) && company is not null ? Convert.ToInt32(company) : 0;
        if (companyId > 0 && await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;

        // The file row of this record. The procedure already drops the deleted rows, so an id that is not
        // in the answer is either another record's file or a file that is gone: both are a 404.
        var files = ToRows(await _dal.QueryTableAsync("PIPE.ListLineListFiles", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@Id", SqlDbType.Int) { Value = id }));
        var file = files.FirstOrDefault(row => row.TryGetValue("id", out var rowId) && Convert.ToInt32(rowId) == fileId);
        if (file is null) return NotFound();

        var fileTable = file.TryGetValue("fileTableName", out var table) ? Convert.ToString(table) : null;
        var streamId = file.TryGetValue("filePath", out var stream) && stream is Guid value ? value : Guid.Empty;
        if (string.IsNullOrWhiteSpace(fileTable) || streamId == Guid.Empty) return NotFound();

        var content = await _dal.QueryTableAsync("CM.GetFileContent", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@FileTable", SqlDbType.VarChar, 200) { Value = fileTable },
            new SqlParameter("@StreamId", SqlDbType.UniqueIdentifier) { Value = streamId });
        if (content.Rows.Count == 0) return NotFound();

        if (content.Rows[0]["file_stream"] is not byte[] bytes || bytes.Length == 0) return NotFound();

        // The name of the file row, not the name stored in the table: an attachment renamed on the form is
        // the name the operator expects to see on the download.
        var fileName = Convert.ToString(file.TryGetValue("name", out var name) ? name : null);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = Convert.ToString(content.Rows[0]["name"]);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "attachment";

        var contentType = ContentTypes.TryGetContentType(fileName, out var known) ? known : "application/octet-stream";
        // `SetHttpFileName` writes both the ASCII `filename` and the UTF-8 `filename*`, so a file named in
        // Persian or Russian survives the header.
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(fileName);
        Response.Headers.ContentDisposition = disposition.ToString();

        return File(bytes, contentType, enableRangeProcessing: true);
    }

    [HttpGet("base-info/contractor-estimates")]
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

    [HttpPut("base-info/contractor-estimates")]
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

    [HttpGet("base-info/contractor-priorities")]
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

    [HttpPut("base-info/contractor-priorities")]
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

    [HttpGet("base-info/line-contracts")]
    public async Task<IActionResult> GetLineContracts([FromQuery] int companyId, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var table = await _dal.QueryTableAsync("PIPE.GetLinesContracts", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId }, new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId }, new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });
        return Ok(ToRows(table));
    }

    [HttpPut("base-info/line-contracts")]
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

    [HttpGet("base-info/sheet-contracts")]
    public async Task<IActionResult> GetSheetContracts([FromQuery] int companyId, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var table = await _dal.QueryTableAsync("PIPE.GetSheetContracts", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId }, new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId }, new SqlParameter("@UserId", SqlDbType.Int) { Value = userId });
        return Ok(ToRows(table));
    }

    [HttpPut("base-info/sheet-contracts")]
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

    [HttpGet("base-info/pms-materials")]
    public async Task<IActionResult> GetPmsMaterials([FromQuery] int companyId, CancellationToken cancellationToken)
    {
        if (companyId <= 0 || !TryGetProjectId(out var projectId)) return BadRequest();
        if (await GuardCompanyAsync(companyId, cancellationToken) is { } companyGuard) return companyGuard;
        var table = await _dal.QueryTableAsync("PIPE.GetPMSMatGrid", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId }, new SqlParameter("@CompanyId", SqlDbType.Int) { Value = companyId });
        return Ok(ToRows(table));
    }

    [HttpGet("base-info/pms-materials/warehouse-items")]
    public async Task<IActionResult> GetWarehouseItems(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetWarehouseMaterials", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    [HttpPut("base-info/pms-materials")]
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

    [HttpDelete("base-info/pms-materials")]
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

    [HttpGet("base-info/locations")]
    public async Task<IActionResult> GetLocations(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();

        var table = await _dal.QueryTableAsync("PIPE.GetLocations", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });

        return Ok(ToRows(table));
    }

    [HttpPut("base-info/locations")]
    public async Task<IActionResult> SaveLocations([FromBody] JsonElement rows, CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();

        // The table parameter is the type the procedure declares, read from `sys.table_types`: the shape
        // of a row is the database's, so a column added to the type needs no edit here.
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SaveLocations", "@Locations", cancellationToken);
        var locations = await CreateTypedTableAsync(typeName, rows, cancellationToken);

        foreach (DataRow row in locations.Rows)
        {
            var name = Convert.ToString(row["Location"]);
            if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "Each location needs a name." });
            row["Location"] = name.Trim();
        }

        await _dal.ExecuteAsync("PIPE.SaveLocations", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            StructuredParameter("@Locations", typeName, locations));

        return NoContent();
    }

    [HttpGet("base-info/pms-thickness")]
    [RequireAcl(PipeAcl.PMS_THK)]
    public async Task<IActionResult> GetPmsThickness(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out _)) return Unauthorized();

        var table = await _dal.QueryTableAsync("PIPE.GetPMSThkGrid", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });

        var pwhtValues = await GetPwhtValues(projectId, cancellationToken);

        // The screen shows the grid to everyone allowed to read it, but only unlocks editing
        // with PMS_THK_EDIT. This is the "Deny = visible but disabled" state of the client ACL,
        // not a permission check: the write endpoints are guarded on the server.
        var canEdit = await _permissions.IsAllowedAsync(PipeAcl.PMS_THK_EDIT, cancellationToken);

        // The rows are the table of the procedure (`ToRows`, no record declared for them); the two flags
        // are what the screen needs besides them.
        return Ok(new { rows = ToRows(table), pwhtValues, canEdit });
    }

    [HttpPut("base-info/pms-thickness")]
    [RequireAcl(PipeAcl.PMS_THK_EDIT)]
    public async Task<IActionResult> SavePmsThickness([FromBody] JsonElement rows, CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();

        // The type does not restrict this column, so the value is checked here: a PWHT that is not one of
        // the configured ones is rejected before the rows become a table parameter.
        var allowedPwht = await GetPwhtValues(projectId, cancellationToken);
        if (rows.ValueKind == JsonValueKind.Array && rows.EnumerateArray().Any(row =>
                TryGetProperty(row, "PWHT", out var pwht) && pwht.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(pwht.GetString()) &&
                !allowedPwht.Contains(pwht.GetString()!, StringComparer.Ordinal)))
            return BadRequest(new { message = "PWHT must be one of the configured project values." });

        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SavePMSThk", "@Items", cancellationToken);
        var items = await CreateTypedTableAsync(typeName, rows, cancellationToken);

        await _dal.ExecuteAsync("PIPE.SavePMSThk", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId },
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            StructuredParameter("@Items", typeName, items));

        return NoContent();
    }

    [HttpDelete("base-info/pms-thickness")]
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

    [HttpGet("base-info/material-list")]
    public async Task<IActionResult> GetMaterialList(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetMaterialList", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    [HttpPut("base-info/material-list")]
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

    [HttpGet("base-info/material-list/types")]
    public async Task<IActionResult> GetMaterialTypes(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetMaterialTypeComboForBaseInfoMaterialList", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    [HttpGet("base-info/cladding")]
    public async Task<IActionResult> GetCladdings(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetAllMatCladdings", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    [HttpPut("base-info/cladding")]
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

    [HttpGet("base-info/pipe-class-codes")]
    public async Task<IActionResult> GetPipeClassCodes(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetPipeClassItemCodesGrid", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    [HttpGet("base-info/pipe-class-codes/materials")]
    public async Task<IActionResult> GetPipeClassCodeMaterials(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetMaterialsForItemCodePipeClasses", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    [HttpPut("base-info/pipe-class-codes")]
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

    [HttpGet("base-info/material-details")]
    public async Task<IActionResult> GetMaterialDetails(CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();
        var table = await _dal.QueryTableAsync("PIPE.GetMaterialDetails", CommandType.StoredProcedure, cancellationToken,
            new SqlParameter("@UserId", SqlDbType.Int) { Value = userId },
            new SqlParameter("@ProjectId", SqlDbType.Int) { Value = projectId });
        return Ok(ToRows(table));
    }

    /// <remarks>
    /// The rows of the grid carry the id as the read calls it (`MaterialId` → `materialId`), while the
    /// type of the save declares that same column as `Id`: the screen sends it under the name the type
    /// uses, and only the rows it touched (`modified`), which is not a column of the type.
    /// </remarks>
    [HttpPut("base-info/material-details")]
    public async Task<IActionResult> SaveMaterialDetails([FromBody] JsonElement rows, CancellationToken cancellationToken)
    {
        if (!TryGetProjectId(out var projectId) || !TryGetUserId(out var userId)) return Unauthorized();
        var typeName = await _dal.GetTableParameterTypeAsync("PIPE.SaveMaterialDetails", "@Items", cancellationToken);
        var items = await CreateTypedTableAsync(typeName, rows, cancellationToken, only: "Modified");
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

    private async Task<DataTable> CreateTypedTableAsync(string typeName, JsonElement rows, CancellationToken cancellationToken, string? only = null)
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
            // `only` is the `Modified` flag of an editable grid: a save sends the rows the user touched.
            if (only is not null && (!TryGetProperty(source, only, out var flag) || flag.ValueKind != JsonValueKind.True)) continue;

            var row = table.NewRow();
            foreach (DataColumn column in table.Columns)
            {
                if (!TryGetProperty(source, column.ColumnName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    // PIPE.Line.DisableRT is NOT NULL in SQL Server. Existing rows or clients
                    // may omit this legacy flag, so preserve the database's false/default meaning
                    // instead of sending DBNull through the table-valued parameter.
                    row[column] = string.Equals(column.ColumnName, "DisableRT", StringComparison.OrdinalIgnoreCase)
                        ? false
                        : DBNull.Value;
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

