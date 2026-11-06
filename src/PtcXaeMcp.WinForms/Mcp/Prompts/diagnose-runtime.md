Find out why the TwinCAT system {{target}} does not behave as expected. Read first, change nothing.

1. **Status**: `twincat_get_status` and `twincat_get_plc_online_state` — XAE, project, System Service, PLC state.
2. **Alarms**: `twincat_read_event_logger` for the target (authoritative for runtime messages).
3. **Build and project messages**: `twincat_get_error_list` with `minLevel='warning'`.
4. **Crashes**: `twincat_coredump_list`, then `twincat_coredump_analyze` / `twincat_coredump_read_callstack` for the
   newest one.
5. **Fieldbus**: `twincat_get_fieldbus_topology` and, when the target answers, `twincat_ethercat_read_identities`.
6. **Live values** of the signals the alarm names: `twincat_read_ads_variables_batch`; for a step chain
   `twincat_locate_step` shows the CASE branch of the current step.

Name the most likely cause, the evidence for it, and the next step a person should take. Do not write variables,
restart or activate anything unless asked.
