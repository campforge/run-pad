using RunPad.Contracts.Models;

namespace RunPad.Contracts;

public interface ISdkProvider
{
    IReadOnlyList<DotnetSdk> Get();
}
