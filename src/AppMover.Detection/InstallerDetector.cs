using AppMover.Core.Models;
using AppMover.Detection.Rules;
using Microsoft.Extensions.Logging;

namespace AppMover.Detection;

/// <summary>Step 2: identifies the technology used to install an application.</summary>
public sealed class InstallerDetector(DetectionContext context, ILogger<InstallerDetector> logger, IReadOnlyList<IDetectionRule>? rules = null)
{
    /// <summary>Minimum score for a technology to be retained.</summary>
    public const int Threshold = 30;

    /// <summary>Rules in priority order (first wins on equal scores).</summary>
    public static IReadOnlyList<IDetectionRule> DefaultRules { get; } =
    [
        new ClickOnceRule(),
        new SquirrelRule(),
        new WixBurnRule(),
        new MsiRule(),
        new InnoSetupRule(),
        new InstallShieldRule(),
        new NsisRule(),
    ];

    private readonly IReadOnlyList<IDetectionRule> _rules = rules ?? DefaultRules;

    public InstallerInfo Detect(InstalledApp app)
    {
        var candidates = new List<DetectionCandidate>();
        foreach (var rule in _rules)
        {
            try
            {
                if (rule.Evaluate(app, context) is { } candidate && candidate.Score > 0)
                    candidates.Add(candidate);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Rule {Rule} failed on {App}", rule.Type, app.Id);
            }
        }

        // Stable sort: keeps the rule priority for equal scores.
        var retained = candidates.Where(c => c.Score >= Threshold).OrderByDescending(c => c.Score).ToList();
        var uninstaller = context.GetUninstallerPath(app);

        if (retained.Count == 0)
        {
            return new InstallerInfo
            {
                Type = InstallerType.Unknown,
                Confidence = 0,
                Evidence = candidates.SelectMany(c => c.Evidence).ToList(),
                UninstallerPath = uninstaller,
            };
        }

        var best = retained[0];
        return new InstallerInfo
        {
            Type = best.Type,
            Confidence = best.Score,
            Evidence = best.Evidence,
            Secondary = retained.Skip(1).Select(c => new SecondaryMatch(c.Type, c.Score)).ToList(),
            UninstallerPath = uninstaller,
            // Secondary technologies may hold useful data (e.g. MSI product code of an InstallShield setup).
            Properties = retained
                .SelectMany(c => c.Properties)
                .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Runs the detection on every application and stores the result in <see cref="InstalledApp.Installer"/>.</summary>
    public void DetectAll(IEnumerable<InstalledApp> apps)
    {
        foreach (var app in apps)
            app.Installer = Detect(app);
    }
}
