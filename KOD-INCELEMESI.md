# WindowsUtils kod incelemesi

**Tarih:** 2026-09-26. **Kapsam:** `main` üzerindeki kaynak kodun tamamı (commit `f20c27c`, sonra gelen `cdb36f6` ve `dbc8b81` dahil): 3 proje. Diff değil, bütün kod okundu. Satır numaraları bu dosyanın yazıldığı sıradaki koda göredir.

**Özet:** 2 Yüksek, 2 Orta, 4 Düşük. 4 bulgu düzeltildi (1, 2, 3, 7).

| # | Önem | Durum | Konum | Sorun |
|---|---|---|---|---|
| 1 | Yüksek | Düzeltildi | `LargestFilesControl.cs` silme | Geri Dönüşüm Kutusu'na gidemeyen dosyalar uyarı verilmeden kalıcı olarak siliniyordu |
| 2 | Yüksek | Düzeltildi | `PcTools.cs` yol alan araçlar | AI araçları UNC yollarını kabul ediyordu: NTLM kimlik bilgisi sızıntısı ve dışarıya veri kanalı |
| 3 | Orta | Düzeltildi | `PcTools.cs:222-256`, `ChatSession.cs:70-91` | AI ajanı her dosyayı ve ortam değişkenini onay almadan okuyup uç noktaya gönderebiliyor |
| 4 | Orta | Açık | `ChatControl.cs:232-238, 277-286` | reasoning_effort geri dönüşünden sonraki mesajda konuşma geçmişi kayboluyor |
| 5 | Düşük | Açık | `ChatControl.cs:105-111, 307-311` | Yanıt akarken Enter ikinci bir istek başlatıyor ve Stop devre dışı kalıyor |
| 6 | Düşük | Açık | `PcTools.cs:166, 234, 275, 308` | AI araçları iptal edilemiyor; ReadTextFile dosyanın tamamını belleğe okuyor |
| 7 | Düşük | Düzeltildi | `ChatSession.cs:164` | Başka hatalar "API anahtarı reddedildi" diye gösterilebiliyordu |
| 8 | Düşük | Açık | `FileHashControl.cs:113, 150-167, 170-199` | Doğrulama, ekrandaki dosyaya değil önceki dosyanın hash'lerine göre yapılabiliyor |

---

## 1. [Yüksek, düzeltildi] "Geri Dönüşüm Kutusu'na taşınacak" denen dosyalar kalıcı olarak silinebiliyordu

**Konum (eski):** `WindowsUtils/Utilities/LargestFilesControl.cs`, `FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`

Bu çağrı Shell'e `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT` bayraklarını gönderiyor, `FOF_WANTNUKEWARNING` bayrağını göndermiyordu. Bunu .NET 10.0.11 `Microsoft.VisualBasic.Core.dll` içindeki `GetOperationFlags` ve `ShellDelete` metotlarının IL kodundan doğruladım: gönderilen değer `0x2254`. `FOF_WANTNUKEWARNING` olmadığında Shell, geri dönüşüme gidemeyen dosyayı hiçbir şey sormadan kalıcı olarak siler.

**Neden gerçek bir sorundu:** Bu ekran zaten en büyük dosyaları listeliyor. Geri Dönüşüm Kutusu'nun en büyük boyutundan büyük dosyalar (VM diskleri, yedekler, ISO'lar) oraya sığmıyor. Ağ paylaşımlarında, eşlenmiş sürücülerde ve USB belleklerde ise Geri Dönüşüm Kutusu hiç yok. Kullanıcı "Recycle Bin'e taşınacak" metnini onaylıyordu ama dosya geri alınamayacak şekilde siliniyordu.

**Düzeltme:** Yeni `WindowsUtils.Core/IO/RecycleBin.cs`, SHFileOperation'ı `FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT` ile çağırıyor. Geri dönüşüme gidemeyen dosyada Windows kalıcı silmeden önce soruyor. Kullanıcı "Hayır" derse dosya duruyor ve durum satırında "Kept N" olarak görünüyor. Onay metnine de bu durum eklendi. AGENTS.md'deki eski "always Recycle Bin" kalıbı güncellendi.

## 2. [Yüksek, düzeltildi] AI araçları UNC yollarını engellemiyordu: NTLM kimlik bilgisi sızıntısı ve dışarıya veri kanalı (SSRF benzeri)

**Konum (eski):** `WindowsUtils.AI/PcTools.cs`: `ResolveDirectory` (ListFiles, ListFolders, GetLargestFiles), ReadTextFile, ComputeFileHash, VerifyFileHash

