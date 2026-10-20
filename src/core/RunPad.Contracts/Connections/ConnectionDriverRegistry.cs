namespace RunPad.Contracts.Connections;

/// <summary>The connection drivers RunPad knows, by <see cref="IConnectionDriver.Id"/> (letter case aside).</summary>
public sealed class ConnectionDriverRegistry
{
    private readonly Dictionary<string, IConnectionDriver> _drivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IConnectionDriver> _order = [];

    /// <summary>In registration order.</summary>
    public IReadOnlyList<IConnectionDriver> Drivers => [.. _order];

    /// <summary>Registering a driver id again replaces that driver in place.</summary>
    public void Register(IConnectionDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (_drivers.TryGetValue(driver.Id, out var existing)) _order[_order.IndexOf(existing)] = driver;
        else _order.Add(driver);
        _drivers[driver.Id] = driver;
    }

    /// <summary>Null for a driver this RunPad doesn't have (a connection from a newer RunPad or a missing plugin).</summary>
    public IConnectionDriver? Find(string driverId) => _drivers.GetValueOrDefault(driverId);
}
