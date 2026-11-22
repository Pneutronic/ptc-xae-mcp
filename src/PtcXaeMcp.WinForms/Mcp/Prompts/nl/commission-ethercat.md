Neem de EtherCAT-I/O van het geopende TwinCAT-project in bedrijf. Master: {{master}}. Doelsysteem: {{target}}.

1. **Lees wat is geconfigureerd**: `twincat_get_fieldbus_topology` en, voor de boxen, `twincat_read_ethercat_startup`.
   Ga er nooit van uit dat een box bestaat omdat een project die gewoonlijk heeft — lees de boom.
2. **Vergelijk met de hardware** wanneer het doelsysteem bereikbaar is: `twincat_ethercat_read_identities` (vendor, product,
   revisie en serienummer per slave). Noem elk verschil voordat u iets wijzigt.
3. **Boxen toevoegen of corrigeren**: `twincat_apply_schema` voor een compleet JSON-I/O-schema (valideer het eerst met
   `twincat_validate_schema`), of `twincat_add_ethercat_box` / `twincat_remove_ethercat_box` /
   `twincat_set_previous_port` voor afzonderlijke boxen; `twincat_configure_ethercat_slave` voor slave-instellingen.
4. **Koppel** de PLC-variabelen aan de I/O met `twincat_link_variables`; controleer het resultaat met
   `twincat_get_mapping_info`.
5. **Bouw** de solution (`twincat_build_solution`) en los elke fout op.
6. **Activeer** alleen op verzoek en alleen op het genoemde doelsysteem: `twincat_set_target`, daarna `twincat_activate_config`.
   Dit wijzigt een doelsysteem — het toegangsbeleid moet er runtime-acties voor toestaan, en een mens kan om
   bevestiging worden gevraagd. Geef een nieuwe UUID mee als `requestKey`: na een time-out activeert dezelfde aanroep met dezelfde sleutel
   nooit twee keer (`twincat_receipt_get` toont wat ervan geworden is).

Rapporteer de topologie voor en na, elk verschil uit stap 2 en wat nog een persoon ter plaatse nodig heeft.
