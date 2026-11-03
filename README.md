# MobileBridge

**Run Android apps on your Windows PC, or mirror and control your real phone. Both modes are in one app.**

MobileBridge is a Windows desktop app (C# / WPF, .NET 8) with two modes:

| Mode | What it does | Best for |
|---|---|---|
| **Virtual Phone** | Downloads Google's official Android Emulator and an Android system image with Google Play, builds a virtual phone, and starts it. Apps you install appear in the phone's real launcher, just like on a phone. | Normal apps, testing, no phone needed |
| **Real Phone (Mirror)** | Shows your real Android phone on the PC and lets you control it with mouse and keyboard (powered by [scrcpy](https://github.com/Genymobile/scrcpy)). Apps run on the phone itself. | Banking apps, games with anti-cheat, any app that blocks emulators |

You don't need Android Studio, Java, or a manual SDK install. MobileBridge downloads everything it needs the first time you use each mode.

---

## Requirements

- Windows 10/11, 64-bit
- **Virtual Phone mode:**
  - Virtualization turned on in BIOS/UEFI (Intel VT-x / AMD-V)
  - The **Windows Hypervisor Platform** feature turned on (the app has a button that does this; restart afterwards)
  - 8 GB RAM minimum (16 GB recommended) and about 10–15 GB of free disk space
- **Real Phone mode:** an Android phone with **USB debugging** turned on, plus a USB cable or the same Wi-Fi network (Android 11+)

## Download & run

1. Download `MobileBridge.exe` from the [Releases](../../releases) page. It's a self-contained exe, so you don't need to install .NET.
2. Run it.

## How to use

### Virtual Phone
1. Pick an Android version (Android 14 is recommended) and click **Download & Set Up**. Accept Google's Android SDK license when asked. The app then downloads about 2–3 GB, with resume and checksum verification.
2. Click **Start Phone**. The phone opens in its own window. The first boot takes 2–5 minutes.
3. Install apps:
   - drag and drop `.apk`, `.xapk` or `.apks` files onto the app, **or**
   - click **Install APK…**, **or**
   - open the **Play Store** inside the phone and sign in with your Google account.
4. Installed apps appear in the phone's app drawer. You can also open or uninstall them from the list in MobileBridge.

### Real Phone (Mirror)
1. Click **Download tools** (about 10–20 MB).
2. On the phone, open Settings › About phone and tap **Build number** 7 times. Then open Developer options and turn on **USB debugging**.
3. Connect the phone with USB and tap **Allow** on the phone. For a wireless connection, use the "Connect without a cable" section.
4. Click **Start Mirroring**. You can also install, open, and uninstall apps on the phone from the right-hand panel.

## Known limits

- **iOS apps (.ipa) are not supported.** There is no iOS emulator for Windows.
- Some apps detect emulators and refuse to run in the virtual phone, mainly many banking/payment apps and games with anti-cheat. MobileBridge does **not** try to hide the emulator or bypass those checks. Use **Real Phone mode** for those apps.
- ARM-only apps generally work on the Android 11+ images (they include ARM translation), but a few may not.

## Where files are stored

By default everything goes in `%LOCALAPPDATA%\MobileBridge` (SDK, virtual phone, scrcpy, and logs). You can change this in **Settings**, for example to a larger D: drive. **Settings › Remove everything downloaded** frees the space again.

## Build from source

```powershell
# needs the .NET 8 SDK
dotnet build MobileBridge.sln -c Release
# single self-contained exe:
dotnet publish src/MobileBridge -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## How it works (technical)

- Reads Google's official SDK repository index (`dl.google.com/android/repository/repository2-3.xml` and the Google Play system-image index). This is the same source Android Studio's `sdkmanager` uses. It downloads `platform-tools`, `emulator`, and `system-images;android-XX;google_apis_playstore;x86_64`, and verifies each one's SHA-1.
- Writes the AVD (`config.ini`) itself, so Java and `avdmanager` aren't needed. It then runs `emulator -avd MobileBridge -port 5554` and waits for `sys.boot_completed`.
- Installs apps with `adb install` / `adb install-multiple` (split APK bundles), and copies the OBB game data that `.xapk` bundles include.
- Real Phone mode downloads the latest `scrcpy-win64` release from GitHub and runs it against MobileBridge's own adb.

## Licenses

MobileBridge's own code is yours to license as you choose. The components it downloads keep their own licenses: the Android SDK components are under the Android Software Development Kit License Agreement (accepted in-app), and scrcpy is under Apache-2.0.
