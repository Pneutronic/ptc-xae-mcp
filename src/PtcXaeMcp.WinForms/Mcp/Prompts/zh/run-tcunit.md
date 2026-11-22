运行当前打开的 TwinCAT 项目的 TcUnit 测试并报告结果。

1. 检查是否引用了 TcUnit 库：`twincat_list_library_references`。
2. 构建：`twincat_build_plc`。先修复所有错误。
3. 将程序部署到测试运行时：`twincat_activate_config`（或使用 `mode='download'` 的 `twincat_login`，
   随后 `twincat_plc_start`）。这会更改目标系统 — 仅限在给定的测试运行时上进行。请将
   新的 UUID 作为 `requestKey` 传入，这样超时后的重试不会运行两次。
4. 收集结果：`twincat_tcunit_results` 会等待，直到运行器存储了结果；传入 `junitPath` 还可
   写出供 CI 服务器使用的 JUnit XML 文件。
5. 对每个失败的测试，用 `twincat_plc_read` 读取该测试及被测代码，并说明哪个元素以及
   哪个断言失败。

报告通过 / 失败 / 总数，以及失败项及其所属元素。
