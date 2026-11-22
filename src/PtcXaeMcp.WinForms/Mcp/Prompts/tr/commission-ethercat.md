Açık TwinCAT projesinin EtherCAT G/Ç'sini devreye alın. Master: {{master}}. Hedef sistem: {{target}}.

1. **Yapılandırılmış olanı okuyun**: `twincat_get_fieldbus_topology` ve box'lar için `twincat_read_ethercat_startup`.
   Bir projede genellikle bulunuyor diye bir box'ın var olduğunu asla varsaymayın — ağacı okuyun.
2. Hedefe ulaşılabiliyorsa **donanımla karşılaştırın**: `twincat_ethercat_read_identities` (slave başına üretici,
   ürün, revizyon ve seri numarası). Herhangi bir şeyi değiştirmeden önce her farkı listeleyin.
3. **Box ekleyin veya düzeltin**: tüm bir JSON G/Ç şeması için `twincat_apply_schema` (önce
   `twincat_validate_schema` ile doğrulayın), tek tek box'lar için `twincat_add_ethercat_box` / `twincat_remove_ethercat_box` /
   `twincat_set_previous_port`; slave ayarları için `twincat_configure_ethercat_slave`.
4. PLC değişkenlerini G/Ç'ye `twincat_link_variables` ile **bağlayın**; sonucu
   `twincat_get_mapping_info` ile kontrol edin.
5. Çözümü **derleyin** (`twincat_build_solution`) ve her hatayı giderin.
6. **Etkinleştirmeyi** yalnızca istendiğinde ve yalnızca adı verilen hedefte yapın: `twincat_set_target`, ardından `twincat_activate_config`.
   Bu, bir hedef sistemi değiştirir — erişim ilkesi o sistemde runtime eylemlerine izin vermelidir ve bir kişiden
   onay istenebilir. `requestKey` olarak yeni bir UUID verin: zaman aşımından sonra aynı anahtarla yapılan aynı çağrı asla iki kez etkinleştirmez
   (`twincat_receipt_get` ne olduğunu gösterir).

Önceki ve sonraki topolojiyi, 2. adımda bulunan her farkı ve sahada hâlâ bir kişiye ihtiyaç duyan her şeyi raporlayın.
