# 基于本机 360 清理对比的扩展建议

调查日期：2026-11-08。依据 E 盘 `artifacts/diagnostics/360-before-20261108-180655` 与 `360-after-20261108-181556` 的逐文件记录，并对照当前 0.4.0 内置规则和路径策略。

实施状态（0.5.0）：第一批五条规则及可选的近期浏览器缓存模式已接入统一分类清理；文件名过滤、不可被 JSON 移除的日志类型和进程限制已有隔离回归测试。第三方应用实际清理后的重建体验仍需人工验收；本机真实文件只做只读扫描。第二批系统、WPS 和 Firefox 仍未扩大清理范围。

下述体积是这次对比中消失文件的逻辑大小，单位 MiB；已经被清理，不能视为当前还能释放的空间。一次删除记录只能证明发生过删除，不能单独证明同目录其他文件也适合清理。

## 优先实现的项目

| 项目 | 本次观察 | 集成边界 |
| --- | ---: | --- |
| QQ 诊断日志 | 255.72 MiB，6 文件 | 只允许 `%APPDATA%/Tencent/Logs` 下已确认的 `QQ.tlg` 及滚动副本；退出 QQ，保留期生效，占用跳过。不得扩大到 Tencent 整个目录。 |
| 微信历史诊断日志 | 93.98 MiB，22 文件 | 仅 `%APPDATA%/Tencent/xwechat/log` 内的历史 `.xlog`，包括已验证的 radium 日志。跳过 `.mmap`、`.mmap3` 和未知类型；需关闭微信。 |
| 腾讯会议日志 | 21.42 MiB，22 文件 | 仅 `%APPDATA%/Tencent/WeMeet/Global/Logs` 的 `.log`，关闭相关进程。 |
| 360 极速浏览器 X 缓存 | 163.28 MiB，902 文件 | 只接入其 User Data 下 Default/已验证配置的 Cache、Code Cache、GPUCache；不扫描整个配置目录。补充配置发现、实际进程识别与重建验证。 |
| Chromium 着色器缓存 | 12.58 MiB，137 文件 | 在现有 Chrome 规则中补充 User Data 根目录的 GrShaderCache、ShaderCache；Edge 等变体先验证布局。关闭浏览器后清理。 |

这些候选合计约 547 MiB，但默认保留期和运行中检查仍会影响实际候选量。应先验证重建与正常启动，再发布规则。

扩展仍使用统一“分类清理”，不增加专用页面。多数沿用 `files` 处理器；Roaming 中的日志位置需要逐项加入 C# 路径白名单。QQ 滚动日志应补充通用的文件名白名单能力，而不是允许删除目录内任意文件。旧规则、排除项和升级记录保持有效。

## 需要进一步验证或交由系统的项目

| 项目 | 本次观察 | 建议 |
| --- | ---: | --- |
| WPS 缓存 | 40.02 MiB | 4 个位于 office6/cache 的 `.merge` 文件。先验证是否可正常重建，只加入确认的文件类型/命名；不触碰备份、自动恢复、云文档或整个 office6。 |
| Firefox startupCache | 18.63 MiB | 需验证配置发现和启动缓存重建，只处理缓存子目录，不触碰 Cookie、密码、会话和书签。 |
| Windows 缩略图 | 102.00 MiB | 明确限定 thumbcache*.db，集成系统工具/清理处理器；不扫描删除整个 Explorer 目录，不默认结束 Explorer 进程。 |
| Windows Internet 缓存 | 约 23 MiB | 使用 Windows 支持的清理入口；不递归删除整个 Microsoft/Windows、WebCache 或 INetCache。 |
| WMI 历史 ETL | 60.62 MiB | 涉及系统诊断会话，先区分历史与活动记录；不按 `.etl` 后缀在系统盘全局删除。 |
| Windows 更新、系统 Temp 等 | 多个小项 | 继续使用 Windows 清理入口，并逐步增加可说明范围的系统处理器。不能直接递归清 SoftwareDistribution、DataStore 或 System32。 |

## 已支持项目与保留期差异

Chrome 本次消失文件中，有 425.73 MiB、5,478 个文件位于现有 Default/Cache、Code Cache、GPUCache 范围内：

- 超过 7 天：10.60 MiB、30 文件。
- 未满 7 天：415.13 MiB、5,448 文件。

因此 Chrome 的多数差异不是缺少路径，而是当前保留期及运行中检查与 360 的行为不同。不能把所有规则的保留天数统一调到 0。

后续若增加“关闭应用后清理近期可重建缓存”，应作为现有规则的可选策略，明确确认、不默认开启，只适用于验证过的可重建缓存；普通临时文件、诊断日志及未知目录不自动放宽。

## 不应跟随本次删除的内容

- QQ 的 webkit_cache 中实际存在并被删除了 Cookies、Cookies-journal，不把这个目录整体标为普通缓存。
- QQ 接收的图片、视频、音频及文件属于个人资料，不因 360 删除过就自动加入。
- 聊天数据库、Moect 归档和导出、项目 `.git`、训练数据、C/C++ 同级数据库不自动删除。Moect 后续可以考虑只读占用和迁移建议。
- 未核对到具体文件的约 2.43 GiB 空间差额不作为新规则的收益证据。

## 参考与验证依据

- 本机逐文件记录是候选路径及历史体量的依据。未读取聊天内容，未执行额外清理。
- [BleachBit 的 Chrome 规则](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/google_chrome.xml)包含 GrShaderCache 和 ShaderCache 的处理，可参考其范围划分，独立实现。
- [Mozilla 的配置数据说明](https://support.mozilla.org/en-US/kb/profiles-where-firefox-stores-user-data)区分 Cookie、密码、会话和书签等数据，不能把整个配置目录视作缓存。
- [Microsoft cleanmgr](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cleanmgr)与[磁盘清理处理器接口](https://learn.microsoft.com/en-us/windows/win32/lwef/disk-cleanup)提供系统清理的集成路线。

以上历史体量只用于扩展依据，不承诺新版本扫描时能获得相同清理量。
