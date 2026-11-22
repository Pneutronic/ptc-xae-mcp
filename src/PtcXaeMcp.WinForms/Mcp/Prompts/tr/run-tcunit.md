Açık TwinCAT projesinin TcUnit testlerini çalıştırın ve sonucu raporlayın.

1. TcUnit kitaplığına başvurulduğunu kontrol edin: `twincat_list_library_references`.
2. Derleyin: `twincat_build_plc`. Önce her hatayı giderin.
3. Programı test runtime'ına alın: `twincat_activate_config` (veya `mode='download'` ile `twincat_login`
   ve ardından `twincat_plc_start`). Bu, bir hedef sistemi değiştirir — yalnızca size verilen test runtime'ında. `requestKey` olarak
   yeni bir UUID verin; böylece zaman aşımından sonra yapılan bir yeniden deneme bunu iki kez çalıştırmaz.
4. Sonuçları toplayın: `twincat_tcunit_results`, çalıştırıcı onları saklayana kadar bekler; bir CI sunucusu için ayrıca bir JUnit XML dosyası
   yazmak üzere `junitPath` verin.
5. Başarısız olan her test için testi ve test edilen kodu `twincat_plc_read` ile okuyun ve hangi öğenin ve
   hangi assertion'ın başarısız olduğunu belirtin.

Başarılı / başarısız / toplam sayısını ve başarısızlıkları öğeleriyle birlikte raporlayın.
