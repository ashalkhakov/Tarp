using Shop.Core;

namespace Shop.Core.Tests;

[TestClass]
public sealed class OrdersTests
{
    [TestMethod]
    public void Ships()
    {
        Assert.IsTrue(Orders.CanShip(true, true, false));
        Assert.IsFalse(Orders.CanShip(true, false, false));
        Assert.IsTrue(Orders.CanShip(false, false, true));
    }

    [TestMethod]
    public void Ids()
    {
        Assert.IsTrue(Orders.IsValidId("AB-12"));
        Assert.IsFalse(Orders.IsValidId(null));
    }

    [TestMethod]
    public void Describes()
    {
        Assert.AreEqual("abc", Orders.Describe("abc"));
        Assert.AreEqual("null", Orders.Describe(null));
    }

    [TestMethod]
    public void Parses()
    {
        Assert.AreEqual(12, Orders.Parse("12"));
        Assert.AreEqual(-1, Orders.Parse("x"));
    }
}
