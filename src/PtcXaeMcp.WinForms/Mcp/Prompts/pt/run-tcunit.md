Executar os testes TcUnit do projeto TwinCAT aberto e relatar o resultado.

1. Verificar se a biblioteca TcUnit está referenciada: `twincat_list_library_references`.
2. Compilar: `twincat_build_plc`. Corrigir primeiro todos os erros.
3. Levar o programa para o runtime de teste: `twincat_activate_config` (ou `twincat_login` com `mode='download'`
   seguido de `twincat_plc_start`). Isso altera um sistema de destino — somente no runtime de teste informado. Passar um
   UUID novo como `requestKey`, para que uma nova tentativa após um timeout não o execute duas vezes.
4. Coletar os resultados: `twincat_tcunit_results` aguarda até que o runner os tenha armazenado; passar `junitPath` para também
   gravar um arquivo JUnit XML para um servidor de CI.
5. Para cada teste que falhou, ler o teste e o código testado com `twincat_plc_read` e dizer qual elemento e
   qual asserção falhou.

Relatar aprovados / reprovados / total e as falhas com seus elementos.
