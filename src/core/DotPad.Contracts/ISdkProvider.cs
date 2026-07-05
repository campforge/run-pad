using DotPad.Contracts.Models;

namespace DotPad.Contracts;

public interface ISdkProvider
{
    IReadOnlyList<DotnetSdk> Get();
}
