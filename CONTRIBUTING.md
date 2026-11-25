# Contributing to BoBo Remote

BoBo Remote V6 connects Windows, Android, macOS, and iOS apps over the BoBo6.2 protocol. The Windows WinForms app embeds the C++17 media and input core in `BoBo远程.exe`. The old Windows engine UI, launcher code, and embedded tuning scripts were removed from the V6 build. The Sunshine-specific scripts in `tools/` are historical references. [Protocol specification](protocol/BOBO6-SPEC.md) · [Third-party records](THIRD-PARTY.md)

## 1. Report what you measured

A PR that changes a tuning default must say whether the evidence is a configuration change, a session log, or a before-and-after measurement under the same conditions. Include the two endpoint platforms and app versions, network path, resolution, frame rate, and encoder when relevant.

## 2. Keep the Windows app small

The Windows app uses C# 5, WinForms, and the in-box `csc.exe` against .NET Framework 4.8. Use the existing dependencies and compiler settings.

- UI controls and styling live in `src/RemoteTheme.cs`.
- `src/Backend/` contains the protocol, host, client, updater, and self-tests.
- `native/bobo-core/` contains the embedded Windows media and input core.

## Ways to contribute

For connection or rendering issues, include the OS and app versions for both endpoints, GPU and driver details where relevant, the Wi-Fi or wired network path, and the relevant logs or Windows `--self-test` output. When testing encrypted UDP+FEC, identify both Windows peers and the transport reported during the session.

When updating protocol behavior, update the matching C#, Kotlin, and Swift checks and compare results against the shared vectors. [Golden vectors](protocol/golden-vectors.json)

## Development checks

Run the checks for the platforms you changed. Start the following commands at the repository root.

```powershell
.\ci\build.ps1
.\build.ps1
.\dist\BoBo远程-Windows-x64\BoBo远程.exe --self-test
```

```bash
(cd android && gradle :app:testDebugUnitTest :app:assembleRelease --stacktrace)
(cd apple/BoBoAppleCore && swift test)
bash apple/scripts/package-macos-dmg.sh
bash apple/scripts/package-ios-ipa.sh
```

Windows builds use MSVC Build Tools and the Windows SDK. Android requires JDK 17, Android SDK 34, and Gradle 8.7. Apple tests and packages require macOS with Xcode, and IPA packaging also requires XcodeGen.

Local Windows verification with direct compiler invocations passed 10 native and 118 managed checks. The installer self-test exited with code 0. RemoteSigned blocked normal script execution in that environment. Apple SDK build, packaging, and device checks are pending. [Platform guide](docs/CROSS-PLATFORM-GUIDE.md) · [CI workflow](.github/workflows/ci.yml)

## Commit messages

一句话说清「改了什么、为什么」。涉及调优默认值的提交，请把测量范围和结论写进正文。
