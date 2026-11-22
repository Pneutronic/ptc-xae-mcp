Açık TwinCAT projesinin PLC kodunu değiştirin: {{task}}

ptc-xae-mcp araçlarıyla küçük, doğrulanmış adımlarla çalışın:

1. Değişikliğin dokunduğu her yeri `twincat_plc_search` ile **bulun** (düz metin veya regex, tanımlayıcılar için `wholeWord`).
   `twincat_plc_list` yalnızca göz atmak için kullanılsın; düğüm etiketleri yerelleştirilmiştir, bu yüzden ağaç yolunu asla tahmin etmeyin.
2. Değiştireceğiniz her öğeyi `twincat_plc_read` ile **okuyun** (birden çoğunu `paths` ile birlikte). Her öğenin
   `Revision` değerini not alın.
3. Tam olarak **değiştirin**:
   - tam olarak bir metin parçası: `expectedRevision` ile `twincat_plc_replace_text`;
   - bildirim satırları: `expectedRevision` ile `twincat_plc_edit_entries`;
   - tüm bir bölüm: `expectedRevision` ile `twincat_plc_write`;
   - yeni öğeler: `twincat_plc_create` (`implementation` değerini de verin).
   Eski bir revizyon nedeniyle reddedilen bir yazma, öğeyi başka birinin değiştirdiği anlamına gelir: önce yeniden okuyun.
4. `twincat_build_plc` ile **derleyin** ve yeni adları `expectSymbols` içinde verin; böylece derleyicinin düşürdüğü bir sembol
   raporlanır. Sonraki adımdan önce her hatayı giderin.
5. Hiçbir görevin ulaşmadığı kodu `twincat_plc_check_all_objects` ile **denetleyin** ve proje kullanıyorsa
   `twincat_plc_static_analysis` çalıştırın.
6. Yalnızca değişikliği çalışan bir PLC'ye almanız istendiğinde: `mode='online_change'` ile `twincat_login` (değişikliği
   PLC'de doğrular); aksi halde derlemeden sonra durun ve neyin değiştiğini raporlayın.

Değişen öğeleri, derleme sonucunu ve doğrulayamadığınız her şeyi raporlayın.
