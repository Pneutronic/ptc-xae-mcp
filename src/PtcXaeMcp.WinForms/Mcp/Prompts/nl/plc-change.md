Wijzig de PLC-code van het geopende TwinCAT-project: {{task}}

Werk in kleine, geverifieerde stappen met de ptc-xae-mcp-tools:

1. **Zoek** elke plek die de wijziging raakt met `twincat_plc_search` (letterlijk of regex, `wholeWord` voor identifiers).
   Gebruik `twincat_plc_list` alleen om te bladeren; knooplabels zijn gelokaliseerd, dus raad nooit een boompad.
2. **Lees** elk element dat u gaat wijzigen met `twincat_plc_read` (meerdere tegelijk met `paths`). Noteer de
   `Revision` van elk element.
3. **Wijzig** nauwkeurig:
   - één exact stuk tekst: `twincat_plc_replace_text` met `expectedRevision`;
   - declaratieregels: `twincat_plc_edit_entries` met `expectedRevision`;
   - een volledig onderdeel: `twincat_plc_write` met `expectedRevision`;
   - nieuwe elementen: `twincat_plc_create` (geef `implementation` mee).
   Een geweigerde schrijfactie wegens een verouderde revisie betekent dat iemand anders het element heeft gewijzigd: lees het eerst opnieuw.
4. **Bouw** met `twincat_build_plc` en geef de nieuwe namen mee in `expectSymbols`, zodat een symbool dat de compiler heeft
   weggelaten wordt gemeld. Los elke fout op vóór de volgende stap.
5. **Controleer** code die geen enkele task bereikt met `twincat_plc_check_all_objects`, en voer `twincat_plc_static_analysis` uit wanneer
   het project dat gebruikt.
6. Alleen wanneer gevraagd wordt de wijziging naar een draaiende PLC te brengen: `twincat_login` met `mode='online_change'` (het verifieert
   de wijziging op de PLC); stop anders na de build en rapporteer wat er is gewijzigd.

Rapporteer de gewijzigde elementen, het buildresultaat en alles wat u niet kon verifiëren.
