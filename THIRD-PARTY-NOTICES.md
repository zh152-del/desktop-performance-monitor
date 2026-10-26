# 第三方组件声明

本仓库 `Project/packages/LibreHardwareMonitor.0.9.6/` 目录下内置了以下第三方二进制组件。
这些组件**未经修改**，按各自上游许可分发，使用时请遵循相应条款。

| 组件 | 用途 | 上游许可 |
| --- | --- | --- |
| `LibreHardwareMonitorLib.dll` | 硬件传感器读取（温度、风扇、负载） | MPL-2.0 |
| `BlackSharp.Core.dll` | Libre Hardware Monitor 生态基础库 | MPL-2.0 |
| `DiskInfoToolkit.dll` | 磁盘信息工具库 | MPL-2.0 |
| `HidSharp.dll` | HID 设备访问（USB 传感器） | MPL-2.0 |
| `RAMSPDToolkit-NDD.dll` | 内存 SPD 读取工具库 | MPL-2.0 |
| `System.Memory.dll` | .NET 兼容包（Span\<T\> 等） | MIT |
| `System.Buffers.dll` | .NET 兼容包 | MIT |
| `System.Numerics.Vectors.dll` | .NET 兼容包 | MIT |
| `System.Runtime.CompilerServices.Unsafe.dll` | .NET 兼容包 | MIT |

MPL-2.0 组件的主要义务：若修改了这些文件，需以 MPL-2.0 公开修改后的源码；未修改时，引用与再分发需保留原许可声明。

上述组件的完整许可文本可从各自上游项目获取：

- Libre Hardware Monitor：<https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>
- HidSharp：<https://github.com/IntergatedCircuits/HidSharp>

`System.*` 系列由 Microsoft 发布，遵循 MIT 许可。

---

本仓库自身的源码许可由项目所有者补充发布；在此之前，默认保留全部权利。
