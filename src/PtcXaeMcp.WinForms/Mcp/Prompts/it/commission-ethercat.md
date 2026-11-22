Mettere in servizio l'I/O EtherCAT del progetto TwinCAT aperto. Master: {{master}}. Sistema di destinazione: {{target}}.

1. **Leggere cosa è configurato**: `twincat_get_fieldbus_topology` e, per le box, `twincat_read_ethercat_startup`.
   Non presumere mai che una box esista solo perché un progetto di solito la ha — leggere l'albero.
2. **Confrontare con l'hardware** quando la destinazione è raggiungibile: `twincat_ethercat_read_identities` (produttore,
   prodotto, revisione e numero di serie per ogni slave). Elencare ogni differenza prima di modificare qualsiasi cosa.
3. **Aggiungere o correggere box**: `twincat_apply_schema` per uno schema I/O JSON completo (convalidarlo prima con
   `twincat_validate_schema`), oppure `twincat_add_ethercat_box` / `twincat_remove_ethercat_box` /
   `twincat_set_previous_port` per singole box; `twincat_configure_ethercat_slave` per le impostazioni dello slave.
4. **Collegare** le variabili PLC all'I/O con `twincat_link_variables`; controllare il risultato con
   `twincat_get_mapping_info`.
5. **Compilare** la soluzione (`twincat_build_solution`) e correggere ogni errore.
6. **Attivare** solo su richiesta e solo sulla destinazione indicata: `twincat_set_target`, poi `twincat_activate_config`.
   Questo modifica un sistema di destinazione — la policy di accesso deve consentirvi azioni di runtime, e a una persona
   può essere chiesto di confermarlo. Passare un UUID nuovo come `requestKey`: dopo un timeout la stessa chiamata con la
   stessa chiave non attiva mai due volte (`twincat_receipt_get` mostra com'è andata a finire).

Da riferire: la topologia prima e dopo, ogni differenza trovata al passo 2 e ciò che richiede ancora una persona sul posto.
