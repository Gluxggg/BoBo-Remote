# BoBo Remote Protocol Specification (`BOBO6` · v6.2 Cross-Platform)

本规范定义 **BoBo远程** 四端互通（Windows / Android / macOS / iOS）的唯一权威线协议。所有平台实现必须通过同目录下的 [`golden-vectors.json`](golden-vectors.json) 字节级一致性测试。

---

## 1. 传输层与端口约定

| 项目 | 约定 |
|---|---|
| 协议魔数 | `BOBO6` |
| 控制与媒体端口 | `TCP 49810`，使用 TLS 1.2+ 和 SNI `BoBoRemote-V6-Node`。Windows 两端可协商加密 UDP+FEC，端口为 `UDP 49811`。iOS ReplayKit 有独立的本机桥接，使用 `TCP 127.0.0.1:49811`。 |
| 局域网发现端口 | `UDP 49812`（UTF-8 JSON 广播） |
| 证书与双向身份证明模型 | 被控端持有本地生成的自签名 RSA-2048 X.509 证书；控制端必须直接从 TLS 握手叶证书计算 `SHA-256` 小写十六进制指纹（`certThumbprint`）并执行严格 TOFU 钉扎（绝不信任 JSON 自报指纹或静默覆盖变更指纹）。控制端在 `HELLO` 附带自身 `clientCertDer`（Base64 X.509 DER），并在已配对重连（`AUTH_PROOF`）或首次配对（`PAIR_REQUEST.authSig`）时使用自身 RSA 私钥对 `BOBO6-AUTH:<nonce>\|<clientThumb>\|<hostThumb>` 签名（RSASSA-PKCS1-v1_5 + SHA-256），由被控端核验 `SHA256(clientCertDer) == clientThumbprint` 及公钥验签，彻底杜绝伪造 JSON `certThumbprint` 绕过鉴权。 |
| 预认证帧限制与防爆破 | 完成鉴权前单帧载荷上限严格限制为 `64 KiB`；4 位临时配对码连续失败 `5` 次立即作废，每次失败延迟 `500 ms` 并立即断开当前连接。 |
| 跨网互联 | 局域网直连（UDP `49812` 自动发现）或 Tailscale 虚拟内网 IP（`100.x.y.z`）直连。 |

---

## 2. 身份、9 位设备码与 4 位临时配对码算法

1. **证书指纹 `certThumbprint`**：
   - `hex_lower(SHA256(X509_DER_BYTES))`（64 位小写十六进制字符串）。
2. **9 位设备码 `deviceCode`**（纯数字，展示时格式化为 `XXX XXX XXX`）：
   - `digest = SHA256(UTF8("BOBO6-CODE:" + certThumbprint))`
   - `u32 = ReadUInt32LE(digest, 0)`
   - `code = (u32 % 900000000) + 100000000`（9 位十进制，范围 `100000000..999999999`）
3. **4 位临时配对码校验值 `proof`**：
   - 有效期默认 `120` 秒，单次消费。
   - `key = UTF8("BOBO6-PIN:" + pin)`
   - `msg = UTF8(lower(nonceHex) + "|" + lower(clientThumbprint) + "|" + lower(hostThumbprint))`
   - `proof = hex_lower(HMAC_SHA256(key, msg))`（必须使用常量时间比较）。

---

## 3. 二进制帧封装（所有多字节整数均为 Little-Endian）

每帧固定 **9 字节帧头** + 变长载荷：

```
Offset  Size  Type      Field
0       1     uint8     type          (0x01..0x06)
1       4     int32 LE  payloadLength (预认证 <= 64 KiB，Video <= 16 MiB，FileChunk <= 512 KiB，其他 <= 2 MiB)
5       4     uint32 LE flagsOrTimestamp
9       N     bytes     payload
```

### 3.1 帧类型定义

