using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Portalito.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>
/// The config page is plain HTML/JS that Jellyfin serves from an embedded resource; nothing in the build checks that it
/// lines up with <see cref="PluginConfiguration"/>. These tests catch the mismatches that would otherwise only show up as
/// an empty or non-saving page in a running Jellyfin.
/// </summary>
public class ConfigPageTests
{
    // Set programmatically (generated on first use), deliberately not editable on the page.
    private static readonly string[] NotOnThePage = { nameof(PluginConfiguration.ProxySigningSecret) };

    private static readonly Lazy<string> Html = new(() =>
    {
        var path = PluginWithoutHost().GetPages().Single().EmbeddedResourcePath;
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(path)
            ?? throw new InvalidOperationException($"Embedded resource '{path}' not found. Resources: {string.Join(", ", typeof(Plugin).Assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    // GetPages/Id only read constants; the constructor just needs Jellyfin's services.
    private static Plugin PluginWithoutHost() => (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

    private static string[] InputIds()
        => Regex.Matches(Html.Value, @"<input\s+id=""(\w+)""").Select(m => m.Groups[1].Value).ToArray();

    private static string[] EditableProperties()
        => typeof(PluginConfiguration)
            .GetProperties()
            .Where(p => p.DeclaringType == typeof(PluginConfiguration) && p.CanWrite)
            .Select(p => p.Name)
            .Except(NotOnThePage)
            .ToArray();

    [Fact]
    public void The_page_resource_named_by_GetPages_is_embedded_in_the_assembly()
    {
        var plugin = PluginWithoutHost();
        var page = Assert.Single(plugin.GetPages());

        Assert.NotNull(typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath));
        Assert.Equal("Portalito", page.Name);
    }

    [Fact]
    public void The_page_uses_the_plugin_id()
    {
        var plugin = PluginWithoutHost();

        Assert.Contains($"var pluginId = '{plugin.Id}';", Html.Value);
    }

    [Fact]
    public void Every_input_on_the_page_is_a_configuration_property()
    {
        var properties = typeof(PluginConfiguration).GetProperties().Select(p => p.Name).ToHashSet();

        Assert.NotEmpty(InputIds());
        Assert.All(InputIds(), id => Assert.Contains(id, properties));
    }

    [Fact]
    public void Every_editable_configuration_property_has_an_input_on_the_page()
        => Assert.Empty(EditableProperties().Except(InputIds()));

    [Fact]
    public void Text_fields_loaded_and_saved_by_the_script_are_exactly_the_text_inputs()
    {
        var list = Regex.Match(Html.Value, @"var textFields = \[(.*?)\];").Groups[1].Value;
        var scripted = Regex.Matches(list, @"'(\w+)'").Select(m => m.Groups[1].Value).ToArray();
        var textInputs = Regex.Matches(Html.Value, @"<input\s+id=""(\w+)""[^>]*type=""(?:text|password)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.Equal(textInputs.OrderBy(x => x), scripted.OrderBy(x => x));
    }

    [Fact]
    public void The_checkbox_input_is_a_boolean_property_handled_by_the_script()
    {
        var checkboxes = Regex.Matches(Html.Value, @"<input\s+id=""(\w+)""[^>]*type=""checkbox""").Select(m => m.Groups[1].Value).ToArray();

        var id = Assert.Single(checkboxes);
        Assert.Equal(typeof(bool), typeof(PluginConfiguration).GetProperty(id)!.PropertyType);
        Assert.Contains($"config.{id} = ", Html.Value);
        Assert.Contains($"'#{id}').checked = !!config.{id}", Html.Value);
    }

    [Fact]
    public void Secrets_are_masked_inputs()
    {
        foreach (var id in new[] { nameof(PluginConfiguration.TripleDesKeyHex), nameof(PluginConfiguration.DeviceSn), nameof(PluginConfiguration.DeviceToken), nameof(PluginConfiguration.AccountPassword), nameof(PluginConfiguration.TmdbApiKey) })
        {
            Assert.Matches($@"<input\s+id=""{id}""[^>]*type=""password""", Html.Value);
        }
    }
}
