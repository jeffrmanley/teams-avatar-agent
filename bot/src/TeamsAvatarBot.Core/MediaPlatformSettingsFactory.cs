using System.Net;
using Microsoft.Skype.Bots.Media;

namespace TeamsAvatarBot.Core;

/// <summary>
/// Builds the <see cref="MediaPlatformSettings"/> the Graph Communications
/// SDK hands to the underlying Skype.Bots.Media stack.
///
/// In the Microsoft sample bots this is hand-rolled in the worker role's
/// configuration file; we centralise it here so BotConfig stays the single
/// source of truth.
/// </summary>
public static class MediaPlatformSettingsFactory
{
    public static MediaPlatformSettings Build(BotConfig config, string certThumbprint) => new()
    {
        MediaPlatformInstanceSettings = new MediaPlatformInstanceSettings
        {
            // Same value for internal + public on a directly-addressable
            // host (Interserver VM with public IPv4). Behind a NAT or load
            // balancer they'd differ; PRD §3.2 ruled out shared LBs anyway.
            CertificateThumbprint   = certThumbprint,
            InstanceInternalPort    = config.MediaPort,
            InstancePublicPort      = config.MediaPort,
            InstancePublicIPAddress = IPAddress.Parse(config.InstancePublicIp),
            ServiceFqdn             = config.PublicFqdn,
        },
        ApplicationId = config.AppClientId
            ?? throw new InvalidOperationException(
                "MediaPlatformSettings requires AppClientId — Azure config must be populated"),
    };
}
