using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.PortalitoTV.Content;
using Xunit;

namespace Jellyfin.Plugin.PortalitoTV.Tests;

/// <summary>
/// <c>IUserManager.Users</c> (10.11.0) became <c>GetUsers()</c> in later 10.11.x patches. The plugin is built against 10.11.0 and
/// must run on both, so it finds the user list by reflection. These fakes stand for the two shapes of the interface.
/// </summary>
public class UsersOfTests
{
    private static User U(string name) => new(name, "auth", "reset");

    [Fact]
    public void ReadsThePropertyOfTheOlderShape()
    {
        var manager = new OlderShape(new[] { U("a"), U("b") });
        Assert.Equal(new[] { "a", "b" }, PortalitoLibrary.UsersOf(manager).Select(u => u.Username));
    }

    [Fact]
    public void CallsTheMethodOfTheNewerShape()
    {
        var manager = new NewerShape(new[] { U("c") });
        Assert.Equal(new[] { "c" }, PortalitoLibrary.UsersOf(manager).Select(u => u.Username));
    }

    [Fact]
    public void PrefersTheMethodWhenBothExist()
    {
        var manager = new BothShapes();
        Assert.Equal(new[] { "from-method" }, PortalitoLibrary.UsersOf(manager).Select(u => u.Username));
    }

    [Fact]
    public void ReturnsNothingWhenNeitherMemberExists() => Assert.Empty(PortalitoLibrary.UsersOf(new object()));

    private sealed class OlderShape
    {
        private readonly User[] _users;

        public OlderShape(User[] users) => _users = users;

        public IEnumerable<User> Users => _users;
    }

    private sealed class NewerShape
    {
        private readonly User[] _users;

        public NewerShape(User[] users) => _users = users;

        public IReadOnlyList<User> GetUsers() => _users;
    }

    private sealed class BothShapes
    {
        public IEnumerable<User> Users => new[] { U("from-property") };

        public IEnumerable<User> GetUsers() => new[] { U("from-method") };
    }
}
