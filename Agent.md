# Agent.md — CMIS API

Working notes for AI agents (and humans) on this repository. Read this before opening source
files. If you change the architecture, update this file in the same change.

Its sibling is the web client **`D:\Sharifat\Projects\web\CMIS\Clinet\cmis-web`** (pnpm
workspace, React + Vite). The two are **separate git repositories**, so a feature usually means
two commits in two places — see §9 and
`…\Clinet\cmis-web\docs\backend-contract.md`, which is the contract seen from the client side.

**Keep them separate.** This repo has its own published remote
(`github.com/adelsharifat/cmapp.git`), its own deploy and its own toolchain; it shares no build
output with the client, so folding the folders together would cost a history graft and buy only
"one folder tree". The two halves are linked by documentation instead — §9 here, the contract doc
there, and `…\web\CMIS\cmis.code-workspace`, which opens both repos in one window.

---

## 1. What this is

A **Construction Management Information System (CMIS)** API: authentication, session management
and per-domain business modules behind `/api`. It is consumed by the React client above, but is
independently runnable through its own Swagger UI.

## 2. Stack

| Area | Choice |
| --- | --- |
| Framework | ASP.NET Core on **`net10.0`**, SDK `10.0.300` |
| Solution | `CMIS.slnx` — the **XML** solution format; there is no `.sln` |
| Language settings | `Nullable` + `ImplicitUsings` enabled everywhere, file-scoped namespaces; DTOs are positional `record`s (`LoginDto`, `UserInformationDto`, `ApiError`), services and controllers use explicit constructors |
| Data access | **ADO.NET + `Microsoft.Data.SqlClient`**, stored procedures only. No EF Core, no Dapper, no inline SQL in controllers |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer`, HS256, symmetric key |
| Realtime | `SignalR` (`SessionHub`) |
| API docs | `Swashbuckle.AspNetCore` + `Microsoft.AspNetCore.OpenApi`, Development only |
| Passwords | `Md5Helper` (MD5) — `BCrypt.Net-Next` is referenced but unused, see §10 |

## 3. Layout and the module pattern

```
CMIS.slnx
├─ CMIS/         host: Program.cs (composition root), appsettings*, launchSettings, CMIS.http
├─ CMIS.CORE/    shared: DAL, Jwt, Services, Security, Hubs, Middleware, Models, Modules
├─ CMIS.PIPE/    Pipe module     — PipeModule + PipeController + PipeBaseInfoController
└─ CMIS.WH/      Warehouse module — WarehouseModule + WarehouseController (health only)
```

`CMIS.CORE` sets `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, so it may own
controllers, middleware and hubs while staying a class library.

A module is an `ICMISModule` implemented by an assembly marker class:

```csharp
public sealed class PipeModule : ICMISModule
{
    public const string ModuleName = "Pipe";
    public const string ModuleVersion = "1.0.0";

    public string Name => ModuleName;
    public string Version => ModuleVersion;
    public Assembly Assembly => typeof(PipeModule).Assembly;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // module-specific services
    }
}
```

`AddCMISModules` (`CMIS.CORE/Extensions/CMISModuleExtensions.cs`) calls `Register` and
`AddApplicationPart(module.Assembly)`; the second call is what makes controllers in a *separate*
assembly discoverable. Registration, in `CMIS/Program.cs`:

```csharp
builder.Services.AddCMISModules(builder.Configuration, modules =>
{
    modules.Add<WarehouseModule>();
    modules.Add<PipeModule>();
});
```

**Adding a module**: new `CMIS.<NAME>` class library → add it to `CMIS.slnx` → `ICMISModule`
marker → one line in `Program.cs`. Module controllers never go into `CMIS.CORE`.

Route convention: `[Route("api/<module>/<area>")]` — `api/pipe`, `api/pipe/base-info`,
`api/warehouse`. `[ApiController]` everywhere, `[Authorize]` on every controller that is not
authentication itself.

## 4. Commands

```powershell
cd D:\Sharifat\Projects\web\CMIS\CMIS
dotnet build CMIS.slnx                  # 0 Warning(s) / 0 Error(s) today — keep it that way
dotnet run --project CMIS               # http://localhost:5000 (launchSettings, Development)
dotnet build CMIS.slnx -v q --nologo    # quiet build, what CI would use
```

