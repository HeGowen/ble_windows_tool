# BLE Windows Diagnostic Tool (DreamPod Box)

这是一个**独立**的 Windows 诊断工具仓库，只用于定位“为什么充电盒在 Windows 上扫描/连接/收包失败”。

它不会修改你当前 `dreamPod` 主项目，也不会依赖 Node-RED、MQTT、雷达桥。

## 1. 这个工具做了什么

启动后按顺序执行：

1. 采集 Windows 蓝牙健康信息
- 蓝牙服务状态（`bthserv` / `BluetoothUserService*` / `BTAGService`）
- 蓝牙 Radio 状态（On/Off）
- WinRT 默认 BLE Adapter 可用性
- 已知 BLE 设备摘要

2. 扫描 BLE 广播并打分选择“最可能的盒子”
- 参考项：设备名、MDSK 厂商数据、DreamPod Service UUID、RSSI、地址白名单
- 每次扫描都写完整日志，便于回溯为什么选中/没选中

3. 尝试连接盒子并发现 GATT
- 尝试多个地址来源（扫描地址、厂商数据里的 MAC、配置地址）
- 查找 Service/Characteristic：
  - service: `534b0001-b5a3-f393-e0a9-68716563686f`
  - rx: `534b0002-b5a3-f393-e0a9-68716563686f`
  - tx: `534b0003-b5a3-f393-e0a9-68716563686f`

4. 订阅通知并收包（只显示/记录，不转发）
- 发送 `sync_time`、`connect_all`、`collect_all`
- 解析 frame/STLD/TLD
- 记录功能码计数、数据类型计数、状态包内容

## 2. 一键运行（推荐）

在 Windows 解压后，直接双击：

- `run_ble_windows_tool.bat`

它会调用：

- `ble_windows_tool.exe`

并且窗口会停留，便于看到错误信息。

## 3. 输出日志在哪里

每次运行都会生成独立目录：

- `logs/run_YYYYMMDD_HHMMSS/`

其中关键文件：

- `runtime.log`
  - 主流程日志（扫描、连接、GATT、异常）

- `bluetooth_health.json`
  - 目标机蓝牙环境健康快照（服务、radio、adapter、known devices）

- `scan_attempts.log`
  - 每次扫描到的设备清单、打分、选择依据

- `packets.log`
  - 收到的数据帧概要（功能码、数据类型、状态字段）

- `summary.json`
  - 本次会话统计（连接次数、断开次数、通知数、帧数、最后错误）

- `effective_config.json`
  - 本次实际生效配置（用于复现）

## 4. 配置文件

默认配置路径：

- `config/box_diag_config.json`

如果不存在会自动生成。

主要字段：

- `box.target_name_contains`: 默认 `MDSK-RELAY`
- `box.target_address`: 可空；填后优先按地址选设备
- `box.allow_addresses`: 可空数组；放白名单地址
- `diag.scan_timeout_seconds`: 单次扫描窗口
- `diag.scan_attempts`: 扫描重试次数
- `diag.session_seconds`: 收包会话时长
- `diag.max_connect_attempts`: 连接重试次数
- `diag.dump_raw_hex`: 是否记录原始通知 hex（默认 false）

## 5. 命令行参数（可选）

```powershell
ble_windows_tool.exe --scan-only
ble_windows_tool.exe --address D0:CF:13:3A:18:BE
ble_windows_tool.exe --name MDSK-RELAY
ble_windows_tool.exe --session-seconds 300
ble_windows_tool.exe --config C:\path\box_diag_config.json
```

## 6. 本地编译（Windows）

要求：

- Windows 10/11 x64
- .NET 8 SDK

命令：

```powershell
dotnet restore .\src\BleWindowsTool\BleWindowsTool.csproj
dotnet publish .\src\BleWindowsTool\BleWindowsTool.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o .\dist
```

## 7. 回传给我排查时，请发

请把单次运行目录整体压缩发我，例如：

- `logs/run_20260227_123456.zip`

最少要包含：

- `runtime.log`
- `bluetooth_health.json`
- `scan_attempts.log`
- `packets.log`
- `summary.json`

## 8. 回退基线

已记录“盒子稳定连接”回退基线，请见：

- `docs/回退基线_盒子稳定连接_20260227.md`

后续新增雷达测试功能时，必须保证不影响该基线的盒子诊断能力。
