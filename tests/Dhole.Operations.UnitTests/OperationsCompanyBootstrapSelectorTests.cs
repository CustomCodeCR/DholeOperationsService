using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsCompanyBootstrapSelectorTests
{
    [TestMethod]
    public void HonorsClaimedCompanyWithoutAffectingAnotherCompany()
    {
        var tenant = Guid.NewGuid();
        Assert.AreEqual(tenant, OperationsCompanyBootstrapSelector.ResolveExisting(
            tenant, [Guid.NewGuid()]));
    }

    [TestMethod]
    public void ResolvesExistingSingleCompanyForAuthInternalUsers()
    {
        var company = Guid.NewGuid();
        Assert.AreEqual(company, OperationsCompanyBootstrapSelector.ResolveExisting(
            null, [company, company]));
    }

    [TestMethod]
    public void MultipleCompaniesRequireAnExplicitCompanyClaim()
    {
        var tenantOne = Guid.NewGuid();
        var tenantTwo = Guid.NewGuid();
        Assert.IsNull(OperationsCompanyBootstrapSelector.ResolveExisting(null,
            [tenantOne, tenantTwo]));
        Assert.IsNull(OperationsCompanyBootstrapSelector.ResolveForBootstrap(null, true,
            [tenantOne, tenantTwo], Guid.NewGuid()));
    }

    [TestMethod]
    public void OnlySystemAdministratorMayInitializeAnEmptyWorkspace()
    {
        var proposed = Guid.NewGuid();
        Assert.IsNull(OperationsCompanyBootstrapSelector.ResolveForBootstrap(
            null, false, [], proposed));
        Assert.AreEqual(proposed, OperationsCompanyBootstrapSelector.ResolveForBootstrap(
            null, true, [], proposed));
    }
}
