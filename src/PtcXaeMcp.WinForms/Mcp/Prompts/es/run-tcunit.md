Ejecute las pruebas TcUnit del proyecto TwinCAT abierto e informe del resultado.

1. Compruebe que la biblioteca TcUnit está referenciada: `twincat_list_library_references`.
2. Compile: `twincat_build_plc`. Corrija primero todos los errores.
3. Lleve el programa al runtime de pruebas: `twincat_activate_config` (o `twincat_login` con `mode='download'`
   seguido de `twincat_plc_start`). Esto modifica un sistema de destino: solo en el runtime de pruebas que se le indicó. Pase un
   UUID nuevo como `requestKey`, para que un reintento tras un timeout no lo ejecute dos veces.
4. Recoja los resultados: `twincat_tcunit_results` espera hasta que el ejecutor los haya almacenado; pase `junitPath` para escribir además
   un archivo XML JUnit para un servidor de CI.
5. Para cada prueba fallida, lea la prueba y el código bajo prueba con `twincat_plc_read` e indique qué elemento y
   qué aserción fallaron.

Informe de aprobadas / fallidas / total y de los fallos con sus elementos.