Swagger UI: `http://localhost:5000/` (the `RoutePrefix` is `string.Empty`), spec at
`/swagger/v1/swagger.json`. Send `Authorization: Bearer <token>` from
`POST /api/auth/login`.

The web client must be running for an end-to-end check:

```powershell
cd D:\Sharifat\Projects\web\CMIS\Clinet\cmis-web\apps\web; pnpm dev   # http://localhost:5173
```

`CMIS/CMIS.http` is still the Visual Studio template (`/weatherforecast`, port 5215 — neither
exists). Fix or delete it before trusting it (§10).

## 5. Request pipeline (`CMIS/Program.cs`)

```
UseCors                                    5173 / 192.168.136.75:5173, with credentials
UseHttpsRedirection                        no https URL configured → no-op in dev
UseMiddleware<ApiExceptionMiddleware>      UnauthorizedAccessException → 401, anything else → 500
UseAuthentication                          JwtBearer; also accepts ?access_token= for /hubs/session
UseAuthorization
UseMiddleware<SessionValidationMiddleware> DB check per request, bare 401 if the session is dead
UseSwagger / UseSwaggerUI                  Development only, served at "/"
MapControllers
MapHub<SessionHub>("/hubs/session")
```

Things that bite:

- **CORS allows exactly two origins**, hard-coded in `Program.cs`. A client on another host or
  port gets a preflight failure that looks like a random network error in the browser. The
  client's `VITE_API_URL` defaults to `http://localhost:5000/api` — keep them in sync.
- `ApiExceptionMiddleware` is the only error shape: `ApiError(Code, Message)` →
  `{ "code": …, "message": … }` after camelCasing. Throwing `UnauthorizedAccessException` is the
  supported way to produce a 401 from a service; anything else becomes a logged 500 with the
  detail withheld.
- `Forbid()` sends **403 with an empty body**. If a denial has to explain itself to the user,
  return a typed body instead. `AclItemGuard` does exactly that (`acl_item_denied`); see `…\Clinet\cmis-web\docs\backend-contract.md` section 6.
- Middleware order matters: authentication must run before `SessionValidationMiddleware`,
  otherwise `context.User` is empty and every session is "invalid".
- Because the session middleware runs after `UseAuthorization`, an endpoint that is `[AllowAnonymous]`
  (login, refresh, logout, health) never pays the DB round-trip. Keep new anonymous endpoints that way.

## 6. Authentication and sessions

`POST /api/auth/login` → `{ accessToken }` + two cookies; `POST /api/auth/refresh` → new
`{ accessToken }` and a rotated cookie; `POST /api/auth/logout` → revokes and deletes.

| Piece | Detail |
| --- | --- |
| Claims | `sub` = user id, `jti` = token id, `sid` = **refresh-token id used as the session id**, `project` = project id |
| `MapInboundClaims = false` | deliberately set so `sub` and `sid` keep their names — the client parses `sid` out of the token |
| Access token | `Jwt:AccessTokenExpirationMinutes`, **2** minutes today; short on purpose, the client refreshes silently |
| Refresh cookie | `RefreshToken`, `HttpOnly`, `SameSite=Strict`, `Secure = Request.IsHttps`, `Path=/`, 30 days |
| Device cookie | `DeviceId`, not HttpOnly, 1 year, created on login from the incoming cookie |
| Hub auth | `?access_token=` query parameter, read in `JwtBearerEvents.OnMessageReceived` for `/hubs/session` only |
| Revocation push | `SessionRevoked` sent to the SignalR group `session:{id}`; `SessionHub.OnConnectedAsync` joins that group from the `sid` claim |

Rules to preserve:

- **The user id and project id always come from the token**, never from a route, query or body
  (`TryGetUserId` / `TryGetProjectId` in `PipeBaseInfoController`).
- Project selection is currently **hard-coded to 1** (`AuthService.SignInAsync` rejects anything
  else), because only one project is exposed. That check is a security control, not a placeholder
  to delete — replace it with a real project lookup when more projects exist (§10).
- `SessionValidationMiddleware` costs **one DB call per authenticated request**
  (`CM.sp_GetSessionStatus`). It is the reason "signed out on another device" takes effect
  instantly. Don't move it, and don't cache it away without replacing the behaviour.
- Logout must stay callable **with an expired access token**: the controller deletes the cookie in
  a `finally`, and also deletes the legacy `/api/auth` cookie path left over from an older version.

