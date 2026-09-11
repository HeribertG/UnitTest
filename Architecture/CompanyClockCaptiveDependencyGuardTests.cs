// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard against a captive dependency: ICompanyClock is Scoped (it reads through a
/// Scoped DbContext), so a Singleton or HostedService that injects it directly would capture that
/// Scoped dependency for the whole app lifetime - Development's ValidateScopes/ValidateOnBuild would
/// fail at startup (Incident B1, 2026-09-11: NagerDateHolidayProvider was Singleton and injected
/// ICompanyClock in its constructor). Finds every concrete Klacks.Api class with ICompanyClock as a
/// direct constructor parameter via reflection, then source-scans every
/// <c>services.AddSingleton&lt;...&gt;(...)</c> statement in Infrastructure/Extensions/*.cs and
/// Program.cs (statement-level - from the "AddSingleton" token to its matching closing paren, like
/// DateTimeStylesGuardTests - because a registration can split its type arguments onto their own
/// line) for that class's simple name.
///
/// Known scope limits (documented rather than silently unchecked):
/// - Only a DIRECT ICompanyClock constructor parameter is detected. A class that captures it
///   transitively through another Scoped service is not found by this guard - auditing the full
///   transitive graph would require building the app's actual ServiceProvider with
///   ValidateScopes/ValidateOnBuild, which needs the full runtime configuration (DB connection
///   string, JWT settings, feature/language plugin discovery, ...) this unit test project does not
///   assemble. The B1 finding's manual audit (2026-09-11) covered the transitive case once; this
///   guard only prevents the direct case from silently recurring.
/// - Only <c>services.AddSingleton&lt;...&gt;</c> call sites are treated as violations. A Singleton
///   registered through a factory lambda that resolves ICompanyClock from the provider at request
///   time (e.g. inside a per-call method, not captured in a field) would not be a captive dependency
///   and is out of scope; none of the current AddSingleton factory lambdas do this (checked manually
///   2026-09-11).
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Domain.Interfaces.Settings;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class CompanyClockCaptiveDependencyGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const int MinimumConsumerCount = 60;
    private const string AddSingletonToken = "AddSingleton";

    // A known, unrelated Singleton registration used only to prove the statement-level AddSingleton
    // scan itself actually works (anti-vacuous, mirrors DateTimeStylesGuardTests' MultiLineCorrectSites).
    private const string KnownSingletonPositiveControl = "SettingsChangeVersion";

    private static readonly string[] RegistrationFileRelativePaths =
    [
        "Infrastructure/Extensions/ServiceCollectionExtensions.cs",
        "Infrastructure/Extensions/ErpImportServiceCollectionExtensions.cs",
        "Infrastructure/Extensions/AssistantExtensions.cs",
        "Infrastructure/Extensions/KlacksBotServiceCollectionExtensions.cs",
        "Infrastructure/Extensions/LanguagePluginExtensions.cs",
        "Infrastructure/Extensions/RegionSetupExtensions.cs",
        "Infrastructure/Extensions/FeaturePluginExtensions.cs",
        "Program.cs"
    ];

    [Test]
    public void NoSingletonRegistration_DirectlyInjectsTheScopedCompanyClock()
    {
        var consumers = FindDirectCompanyClockConsumers();

        consumers.Count.ShouldBeGreaterThan(
            MinimumConsumerCount,
            $"Only {consumers.Count} classes with a direct ICompanyClock constructor parameter were " +
            "found via reflection. The guard cannot have inspected the real Klacks.Api assembly, so a " +
            "green result would be meaningless.");

        var (singletonStatements, positiveControlFound) = ScanAddSingletonStatements();

        positiveControlFound.ShouldBeTrue(
            $"The known positive control '{KnownSingletonPositiveControl}' was not found inside any " +
            "AddSingleton<...>(...) statement - the statement-level scan itself is broken, so a green " +
            "result below would be meaningless.");

        var violations = consumers
            .Where(consumerName => singletonStatements.Any(stmt => ContainsTypeName(stmt, consumerName)))
            .ToList();

        violations.ShouldBeEmpty(
            "These classes have ICompanyClock as a direct constructor parameter but also appear inside " +
            "an AddSingleton<...>(...) statement - a captive dependency that fails Development's " +
            $"ValidateScopes/ValidateOnBuild at startup (see NagerDateHolidayProvider, 2026-09-11). " +
            $"Register as Scoped instead, or resolve ICompanyClock per-call via a scope factory " +
            $"instead of the constructor.{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations.Select(v => $"  {v}")));
    }

    private static List<string> FindDirectCompanyClockConsumers()
    {
        var assembly = typeof(ICompanyClock).Assembly;

        return assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ICompanyClock))))
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static (List<string> Statements, bool PositiveControlFound) ScanAddSingletonStatements()
    {
        var apiRoot = LocateApiProject();
        var statements = new List<string>();
        var positiveControlFound = false;

        foreach (var relativePath in RegistrationFileRelativePaths)
        {
            var absolutePath = Path.Combine(apiRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException(
                    $"Registration file '{relativePath}' was not found under '{apiRoot}'. The guard's " +
                    "file list is stale - update RegistrationFileRelativePaths.", absolutePath);
            }

            var text = File.ReadAllText(absolutePath);
            foreach (var statement in ExtractAddSingletonStatements(text))
            {
                statements.Add(statement);
                if (statement.Contains(KnownSingletonPositiveControl, StringComparison.Ordinal))
                {
                    positiveControlFound = true;
                }
            }
        }

        return (statements, positiveControlFound);
    }

    private static IEnumerable<string> ExtractAddSingletonStatements(string text)
    {
        var index = 0;
        while (true)
        {
            var tokenIndex = text.IndexOf(AddSingletonToken, index, StringComparison.Ordinal);
            if (tokenIndex < 0)
            {
                yield break;
            }

            var openParenIndex = text.IndexOf('(', tokenIndex);
            if (openParenIndex < 0)
            {
                yield break;
            }

            var closeParenIndex = FindMatchingCloseParen(text, openParenIndex);
            var end = closeParenIndex >= 0 ? closeParenIndex : text.Length - 1;
            yield return text[tokenIndex..(end + 1)];

            index = end + 1;
        }
    }

    private static int FindMatchingCloseParen(string text, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool ContainsTypeName(string statement, string typeName)
        => Regex.IsMatch(statement, $@"\b{Regex.Escape(typeName)}\b");

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, "Domain", "Services")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }
}
