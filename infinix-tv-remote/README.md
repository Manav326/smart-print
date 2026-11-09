# Infinix TV Remote APK build

This isolated branch builds an installable Android debug APK for a local Android TV Remote v2 connection.

## User-configurable connection

The app's Devices > Connect by IP dialog exposes:
- TV IPv4 address (prefilled with 192.168.31.216, editable)
- Pairing TCP port (default 6467, editable)
- Remote-control TCP port (default 6466, editable)

The values persist on the phone. The app tests both configured Android TV ports before initiating secure pairing. The TV's six-character pairing code must be entered by the user.

## GitHub Actions

The workflow .github/workflows/infinix-tv-remote.yml builds from a pinned upstream commit, applies the patch in .github/scripts/patch-infinix-remote.py, runs the Android unit tests, assembles and lints the APK, and uploads the Infinix-TV-Remote-APK artifact.

This branch intentionally does not trigger the SmartPrint main release workflow.

## Source attribution

The Android implementation is adapted from OpenTV-Remote (https://github.com/levhakobyan/OpenTV-Remote), pinned at commit a3e9c04977783c76ee491795406dfc14d2eb99e4, under the MIT License. The APK artifact includes the upstream MIT license notice.
