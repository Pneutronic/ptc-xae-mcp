用 TwinCAT Scope 记录并评估这些 PLC 信号：{{symbols}}。时长：{{seconds}} 秒。

1. 创建测量项目：`twincat_create_scope_project`，然后为每个符号用
   `twincat_add_scope_channel` 添加一个通道（用 `twincat_list_scope_channels` 检查）。
2. 用 `twincat_set_scope_record_settings` 设置记录时间；如果指定了启动条件，则用
   `twincat_add_scope_trigger_set` 添加触发器。
3. 记录：`twincat_start_scope_record`，等待该时长，`twincat_stop_scope_record`
   （`twincat_get_scope_record_state` 显示状态）。
4. 保存并读取数据：`twincat_save_scope_data`，然后 `twincat_read_scope_file`（长记录请使用
   `maxPoints`）。
5. 评估：最小值、最大值、边沿及其时间，以及任何意外情况。

报告每个信号的值以及数据文件所在的位置。
