Trouver pourquoi le système TwinCAT {{target}} ne se comporte pas comme prévu. Lire d'abord, ne rien modifier.

1. **Statut** : `twincat_get_status` et `twincat_get_plc_online_state` — XAE, projet, System Service, état du PLC.
2. **Alarmes** : `twincat_read_event_logger` pour la cible (référence pour les messages d'exécution).
3. **Messages de compilation et de projet** : `twincat_get_error_list` avec `minLevel='warning'`.
4. **Plantages** : `twincat_coredump_list`, puis `twincat_coredump_analyze` / `twincat_coredump_read_callstack` pour le
   plus récent.
5. **Bus de terrain** : `twincat_get_fieldbus_topology` et, lorsque la cible répond, `twincat_ethercat_read_identities`.
6. **Valeurs en direct** des signaux que nomme l'alarme : `twincat_read_ads_variables_batch` ; pour une chaîne d'étapes,
   `twincat_locate_step` montre la branche CASE de l'étape courante.

Nommer la cause la plus probable, les preuves en sa faveur et la prochaine étape qu'une personne devrait suivre. Ne pas
écrire de variables, ne rien redémarrer ni activer, sauf demande.
