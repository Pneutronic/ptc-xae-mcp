Herausfinden, warum sich das TwinCAT-System {{target}} nicht wie erwartet verhält. Zuerst lesen, nichts ändern.

1. **Status**: `twincat_get_status` und `twincat_get_plc_online_state` — XAE, Projekt, System Service, PLC-Status.
2. **Alarme**: `twincat_read_event_logger` für das Ziel (massgebend für Laufzeitmeldungen).
3. **Build- und Projektmeldungen**: `twincat_get_error_list` mit `minLevel='warning'`.
4. **Abstürze**: `twincat_coredump_list`, dann `twincat_coredump_analyze` / `twincat_coredump_read_callstack` für den
   neuesten.
5. **Feldbus**: `twincat_get_fieldbus_topology` und, wenn das Ziel antwortet, `twincat_ethercat_read_identities`.
6. **Live-Werte** der Signale, die der Alarm nennt: `twincat_read_ads_variables_batch`; bei einer Schrittkette
   zeigt `twincat_locate_step` den CASE-Zweig des aktuellen Schritts.

Die wahrscheinlichste Ursache, die Belege dafür und den nächsten Schritt nennen, den ein Mensch tun sollte. Keine
Variablen schreiben und nichts neu starten oder aktivieren, ausser auf Anweisung.
