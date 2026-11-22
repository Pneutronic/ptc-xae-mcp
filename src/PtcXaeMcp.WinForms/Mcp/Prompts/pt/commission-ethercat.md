Colocar em operação o EtherCAT I/O do projeto TwinCAT aberto. Master: {{master}}. Sistema de destino: {{target}}.

1. **Ler o que está configurado**: `twincat_get_fieldbus_topology` e, para as boxes, `twincat_read_ethercat_startup`.
   Nunca presumir que uma box existe só porque um projeto costuma tê-la — ler a árvore.
2. **Comparar com o hardware** quando o destino estiver acessível: `twincat_ethercat_read_identities` (fabricante, produto,
   revisão e número de série por slave). Listar todas as diferenças antes de alterar qualquer coisa.
3. **Adicionar ou corrigir boxes**: `twincat_apply_schema` para um esquema JSON de I/O completo (validá-lo antes com
   `twincat_validate_schema`), ou `twincat_add_ethercat_box` / `twincat_remove_ethercat_box` /
   `twincat_set_previous_port` para boxes individuais; `twincat_configure_ethercat_slave` para as configurações do slave.
4. **Vincular** as variáveis do PLC ao I/O com `twincat_link_variables`; conferir o resultado com
   `twincat_get_mapping_info`.
5. **Compilar** a solução (`twincat_build_solution`) e corrigir todos os erros.
6. **Ativar** somente quando solicitado e somente no destino indicado: `twincat_set_target` e depois `twincat_activate_config`.
   Isso altera um sistema de destino — a política de acesso precisa permitir ações de runtime nele, e uma pessoa pode ser
   consultada para confirmar. Passar um UUID novo como `requestKey`: após um timeout, a mesma chamada com a mesma chave nunca ativa duas vezes
   (`twincat_receipt_get` mostra o que aconteceu com ela).

Relatar a topologia antes e depois, todas as diferenças encontradas na etapa 2 e o que ainda precisa de uma pessoa no local.
