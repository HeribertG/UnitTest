// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Curated Klacksy feature-coverage decisions for every API controller in Klacks.Api.
/// Each controller is either covered (value lists the chat skills exposing the feature)
/// or excluded (value explains why no chat skill is required, or documents a known gap
/// with its roadmap reference). SkillFeatureCoverageGuardTests fails for any controller
/// missing from this map, forcing an explicit coverage decision for every new feature.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Skills;

public static class SkillFeatureCoverageMap
{
    public const string CoveredPrefix = "covered: ";
    public const string ExcludedPrefix = "excluded: ";

    private const string GapRoadmapSuffix = "), planned phase A3 (roadmap 2026-06-10)";
    public static readonly IReadOnlyDictionary<string, string> Decisions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AgentAutonomyController"] = Covered("get_autonomy_level", "set_autonomy_level"),
        ["ProactiveGovernanceController"] = Covered("get_proactive_governance", "set_proactive_governance"),
        ["AgentMemoriesController"] = Covered("get_ai_memories", "add_ai_memory", "add_personal_memory", "update_ai_memory", "delete_ai_memory"),
        ["AgentPlansController"] = Excluded("Klacksy assistant infrastructure: persisted planner runs; plans are initiated via the propose_plan skill"),
        ["AgentSoulController"] = Covered("get_ai_soul", "update_ai_soul"),
        ["AgentTriggerPreferencesController"] = Excluded("Klacksy assistant self-configuration: per-user trigger mute/snooze managed in the assistant settings UI"),
        ["AgentsController"] = Excluded("Klacksy assistant administration (agent registry), assistant infrastructure"),
        ["ChatController"] = Excluded("Klacksy chat pipeline itself: the host that executes skills, covering it with skills would be circular"),
        ["CustomSttProviderController"] = Excluded("voice STT provider plumbing; configuration visibility via get_speech_settings"),
        ["DocsController"] = Excluded("serves embedded assistant documentation, internal infrastructure"),
        ["EscalationChainsController"] = Gap("escalation intervention list (list running chains, acknowledge/cancel); Owner decision B7 scopes cancel to admins and this chain's roster members, not chat-wide, so a skill would need the same scoping before this can be chat-addressable"),
        ["EscalationRosterController"] = Gap("escalation call-list read (visible members plus reachability status); a read-only get_escalation_roster-style skill would be a natural addition, admin-UI only for now"),
        ["UserAbsencePeriodsController"] = Gap("user absence periods (holidays/sick leave) that the escalation roster skips; a set_my_absence-style skill would let planners self-serve instead of asking an admin, admin-UI only for now"),
        ["EvalController"] = Excluded("Klacksy evaluation harness, developer tooling"),
        ["GlobalRulesController"] = Covered("get_ai_guidelines", "update_ai_guidelines"),
        ["GoalCandidatesController"] = Excluded("deliberately NOT skill-addressable: these are Klacksy's own self-generated goal candidates, so a skill that approves them would let the assistant authorize its own work. Approval must stay a human action in the UI"),
        ["KnowledgeIndexSnapshotController"] = Excluded("developer tooling: admin-only export of the knowledge index embedding snapshot that is committed to the repository; not an end-user feature"),
        ["KlacksyTrainingController"] = Excluded("Klacksy training and knowledge ingestion, assistant infrastructure"),
        ["ModelSyncController"] = Excluded("admin-only LLM model catalog synchronization; day-to-day model management is covered by the llm model skills"),
        ["ModelsController"] = Covered("list_llm_models", "create_llm_model", "update_llm_model", "delete_llm_model"),
        ["ProactiveConditionsController"] = Excluded("read-only grid decoration: marks which service-grid entities Klacksy's remediation already handled, read off the condition ledger the list_open_findings skill already exposes in chat"),
        ["ProactiveMessagesController"] = Excluded("Klacksy assistant self-feedback: helpful/dismissed reactions on proactive messages are set via the chat bubble actions, not chat-addressable"),
        ["ProvidersController"] = Covered("list_llm_providers", "create_llm_provider", "update_llm_provider", "delete_llm_provider"),
        ["ScheduleSetupStateController"] = Excluded("read-only installation setup snapshot for the frontend's own empty-installation detection; same audience and facts as the get_setup_guidance skill, which already covers this for chat"),
        ["SkillCoverageController"] = Excluded("Klacksy skill introspection endpoint, assistant infrastructure"),
        ["KlacksyLearningController"] = Excluded("admin-only review of what Klacksy learned; deliberately no chat skill, a skill that deletes its own learning artefacts would be self-reinforcing"),
        ["SkillRelationsController"] = Excluded("admin-only insight view of the emergent skill-relationship graph (accept/dismiss learned edges); assistant meta-infrastructure, not chat-addressable"),
        ["SkillsController"] = Covered("list_agent_skills", "create_agent_skill", "update_agent_skill", "delete_agent_skill"),
        ["SttController"] = Excluded("voice audio streaming pipeline, not chat-addressable; settings covered by get_speech_settings"),
        ["TranscriptionController"] = Excluded("voice audio streaming pipeline, not chat-addressable; settings covered by get_speech_settings"),
        ["TranscriptionDictionaryController"] = Covered("list_transcription_dictionary_entries", "add_transcription_dictionary_entry", "update_transcription_dictionary_entry", "delete_transcription_dictionary_entry"),
        ["TtsController"] = Excluded("voice audio streaming pipeline, not chat-addressable; settings covered by get_speech_settings"),
        ["UsageController"] = Gap("LLM usage analytics"),

