Change the PLC code of the open TwinCAT project: {{task}}

Work in small, verified steps with the ptc-xae-mcp tools:

1. **Find** every place the change touches with `twincat_plc_search` (literal or regex, `wholeWord` for identifiers).
   Use `twincat_plc_list` only to browse; node labels are localized, so never guess a tree path.
2. **Read** each element you will change with `twincat_plc_read` (several at once with `paths`). Note the
   `Revision` of every element.
3. **Change** precisely:
   - one exact piece of text: `twincat_plc_replace_text` with `expectedRevision`;
   - declaration lines: `twincat_plc_edit_entries` with `expectedRevision`;
   - a whole section: `twincat_plc_write` with `expectedRevision`;
   - new elements: `twincat_plc_create` (pass `implementation` along).
   A refused write because of a stale revision means someone else changed the element: read it again first.
4. **Build** with `twincat_build_plc` and pass the new names in `expectSymbols`, so a symbol the compiler dropped
   is reported. Fix every error before the next step.
5. **Check** code no task reaches with `twincat_plc_check_all_objects`, and run `twincat_plc_static_analysis` when
   the project uses it.
6. Only when asked to bring the change onto a running PLC: `twincat_login` with `mode='online_change'` (it verifies
   the change on the PLC), otherwise stop after the build and report what changed.

Report the changed elements, the build result and anything you could not verify.
