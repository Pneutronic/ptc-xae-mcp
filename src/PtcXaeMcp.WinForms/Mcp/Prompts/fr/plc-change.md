Modifier le code PLC du projet TwinCAT ouvert : {{task}}

Travailler par petites étapes vérifiées avec les outils ptc-xae-mcp :

1. **Chercher** chaque endroit concerné par la modification avec `twincat_plc_search` (littéral ou regex, `wholeWord` pour les identifiants).
   N'utiliser `twincat_plc_list` que pour parcourir ; les libellés des nœuds sont localisés, donc ne jamais deviner un chemin dans l'arborescence.
2. **Lire** chaque élément à modifier avec `twincat_plc_read` (plusieurs à la fois avec `paths`). Noter la
   `Revision` de chaque élément.
3. **Modifier** avec précision :
   - un morceau de texte exact : `twincat_plc_replace_text` avec `expectedRevision` ;
   - des lignes de déclaration : `twincat_plc_edit_entries` avec `expectedRevision` ;
   - une section entière : `twincat_plc_write` avec `expectedRevision` ;
   - de nouveaux éléments : `twincat_plc_create` (passer `implementation` en même temps).
   Une écriture refusée pour cause de révision périmée signifie que quelqu'un d'autre a modifié l'élément : le relire d'abord.
4. **Compiler** avec `twincat_build_plc` en passant les nouveaux noms dans `expectSymbols`, afin qu'un symbole supprimé
   par le compilateur soit signalé. Corriger chaque erreur avant l'étape suivante.
5. **Contrôler** le code qu'aucune tâche n'atteint avec `twincat_plc_check_all_objects`, et lancer `twincat_plc_static_analysis`
   lorsque le projet l'utilise.
6. Uniquement sur demande de porter la modification sur un PLC en marche : `twincat_login` avec `mode='online_change'` (il vérifie
   la modification sur le PLC) ; sinon, s'arrêter après la compilation et rapporter ce qui a changé.

À rapporter : les éléments modifiés, le résultat de la compilation et tout ce qui n'a pas pu être vérifié.
