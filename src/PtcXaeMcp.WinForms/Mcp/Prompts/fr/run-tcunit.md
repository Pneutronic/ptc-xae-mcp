Exécuter les tests TcUnit du projet TwinCAT ouvert et rapporter le résultat.

1. Vérifier que la bibliothèque TcUnit est référencée : `twincat_list_library_references`.
2. Compiler : `twincat_build_plc`. Corriger d'abord chaque erreur.
3. Amener le programme sur le runtime de test : `twincat_activate_config` (ou `twincat_login` avec `mode='download'`,
   suivi de `twincat_plc_start`). Cela modifie un système cible — uniquement sur le runtime de test qui vous a été donné.
   Passer un UUID neuf comme `requestKey`, afin qu'une nouvelle tentative après un délai dépassé ne l'exécute pas deux fois.
4. Recueillir les résultats : `twincat_tcunit_results` attend que le runner les ait enregistrés ; passer `junitPath` pour
   écrire en plus un fichier XML JUnit destiné à un serveur CI.
5. Pour chaque test en échec, lire le test et le code testé avec `twincat_plc_read` et dire quel élément et quelle
   assertion ont échoué.

À rapporter : réussis / échoués / total, et les échecs avec leurs éléments.
