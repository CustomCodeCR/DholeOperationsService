namespace Dhole.Operations.Domain.Services;

/// <summary>Pure validation of operational line amounts. Does not change a QUO,
/// convert currencies, or infer a confirmed value from missing information.</summary>
public static class OperationsCostReconciliation
{
    public const string Pending = "PENDING";
    public const string Match = "MATCH";
    public const string Changed = "CHANGED";
    public const string Additional = "ADDITIONAL";
    public const string TariffChanged = "TariffChanged";
    public const string AdditionalCost = "AdditionalCost";

    public sealed record Baseline(decimal? Amount, string? CurrencyCode, string? ChargeBasis, decimal? Quantity);
    public sealed record Input(string Status, decimal? ActualAmount, string? CurrencyCode,
        string? ChargeBasis, decimal? Quantity, string? Reason);
    public sealed record Result(string Status, decimal? QuotedAmount, decimal? ActualAmount,
        decimal? Difference, decimal? AdditionalAmount, string? RequestType);

    // Amounts are complete line totals using the same effective commercial
    // quantity and basis, never a unit price compared to a line total.
    public static bool TryEvaluate(Baseline? baseline, Input input, out Result? result, out string? error)
    {
        result = null;
        error = null;
        if (input.Status == Pending)
        {
            if (baseline is null || input.ActualAmount.HasValue)
                return Fail("OPERATIONS_COST_PENDING_INVALID", out error);
            result = new(Pending, baseline.Amount, null, null, null, null);
            return true;
        }

        if (input.ActualAmount is null or < 0m || !ValidContext(input.CurrencyCode, input.ChargeBasis, input.Quantity))
            return Fail("OPERATIONS_COST_AMOUNT_OR_CONTEXT_INVALID", out error);

        if (input.Status == Additional)
        {
            if (baseline is not null || input.ActualAmount.Value <= 0m || !ValidReason(input.Reason))
                return Fail("OPERATIONS_ADDITIONAL_COST_INVALID", out error);
            result = new(Additional, null, input.ActualAmount, null, input.ActualAmount, AdditionalCost);
            return true;
        }

        if (input.Status is not (Match or Changed) || baseline is null ||
            baseline.Amount is null or < 0m ||
            !ValidContext(baseline.CurrencyCode, baseline.ChargeBasis, baseline.Quantity))
            return Fail("OPERATIONS_COST_BASELINE_INVALID", out error);

        if (!SameCode(baseline.CurrencyCode, input.CurrencyCode) ||
            !SameCode(baseline.ChargeBasis, input.ChargeBasis) || baseline.Quantity != input.Quantity)
            return Fail("OPERATIONS_COST_COMPARISON_CONTEXT_MISMATCH", out error);

        var difference = input.ActualAmount.Value - baseline.Amount.Value;
        if (input.Status == Match && difference != 0m ||
            input.Status == Changed && (difference == 0m || !ValidReason(input.Reason)))
            return Fail("OPERATIONS_COST_STATUS_CONTRADICTION", out error);

        result = new(input.Status, baseline.Amount, input.ActualAmount, difference, null,
            input.Status == Changed ? TariffChanged : null);
        return true;
    }

    private static bool ValidContext(string? currency, string? basis, decimal? quantity) =>
        !string.IsNullOrWhiteSpace(currency) && currency.Length <= 60 &&
        !string.IsNullOrWhiteSpace(basis) && basis.Length <= 60 && quantity is > 0m;
    private static bool SameCode(string? first, string? second) =>
        string.Equals(first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool ValidReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) && reason.Trim().Length <= 2000;
    private static bool Fail(string code, out string? error) { error = code; return false; }
}
