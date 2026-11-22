Enregistrer ces signaux PLC avec le TwinCAT Scope et les évaluer : {{symbols}}. Durée : {{seconds}} s.

1. Créer un projet de mesure : `twincat_create_scope_project`, puis un canal par symbole avec
   `twincat_add_scope_channel` (les vérifier avec `twincat_list_scope_channels`).
2. Régler la durée d'enregistrement avec `twincat_set_scope_record_settings` ; ajouter un déclencheur avec
   `twincat_add_scope_trigger_set` si une condition de départ a été nommée.
3. Enregistrer : `twincat_start_scope_record`, attendre la durée, `twincat_stop_scope_record`
   (`twincat_get_scope_record_state` montre l'état).
4. Sauvegarder et lire les données : `twincat_save_scope_data`, puis `twincat_read_scope_file` (utiliser `maxPoints` pour
   les longs enregistrements).
5. Évaluer : minimum, maximum, fronts et leurs instants, et tout ce qui est inattendu.

À rapporter : les valeurs par signal et l'emplacement du fichier de données.
