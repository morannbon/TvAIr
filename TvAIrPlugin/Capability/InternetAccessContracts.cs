namespace TvAIrPlugin;

/// <summary>
/// Host-owned Internet permission boundary for plugin-owned transports such as WebSocket.
/// Plugins must declare UseInternetAccess and use CreateLinkedCancellation for every external session.
/// </summary>
public interface ITvAirInternetAccessApi
{
    TvAirInternetAccessStateDto GetState();
    CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken = default);
}

public sealed class TvAirInternetAccessStateDto
{
    public bool PluginDeclaredPermission { get; init; }
    public bool UserAllowed { get; init; }
    public bool NetworkUsageEnabled { get; init; }
    public bool Effective => PluginDeclaredPermission && UserAllowed && NetworkUsageEnabled;
}
