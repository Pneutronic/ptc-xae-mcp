Bu PLC sinyallerini TwinCAT Scope ile kaydedin ve değerlendirin: {{symbols}}. Süre: {{seconds}} sn.

1. Bir ölçüm projesi oluşturun: `twincat_create_scope_project`, ardından her sembol için bir kanal
   `twincat_add_scope_channel` ile (bunları `twincat_list_scope_channels` ile kontrol edin).
2. Kayıt süresini `twincat_set_scope_record_settings` ile ayarlayın; bir başlangıç koşulu
   belirtilmişse `twincat_add_scope_trigger_set` ile bir tetikleyici ekleyin.
3. Kaydedin: `twincat_start_scope_record`, süre dolana kadar bekleyin, `twincat_stop_scope_record`
   (`twincat_get_scope_record_state` durumu gösterir).
4. Veriyi kaydedin ve okuyun: `twincat_save_scope_data`, ardından `twincat_read_scope_file` (uzun
   kayıtlar için `maxPoints` kullanın).
5. Değerlendirin: minimum, maksimum, kenarlar ve zamanları ve beklenmeyen her şey.

Değerleri sinyal başına ve veri dosyasının nerede durduğunu raporlayın.
