Die TcUnit-Tests des geöffneten TwinCAT-Projekts ausführen und das Ergebnis melden.

1. Prüfen, ob die TcUnit-Bibliothek referenziert ist: `twincat_list_library_references`.
2. Build: `twincat_build_plc`. Zuerst jeden Fehler beheben.
3. Das Programm auf die Test-Runtime bringen: `twincat_activate_config` (oder `twincat_login` mit `mode='download'`,
   danach `twincat_plc_start`). Das ändert ein Zielsystem — nur auf der Test-Runtime, die vorgegeben wurde. Als
   `requestKey` eine frische UUID übergeben, damit ein Wiederholungsversuch nach einem Timeout es nicht zweimal ausführt.
4. Die Ergebnisse einsammeln: `twincat_tcunit_results` wartet, bis der Runner sie gespeichert hat; mit `junitPath` zusätzlich
   eine JUnit-XML-Datei für einen CI-Server schreiben.
5. Für jeden fehlgeschlagenen Test den Test und den getesteten Code mit `twincat_plc_read` lesen und sagen, welches Element und
   welche Assertion fehlgeschlagen ist.

Zu melden: bestanden / fehlgeschlagen / gesamt und die Fehlschläge mit ihren Elementen.
