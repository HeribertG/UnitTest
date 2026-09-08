// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ProactiveTriggerDispatchRepository: RecordAsync persists a dispatch row once per
/// (user, kind, dedup key), and after a failed SaveChangesAsync the poisoned row is detached so
/// the change tracker stays clean and subsequent records in the same scope still succeed.
/// Also covers the inbox surface: listing own rows newest first with unread filter and take,
/// counting unread rows, loading recent reactions per kind for dismiss-streak learning, and
/// marking single rows (ownership enforced) as read and marking exactly the rows of one fetched
/// page as read, which must leave every unread row beyond that page untouched - and, for both mark-read
/// paths, that reading a row never touches AcknowledgedAtUtc or NextReminderAtUtc (package F1: reading
/// is not acknowledging; the MarkAllReadAsync counterpart lives in the CAS integration test). Also covers the
/// reminder surface: condition-scoped dedup (a recurrence under a new ConditionId gets its own row),
/// the due-for-reminder listing with its filter and ordering rules, and acknowledgement as the only
/// stop truth (ownership enforced, idempotent, clears the schedule).
/// Uses a shared in-memory DataBaseContext, mirroring the neighbouring repository tests.
/// MarkAllReadAsync, the two Try*Reminder compare-and-swap methods and the set-based
/// AcknowledgeAllForKindAsync of F1 (muting a kind acknowledges the user's open rows of it) use
/// ExecuteUpdateAsync, which the EF in-memory provider does not support, so they are intentionally
/// not covered here (same as the other ExecuteUpdateAsync repositories); their real behaviour is
/// verified by ProactiveTriggerDispatchRepositoryReminderCasTests against a real database.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class ProactiveTriggerDispatchRepositoryTests
{
    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private static ProactiveTriggerDispatchRow Row(Guid id, string userId, string dedupKey) => new()
    {
        Id = id,
        UserId = userId,
        TriggerKind = "test_kind",
        DedupKey = dedupKey,
        ContentKey = "content",
        Severity = "low"
    };

    private static ProactiveTriggerDispatchRow DedupOnlyRow(Guid id, string userId, string dedupKey) => new()
    {
        Id = id,
        UserId = userId,
        TriggerKind = "test_kind",
        DedupKey = dedupKey,
        ContentKey = null,
        Severity = "low"
    };

    [Test]
    public async Task RecordAsync_PersistsRow()
    {
        using var context = CreateContext();
        var repository = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);

        await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-1"));

        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task RecordAsync_SameUserKindAndDedupKey_IsRecordedOnlyOnce()
    {
        using var context = CreateContext();
        var repository = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);

        await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-1"));
        await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-1"));

        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task ListForUserAsync_ReturnsOnlyOwnRows_NewestFirst()
    {
        var oldestId = Guid.NewGuid();
        var newestId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(Row(oldestId, "user-a", "dedup-1"));
            await Task.Delay(TimeSpan.FromMilliseconds(10));
            await repository.RecordAsync(Row(newestId, "user-a", "dedup-2"));
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-b", "dedup-3"));
        }

        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .ListForUserAsync("user-a", unreadOnly: false, take: 10);

        result.Count.ShouldBe(2);
        result[0].Id.ShouldBe(newestId);
        result[1].Id.ShouldBe(oldestId);
    }

    [Test]
    public async Task ListForUserAsync_UnreadOnly_ExcludesReadRows()
    {
        var readId = Guid.NewGuid();
        var unreadId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(Row(readId, "user-a", "dedup-1"));
            await repository.RecordAsync(Row(unreadId, "user-a", "dedup-2"));
            await repository.MarkReadAsync(readId, "user-a");
        }

        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .ListForUserAsync("user-a", unreadOnly: true, take: 10);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(unreadId);
    }

    [Test]
    public async Task ListForUserAsync_RespectsTake()
    {
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-1"));
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-2"));
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-3"));
        }

        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .ListForUserAsync("user-a", unreadOnly: false, take: 2);

        result.Count.ShouldBe(2);
    }

    [Test]
    public async Task GetRecentReactionsAsync_ReturnsOnlyOwnReactedRowsOfKind_NewestReactionFirst_RespectsTake()
    {
        static ProactiveTriggerDispatchRow ReactedRow(string userId, string kind, string dedupKey, ProactiveReaction reaction, DateTime? reactionAtUtc) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TriggerKind = kind,
            DedupKey = dedupKey,
            Reaction = reaction,
            ReactionAtUtc = reactionAtUtc
        };

        var baseTime = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(ReactedRow("user-a", "test_kind", "dedup-1", ProactiveReaction.Dismissed, baseTime.AddMinutes(-3)));
            await repository.RecordAsync(ReactedRow("user-a", "test_kind", "dedup-2", ProactiveReaction.Helpful, baseTime.AddMinutes(-2)));
            await repository.RecordAsync(ReactedRow("user-a", "test_kind", "dedup-3", ProactiveReaction.Dismissed, baseTime.AddMinutes(-1)));
            await repository.RecordAsync(ReactedRow("user-a", "test_kind", "dedup-4", ProactiveReaction.None, null));
            await repository.RecordAsync(ReactedRow("user-a", "other_kind", "dedup-5", ProactiveReaction.Dismissed, baseTime));
            await repository.RecordAsync(ReactedRow("user-b", "test_kind", "dedup-6", ProactiveReaction.Dismissed, baseTime));
        }

        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .GetRecentReactionsAsync("user-a", "test_kind", take: 2);

        result.Count.ShouldBe(2);
        result[0].DedupKey.ShouldBe("dedup-3");
        result[0].Reaction.ShouldBe(ProactiveReaction.Dismissed);
        result[1].DedupKey.ShouldBe("dedup-2");
        result[1].Reaction.ShouldBe(ProactiveReaction.Helpful);
    }

    [Test]
    public async Task CountUnreadAsync_CountsOnlyOwnUnreadRows()
    {
        var readId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(Row(readId, "user-a", "dedup-1"));
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-2"));
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-b", "dedup-3"));
            await repository.MarkReadAsync(readId, "user-a");
        }

        using var context = CreateContext();
        var count = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System).CountUnreadAsync("user-a");

        count.ShouldBe(1);
    }

    [Test]
    public async Task ListForUserAsync_ExcludesRowsWithoutContentKey()
    {
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-1"));
            await repository.RecordAsync(DedupOnlyRow(Guid.NewGuid(), "user-a", "dedup-2"));
        }

        using var context = CreateContext();
        var rows = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System).ListForUserAsync("user-a", unreadOnly: false, take: 50);

        rows.Count.ShouldBe(1);
        rows[0].DedupKey.ShouldBe("dedup-1");
    }

    [Test]
    public async Task CountUnreadAsync_ExcludesRowsWithoutContentKey()
    {
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-1"));
            await repository.RecordAsync(DedupOnlyRow(Guid.NewGuid(), "user-a", "dedup-2"));
        }

        using var context = CreateContext();
        var count = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System).CountUnreadAsync("user-a");

        count.ShouldBe(1);
    }

    [Test]
    public async Task WasDispatchedAsync_StillSeesRowsWithoutContentKey()
    {
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(DedupOnlyRow(Guid.NewGuid(), "user-a", "dedup-1"));
        }

        using var context = CreateContext();
        var wasDispatched = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .WasDispatchedAsync("user-a", "test_kind", "dedup-1", null);

        wasDispatched.ShouldBeTrue();
    }

    [Test]
    public async Task MarkReadAsync_OwnUnreadRow_SetsReadAtUtcAndReturnsTrue()
    {
        var id = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            await new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System).RecordAsync(Row(id, "user-a", "dedup-1"));
        }

        var before = DateTime.UtcNow;
        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System).MarkReadAsync(id, "user-a");

        result.ShouldBeTrue();
        using var verify = CreateContext();
        var row = await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == id);
        row.ReadAtUtc.ShouldNotBeNull();
        row.ReadAtUtc.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Test]
    public async Task MarkReadAsync_AlreadyReadRow_KeepsOriginalTimestampAndReturnsTrue()
    {
        var id = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(Row(id, "user-a", "dedup-1"));
            await repository.MarkReadAsync(id, "user-a");
        }

        DateTime? firstReadAt;
        using (var verifyFirst = CreateContext())
        {
            firstReadAt = (await verifyFirst.AgentTriggerDispatches.SingleAsync(d => d.Id == id)).ReadAtUtc;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(10));
        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System).MarkReadAsync(id, "user-a");

        result.ShouldBeTrue();
        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == id)).ReadAtUtc.ShouldBe(firstReadAt);
    }

    [Test]
    public async Task MarkReadAsync_RowOfOtherUser_ReturnsFalseAndDoesNotMark()
    {
        var id = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            await new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System).RecordAsync(Row(id, "user-b", "dedup-1"));
        }

        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System).MarkReadAsync(id, "user-a");

        result.ShouldBeFalse();
        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == id)).ReadAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task MarkReadAsync_UnknownId_ReturnsFalse()
    {
        using var context = CreateContext();

        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System).MarkReadAsync(Guid.NewGuid(), "user-a");

        result.ShouldBeFalse();
    }

    [Test]
    public async Task MarkManyReadAsync_LeavesRowsBeyondTheFetchedPageUnread()
    {
        const int unreadRows = 63;
        const int pageSize = 50;
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            for (var i = 0; i < unreadRows; i++)
            {
                await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", $"dedup-{i}"));
            }
        }

        using var context = CreateContext();
        var inboxRepository = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);
        var page = await inboxRepository.ListForUserAsync("user-a", unreadOnly: true, take: pageSize);
        page.Count.ShouldBe(pageSize);

        await inboxRepository.MarkManyReadAsync(page.Select(row => row.Id).ToList(), "user-a");

        using var verify = CreateContext();
        var verifyRepository = new ProactiveTriggerDispatchRepository(verify, TimeProvider.System);
        (await verifyRepository.CountUnreadAsync("user-a")).ShouldBe(unreadRows - pageSize);
        var shownIds = page.Select(row => row.Id).ToHashSet();
        (await verify.AgentTriggerDispatches
            .Where(d => !shownIds.Contains(d.Id))
            .AllAsync(d => d.ReadAtUtc == null)).ShouldBeTrue();
    }

    [Test]
    public async Task MarkManyReadAsync_IgnoresRowsOfAnotherUser()
    {
        var ownId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(Row(ownId, "user-a", "dedup-own"));
            await repository.RecordAsync(Row(foreignId, "user-b", "dedup-foreign"));
        }

        using var context = CreateContext();
        await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .MarkManyReadAsync([ownId, foreignId], "user-a");

        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == ownId)).ReadAtUtc.ShouldNotBeNull();
        (await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == foreignId)).ReadAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task MarkManyReadAsync_EmptyIdList_TouchesNothing()
    {
        var id = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            await new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System).RecordAsync(Row(id, "user-a", "dedup-1"));
        }

        using var context = CreateContext();
        await new ProactiveTriggerDispatchRepository(context, TimeProvider.System).MarkManyReadAsync([], "user-a");

        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == id)).ReadAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task MarkReadAsync_LeavesTheAcknowledgementAndTheReminderScheduleUntouched()
    {
        // F1 negative case: acknowledgement is the ONLY stop truth of the reminder backoff. Reading a
        // message must not quietly end the loop, so neither column may move.
        var id = Guid.NewGuid();
        var due = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        using (var seedContext = CreateContext())
        {
            var row = Row(id, "user-a", "dedup-ack-1");
            row.NextReminderAtUtc = due;
            row.ReminderCount = 2;
            await new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System).RecordAsync(row);
        }

        using (var context = CreateContext())
        {
            (await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
                .MarkReadAsync(id, "user-a")).ShouldBeTrue();
        }

        using var verify = CreateContext();
        var stored = await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == id);
        stored.ReadAtUtc.ShouldNotBeNull();
        stored.AcknowledgedAtUtc.ShouldBeNull();
        stored.NextReminderAtUtc.ShouldBe(due);
        stored.ReminderCount.ShouldBe(2);
    }

    [Test]
    public async Task MarkManyReadAsync_LeavesTheAcknowledgementAndTheReminderScheduleUntouched()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var due = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            foreach (var (id, dedup) in new[] { (first, "dedup-ack-2"), (second, "dedup-ack-3") })
            {
                var row = Row(id, "user-a", dedup);
                row.NextReminderAtUtc = due;
                row.ReminderCount = 1;
                await repository.RecordAsync(row);
            }
        }

        using (var context = CreateContext())
        {
            await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
                .MarkManyReadAsync([first, second], "user-a");
        }

        using var verify = CreateContext();
        var rows = await verify.AgentTriggerDispatches.Where(d => d.Id == first || d.Id == second).ToListAsync();
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(d => d.ReadAtUtc != null);
        rows.ShouldAllBe(d => d.AcknowledgedAtUtc == null);
        rows.ShouldAllBe(d => d.NextReminderAtUtc == due);
        rows.ShouldAllBe(d => d.ReminderCount == 1);
    }

    [Test]
    public async Task RecordAsync_AfterFailedSave_LeavesTrackerCleanAndNextRecordSucceeds()
    {
        var duplicateId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            await new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System).RecordAsync(Row(duplicateId, "user-a", "dedup-1"));
        }

        using var context = CreateContext();
        var repository = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);

        await Should.ThrowAsync<Exception>(
            () => repository.RecordAsync(Row(duplicateId, "user-b", "dedup-2")));

        context.ChangeTracker.Entries<ProactiveTriggerDispatchRow>()
            .Count(entry => entry.State == EntityState.Added)
            .ShouldBe(0);

        await repository.RecordAsync(Row(Guid.NewGuid(), "user-c", "dedup-3"));

        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.CountAsync()).ShouldBe(2);
    }

    private static ProactiveTriggerDispatchRow ReminderRow(
        Guid id,
        string userId,
        string dedupKey,
        Guid? conditionId,
        DateTime? nextReminderAtUtc,
        DateTime? acknowledgedAtUtc = null,
        string? contentKey = "content") => new()
    {
        Id = id,
        UserId = userId,
        TriggerKind = "test_kind",
        DedupKey = dedupKey,
        ContentKey = contentKey,
        Severity = "low",
        ConditionId = conditionId,
        NextReminderAtUtc = nextReminderAtUtc,
        AcknowledgedAtUtc = acknowledgedAtUtc
    };

    [Test]
    public async Task WasDispatchedAsync_WithConditionId_MatchesOnlyTheRowOfThatCondition()
    {
        var conditionId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            await new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System)
                .RecordAsync(ReminderRow(Guid.NewGuid(), "user-a", "dedup-1", conditionId, null));
        }

        using var context = CreateContext();
        var repository = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);

        (await repository.WasDispatchedAsync("user-a", "test_kind", "dedup-1", conditionId)).ShouldBeTrue();
        (await repository.WasDispatchedAsync("user-a", "test_kind", "dedup-1", Guid.NewGuid())).ShouldBeFalse();
    }

    [Test]
    public async Task WasDispatchedAsync_NullConditionId_MatchesRegardlessOfTheConditionLink()
    {
        using (var seedContext = CreateContext())
        {
            await new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System)
                .RecordAsync(ReminderRow(Guid.NewGuid(), "user-a", "dedup-1", Guid.NewGuid(), null));
        }

        using var context = CreateContext();
        var wasDispatched = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .WasDispatchedAsync("user-a", "test_kind", "dedup-1", null);

        wasDispatched.ShouldBeTrue();
    }

    [Test]
    public async Task RecordAsync_RecurrenceWithNewConditionId_CreatesANewRow()
    {
        using var context = CreateContext();
        var repository = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);

        await repository.RecordAsync(ReminderRow(Guid.NewGuid(), "user-a", "dedup-1", Guid.NewGuid(), null));
        await repository.RecordAsync(ReminderRow(Guid.NewGuid(), "user-a", "dedup-1", Guid.NewGuid(), null));

        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.CountAsync()).ShouldBe(2);
    }

    [Test]
    public async Task RecordAsync_SameConditionId_IsRecordedOnlyOnce()
    {
        var conditionId = Guid.NewGuid();
        using var context = CreateContext();
        var repository = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);

        await repository.RecordAsync(ReminderRow(Guid.NewGuid(), "user-a", "dedup-1", conditionId, null));
        await repository.RecordAsync(ReminderRow(Guid.NewGuid(), "user-a", "dedup-1", conditionId, null));

        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task GetDueForReminderAsync_ReturnsDueRowsOrderedByDueDate_AndExcludesNotDueAcknowledgedUnlinkedAndLedgerOnlyRows()
    {
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
        var dueEarlierId = Guid.NewGuid();
        var dueLaterId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(ReminderRow(dueLaterId, "user-a", "dedup-1", Guid.NewGuid(), now.AddHours(-1)));
            await repository.RecordAsync(ReminderRow(dueEarlierId, "user-a", "dedup-2", Guid.NewGuid(), now.AddHours(-2)));
            await repository.RecordAsync(ReminderRow(Guid.NewGuid(), "user-a", "dedup-3", Guid.NewGuid(), now.AddHours(1)));
            await repository.RecordAsync(ReminderRow(Guid.NewGuid(), "user-a", "dedup-4", Guid.NewGuid(), now.AddHours(-3), acknowledgedAtUtc: now.AddHours(-4)));
            await repository.RecordAsync(ReminderRow(Guid.NewGuid(), "user-a", "dedup-5", null, now.AddHours(-5)));
            await repository.RecordAsync(ReminderRow(Guid.NewGuid(), "user-a", "dedup-6", Guid.NewGuid(), now.AddHours(-6), contentKey: null));
            await repository.RecordAsync(ReminderRow(Guid.NewGuid(), "user-a", "dedup-7", Guid.NewGuid(), null));
        }

        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .GetDueForReminderAsync(now, take: 10);

        result.Count.ShouldBe(2);
        result[0].Id.ShouldBe(dueEarlierId);
        result[1].Id.ShouldBe(dueLaterId);
    }

    [Test]
    public async Task GetDueForReminderAsync_RespectsTake()
    {
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            for (var i = 0; i < 3; i++)
            {
                await repository.RecordAsync(ReminderRow(Guid.NewGuid(), "user-a", $"dedup-{i}", Guid.NewGuid(), now.AddHours(-1 - i)));
            }
        }

        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .GetDueForReminderAsync(now, take: 2);

        result.Count.ShouldBe(2);
    }

    [Test]
    public async Task AcknowledgeAsync_OwnRow_SetsAcknowledgedAtUtcAndClearsTheReminderSchedule()
    {
        var id = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            await new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System)
                .RecordAsync(ReminderRow(id, "user-a", "dedup-1", Guid.NewGuid(), DateTime.UtcNow.AddHours(1)));
        }

        var before = DateTime.UtcNow;
        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .AcknowledgeAsync(id, "user-a");

        result.ShouldBeTrue();
        using var verify = CreateContext();
        var row = await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == id);
        row.AcknowledgedAtUtc.ShouldNotBeNull();
        row.AcknowledgedAtUtc.Value.ShouldBeGreaterThanOrEqualTo(before);
        row.NextReminderAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task AcknowledgeAsync_AlreadyAcknowledgedRow_KeepsTheFirstTimestampAndReturnsTrue()
    {
        var id = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(ReminderRow(id, "user-a", "dedup-1", Guid.NewGuid(), DateTime.UtcNow.AddHours(1)));
            await repository.AcknowledgeAsync(id, "user-a");
        }

        DateTime? firstAcknowledgedAt;
        using (var verifyFirst = CreateContext())
        {
            firstAcknowledgedAt = (await verifyFirst.AgentTriggerDispatches.SingleAsync(d => d.Id == id)).AcknowledgedAtUtc;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(10));
        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .AcknowledgeAsync(id, "user-a");

        result.ShouldBeTrue();
        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == id)).AcknowledgedAtUtc.ShouldBe(firstAcknowledgedAt);
    }

    [Test]
    public async Task AcknowledgeAsync_RowOfOtherUser_ReturnsFalseAndDoesNotAcknowledge()
    {
        var id = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            await new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System)
                .RecordAsync(ReminderRow(id, "user-b", "dedup-1", Guid.NewGuid(), DateTime.UtcNow.AddHours(1)));
        }

        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .AcknowledgeAsync(id, "user-a");

        result.ShouldBeFalse();
        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == id)).AcknowledgedAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task AcknowledgeAsync_UnknownId_ReturnsFalse()
    {
        using var context = CreateContext();

        var result = await new ProactiveTriggerDispatchRepository(context, TimeProvider.System)
            .AcknowledgeAsync(Guid.NewGuid(), "user-a");

        result.ShouldBeFalse();
    }

    [Test]
    public async Task GetAcknowledgedConditionIdsAsync_ReturnsOnlyAcknowledgedIdsOfThatUser()
    {
        var acknowledgedRowId = Guid.NewGuid();
        var openRowId = Guid.NewGuid();
        var foreignRowId = Guid.NewGuid();
        var acknowledgedConditionId = Guid.NewGuid();
        var openConditionId = Guid.NewGuid();
        var foreignConditionId = Guid.NewGuid();

        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(LinkedRow(acknowledgedRowId, "user-a", "dedup-1", acknowledgedConditionId));
            await repository.RecordAsync(LinkedRow(openRowId, "user-a", "dedup-2", openConditionId));
            await repository.RecordAsync(LinkedRow(foreignRowId, "user-b", "dedup-3", foreignConditionId));
            (await repository.AcknowledgeAsync(acknowledgedRowId, "user-a")).ShouldBeTrue();
            (await repository.AcknowledgeAsync(foreignRowId, "user-b")).ShouldBeTrue();
        }

        using var context = CreateContext();
        var sut = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);

        var result = await sut.GetAcknowledgedConditionIdsAsync(
            "user-a",
            new[] { acknowledgedConditionId, openConditionId, foreignConditionId });

        result.Count.ShouldBe(1);
        result.ShouldContain(acknowledgedConditionId);
        result.ShouldNotContain(openConditionId);
        result.ShouldNotContain(foreignConditionId);
    }

    [Test]
    public async Task GetAcknowledgedConditionIdsAsync_EmptyInput_ReturnsEmptySetWithoutQuerying()
    {
        using var context = CreateContext();
        var repository = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);

        var result = await repository.GetAcknowledgedConditionIdsAsync("user-a", Array.Empty<Guid>());

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetAcknowledgedConditionIdsAsync_IgnoresIdsThatWereNotAsked()
    {
        var rowId = Guid.NewGuid();
        var acknowledgedConditionId = Guid.NewGuid();

        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext, TimeProvider.System);
            await repository.RecordAsync(LinkedRow(rowId, "user-a", "dedup-1", acknowledgedConditionId));
            (await repository.AcknowledgeAsync(rowId, "user-a")).ShouldBeTrue();
        }

        using var context = CreateContext();
        var sut = new ProactiveTriggerDispatchRepository(context, TimeProvider.System);

        var result = await sut.GetAcknowledgedConditionIdsAsync("user-a", new[] { Guid.NewGuid() });

        result.ShouldBeEmpty();
    }

    private static ProactiveTriggerDispatchRow LinkedRow(Guid id, string userId, string dedupKey, Guid conditionId) => new()
    {
        Id = id,
        UserId = userId,
        TriggerKind = "test_kind",
        DedupKey = dedupKey,
        ContentKey = "content",
        Severity = "low",
        ConditionId = conditionId
    };
}