| `type` | 名称 | 载荷结构 |
|---|---|---|
| `0x01` | `ControlJson` | UTF-8 编码 JSON 对象 |
| `0x02` | `VideoH264` | `[0..1] uint16 LE width`, `[2..3] uint16 LE height`, `[4] uint8 keyframeFlag (bit0=1 表示 IDR)`, `[5..] H.264 Annex-B 码流 (00 00 00 01 起始码，IDR 前必须含 SPS+PPS)` |
| `0x03` | `AudioAdpcm` | `[0..3] int32 LE sampleRate (48000)`, `[4] uint8 channels (2)`, `[5..6] uint16 LE sampleCount`, `[7..] IMA-ADPCM 立体声数据` |
| `0x04` | `InputEvent` | 固定 6 字节：`[0] uint8 kind`, `[1] uint8 flags`, `[2..3] int16 LE a`, `[4..5] int16 LE b` |
| `0x05` | `FileChunk` | `[0..15] 16B ASCII transferId (右补 0x00)`, `[16..23] int64 LE fileOffset`, `[24..] 文件块原始字节`（单块 `<= 512 KiB`，单文件 `<= 2 GiB`，强制校验 `offset >= 0 && offset + len <= totalBytes` 及必填 64 位 SHA-256） |
| `0x06` | `CursorShape` | `[0] uint8 standardCursorId (0=Hidden, 1=Arrow, 2=IBeam, ... 13=Help)`。客户端在 `START_STREAM` 声明 `cursorMetrics: true` 后，主机可追加 `[1] uint8 width`、`[2] uint8 height`，单位为主机桌面像素，每边最大 128。UDP 类型的载荷在此格式前另带 4 字节小端序号。旧端只发送和读取光标编号。 |

### 3.2 `InputEvent`（`0x04`）编码表

| `kind` | 语义 | `flags` | `a` (`int16 LE`) | `b` (`int16 LE`) |
|---|---|---|---|---|
| `1` | 绝对鼠标位置 | `0` | `x`（无符号 `0..65535` 强转 `int16`） | `y`（无符号 `0..65535` 强转 `int16`） |
| `2` | 相对鼠标位移（锁鼠/触控板） | `0` | `dx` | `dy` |
| `3` | 鼠标按键 | `bit0`: `1=Down, 0=Up` | `1=Left, 2=Middle, 3=Right, 4=X1, 5=X2` | `0` |
| `4` | 鼠标滚轮 | `bit0`: `0=垂直, 1=水平` | `wheelDelta`（标准 `±120`） | `0` |
| `5` | 键盘按键 | `bit0`: `1=Down, 0=Up`; `bit1`: `1=Extended` | Windows `VK_*`（低 8 位） | `0` |
| `6` | 释放全部修饰键 | `0` | `0` | `0` |
| `7` | **V6.2 多指触控指针** | `low4`: `0=Down, 1=Move, 2=Up, 3=Cancel`; `high4`: `pointerId (0..15)` | `x`（无符号 `0..65535`） | `y`（无符号 `0..65535`） |
| `8` | **V6.2 移动系统导航动作** | `0` | `1=Back, 2=Home, 3=Recents, 4=Notifications, 5=Lock, 6=VolUp, 7=VolDown, 8=PowerDialog` | `0` |

> **向后兼容规则**：Windows/macOS 被控端收到 `kind=7` 且 `pointerId==0` 时自动映射为绝对鼠标左键按下/移动/松开；未识别的 `kind` 静默忽略，不断开连接。

### 3.3 `IMA-ADPCM` 立体声音频编码（`0x03` 载荷偏移 7 起）

- 头部 **8 字节**：
  - `[0..1]` `int16 LE` 左声道初始 `predictor`（等于首帧左采样）
  - `[2]` `uint8` 左声道初始 `stepIndex`（`0..88`，初始为 `0`）
  - `[3]` `0x00` 保留
  - `[4..5]` `int16 LE` 右声道初始 `predictor`（等于首帧右采样）
  - `[6]` `uint8` 右声道初始 `stepIndex`（`0..88`，初始为 `0`）
  - `[7]` `0x00` 保留
- 数据区 `sampleFrames` 字节：每字节编码 1 对立体声采样，`low4 = leftNibble`，`high4 = rightNibble`，使用标准 89 项 IMA `StepTable` 与 16 项 `IndexTable`。

