# SmartPrint Windows installer exit codes

The Microsoft Store submission uses Inno Setup silent installation parameters:

`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`

The SmartPrint installer installs the desktop application and the SmartPrint Windows Agent service. Installation requires elevation because the Agent is installed as a machine-level Windows service.

| Exit code | Meaning | Customer action |
|---:|---|---|
| 0 | Installation completed successfully | Launch SmartPrint from the Start menu. |
| 1 | Setup initialization failed | Restart Windows and retry. If it repeats, contact SmartPrint support with the installer version. |
| 2 | User cancelled installation | Run setup again when ready. |
| 5 | Access denied or elevation was refused | Run the installer again and approve the Windows UAC prompt. |
| 1603 | Windows Installer/installation failure reported by a prerequisite | Restart Windows, verify available disk space and Windows servicing, then retry. |
| 3010 | A prerequisite completed and requested a restart | Restart Windows before launching SmartPrint. |
| Other non-zero | Setup, WebView2 prerequisite, or Windows Agent service installation failed | Do not delete Agent/service data manually. Restart Windows and contact SmartPrint support with the exact exit code and installer version. |

The installer checks the WebView2 Runtime before launching the desktop application and installs the bundled Microsoft Evergreen Standalone Runtime when it is missing. The installer installs the SmartPrint Agent service and reports failure if the service cannot be installed or started.

Uninstallation invokes the matching Agent service uninstall script. The installer must not remove customer print queues or user-owned print data.