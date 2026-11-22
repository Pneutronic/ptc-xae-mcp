Registreer deze PLC-signalen met de TwinCAT Scope en evalueer ze: {{symbols}}. Duur: {{seconds}} s.

1. Maak een meetproject: `twincat_create_scope_project`, daarna één kanaal per symbool met
   `twincat_add_scope_channel` (controleer ze met `twincat_list_scope_channels`).
2. Stel de opnameduur in met `twincat_set_scope_record_settings`; voeg een trigger toe met `twincat_add_scope_trigger_set`
   wanneer een startvoorwaarde is genoemd.
3. Neem op: `twincat_start_scope_record`, wacht de duur af, `twincat_stop_scope_record`
   (`twincat_get_scope_record_state` toont de status).
4. Sla de gegevens op en lees ze: `twincat_save_scope_data`, daarna `twincat_read_scope_file` (gebruik `maxPoints` voor lange
   opnamen).
5. Evalueer: minimum, maximum, flanken en hun tijdstippen, en alles wat onverwacht is.

Rapporteer de waarden per signaal en waar het gegevensbestand staat.
