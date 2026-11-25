# NOTICE

本仓库的启动器（`src\`）、工具脚本（`tools\`、`ci\`）、构建脚本（`build.ps1`）
与文档以 [MIT](LICENSE) 发布，版权归仓库作者。

仓库本身不包含第三方引擎二进制；构建产物包含下列上游组件。启动器把它们作为
独立进程调用，不修改其源码。包内 `engines\Sunshine` 保留 Foundation Sunshine
官方 portable 文件及其 `LICENSE`、`NOTICE`、`licenses\` 清单。

| 组件 | 版本 | 许可证 | 上游仓库与源码 |
| --- | --- | --- | --- |
| moonlight-common-c | master (vendored) | GPL-3.0 | <https://github.com/moonlight-stream/moonlight-common-c> |
| RustDesk | 1.5.3 | AGPL-3.0 | <https://github.com/rustdesk/rustdesk/tree/1.5.3> |
| Moonlight | 6.2.0 | GPL-3.0 | <https://github.com/moonlight-stream/moonlight-qt/tree/v6.2.0> |
| Moonlight V+ | 6.5.6 | GPL-3.0 | <https://github.com/qiin2333/moonlight-qt/tree/v6.5.6> |
| Foundation Sunshine | v2026.1123.185102 | GPL-3.0 | <https://github.com/AlkaidLab/foundation-sunshine/tree/v2026.1123.185102.%E6%9D%82%E9%B1%BC> |

许可证全文随构建产物放在：

- `LICENSES\AGPL-3.0.txt`
- `LICENSES\GPL-3.0.txt`
- `engines\Sunshine\LICENSE`

Foundation Sunshine 的 `NOTICE` 和 `licenses\manifest.json` 还列出了 ViGEmBus、
usbip-win2、nefcon、ZakoVDD、Zako Virtual Mouse、zlib、foundation-ui 等组件的
许可与来源；这些文件原样保留，具体以 `engines\Sunshine` 内文本为准。启动器不会
自动运行驱动安装、服务注册、防火墙或注册表修改脚本；官方包中的安装批处理仅作为
上游原始文件存在，本工具不调用。需要这些可选能力时，由用户自行确认并承担系统
配置变化。

各版本的下载地址、字节数与 SHA-256 见 [THIRD-PARTY.md](THIRD-PARTY.md)。

若你把构建产物分发给他人：GPL-3.0 与 AGPL-3.0 都要求向接收者提供对应源码，
或提供符合 GPL 第 6 条的书面要约；只给一个下载链接通常不足以履行义务。Foundation
Sunshine 的驱动和专有组件还要按它自己的 NOTICE 与清单单独复核。

本项目不是 RustDesk、Moonlight、Moonlight V+、Foundation Sunshine 或网易 UU
远程的官方发行版，也不代表任何上游项目的背书。
