Run the TcUnit tests of the open TwinCAT project and report the result.

1. Check that the TcUnit library is referenced: `twincat_list_library_references`.
2. Build: `twincat_build_plc`. Fix every error first.
3. Bring the program onto the test runtime: `twincat_activate_config` (or `twincat_login` with `mode='download'`
   followed by `twincat_plc_start`). This changes a target system — only on the test runtime you were given.
4. Collect the results: `twincat_tcunit_results` waits until the runner has stored them; pass `junitPath` to also
   write a JUnit XML file for a CI server.
5. For every failed test, read the test and the code under test with `twincat_plc_read` and say which element and
   which assertion failed.

Report passed / failed / total and the failures with their elements.
