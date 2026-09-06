// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Diagnostics;
using System.Globalization;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx.Probe;

/// <summary>
/// Reads resident and peak-resident memory of the current process. On Linux it parses
/// /proc/self/status (VmRSS, VmHWM) because those are what the container's memcg OOM killer looks at;
/// elsewhere it falls back to the Process working-set counters.
/// </summary>
public static class ProcessMemoryProbe
{
    private const string ProcStatusPath = "/proc/self/status";
    private const string ResidentKey = "VmRSS:";
    private const string PeakResidentKey = "VmHWM:";
    private const double KilobytesPerMegabyte = 1024.0;
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    public sealed record Sample(double ResidentMb, double PeakResidentMb);

    public static Sample Read()
    {
        if (OperatingSystem.IsLinux() && File.Exists(ProcStatusPath))
        {
            return ReadFromProc();
        }

        var process = Process.GetCurrentProcess();
        process.Refresh();
        return new Sample(process.WorkingSet64 / BytesPerMegabyte, process.PeakWorkingSet64 / BytesPerMegabyte);
    }

    private static Sample ReadFromProc()
    {
        double resident = 0, peak = 0;
        foreach (var line in File.ReadLines(ProcStatusPath))
        {
            if (line.StartsWith(ResidentKey, StringComparison.Ordinal))
                resident = ParseKilobytes(line) / KilobytesPerMegabyte;
            else if (line.StartsWith(PeakResidentKey, StringComparison.Ordinal))
                peak = ParseKilobytes(line) / KilobytesPerMegabyte;
        }

        return new Sample(resident, peak);
    }

    private static double ParseKilobytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return double.Parse(parts[1], CultureInfo.InvariantCulture);
    }
}
