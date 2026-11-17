namespace Bifrost.App.Service;

/// <summary>
/// Host-only actions (design §5) ask for the operating system's own password prompt through polkit.
/// The policy file is written by <c>service install</c>; without it, or without polkit, the action is refused.
/// </summary>
public static class HostGate
{
    public const string ActionId = "org.bifrost.host-action";
    public const string PolicyPath = "/usr/share/polkit-1/actions/org.bifrost.policy";

    public const string Policy = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE policyconfig PUBLIC "-//freedesktop//DTD PolicyKit Policy Configuration 1.0//EN"
          "http://www.freedesktop.org/standards/PolicyKit/1.0/policyconfig.dtd">
        <policyconfig>
          <action id="org.bifrost.host-action">
            <description>Change Bifröst's settings or keys</description>
            <message>Authentication is required to change Bifröst's settings or keys</message>
            <defaults>
              <allow_any>auth_admin</allow_any>
              <allow_inactive>auth_admin</allow_inactive>
              <allow_active>auth_admin_keep</allow_active>
            </defaults>
          </action>
        </policyconfig>

        """;

    /// <returns>True when the person at this terminal passed the prompt.</returns>
    public static async Task<bool> RequireAsync(ICommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var (exit, _) = await runner.RunAsync("pkcheck", ["--action-id", ActionId, "--process", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--allow-user-interaction"]);
        return exit == 0;
    }
}