        ["DatabaseController"] = Excluded("internal database maintenance endpoint, not a user-facing feature"),

        ["FeaturePluginController"] = Gap("feature plugins"),

        ["ClientShiftPreferencesController"] = Covered("set_shift_preferences"),
        ["ContractsController"] = Covered("list_contracts", "get_contract_details", "create_contract", "update_contract", "delete_contract", "assign_contract_to_client", "assign_contract_by_name"),
        ["IndividualPeriodsController"] = Excluded("admin settings CRUD for custom (individual) contract pay-period definitions used by PaymentInterval.Individual contracts; admin settings card only, no Klacksy chat skill in this iteration"),
        ["MonthlyTargetHoursController"] = Excluded("admin settings CRUD for the company-wide monthly target hours table used by PaymentInterval.MonthlyTargetHours contracts; admin settings card only, no Klacksy chat skill in this iteration"),
        ["GroupItemsController"] = Covered("add_client_to_group", "add_client_to_group_by_name", "remove_client_from_group"),
        ["GroupVisibilitiesController"] = Covered("set_user_group_scope", "get_user_group_scope"),
        ["GroupsController"] = Covered("list_groups", "list_groups_hierarchical", "create_group", "update_group", "delete_group", "set_group_location"),
        ["MembershipsController"] = Covered("list_client_memberships", "update_membership"),

        ["AccountsController"] = Covered("create_user", "delete_system_user", "list_system_users", "assign_user_permissions", "get_user_permissions"),
        ["IdentityProvidersController"] = Covered("list_identity_providers", "create_identity_provider", "update_identity_provider", "delete_identity_provider"),
        ["SetupController"] = Excluded("admin setup gate status/complete endpoints; onboarding infrastructure, not chat-addressable"),
        ["OAuth2Controller"] = Excluded("auth infrastructure: browser redirect flow, not chat-addressable"),
        ["OAuthAuthorizationServerController"] = Excluded("OAuth authorization server endpoints; not chat-skill material"),
        ["OAuthAuthorizationServerMetadataController"] = Excluded("OAuth authorization server endpoints; not chat-skill material"),
        ["PersonalAccessTokensController"] = Excluded("auth infrastructure: self-service credential management for MCP access; exposing token minting/revocation as chat skills would be a security risk"),
        ["ErpDropPointsController"] = Covered("get_erp_import_status", "trigger_erp_import_run"),
        ["ErpOrderUploadController"] = Excluded("machine-to-machine ERP upload endpoint authenticated by a drop-point-scoped import token, not a logged-in Klacks user; not chat-addressable"),
        ["ErpImportTokensController"] = Excluded("security-sensitive credential minting/revocation for ERP drop points, same rationale as PersonalAccessTokensController; not chat-addressable"),
        ["KlacksBotTokensController"] = Excluded("security-sensitive credential minting/revocation for the external Otto marketing bot, same rationale as PersonalAccessTokensController/ErpImportTokensController; not chat-addressable"),
        ["BotQueryController"] = Excluded("machine-to-machine read-only endpoint for the external Otto marketing bot, authenticated by a bot-scoped token with no user role, not a logged-in Klacks user; not chat-addressable, same rationale as ErpOrderUploadController"),

