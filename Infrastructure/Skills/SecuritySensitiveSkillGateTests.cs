// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the permission gate in front of the security-relevant skills that used to carry no
/// requiredPermissions at all — minting or revoking a personal access token, changing the autonomy
/// level, changing the proactive governance, scheduling and cancelling unattended runs and drafting a
/// multi-step plan were reachable by every role. They were then gated on CanPlan, which stopped
/// separating anyone once the Planer floor took CanPlan in. They now carry gates of
/// their own: CanManageAutomation and CanManageAccessTokens, neither of which is in the floor.
/// The genuinely self-service skills must stay open, otherwise a plain user loses their own account.
/// The last test widens the rule from a named handful to every seed: a skill that mutates state and
/// names no permission is reachable by every authenticated caller, so each one has to be listed with a
/// written reason instead of being discovered later.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SecuritySensitiveSkillGateTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";

    private const string MutateEffect = "Mutate";

    private static readonly string[] PluginSeedsRelativePath =
    [
        "Klacks.Api", "Plugins", "Features", "messaging", "skill-seeds.json"
    ];

    private const string VersionBumpReminder =
        "Remember to bump the skill's version when changing this — SkillSeedLoader silently skips a " +
        "seed whose version did not increase.";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly Dictionary<string, string> RequiredGateBySkill = new(StringComparer.Ordinal)
    {
        ["create_personal_access_token"] = Permissions.CanManageAccessTokens,
        ["revoke_personal_access_token"] = Permissions.CanManageAccessTokens,
        ["set_autonomy_level"] = Permissions.CanManageAutomation,
        ["set_proactive_governance"] = Permissions.CanManageAutomation,
        ["schedule_recurring_task"] = Permissions.CanManageAutomation,
        // Cancelling somebody's recurring task is the off switch of the same unattended machinery
        // schedule_recurring_task turns on: without a gate any authenticated caller could silently stop
        // every automated run of the installation.
        ["cancel_recurring_task"] = Permissions.CanManageAutomation,
        ["create_plan"] = Permissions.CanManageAutomation
    };

    private static readonly string[] MustStayOpenForSelfService =
    [
        "update_my_account",
        "add_personal_memory",
        "stash_pending_note",
        "manage_pending_notes"
    ];

    /// <summary>
    /// Every seeded skill with effect "Mutate" and no requiredPermissions, with the reason it is
    /// allowed to stay open to every authenticated caller. A skill that writes and names no permission
    /// is reachable from the Planer floor, so the list is the place where that is decided rather than
    /// discovered. Adding an entry here is a security decision: write why, or gate the skill instead.
    /// </summary>
    private static readonly Dictionary<string, string> UngatedMutatorsWithReason = new(StringComparer.Ordinal)
    {
        ["add_personal_memory"] =
            "Writes a fact about the signed-in user into their own memory; gating it would stop a plain " +
            "user from teaching Klacksy anything about themselves.",
        ["update_my_account"] =
            "Writes the caller's OWN login name and email. Gating it on an account permission would lock " +
            "a plain user out of their own account. The risk that remains — a redirected password-reset " +
            "address — is answered by SkillRiskClassifier.SensitiveSkills, which holds it for an explicit " +
            "confirmation at every autonomy level.",
        ["rollback_my_last_change"] =
            "Seeded as Mutate but only LOOKS UP the inverse of the caller's own last execution and " +
            "returns it as a proposal; the inverse call it suggests passes the gate of the skill it names " +
            "(SkillRiskClassifier.ReadOnlyExtras says the same).",
        ["confirm_pending_action"] =
            "Redeems a one-time confirmation token the server issued for an action that was already " +
            "permission-checked when it was proposed. A gate here would ask for a right the proposed " +
            "action does not need, and the token is worthless without the proposal.",
        ["start_guided_tour"] =
            "A UiAction that opens the onboarding tour overlay in the caller's own browser; it writes " +
            "nothing (SkillRiskClassifier.ReadOnlyExtras says the same).",
        ["stash_pending_note"] =
            "Open ONLY because StashPendingNoteSkill gates the dangerous half in its body: addressing a " +
            "note at another user or at everyone requires Roles.Admin, and a caller without it can stash " +
            "a note for themselves alone. Remove that body gate and this entry has to go with it.",
        ["manage_pending_notes"] =
            "Reads and archives the caller's own pending notes; the repository scopes every call to the " +
            "caller, so there is nothing of anybody else's to reach.",
        ["set_my_theme"] =
            "Writes the caller's own display preference. Nothing but their own screen changes.",
        ["set_my_display_language"] =
            "Writes the caller's own display preference. Nothing but their own screen changes.",
        ["create_donation_checkout"] =
            "Opens a payment session the CALLER pays, and charges nothing to the installation. It is " +
            "nonetheless in SkillRiskClassifier.SensitiveSkills, so it never runs unattended and always " +
            "asks in chat — the permission list is not what holds it."
    };

    private static Dictionary<string, JsonElement> LoadSkillsByName()
    {
        var json = File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName));
        using var document = JsonDocument.Parse(json);

        return document.RootElement
            .GetProperty("skills")
            .EnumerateArray()
            .Where(s => s.TryGetProperty("name", out _))
            .ToDictionary(
                s => s.GetProperty("name").GetString()!,
                s => s.Clone(),
                StringComparer.Ordinal);
    }

    private static List<string> PermissionsOf(JsonElement skill)
    {
        if (!skill.TryGetProperty("requiredPermissions", out var permissions)
            || permissions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return permissions.EnumerateArray().Select(p => p.GetString() ?? string.Empty).ToList();
    }

    [Test]
    public void SecuritySensitiveSkills_CarryTheirOwnGate()
    {
        var skills = LoadSkillsByName();

        foreach (var (name, gate) in RequiredGateBySkill)
        {
            skills.ShouldContainKey(name);
            PermissionsOf(skills[name]).ShouldContain(
                gate,
                $"'{name}' mutates security-relevant state and must be gated on {gate}. {VersionBumpReminder}");

            PermissionsOf(skills[name]).ShouldNotContain(
                Permissions.CanPlan,
                $"'{name}' must not fall back to CanPlan: CanPlan is part of the Planer floor, so every " +
                $"authenticated caller holds it and the gate would separate nobody. {VersionBumpReminder}");
        }
    }

    [Test]
    public void PlannerFloor_ReachesNoneOfTheSecuritySensitiveSkills()
    {
        var skills = LoadSkillsByName();
        var floorFromEmptyRoleList = Permissions.ExpandRoles(Array.Empty<string>());
        var floorFromUserRole = Permissions.GetPermissionsForRole(Roles.User);

        foreach (var (name, gate) in RequiredGateBySkill)
        {
            skills.ShouldContainKey(name);
            var required = PermissionsOf(skills[name]).ToArray();

            Permissions.HasAllPermissions(floorFromEmptyRoleList, required).ShouldBeFalse(
                $"A caller without any role (Planer) must not reach '{name}'. Its gate is {gate}, which " +
                "must stay out of Permissions.PlannerFloor.");

            Permissions.HasAllPermissions(floorFromUserRole, required).ShouldBeFalse(
                $"The User role falls through to the Planer floor and must not reach '{name}' either.");
        }

        floorFromEmptyRoleList.ShouldNotContain(Permissions.CanManageAutomation);
        floorFromEmptyRoleList.ShouldNotContain(Permissions.CanManageAccessTokens);
    }

    [Test]
    public void AdminAndAuthorised_ReachAllSecuritySensitiveSkills()
    {
        var skills = LoadSkillsByName();
        var admin = Permissions.ExpandRoles([Roles.Admin]);
        var authorised = Permissions.ExpandRoles([Roles.Authorised]);

        foreach (var name in RequiredGateBySkill.Keys)
        {
            skills.ShouldContainKey(name);
            var required = PermissionsOf(skills[name]).ToArray();

            Permissions.HasAllPermissions(admin, required).ShouldBeTrue(
                $"Admin must keep reaching '{name}'.");

            Permissions.HasAllPermissions(authorised, required).ShouldBeTrue(
                $"Authorised (Supervisor) must keep reaching '{name}'.");
        }
    }

    [Test]
    public void SelfServiceSkills_StayOpenToEveryRole()
    {
        var skills = LoadSkillsByName();

        foreach (var name in MustStayOpenForSelfService)
        {
            skills.ShouldContainKey(name);
            PermissionsOf(skills[name]).ShouldBeEmpty(
                $"'{name}' only touches the caller's own data; gating it would lock a plain user out " +
                "of their own account.");
        }
    }

    [Test]
    public void EveryUngatedMutatingSkill_IsOnTheAllowlistWithAReason()
    {
        var ungated = AllSeededSkills()
            .Where(skill => string.Equals(EffectOf(skill.Skill), MutateEffect, StringComparison.Ordinal))
            .Where(skill => PermissionsOf(skill.Skill).Count == 0)
            .ToList();

        var unlisted = ungated
            .Where(skill => !UngatedMutatorsWithReason.ContainsKey(skill.Name))
            .Select(skill => $"{skill.Name} ({skill.Source})")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        unlisted.ShouldBeEmpty(
            "A seeded skill that mutates state and names no requiredPermissions is reachable by every " +
            "authenticated caller, including one without any role (Planer). Either give it a gate that " +
            "is outside Permissions.PlannerFloor, or add it to UngatedMutatorsWithReason with the reason " +
            $"it may stay open. {VersionBumpReminder} Unlisted: " + string.Join(", ", unlisted));

        var ungatedNames = ungated.Select(skill => skill.Name).ToHashSet(StringComparer.Ordinal);
        var stale = UngatedMutatorsWithReason.Keys
            .Where(name => !ungatedNames.Contains(name))
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        stale.ShouldBeEmpty(
            "These skills are no longer ungated mutators — they were gated, renamed or removed — so the " +
            "written justification is dead weight that hides the fact that nobody reviewed it. Remove: "
            + string.Join(", ", stale));
    }

    [Test]
    public void StashPendingNote_StaysOnTheAllowlistOnlyWhileItsBodyGateExists()
    {
        UngatedMutatorsWithReason.ShouldContainKey("stash_pending_note");

        var skillSource = File.ReadAllText(LocateSourceFile(
            "Klacks.Api", "Application", "Skills", "StashPendingNoteSkill.cs"));

        skillSource.ShouldContain(
            "Permissions.HasPermission(context.UserPermissions, Roles.Admin)",
            Case.Sensitive,
            "stash_pending_note carries no requiredPermissions, so a caller without any role reaches it. " +
            "It is allowed to stay ungated ONLY because the skill body refuses a note addressed at " +
            "another user or at everyone unless the caller is an administrator. If that gate is gone, " +
            "remove the skill from UngatedMutatorsWithReason and give it a real permission instead.");
    }

    [Test]
    public void PluginSeeds_DeclareAPermissionForEveryMutatingSkill()
    {
        var pluginMutatorsWithoutGate = AllSeededSkills()
            .Where(skill => skill.Source != SkillSeedsFileName)
            .Where(skill => string.Equals(EffectOf(skill.Skill), MutateEffect, StringComparison.Ordinal))
            .Where(skill => PermissionsOf(skill.Skill).Count == 0)
            .Select(skill => skill.Name)
            .ToList();

        pluginMutatorsWithoutGate.ShouldBeEmpty(
            "A feature plugin ships its own seeds, and a mutating plugin skill without a permission is " +
            "reachable from the Planer floor just like a core one. Gate it in the plugin's seed file.");
    }

    private static IEnumerable<(string Name, string Source, JsonElement Skill)> AllSeededSkills()
    {
        foreach (var skill in LoadSkillsByName())
        {
            yield return (skill.Key, SkillSeedsFileName, skill.Value);
        }

        foreach (var skill in LoadPluginSkills())
        {
            yield return skill;
        }
    }

    /// <summary>
    /// A plugin seed file is a bare JSON array, the core file an object with a "skills" property. Both
    /// shapes are read here so a plugin can never escape the rule by carrying the other one.
    /// </summary>
    private static List<(string Name, string Source, JsonElement Skill)> LoadPluginSkills()
    {
        var path = LocateSourceFile(PluginSeedsRelativePath);
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var skills = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.GetProperty("skills");

        return skills
            .EnumerateArray()
            .Where(skill => skill.TryGetProperty("name", out _))
            .Select(skill => (
                Name: skill.GetProperty("name").GetString()!,
                Source: Path.GetFileName(Path.GetDirectoryName(path)!) + "/" + SkillSeedsFileName,
                Skill: skill.Clone()))
            .ToList();
    }

    private static string EffectOf(JsonElement skill)
        => skill.TryGetProperty("effect", out var effect) && effect.ValueKind == JsonValueKind.String
            ? effect.GetString() ?? string.Empty
            : string.Empty;

    private static string LocateSourceFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativePath).ToArray());

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativePath)} above the test directory.");
    }

    private static string LocateDefinitionsFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(DefinitionsRelativePath).Concat([fileName]).ToArray());

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {fileName} above the test directory.");
    }
}