Yolu model seçiyordu ve yol hiçbir kontrolden geçmeden `File.Exists` / `Directory.Exists` çağrılarına gidiyordu. Bir UNC yolu (`\\sunucu\paylasim\...`) verildiğinde Windows o sunucuya bağlanmaya çalışır (SMB, bazı durumlarda WebDAV) ve varsayılan ayarlarla kullanıcının NTLM kimlik doğrulama yanıtını gönderir. Bu yanıt kırılabilir ya da başka bir sunucuya aktarılabilir (relay). Sunucu adı DNS sorgusuyla dışarı çıktığı için yola gömülen veri de üçüncü bir tarafa ulaşır. Bunu, ajanın okuduğu bir dosyadaki talimatlar (dolaylı prompt injection) ya da kötü niyetli bir uç nokta kullanıcıya sorulmadan tetikleyebiliyordu.

**Düzeltme:** `PcTools.ToLocalPath` yolu `Path.GetFullPath` ile normalleştiriyor. Bu işlem yalnızca metin üzerinde çalışıyor ve ağa dokunmuyor. Ardından yalnızca `X:\` ile başlayan sürücü yollarına izin veriyor. UNC, `//`, `\\?\`, `\\.\` ve `\??\` yolları, dosya sistemine hiçbir çağrı yapılmadan reddediliyor. Bütün yol alan araçlar bu kontrolden geçiyor. `Documents`, `Desktop` gibi kısayollar da önceki gibi çalışıyor. Not: Kullanıcının kendi eşlediği ağ sürücüleri (ör. `Z:\`) hâlâ kullanılabiliyor, çünkü o sunucuyu model değil kullanıcı seçmiş.

## 3. [Orta, düzeltildi] AI ajanı her dosyayı ve ortam değişkenini onay almadan okuyup uç noktaya gönderebiliyor

**Konum:** `WindowsUtils.AI/PcTools.cs:222-256` (ReadTextFile, GetEnvironmentVariable), listeleme araçları `:108-178`; araçların kaydı `ChatSession.cs:70-91`

Hangi aracın hangi argümanla çalışacağını uzaktaki model belirliyor. Uygulama her çağrıyı otomatik olarak çalıştırıp sonucunu uç noktaya gönderiyor. İzin verilen klasörler listesi, hassas konumlar için bir engel ya da kullanıcı onayı yok. SSH anahtarları (`%USERPROFILE%\.ssh`), bulut CLI kimlik dosyaları, `.env` dosyaları ve ortam değişkenlerindeki token'lar modele ve uç noktaya gidebiliyor. Uç nokta serbestçe ayarlanabildiğinden (üçüncü taraf proxy'ler dahil) uç noktayı işleten taraf bu dosyaları fiilen okuyabilir. Güvenilir bir uç noktada bile dolaylı prompt injection aynı okumayı tetikleyebilir.

**Öneri:** Araçları kullanıcının seçtiği klasörlerle sınırlamak ve hassas yolları ile değişkenleri engellemek. Ya da dosya okuma ve ortam değişkeni araçları için kullanıcı onayı istemek.

**Düzeltme (2026-09-27):** İkinci yol seçildi. `ReadTextFile` ve `GetEnvironmentVariable` artık `ApprovalRequiredAIFunction` olarak kayıtlı. `ChatSession.StreamResponseAsync` bu araçlar çalışmadan önce duruyor ve çağıranın onay fonksiyonuna soruyor; AI Chat ekranı dosya yolunu veya değişken adını ve verinin gideceği uç noktayı gösteren bir Evet/Hayır penceresi açıyor (varsayılan Hayır). Onay fonksiyonu verilmezse bu çağrılar reddediliyor. Listeleme araçları (dosya adları, boyutlar) hâlâ onaysız çalışıyor. Ayrıntılar: `AI-ARACLARI-GUVENLIK.md`.

## 4. [Orta, açık] reasoning_effort'a otomatik geri dönüşten sonra gelen mesajda konuşma geçmişi siliniyor

**Konum:** `WindowsUtils/Utilities/ChatControl.cs:277-286` ve `:232-238`

Model reasoning_effort'u reddettiğinde açılır kutu "Default" yapılıyor (280) ve oturumda `DisableReasoningEffort()` çağrılıyor (283). Ancak `_sessionCacheKey` eski seviyeyi tutmaya devam ediyor. Bir sonraki gönderimde anahtar eşleşmediği için `ChatSession.Create` ile yeni ve boş bir oturum açılıyor. Varsayılan ayarlar gpt-4o-mini (satır 11) ve "Low" (satır 57) olduğundan, ilk kurulumda gönderilen ilk mesaj her zaman bu yoldan geçiyor. İkinci mesajda model ilk soru-cevabı hatırlamıyor.

