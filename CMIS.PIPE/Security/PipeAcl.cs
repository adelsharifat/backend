using CMIS.Core.Security.Acl;

namespace CMIS.PIPE.Security;

/// <summary>
/// Every permission the Piping module can grant. The names and captions are copied from the
/// WinForms application (<c>ODCC-WinApplication\PIPE\Acl.cs</c>), so the rows already in the ACL
/// table keep working and an administrator recognises the list in either client. The order below
/// follows the Piping home board (<c>PIPE\Others\frmHome.Designer.cs</c>) and matches
/// <c>cmis-web/apps/web/src/modules/pipe/acl.ts</c> one for one.
/// </summary>
/// <remarks>
/// Adding a permission is one line here — the registry, the ACL editor tree and the client's
/// access map are all generated from this class. A name that is only written in a controller or
/// only inserted into the database is invisible to one of the three, which is exactly the drift
/// this file exists to prevent.
///
/// This is the declared catalogue, not the whole legacy <c>Acl.cs</c>: a permission whose screen is
/// not ported yet is added together with that screen, so the two vocabularies never drift apart.
/// The client mirror carries the same names, in the same order, with the same legacy captions.
/// </remarks>
[AclSchema("PIPE", Caption = "Piping")]
public static class PipeAcl
{
    [AclPermission("PI001 - Allow to all child permissions",
        Description = "Wildcard. Grants every PIPE permission the user has no explicit row for.")]
    public const string ALL = "*";

    // --- Row-level permissions (`Items`): they carry the ids a user may see, not a yes/no. ---

    [AclPermission("PI003 - Allowed companies", Kind = AclPermissionKind.Items,
        ItemsSource = "pipe/base-info/companies")]
    public const string COMPANY = "COMPANY";

    [AclPermission("PI002 - Allowed contracts", Kind = AclPermissionKind.Items,
        ItemsSource = "pipe/base-info/contracts")]
    public const string CONTRACTS = "CONTRACTS";

    // --- Line list. ---

    [AclPermission("PI004 - Line Lists",
        Description = "The lines list of the module; also the gate of the module itself.")]
    public const string LINELIST = "LINELIST";

    [AclPermission("PI013 - New LineList")]
    public const string NEW_LINELIST = "NEW_LINELIST";

    [AclPermission("PI081 - Line list : Edit")]
    public const string LINE_LIST_EDIT = "LINE_LIST_EDIT";

    [AclPermission("PI082 - Line list : Clone line")]
    public const string LINE_LIST_CLONE = "LINE_LIST_CLONE";

    [AclPermission("PI080 - Line list : Attachments")]
    public const string LINE_LIST_ATTACHMENTS = "LINE_LIST_ATTACHMENTS";

    [AclPermission("PI083 - Line list : Export to excel")]
    public const string LINE_LIST_EXCEL_EXPORT = "LINE_LIST_EXCEL_EXPORT";

    [AclPermission("PI103 - Line Batch Update in Line List")]
    public const string LINE_BATCH_UPDATE = "LINE_BATCH_UPDATE";

    [AclPermission("Design Line Lists")]
    public const string DESIGN_LINELIST = "DESIGN_LINELIST";

    // --- Joint history. ---

    [AclPermission("PI005 - Joint History")]
    public const string JOINTS = "JOINTS";

    [AclPermission("PI051 - Joint history : Edit")]
    public const string JOINT_HISTORY_EDIT = "JOINT_HISTORY_EDIT";

    [AclPermission("PI052 - Joint history : Edit MTO")]
    public const string JOINT_HISTORY_EDIT_MTO = "JOINT_HISTORY_EDIT_MTO";

    [AclPermission("PI055 - Joint history : Move from another IsoSheets")]
    public const string JOINT_HISTORY_MOVE = "JOINT_HISTORY_MOVE";

    [AclPermission("PI058 - Joint history : Export to excel")]
    public const string JOINT_HISTORY_EXCEL_EXPORT = "JOINT_HISTORY_EXCEL_EXPORT";

    [AclPermission("PI059 - Joint history : Attachments")]
    public const string JOINT_HISTORY_ATTACHMENTS = "JOINT_HISTORY_ATTACHMENTS";

    [AclPermission("PI102 - Joints Batch Update in Main and Main all", Kind = AclPermissionKind.Items,
        ItemsSource = "pipe/base-info/contracts")]
    public const string JOINT_BATCH_UPDATE = "JOINT_BATCH_UPDATE";

    // --- Work front, MIV and MRV. ---

