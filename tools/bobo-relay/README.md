# bobo-relay — 开源轻量自建 VPS 兜底中转与实时负载上报服务

`bobo-relay` 是随 BoBo Remote（`v7.2.0+`）提供的零外部依赖纯 Go TCP 盲中转与实时服务器负载监控程序（`CGO_ENABLED=0`），专为自建 VPS 兜底中转设计：

- **端到端盲转发**：仅做 TCP 字节流转发，TLS 1.2 + RSA-2048 TOFU + 4 位临时配对码仍严格端到端位于主被控端，中转 VPS 无法解密任何画面、声音与键鼠事件。
- **内置实时负载接口**：在 `/bobo-relay/health`（默认监听 `中转端口 + 1`，例如 `:49811`）实时上报系统 CPU、活跃并发会话数、带宽吞吐 Mbps 与综合负载 `load`（`0.0 ~ 1.0`），供 BoBo 客户端按 **RTT + 负载** 进行多节点智能优选、熔断退避与过载迁移。

## 一键编译运行

```bash
cd tools/bobo-relay
CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o bobo-relay .

# 将 VPS 的 49810 TCP 转发到目标被控端（例如 Tailscale IP 或内网主机），并在 49811 暴露负载监控：
./bobo-relay -listen :49810 -target 100.64.0.12:49810 -health-listen :49811 -max-sessions 64 -max-bandwidth-mbps 200 -region cn-north
```

## 命令行参数

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `-listen` | `:49810` | 对外暴露的 TCP 中转监听地址 |
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
