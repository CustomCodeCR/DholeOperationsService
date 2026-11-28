using Dhole.Operations.Domain.Services;

namespace Dhole.Operations.UnitTests;

[TestClass]
public sealed class OperationsDocumentPolicyTests
{
    [TestMethod]
    public void TransversalReadRequiresVerifiedOwnerSameCaseAndOperationalType()
    {
        var id = Guid.NewGuid();
        var stage = Guid.NewGuid();
        Assert.IsTrue(OperationsDocumentPolicy.CanReadTransversally(id, id, stage, id, "OPERATIONAL", "HBL", "COORDINACION_ORIGEN"));
        Assert.IsFalse(OperationsDocumentPolicy.CanReadTransversally(id, Guid.NewGuid(), stage, id, "OPERATIONAL", "HBL", "COORDINACION_ORIGEN"));
        Assert.IsFalse(OperationsDocumentPolicy.CanReadTransversally(id, id, stage, Guid.NewGuid(), "OPERATIONAL", "HBL", "COORDINACION_ORIGEN"));
        Assert.IsFalse(OperationsDocumentPolicy.CanReadTransversally(id, id, null, id, "OPERATIONAL", "HBL", "COORDINACION_ORIGEN"));
        Assert.IsFalse(OperationsDocumentPolicy.CanReadTransversally(id, id, stage, id, null, "HBL", "COORDINACION_ORIGEN"));
        Assert.IsFalse(OperationsDocumentPolicy.CanReadTransversally(id, id, stage, id, "OPERATIONAL", "INVOICE", "COORDINACION_ORIGEN"));
        Assert.IsFalse(OperationsDocumentPolicy.CanReadTransversally(id, id, stage, id, "OPERATIONAL", "HBL", "FINANZAS"));
    }

    [TestMethod]
    public void RegisterRejectsClosedCaseAndWrongStage()
    {
        var id = Guid.NewGuid();
        Assert.IsTrue(OperationsDocumentPolicy.CanRegister(id, id, "COORDINACION_ORIGEN", "READY", "APERTURA_VALIDADA", "HBL"));
        Assert.IsFalse(OperationsDocumentPolicy.CanRegister(id, Guid.NewGuid(), "COORDINACION_ORIGEN", "READY", "APERTURA_VALIDADA", "HBL"));
        Assert.IsFalse(OperationsDocumentPolicy.CanRegister(id, id, "COORDINACION_ORIGEN", "COMPLETED", "APERTURA_VALIDADA", "HBL"));
        Assert.IsFalse(OperationsDocumentPolicy.CanRegister(id, id, "COORDINACION_ORIGEN", "READY", "CANCELLED", "HBL"));
        Assert.IsNull(OperationsDocumentPolicy.NormalizeType("quotation"));
        Assert.AreEqual("BOOKING", OperationsDocumentPolicy.NormalizeType(" booking "));
    }
}
