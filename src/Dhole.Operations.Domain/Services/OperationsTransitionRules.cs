namespace Dhole.Operations.Domain.Services;

/// <summary>Pure, deterministic transition checklist. No state changes or implicit policy.</summary>
public static class OperationsTransitionRules
{
    public sealed record Rule(Guid Id, int Version, bool Approved, DateTimeOffset ValidFromUtc,
        DateTimeOffset? ValidToUtc, string Mode, Dictionary<string, string> Scope,
        Criterion[] Criteria);
    public sealed record Criterion(string Code, string Requirement);
    public sealed record Context(IReadOnlyDictionary<string, string> Dimensions, IReadOnlySet<string> Services);
    public sealed record Fact(string State, string Source, string Reason);
    public sealed record Check(string Code, string Requirement, string State, string Source, string Reason, bool Blocks);
    public sealed record Evaluation(string State, Guid? RuleId, int? RuleVersion, string? Mode,
        bool CanTransfer, IReadOnlyList<Check> Checks);

    private static readonly HashSet<string> Dimensions = new(StringComparer.Ordinal)
        { "fromStage", "toStage", "companyId", "teamId", "transport", "shipment", "originCountry",
          "destinationCountry", "polId", "poeId", "podId", "incoterm", "service", "legType" };
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
        { "ETD", "ETA", "ACTUAL_DEPARTURE", "HBL", "MBL", "BOOKING", "SHIPPING_ORDER",
          "PRICING_RESOLVED", "ORIGIN_COSTS_VALIDATED", "BOOKING_CONFIRMED", "VESSEL", "VOYAGE",
          "CONTAINERS", "SEALS", "CARGO", "PARTIES", "HAWB", "MAWB", "FLIGHT", "GCF" };

    public static bool IsValid(Rule r) =>
        r.Id != Guid.Empty && r.Version > 0 && r.ValidFromUtc != default &&
        (!r.ValidToUtc.HasValue || r.ValidToUtc > r.ValidFromUtc) &&
        r.Mode is "MANUAL" or "AUTOMATIC" && r.Scope is not null && r.Criteria is not null &&
        r.Scope.TryGetValue("fromStage", out var from) && from == "COORDINACION_ORIGEN" &&
        r.Scope.TryGetValue("toStage", out var to) && to == "TRANSITO" &&
        r.Scope.All(x => Dimensions.Contains(x.Key) && !string.IsNullOrWhiteSpace(x.Value) && x.Value.Length <= 100) &&
        r.Criteria.Length is > 0 and <= 50 &&
        r.Criteria.All(x => x is not null && Codes.Contains(x.Code) &&
            x.Requirement is "REQUIRED" or "BLOCKING" or "PENDING_ALLOWED" or "NOT_APPLICABLE") &&
        r.Criteria.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() == r.Criteria.Length;

    public static Evaluation Evaluate(IEnumerable<Rule> rules, Context context,
        IReadOnlyDictionary<string, Fact> facts, DateTimeOffset now)
    {
        var candidates = rules.ToArray();
        if (candidates.Any(r => r is null || !IsValid(r))) return Empty("INVALID_CONFIGURATION");
        var matches = candidates.Where(r => r.Approved && r.ValidFromUtc <= now &&
            (!r.ValidToUtc.HasValue || now < r.ValidToUtc.Value) &&
            r.Scope.All(x => x.Key == "service" ? context.Services.Contains(x.Value) :
                context.Dimensions.TryGetValue(x.Key, out var value) &&
                string.Equals(value, x.Value, StringComparison.Ordinal))).ToArray();
        if (matches.Length == 0) return Empty("NO_APPROVED_RULE");
        // A full rule is selected, not a merge of partial policies. Equally
        // specific overlapping rules are configuration conflicts, not tie-breaks by ID.
        var specificity = matches.Max(r => r.Scope.Count);
        var best = matches.Where(r => r.Scope.Count == specificity).ToArray();
        if (best.Length != 1) return Empty("RULE_CONFLICT");
        var rule = best[0];
        var checks = rule.Criteria.Select(c =>
        {
            if (c.Requirement == "NOT_APPLICABLE")
                return new Check(c.Code, c.Requirement, "NOT_APPLICABLE", "CONFIG", "NOT_APPLICABLE", false);
            var fact = facts.TryGetValue(c.Code, out var observed) && observed.State is "CONFIRMED" or "PENDING" or "UNVERIFIED"
                ? observed : new Fact("UNVERIFIED", "UNAVAILABLE", "SOURCE_NOT_IMPLEMENTED");
            var blocks = fact.State != "CONFIRMED" && c.Requirement is "REQUIRED" or "BLOCKING";
            return new Check(c.Code, c.Requirement, fact.State, fact.Source, fact.Reason, blocks);
        }).ToArray();
        var allowed = !checks.Any(c => c.Blocks);
        return new(allowed ? "READY" : "BLOCKED", rule.Id, rule.Version, rule.Mode, allowed, checks);
    }

    private static Evaluation Empty(string state) => new(state, null, null, null, false, []);
}
