Alterar o código PLC do projeto TwinCAT aberto: {{task}}

Trabalhar em etapas pequenas e verificadas com as ferramentas do ptc-xae-mcp:

1. **Localizar** todos os pontos que a alteração toca com `twincat_plc_search` (literal ou regex, `wholeWord` para identificadores).
   Usar `twincat_plc_list` somente para navegar; os rótulos dos nós são localizados, portanto nunca adivinhar um caminho da árvore.
2. **Ler** cada elemento que será alterado com `twincat_plc_read` (vários de uma vez com `paths`). Anotar o
   `Revision` de cada elemento.
3. **Alterar** com precisão:
   - um trecho exato de texto: `twincat_plc_replace_text` com `expectedRevision`;
   - linhas de declaração: `twincat_plc_edit_entries` com `expectedRevision`;
   - uma seção inteira: `twincat_plc_write` com `expectedRevision`;
   - novos elementos: `twincat_plc_create` (passar `implementation` junto).
   Uma gravação recusada por revisão desatualizada significa que outra pessoa alterou o elemento: ler novamente antes de tudo.
4. **Compilar** com `twincat_build_plc` e passar os novos nomes em `expectSymbols`, para que um símbolo descartado pelo compilador
   seja relatado. Corrigir todos os erros antes da próxima etapa.
5. **Verificar** o código que nenhuma tarefa alcança com `twincat_plc_check_all_objects` e executar `twincat_plc_static_analysis` quando
   o projeto o utiliza.
6. Somente quando solicitado a levar a alteração para um PLC em execução: `twincat_login` com `mode='online_change'` (ele verifica
   a alteração no PLC); caso contrário, parar após a compilação e relatar o que mudou.

Relatar os elementos alterados, o resultado da compilação e tudo o que não pôde ser verificado.
