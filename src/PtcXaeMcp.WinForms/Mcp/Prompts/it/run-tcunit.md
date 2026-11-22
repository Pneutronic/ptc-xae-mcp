Eseguire i test TcUnit del progetto TwinCAT aperto e riferire il risultato.

1. Verificare che la libreria TcUnit sia referenziata: `twincat_list_library_references`.
2. Compilare: `twincat_build_plc`. Prima correggere ogni errore.
3. Portare il programma sul runtime di test: `twincat_activate_config` (oppure `twincat_login` con `mode='download'`,
   seguito da `twincat_plc_start`). Questo modifica un sistema di destinazione — solo sul runtime di test che le è stato
   indicato. Passare un UUID nuovo come `requestKey`, così un nuovo tentativo dopo un timeout non lo esegue due volte.
4. Raccogliere i risultati: `twincat_tcunit_results` attende che il runner li abbia salvati; passare `junitPath` per
   scrivere anche un file XML JUnit per un server CI.
5. Per ogni test fallito, leggere il test e il codice sotto test con `twincat_plc_read` e dire quale elemento e quale
   asserzione sono falliti.

Da riferire: superati / falliti / totale e i fallimenti con i loro elementi.
