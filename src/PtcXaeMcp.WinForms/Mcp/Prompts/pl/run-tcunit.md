Uruchomić testy TcUnit otwartego projektu TwinCAT i zraportować wynik.

1. Sprawdzić, czy biblioteka TcUnit jest przywołana w projekcie: `twincat_list_library_references`.
2. Zbudować: `twincat_build_plc`. Najpierw naprawić każdy błąd.
3. Wprowadzić program na testowy runtime: `twincat_activate_config` (albo `twincat_login` z `mode='download'`,
   a potem `twincat_plc_start`). To zmienia system docelowy — tylko na testowym runtime, który został wskazany. Przekazać
   świeży UUID jako `requestKey`, aby ponowna próba po przekroczeniu czasu nie wykonała tego dwukrotnie.
4. Zebrać wyniki: `twincat_tcunit_results` czeka, aż runner je zapisze; przekazać `junitPath`, aby dodatkowo
   zapisać plik JUnit XML dla serwera CI.
5. Dla każdego nieudanego testu odczytać test i testowany kod przez `twincat_plc_read` i podać, który element oraz
   która asercja nie powiodły się.

Zraportować zaliczone / niezaliczone / razem oraz niepowodzenia wraz z ich elementami.