        ["BaseController"] = Excluded("base class for API controllers, no own endpoints"),
        ["DashboardController"] = Covered("get_dashboard_summary", "get_client_locations_overview", "interpret_resource_monitor"),
        ["DonationController"] = Covered("create_donation_checkout"),
        ["LanguageConfigController"] = Covered("list_languages", "install_language_pack", "uninstall_language_pack"),
        ["LoadFileController"] = Excluded("binary file upload/download infrastructure, not chat-addressable"),
        ["RouteOptimizationController"] = Gap("route optimization; geographic grouping is covered by propose_grouping/apply_grouping but route planning itself is not"),
        ["ScheduleChangesController"] = Gap("schedule change history; rollback_my_last_change/verify_my_last_action only cover the assistant's own session"),
        ["TranslationController"] = Covered("get_translation_status"),
        ["UpdateController"] = Excluded("auto-update deployment infrastructure, not a Klacksy domain feature"),
        ["VersionController"] = Covered("get_system_info"),
        ["WhisperPluginController"] = Excluded("Whisper plugin install/uninstall rides the updater deployment infrastructure; admin settings card only, engine visibility via get_speech_settings"),
        ["ExportFormatOverridesController"] = Excluded("support hotfix tooling for export formats; admin settings card only, not a Klacksy domain feature"),

        ["CalendarSelectionsController"] = Gap("calendar selection management"),
        ["SelectedCalendarsController"] = Gap("calendar selection management"),

        ["EmailFoldersController"] = Covered("list_email_folders"),
        ["ReceivedEmailController"] = Covered("list_emails", "read_email"),
        ["SpamRulesController"] = Covered("get_spam_filter_settings", "update_spam_filter_settings"),

        ["OrderExportController"] = Covered("list_sealed_orders", "list_recent_exports", "open_order_export"),
        ["ClientPeriodExportController"] = Excluded("new date-range client-period export (hours/expenses/breaks per employee/external employee) for period-closing; no chat skill yet, backend-only for this iteration"),
        ["OrderRangeExportController"] = Excluded("date-range ZIP export of all sealed orders plus client-period file for ERP hand-off; binary download, no chat skill yet, backend-only for this iteration"),
        ["PayrollExportController"] = Excluded("manual on-demand payroll export download for a closed period; binary download, no chat skill yet, backend-only for this iteration"),

        ["PeriodClosingController"] = Covered("close_period", "reopen_period", "approve_day", "revoke_day_approval", "generate_period_summary"),

        ["ReportExportController"] = Excluded("technical endpoint: turns rows the frontend already resolved into a spreadsheet, so the assistant has nothing to pass it"),
        ["RoutingController"] = Excluded("technical endpoint: server-side proxy that keeps the OpenRouteService API key off the browser and returns raw map geometry, nothing the assistant could act on"),
        ["ReportTemplatesController"] = Covered("list_report_templates"),
        ["ScheduleReportController"] = Covered("email_schedule_to_client"),

        ["ClientSortPreferencesController"] = Excluded("per-user UI sort preference persistence, not meaningful as a chat skill"),

