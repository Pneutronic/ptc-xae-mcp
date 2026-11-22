Ustalić, dlaczego system TwinCAT {{target}} nie zachowuje się zgodnie z oczekiwaniami. Najpierw odczyt, niczego nie zmieniać.

1. **Status**: `twincat_get_status` oraz `twincat_get_plc_online_state` — XAE, projekt, System Service, stan PLC.
2. **Alarmy**: `twincat_read_event_logger` dla systemu docelowego (wiarygodne źródło komunikatów runtime).
3. **Komunikaty kompilacji i projektu**: `twincat_get_error_list` z `minLevel='warning'`.
4. **Awarie**: `twincat_coredump_list`, następnie `twincat_coredump_analyze` / `twincat_coredump_read_callstack` dla
   najnowszej.
5. **Fieldbus**: `twincat_get_fieldbus_topology` oraz, gdy system docelowy odpowiada, `twincat_ethercat_read_identities`.
6. **Wartości bieżące** sygnałów wymienionych w alarmie: `twincat_read_ads_variables_batch`; dla łańcucha kroków
   `twincat_locate_step` pokazuje gałąź CASE bieżącego kroku.

Wskazać najbardziej prawdopodobną przyczynę, dowody na nią i kolejny krok, który powinien wykonać człowiek. Niczego nie zapisywać do zmiennych,
nie restartować ani nie aktywować, chyba że zostanie to polecone.
