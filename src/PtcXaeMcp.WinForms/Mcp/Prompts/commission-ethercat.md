Commission the EtherCAT I/O of the open TwinCAT project. Master: {{master}}. Target system: {{target}}.

1. **Read what is configured**: `twincat_get_fieldbus_topology` and, for the boxes, `twincat_read_ethercat_startup`.
   Never assume a box exists because a project usually has it — read the tree.
2. **Compare with the hardware** when the target is reachable: `twincat_ethercat_read_identities` (vendor, product,
   revision and serial number per slave). List every difference before changing anything.
3. **Add or correct boxes**: `twincat_apply_schema` for a whole JSON I/O schema (validate it first with
   `twincat_validate_schema`), or `twincat_add_ethercat_box` / `twincat_remove_ethercat_box` /
   `twincat_set_previous_port` for single boxes; `twincat_configure_ethercat_slave` for slave settings.
4. **Link** the PLC variables to the I/O with `twincat_link_variables`; check the result with
   `twincat_get_mapping_info`.
5. **Build** the solution (`twincat_build_solution`) and fix every error.
6. **Activate** only when asked and only on the named target: `twincat_set_target`, then `twincat_activate_config`.
   This changes a target system — the access policy must allow runtime actions on it.

Report the topology before and after, every difference found in step 2, and what still needs a person on site.
