Averigüe por qué el sistema TwinCAT {{target}} no se comporta como se espera. Lea primero, no cambie nada.

1. **Estado**: `twincat_get_status` y `twincat_get_plc_online_state` — XAE, proyecto, System Service, estado del PLC.
2. **Alarmas**: `twincat_read_event_logger` del destino (fuente autorizada para los mensajes de runtime).
3. **Mensajes de compilación y de proyecto**: `twincat_get_error_list` con `minLevel='warning'`.
4. **Fallos**: `twincat_coredump_list` y después `twincat_coredump_analyze` / `twincat_coredump_read_callstack` para el
   más reciente.
5. **Bus de campo**: `twincat_get_fieldbus_topology` y, si el destino responde, `twincat_ethercat_read_identities`.
6. **Valores en vivo** de las señales que nombra la alarma: `twincat_read_ads_variables_batch`; para una cadena de pasos
   `twincat_locate_step` muestra la rama CASE del paso actual.

Indique la causa más probable, las pruebas que la respaldan y el siguiente paso que debería dar una persona. No escriba variables,
ni reinicie ni active nada salvo que se le pida.