---

## 4. 控制信令 JSON 流程（`0x01 ControlJson`）

### 4.1 握手（`HELLO` → `HELLO_ACK`）

Client → Host:
```json
{
  "type": "HELLO",
  "protocol": "BOBO6",
  "protocolVersion": "6.2",
  "platform": "android",
  "nodeId": "<uuid-hex>",
  "deviceCode": "123456789",
  "deviceName": "Pixel 9",
  "certThumbprint": "<64-hex>",
  "version": "6.2.0"
}
```

Host → Client:
```json
{
  "type": "HELLO_ACK",
  "protocol": "BOBO6",
  "protocolVersion": "6.2",
  "platform": "windows",
  "hostControlMode": "full",
  "screenOrientation": 0,
  "nodeId": "<uuid-hex>",
  "deviceCode": "987654321",
  "deviceName": "Workstation",
  "certThumbprint": "<64-hex>",
  "version": "6.2.0",
  "allowIncoming": true,
  "authorized": true,
  "nonce": "<32-hex>",
  "monitors": [
    { "index": 0, "name": "DISPLAY1 (2560x1440)", "width": 2560, "height": 1440, "primary": true }
  ]
}
```

- `platform` 取值：`"windows"` | `"macos"` | `"android"` | `"ios"`（旧版缺失时默认 `"windows"`）。
- `hostControlMode` 取值：`"full"`（完整键鼠/触控控制）或 `"view_only"`（如 iOS ReplayKit 仅查看投屏协助）。

### 4.2 会话身份验证与配对

控制端在免码串流或发送文件前校验本地配对记录中的设备 ID 和实际 TLS 证书指纹。`HELLO_ACK.authorized` 表示被控端已保存该控制设备的配对记录。升级后的被控端还要求会话私钥签名，使用旧版控制端连接时需升级控制端。

- 当 `HELLO_ACK.authorized == true` 且 `HELLO_ACK.authChallenge == true` 时，Client 在发起 `START_STREAM` 或 `FILE_OFFER` 前必须发送：
  - `{"type": "AUTH_PROOF", "signature": "<base64-RSA-PKCS1v15-SHA256>", "clientCertDer": "<base64-X509-DER>"}`
  - 签名原文为 UTF-8 字符串：`BOBO6-AUTH:<lower(nonce)>|<lower(clientThumbprint)>|<lower(hostThumbprint)>`
  - Host 验证通过后回复 `{"type": "AUTH_RESULT", "ok": true}`；验证失败则回复 `{"type": "AUTH_RESULT", "ok": false, "error": "..."}` 并立即关闭连接。
- 当 `HELLO_ACK.authorized == false` 时，Client 计算 `proof` 与 `authSig` 发送：
  - `{"type": "PAIR_REQUEST", "proof": "<64-hex>", "authSig": "<base64-RSA-PKCS1v15-SHA256>", "clientCertDer": "<base64-X509-DER>"}`
  - Host 回复 `{"type": "PAIR_RESULT", "ok": true, "hostName": "...", "hostDeviceCode": "..."}`（失败带 `"error"`，延迟 500ms 并立即关闭连接；单次 PIN 累计失败 5 次自动作废）。

### 4.3 推流与动态控制

