Diese PLC-Signale mit dem TwinCAT Scope aufzeichnen und auswerten: {{symbols}}. Dauer: {{seconds}} s.

1. Ein Messprojekt erstellen: `twincat_create_scope_project`, dann pro Symbol einen Kanal mit
   `twincat_add_scope_channel` (mit `twincat_list_scope_channels` prüfen).
2. Die Aufzeichnungszeit mit `twincat_set_scope_record_settings` setzen; mit `twincat_add_scope_trigger_set` einen Trigger
   hinzufügen, wenn eine Startbedingung genannt wurde.
3. Aufzeichnen: `twincat_start_scope_record`, die Dauer abwarten, `twincat_stop_scope_record`
   (`twincat_get_scope_record_state` zeigt den Zustand).
4. Die Daten speichern und lesen: `twincat_save_scope_data`, dann `twincat_read_scope_file` (`maxPoints` für lange
   Aufzeichnungen verwenden).
5. Auswerten: Minimum, Maximum, Flanken und ihre Zeitpunkte sowie alles Unerwartete.

Zu melden: die Werte pro Signal und wo die Datendatei liegt.
