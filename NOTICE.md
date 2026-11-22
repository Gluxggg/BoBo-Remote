# NOTICE

本仓库的启动器（`src\`）、工具脚本（`tools\`、`ci\`）、构建脚本（`build.ps1`）
与文档以 [MIT](LICENSE) 发布，版权归仓库作者。

仓库**不包含**任何第三方引擎二进制。构建产物里三个引擎来自各自上游项目，以独立
进程方式被调用，本项目没有修改它们的源码，也不对它们做二次打包再分发以外的改动：

| 组件 | 版本 | 许可证 | 上游仓库与源码 |
| --- | --- | --- | --- |
| RustDesk | 1.5.3 | AGPL-3.0 | <https://github.com/rustdesk/rustdesk/tree/1.5.3> |
| Moonlight | 6.2.0 | GPL-3.0 | <https://github.com/moonlight-stream/moonlight-qt/tree/v6.2.0> |
| Moonlight V+ | 6.5.6 | GPL-3.0 | <https://github.com/qiin2333/moonlight-qt/tree/v6.5.6> |

许可证全文随构建产物放在 `LICENSES\AGPL-3.0.txt` 与 `LICENSES\GPL-3.0.txt`。
各版本的下载地址、字节数与 SHA-256 见 [THIRD-PARTY.md](THIRD-PARTY.md)。

若你也把自己构建的产物分发给他人：GPL-3.0 与 AGPL-3.0 都要求向接收者提供对应
源码，或提供符合 GPL 第 6 条的书面要约；只给一个下载链接通常不足以履行义务。

本项目不是 RustDesk、Moonlight、Foundation Sunshine 或网易 UU 远程的官方发行版，
也不代表任何上游项目的背书。
