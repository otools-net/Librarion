using Librarion.Core.Access;
using Librarion.Core.Data;

namespace Librarion.Tests;

/// <summary>The rights catalog's rules and the pieces of access that need no server (agents/docs/features/rights.md).</summary>
[TestClass]
public sealed class RightCatalogTests
{
    [TestMethod]
    public void EveryRightIsWellFormed()
    {
        var ids = RightCatalog.All.Select(r => r.Id).ToList();

        CollectionAssert.AllItemsAreUnique(ids);
        Assert.IsTrue(typeof(Rights).GetFields().All(f => ids.Contains((string)f.GetValue(null)!)), "Every Rights constant is in the catalog");
        Assert.IsTrue(RightCatalog.All.Where(r => r.Scope == RightScope.Server).All(r => r.DefaultGroups.Count == 0), "Server rights belong to server administrators only");
        Assert.IsTrue(RightCatalog.All.All(r => r.Id.StartsWith(r.Scope switch
        {
            RightScope.Library => "library.",
            RightScope.Server => "server.",
            _ => "",
        }, StringComparison.Ordinal)), "Ids say their scope");
        Assert.IsTrue(RightCatalog.All.Where(r => r.DefaultGroups.Contains(BuiltInGroup.Users)).All(r => r.DefaultGroups.Contains(BuiltInGroup.Administrators)),
            "Administrators get whatever users get");
    }

    [TestMethod]
    public void PatternsNameRights()
    {
        Assert.IsTrue(RightCatalog.Matches("tenant.*", Rights.ManageUsers));
        Assert.IsTrue(RightCatalog.Matches("*", Rights.Browse));
        Assert.IsTrue(RightCatalog.Matches("LIBRARY.BROWSE", Rights.Browse));
        Assert.IsTrue(RightCatalog.Matches(RightCatalog.WriteSelector, Rights.ManageUsers));
        Assert.IsFalse(RightCatalog.Matches(RightCatalog.WriteSelector, Rights.Browse));
        Assert.IsFalse(RightCatalog.Matches("tenant.users", Rights.ManageLibraries));
    }

    [TestMethod]
    public void PasswordsVerifyAndOlderHashesAreMarkedOutdated()
    {
        var hash = PasswordHashing.Hash("secret words");
        var parts = hash.Split('$');
        var weaker = $"{parts[0]}$1000${parts[2]}${Convert.ToBase64String(System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes("secret words"), Convert.FromBase64String(parts[2]), 1000, System.Security.Cryptography.HashAlgorithmName.SHA512, 32))}";

        Assert.IsTrue(PasswordHashing.Verify("secret words", hash, out var outdated));
        Assert.IsFalse(outdated);
        Assert.IsFalse(PasswordHashing.Verify("other words", hash, out _));
        Assert.IsFalse(PasswordHashing.Verify("secret words", "garbage", out _));
        Assert.IsTrue(PasswordHashing.Verify("secret words", weaker, out outdated));
        Assert.IsTrue(outdated);
    }

    [TestMethod]
    public void SetupCodesCompareAsTyped()
    {
        var generated = new SetupOptions { Code = "ABCD-EFGH-JKMN-PQRS", CodeIsGenerated = true };
        var configured = new SetupOptions { Code = "My-Own-Code-123" };

        Assert.IsTrue(generated.Matches("abcd efgh jkmn pqrs"));
        Assert.IsFalse(generated.Matches("ABCD-EFGH-JKMN-PQRT"));
        Assert.IsTrue(configured.Matches("My-Own-Code-123"));
        Assert.IsFalse(configured.Matches("my-own-code-123"));
        Assert.IsFalse(new SetupOptions { Code = "short" }.Matches("short"));
        Assert.MatchesRegex(@"^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){3}$", GeneratedSetupCode.Generate());
    }

    [TestMethod]
    public void SlugsAreShortAsciiNames()
    {
        Assert.AreEqual("familia-garcia", AccessService.Slugify("Família García"));
        Assert.AreEqual("tenant", AccessService.Slugify("Библиотека"));
        Assert.AreEqual("a-b", AccessService.Slugify("  A -- B  "));
    }
}
