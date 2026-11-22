Mettre en service les E/S EtherCAT du projet TwinCAT ouvert. Master : {{master}}. Système cible : {{target}}.

1. **Lire la configuration** : `twincat_get_fieldbus_topology` et, pour les box, `twincat_read_ethercat_startup`.
   Ne jamais supposer qu'une box existe parce qu'un projet en a habituellement une — lire l'arborescence.
2. **Comparer avec le matériel** lorsque la cible est joignable : `twincat_ethercat_read_identities` (fabricant,
   produit, révision et numéro de série par esclave). Lister chaque différence avant de modifier quoi que ce soit.
3. **Ajouter ou corriger des box** : `twincat_apply_schema` pour un schéma d'E/S JSON complet (le valider d'abord avec
   `twincat_validate_schema`), ou `twincat_add_ethercat_box` / `twincat_remove_ethercat_box` /
   `twincat_set_previous_port` pour des box individuelles ; `twincat_configure_ethercat_slave` pour les paramètres d'esclave.
4. **Lier** les variables PLC aux E/S avec `twincat_link_variables` ; vérifier le résultat avec
   `twincat_get_mapping_info`.
5. **Compiler** la solution (`twincat_build_solution`) et corriger chaque erreur.
6. **Activer** uniquement sur demande et uniquement sur la cible nommée : `twincat_set_target`, puis `twincat_activate_config`.
   Cela modifie un système cible — la politique d'accès doit y autoriser les actions d'exécution, et une personne peut
   être invitée à le confirmer. Passer un UUID neuf comme `requestKey` : après un délai dépassé, le même appel avec la
   même clé n'active jamais deux fois (`twincat_receipt_get` montre ce qu'il est advenu).

À rapporter : la topologie avant et après, chaque différence trouvée à l'étape 2 et ce qui nécessite encore une personne sur site.
