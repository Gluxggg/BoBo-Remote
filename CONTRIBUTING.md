# Contributing to BoBo Remote

Thanks for taking a look. BoBo Remote (`Gluxggg/BoBo-Remote`) is a personal-scale remote desktop launcher and installer for
Windows: it wraps four upstream engines (RustDesk, Moonlight V+, Moonlight, and bundled Foundation Sunshine) behind
one small native launcher + graphical installer, and adds onboarding, privacy curtain protection, and built-in tuning/diagnostics.

Issues and pull requests are welcome. Please read the two rules below first — they
keep this project honest.

## 1. Do not report a setting change as a performance result

This project distinguishes three different things and never mixes them:

| 状态 | 含义 |
| --- | --- |
| 已写入的配置 | `sunshine.conf` 里确实存在的键值 |
| 日志中观察到的现象 | 某次会话的日志行，属于历史证据 |
| 双机同条件对照 | 同一网络、同一会话参数下的前后测量 |

A PR that changes a tuning default **must** say which of the three it is. "FEC 打开
后就不丢帧了" is not acceptable; "已写入 `fec_percentage=20`，尚无同条件对照" is.

## 2. Keep the launcher small

The launcher is C# 5 compiled by the in-box `csc.exe` against .NET Framework 4.8.
That is deliberate: the target machine should not need an SDK, a runtime download, or
an installer. Please do not introduce NuGet packages, C# 6+ syntax, or a build system.

- UI: WinForms, hand-drawn controls (`src/RemoteTheme.cs`)
- Backend: `src/Backend/*.cs`, each file owns one concern
- Everything the launcher needs from upstream engines goes through a fixed relative
  path under `engines\`, and every launch is a separate process invocation

## Ways to contribute

### 上报引擎或平台差异

最有价值的贡献是把「上游引擎在你机器上的真实行为」写清楚：显卡与驱动版本、编码器
（`h264_amf` / `hevc_amf` / `nvenc` / `quicksilver`）、被控端与主控端系统版本、是否
经过 DERP 中继。请在 issue 里贴上 `--self-test` 输出和关键日志行。

### 补充调优脚本的硬件分支

`tools/Optimize-SunshineStream.ps1` 目前只对 AMD AMF 与「自动编码器」给出确定性建议，
其它编码器只检查自动 FEC。若你能给出某类硬件的可验证依据，欢迎补充分支——但请保持
「诊断 + 建议 + 可还原」的形态，不要默认静默改机器配置。

### 改进文档

`低延迟调优与防窥指南.md` 与 `UU远程功能核对.md` 里的每条结论都带来源分级。发现链接
失效、结论过期或官方口径变化，直接提 PR。

## Development checks

本地必须先过这两步，否则 PR 不会合并：

```powershell
# 1. 快速编译 + 无 UI 自检（与 GitHub Actions CI 完全一致）
powershell -NoProfile -ExecutionPolicy Bypass -File .\ci\build.ps1

# 2. 完整便携包构建（离线复用已校验缓存）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SkipFetch

# 3. 单独运行无 UI 自检（参数校验、CLI 拼装、壁纸几何、防窥锁屏状态机、新手引导布局、抗丢包诊断）
.\dist\BoBo远程-Windows-x64\BoBo远程.exe --self-test
```

`--self-test` 在禁止 ICMP 的环境（沙箱、部分安全软件）会把两项回环回包校验标记为
`[跳过]`，这是预期行为，不算失败。

## Commit messages

一句话说清「改了什么、为什么」，不要写「update」「fix bug」。涉及调优默认值的提交，
请把测量范围和结论写进正文。
