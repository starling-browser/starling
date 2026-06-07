// SPDX-License-Identifier: Apache-2.0
using System.Numerics;
using AwesomeAssertions;

namespace Starling.Scene.Tests;

[TestClass]
public class HitRegionSetTests
{
    [TestMethod]
    public void Topmost_region_wins_on_overlap()
    {
        var set = new HitRegionSet();
        set.Add(new PxRect(0, 0, 100, 100), hitId: 1);
        set.Add(new PxRect(10, 10, 20, 20), hitId: 2);

        set.HitTest(new Vector2(15, 15)).Should().Be(2);
    }

    [TestMethod]
    public void Falls_through_to_lower_region_outside_top()
    {
        var set = new HitRegionSet();
        set.Add(new PxRect(0, 0, 100, 100), hitId: 1);
        set.Add(new PxRect(10, 10, 20, 20), hitId: 2);

        set.HitTest(new Vector2(80, 80)).Should().Be(1);
    }

    [TestMethod]
    public void Miss_returns_null()
    {
        var set = new HitRegionSet();
        set.Add(new PxRect(0, 0, 10, 10), hitId: 1);

        set.HitTest(new Vector2(50, 50)).Should().BeNull();
    }
}
