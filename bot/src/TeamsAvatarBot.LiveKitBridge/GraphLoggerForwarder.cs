using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Communications.Common.Telemetry;

namespace TeamsAvatarBot.LiveKitBridge;

/// <summary>
/// Forwards Microsoft.Graph.Communications + Skype.Bots.Media internal log
/// events into the host's <see cref="ILogger{T}"/> (i.e. Serilog). Without
/// this, every diagnostic from the SDK — SDP failures, ICE problems, media
/// platform errors — vanishes and we're left guessing why a call dies.
///
/// Subscribe with <c>graphLogger.Subscribe(new GraphLoggerForwarder(...))</c>.
/// </summary>
internal sealed class GraphLoggerForwarder : IObserver<LogEvent>
{
    private readonly ILogger _log;

    public GraphLoggerForwarder(ILogger log) => _log = log;

    public void OnCompleted() { }
    public void OnError(Exception error) =>
        _log.LogError(error, "GraphLogger observer received OnError");

    public void OnNext(LogEvent value)
    {
        var level = value.Level switch
        {
            TraceLevel.Error    => LogLevel.Error,
            TraceLevel.Warning  => LogLevel.Warning,
            TraceLevel.Info     => LogLevel.Information,
            TraceLevel.Verbose  => LogLevel.Debug,
            _                   => LogLevel.Trace,
        };
        if (!_log.IsEnabled(level)) return;

        // Component + EventType give context (e.g., "MediaPlatform" /
        // "MediaSession") that's invaluable when triaging a media-stack
        // failure. Properties string holds the structured-key-value blob
        // the SDK attached to the event.
        _log.Log(
            level,
            "graphsdk {Component} {EventType} corr={Corr} msg={Msg} props={Props}",
            value.Component,
            value.EventType,
            value.CorrelationId,
            value.Message,
            value.PropertiesString);
    }
}
