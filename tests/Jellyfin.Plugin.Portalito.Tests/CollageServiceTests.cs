using System.Net;
using Jellyfin.Plugin.Portalito.Collage;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

public sealed class CollageServiceTests : IDisposable
{
    private readonly CollageService _service = new(Path.Combine(Path.GetTempPath(), "portalito-collage-service-" + Guid.NewGuid().ToString("N")));
    private readonly List<Uri> _fetched = new();

    public void Dispose()
    {
        if (Directory.Exists(_service.Directory))
        {
            Directory.Delete(_service.Directory, recursive: true);
        }
    }

    private Func<Uri, CancellationToken, Task<HttpResponseMessage>> Serve(HttpStatusCode status = HttpStatusCode.OK)
        => (uri, _) =>
        {
            lock (_fetched)
            {
                _fetched.Add(uri);
            }

            var poster = CollageRenderer.Render(Array.Empty<byte[]>(), "p", CollageFit.Cover);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(poster) });
        };

    [Fact]
    public async Task Renders_the_collage_to_its_file_once_and_reuses_it()
    {
        var posters = new[] { "https://cdn.test/a.jpg", "https://cdn.test/b.jpg" };

        var path = await _service.FileAsync("Acción", CollageFit.Cover, posters, Serve(), default);

        Assert.Equal(_service.PathFor("Acción", CollageFit.Cover, posters), path);
        Assert.StartsWith(_service.Directory, path, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, File.ReadAllBytes(path).Take(2).ToArray());
        Assert.Equal(2, _fetched.Count);

        Assert.Equal(path, await _service.FileAsync("Acción", CollageFit.Cover, posters, Serve(), default));
        Assert.Equal(2, _fetched.Count);
    }

    [Fact]
    public void Different_names_fits_or_images_are_different_files()
    {
        var a = new[] { "https://cdn.test/a.jpg" };
        var path = _service.PathFor("Acción", CollageFit.Cover, a);

        Assert.NotEqual(path, _service.PathFor("Drama", CollageFit.Cover, a));
        Assert.NotEqual(path, _service.PathFor("Acción", CollageFit.Contain, a));
        Assert.NotEqual(path, _service.PathFor("Acción", CollageFit.Cover, new[] { "https://cdn.test/b.jpg" }));
    }

    [Fact]
    public async Task Unreachable_or_non_http_images_still_give_a_name_tile()
    {
        var path = await _service.FileAsync("Colombia", CollageFit.Contain, new[] { "https://cdn.test/logo.png", "file:///etc/passwd" }, Serve(HttpStatusCode.NotFound), default);

        Assert.True(File.Exists(path));
        Assert.Single(_fetched);
    }

    [Fact]
    public async Task Prune_deletes_only_old_files_no_folder_uses()
    {
        var kept = await _service.FileAsync("A", CollageFit.Cover, Array.Empty<string>(), Serve(), default);
        var old = await _service.FileAsync("B", CollageFit.Cover, Array.Empty<string>(), Serve(), default);
        var fresh = await _service.FileAsync("C", CollageFit.Cover, Array.Empty<string>(), Serve(), default);
        File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));

        Assert.Equal(1, _service.Prune(new HashSet<string> { kept }, TimeSpan.FromDays(1)));

        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }
}
