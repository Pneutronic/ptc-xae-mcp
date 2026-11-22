Zoek uit waarom het TwinCAT-systeem {{target}} zich niet gedraagt zoals verwacht. Eerst lezen, niets wijzigen.

1. **Status**: `twincat_get_status` en `twincat_get_plc_online_state` — XAE, project, System Service, PLC-status.
2. **Alarmen**: `twincat_read_event_logger` voor het doelsysteem (leidend voor runtimemeldingen).
3. **Build- en projectmeldingen**: `twincat_get_error_list` met `minLevel='warning'`.
4. **Crashes**: `twincat_coredump_list`, daarna `twincat_coredump_analyze` / `twincat_coredump_read_callstack` voor de
   nieuwste.
5. **Fieldbus**: `twincat_get_fieldbus_topology` en, wanneer het doelsysteem antwoordt, `twincat_ethercat_read_identities`.
6. **Live waarden** van de signalen die het alarm noemt: `twincat_read_ads_variables_batch`; voor een stappenketen
   toont `twincat_locate_step` de CASE-tak van de huidige stap.

Noem de meest waarschijnlijke oorzaak, het bewijs ervoor en de volgende stap die een persoon moet nemen. Schrijf geen variabelen,
herstart of activeer niets, tenzij daarom wordt gevraagd.