        ["AbsenceDetailsController"] = Covered("search_client_absences", "cover_absence", "find_replacement"),
        ["AbsencesController"] = Covered("create_absence", "update_absence", "delete_absence", "list_absence_types"),
        ["AnalyseScenariosController"] = Covered("list_scenarios", "evaluate_scenario", "accept_scenario", "reject_scenario"),
        ["AutoWizardController"] = Covered("start_autowizard"),
        ["BreakPlaceholdersController"] = Gap("break placeholder planning"),
        ["BreaksController"] = Covered("add_break", "delete_break"),
        ["ContainerLocksController"] = Excluded("schedule grid concurrency locking between UI sessions, not chat-addressable"),
        ["ContainersController"] = Gap("container templates"),
        ["ExpensesController"] = Covered("list_expenses", "add_expense", "update_expense", "delete_expense"),
        ["HarmonizerController"] = Covered("start_wizard2"),
        ["HolisticHarmonizerController"] = Covered("start_wizard3"),
        ["RecoveryController"] = Covered("cover_absence"),
        ["ScheduleCommandsController"] = Covered("add_schedule_command"),
        ["ScheduleNotesController"] = Covered("list_schedule_notes", "add_schedule_note", "delete_schedule_note"),
        ["ShiftsController"] = Covered("create_shift", "update_shift", "delete_shift", "set_shift_required_qualification", "search_shifts", "get_shift_details"),
        ["WizardController"] = Covered("start_wizard1", "list_open_wizard_jobs", "cancel_wizard_job"),
        ["WorkChangesController"] = Covered("add_workchange"),
        ["WorksController"] = Covered("place_work", "delete_work", "confirm_work", "unconfirm_work", "read_schedule_state"),

        ["SchedulingRulesController"] = Covered("list_scheduling_rules", "create_scheduling_rule", "update_scheduling_rule", "delete_scheduling_rule", "get_scheduling_defaults", "update_scheduling_defaults"),
        ["CounterRulesController"] = Excluded("admin settings CRUD for counter rules (compliance event thresholds); admin settings card only, no Klacksy chat skill in this iteration"),
        ["PeriodCapRulesController"] = Excluded("admin settings CRUD for period-cap rules (compliance hour caps); admin settings card only, no Klacksy chat skill in this iteration"),
        ["RestrictedTimeWindowRulesController"] = Excluded("admin settings CRUD for restricted time-window rules (compliance forbidden windows); admin settings card only, no Klacksy chat skill in this iteration"),
        ["IndustryTemplatesController"] = Covered("start_planning_profile_setup", "set_planning_profile_parameters", "preview_planning_profile", "apply_planning_profile", "cancel_planning_profile_setup"),

        ["BranchController"] = Covered("list_branches", "create_branch", "update_branch", "delete_branch"),
        ["CalendarRulesController"] = Covered("import_calendar_rules", "validate_calendar_rule", "list_holidays_for_period", "validate_holiday_overlap"),
        ["CountriesController"] = Covered("list_countries"),
        ["GeneralSettingsController"] = Covered("get_general_settings", "update_general_settings", "get_email_settings", "update_email_settings", "get_imap_settings", "update_imap_settings", "get_owner_address", "update_owner_address", "get_work_settings", "update_work_settings", "get_deepl_settings", "update_deepl_settings", "get_web_search_settings", "update_web_search_settings", "test_smtp_connection", "test_imap_connection", "set_erp_import_schedule"),
        ["MacrosController"] = Covered("list_macros", "create_macro", "update_macro", "delete_macro"),
        ["PostcodeChController"] = Covered("lookup_location"),
        ["QualificationController"] = Covered("create_qualification", "update_qualification", "delete_qualification", "list_qualifications", "set_client_qualification"),
        ["StatesController"] = Covered("list_states"),

        ["AddressesController"] = Covered("create_employee", "update_client", "validate_address"),
        ["AnnotationsController"] = Covered("add_client_note"),
        ["AssignedGroupsController"] = Covered("add_client_to_group", "remove_client_from_group", "set_user_group_scope"),
        ["ClientAvailabilitiesController"] = Covered("list_client_availabilities", "set_client_availability"),
        ["ClientsController"] = Covered("search_employees", "get_client_details", "create_employee", "update_client", "delete_client", "update_client_birthdate", "update_client_gender"),
        ["CommunicationsController"] = Covered("add_client_email", "add_client_phone"),

        ["BaseWebController"] = Excluded("base class for MVC web controllers, no own endpoints"),
        ["PasswordResetController"] = Excluded("anonymous password reset web flow, auth infrastructure"),
    };

    private static string Covered(params string[] skillNames)
    {
        return CoveredPrefix + string.Join(", ", skillNames);
    }

    private static string Excluded(string reason)
    {
        return ExcludedPrefix + reason;
    }

    private static string Gap(string area)
    {
        return ExcludedPrefix + "gap (" + area + GapRoadmapSuffix;
    }
}
