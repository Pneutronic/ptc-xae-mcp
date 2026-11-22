Descobrir por que o sistema TwinCAT {{target}} não se comporta como esperado. Primeiro ler, não alterar nada.

1. **Status**: `twincat_get_status` e `twincat_get_plc_online_state` — XAE, projeto, System Service, estado do PLC.
2. **Alarmes**: `twincat_read_event_logger` para o destino (autoritativo para mensagens de runtime).
3. **Mensagens de compilação e do projeto**: `twincat_get_error_list` com `minLevel='warning'`.
4. **Falhas**: `twincat_coredump_list` e depois `twincat_coredump_analyze` / `twincat_coredump_read_callstack` para o
   mais recente.
5. **Fieldbus**: `twincat_get_fieldbus_topology` e, quando o destino responder, `twincat_ethercat_read_identities`.
6. **Valores ao vivo** dos sinais que o alarme cita: `twincat_read_ads_variables_batch`; para uma cadeia de etapas
   `twincat_locate_step` mostra o ramo CASE da etapa atual.

Indicar a causa mais provável, as evidências a favor dela e o próximo passo que uma pessoa deve dar. Não gravar variáveis,
reiniciar nem ativar nada, a menos que seja solicitado.
