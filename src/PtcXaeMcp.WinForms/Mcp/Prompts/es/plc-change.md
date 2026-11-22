Modifique el código PLC del proyecto TwinCAT abierto: {{task}}

Trabaje en pasos pequeños y verificados con las herramientas de ptc-xae-mcp:

1. **Buscar** todos los lugares que afecta el cambio con `twincat_plc_search` (literal o regex, `wholeWord` para identificadores).
   Use `twincat_plc_list` solo para explorar; las etiquetas de los nodos están localizadas, así que nunca adivine una ruta del árbol.
2. **Leer** cada elemento que vaya a modificar con `twincat_plc_read` (varios a la vez con `paths`). Anote la
   `Revision` de cada elemento.
3. **Modificar** con precisión:
   - un fragmento de texto exacto: `twincat_plc_replace_text` con `expectedRevision`;
   - líneas de declaración: `twincat_plc_edit_entries` con `expectedRevision`;
   - una sección completa: `twincat_plc_write` con `expectedRevision`;
   - elementos nuevos: `twincat_plc_create` (pase también `implementation`).
   Una escritura rechazada por una revisión obsoleta significa que otra persona modificó el elemento: léalo de nuevo antes de continuar.
4. **Compilar** con `twincat_build_plc` y pase los nombres nuevos en `expectSymbols`, de modo que se notifique un símbolo
   que el compilador haya descartado. Corrija todos los errores antes del siguiente paso.
5. **Comprobar** el código que ninguna tarea alcanza con `twincat_plc_check_all_objects` y ejecute `twincat_plc_static_analysis` cuando
   el proyecto lo use.
6. Solo si se pide llevar el cambio a un PLC en ejecución: `twincat_login` con `mode='online_change'` (verifica
   el cambio en el PLC); en caso contrario, deténgase tras la compilación e informe de lo que cambió.

Informe de los elementos modificados, del resultado de la compilación y de todo lo que no pudo verificar.
