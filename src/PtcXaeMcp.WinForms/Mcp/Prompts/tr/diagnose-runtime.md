TwinCAT sistemi {{target}} neden beklendiği gibi davranmıyor, bulun. Önce okuyun, hiçbir şeyi değiştirmeyin.

1. **Durum**: `twincat_get_status` ve `twincat_get_plc_online_state` — XAE, proje, System Service, PLC durumu.
2. **Alarmlar**: hedef için `twincat_read_event_logger` (runtime iletileri için belirleyici kaynak).
3. **Derleme ve proje iletileri**: `minLevel='warning'` ile `twincat_get_error_list`.
4. **Çökmeler**: `twincat_coredump_list`, ardından
   en yenisi için `twincat_coredump_analyze` / `twincat_coredump_read_callstack`.
5. **Fieldbus**: `twincat_get_fieldbus_topology` ve hedef yanıt veriyorsa `twincat_ethercat_read_identities`.
6. Alarmın adını verdiği sinyallerin **canlı değerleri**: `twincat_read_ads_variables_batch`; bir adım zinciri için
   `twincat_locate_step` geçerli adımın CASE dalını gösterir.

En olası nedeni, bunun kanıtını ve bir kişinin atması gereken sonraki adımı belirtin. İstenmedikçe değişken yazmayın,
hiçbir şeyi yeniden başlatmayın veya etkinleştirmeyin.
