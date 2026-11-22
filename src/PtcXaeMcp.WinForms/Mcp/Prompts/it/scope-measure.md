Registrare questi segnali PLC con il TwinCAT Scope e valutarli: {{symbols}}. Durata: {{seconds}} s.

1. Creare un progetto di misura: `twincat_create_scope_project`, poi un canale per ogni simbolo con
   `twincat_add_scope_channel` (controllarli con `twincat_list_scope_channels`).
2. Impostare il tempo di registrazione con `twincat_set_scope_record_settings`; aggiungere un trigger con
   `twincat_add_scope_trigger_set` se è stata indicata una condizione di avvio.
3. Registrare: `twincat_start_scope_record`, attendere la durata, `twincat_stop_scope_record`
   (`twincat_get_scope_record_state` mostra lo stato).
4. Salvare e leggere i dati: `twincat_save_scope_data`, poi `twincat_read_scope_file` (usare `maxPoints` per le
   registrazioni lunghe).
5. Valutare: minimo, massimo, fronti e i loro istanti, e tutto ciò che è inatteso.

Da riferire: i valori per segnale e dove si trova il file dei dati.
