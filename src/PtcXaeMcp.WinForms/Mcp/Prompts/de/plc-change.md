Den PLC-Code des geöffneten TwinCAT-Projekts ändern: {{task}}

In kleinen, überprüften Schritten mit den ptc-xae-mcp-Tools arbeiten:

1. **Suchen**: jede Stelle, die die Änderung berührt, mit `twincat_plc_search` finden (literal oder Regex, `wholeWord` für Bezeichner).
   `twincat_plc_list` nur zum Durchsuchen verwenden; Knotenbezeichnungen sind lokalisiert, daher nie einen Baumpfad raten.
2. **Lesen**: jedes Element, das geändert wird, mit `twincat_plc_read` lesen (mehrere auf einmal mit `paths`). Die
   `Revision` jedes Elements notieren.
3. **Ändern**, präzise:
   - ein genauer Textabschnitt: `twincat_plc_replace_text` mit `expectedRevision`;
   - Deklarationszeilen: `twincat_plc_edit_entries` mit `expectedRevision`;
   - ein ganzer Abschnitt: `twincat_plc_write` mit `expectedRevision`;
   - neue Elemente: `twincat_plc_create` (`implementation` mitgeben).
   Ein abgelehntes Schreiben wegen veralteter Revision bedeutet, dass jemand anderes das Element geändert hat: zuerst erneut lesen.
4. **Bauen** mit `twincat_build_plc` und die neuen Namen in `expectSymbols` übergeben, damit ein vom Compiler
   verworfenes Symbol gemeldet wird. Jeden Fehler vor dem nächsten Schritt beheben.
5. **Prüfen**: Code, den keine Task erreicht, mit `twincat_plc_check_all_objects` prüfen und `twincat_plc_static_analysis`
   ausführen, wenn das Projekt sie verwendet.
6. Nur wenn verlangt wird, die Änderung auf eine laufende PLC zu bringen: `twincat_login` mit `mode='online_change'` (es überprüft
   die Änderung auf der PLC), andernfalls nach dem Build anhalten und melden, was sich geändert hat.

Zu melden: die geänderten Elemente, das Build-Ergebnis und alles, was nicht überprüft werden konnte.
