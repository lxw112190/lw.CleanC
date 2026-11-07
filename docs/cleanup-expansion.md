# 清理范围扩展调研（2026-11-07）

本轮只做公开资料核对与本机只读检查，没有删除文件，也没有将以下候选直接加入现有自动删除规则。

后续实现状态：v0.3.0 已加入 ipch、NVIDIA 缓存和 VS 安装临时包专项，并增加独立确认与安装/重启状态检查。下文保留调研当时的占用与优先级，不代表当前可清理量。

## 参考产品与核实边界

360 清理 Pro 的官方隐私白皮书列出了 C 盘垃圾/大文件/重复文件，以及微信、QQ 和使用痕迹等场景。系统盘瘦身介绍还涉及大文件转移。腾讯电脑管家官网提供垃圾清理入口，但当前页面正文无法读取，未取得公开、可复用的完整路径规则。可以参考产品分类，不据此宣称复现其内部规则库。

本项目继续按应用官方文档、实际目录结构、保留范围和清理影响独立定义规则。软件缓存、重新下载的安装包、用户文件、系统维护需要分别处理。

## 本机优先级

以下均为逻辑大小；不是承诺可安全删除或实际可释放的大小。时间敏感数据来自当日检查。

| 优先级 | 类别 | 本机发现 | 建议集成方式 |
| --- | --- | --- | --- |
| P0 | VS Code C/C++ IntelliSense | vscode-cpptools 总计约 9.99 GiB，其中 ipch 约 4.836 GiB、数据库约 5.156 GiB | 先对白名单 ipch 子目录提供专项预览；关闭 Code 和 C/C++ 语言服务，删除前检查文件是否变化。数据库部分先走扩展重置/重新索引，不按通用 db 后缀删除。提示补全会重建缓存。 |
| P0 | 已完成的 VS 安装临时包 | 两个 Temp 子目录合计约 5.67 GiB | 识别安装包清单、签名和所属产品；检查安装进程/待重启状态；用户确认安装已结束后专项处理。未满 7 天的文件不能靠降低全局临时文件保留期自动放行。与普通 Temp 结果去重。 |
| P1 | NVIDIA 着色器缓存 | GLCache 约 0.764 GiB，DXCache 约 0.078 GiB；超过 7 天的两者合计约 0.809 GiB | 仅考虑明确的 GLCache/DXCache 目录，进一步验证驱动版本和在用进程。关闭图形应用后预览，说明首次运行可能重新编译。不可整目录删除 NVIDIA 应用或驱动。 |
| P1 | JetBrains 缓存与索引 | PyCharmCE2023.3 目录合计约 4.083 GiB；包括缓存、索引、日志等 | 展示占用并引导 Invalidate Caches。版本识别后仅选择已证实的缓存子目录，保留 Local History、配置、插件及工作区数据。目录总量不能全部算作可清理。 |
| P1 | 软件更新下载包 | 有道 updater 约 0.714 GiB，含 installer.exe 和 pending 下的安装包 | 区分已安装版本与待安装更新。pending 不能仅按文件名认定无用；检查签名、版本和更新状态后供用户选择。WPS/Codex 的已安装版本目录不直接当缓存。 |
| P1 | Windows 系统维护 | 当前尚未给出可回收量 | 扩展现有系统工具入口，覆盖系统临时文件、错误报告、缩略图、传递优化和组件清理。系统组件使用 Windows 支持的接口/工具；不递归删除 Installer、WinSxS 或更新服务目录，不默认执行 ResetBase。 |
| P2 | 微信/QQ 专清 | 微信数据目录约 10.68 GiB，QQ 数据也较大 | 先按日志、缓存、图片、视频、接收文件和数据库拆分，只对核实过的日志/缓存提供规则。聊天数据库和附件由用户通过原软件存储管理选择；不得整目录默认勾选。 |
| P2 | Conda、NuGet 等开发包缓存 | NuGet 全局目录约 8.11 GiB；两个已知 Conda 包目录顶层未发现可计量的 .conda/.tar.bz2 下载包 | 优先提供官方工具的 dry-run/清理入口，不能把整个包目录大小当收益。Conda 环境和已解压包可能被链接使用；NuGet 包也可能用于离线构建。 |
| P2 | 下载与大文件迁移 | 已有大文件/重复文件只读分析 | 增加已安装程序列表、下载包年龄/版本展示、迁移向导。迁移需校验、可恢复，并优先利用应用设置中的数据位置；不盲目移动 Program Files 或制造目录联接。 |

## 下一版建议

先做三项：C/C++ 缓存专项、安装临时包专项、显卡缓存专项。它们比继续增加零散日志路径更能解决当前机器的空间问题。

“普通清理”继续沿用保留期；“专项清理”增加来源、重建代价、当前是否在用、需要用户确认的条件；“系统维护”由内置 Windows 工具处理；“个人资料”只提供分析、定位或原软件入口。

扫描层需要支持按完整路径白名单和文件类型双重匹配、目录/文件组去重、规则证据与版本记录、扫描总量和实际候选量分开显示。更新内置规则时要保留用户修改及排除项，不能直接覆盖个人 JSON。

## 明确不扩大的范围

- 不把 .git/objects、聊天数据库、浏览器 Cookie/密码、IDE 本地历史、Conda 环境或模型权重归入默认垃圾清理。
- 不递归清 Windows Installer、WinSxS、DriverStore 或所有 ProgramData/Package Cache。
- 注册表清理、内存加速和周期性删除 Prefetch 不作为释放磁盘空间的默认功能。
- 休眠、还原点与分页文件属于功能/恢复能力选择，不能伪装成普通缓存清理。当前机器分页文件在 E 盘，更不应当作 C 盘清理收益。

## 主要资料

- [360 清理 Pro 官方隐私白皮书](https://www.360.cn/privacy/v3/cleanpro.html)
- [360 系统盘瘦身介绍](https://weishi.360.cn/clear/xtpss/)
- [腾讯电脑管家官网](https://guanjia.qq.com/)
- [VS Code C/C++：ipch、缓存路径与体积限制](https://code.visualstudio.com/docs/cpp/faq-cpp)
- [Visual Studio：禁用或移动安装包缓存](https://learn.microsoft.com/en-us/visualstudio/install/disable-or-move-the-package-cache)
- [PyCharm：使缓存失效及 Local History 选项](https://www.jetbrains.com/help/pycharm/invalidate-caches.html)
- [PyCharm：配置、缓存、插件和日志目录](https://www.jetbrains.com/help/pycharm/directories-used-by-the-ide-to-store-settings-caches-plugins-and-logs.html)
- [Windows：组件存储清理及 ResetBase 的影响](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)
- [Conda：clean 的范围和警告](https://docs.conda.io/projects/conda/en/latest/commands/clean.html)
