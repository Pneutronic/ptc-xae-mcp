更改当前打开的 TwinCAT 项目的 PLC 代码：{{task}}

使用 ptc-xae-mcp 工具，以小步、经过验证的方式工作：

1. 用 `twincat_plc_search` **查找**更改涉及的每一处（字面文本或正则表达式，标识符使用 `wholeWord`）。
   `twincat_plc_list` 仅用于浏览；节点标签已本地化，因此切勿猜测树路径。
2. 用 `twincat_plc_read` **读取**你要更改的每个元素（用 `paths` 一次读取多个）。记下每个元素的
   `Revision`。
3. 精确地**更改**：
   - 某一段确切的文本：带 `expectedRevision` 的 `twincat_plc_replace_text`；
   - 声明行：带 `expectedRevision` 的 `twincat_plc_edit_entries`；
   - 整个段落：带 `expectedRevision` 的 `twincat_plc_write`；
   - 新元素：`twincat_plc_create`（同时传入 `implementation`）。
   因修订号过期而被拒绝的写入意味着其他人已更改该元素：请先重新读取它。
4. 用 `twincat_build_plc` **构建**，并在 `expectSymbols` 中传入新名称，以便报告被编译器丢弃的符号。
   在进行下一步之前修复所有错误。
5. 用 `twincat_plc_check_all_objects` **检查**没有任务会执行到的代码，如果项目使用静态分析，
   则运行 `twincat_plc_static_analysis`。
6. 仅在被要求将更改应用到正在运行的 PLC 时：使用 `mode='online_change'` 的 `twincat_login`（它会在 PLC 上验证
   该更改），否则在构建后停止，并报告更改了什么。

报告已更改的元素、构建结果以及任何你无法验证的内容。
