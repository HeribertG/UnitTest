// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.KnowledgeIndex.Application.Services;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class ProcessMemorySnapshotTests
{
    // Binary megabytes, not decimal: the values this type produces are compared against docker's
    // memory limits and against /proc, and both count 1024 * 1024. A snapshot that quietly used
    // 1_000_000 would read about 5% low and make a container look further from its limit than it is.
    private const long OneMegabyte = 1024L * 1024L;

    [Test]
    public void Megabytes_ConvertUsingBinaryUnits()
    {
        var snapshot = new ProcessMemorySnapshot(
            residentBytes: 2304 * OneMegabyte,
            managedHeapSizeBytes: 512 * OneMegabyte,
            allocatedManagedBytes: 384 * OneMegabyte);

        snapshot.ResidentMegabytes.ShouldBe(2304d);
        snapshot.ManagedHeapMegabytes.ShouldBe(512d);
        snapshot.AllocatedManagedMegabytes.ShouldBe(384d);
    }

    [Test]
    public void NativeMegabytes_IsResidentMinusManagedHeap()
    {
        var snapshot = new ProcessMemorySnapshot(
            residentBytes: 2304 * OneMegabyte,
            managedHeapSizeBytes: 512 * OneMegabyte,
            allocatedManagedBytes: 384 * OneMegabyte);

        snapshot.NativeMegabytes.ShouldBe(1792d);
    }

    // A managed heap larger than the working set is possible whenever part of the heap is paged out.
    // The value is reported as measured rather than clamped to zero, because a negative native share
    // says something about the host that a zero would hide.
    [Test]
    public void NativeMegabytes_ManagedHeapExceedsResident_StaysNegative()
    {
        var snapshot = new ProcessMemorySnapshot(
            residentBytes: 100 * OneMegabyte,
            managedHeapSizeBytes: 150 * OneMegabyte,
            allocatedManagedBytes: 120 * OneMegabyte);

        snapshot.NativeMegabytes.ShouldBe(-50d);
    }

    [Test]
    public void Megabytes_SubMegabyteValues_KeepTheirFraction()
    {
        var snapshot = new ProcessMemorySnapshot(
            residentBytes: OneMegabyte / 2,
            managedHeapSizeBytes: OneMegabyte / 4,
            allocatedManagedBytes: 0);

        snapshot.ResidentMegabytes.ShouldBe(0.5d);
        snapshot.ManagedHeapMegabytes.ShouldBe(0.25d);
        snapshot.AllocatedManagedMegabytes.ShouldBe(0d);
    }

    // Capture reads the live process, so the only claims that hold on every machine are that it
    // returns something and that its parts stay consistent with each other. No ONNX session is
    // involved: the point is that the reader itself works, not what the numbers happen to be.
    [Test]
    public void Capture_ReturnsPlausibleLiveReading()
    {
        var snapshot = ProcessMemorySnapshot.Capture();

        snapshot.ResidentMegabytes.ShouldBeGreaterThan(0d);
        snapshot.ManagedHeapMegabytes.ShouldBeGreaterThan(0d);
        snapshot.AllocatedManagedMegabytes.ShouldBeGreaterThan(0d);
        snapshot.NativeMegabytes.ShouldBe(
            snapshot.ResidentMegabytes - snapshot.ManagedHeapMegabytes);
    }
}
