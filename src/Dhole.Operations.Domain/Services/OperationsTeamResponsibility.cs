namespace Dhole.Operations.Domain.Services;

public static class OperationsTeamResponsibility
{
    public const string Operator = "OPERATOR";
    public const string LegacyTeamSupervisor = "TEAM_SUPERVISOR";
    public const string LogisticsManager = "LOGISTICS_MANAGER";
    public const string OperationsHead = "OPERATIONS_HEAD";
    public const string OriginSupervisor = "ORIGIN_SUPERVISOR";

    public static bool IsLeadership(string? role) => role is LogisticsManager or OperationsHead or OriginSupervisor;
    public static bool IsValid(string? role, string department) => role switch
    {
        Operator or LegacyTeamSupervisor => true,
        LogisticsManager or OperationsHead => department is "APERTURAS" or "COORDINACION_ORIGEN" or "DOCUMENTACION" or "TRANSITO" or "PANAMA" or "COSTA_RICA",
        OriginSupervisor => department == "COORDINACION_ORIGEN",
        _ => false
    };

    // Older REST clients can continue editing operator/supervisor memberships.
    // They cannot silently demote a newly configured cross-team responsibility.
    public static string Resolve(string? requested, bool legacySupervisor, string? current = null)
        => requested ?? (IsLeadership(current) ? current! : legacySupervisor ? LegacyTeamSupervisor : Operator);

    public static bool IsSupervisor(string role) => role != Operator;
    public static bool Available(string role, bool requested) => !IsLeadership(role) && requested;
}
