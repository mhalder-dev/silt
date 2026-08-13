namespace Silt.Core.Cleanup;

/// <summary>How much confidence a rule's targets deserve.</summary>
public enum SafetyTier
{
    /// <summary>Regenerated automatically, no user decision involved.</summary>
    AlwaysSafe,

    /// <summary>Safe, but with a cost worth naming — a slower next launch, a re-download.</summary>
    SafeWithCaveat,

    /// <summary>May contain something unique. Never suggested without review.</summary>
    RequiresReview,
}

/// <summary>
/// How deleted data comes back. Required for every rule — see Rule 0.
/// </summary>
/// <param name="Description">Plain-language answer to "what happens if I delete this?"</param>
/// <param name="Command">
/// The exact command that rebuilds it, where one exists. Null means it regenerates on its
/// own without user action, which <see cref="Description"/> must then explain.
/// </param>
public sealed record Regeneration(string Description, string? Command = null);

/// <summary>What a rule points at.</summary>
public enum RuleTargetKind
{
    /// <summary>Delete the entries inside the directory, keeping the directory itself.</summary>
    DirectoryContents,

    /// <summary>Delete files in the directory matching a glob, non-recursively.</summary>
    MatchingFiles,
}

/// <summary>
/// One location a rule targets.
/// </summary>
/// <param name="PathTemplate">
/// May contain <c>%ENVVAR%</c> and a literal <c>*</c> as a whole path segment — the latter
/// expands to every matching subdirectory, which is how per-profile Chrome caches and
/// per-product JetBrains caches are addressed without hardcoding them.
/// </param>
public sealed record RuleTarget(string PathTemplate, RuleTargetKind Kind, string? Glob = null);

/// <summary>
/// A cleanup rule: what to remove, why it is safe, and how it comes back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rule 0 is enforced here, in the constructor.</b> Nothing is deletable unless the rule
/// can say how it returns. This is not a convention or a review checklist item — a rule
/// without a regeneration story cannot be constructed, so it cannot exist to be executed.
/// </para>
/// <para>
/// Rules are data. The catalogue is a list of these objects rather than a set of methods,
/// so adding a cleaner is adding a row with a regeneration story attached, not writing
/// deletion code.
/// </para>
/// </remarks>
public sealed class CleanupRule
{
    public CleanupRule(
        string id,
        string displayName,
        string description,
        SafetyTier tier,
        IReadOnlyList<RuleTarget> targets,
        Regeneration regeneration,
        TimeSpan? minimumAge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0)
        {
            throw new ArgumentException("A rule with no targets cannot do anything.", nameof(targets));
        }

        // Rule 0.
        ArgumentNullException.ThrowIfNull(regeneration);
        if (string.IsNullOrWhiteSpace(regeneration.Description))
        {
            throw new ArgumentException(
                $"Rule '{id}' violates Rule 0: nothing is deleted unless the rule can name " +
                "how it comes back. Supply a regeneration description.",
                nameof(regeneration));
        }

        Id = id;
        DisplayName = displayName;
        Description = description;
        Tier = tier;
        Targets = targets;
        Regeneration = regeneration;
        MinimumAge = minimumAge;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public SafetyTier Tier { get; }
    public IReadOnlyList<RuleTarget> Targets { get; }
    public Regeneration Regeneration { get; }

    /// <summary>
    /// Only items untouched for at least this long are eligible.
    /// </summary>
    /// <remarks>
    /// This is a per-item test, not a per-directory one. Applying it to the directory would
    /// disqualify <c>%LOCALAPPDATA%\Temp</c> permanently, since something writes to it every
    /// few seconds — which would silently disable the single highest-value rule in the
    /// product.
    /// </remarks>
    public TimeSpan? MinimumAge { get; }

    public override string ToString() => $"{Id} ({DisplayName})";
}