    [AclPermission("PI008 - WorkFront Shop")]
    public const string WF_SHOP = "WF_SHOP";

    [AclPermission("PI009 - WorkFront Manual")]
    public const string WF_MANUAL = "WF_MANUAL";

    [AclPermission("PI010 - WorkFront History")]
    public const string WF_HISTORY = "WF_HISTORY";

    [AclPermission("PI011 - Front Reports")]
    public const string FRONT_REPORTS = "FRONT_REPORTS";

    [AclPermission("Report Material Front (MR)")]
    public const string MATERIAL_FRONT_MR = "MATERIAL_FRONT_MR";

    [AclPermission("PI016 - General MIV")]
    public const string MIV_GENERAL = "MIV_GENERAL";

    [AclPermission("PI015 - General MIV History")]
    public const string MIV_GENERAL_HISTORY = "MIV_GENERAL_HISTORY";

    [AclPermission("PI012 - MIV History")]
    public const string MIV_HISTORY = "MIV_HISTORY";

    [AclPermission("PI025 - Non-PIPE Miv")]
    public const string NON_PIPE_MIV = "NON_PIPE_MIV";

    [AclPermission("PI027 - Create MRV")]
    public const string MRV = "MRV";

    [AclPermission("PI026 - MRV List")]
    public const string MRV_LIST = "MRV_LIST";

    [AclPermission("PI032 - Line-wise MRV")]
    public const string LINE_WISE_MRV = "LINE_WISE_MRV";

    [AclPermission("Allow tile Contractor MIV Line-Wise")]
    public const string CONTRACTOR_PIPE_MIV = nameof(CONTRACTOR_PIPE_MIV);

    [AclPermission("Allow tile Contractor MIV Line-Wise List")]
    public const string Tile_LineWiseMIV_Contractor_List = nameof(Tile_LineWiseMIV_Contractor_List);

    [AclPermission("PI014 - Contractor Batch Update")]
    public const string CONTRACTOR_BATCH_UPDATE = "CONTRACTOR_BATCH_UPDATE";

    [AclPermission("Modify MIV")]
    public const string MODIFY_MIV = "MODIFY_MIV";

    [AclPermission(nameof(Tile_SPOOL_ERECTION))]
    public const string Tile_SPOOL_ERECTION = nameof(Tile_SPOOL_ERECTION);

    [AclPermission(nameof(Tile_SpoolErection_LIST))]
    public const string Tile_SpoolErection_LIST = nameof(Tile_SpoolErection_LIST);

    // --- Tools. ---

    [AclPermission("PI029 - NDT Spec monitor")]
    public const string NDT_SPEC_MONITOR = "NDT_SPEC_MONITOR";

    [AclPermission("PI020 - Piping Attachments")]
    public const string ATTACHMENTS = "ATTACHMENTS";

    [AclPermission("PI021 - Piping Attachments edit")]
    public const string ATTACHMENTS_EDIT = "ATTACHMENTS_EDIT";

    [AclPermission("PI031 - Excel Template")]
    public const string EXCEL_TEMPLATE = "EXCEL_TEMPLATE";

    [AclPermission("Tile Final TakeOff In Tools Section")]
    public const string Tile_FinalTakeOff = nameof(Tile_FinalTakeOff);

    // --- Base info. ---

    [AclPermission("PI024 - Locations")]
    public const string LOCATIONS = "LOCATIONS";

    [AclPermission("PI023 - Tile: Piping PMS Materials (TakeOff)")]
    public const string PMS_MAT = "PMS_MAT";

    [AclPermission("PI023 - Edit: Piping PMS Materials (TakeOff)")]
    public const string PMS_MAT_EDIT = "PMS_MAT_EDIT";

    [AclPermission("PI022 - Tile: Piping PMS Thk")]
    public const string PMS_THK = "PMS_THK";

    [AclPermission("PI022 - Edit: Piping PMS Thk")]
    public const string PMS_THK_EDIT = "PMS_THK_EDIT";

    [AclPermission("Base Info : Tile: Contractor Estimate")]
    public const string CONTRACTOR_ESTIMATE = "CONTRACTOR_ESTIMATE";

    [AclPermission("Base Info : Tile: Contractor Priority")]
    public const string CONTRACTOR_PRIORITY = "CONTRACTOR_PRIORITY";

    [AclPermission("Base Info : Material Detail")]
    public const string BASE_INFO_MATERIAL_DETAIL = "BASE_INFO_MATERIAL_DETAIL";

