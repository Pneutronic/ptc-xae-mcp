Poner en marcha la E/S EtherCAT del proyecto TwinCAT abierto. Maestro: {{master}}. Sistema de destino: {{target}}.

1. **Leer lo configurado**: `twincat_get_fieldbus_topology` y, para los módulos, `twincat_read_ethercat_startup`.
   Nunca suponga que existe un módulo porque un proyecto suele tenerlo: lea el árbol.
2. **Comparar con el hardware** cuando el destino sea accesible: `twincat_ethercat_read_identities` (fabricante, producto,
   revisión y número de serie por esclavo). Enumere todas las diferencias antes de cambiar nada.
3. **Añadir o corregir módulos**: `twincat_apply_schema` para un esquema JSON de E/S completo (valídelo antes con
   `twincat_validate_schema`), o `twincat_add_ethercat_box` / `twincat_remove_ethercat_box` /
   `twincat_set_previous_port` para módulos individuales; `twincat_configure_ethercat_slave` para los ajustes del esclavo.
4. **Vincular** las variables PLC con la E/S mediante `twincat_link_variables`; compruebe el resultado con
   `twincat_get_mapping_info`.
5. **Compilar** la solución (`twincat_build_solution`) y corregir todos los errores.
6. **Activar** solo si se solicita y solo en el destino indicado: `twincat_set_target` y después `twincat_activate_config`.
   Esto modifica un sistema de destino: la política de acceso debe permitir acciones de runtime en él, y puede que se
   pida a una persona que lo confirme. Pase un UUID nuevo como `requestKey`: tras un timeout, la misma llamada con la misma clave nunca activa dos veces
   (`twincat_receipt_get` muestra qué ocurrió con ella).

Informe de la topología antes y después, de cada diferencia encontrada en el paso 2 y de lo que aún requiere a una persona in situ.