## 7. ACL: ported from WinForms, still owned by the database

`CMIS.CORE/Security/Acl/` is the permission system — a deliberate port of the legacy
`Security.LoginInfo` / `<Module>.Acl.cs` model, not a new design:

- `AclSchemaAttribute` / `AclPermissionAttribute` — every module declares its permissions on a
  static class (`CMIS.PIPE/Security/PipeAcl.cs`), keeping the **legacy caption verbatim** so an ACL
  editor shows the same text as WinForms. `AclRegistry` (singleton) collects them at startup and
  **throws on a duplicate name**, so two modules cannot shadow each other.
- `RequireAclAttribute` + `AclPolicyProvider` / `AclAuthorizationHandler` — the guard on a
  controller action: `[RequireAcl(PipeAcl.PMS_THK)]` becomes the policy `acl:PIPE.PMS_THK`. The
  provider accepts the qualified name or a bare name, and **throws** when a name is not declared by
  exactly one module, so a typo fails on the first request instead of silently denying.
- `PermissionService` (`IPermissionService`) — loads `CM.GetPermissions` **once per request**
  (`@UserId`, `@ProjectId` from the token) and answers `ResolveAsync` / `IsAllowedAsync` /
  `IsHiddenAsync` / `GetAccessMapAsync`. Per request on purpose: a static `Security.LoginInfo` is
  exactly what a server must not do.
- `AclPermissionSet.Resolve` — the only implementation of the rules: **exact name** (wins even when
  it denies) → the module wildcard `SCHEMA.*` → **deny**.
- `AclController` (`api/acl/…`) — read-only: `permissions` (the map the web client loads at
  sign-in), `registry`, `check/{permission}`, `items/{permission}`, `item/{permission}`.
  **No writes**: the ACL table belongs to the WinForms administration tool (`frmAclManagement`,
  `CM.SaveUserAcl` / `CM.SaveGroupAcl`).

`CM.GetPermissions` returns rows of `Name` **|** `ProjectId` **|** `Allow`, and `Allow` is
**nullable**:

| `Allow` | `AclGrant` | `[RequireAcl]` | Web client (`cmis-web/docs/acl.md`) |
| --- | --- | --- | --- |
| `1` | `Allow` | passes | enabled, handlers bound |
| `0` | `Deny` | `403 Forbid()` | visible but disabled |
| `NULL` | `Hidden` | `403 Forbid()` | not rendered at all |
| no row | `Inherit` | wildcard, else deny | never sent unresolved |

`Hide` is a rendering state, **not** a grant: the server still forbids it, so it can never widen
access. Those values are the wire format (`1 / 0 / null`) and the web client's
`Access.Allow / Deny / Hide` are the same numbers — do not invent another one.

- Names are **lower-cased before every DB lookup** (`pipe.pms_thk`), like the legacy client did, so
  rows written by the WinForms tool keep working. Rows for another project are dropped, and a
  `ProjectId = NULL` row only applies to a "no project" query.
- Row-level ACLs still live **inside the stored procedures**, which accept an `@Acl` (and sometimes
  `@Schema`) string: `CM.GetCompaniesCombo @Acl = 'PIPE.COMPANY'`,
  `CM.GetAllowedContracts @Schema = 'PIPE', @Acl = 'CONTRACTS'` (note the second omits the `PIPE.`
  prefix — an inconsistency predating the module system). `CM.GetAllowedItems` / `CM.GetAllowedItem`
  expose the same ids to the client through `GET api/acl/items|item/{permission}`. Both halves are
  needed for a permission to be real: the procedure filters *rows*, the guard gates the *action*.
- `AclItemGuard` (`CMIS.CORE/Security/Acl/AclItemGuard.cs`) - the port of the legacy
  `Acl.AllowItem` calls that every WinForms form had to remember:
  `CheckAllowedItemsAsync(permission, [id])` turns an id that arrives in a request into a
  `403 { code: "acl_item_denied", message: "Value(s) 42 are not allowed by PIPE.COMPANY." }`.
  An endpoint that takes a company id calls it (the private `GuardCompanyAsync` helper in
  `PipeBaseInfoController`), and the combo endpoints (`companies`, `contracts`) validate their
  `acl` / `schema` query parameter against `AclRegistry`: an unknown or non-`Items` name is a 404,
  a permission the caller does not hold is a 403 - so a forged combo value can never read or write
  another company.
