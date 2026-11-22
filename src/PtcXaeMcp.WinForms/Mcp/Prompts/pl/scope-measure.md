Zarejestrować te sygnały PLC za pomocą TwinCAT Scope i ocenić je: {{symbols}}. Czas trwania: {{seconds}} s.

1. Utworzyć projekt pomiarowy: `twincat_create_scope_project`, następnie jeden kanał na symbol przez
   `twincat_add_scope_channel` (sprawdzić je przez `twincat_list_scope_channels`).
2. Ustawić czas zapisu przez `twincat_set_scope_record_settings`; dodać wyzwalacz przez `twincat_add_scope_trigger_set`,
   gdy podano warunek startu.
3. Zapisywać: `twincat_start_scope_record`, odczekać zadany czas, `twincat_stop_scope_record`
   (`twincat_get_scope_record_state` pokazuje stan).
4. Zapisać i odczytać dane: `twincat_save_scope_data`, następnie `twincat_read_scope_file` (dla długich
   zapisów użyć `maxPoints`).
5. Ocenić: minimum, maksimum, zbocza i ich czasy oraz wszystko, co nieoczekiwane.

Zraportować wartości dla każdego sygnału i miejsce, w którym leży plik z danymi.
