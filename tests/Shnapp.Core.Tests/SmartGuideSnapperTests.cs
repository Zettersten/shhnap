using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Shnapp.Core.Tests;

[TestClass]
public sealed class SmartGuideSnapperTests
{
    [TestMethod]
    public void FindsClosestEdgeOrCenterEvenWhenTheMatchingFeaturesDiffer()
    {
        // A moving element's right edge aligns to a saved element's center.
        AxisSnap snap = SmartGuideSnapper.FindClosest(100, 125, 150, [48, 153, 260], 7);

        Assert.AreEqual(3, snap.Correction);
        Assert.AreEqual(153, snap.Guide);
    }

    [TestMethod]
    public void UsesTheNearestGuideAndDoesNotSnapOutsideScreenSpaceTolerance()
    {
        AxisSnap near = SmartGuideSnapper.FindClosest(20, 30, 40, [44, 38], 6);
        AxisSnap far = SmartGuideSnapper.FindClosest(20, 30, 40, [47], 6);

        Assert.AreEqual(-2, near.Correction);
        Assert.AreEqual(38, near.Guide);
        Assert.IsNull(far.Guide);
        Assert.AreEqual(0, far.Correction);
    }
}
