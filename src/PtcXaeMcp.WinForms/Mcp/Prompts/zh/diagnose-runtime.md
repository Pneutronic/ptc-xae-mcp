找出 TwinCAT 系统 {{target}} 为什么没有按预期运行。先读取，不要更改任何内容。

1. **状态**：`twincat_get_status` 和 `twincat_get_plc_online_state` — XAE、项目、系统服务、PLC 状态。
2. **报警**：针对该目标使用 `twincat_read_event_logger`（运行时消息以此为准）。
3. **构建和项目消息**：`twincat_get_error_list`，使用 `minLevel='warning'`。
4. **崩溃**：`twincat_coredump_list`，然后对最新的一个使用 `twincat_coredump_analyze` /
   `twincat_coredump_read_callstack`。
5. **现场总线**：`twincat_get_fieldbus_topology`，目标有应答时再用 `twincat_ethercat_read_identities`。
6. 报警中提到的信号的**实时值**：`twincat_read_ads_variables_batch`；对于步进链，
   `twincat_locate_step` 会显示当前步骤的 CASE 分支。

指出最可能的原因、相应的证据，以及人员接下来应采取的步骤。除非被要求，否则不要写入变量，
也不要重启或激活任何内容。
