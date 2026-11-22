Die EtherCAT-I/O des geöffneten TwinCAT-Projekts in Betrieb nehmen. Master: {{master}}. Zielsystem: {{target}}.

1. **Konfiguration lesen**: `twincat_get_fieldbus_topology` und, für die Boxen, `twincat_read_ethercat_startup`.
   Nie annehmen, dass eine Box existiert, nur weil ein Projekt sie üblicherweise hat — den Baum lesen.
2. **Mit der Hardware vergleichen**, wenn das Ziel erreichbar ist: `twincat_ethercat_read_identities` (Hersteller,
   Produkt, Revision und Seriennummer pro Slave). Jede Abweichung auflisten, bevor etwas geändert wird.
3. **Boxen ergänzen oder korrigieren**: `twincat_apply_schema` für ein ganzes JSON-I/O-Schema (vorher mit
   `twincat_validate_schema` prüfen), oder `twincat_add_ethercat_box` / `twincat_remove_ethercat_box` /
   `twincat_set_previous_port` für einzelne Boxen; `twincat_configure_ethercat_slave` für Slave-Einstellungen.
4. **Verknüpfen**: die PLC-Variablen mit `twincat_link_variables` mit der I/O verknüpfen; das Ergebnis mit
   `twincat_get_mapping_info` prüfen.
5. **Bauen**: die Lösung (`twincat_build_solution`) bauen und jeden Fehler beheben.
6. **Aktivieren** nur auf Anweisung und nur auf dem genannten Ziel: `twincat_set_target`, dann `twincat_activate_config`.
   Das ändert ein Zielsystem — die Zugriffsrichtlinie muss Runtime-Aktionen darauf erlauben, und ein Mensch wird
   möglicherweise um Bestätigung gebeten. Als `requestKey` eine frische UUID übergeben: nach einem Timeout aktiviert
   derselbe Aufruf mit demselben Schlüssel nie zweimal (`twincat_receipt_get` zeigt, was daraus geworden ist).

Zu melden: die Topologie vorher und nachher, jede in Schritt 2 gefundene Abweichung und was noch einen Menschen vor Ort braucht.
