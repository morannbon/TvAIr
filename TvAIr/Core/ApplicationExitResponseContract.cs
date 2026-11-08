using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace TvAIr.Core;

/// <summary>
/// Explicit exit/restart requests terminate the Host only after their HTTP response
/// has finished. Response completion, not an assumed millisecond delay, is the signal.
/// </summary>
internal static class ApplicationExitResponseContract
{
    public static void RegisterStopAfterResponse(HttpResponse response, IHostApplicationLifetime lifetime)
    {
        response.OnCompleted(() =>
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        });
    }
}
