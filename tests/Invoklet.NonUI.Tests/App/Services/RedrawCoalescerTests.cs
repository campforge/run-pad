using Invoklet.NonUI.Tests.App.Services.Fixtures;
using Invoklet.Services;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class RedrawCoalescerTests
{
    [Fact]
    public void Request_NothingChanged_PostsNothing()
    {
        // Arrange
        var fixture = new RedrawCoalescerFixture();
        var sut = fixture.CreateSut();

        // Act
        sut.Request(null);
        fixture.RunPosted();

        // Assert
        Assert.Empty(fixture.Redraws);
        Assert.Equal(0, sut.RedrawsRun);
    }

    [Fact]
    public void Request_SeveralSpansBeforeTheRedrawRuns_RedrawsTheirUnionOnce()
    {
        // Arrange
        var fixture = new RedrawCoalescerFixture();
        var sut = fixture.CreateSut();

        // Act
        sut.Request((10, 20));
        sut.Request((5, 12));
        sut.Request((30, 40));
        fixture.RunPosted();

        // Assert
        Assert.Equal([(5, 35)], fixture.Redraws);
        Assert.Equal(1, sut.RedrawsRun);
    }

    [Fact]
    public void Request_AfterTheRedrawRan_StartsANewOneWithoutTheOldSpan()
    {
        // Arrange
        var fixture = new RedrawCoalescerFixture();
        var sut = fixture.CreateSut();
        sut.Request((10, 20));
        fixture.RunPosted();

        // Act
        sut.Request((50, 60));
        fixture.RunPosted();

        // Assert
        Assert.Equal([(10, 10), (50, 10)], fixture.Redraws);
    }

    [Fact]
    public void Request_ASpanPastTheEndOfTheText_IsClampedToIt()
    {
        // Arrange
        var fixture = new RedrawCoalescerFixture { TextLength = 30 };
        var sut = fixture.CreateSut();

        // Act
        sut.Request((25, 90));
        fixture.RunPosted();

        // Assert
        Assert.Equal([(25, 5)], fixture.Redraws);
    }

    [Fact]
    public void Request_AZeroLengthSpan_RedrawsAtLeastOneCharacter()
    {
        // Arrange
        var fixture = new RedrawCoalescerFixture();
        var sut = fixture.CreateSut();

        // Act
        sut.Request((7, 7));
        fixture.RunPosted();

        // Assert
        Assert.Equal([(7, 1)], fixture.Redraws);
    }

    [Fact]
    public void Request_NoTextToDrawWhenItRuns_RedrawsNothing()
    {
        // Arrange
        var fixture = new RedrawCoalescerFixture();
        var sut = fixture.CreateSut();
        sut.Request((10, 20));
        fixture.TextLength = null;

        // Act
        fixture.RunPosted();

        // Assert
        Assert.Empty(fixture.Redraws);
        Assert.Equal(0, sut.RedrawsRun);
    }

    [Fact]
    public void Request_MadeWhileTheRedrawRuns_IsNotLost()
    {
        // Arrange
        var posted = new Queue<Action>();
        var redraws = new List<(int Start, int Length)>();
        RedrawCoalescer? sut = null;
        sut = new RedrawCoalescer(posted.Enqueue, () => 100, (start, length) =>
        {
            redraws.Add((start, length));
            if (redraws.Count == 1) sut!.Request((40, 50));
        });
        sut.Request((10, 20));

        // Act
        while (posted.Count > 0) posted.Dequeue()();

        // Assert
        Assert.Equal([(10, 10), (40, 10)], redraws);
    }
}
