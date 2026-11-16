namespace Bifrost.App.Service;

/// <summary>The systemd system unit (design §3): Bifröst under its own account, in a sandbox.</summary>
public static class SystemUnit
{
    public const string Name = "bifrost.service";
    public const string Account = "bifrost";

    public static string Render(string binary) => $"""
        [Unit]
        Description=Bifröst, the home
        After=network.target

        [Service]
        Type=simple
        User={Account}
        Group={Account}
        ExecStart={binary} daemon
        Restart=on-failure
        RestartSec=5
        RestartPreventExitStatus=1
        TimeoutStopSec=20
        StateDirectory=bifrost
        StateDirectoryMode=0700
        ConfigurationDirectory=bifrost
        RuntimeDirectory=bifrost bifrost-admin
        RuntimeDirectoryMode=0755
        UMask=0077
        NoNewPrivileges=yes
        ProtectSystem=strict
        ProtectHome=yes
        PrivateTmp=yes
        PrivateDevices=yes
        ProtectKernelTunables=yes
        ProtectKernelModules=yes
        ProtectControlGroups=yes
        RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
        RestrictNamespaces=yes
        LockPersonality=yes
        SystemCallArchitectures=native

        [Install]
        WantedBy=multi-user.target

        """.ReplaceLineEndings("\n");
}