    [AclPermission("Base Info : Lines Contracts")]
    public const string BASE_INFO_LINES_CONTRACTS = "BASE_INFO_LINES_CONTRACTS";

    [AclPermission("Base Info : Sheets Contractors")]
    public const string BASE_INFO_SHEET_CONTRACTORS = "BASE_INFO_SHEET_CONTRACTORS";

    [AclPermission("Base Info : Material Cladding")]
    public const string BASE_INFO_MATERIAL_CLADDING = "BASE_INFO_MATERIAL_CLADDING";

    [AclPermission(nameof(Tile_Material_List))]
    public const string Tile_Material_List = nameof(Tile_Material_List);

    [AclPermission(nameof(Tile_PipeClass_ItemCodes))]
    public const string Tile_PipeClass_ItemCodes = nameof(Tile_PipeClass_ItemCodes);

    // --- Reports. ---

    [AclPermission("PI007 - Piping Reports",
        Description = "The report screens that have no permission of their own.")]
    public const string REPORTS = "REPORTS";

    [AclPermission("PI100 - Reports: Main")]
    public const string REPORT_MAIN = "REPORT_MAIN";

    // The legacy captions number Main All and Invoice both PI101; kept verbatim so an ACL editor
    // shows exactly what the WinForms tool shows.
    [AclPermission("PI101 - Reports: Main All")]
    public const string REPORT_MAIN_ALL = "REPORT_MAIN_ALL";

    [AclPermission("PI101 - Reports: Invoice")]
    public const string REPORT_INVOICE = "REPORT_INVOICE";

    [AclPermission("Report : Report MTO Line Wise")]
    public const string REPORT_MTO_LINE_WISE = "REPORT_MTO_LINE_WISE";

    [AclPermission("Report : Report Line Progress Status")]
    public const string REPORT_LINE_PROGRESS_STATUS = "REPORT_LINE_PROGRESS_STATUS";

    [AclPermission("Report : Report Workfront Analyze")]
    public const string REPORT_WORKFRONT_ANAlYZE = "REPORT_WORKFRONT_ANAlYZE";

    [AclPermission("Report : Report Volume of Activities")]
    public const string REPORT_VOLUME_OF_ACTIVITIES = "REPORT_VOLUME_OF_ACTIVITIES";

    [AclPermission("Report : Report Daily WorkDone Status")]
    public const string REPORT_DAILY_WORKDONE_STATUS = "REPORT_DAILY_WORKDONE_STATUS";

    [AclPermission("Report : Report Line Balance")]
    public const string REPORT_LINE_BALANCE = "REPORT_LINE_BALANCE";

    [AclPermission("Report : Report MTO Contractor Wise With Vendor")]
    public const string REPORT_CONTRACTOR_WISE_WITH_VENDOR = "REPORT_CONTRACTOR_WISE_WITH_VENDOR";

    [AclPermission("Report: Modify/Reweld Attachments")]
    public const string REPORT_MODIFY_REWELD_ATTACHMENTS = "REPORT_MODIFY_REWELD_ATTACHMENTS";

    [AclPermission("Report: Contractor MIV Piping Summary")]
    public const string REPORT_CONTRACTOR_MIV_PIPING_SUMMARY = "REPORT_CONTRACTOR_MIV_PIPING_SUMMARY";

    [AclPermission("REPORTS:: Summary Material Status")]
    public const string REPORTS_SummaryMaterialStatus = "REPORTS_SummaryMaterialStatus";

    [AclPermission("REPORTS:: Contractor Status")]
    public const string REPORTS_ContractorStatus = "REPORTS_ContractorStatus";

    [AclPermission("Contractor Material Balance")]
    public const string CONTRACTOR_MATERIAL_BALANCE = "CONTRACTOR_MATERIAL_BALANCE";

    // --- Warehouse: tiles of the Piping board open Warehouse forms, so their names live here. ---

    [AclPermission("PI017 - Warehouse Materials")]
    public const string MATERIALS = "MATERIALS";

    [AclPermission("PI019 - Warehouse Inventory")]
    public const string WH_INVENTORY = "WH_INVENTORY";

    [AclPermission("PI018 - Warehouse Archive MRS")]
    public const string WH_ARCHIVE_MRS = "WH_ARCHIVE_MRS";

    // --- Added with the web client, with no legacy twin. ---

    [AclPermission("Post documents", Description = "New in the web client: posts a document to the next stage.")]
    public const string POST_DOCUMENT = "POST_DOCUMENT";
}
