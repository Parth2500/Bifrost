namespace Bifrost.App.Desk;

/// <summary>The systemd user unit that runs the desk companion in the admin's desktop session.</summary>
public static class DeskUnit
{
    public const string Name = "bifrost-desk.service";

    public static string Render(string binary) => $"""
        [Unit]
        Description=Bifröst desk companion
        PartOf=graphical-session.target
        After=graphical-session.target

        [Service]
        ExecStart={binary} desk
        Restart=always
        RestartSec=5

        [Install]
        WantedBy=graphical-session.target

        """.ReplaceLineEndings("\n");
}