## 5. [Düşük, açık] Yanıt akarken Enter ikinci bir istek başlatıyor; Stop o istek için kullanılamıyor

**Konum:** `WindowsUtils/Utilities/ChatControl.cs:105-111`, `:261`, `:307-311`

Send düğmesi istek sürerken kapalı, ama Enter işleyicisi açık kalıyor. İkinci `SendAsync` önceki isteği iptal edip aynı oturumda başlıyor. İptal edilen isteğin `finally` bloğu ise Send'i açıp Stop'u kapatıyor. Bu yüzden çalışan ikinci istek durdurulamıyor ve "(cancelled)" satırı yeni yanıtın ortasına yazılıyor.

## 6. [Düşük, açık] AI araçları iptal edilemiyor; ReadTextFile dosyanın tamamını belleğe okuyor

**Konum:** `WindowsUtils.AI/PcTools.cs:166`, `:275`, `:308` (token verilmeden yapılan çağrılar), `:234` (`File.ReadAllText`)

Araçların hiçbiri CancellationToken almıyor. Tüm diski tarayan bir `GetLargestFiles` çağrısı bitene kadar Stop ve New chat etkisiz kalıyor. `ReadTextFile` ise 20.000 karakter sınırını uygulamadan önce dosyanın tamamını okuyor. Birkaç GB'lık bir dosyada bu, çok yüksek bellek tüketimine ve OutOfMemory'ye yol açabiliyor.

**Öneri:** Araçlara `CancellationToken` parametresi eklemek (AIFunctionFactory bunu otomatik bağlıyor). ReadTextFile'da akıştan yalnızca ilk `maxChars` karakteri okumak.

## 7. [Düşük, düzeltildi] Başka hatalar "API anahtarı reddedildi" diye gösterilebiliyordu

**Konum:** `WindowsUtils.AI/ChatSession.cs:164` (kullanıldığı yer `ChatControl.cs:301-302`)

Eski kod, mesajın herhangi bir yerinde " 401" veya " 403" geçmesini yeterli sayıyordu. Bu yüzden içinde " 4031" gibi bir token sayısı geçen bir rate-limit hatası yetkilendirme hatası sanılıyor ve gerçek hata mesajı gizleniyordu.

**Düzeltme:** Karar artık mesaj metnine göre değil HTTP durum koduna göre veriliyor (`ClientResultException.Status`, `HttpRequestException.StatusCode`, iç içe istisnalar dahil). Denendi: içinde "4031" geçen bir rate-limit hatası için `false`, iç istisna olarak gelen 401 ve doğrudan gelen 403 için `true`, 429 için `false` dönüyor.

## 8. [Düşük, açık] Hash doğrulaması önceki dosyanın hash'lerine göre yapılabiliyor

**Konum:** `WindowsUtils/Utilities/FileHashControl.cs:113`, `:150-167`, `:170-199`

Hash kutuları yalnızca hesaplama başarılı olduğunda güncelleniyor; yeni hesaplama başladığında veya başarısız olduğunda temizlenmiyor. B dosyası hesaplanamazsa kutularda A'nın hash'leri kalıyor ve Verify, yol kutusunda B yazarken A'ya göre MATCH veya NO MATCH sonucu veriyor. Ayrıca Browse hesaplama sürerken açık olduğu için ikinci bir hesaplama başlatılabiliyor. Bu durumda iptal edilen ilk hesaplamanın `finally` bloğu, ikinci hesaplama sürerken Compute düğmesini yeniden etkinleştiriyor.

---

## Kontrol edilen ve sorun bulunmayan alanlar

- **Güvensiz deserialization:** Yok. Ayarlar System.Text.Json ile basit bir record'a okunuyor, BinaryFormatter kullanılmıyor. `.resx` dosyaları boş şablon.
- **Komut injection:** `explorer.exe /select,"<yol>"` çağrısında yol tırnak içinde ve Windows dosya adlarında `"` karakteri bulunamıyor.
- **API anahtarının saklanması:** Anahtar Credential Manager'da duruyor (kullanıcıya özel, DPAPI ile şifreli) ve P/Invoke struct düzeni doğru. Registry erişimi salt okunur.
- **`FileHasher.cs`** (IncrementalHash'e geçilmiş haliyle): Sorun bulunmadı.
