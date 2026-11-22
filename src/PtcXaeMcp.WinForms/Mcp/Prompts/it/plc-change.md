Modificare il codice PLC del progetto TwinCAT aperto: {{task}}

Lavorare a piccoli passi verificati con gli strumenti ptc-xae-mcp:

1. **Cercare** ogni punto toccato dalla modifica con `twincat_plc_search` (letterale o regex, `wholeWord` per gli identificatori).
   Usare `twincat_plc_list` solo per sfogliare; le etichette dei nodi sono localizzate, quindi non indovinare mai un percorso nell'albero.
2. **Leggere** ogni elemento da modificare con `twincat_plc_read` (più elementi insieme con `paths`). Annotare la
   `Revision` di ogni elemento.
3. **Modificare** con precisione:
   - un frammento di testo esatto: `twincat_plc_replace_text` con `expectedRevision`;
   - righe di dichiarazione: `twincat_plc_edit_entries` con `expectedRevision`;
   - una sezione intera: `twincat_plc_write` con `expectedRevision`;
   - nuovi elementi: `twincat_plc_create` (passare anche `implementation`).
   Una scrittura rifiutata per revisione obsoleta significa che qualcun altro ha modificato l'elemento: rileggerlo prima.
4. **Compilare** con `twincat_build_plc` passando i nuovi nomi in `expectSymbols`, così un simbolo scartato dal
   compilatore viene segnalato. Correggere ogni errore prima del passo successivo.
5. **Controllare** il codice che nessun task raggiunge con `twincat_plc_check_all_objects` ed eseguire `twincat_plc_static_analysis`
   quando il progetto la usa.
6. Solo su richiesta di portare la modifica su un PLC in esecuzione: `twincat_login` con `mode='online_change'` (verifica
   la modifica sul PLC); altrimenti fermarsi dopo la compilazione e riferire cosa è cambiato.

Da riferire: gli elementi modificati, il risultato della compilazione e tutto ciò che non è stato possibile verificare.
