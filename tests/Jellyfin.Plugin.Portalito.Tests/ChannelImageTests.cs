using System.Buffers.Binary;
using Jellyfin.Plugin.Portalito.Channels;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public class ChannelImageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("portalito-img-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private PortalitoVodChannel Channel(IPortalitoServicesProvider provider)
        => new(provider, new FakeVodTrackProbe(), new SubtitleFileCache(_dir, new HttpClient()), new Collage.CollageService(_dir), NullLogger<PortalitoVodChannel>.Instance);

    private sealed class ImageProvider(string? url) : IPortalitoServicesProvider
    {
        private readonly WiringHarness _h = new();

        public PortalitoServices Get() => _h.Get() with { ChannelImageUrl = url };
    }

    /// <summary>The width and height from a PNG's IHDR chunk; fails if the bytes aren't a PNG.</summary>
    private static (int Width, int Height) PngSize(Stream stream)
    {
        var header = new byte[24];
        stream.ReadExactly(header);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header[..8]);
        return (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)));
    }

    [Fact]
    public void The_channel_offers_primary_thumb_and_backdrop_images()
        => Assert.Equal(new[] { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop }, Channel(new UnconfiguredProvider()).GetSupportedChannelImages());

    [Fact]
    public async Task Without_a_configured_portal_the_built_in_logo_and_banner_are_served()
    {
        // The tile must never be blank, even before the portal is set up.
        var channel = Channel(new UnconfiguredProvider());

        var primary = await channel.GetChannelImage(ImageType.Primary, CancellationToken.None);
        Assert.True(primary.HasImage);
        Assert.Equal(MediaBrowser.Model.Drawing.ImageFormat.Png, primary.Format);
        Assert.Equal((512, 512), PngSize(primary.Stream));

        var thumb = await channel.GetChannelImage(ImageType.Thumb, CancellationToken.None);
        Assert.Equal((1280, 720), PngSize(thumb.Stream));
    }

    [Fact]
    public async Task A_configured_image_url_replaces_the_built_in_images()
    {
        var channel = Channel(new ImageProvider("https://example.test/my-logo.png"));

        foreach (var type in new[] { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop })
        {
            var image = await channel.GetChannelImage(type, CancellationToken.None);
            Assert.True(image.HasImage);
            Assert.Equal("https://example.test/my-logo.png", image.Path);
            Assert.Equal(MediaBrowser.Model.MediaInfo.MediaProtocol.Http, image.Protocol);
            Assert.Null(image.Stream);
        }
    }

    [Fact]
    public async Task Without_an_image_url_the_built_in_logo_is_used_even_when_configured()
    {
        var image = await Channel(new ImageProvider(null)).GetChannelImage(ImageType.Primary, CancellationToken.None);

        Assert.Equal((512, 512), PngSize(image.Stream));
    }
}
