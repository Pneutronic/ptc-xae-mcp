调试当前打开的 TwinCAT 项目的 EtherCAT I/O。主站：{{master}}。目标系统：{{target}}。

1. **读取已配置的内容**：`twincat_get_fieldbus_topology`，对于各个模块再用 `twincat_read_ethercat_startup`。
   切勿因为项目通常有某个模块就假定它存在 — 请读取树。
2. **与硬件比对**（目标可访问时）：`twincat_ethercat_read_identities`（每个从站的供应商、产品、
   版本和序列号）。在更改任何内容之前，列出所有差异。
3. **添加或更正模块**：整个 JSON I/O 架构用 `twincat_apply_schema`（先用
   `twincat_validate_schema` 验证），单个模块用 `twincat_add_ethercat_box` / `twincat_remove_ethercat_box` /
   `twincat_set_previous_port`；从站设置用 `twincat_configure_ethercat_slave`。
4. **链接** PLC 变量与 I/O，用 `twincat_link_variables`；用
   `twincat_get_mapping_info` 检查结果。
5. **构建**解决方案（`twincat_build_solution`），并修复所有错误。
6. 仅在被要求时，且仅在指定的目标上**激活**：先 `twincat_set_target`，再 `twincat_activate_config`。
   这会更改目标系统 — 访问策略必须允许对其执行运行时操作，并且可能会请人工
   确认。请将新的 UUID 作为 `requestKey` 传入：超时后使用相同 key 的同一调用绝不会激活两次
   （`twincat_receipt_get` 会显示其结果）。

报告前后的拓扑、步骤 2 中发现的所有差异，以及仍需现场人员处理的事项。
