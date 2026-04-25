# 🚀 Publishing & Distribution Guide

This document outlines the complete process for building, packaging, and distributing `MediaDebrid-cli` for Windows, macOS, and Linux using **Native AOT** and **GitHub Actions**.

---

## 🏗️ 1. Build Strategy: Native AOT

To ensure the best user experience (instant startup, single-file, no .NET dependency), we use **Native AOT**.

### Local Build Commands
```powershell
# Windows x64
dotnet publish -c Release -r win-x64 -o publish/win-x64 /p:PublishAot=true /p:SelfContained=true

# Windows ARM64
dotnet publish -c Release -r win-arm64 -o publish/win-arm64 /p:PublishAot=true /p:SelfContained=true
```

---

## 🤖 2. Automation: GitHub Actions

The repository is configured with a GitHub Action (`.github/workflows/release.yml`) that automates the entire distribution process whenever a new tag is pushed.

### Release Artifacts
Every release automatically generates and attaches the following 6 assets:

| Platform | Architecture | File Name |
| :--- | :--- | :--- |
| **Windows** | x64 | `mediadebrid-win-installer-x64.exe` |
| **Windows** | ARM64 | `mediadebrid-win-installer-arm64.exe` |
| **Linux** | x64 | `mediadebrid-linux-x64.tar.gz` |
| **Linux** | ARM64 | `mediadebrid-linux-arm64.tar.gz` |
| **macOS (Intel)** | x64 | `mediadebrid-macos-x64.tar.gz` |
| **macOS (Apple Silicon)** | ARM64 | `mediadebrid-macos-arm64.tar.gz` |

### How to Trigger a New Release:
1.  **Update Version**: Update `<Version>` in `MediaDebrid-cli.csproj`.
2.  **Commit**: Push your changes to `main`.
3.  **Tag & Push**:
    ```powershell
    git tag v1.1.0
    git push origin v1.1.0
    ```

---

## 🎨 3. Windows Installer (`installer.iss`)

We use **Inno Setup** to create professional Windows installers. 

### Features:
- **No-Admin Install**: Installs to `{localappdata}\Programs\MediaDebrid`.
- **PATH Integration**: Automatically adds the install directory to the User's `Path` environment variable.
- **Architecture Locked**: The `x64` installer won't run on `arm64` (and vice-versa) to ensure users get the native performance of their hardware.

---

## 🏁 4. WinGet Submission

WinGet is the primary distribution channel for Windows.

### Initial Submission / Updates
Use `wingetcreate` to submit the new installers. Note that you should provide both the x64 and ARM64 URLs for a complete manifest.

```powershell
wingetcreate new https://github.com/zyr-ux/MediaDebrid-cli/releases/download/v1.1.0/mediadebrid-win-installer-x64.exe
```

---

## 🍎 5. macOS & Linux Instructions

For Unix-based systems, users download the `.tar.gz` archive directly. The archive contains the `mediadebrid` binary with execution permissions already set.

### macOS Security (Quarantine)
Because the binary is not notarized, macOS may block it. Users need to run:
```bash
# Extract the archive
tar -xzvf mediadebrid-macos-arm64.tar.gz

# Remove quarantine attribute
xattr -d com.apple.quarantine mediadebrid

# Move to path
sudo mv mediadebrid /usr/local/bin/mediadebrid
```

### Linux Installation
```bash
# Extract the archive
tar -xzvf mediadebrid-linux-x64.tar.gz

# Move to path
sudo mv mediadebrid /usr/local/bin/mediadebrid
```
