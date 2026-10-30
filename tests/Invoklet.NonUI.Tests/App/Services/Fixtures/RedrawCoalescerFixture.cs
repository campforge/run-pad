using Invoklet.Services;

namespace Invoklet.NonUI.Tests.App.Services.Fixtures;

// The three delegates are the whole of the class's surroundings: the fixture owns them, runs the posted work on demand and
// records what was redrawn, so a test decides when "later" comes.
internal sealed class RedrawCoalescerFixture
{
    public Queue<Action> Posted { get; } = new();

    public List<(int Start, int Length)> Redraws { get; } = [];

    public int? TextLength { get; set; } = 100;

    public RedrawCoalescer CreateSut() => new(Posted.Enqueue, () => TextLength, (start, length) => Redraws.Add((start, length)));

    public void RunPosted()
    {
        while (Posted.Count > 0) Posted.Dequeue()();
    }
}
