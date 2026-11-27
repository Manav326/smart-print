# SmartPrint Windows installer exit codes

The Microsoft Store submission uses Inno Setup silent installation parameters:

`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`

The SmartPrint installer installs the desktop application and the SmartPrint Windows Agent service. Installation requires elevation because the Agent is installed as a machine-level Windows service. A Windows User Account Control (UAC) prompt is allowed.

The outer setup executable returns the standard Inno Setup exit codes:

| Exit code | Meaning | Customer/support action |
|---:|---|---|
| 0 | Setup completed successfully. | Launch SmartPrint from the Start menu. |
| 1 | Setup failed to initialize. | Check the Windows event/log information, restart Windows and retry. |
| 2 | User cancelled before the installation phase. | Run setup again when ready. |
| 3 | Fatal error while preparing to enter the installation phase. | Restart Windows and retry; contact SmartPrint support if it repeats. |
| 4 | Fatal error during installation. This includes a failed WebView2 or Windows Agent service installation. | Do not manually delete Agent/service data. Restart Windows and contact SmartPrint support with the installer version and setup log. |
| 5 | User cancelled during installation, or selected Abort in an Abort/Retry/Ignore error dialog. | Retry only after resolving the displayed error; contact support if it repeats. |
| 6 | Setup was terminated by the Inno Setup debugger. | Not expected in customer installations; report to SmartPrint support. |
| 7 | Setup determined it cannot proceed during the Preparing to Install stage. | Follow the displayed reason, restart Windows if appropriate, then retry. |
| 8 | Setup cannot proceed and Windows must be restarted to correct the condition. | Restart Windows and run setup again. |

Any non-zero exit code means setup did not complete successfully. Future Inno Setup versions may add codes; support tooling must treat unknown non-zero values as failures rather than assuming success.

The bundled Microsoft WebView2 Evergreen Standalone Runtime may internally return 3010 to request a restart. SmartPrint accepts that prerequisite result and verifies the runtime registration; the outer Inno Setup installer does not expose 3010 as its own success code.

The installer checks WebView2 before launching the desktop application and installs the bundled Microsoft Evergreen Standalone Runtime when it is missing. It installs the SmartPrint Agent service and reports a setup failure if the service cannot be installed or started.

Uninstallation invokes the matching Agent service uninstall script. The installer must not remove customer print queues or user-owned print data.

References:
- [Inno Setup Setup Exit Codes](https://jrsoftware.org/inno-setup/documentation/topic_setupexitcodes.htm)
- [Inno Setup Setup Command-Line Parameters](https://jrsoftware.org/inno-setup/documentation/topic_setupcmdline.htm)
