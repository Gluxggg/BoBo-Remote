# NOTICE

BoBo Remote 的项目源码、安装器、构建脚本和文档以 [MIT](LICENSE) 发布，版权归仓库作者。Windows 构建在主程序资源中内嵌自研原生内核 `BoBoCore.dll` 与纯 Go 用户态跨网组件 `BoBoTsNet.exe`（基于 `tailscale.com/tsnet`，BSD-3-Clause）。RustDesk、Moonlight、Moonlight V+ 和 Foundation Sunshine 属于早期 Windows 发行包。其界面、调用代码及相关资源已从当前 Windows 应用和构建中移除，仓库中的 Sunshine 调优脚本作为历史资料保留。 [第三方组件记录](THIRD-PARTY.md)

## 当前构建第三方组件

| 组件 | 版本 | 许可证 | 上游仓库与源码 |
| --- | --- | --- | --- |
| tailscale (`tsnet`) | v1.106.1 | BSD-3-Clause | <https://github.com/tailscale/tailscale> |

## 历史 Windows 发行包

| 组件 | 版本 | 许可证 | 上游仓库与源码 |
| --- | --- | --- | --- |
| moonlight-common-c | master（vendored） | GPL-3.0 | <https://github.com/moonlight-stream/moonlight-common-c> |
| RustDesk | 1.5.3 | AGPL-3.0 | <https://github.com/rustdesk/rustdesk/tree/1.5.3> |
| Moonlight | 6.2.0 | GPL-3.0 | <https://github.com/moonlight-stream/moonlight-qt/tree/v6.2.0> |
| Moonlight V+ | 6.5.6 | GPL-3.0 | <https://github.com/qiin2333/moonlight-qt/tree/v6.5.6> |
| Foundation Sunshine | v2026.1123.185102 | GPL-3.0 | <https://github.com/AlkaidLab/foundation-sunshine/tree/v2026.1123.185102.%E6%9D%82%E9%B1%BC> |

早期启动器以独立进程运行上游二进制，原生播放器以 GPL-3.0 许可链接 `moonlight-common-c`。其源码与许可仍可查阅。 [V5 计划归档](docs/V5-NATIVE-STREAM-PLAN.md) · [moonlight-common-c 许可](native/third_party/moonlight-common-c/LICENSE.txt)

历史成包许可文件包括 `LICENSES\AGPL-3.0.txt`、`LICENSES\GPL-3.0.txt` 和 `engines\Sunshine\LICENSE`。Foundation Sunshine 官方便携包还保留了 `NOTICE` 和 `licenses\manifest.json`，其中列出 ViGEmBus、usbip-win2、nefcon、ZakoVDD、Zako Virtual Mouse、zlib 和 foundation-ui 等组件。 [AGPL-3.0 全文](LICENSES/AGPL-3.0.txt) · [GPL-3.0 全文](LICENSES/GPL-3.0.txt)

旧版二进制再分发适用各自的许可证及对应源码要求，Foundation Sunshine 的随附驱动和专有组件适用其上游清单。各版本的下载地址、字节数、SHA-256 和源码获取方式见历史记录。 [第三方组件记录](THIRD-PARTY.md)

RustDesk、Moonlight、Moonlight V+、Foundation Sunshine 和网易 UU 远程的名称与商标归各自所有者。
