Zmienić kod PLC otwartego projektu TwinCAT: {{task}}

Pracować małymi, weryfikowanymi krokami z narzędziami ptc-xae-mcp:

1. **Znaleźć** każde miejsce, którego dotyczy zmiana, przez `twincat_plc_search` (tekst dosłowny lub regex, `wholeWord` dla identyfikatorów).
   `twincat_plc_list` używać tylko do przeglądania; etykiety węzłów są zlokalizowane, więc nigdy nie zgadywać ścieżki w drzewie.
2. **Odczytać** każdy element, który zostanie zmieniony, przez `twincat_plc_read` (kilka naraz przez `paths`). Zanotować
   `Revision` każdego elementu.
3. **Zmieniać** precyzyjnie:
   - jeden dokładny fragment tekstu: `twincat_plc_replace_text` z `expectedRevision`;
   - wiersze deklaracji: `twincat_plc_edit_entries` z `expectedRevision`;
   - całą sekcję: `twincat_plc_write` z `expectedRevision`;
   - nowe elementy: `twincat_plc_create` (przekazać też `implementation`).
   Odrzucony zapis z powodu nieaktualnej rewizji oznacza, że ktoś inny zmienił element: najpierw odczytać go ponownie.
4. **Zbudować** przez `twincat_build_plc` i przekazać nowe nazwy w `expectSymbols`, aby symbol pominięty przez kompilator
   został zgłoszony. Naprawić każdy błąd przed następnym krokiem.
5. **Sprawdzić** kod, do którego nie dociera żadne zadanie, przez `twincat_plc_check_all_objects` oraz uruchomić `twincat_plc_static_analysis`, gdy
   projekt go używa.
6. Tylko na polecenie wprowadzenia zmiany na działające PLC: `twincat_login` z `mode='online_change'` (weryfikuje
   zmianę na PLC); w przeciwnym razie zatrzymać się po kompilacji i zraportować, co się zmieniło.

Zraportować zmienione elementy, wynik kompilacji i wszystko, czego nie udało się zweryfikować.
