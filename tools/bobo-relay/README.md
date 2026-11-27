# bobo-relay — 开源轻量自建 VPS TCP+UDP 盲中转、内置 STUN 与负载上报服务

`bobo-relay` 是随 BoBo Remote（`v7.2.0+`，`v7.4.0` 升级双栈 UDP/STUN）提供的零外部依赖纯 Go TCP+UDP 盲中转、RFC 5389 STUN 服务与实时服务器负载监控程序（`CGO_ENABLED=0`），专为自建 VPS 兜底中转与跨网 UDP 打洞设计：

- **TCP + UDP 端到端盲转发**：TCP `:49810` 与 UDP `:49811` 仅做字节流与数据报盲转发，TLS 1.2 + RSA-2048 TOFU + AES/ChaCha UDP 会话密钥仍严格端到端位于主被控端，中转 VPS 无法解密任何画面、声音与键鼠事件。
- **内置 RFC 5389 STUN 探测服务**：默认监听 `:49812` UDP（`XOR-MAPPED-ADDRESS`），协助校园网/移动热点等双 NAT 环境获取公网映射端口，配合打洞竞速实现高帧率直连。
- **内置实时负载接口**：在 `/bobo-relay/health`（默认监听 TCP `:49811`，与 UDP `:49811` 协议隔离无冲突）实时上报系统 CPU、活跃并发会话数、带宽吞吐 Mbps、`udpPort`、`stunPort` 与综合负载 `load`（`0.0 ~ 1.0`），供 BoBo 客户端按 **RTT + 负载** 进行多节点智能优选、熔断退避与过载迁移。

## 一键编译运行

```bash
cd tools/bobo-relay
CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o bobo-relay .

# 将 VPS 的 49810 TCP / 49811 UDP 转发到目标被控端，并在 TCP 49811 暴露负载监控、UDP 49812 提供 STUN：
./bobo-relay -listen :49810 -udp-listen :49811 -stun-listen :49812 -target 100.64.0.12:49810 -health-listen :49811 -max-sessions 64 -max-bandwidth-mbps 200 -region cn-north
```

## 命令行参数

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `-listen` | `:49810` | 对外暴露的 TCP 中转监听地址 |
| `-udp-listen` | `:49811` | 对外暴露的 UDP 媒体盲中转监听地址（留空关闭，与 TCP 49811 健康口不冲突） |
| `-udp-target` | *(自动推算)* | 转发 UDP 目的地 `host:port`（默认推算为 `-target` 的 `port+1`） |
| `-stun-listen` | `:49812` | 内置 RFC 5389 STUN UDP 监听地址（留空关闭） |
| `-target` | *(必填)* | 转发目的地 `host:port`（例如 `100.64.0.12:49810` 或内网被控端地址） |
| `-health-listen` | `:49811` | HTTP 负载与健康监控监听地址（留空关闭）；客户端自动默认探测 `中转端口 + 1` |
| `-max-sessions` | `64` | 最大并发连接数；达到上限后自动将 `accepting` 置为 `false` |
| `-max-bandwidth-mbps` | `200` | 参考带宽容量（Mbps），用于计算带宽占用比 |
| `-region` | `""` | 可选区域标签（如 `cn-north` / `hk-bgp`） |

## 单元测试

```bash
cd tools/bobo-relay
go test -v ./...
```