- Naming is settled: `SCHEMA.NAME`, where the schema is the module (`PIPE`, `WH`), mirroring
  `<Module>.Acl.cs`. The web app mirrors the same strings in `modules/<name>/acl.ts`, and
  `…\Clinet\cmis-web\docs\acl.md` is the end-to-end reference.

## 8. Data access (DAL)

`DAL` in `CMIS.CORE/Db/DAL.cs` is the only data-access type, registered as a **singleton**
(stateless — it only holds the connection string; keep it that way) and injected into
controllers and services:

```csharp
services.Configure<JwtOptions>(cfg.GetSection("Jwt"));
services.AddSingleton<DAL>();
services.AddScoped<IAuthService, AuthService>();
```

| Helper | Use for |
| --- | --- |
| `ExecuteAsync(proc, CommandType.StoredProcedure, ct, params…)` | writes |
| `QueryAsync<T>(proc, mapper, params…)` | typed list |
| `QuerySingleAsync<T>(proc, mapper, params…)` | one row; throws `InvalidOperationException` when there is none — catch it and translate (`NotFound()`, "invalid credentials") |
| `QueryTableAsync(proc, ct, params…)` | `DataTable` — what the grid endpoints use |
| `GetTableParameterTypeAsync(proc, "@Items", ct)` | resolves a TVP's SQL type name from `sys.parameters` |
| `ExecuteInTransactionAsync(…)` | several statements that must commit together |

Conventions:

- **Stored procedures only.** The single `CommandType.Text` query in the repo is the metadata
  lookup inside `GetTableParameterTypeAsync`. Schema prefixes: `CM.*`, `PIPE.*`, `QCPIPE.*`.
- **TVP saves** are the house style for bulk writes: build a `DataTable` with typed columns,
  resolve the type name with `GetTableParameterTypeAsync` (never hard-code it), then
  `new SqlParameter("@Items", SqlDbType.Structured) { TypeName = … }`.
  `PipeBaseInfoController` has reusable helpers — `StructuredParameter`, `CreateTable`,
  `FillTable`, `ToRows`, `NullIfDbNull`, `ReadDecimal` — copy their shape for a new module rather
  than inventing a second mapping style.
- **`ExecuteInTransactionAsync<T>` is hand-rolled** (`BeginTransaction` + commit/rollback in a
  `try/catch`) and takes a `Func<…, Task<T>>`; it is not a distributed transaction and it does not
  flow through `TransactionScope`.
- **Response shape for grids**: `ToRows(table)` → `IReadOnlyList<Dictionary<string, object?>>`
  with **camelCased** column names and `DBNull` → `null`. Note the deliberate spelling fix
  `Wight` → `Weight` in `FillTable` (a legacy column name kept for an older client) — that is a
  migration in progress, not a typo.

## 9. A feature is two changes

The client lives in another repository (`…\Clinet\cmis-web`); nothing here can change it. For any
feature that needs data:

1. **Name the permission first**: `SCHEMA.NAME` (`MODULE.AREA[.ACTION]`), declared in the module's
   `[AclSchema]` class with the legacy caption. Add the ACL row for the project and mirror the
   constant in the client's `modules/<name>/acl.ts` (§7).
2. **Endpoint** in the module's controller: `[Authorize]`, `TryGetUserId`/`TryGetProjectId` from
   the token, the permission guard, then the `DAL` call. Pass `@UserId` + `@ProjectId` (+ `@Acl`
   for row filtering) down to the procedure — the database repeats the check on purpose.
3. **`dotnet build CMIS.slnx`** must stay at 0 warnings, then try the route in Swagger including
   the 401 (no token) and 403 (denied) paths.
4. **Update the contract docs**: any new endpoint, permission name or response shape goes into the
   inventory in `…\Clinet\cmis-web\docs\backend-contract.md`. That file is how the client side
   finds out you exist.
5. **Only then** the client adds its `api.ts` + query hooks + screen. Do not hand the frontend an
   undocumented shape.

## 10. Known gaps, in the order that unblocks the most

