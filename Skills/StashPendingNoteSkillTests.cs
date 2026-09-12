// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for stash_pending_note. The skill carries no requiredPermissions, so every
/// authenticated caller reaches it, including one without any role (Planer). The gate lives in the
/// skill body: a note addressed at everyone or at a named other user is an administrator action,
/// while a note for the caller themselves stays open. The refusal text must name no internal
/// identifier (parameter name, permission constant, skill name).
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class StashPendingNoteSkillTests
{
    private const string Content = "Remind the team about the night shift.";

    private readonly Guid _agentId = Guid.NewGuid();
    private IPendingUserNoteRepository _notes = null!;
    private IAgentRepository _agents = null!;
    private StashPendingNoteSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _notes = Substitute.For<IPendingUserNoteRepository>();
        _agents = Substitute.For<IAgentRepository>();
        _agents.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = _agentId });

        _skill = new StashPendingNoteSkill(_notes, _agents);
    }

    private static SkillExecutionContext Planner() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = Permissions.ExpandRoles(Array.Empty<string>())
    };

    private static SkillExecutionContext Admin() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = Permissions.ExpandRoles([Roles.Admin])
    };

    private async Task<PendingUserNote> StashedNote()
    {
        var calls = _notes.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IPendingUserNoteRepository.AddAsync)).ToList();
        calls.Count.ShouldBe(1);
        await Task.CompletedTask;
        return (PendingUserNote)calls[0].GetArguments()[0]!;
    }

    [Test]
    public async Task Planner_AddressingEveryone_IsRefusedAndNothingIsStashed()
    {
        var context = Planner();

        var result = await _skill.ExecuteAsync(context, new Dictionary<string, object>
        {
            ["content"] = Content,
            ["forEveryone"] = true
        });

        result.Success.ShouldBeFalse();
        await _notes.DidNotReceive().AddAsync(Arg.Any<PendingUserNote>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Planner_AddressingAnotherUser_IsRefusedAndNothingIsStashed()
    {
        var context = Planner();

        var result = await _skill.ExecuteAsync(context, new Dictionary<string, object>
        {
            ["content"] = Content,
            ["userId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        await _notes.DidNotReceive().AddAsync(Arg.Any<PendingUserNote>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Refusal_NamesNoInternalIdentifier()
    {
        var result = await _skill.ExecuteAsync(Planner(), new Dictionary<string, object>
        {
            ["content"] = Content,
            ["forEveryone"] = true
        });

        result.Success.ShouldBeFalse();
        foreach (var identifier in new[] { "forEveryone", "userId", "stash_pending_note", Roles.Admin, "UserPermissions" })
        {
            result.Message!.ShouldNotContain(identifier, Case.Sensitive);
        }
    }

    [Test]
    public async Task Planner_StashingForThemselves_IsStored()
    {
        var context = Planner();

        var result = await _skill.ExecuteAsync(context, new Dictionary<string, object>
        {
            ["content"] = Content
        });

        result.Success.ShouldBeTrue();
        (await StashedNote()).UserId.ShouldBe(context.UserId);
    }

    [Test]
    public async Task Planner_NamingTheirOwnUserId_IsStored()
    {
        var context = Planner();

        var result = await _skill.ExecuteAsync(context, new Dictionary<string, object>
        {
            ["content"] = Content,
            ["userId"] = context.UserId.ToString()
        });

        result.Success.ShouldBeTrue();
        (await StashedNote()).UserId.ShouldBe(context.UserId);
    }

    [Test]
    public async Task Admin_AddressingEveryone_IsStoredAsABroadcast()
    {
        var result = await _skill.ExecuteAsync(Admin(), new Dictionary<string, object>
        {
            ["content"] = Content,
            ["forEveryone"] = true
        });

        result.Success.ShouldBeTrue();
        (await StashedNote()).UserId.ShouldBeNull();
    }

    [Test]
    public async Task Admin_AddressingAnotherUser_IsStoredForThatUser()
    {
        var recipient = Guid.NewGuid();

        var result = await _skill.ExecuteAsync(Admin(), new Dictionary<string, object>
        {
            ["content"] = Content,
            ["userId"] = recipient.ToString()
        });

        result.Success.ShouldBeTrue();
        (await StashedNote()).UserId.ShouldBe(recipient);
    }
}
