using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Portalito.Tests;

/// <summary>
/// An <see cref="ILibraryManager"/> that only names items: <c>GetNewItemId</c> gives a stable GUID per key and type,
/// every other member answers its default (no items in the library).
/// </summary>
public class FakeLibraryManager : DispatchProxy
{
    public static ILibraryManager Create() => Create<ILibraryManager, FakeLibraryManager>();

    public static Guid IdFor(string key, Type type) => new(MD5.HashData(Encoding.UTF8.GetBytes(type.FullName + key)));

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(ILibraryManager.GetNewItemId))
        {
            return IdFor((string)args![0]!, (Type)args[1]!);
        }

        var returns = targetMethod?.ReturnType;
        return returns is { IsValueType: true } && returns != typeof(void) ? Activator.CreateInstance(returns) : null;
    }
}
