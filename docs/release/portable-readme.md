# Portable VibeWallpaper

Run `eng/publish-portable.ps1` from a Windows PowerShell 5.1+ prompt, then `eng/verify-portable.ps1`. The artifact is self-contained x64 and does not bundle the Evergreen WebView2 browser executable. WebView2 remains an external runtime; LibVLC binaries/plugins are expected inside the portable folder.