| 消息 `type` | 方向 | 关键字段 |
|---|---|---|
| `START_STREAM` | C → H | `monitorIndex`, `width`, `height`, `fps`, `bitrateKbps`, `muteHost`, `requestPrivacy`, `superDisplay`, `clientWidth`, `clientHeight`（V6.3 可选：`autoBitrate`, `freeMouse`, `udpTransport`, `nativeLocalCursor`, `initialRttMs`，支持交错 FEC 时发送 `udpInterleavedFec: true`，支持选择性 NACK 时发送 `udpNack: true`） |
| `STREAM_STARTED` | H → C | `width`, `height`, `fps`, `bitrateKbps`, `monitorIndex`, `superDisplayActive`, `superDisplayNativeMatch`, `monitors`, `platform`, `hostControlMode`, `hostCursorComposited`（V6.3 可选：`udpPort`, `udpSessionId`, `udpKey`, `udpNack`） |
| `RECONFIGURE` / `RECONFIGURED` | C ↔ H | `monitorIndex`, `width`, `height`, `fps`, `bitrateKbps` |
| `SET_SUPER_DISPLAY` / `SUPER_DISPLAY_STATE` | C ↔ H | `enable`/`active`, `nativeMatch`, `clientWidth`/`width`, `clientHeight`/`height` |
| `SET_CURSOR_MODE` / `CURSOR_MODE_STATE` | C ↔ H | `composite` / `hostCursorComposited` |
| `REQUEST_KEYFRAME` | C → H | 无 |
| `SET_MUTE_HOST` | C → H | `mute` |
| `SET_PRIVACY` / `PRIVACY_STATE` | C ↔ H | `enable`/`active` |
| `LOCK_WORKSTATION` | C → H | 无 |
| `CLIPBOARD_TEXT` | C → H | `text`, `injectUnicode` |
| `PING` / `PONG` | C ↔ H | `ts`（回传相同数值） |
| `FILE_OFFER` → `FILE_ACCEPT` → `0x05 FileChunk*` → `FILE_DONE` → `FILE_ACK` | C ↔ H | `transferId` (16B ASCII), `fileName`, `totalBytes` (`<= 2 GiB`), `sha256` (必填 64-hex), `ok`, `savedPath`, `error` |
| `DISCONNECT` | 双向 | 优雅断开 |
| `ERROR` | H → C | `code`, `message`。Android 和 Apple 被控端在有活动串流时可返回 `HOST_BUSY`，采集未就绪时可返回 `CAPTURE_NOT_READY`。 |

Windows 被控端可以在 `HELLO_ACK` 中声明 `superDisplayVersion: 2`。支持此版本的 Windows 控制端在 `START_STREAM` 中回传版本，`clientWidth` / `clientHeight` 表示视频区域的实际像素大小，`fullscreen` 表示当前是否全屏。调整窗口后用 `SET_SUPER_DISPLAY` 更新这三个字段，并发送递增的 `displayRequestId`。全屏时可以临时切换当前被控显示器的模式。窗口模式使用原桌面比例调整串流尺寸。

协商后的 `STREAM_STARTED`、`RECONFIGURED` 和 `SUPER_DISPLAY_STATE` 中，`width` / `height` 表示串流尺寸，`desktopWidth` / `desktopHeight` 表示被控显示器模式，`desktopModeChanged` 表示当前会话是否临时更改了该模式。`superDisplayNativeMatch`（更新回执中为 `nativeMatch`）要求桌面像素尺寸与视频区域完全一致。回执携带 `clientWidth`、`clientHeight` 和 `displayRequestId`。未协商版本的旧客户端仍收到原格式，`SUPER_DISPLAY_STATE.width` / `height` 对它们表示桌面尺寸。

Windows 端在 `START_STREAM` 与 `STREAM_STARTED` 协商 `udpNack: true` 后，加密 UDP 通道支持分片级选择性重传：客户端通过报文类型 `10 (Nack)` 发送 `[uint16 LE count]` 与最多 48 条 `[uint32 LE frameId][uint16 LE shardIdx]` 请求补发；被控端在 18 帧 / 2 MiB 环形缓存与限速预算内补发原数据分片，并在分片标志位附加 `0x08`（重传标记）。

---

## 5. 局域网发现广播（`UDP 49812`）

```json
{
  "kind": "BOBO6_HELLO",
  "protocol": "BOBO6",
  "protocolVersion": "6.2",
  "platform": "windows",
  "hostControlMode": "full",
  "nodeId": "<uuid-hex>",
  "deviceCode": "987654321",
  "deviceName": "Workstation",
  "port": 49810,
  "version": "6.2.0",
  "allowIncoming": true
}
```
- `kind` 支持：`BOBO6_PROBE`（收到后单播回复 `BOBO6_HELLO`）、`BOBO6_HELLO`（每 3.5 秒心跳）、`BOBO6_BYE`（退出时立即下线通知）。
