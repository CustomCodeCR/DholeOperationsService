namespace Dhole.Operations.Domain.Services;

/// <summary>
/// Choosing a company without a JWT company claim is only safe when there is
/// exactly one persisted tenant. Empty databases may only be provisioned by a
/// privileged system administrator. Never infer a tenant when multiple exist.
/// </summary>
public static class OperationsCompanyBootstrapSelector
{
    public static Guid? ResolveExisting(Guid? claimedCompanyId, IEnumerable<Guid> knownCompanyIds)
    {
        if (claimedCompanyId.HasValue && claimedCompanyId != Guid.Empty)
            return claimedCompanyId;
        var known = knownCompanyIds.Where(x => x != Guid.Empty).Distinct().Take(2).ToArray();
        return known.Length == 1 ? known[0] : null;
    }

    public static Guid? ResolveForBootstrap(
        Guid? claimedCompanyId, bool isSystemAdministrator,
        IEnumerable<Guid> knownCompanyIds, Guid freshCompanyId)
    {
        if (claimedCompanyId.HasValue && claimedCompanyId != Guid.Empty)
            return claimedCompanyId;
        var known = knownCompanyIds.Where(x => x != Guid.Empty).Distinct().Take(2).ToArray();
        if (known.Length == 1) return known[0];
        if (known.Length > 1) return null;
        // This is a new, persistently identifiable Operations workspace, not a CRM customer.
        return isSystemAdministrator && freshCompanyId != Guid.Empty ? freshCompanyId : null;
    }
}
