// ============================================================================
// INT VoiceToText — SignalR hub (server → UI realtime push)
// ============================================================================

using Microsoft.AspNetCore.SignalR;

namespace INTVoiceToText.Hubs;

/// <summary>
/// Realtime channel between the C# backend and the React frontend.
/// Server-pushed events:
///   "state"  { status: "idle" | "recording" | "transcribing", t }
///   "text"   { text, durationMs, pasted }
///   "error"  { message }
/// </summary>
public class VoiceHub : Hub
{
    // Intentionally minimal: the engine pushes via IHubContext<VoiceHub>.Clients.All.
}
