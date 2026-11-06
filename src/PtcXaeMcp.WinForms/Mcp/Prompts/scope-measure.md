Record these PLC signals with the TwinCAT Scope and evaluate them: {{symbols}}. Duration: {{seconds}} s.

1. Create a measurement project: `twincat_create_scope_project`, then one channel per symbol with
   `twincat_add_scope_channel` (check them with `twincat_list_scope_channels`).
2. Set the record time with `twincat_set_scope_record_settings`; add a trigger with `twincat_add_scope_trigger_set`
   when a start condition was named.
3. Record: `twincat_start_scope_record`, wait for the duration, `twincat_stop_scope_record`
   (`twincat_get_scope_record_state` shows the state).
4. Save and read the data: `twincat_save_scope_data`, then `twincat_read_scope_file` (use `maxPoints` for long
   records).
5. Evaluate: minimum, maximum, edges and their times, and anything unexpected.

Report the values per signal and where the data file lies.
