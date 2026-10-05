namespace ServiceLib.Handler.SysProxy;

[SupportedOSPlatform("linux")]
public static class ProxySettingLinux
{
    private static readonly string _proxySetFileName = $"{Global.ProxySetLinuxShellFileName.Replace(Global.NamespaceSample, "")}.sh";

    public static async Task SetProxy(string host, int port, string exceptions)
    {
        List<string> args = ["manual", host, port.ToString(), exceptions];
        // The script exits 1 on desktops it can't configure (Sway, Hyprland, bare i3...).
        // Reporting success there would show "connected" while every app goes direct.
        if (!await ExecCmd(args))
        {
            throw new InvalidOperationException("System proxy could not be applied on this desktop.");
        }
    }

    public static async Task UnsetProxy()
    {
        List<string> args = ["none"];
        await ExecCmd(args);
    }

    private static async Task<bool> ExecCmd(List<string> args)
    {
        var customSystemProxyScriptPath = AppManager.Instance.Config.SystemProxyItem?.CustomSystemProxyScriptPath;
        var fileName = (customSystemProxyScriptPath.IsNotEmpty() && File.Exists(customSystemProxyScriptPath))
            ? customSystemProxyScriptPath
            : await FileUtils.CreateLinuxShellFile(_proxySetFileName, EmbedUtils.GetEmbedText(Global.ProxySetLinuxShellFileName), true);

        // TODO: temporarily notify which script is being used
        NoticeManager.Instance.SendMessage(fileName);

        return await Utils.GetCliWrapOutput(fileName, args) != null;
    }
}
