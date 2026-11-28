using Dhole.Operations.Domain.Models;
using static Dhole.Operations.Domain.Services.OperationsTransitionRules;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsTransitionRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
    private static Context ContextForFcl() => new(new Dictionary<string, string>
    {
        ["fromStage"] = "COORDINACION_ORIGEN", ["toStage"] = "TRANSITO", ["shipment"] = "FCL"
    }, new HashSet<string> { "FREIGHT" });
    private static Rule RuleFor(string requirement = "BLOCKING", bool specific = false) =>
        new(Guid.NewGuid(), 1, true, Now.AddDays(-1), Now.AddDays(1), "MANUAL",
            specific ? new() { ["fromStage"] = "COORDINACION_ORIGEN", ["toStage"] = "TRANSITO", ["shipment"] = "FCL" }
                : new() { ["fromStage"] = "COORDINACION_ORIGEN", ["toStage"] = "TRANSITO" },
            [new("PRICING_RESOLVED", requirement)]);
    private static Dictionary<string, Fact> PendingPricing() => new()
    {
        ["PRICING_RESOLVED"] = new("PENDING", "PRICING_REVIEW", "REGISTERED_REVIEW_OUTSTANDING")
    };

    [TestMethod]
    public void PendingCommercialReviewOnlyBlocksWhenConfigured()
    {
        var facts = PendingPricing();
        var blocked = Evaluate([RuleFor()], ContextForFcl(), facts, Now);
        Assert.IsFalse(blocked.CanTransfer);
        Assert.AreEqual("BLOCKED", blocked.State);
        Assert.IsTrue(blocked.Checks.Single().Blocks);
        var permitted = Evaluate([RuleFor("PENDING_ALLOWED")], ContextForFcl(), facts, Now);
        Assert.IsTrue(permitted.CanTransfer);
        Assert.AreEqual("PENDING", permitted.Checks.Single().State);
        Assert.IsFalse(permitted.Checks.Single().Blocks);
        Assert.AreEqual("PENDING", facts["PRICING_RESOLVED"].State);
    }

    [TestMethod]
    public void SpecificityWinsAndEqualSpecificityConflicts()
    {
        var general = RuleFor();
        var specific = RuleFor("PENDING_ALLOWED", true);
        var result = Evaluate([general, specific], ContextForFcl(), PendingPricing(), Now);
        Assert.AreEqual(specific.Id, result.RuleId);
        Assert.IsTrue(result.CanTransfer);
        var conflict = Evaluate([specific, RuleFor("BLOCKING", true)], ContextForFcl(), PendingPricing(), Now);
        Assert.AreEqual("RULE_CONFLICT", conflict.State);
        Assert.IsFalse(conflict.CanTransfer);
    }

    [TestMethod]
    public void ExpiredDraftFutureAndWrongScopeCannotAuthorize()
    {
        var general = RuleFor();
        var wrong = RuleFor() with { Scope = new() {
            ["fromStage"] = "COORDINACION_ORIGEN", ["toStage"] = "TRANSITO", ["shipment"] = "LCL"
        } };
        var future = general with { Id = Guid.NewGuid(), ValidFromUtc = Now.AddDays(1), ValidToUtc = Now.AddDays(2) };
        var result = Evaluate([general with { Approved = false }, general with { Id = Guid.NewGuid(), ValidToUtc = Now },
            future, wrong], ContextForFcl(), PendingPricing(), Now);
        Assert.AreEqual("NO_APPROVED_RULE", result.State);
        Assert.IsFalse(result.CanTransfer);
    }

    [TestMethod]
    public void MissingActualDepartureNeverInferredFromEtdAndUnknownIsExplicit()
    {
        var rule = RuleFor() with { Criteria = [new("ACTUAL_DEPARTURE", "REQUIRED"), new("GCF", "NOT_APPLICABLE"),
            new("ORIGIN_COSTS_VALIDATED", "PENDING_ALLOWED")] };
        var result = Evaluate([rule], ContextForFcl(), new Dictionary<string, Fact> {
            ["ETD"] = new("CONFIRMED", "OPERATIONS_PLANNING", "ESTIMATE_RECORDED")
        }, Now);
        Assert.IsFalse(result.CanTransfer);
        Assert.AreEqual("UNVERIFIED", result.Checks[0].State);
        Assert.AreEqual("NOT_APPLICABLE", result.Checks[1].State);
        Assert.IsFalse(result.Checks[2].Blocks);
    }

    [TestMethod]
    public void InvalidCriterionOrDuplicateIsRejected()
    {
        var rule = RuleFor();
        foreach (var invalid in new[] {
            rule with { Criteria = [new("UNRECOGNIZED", "BLOCKING")] },
            rule with { Criteria = [new("ETD", "MAYBE")] },
            rule with { Criteria = [new("ETD", "REQUIRED"), new("ETD", "PENDING_ALLOWED")] }
        })
            Assert.AreEqual("INVALID_CONFIGURATION", Evaluate([invalid], ContextForFcl(), PendingPricing(), Now).State);
    }

    [TestMethod]
    public void FingerprintChangesEvenIfCatalogVersionIsImproperlyReused()
    {
        var rule = RuleFor();
        var before = Evaluate([rule], ContextForFcl(), PendingPricing(), Now);
        var after = Evaluate([rule with { Criteria = [new("PRICING_RESOLVED", "PENDING_ALLOWED")] }],
            ContextForFcl(), PendingPricing(), Now);
        Assert.AreEqual(rule.Id, before.RuleId);
        Assert.AreNotEqual(before.RuleFingerprint, after.RuleFingerprint);
        Assert.IsNotNull(before.RuleSnapshotJson);
        StringAssert.Contains(before.RuleSnapshotJson, "BLOCKING");
    }

    [TestMethod]
    public void TransferReplayMustPreserveImmutableIdentity()
    {
        var receipt = new OperationsOriginTransfer {
            CaseId = Guid.NewGuid(), ActorUserId = Guid.NewGuid(), ExpectedCaseVersion = 4,
            ExpectedStageVersion = 2, RuleId = Guid.NewGuid(), RuleVersion = 1, RuleFingerprint = new string('A', 64)
        };
        Assert.IsTrue(receipt.Matches(receipt.CaseId, receipt.ActorUserId, 4, 2, receipt.RuleId, 1, receipt.RuleFingerprint));
        Assert.IsFalse(receipt.Matches(Guid.NewGuid(), receipt.ActorUserId, 4, 2, receipt.RuleId, 1, receipt.RuleFingerprint));
        Assert.IsFalse(receipt.Matches(receipt.CaseId, receipt.ActorUserId, 5, 2, receipt.RuleId, 1, receipt.RuleFingerprint));
        Assert.IsFalse(receipt.Matches(receipt.CaseId, receipt.ActorUserId, 4, 2, receipt.RuleId, 1, new string('B', 64)));
    }
}
