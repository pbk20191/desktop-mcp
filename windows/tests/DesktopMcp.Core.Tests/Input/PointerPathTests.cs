using DesktopMcp.Core.Input;
using DesktopMcp.Core.Snapshots;

namespace DesktopMcp.Core.Tests.Input;

public class PointerPathTests
{
    // desktop/service.py:981-984: steps = max(1, min(80, max(|dx|, |dy|) // 20)), cx + (x - cx) * step // steps (floor).
    [Fact]
    public void MoveStepsFloorTowardsNegativeInfinity() =>
        Assert.Equal([new ScreenPoint(-21, 0), new ScreenPoint(-41, 0)], PointerPath.MoveSteps(new(0, 0), new(-41, 0)));

    [Fact]
    public void MoveStepsAreCappedAt80() => Assert.Equal(80, PointerPath.MoveSteps(new(0, 0), new(5000, 10)).Count);

    [Fact]
    public void ShortMovesTakeOneStepToTheTarget() =>
        Assert.Equal([new ScreenPoint(7, -3)], PointerPath.MoveSteps(new(0, 0), new(7, -3)));

    // desktop/service.py:958-971: duration steps = max(1, min(200, ceil(d / 0.01))), interval d / steps, round half to even.
    [Fact]
    public void DragWithADurationUsesTenMillisecondSteps()
    {
        var (points, interval) = PointerPath.DragSteps(new(0, 0), new(5, 0), 0.04);
        Assert.Equal([new ScreenPoint(1, 0), new ScreenPoint(2, 0), new ScreenPoint(4, 0), new ScreenPoint(5, 0)], points); // 1.25, 2.5, 3.75, 5
        Assert.Equal(TimeSpan.FromMilliseconds(10), interval);
    }

    [Fact]
    public void DragWithoutADurationUsesTheMoveStepCountAndTenMilliseconds()
    {
        var (points, interval) = PointerPath.DragSteps(new(0, 0), new(100, 0), null);
        Assert.Equal(5, points.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(10), interval);
    }

    [Fact]
    public void DragDurationStepsAreCappedAt200AndZeroTakesOneStep()
    {
        Assert.Equal(200, PointerPath.DragSteps(new(0, 0), new(5, 0), 10).Points.Count);
        var (points, interval) = PointerPath.DragSteps(new(0, 0), new(5, 0), 0);
        Assert.Equal([new ScreenPoint(5, 0)], points);
        Assert.Equal(TimeSpan.Zero, interval);
    }

    [Fact]
    public void NormaliseCoversTheVirtualDesktopWithNegativeOrigin()
    {
        var screen = new ScreenRect(-1920, 0, 2560, 1600); // width 4480
        Assert.Equal((0, 0), MouseCoordinates.Normalize(new(-1920, 0), screen));
        Assert.Equal((65535, 65535), MouseCoordinates.Normalize(new(2559, 1599), screen));
        Assert.Equal((28093, 0), MouseCoordinates.Normalize(new(0, 0), screen)); // round(1920 * 65535 / 4479)
    }

    [Fact]
    public void NormaliseRoundsHalfToEven() =>
        Assert.Equal((6554, 13107), MouseCoordinates.Normalize(new(100, 200), new ScreenRect(0, 0, 1001, 1001)));

    [Fact]
    public void NormaliseHalfWayCasesGoToTheEvenNeighbour() =>
        Assert.Equal((19660, 0), MouseCoordinates.Normalize(new(300, 0), new ScreenRect(0, 0, 1001, 1001))); // 19660.5, half-away would give 19661

    // service.py:958-971 rounds only the offset: cx + round((x - cx) * step / steps).
    [Fact]
    public void DragRoundsTheOffsetNotTheSum()
    {
        Assert.Equal([new ScreenPoint(2, 0), new ScreenPoint(3, 0), new ScreenPoint(5, 0), new ScreenPoint(6, 0)],
            PointerPath.DragSteps(new(1, 0), new(6, 0), 0.04).Points);
        Assert.Equal([new ScreenPoint(0, 0), new ScreenPoint(1, 0), new ScreenPoint(3, 0), new ScreenPoint(4, 0)],
            PointerPath.DragSteps(new(-1, 0), new(4, 0), 0.04).Points);
    }

    [Fact]
    public void OutsideMessageIsUpstreams() =>
        Assert.Equal("Coordinates (3000,5) are outside the desktop bounds x=-1920..2559, y=0..1599",
            MouseCoordinates.OutsideMessage(new(3000, 5), new ScreenRect(-1920, 0, 2560, 1600)));

    [Fact]
    public void InsidePointsHaveNoMessage() =>
        Assert.Null(MouseCoordinates.OutsideMessage(new(2559, 1599), new ScreenRect(-1920, 0, 2560, 1600)));
}
