Registre estas señales PLC con el Scope de TwinCAT y evalúelas: {{symbols}}. Duración: {{seconds}} s.

1. Cree un proyecto de medición: `twincat_create_scope_project` y después un canal por símbolo con
   `twincat_add_scope_channel` (compruébelos con `twincat_list_scope_channels`).
2. Defina el tiempo de registro con `twincat_set_scope_record_settings`; añada un disparador con `twincat_add_scope_trigger_set`
   cuando se haya indicado una condición de inicio.
3. Registre: `twincat_start_scope_record`, espere la duración indicada, `twincat_stop_scope_record`
   (`twincat_get_scope_record_state` muestra el estado).
4. Guarde y lea los datos: `twincat_save_scope_data` y después `twincat_read_scope_file` (use `maxPoints` para registros
   largos).
5. Evalúe: mínimo, máximo, flancos y sus instantes, y todo lo inesperado.

Informe de los valores por señal y de dónde se encuentra el archivo de datos.
