using CliWrap;
using CliWrap.Buffered;

namespace ServiceLib.Manager;

public class CoreAdminManager
{
    private static readonly Lazy<CoreAdminManager> _instance = new(() => new());
    public static CoreAdminManager Instance => _instance.Value;
    private Config _config;
    private Func<bool, string, Task>? _updateFunc;
    private int _linuxSudoPid = -1;
    private const string _tag = "CoreAdminHandler";

    public async Task Init(Config config, Func<bool, string, Task> updateFunc)
    {
        if (_config != null)
        {
            return;
        }
        _config = config;
        _updateFunc = updateFunc;

        await Task.CompletedTask;
    }

    private async Task UpdateFunc(bool notify, string msg)
    {
        await _updateFunc?.Invoke(notify, msg);
    }

    public async Task<ProcessService?> RunProcessAsLinuxSudo(string fileName, CoreInfo coreInfo, string configPath)
    {
        // Root runs the packaged core, never the per-user copy in the data folder.
        fileName = RootCoreFile(fileName, Utils.GetBinPath(""), Utils.GetBaseDirectory("bin"), IsWritableByUser);
        StringBuilder sb = new();
        sb.AppendLine("#!/bin/bash");
        // Absolute sudo: never one found earlier in a user-writable PATH entry.
        var sudo = SudoPath();
        var cmdLine = $"{fileName.AppendQuotes()} {string.Format(coreInfo.Arguments, Utils.GetBinConfigPath(configPath).AppendQuotes())}";

        // Passing environment variables to the sudo command, here it only xray or sing-box.
        if (coreInfo.Environment.Count > 0)
        {
            var envArgs = string.Join(" ", coreInfo.Environment.Where(kv => kv.Value.IsNotEmpty()).Select(kv => $"{kv.Key}={kv.Value.AppendQuotes()}"));
            sb.AppendLine($"exec {sudo} -S -- env {envArgs} {cmdLine}");
        }
        else
        {
            sb.AppendLine($"exec {sudo} -S -- {cmdLine}");
        }

        var shFilePath = await FileUtils.CreateLinuxShellFile("run_as_sudo.sh", sb.ToString(), true);

        var procService = new ProcessService(
            fileName: shFilePath,
            arguments: "",
            workingDirectory: Utils.GetBinConfigPath(),
            displayLog: true,
            redirectInput: true,
            environmentVars: null,
            updateFunc: _updateFunc
        );

        await procService.StartAsync(AppManager.Instance.LinuxSudoPwd);

        if (procService is null or { HasExited: true })
        {
            throw new Exception(ResUI.FailedToRunCore);
        }
        _linuxSudoPid = procService.Id;

        return procService;
    }

    /// <summary>
    /// The core binary root may run. A packaged install copies /opt/colitu-vpn/bin into the user's
    /// data folder (LocalAppData); that copy is writable by everything running as the user, so
    /// running it through sudo would turn any such process into root on the next TUN connect.
    /// When the same file exists in the package folder and the user cannot modify it (nor any
    /// folder above it), that one is used. Otherwise (a portable copy, where the whole app is the
    /// user's own) the file is returned unchanged.
    /// </summary>
    public static string RootCoreFile(string fileName, string userBinDir, string packageBinDir, Func<string, bool> writableByUser)
    {
        var file = Path.GetFullPath(fileName);
        var userBin = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userBinDir));
        var packageBin = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageBinDir));
        if (userBin == packageBin || !file.StartsWith(userBin + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return fileName;
        }
        var trusted = Path.Combine(packageBin, Path.GetRelativePath(userBin, file));
        if (!File.Exists(trusted))
        {
            return fileName;
        }
        for (var path = trusted; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
        {
            if (writableByUser(path))
            {
                return fileName;
            }
        }
        return trusted;
    }

    private static bool IsWritableByUser(string path)
    {
        try
        {
            return access(path, 2 /* W_OK */) == 0;
        }
        catch
        {
            return true;
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int access(string path, int mode);

    private static string SudoPath() => File.Exists("/usr/bin/sudo") ? "/usr/bin/sudo" : File.Exists("/bin/sudo") ? "/bin/sudo" : "sudo";

    public async Task KillProcessAsLinuxSudo()
    {
        if (_linuxSudoPid < 0)
        {
            return;
        }

        try
        {
            var shellFileName = Utils.IsMacOS() ? Global.KillAsSudoOSXShellFileName : Global.KillAsSudoLinuxShellFileName;
            // The script text goes to root's bash straight from the app (bash -c), not through
            // a file in the user's data folder that anything running as the user could rewrite.
            var script = EmbedUtils.GetEmbedText(shellFileName).Replace("\r\n", "\n");
            var arg = new List<string>() { "-S", "-p", "", "--", Global.LinuxBash, "-c", script, "kill_as_sudo", _linuxSudoPid.ToString() };
            var result = await Cli.Wrap(SudoPath())
                .WithArguments(arg)
                .WithStandardInputPipe(PipeSource.FromString(AppManager.Instance.LinuxSudoPwd))
                .ExecuteBufferedAsync();

            await UpdateFunc(false, result.StandardOutput.ToString());

            await Task.Delay(1000); // Wait for a second to ensure the process is killed
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        _linuxSudoPid = -1;
    }
}
