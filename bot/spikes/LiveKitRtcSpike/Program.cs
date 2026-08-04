// Phase 2.3 spike — verify Livekit.Rtc.Dotnet 0.1.2 can:
//   1. Connect to a LiveKit room as a participant from .NET 8 on Windows
//   2. Publish a real-time audio track at 16 kHz mono (Teams audio rate)
//   3. Subscribe to remote tracks
// Hard timebox: ~4 hours total. If anything sketchy, bail to Python sidecar.
//
// Usage (from repo root):
//   dotnet run --project bot/spikes/LiveKitRtcSpike

using DotNetEnv;
using LiveKit.Rtc;
using LkRoomOptions = LiveKit.Rtc.RoomOptions;
using LkRoom = LiveKit.Rtc.Room;
using Livekit.Server.Sdk.Dotnet;

const string RoomName = "csharp-rtc-spike";
const string Identity = "spike-bot";
const int    SampleRate = 16_000;   // matches Teams 16kHz mono PCM
const int    Channels   = 1;
const int    FrameSamples = 160;    // 10 ms at 16 kHz
const int    DurationSec = 5;
const double ToneHz = 440.0;

LoadEnvFromRepoRoot();
var url    = RequireEnv("LIVEKIT_URL");
var apiKey = RequireEnv("LIVEKIT_API_KEY");
var secret = RequireEnv("LIVEKIT_API_SECRET");

Log("=== LiveKit C# RTC spike ===");
Log($"url={url} room={RoomName} identity={Identity}");

// Step 1: mint a JWT with publish + subscribe grants
var jwt = new AccessToken(apiKey, secret)
    .WithIdentity(Identity)
    .WithName("C# spike")
    .WithGrants(new VideoGrants
    {
        RoomJoin   = true,
        Room       = RoomName,
        CanPublish = true,
        CanSubscribe = true,
    })
    .WithTtl(TimeSpan.FromMinutes(15))
    .ToJwt();
Log("[1/5] minted token (len=" + jwt.Length + ")");

// Step 2: connect to the room
var room = new LkRoom();
room.ParticipantConnected     += (_, p)  => Log($"   evt: participant connected {p.Identity}");
room.ParticipantDisconnected  += (_, p)  => Log($"   evt: participant disconnected {p.Identity}");
room.TrackSubscribed          += (_, e)  => Log($"   evt: track subscribed sid={e.Publication.Sid} kind={e.Track.Kind}");
room.Disconnected             += (_, _)  => Log("   evt: room disconnected");

await room.ConnectAsync(url, jwt, new LkRoomOptions { AutoSubscribe = true });
Log($"[2/5] connected. local sid={room.LocalParticipant?.Sid}");

// Step 3: publish a 16kHz mono audio track
var audioSource = new AudioSource(SampleRate, Channels);
var audioTrack  = LocalAudioTrack.Create("spike-tone", audioSource);
var publication = await room.LocalParticipant!.PublishTrackAsync(audioTrack);
Log($"[3/5] published audio track sid={publication.Sid}");

// Step 4: stream a 440Hz sine wave for DurationSec seconds
var totalFrames = (DurationSec * SampleRate) / FrameSamples;   // 5s * 16000 / 160 = 500 frames
var pcm = new short[FrameSamples];
var phase = 0.0;
var phaseStep = 2 * Math.PI * ToneHz / SampleRate;
var sw = System.Diagnostics.Stopwatch.StartNew();
for (var i = 0; i < totalFrames; i++)
{
    for (var s = 0; s < FrameSamples; s++)
    {
        pcm[s] = (short)(Math.Sin(phase) * 8000); // ~25% of int16 max — comfortable level
        phase += phaseStep;
        if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
    }
    var frame = new AudioFrame(pcm, SampleRate, Channels, FrameSamples);
    await audioSource.CaptureFrameAsync(frame);

    // Pace at real time so we don't burst-publish 5 seconds of audio in 50 ms
    var targetMs = (i + 1) * 10;   // 10 ms per frame
    var ahead = targetMs - sw.ElapsedMilliseconds;
    if (ahead > 0) await Task.Delay((int)ahead);
}
sw.Stop();
Log($"[4/5] streamed {totalFrames} frames ({DurationSec}s) in {sw.ElapsedMilliseconds} ms");

// Step 5: disconnect cleanly
await room.DisconnectAsync();
Log("[5/5] disconnected cleanly");
Log("=== SUCCESS — Livekit.Rtc.Dotnet works on this host ===");

return 0;

static void Log(string msg)
    => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {msg}");

static string RequireEnv(string name)
    => Environment.GetEnvironmentVariable(name)
       ?? throw new InvalidOperationException($"missing env var: {name}");

static void LoadEnvFromRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var env = Path.Combine(dir.FullName, ".env");
        if (File.Exists(env)) { Env.Load(env); return; }
        dir = dir.Parent;
    }
    throw new FileNotFoundException(".env not found walking up from " + AppContext.BaseDirectory);
}
