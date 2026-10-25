# 第三方组件声明

本项目自身以 GPL-3.0 授权（见 [LICENSE](LICENSE)）。以下是运行时与构建期依赖的第三方组件及其授权。

## 运行时依赖

| 组件 | 版本 | 许可证 | 版权 |
|---|---|---|---|
| .NET Runtime | 10.0 | MIT | © .NET Foundation and Contributors |
| Windows Desktop Runtime（WPF） | 10.0 | MIT | © .NET Foundation and Contributors |
| System.Text.Json（随 BCL 提供） | 10.0 | MIT | © .NET Foundation and Contributors |

本项目**未引用任何第三方 NuGet 包**。以上均为 .NET SDK 自带的基础类库，随运行时一同分发。

## 构建期工具

| 工具 | 用途 | 许可证 |
|---|---|---|
| .NET SDK 10 | 编译 | MIT |
| Inno Setup（计划中） | 制作安装包 | Inno Setup License（允许自由使用与分发） |

## 资源文件

| 文件 | 来源 | 授权 |
|---|---|---|
| `src/DeskCaddy.App/assets/app.ico` | 项目原创设计 | 与代码分离，见 README「商标与品牌」 |
| `src/DeskCaddy.App/assets/app.png` | 由 `DeskCaddy .png` 生成 | 同上 |
| `src/DeskCaddy.App/assets/DeskCaddy .png` | 项目原创设计稿 | 同上 |

图标与设计稿**不适用 GPL-3.0**，保留所有权利。如果你 fork 本项目并发布改版，请替换为自己的图标。

## 关于 .NET 的 MIT 授权

MIT 许可证要求保留版权声明与许可声明。由于本项目以框架依赖（framework-dependent）方式分发、不内嵌 .NET 运行时，通常无需在安装包中附 .NET 的许可全文；若将来改为自包含（self-contained）发布，**必须**在安装包或关于页面附带 .NET 的 MIT 声明。
