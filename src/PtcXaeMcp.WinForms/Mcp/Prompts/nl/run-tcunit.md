Voer de TcUnit-tests van het geopende TwinCAT-project uit en rapporteer het resultaat.

1. Controleer of de TcUnit-bibliotheek als referentie is toegevoegd: `twincat_list_library_references`.
2. Bouw: `twincat_build_plc`. Los eerst elke fout op.
3. Breng het programma naar de testruntime: `twincat_activate_config` (of `twincat_login` met `mode='download'`
   gevolgd door `twincat_plc_start`). Dit wijzigt een doelsysteem — alleen op de testruntime die u is opgegeven. Geef een
   nieuwe UUID mee als `requestKey`, zodat een nieuwe poging na een time-out het niet twee keer uitvoert.
4. Verzamel de resultaten: `twincat_tcunit_results` wacht tot de runner ze heeft opgeslagen; geef `junitPath` mee om ook
   een JUnit-XML-bestand voor een CI-server te schrijven.
5. Lees voor elke mislukte test de test en de geteste code met `twincat_plc_read` en zeg welk element en
   welke assertion is mislukt.

Rapporteer geslaagd / mislukt / totaal en de mislukkingen met hun elementen.
