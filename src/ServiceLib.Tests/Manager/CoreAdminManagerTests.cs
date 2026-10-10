namespace ServiceLib.Tests.Manager;

/// <summary>
/// TUN mode runs the core as root through sudo. A packaged install keeps a per-user copy of the
/// cores in the data folder; root must run the package's own file, which the user cannot replace.
/// </summary>
public class CoreAdminManagerTests
{
    private const string UserBin = "/home/u/.local/share/ColituVPN/bin";
    private const string PackageBin = "/opt/colitu-vpn/bin";

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "colitu-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task PackagedCore_IsUsedInsteadOfTheUserCopy()
    {
        var root = Temp();
        try
        {
            var user = Path.Combine(root, "home", "bin");
            var package = Path.Combine(root, "opt", "bin");
            Directory.CreateDirectory(Path.Combine(user, "sing_box"));
            Directory.CreateDirectory(Path.Combine(package, "sing_box"));
            File.WriteAllText(Path.Combine(user, "sing_box", "sing-box"), "user");
            File.WriteAllText(Path.Combine(package, "sing_box", "sing-box"), "package");

            var file = CoreAdminManager.RootCoreFile(Path.Combine(user, "sing_box", "sing-box"), user, package,
                path => path.StartsWith(Path.Combine(root, "home"), StringComparison.Ordinal));

            await file.Should().BeEqualTo(Path.Combine(package, "sing_box", "sing-box"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task UserCopy_StaysWhenThePackageFileIsWritableByTheUser()
    {
        var root = Temp();
        try
        {
            var user = Path.Combine(root, "home", "bin");
            var package = Path.Combine(root, "opt", "bin");
            Directory.CreateDirectory(Path.Combine(user, "xray"));
            Directory.CreateDirectory(Path.Combine(package, "xray"));
            File.WriteAllText(Path.Combine(user, "xray", "xray"), "user");
            File.WriteAllText(Path.Combine(package, "xray", "xray"), "package");
            var userFile = Path.Combine(user, "xray", "xray");

            // Any writable folder on the way up would let the user swap the file.
            await CoreAdminManager.RootCoreFile(userFile, user, package, path => path == Path.Combine(root, "opt"))
                .Should().BeEqualTo(userFile);
            await CoreAdminManager.RootCoreFile(userFile, user, package, _ => true)
                .Should().BeEqualTo(userFile);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task PortableInstall_AndMissingPackageFile_AreLeftAlone()
    {
        // Portable copy: the bin folder is the app's own folder.
        await CoreAdminManager.RootCoreFile($"{PackageBin}/sing_box/sing-box", PackageBin, PackageBin, _ => false)
            .Should().BeEqualTo($"{PackageBin}/sing_box/sing-box");
        // Not under the user copy at all.
        await CoreAdminManager.RootCoreFile("/usr/bin/sing-box", UserBin, PackageBin, _ => false)
            .Should().BeEqualTo("/usr/bin/sing-box");
        // No matching file in the package (nothing to switch to).
        await CoreAdminManager.RootCoreFile($"{UserBin}/sing_box/sing-box", UserBin, "/nonexistent-colitu/bin", _ => false)
            .Should().BeEqualTo($"{UserBin}/sing_box/sing-box");
    }

    [Test]
    public async Task APrefixLookalikeFolder_IsNotTheUserCopy()
    {
        await CoreAdminManager.RootCoreFile($"{UserBin}-evil/sing-box", UserBin, PackageBin, _ => false)
            .Should().BeEqualTo($"{UserBin}-evil/sing-box");
    }
}
