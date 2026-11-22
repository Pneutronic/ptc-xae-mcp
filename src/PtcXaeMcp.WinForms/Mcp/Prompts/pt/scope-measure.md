Gravar estes sinais do PLC com o TwinCAT Scope e avaliá-los: {{symbols}}. Duração: {{seconds}} s.

1. Criar um projeto de medição: `twincat_create_scope_project` e depois um canal por símbolo com
   `twincat_add_scope_channel` (conferir com `twincat_list_scope_channels`).
2. Definir o tempo de gravação com `twincat_set_scope_record_settings`; adicionar um trigger com `twincat_add_scope_trigger_set`
   quando uma condição de início tiver sido indicada.
3. Gravar: `twincat_start_scope_record`, aguardar a duração, `twincat_stop_scope_record`
   (`twincat_get_scope_record_state` mostra o estado).
4. Salvar e ler os dados: `twincat_save_scope_data` e depois `twincat_read_scope_file` (usar `maxPoints` para gravações
   longas).
5. Avaliar: mínimo, máximo, bordas e seus instantes, e qualquer coisa inesperada.

Relatar os valores por sinal e onde está o arquivo de dados.
