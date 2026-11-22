Scoprire perché il sistema TwinCAT {{target}} non si comporta come previsto. Prima leggere, non modificare nulla.

1. **Stato**: `twincat_get_status` e `twincat_get_plc_online_state` — XAE, progetto, System Service, stato del PLC.
2. **Allarmi**: `twincat_read_event_logger` per la destinazione (fa fede per i messaggi di runtime).
3. **Messaggi di compilazione e di progetto**: `twincat_get_error_list` con `minLevel='warning'`.
4. **Arresti anomali**: `twincat_coredump_list`, poi `twincat_coredump_analyze` / `twincat_coredump_read_callstack` per il
   più recente.
5. **Bus di campo**: `twincat_get_fieldbus_topology` e, quando la destinazione risponde, `twincat_ethercat_read_identities`.
6. **Valori in tempo reale** dei segnali che l'allarme nomina: `twincat_read_ads_variables_batch`; per una catena di passi
   `twincat_locate_step` mostra il ramo CASE del passo corrente.

Indicare la causa più probabile, le prove a suo favore e il prossimo passo che una persona dovrebbe compiere. Non scrivere
variabili, non riavviare né attivare nulla, salvo richiesta.
