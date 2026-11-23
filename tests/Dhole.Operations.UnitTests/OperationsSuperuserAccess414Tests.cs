using System.Security.Claims;
using Dhole.Operations.Api.Security;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsSuperuserAccess414Tests
{
    [TestMethod]
    public void SuperusuarioInRawJwtRoleClaimHasReadAllScopes()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("roles", "SuperUsuario")
        ], "test"));

        Assert.IsTrue(OperationsCaseAccess.IsSystemAdministrator(principal));
        Assert.IsTrue(OperationsCaseAccess.HasScope(principal, "operations.case.read"));
        Assert.IsTrue(OperationsCaseAccess.HasScope(principal, "operations.origin.read"));
    }

    [TestMethod]
    public void NormalUserDoesNotInheritAdministrativeScopes()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("roles", "Operador Logístico"),
            new Claim("scope", "operations.case.read")
        ], "test"));

        Assert.IsFalse(OperationsCaseAccess.IsSystemAdministrator(principal));
        Assert.IsTrue(OperationsCaseAccess.HasScope(principal, "operations.case.read"));
        Assert.IsFalse(OperationsCaseAccess.HasScope(principal, "operations.finance.manage"));
    }
}