| # | Gap | Where |
| --- | --- | --- |
| 1 | **No ACL write path** — deliberate. Grants are still written only by the WinForms administration tool (`CM.SaveUserAcl` / `CM.SaveGroupAcl`); a web ACL editor needs write endpoints plus audit columns first. | `AclController` |
| 2 | **A declared permission with no ACL row resolves to `0`**, and `projectId` is fixed to `1`, so the per-project half of the resolution is untested in practice. | `CM.GetPermissions`, `AuthService.SignInAsync` |
| 3 | **No project list endpoint**, and `projectId` is hard-coded to `1`. Blocks the client's login page (`LOGIN_PROJECTS`) and every multi-project screen. | `AuthService.SignInAsync` |
| 4 | **Committed secrets**: `ConnectionStrings:Default` carries a real SQL `sa` password and `Jwt:SecretKey` is a placeholder. Move both to user-secrets / environment variables before this repo reaches a remote. | `CMIS/appsettings.json` |
| 5 | **MD5 passwords** (`Md5Helper`) while `BCrypt.Net-Next` sits unused in `CMIS.CORE.csproj`. Plan: verify MD5 on login, rehash to BCrypt, then drop the MD5 path. | `AuthService`, `Security/Md5Helper.cs` |
| 6 | **Uncommitted work**: the ACL port (`CMIS.CORE/Security/Acl/*`, `Security/PermissionService.cs`, `IPermissionService.cs`, `Controllers/AclController.cs`), its DI registration and the `PipeBaseInfoController` refactor (now `[RequireAcl]`-guarded) are untracked or modified — none of it is committed. | git |
| 7 | **`CMIS.http` is the default template** (wrong port 5215, `/weatherforecast` does not exist). | `CMIS/CMIS.http` |
| 8 | **No tests, and `.github/` is empty** — no CI. Don't claim a test run that did not happen. | repo root |
| 9 | `PipeBaseInfoController` is ~875 lines and mixes five grids plus the line-list attachment stream (`line-list/{id}/file/{fileId}`); split it per area (`PmsThicknessController`, `LineListController`, …) when a sixth grid arrives. | `CMIS.PIPE` |
| 10 | `CMIS.WH` only has a health endpoint — the Warehouse module is still a placeholder. | `CMIS.WH` |

## 11. Definition of done

```powershell
cd D:\Sharifat\Projects\web\CMIS\CMIS
dotnet build CMIS.slnx     # must print 0 Warning(s) / 0 Error(s)
```

Plus, for anything a user can reach: run it and hit the route in Swagger at
`http://localhost:5000/`, checking the happy path, the 401 without a token and the 403 when the
permission denies. If the change is visible in the UI, verify it in the client at
`http://localhost:5173` as well — its bar is `npx tsc -b`, eslint and `pnpm build`
(see `…\Clinet\cmis-web\Agent.md` §16).

## 12. Quick index

| You need… | Go to |
| --- | --- |
| Composition root, CORS, auth setup, pipeline order | `CMIS/Program.cs` |
| Add a module | `CMIS.CORE/Modules/ICMISModule.cs` + a new `CMIS.<NAME>` project + `CMIS.slnx` + `Program.cs` |
| Shared services / DI | `CMIS.CORE/Extensions/ServiceCollectionExtensions.cs` |
| Login / refresh / logout | `CMIS.CORE/Controllers/AuthController.cs`, `Services/AuthService.cs` |
| JWT creation, claims, options | `CMIS.CORE/Jwt/JwtHelper.cs`, `JwtOptions.cs` |
| Session revocation + realtime | `CMIS.CORE/Hubs/SessionHub.cs`, `Controllers/UserController.cs` |
| Per-request session check | `CMIS.CORE/Security/SessionValidationMiddleware.cs` |
| Error shape | `CMIS.CORE/Middleware/ApiExceptionMiddleware.cs`, `Models/ApiError.cs` |
| Data access | `CMIS.CORE/Db/DAL.cs`, `CMIS.CORE/Db/DataTableExtensions.cs` |
| Permissions / `@Acl` | §7, `CMIS.CORE/Security/Acl/*`, `Controllers/AclController.cs` |
| Row-level guard of a submitted id | `CMIS.CORE/Security/Acl/AclItemGuard.cs` (§7), `PipeBaseInfoController.GuardCompanyAsync` |
| A real business module to copy | `CMIS.PIPE/*` |
| The client-side half of the contract | `…\Clinet\cmis-web\docs\backend-contract.md` and its `Agent.md` §10 |
