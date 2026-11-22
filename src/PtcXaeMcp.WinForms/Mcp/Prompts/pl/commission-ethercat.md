Uruchomić (commissioning) wejścia/wyjścia EtherCAT otwartego projektu TwinCAT. Master: {{master}}. System docelowy: {{target}}.

1. **Odczytać skonfigurowany stan**: `twincat_get_fieldbus_topology` oraz, dla boxów, `twincat_read_ethercat_startup`.
   Nigdy nie zakładać, że box istnieje tylko dlatego, że projekt zwykle go zawiera — odczytać drzewo.
2. **Porównać ze sprzętem**, gdy system docelowy jest osiągalny: `twincat_ethercat_read_identities` (producent, produkt,
   rewizja i numer seryjny każdego slave). Wypisać każdą różnicę przed jakąkolwiek zmianą.
3. **Dodać lub poprawić boxy**: `twincat_apply_schema` dla całego schematu I/O w JSON (najpierw sprawdzić go przez
   `twincat_validate_schema`) albo `twincat_add_ethercat_box` / `twincat_remove_ethercat_box` /
   `twincat_set_previous_port` dla pojedynczych boxów; `twincat_configure_ethercat_slave` dla ustawień slave.
4. **Połączyć** zmienne PLC z I/O przez `twincat_link_variables`; sprawdzić wynik przez
   `twincat_get_mapping_info`.
5. **Zbudować** rozwiązanie (`twincat_build_solution`) i naprawić każdy błąd.
6. **Aktywować** tylko na wyraźne polecenie i tylko na wskazanym systemie docelowym: `twincat_set_target`, następnie `twincat_activate_config`.
   To zmienia system docelowy — zasady dostępu muszą zezwalać na akcje runtime na nim, a człowiek może zostać poproszony
   o potwierdzenie. Przekazać świeży UUID jako `requestKey`: po przekroczeniu czasu to samo wywołanie z tym samym kluczem nigdy nie aktywuje dwukrotnie
   (`twincat_receipt_get` pokazuje, co się z nim stało).

Zraportować topologię przed i po, każdą różnicę znalezioną w kroku 2 oraz to, co nadal wymaga człowieka na miejscu.
